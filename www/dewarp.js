// Dewarp kamera fisheye di browser (WebGL). Memakai model lensa equidistant (r = f * theta).
// Tiga tampilan: 'raw' (lingkaran asli), 'view' (tampilan terarah, bisa digeser dan di-zoom),
// 'pano' (panorama 360 derajat, hanya untuk kamera yang dipasang di langit-langit).
//
// Opsi (semua opsional):
//   fov    sudut pandang lensa dalam derajat (default 180)
//   mount  'ceiling' (langit-langit/meja, lensa menghadap bawah/atas) atau 'wall' (dinding)
//   cx, cy pusat lingkaran gambar, pecahan lebar/tinggi frame (default 0.5, 0.5)
//   r      jari-jari lingkaran, pecahan tinggi frame (default 0.5 = lingkaran menyentuh sisi atas dan bawah)
//   mirror true jika panorama/tampilan terarah terbalik kiri-kanan pada kamera Anda
//   mode   tampilan awal: 'raw' | 'view' | 'pano'
//   interactive  true = bisa digeser (seret) dan di-zoom (roda mouse)

const VS = `
attribute vec2 p;
varying vec2 uv;
void main() { uv = p * 0.5 + 0.5; gl_Position = vec4(p, 0.0, 1.0); }`;

const FS = `
precision highp float;
varying vec2 uv;
uniform sampler2D tex;
uniform float mode;        // 0 raw, 1 view, 2 pano
uniform float aspectOut;   // lebar/tinggi kanvas
uniform float aspectSrc;   // lebar/tinggi frame video
uniform float lensFov;     // radian
uniform vec2  center;      // pusat lingkaran, v ke atas
uniform float radius;      // pecahan tinggi frame
uniform float pan;
uniform float tilt;
uniform float vfov;        // radian, tampilan terarah
uniform float wall;        // 1 = dinding
uniform float mirror;      // 1 = balik kiri-kanan
const float PI = 3.14159265358979;

vec4 fisheye(vec3 d) {
  d = normalize(d);
  float theta = acos(clamp(d.z, -1.0, 1.0));
  float rn = theta / (lensFov * 0.5);
  if (rn > 1.0) return vec4(0.0, 0.0, 0.0, 1.0);
  float phi = atan(d.y, d.x);
  vec2 off = vec2(rn * radius * cos(phi) / aspectSrc, rn * radius * sin(phi));
  return vec4(texture2D(tex, center + off).rgb, 1.0);
}

void main() {
  if (mode < 0.5) { gl_FragColor = vec4(texture2D(tex, uv).rgb, 1.0); return; }

  float u = mirror > 0.5 ? 1.0 - uv.x : uv.x;
  if (mode < 1.5) {
    float tv = tan(vfov * 0.5);
    vec3 d = vec3((u * 2.0 - 1.0) * aspectOut * tv, (uv.y * 2.0 - 1.0) * tv, 1.0);
    float ct = cos(tilt), st = sin(tilt);
    d = vec3(d.x, d.y * ct + d.z * st, -d.y * st + d.z * ct);     // naik/turun
    float cp = cos(pan), sp = sin(pan);
    if (wall > 0.5) d = vec3(d.x * cp + d.z * sp, d.y, -d.x * sp + d.z * cp);   // geser mendatar
    else            d = vec3(d.x * cp - d.y * sp, d.x * sp + d.y * cp, d.z);    // putar mengelilingi sumbu lensa
    gl_FragColor = fisheye(d);
  } else {
    float phi = -(u - 0.5) * 2.0 * PI + pan;
    float thMax = lensFov * 0.5, thMin = 0.12;
    float theta = mix(thMin, thMax, uv.y);
    gl_FragColor = fisheye(vec3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta)));
  }
}`;

const MODES = {raw: 0, view: 1, pano: 2};
const rad = (d) => d * Math.PI / 180;
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

export class Dewarp {
  constructor(video, host, opts = {}) {
    this.video = video;
    this.host = host;
    this.o = Object.assign({fov: 180, mount: 'ceiling', cx: 0.5, cy: 0.5, r: 0.5, mirror: false, mode: 'view', interactive: false}, opts);
    this.wall = this.o.mount === 'wall';
    this.mode = this.wall && this.o.mode === 'pano' ? 'view' : this.o.mode;
    this.pan = 0;
    this.tilt = this.wall ? 0 : rad(40);
    this.vfov = rad(this.wall ? 70 : 80);
    this.dead = false;
    this.ok = false;

    const c = document.createElement('canvas');
    c.style.cssText = 'position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);touch-action:none;';
    this.canvas = c;
    const gl = c.getContext('webgl', {antialias: false, alpha: false, preserveDrawingBuffer: false});
    if (!gl) return;                                   // WebGL tidak ada: video asli tetap tampil
    this.gl = gl;
    if (!this._init()) return;
    this.ok = true;

    this.prevVisibility = video.style.visibility;
    video.style.visibility = 'hidden';                 // tetap diputar dan di-decode, hanya tidak digambar
    host.appendChild(c);

    this.ro = new ResizeObserver(() => this._fit());
    this.ro.observe(host);
    if (this.o.interactive) this._bind();
    this._loop = this._loop.bind(this);
    this.raf = requestAnimationFrame(this._loop);
  }

  _init() {
    const gl = this.gl;
    const sh = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) { console.error(gl.getShaderInfoLog(s)); return null; }
      return s;
    };
    const vs = sh(gl.VERTEX_SHADER, VS), fs = sh(gl.FRAGMENT_SHADER, FS);
    if (!vs || !fs) return false;
    const pr = gl.createProgram();
    gl.attachShader(pr, vs); gl.attachShader(pr, fs); gl.linkProgram(pr);
    if (!gl.getProgramParameter(pr, gl.LINK_STATUS)) { console.error(gl.getProgramInfoLog(pr)); return false; }
    gl.useProgram(pr);
    this.pr = pr;
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(pr, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    this.tex = tex;
    this.u = {};
    for (const n of ['mode', 'aspectOut', 'aspectSrc', 'lensFov', 'center', 'radius', 'pan', 'tilt', 'vfov', 'wall', 'mirror'])
      this.u[n] = gl.getUniformLocation(pr, n);
    return true;
  }

  setMode(m) {
    if (!(m in MODES) || (m === 'pano' && this.wall)) return;
    this.mode = m;
    this._fit();
  }

  // Ukuran kanvas: 'raw' mengikuti rasio video, 'view' memenuhi wadah, 'pano' rasio 3:1
  _fit() {
    if (this.dead || !this.ok) return;
    const W = this.host.clientWidth, H = this.host.clientHeight;
    if (!W || !H) return;
    const vw = this.video.videoWidth || 16, vh = this.video.videoHeight || 9;
    const a = this.mode === 'raw' ? vw / vh : this.mode === 'pano' ? 3 : W / H;
    let w = W, h = W / a;
    if (h > H) { h = H; w = H * a; }
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const bw = Math.max(2, Math.min(1920, Math.round(w * dpr))), bh = Math.max(2, Math.round(bw / a));
    this.canvas.style.width = w + 'px'; this.canvas.style.height = h + 'px';
    if (this.canvas.width !== bw || this.canvas.height !== bh) { this.canvas.width = bw; this.canvas.height = bh; }
    this.aspectOut = a;
  }

  _bind() {
    const c = this.canvas;
    let drag = null;
    c.style.cursor = 'grab';
    c.addEventListener('pointerdown', (e) => {
      drag = {x: e.clientX, y: e.clientY};
      try { c.setPointerCapture(e.pointerId); } catch {}
      c.style.cursor = 'grabbing';
    });
    const end = () => { drag = null; c.style.cursor = 'grab'; };
    c.addEventListener('pointerup', end);
    c.addEventListener('pointercancel', end);
    c.addEventListener('pointermove', (e) => {
      if (!drag || this.mode === 'raw') return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      drag.x = e.clientX; drag.y = e.clientY;
      const k = this.vfov / (c.clientHeight || 1);           // radian per piksel layar
      if (this.mode === 'pano') { this.pan += dx / (c.clientWidth || 1) * 2 * Math.PI; return; }
      // Seret = "menarik" gambar: gambar bergerak mengikuti jari
      this.pan -= dx * k * (this.o.mirror ? -1 : 1) * (this.wall ? 1 : -1);
      this.tilt = this.wall ? this.tilt + dy * k : this.tilt - dy * k;
      this._clampTilt();
    });
    c.addEventListener('wheel', (e) => {
      if (this.mode !== 'view') return;
      e.preventDefault();
      this.vfov = clamp(this.vfov * (e.deltaY > 0 ? 1.08 : 0.92), rad(20), rad(120));
      this._clampTilt();
    }, {passive: false});
  }

  // Batas gerak: jangan keluar dari lingkaran lensa (hindari area hitam)
  _clampTilt() {
    const edge = rad(this.o.fov / 2) - this.vfov / 2;
    if (this.wall) { const m = Math.max(0, edge * 0.9); this.tilt = clamp(this.tilt, -m, m); }
    else this.tilt = clamp(this.tilt, 0, Math.max(0, edge));
  }

  _loop() {
    if (this.dead) return;
    this.raf = requestAnimationFrame(this._loop);
    const v = this.video, gl = this.gl;
    if (v.readyState < 2 || !v.videoWidth) return;
    if (!this.aspectOut || this.srcW !== v.videoWidth || this.srcH !== v.videoHeight) {
      this.srcW = v.videoWidth; this.srcH = v.videoHeight; this._fit();
    }
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    try { gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, v); } catch { return; }
    const u = this.u, o = this.o;
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.uniform1f(u.mode, MODES[this.mode]);
    gl.uniform1f(u.aspectOut, this.aspectOut || 1);
    gl.uniform1f(u.aspectSrc, v.videoWidth / v.videoHeight);
    gl.uniform1f(u.lensFov, rad(o.fov));
    gl.uniform2f(u.center, o.cx, 1 - o.cy);
    gl.uniform1f(u.radius, o.r);
    gl.uniform1f(u.pan, this.pan);
    gl.uniform1f(u.tilt, this.tilt);
    gl.uniform1f(u.vfov, this.vfov);
    gl.uniform1f(u.wall, this.wall ? 1 : 0);
    gl.uniform1f(u.mirror, o.mirror ? 1 : 0);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  destroy() {
    if (this.dead) return;
    this.dead = true;
    cancelAnimationFrame(this.raf);
    if (this.ro) this.ro.disconnect();
    if (this.ok) this.video.style.visibility = this.prevVisibility || '';
    this.canvas.remove();
    const ext = this.gl && this.gl.getExtension('WEBGL_lose_context');
    if (ext) ext.loseContext();
  }
}
