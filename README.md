# CCTV Multiview (go2rtc + halaman web)

Multiview di browser untuk kamera i-PRO/Panasonic seri E. Kamera ditarik langsung lewat RTSP (stream 2 / sub stream), jadi tidak melewati ASM300 maupun NX510.

## Isi folder

| File | Fungsi |
|---|---|
| `go2rtc.yaml` | **Daftar kamera.** Satu-satunya file yang diedit untuk menambah kamera. |
| `www/config.js` | Nama tampilan kamera, alamat NX510 untuk tombol Playback, layout awal. Opsional. |
| `www/index.html` | Halaman multiview (tidak perlu diedit). |
| `docker-compose.yml` | Menjalankan go2rtc di Docker. |
| `nvr-bridge/` | Aplikasi console .NET Framework 4.8 untuk playback rekaman NX510 (opsional, lihat `nvr-bridge/README.md`). |

## Menjalankan

**Dengan Docker** (Linux, atau Windows/Mac dengan Docker Desktop):

```
docker compose up -d
```

**Tanpa Docker (Windows):** unduh `go2rtc_win64.zip` dari https://github.com/AlexxIT/go2rtc/releases, ekstrak `go2rtc.exe` ke folder ini, ubah `static_dir: /www` di `go2rtc.yaml` menjadi `static_dir: www`, lalu jalankan `go2rtc.exe`.

Buka `http://IP-SERVER:1984/` di browser. Selama belum ada kamera, halaman menampilkan "Belum ada kamera".

## Menambah kamera

Edit `go2rtc.yaml`, bagian `streams:`, lalu restart (`docker compose restart`).

```yaml
streams:
  cam01:    rtsp://USER:PASS@192.168.1.101:554/cam/realmonitor?channel=1&subtype=1   # tampil di multiview
  cam01_hd: rtsp://USER:PASS@192.168.1.101:554/cam/realmonitor?channel=1&subtype=0   # dipakai saat tile diperbesar (opsional)
```

- Setiap stream **tanpa** akhiran `_hd` menjadi satu tile. Jumlah kamera tidak dibatasi; lebih dari satu halaman akan dibagi per halaman.
- URL RTSP di atas adalah pola Dahua (kamera seri E adalah OEM Dahua; subtype=0 main, subtype=1 sub). Bisa berbeda per model/firmware. Pastikan lewat menu kamera atau tes dengan VLC (Media > Open Network Stream).
- Password dengan karakter khusus (`@ : / # ?`) harus di-URL-encode (`@` menjadi `%40`).
- Nama tampilan: isi `names` di `www/config.js`, misalnya `cam01: "Lobby Utama"`.

## Memakai halaman

- **Layout:** Auto, 1×1, 2×2, 3×3, 4×4. Tombol `‹ ›` atau panah kiri/kanan untuk pindah halaman. `Otomatis` memutar halaman tiap beberapa detik.
- **Perbesar:** klik dua kali pada tile (atau ikon panah di pojok tile). Gambar memakai stream utama (`_hd`) jika ada. `Esc` untuk menutup. Saat satu kamera dibuka, tile lain berhenti ditarik agar PC tidak berat.
- **Layar penuh:** tombol atau tombol `F`. Di layar penuh, header menghilang saat mouse diam (cocok untuk monitor dinding).
- **Playback NVR:** isi `nvrUrl` di `www/config.js`. Tanpa NvrBridge, tombol hanya membuka web NX510 di tab baru. Dengan NvrBridge (folder `nvr-bridge/`), isi `nvrUrl` dengan alamat NvrBridge dan `nvrCam` dengan pasangan nama stream dan nomor kamera di NVR; tiap tile lalu punya tombol **Playback** yang membuka rekaman kamera itu di halaman playback.
- **PTZ:** buka satu kamera PTZ (klik dua kali). Panel di pojok kanan bawah: 8 arah, zoom, fokus, kecepatan, dan preset. Mendukung kamera Dahua (seri E), i-PRO/Panasonic lewat ONVIF, dan merek lain yang mendukung ONVIF. Butuh NvrBridge, lihat `nvr-bridge/README.md`.
- **Fisheye:** kamera fisheye yang mengirim gambar lingkaran mentah didaftarkan di `fisheye` pada `www/config.js` (`mount: "ceiling"` atau `"wall"`, `fov`). Tile menampilkan hasil dewarp; saat diperbesar ada tombol **Asli / Terarah / Panorama**. Di mode Terarah, seret untuk menggeser dan putar roda mouse untuk zoom. Dewarp berjalan di browser (WebGL), maksimal 6 kamera fisheye per halaman. Fisheye yang sudah di-dewarp oleh kameranya tidak perlu didaftarkan.
- **Peta denah:** tombol **Peta** di header. Unggah gambar denah, letakkan ikon kamera, klik ikon untuk membuka kamera. Butuh NvrBridge.
- Titik di tiap tile: hijau = gambar bergerak, kuning = menghubungkan, merah = tidak ada sinyal.
- Pintasan: `A` auto, `1`–`4` layout, `Spasi` putar otomatis, `F` layar penuh.

## Catatan

- **Kodek:** untuk stream 2 gunakan H.264. H.265 hanya diputar di browser tertentu (Safari, atau Chrome/Edge dengan decoder perangkat keras).
- **Mode pemutaran:** default `mse,webrtc` (jalan di mana saja, termasuk Docker). Jika ingin latensi lebih rendah, ubah `mode` di `www/config.js` menjadi `webrtc,mse`. Di Docker, WebRTC butuh `candidates` di `go2rtc.yaml` (sudah ada contoh yang di-comment).
- **Keamanan:** `go2rtc.yaml` berisi password kamera. Jalankan hanya di jaringan internal, jangan diekspos ke internet, dan aktifkan `username`/`password` pada bagian `api:` jika server bisa diakses dari banyak jaringan.
- Kamera akan melayani satu koneksi tambahan untuk stream ke go2rtc (satu untuk semua penonton, berapa pun jumlah browser yang membuka halaman).
