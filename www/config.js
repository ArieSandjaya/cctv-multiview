// Konfigurasi halaman multiview.
// Daftar kamera TIDAK diisi di sini, tetapi di go2rtc.yaml (bagian "streams").
// File ini hanya untuk nama tampilan dan preferensi. Aman dibiarkan apa adanya.

window.MULTIVIEW_CONFIG = {
  // Judul di bagian atas halaman
  title: "CCTV Multiview",

  // Alamat web NX510 untuk tombol "Playback NVR" (buka di tab baru).
  // Contoh: "http://192.168.1.50". Kosongkan "" untuk menyembunyikan tombol.
  nvrUrl: "",

  // Alamat NvrBridge untuk PTZ dan peta denah. Kosong = memakai nvrUrl. Contoh: "http://IP-SERVER:8090/"
  bridgeUrl: "",

  // Menyambungkan tile ke nomor kamera di NVR, agar tombol "Playback" membuka rekaman kamera itu.
  // Kunci = nama stream di go2rtc.yaml, nilai = nomor kamera yang terdaftar di NVR.
  // Dipakai bersama nvr-bridge (isi nvrUrl dengan alamat bridge, mis. "http://IP-SERVER:8090/").
  nvrCam: {
    // cam01: 1,
    // cam02: 2,
  },

  // Nama tampilan per kamera. Kunci = nama stream di go2rtc.yaml.
  // Kamera yang tidak ditulis di sini memakai nama stream-nya.
  names: {
    // cam01: "Lobby Utama",
    // cam02: "Parkir Basement 1",
  },

  // Urutan tampil (opsional). Kamera yang tidak disebut ditaruh setelahnya.
  order: [
    // "cam02", "cam01",
  ],

  // Stream yang disembunyikan dari halaman (opsional)
  hide: [],

  // Akhiran nama stream utama (stream 1, resolusi tinggi).
  // Contoh: kamera "cam01" (stream 2) dan "cam01_hd" (stream 1).
  // Stream utama dipakai saat tile diperbesar dan tidak tampil sebagai tile sendiri.
  hdSuffix: "_hd",

  // Urutan mode pemutaran: "mse" (stabil, jalan di mana saja) atau "webrtc" (lebih rendah latensi).
  // "mse,webrtc" = coba MSE dulu. Jika video tidak jalan di browser tertentu, coba "webrtc,mse".
  mode: "mse,webrtc",

  // Layout awal: "auto", 1, 2, 3, atau 4 (1 = 1x1, 2 = 2x2, 3 = 3x3, 4 = 4x4)
  layout: "auto",

  // Jeda pindah halaman otomatis (detik), jika jumlah kamera melebihi isi satu halaman
  rotateSeconds: 15,
};
