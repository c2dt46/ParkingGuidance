@echo off
rem ParkingGuidance launcher: chay chuong trinh o http://localhost:5000 va mo trinh duyet.
rem Doi cong: dat bien moi truong ASPNETCORE_URLS. Dat PGS_OPEN_BROWSER=0 de khong tu mo trinh duyet.
rem Cong cu Train tu tim Python o tools\.venv. Tesseract: tesseract.exe o tools\tesseract (da them vao PATH cho tien trinh nay),
rem du lieu ngon ngu o tessdata\ (TESSDATA_PREFIX).
setlocal
set "APPDIR=%~dp0"
if not defined ASPNETCORE_URLS set "ASPNETCORE_URLS=http://localhost:5000"
set "PATH=%APPDIR%tools\tesseract;%PATH%"
set "TESSDATA_PREFIX=%APPDIR%tessdata"

cd /d "%APPDIR%"
start "ParkingGuidance" /min "%APPDIR%__EXE__"

if /i "%PGS_OPEN_BROWSER%"=="0" goto :eof
timeout /t 5 /nobreak >nul
start "" "%ASPNETCORE_URLS%"
