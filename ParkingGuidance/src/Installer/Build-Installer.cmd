@echo off
rem Bam dup file nay de tao file cai dat (khong can go lenh). Them tuy chon neu can: Build-Installer.cmd -NoWheels
setlocal
cd /d "%~dp0"

set "VER=1.0.0"
set /p VER=Phien ban (Enter = 1.0.0): 

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" -Version "%VER%" %*
if errorlevel 1 (
    echo.
    echo THAT BAI - xem loi o tren.
    pause
    exit /b 1
)

echo.
echo Xong. File cai dat nam trong thu muc dist.
start "" "%~dp0dist"
pause
