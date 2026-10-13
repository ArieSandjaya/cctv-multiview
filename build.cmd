@echo off
rem Membangun CctvViewer + NvrBridge dan merakit folder dist\ siap salin ke PC video wall.
rem Prasyarat: .NET SDK (dotnet --version), dan go2rtc.exe di folder ini (atau diunduh otomatis jika ada akses internet).
setlocal
cd /d "%~dp0"

dotnet publish viewer\CctvViewer.csproj -c Release -o dist || goto :fail
dotnet publish nvr-bridge\NvrBridge.csproj -c Release -o dist\NvrBridge || goto :fail

if not exist dist\www mkdir dist\www
xcopy /y /e /i www dist\www >nul
copy /y wall\config.js dist\www\config.js >nul
if not exist dist\go2rtc.yaml copy wall\go2rtc.yaml dist\go2rtc.yaml >nul
copy /y wall\nvrbridge.config dist\NvrBridge\nvrbridge.config.contoh >nul
if not exist dist\NvrBridge\nvrbridge.config copy wall\nvrbridge.config dist\NvrBridge\nvrbridge.config >nul

if exist go2rtc.exe (
  copy /y go2rtc.exe dist\go2rtc.exe >nul
) else if not exist dist\go2rtc.exe (
  echo Mengunduh go2rtc...
  powershell -NoProfile -Command "Invoke-WebRequest https://github.com/AlexxIT/go2rtc/releases/download/v1.9.14/go2rtc_win64.zip -OutFile go2rtc.zip; Expand-Archive go2rtc.zip -DestinationPath . -Force; Remove-Item go2rtc.zip" || goto :fail
  copy /y go2rtc.exe dist\go2rtc.exe >nul
)

echo.
echo Selesai. Salin seluruh folder dist\ ke PC video wall, mis. C:\CctvViewer, jalankan CctvViewer.exe,
echo lalu atur semuanya lewat ikon tray ^> Pengaturan (tidak perlu mengedit berkas).
