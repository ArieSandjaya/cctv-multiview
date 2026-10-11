# CCTV Multiview (go2rtc + halaman web) dan CctvViewer

## CctvViewer: buka satu aplikasi, semua kamera tampil di 3 monitor

`CctvViewer.exe` (Windows, .NET Framework 4.8) menjalankan go2rtc dan NvrBridge, lalu membuka satu layar penuh per monitor.
Default: 3 monitor × 16 kamera (grid 4×4) = 48 kamera. Layar 1 = `cam01`-`cam16`, layar 2 = `cam17`-`cam32`, layar 3 = `cam33`-`cam48`.

**Pasang di PC video wall**
1. Di PC pengembang: jalankan `build.cmd` (butuh .NET SDK). Hasilnya folder `dist\`. Salin ke PC video wall, misalnya `C:\CctvViewer`.
2. Pasang Google Chrome di PC video wall (Edge juga dikenali, Chrome disarankan).
3. Edit `go2rtc.yaml` (daftar kamera bernama `cam01` ... `cam48`, sub stream H.264 640x360, 10-15 fps).
4. Opsional: `NvrBridge\nvrbridge.config` (NX510, PTZ, peta) dan `www\config.js` (nama kamera).
5. Windows: Settings > Display > **Extend these displays**, skala **100%** di ketiga monitor, hubungkan ketiga HDMI.
6. Jalankan `CctvViewer.exe --check` untuk memeriksa monitor, browser, dan berkas, lalu buka `CctvViewer.exe`.
7. Agar otomatis saat Windows login: `CctvViewer.exe --autostart on` (login Windows otomatis diatur terpisah).

**Memakai**: gerakkan mouse ke atas layar untuk memunculkan header (Playback, Peta, Muat ulang, Keluar). Klik dua kali tile untuk memperbesar (stream utama + PTZ), `Esc` menutup. `Keluar` (atau `CctvViewer.exe --stop`) menutup semuanya.

**Ketahanan**: go2rtc, NvrBridge, dan tiap layar dijaga; jika berhenti atau jendela ditutup, dibuka lagi otomatis (jeda bertahap). Jika monitor terlambat terdeteksi atau dicabut-dipasang, jendela dikembalikan ke monitornya. Halaman memuat ulang diri tiap 6 jam (`ReloadMinutes`). Log ada di folder `data\` (`cctvviewer.log`, `go2rtc.log`, `nvrbridge.log`; maks 5 MB + 1 cadangan).

**Pengaturan** di `cctvviewer.config`: `Screens`, `CamsPerScreen`, `Monitors` (manual jika urutan monitor salah), `Browser`, `ReopenClosedScreens`, `ReloadMinutes`, dll., semuanya dijelaskan di berkasnya.

**Kapasitas**: 48 sub stream berarti 48 decoder video di tiga proses browser. Gunakan decoder perangkat keras (GPU Intel/NVIDIA/AMD yang cukup baru) dan sub stream H.264 640x360 10-15 fps. Jika tersendat, turunkan resolusi/fps sub stream di kamera dulu. Pantau CPU/GPU di Task Manager saat uji pertama.

**Keamanan**: pada konfigurasi wall, go2rtc hanya mendengarkan 127.0.0.1 dan RTSP ulang dimatikan. `go2rtc.yaml` dan `nvrbridge.config` berisi password: jangan di-commit dengan password asli.

**Status pengujian**: logika peluncur (start/restart/keluar, 3 layar × 16 kamera dari 48 stream, header, playback, deteksi NvrBridge) diuji di Linux dengan stub Win32. Belum diuji di Windows asli: penempatan di 3 monitor, job object, decoding 48 stream H.264, dan build net48. Lakukan uji pertama langsung di PC video wall.

---


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
