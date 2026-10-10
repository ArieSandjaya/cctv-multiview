# NvrBridge: playback rekaman NX510 di browser

Aplikasi console .NET Framework 4.8 yang menjembatani browser dengan NVR i-PRO WJ-NX510 lewat antarmuka CGI resmi (dokumen *i-PRO NVR CGI Command Interface* v1.3R1, yang mencantumkan NX510 series). Browser tidak bisa memakai CGI itu langsung karena butuh login Digest, sesi (UID) dengan keep-alive, dan format unduhan khusus. NvrBridge mengurus semuanya.

**Cara kerja:** pengguna memilih kamera, tanggal, jam, dan durasi di halaman web. NvrBridge login ke NVR (Digest), meminta klip MP4 lewat `httpdl.cgi`, menyimpannya sementara, lalu halaman memutarnya di browser. Pemutaran dimulai setelah klip selesai diunduh, jadi bukan streaming instan.

> **Status:** logika program sudah diuji terhadap NVR tiruan yang dibuat mengikuti dokumen (login Digest, multipart, file terbagi, status "tidak ada rekaman", Range, validasi input). **Belum pernah diuji terhadap NX510 asli.** Ikuti "Uji pertama" di bawah sebelum dipakai.

## Kebutuhan

- Windows 10/11 atau Windows Server dengan **.NET Framework 4.8** (sudah bawaan Windows 10 versi 1903 ke atas dan Windows 11).
- Untuk build: Visual Studio 2019/2022, atau .NET SDK (`dotnet build`).
- PC ini bisa menjangkau NX510 lewat jaringan.
- Sebaiknya buat **user khusus di NX510** dengan hak playback saja, jangan pakai admin.

## Build

Visual Studio: buka `NvrBridge.csproj`, pilih **Release**, lalu Build. Hasilnya ada di `bin\Release\net48\` (`NvrBridge.exe`, `nvrbridge.config`, folder `web\`).

Atau lewat command line di folder ini:

```
dotnet build -c Release
```

## Konfigurasi

Edit `nvrbridge.config` (di folder yang sama dengan `NvrBridge.exe`). Yang wajib:

| Kunci | Isi |
|---|---|
| `NvrBaseUrl` | Alamat NX510, mis. `https://192.168.1.50`. NX510 memakai HTTPS secara default. |
| `NvrUser`, `NvrPassword` | User NVR (hak playback saja). |
| `Cameras` | Nomor kamera di NVR dan nama tampil, mis. `1=Lobby Utama;2=Parkir B1`. |

Opsi penting lainnya: `NvrTimeMode` (`Utc` atau `Local`, lihat "Uji pertama"), `ClipKind` (`MP4VO` tanpa audio, default), `MaxClipMinutes`, `ListenPrefix`, `BridgeUser`/`BridgePassword`. Semua dijelaskan di komentar file itu.

`nvrbridge.config` berisi password NVR. Batasi hak akses file-nya (NTFS) hanya untuk akun yang menjalankan NvrBridge.

## Menjalankan

```
NvrBridge.exe --selftest     (cek login dan rentang rekaman, lalu keluar)
NvrBridge.exe                (jalankan server)
```

Buka `http://localhost:8090/`. Hentikan dengan Ctrl+C.

### Agar bisa dibuka dari PC lain

1. Ubah `ListenPrefix=http://+:8090/` di `nvrbridge.config`.
2. Sekali saja, di Command Prompt **Administrator**:
   ```
   netsh http add urlacl url=http://+:8090/ user=Everyone
   netsh advfirewall firewall add rule name="NvrBridge" dir=in action=allow protocol=TCP localport=8090
   ```
3. Isi `BridgeUser` dan `BridgePassword` agar halaman meminta login. Login ini memakai HTTP Basic tanpa enkripsi, jadi gunakan hanya di jaringan internal dan jangan diekspos ke internet.

### Agar jalan otomatis saat PC menyala

Daftarkan `NvrBridge.exe` di Task Scheduler (trigger *At startup*, jalankan dengan akun khusus), atau jadikan service dengan NSSM.

## Menghubungkan ke halaman multiview

Di `www\config.js` pada paket multiview, isi:

```js
nvrUrl: "http://IP-SERVER:8090/",
nvrCam: { cam01: 1, cam02: 2 },   // nama stream go2rtc -> nomor kamera di NVR
```

Tiap tile lalu punya tombol **Playback** yang membuka halaman ini dengan kamera yang sudah terpilih.

## PTZ kamera Dahua dan peta denah (untuk halaman multiview)

NvrBridge juga menjadi jembatan untuk dua fitur di halaman multiview. Browser tidak bisa memanggil kamera Dahua langsung karena butuh login Digest, jadi permintaan PTZ lewat NvrBridge.

**Pengaturan di `nvrbridge.config`:**

```
AllowOrigin=http://IP-SERVER:1984
PtzCameras=cam01=192.168.1.101;cam05=192.168.1.105
PtzUser=ptz
PtzPassword=...
```

- `AllowOrigin` adalah alamat halaman multiview (go2rtc). Tanpa ini browser memblokir panggilan dari port 1984 ke 8090.
- `PtzCameras`: nama stream di go2rtc = alamat kamera. Hanya kamera yang dicantumkan yang punya panel PTZ dan hanya itu yang bisa dituju.
- Di `www/config.js` halaman multiview, `nvrUrl` (atau `bridgeUrl`) harus berisi alamat NvrBridge, misalnya `http://IP-SERVER:8090/`.
- **Penting:** jika `BridgeUser` diisi, browser tidak meminta login untuk panggilan lintas-origin ini, jadi PTZ dan peta tidak akan jalan dari halaman multiview. Pakai jaringan internal tepercaya, atau kosongkan `BridgeUser`. Panggilan yang mengubah sesuatu (PTZ, simpan peta) tetap butuh header `X-NvrBridge` dan hanya diterima dari `AllowOrigin`.

**PTZ:** buka satu kamera (klik dua kali tile). Jika kamera itu ada di `PtzCameras`, panel PTZ muncul: 8 arah, zoom, fokus, kecepatan 1-8, dan preset 1-255. Gerakan berjalan selama tombol ditahan. Jika tombol stop tidak sampai (jaringan putus, tab ditutup), kamera berhenti sendiri setelah `PtzMaxMoveSeconds` (default 8 detik). Perintah yang dikirim adalah `/cgi-bin/ptz.cgi` milik Dahua (`action=start|stop`, kode `Up`, `LeftUp`, `ZoomTele`, `GotoPreset`, dan seterusnya).

**Peta denah:** tombol **Peta** di header. Tekan **Ubah**, lalu **+ Peta baru** untuk mengunggah gambar denah (PNG/JPG, maks 5 MB). Pilih kamera dari daftar, klik di gambar untuk meletakkan ikonnya, seret untuk menggeser, lalu **Simpan**. Di mode lihat, klik ikon untuk membuka kamera itu. Data disimpan di folder `data\` (`maps.json` dan `maps\`). Menghapus peta tidak menghapus file gambarnya; hapus manual jika perlu.

**Belum diuji di kamera asli.** Logika PTZ diuji terhadap kamera tiruan yang meniru login Digest dan `ptz.cgi`. Kode perintah dan argumen mengikuti pola Dahua yang umum, tetapi firmware OEM bisa berbeda. Uji dulu dengan satu kamera: arah gerak, zoom, dan preset. Jika kamera tidak bergerak, jalankan dengan `Verbose=true` dan kirim log konsolnya. Jika arah terbalik atau kamera menjawab HTTP 404/400, nomor `PtzChannel` (default 0) atau kode perintahnya yang perlu disesuaikan.

## Uji pertama (wajib)

1. Jalankan `NvrBridge.exe --selftest`. Harus muncul "Login: OK".
2. Jalankan `NvrBridge.exe`, buka halaman, pilih kamera, lalu ambil klip **1 menit** dari waktu yang kejadiannya Anda tahu (misalnya saat Anda lewat di depan kamera).
3. Cocokkan jam yang tampil di gambar (OSD kamera) dengan jam yang Anda minta. Dokumen CGI hanya menyebut UTC untuk perintah playback lain, dan tidak menyebut zona waktu untuk unduhan. Karena itu defaultnya `NvrTimeMode=Utc`. Jika klip bergeser sekian jam, ubah menjadi `Local` (atau sebaliknya) lalu restart.
4. Jika ada yang gagal, set `Verbose=true`, jalankan ulang, ulangi kejadian itu, dan kirim log konsol (UID sudah disamarkan, password tidak pernah dicatat).

## Masalah umum

| Pesan | Penyebab dan solusi |
|---|---|
| Login ke NVR ditolak (HTTP 401) | User atau password salah, atau NVR memakai Basic (ubah `NvrAuthScheme=Basic`). |
| Tidak bisa terhubung ke NVR | Alamat atau port salah, NVR tidak terjangkau, atau HTTP/HTTPS tidak sesuai `NvrBaseUrl`. |
| Kesalahan sertifikat | Pastikan `NvrAllowSelfSigned=true` (hanya berlaku untuk host NVR). |
| Tidak ada rekaman pada rentang waktu itu | Tidak ada rekaman pada jam itu, atau zona waktu salah (lihat uji pertama). |
| File dari NVR bukan MP4 yang dikenal | Rekaman JPEG atau format lain. Kirim log `Verbose=true`. |
| Browser tidak bisa memutar klip | Kodek H.265. Gunakan tombol Unduh MP4 dan putar di VLC, atau ubah kamera ke H.264. |
| Access denied saat start | Alamat `http://+:PORT/` butuh perintah `netsh http add urlacl` (lihat di atas). |
| NVR sibuk | Kurangi `MaxConcurrentDownloads`, tunggu, lalu coba lagi. |

## Batasan

- Satu permintaan unduh paling lama 1 jam (batas NVR); default aplikasi 10 menit (`MaxClipMinutes`). Detik awal dibulatkan ke menit.
- NVR membatasi 16 sesi dan meminta jeda antar perintah. Aplikasi memakai satu sesi bersama, melepasnya saat tidak dipakai 5 menit, dan membatasi unduhan bersamaan (default 2).
- Klip disimpan di folder `clips` dan dihapus otomatis setelah `ClipRetentionMinutes` (default 60 menit).
- Audio (`ClipKind=MP4`) sering memakai G.726 yang tidak bisa diputar browser. Default tanpa audio.
- Mengikuti dokumen v1.3R1. Perilaku firmware NX510 Anda bisa berbeda dari dokumen.
