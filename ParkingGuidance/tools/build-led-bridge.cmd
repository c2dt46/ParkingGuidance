@echo off
rem Build led-bridge (x86, self-contained) va dat canh app web de BridgeLedTransport tim thay.
rem Chay tu thu muc goc cua du an:  tools\build-led-bridge.cmd
setlocal
set ROOT=%~dp0..
dotnet publish "%ROOT%\src\ParkingGuidance.LedBridge\ParkingGuidance.LedBridge.csproj" -c Release -r win-x86 --self-contained true -o "%ROOT%\src\ParkingGuidance.Web\led-bridge"
if errorlevel 1 (echo Build led-bridge THAT BAI & exit /b 1)
echo Xong: src\ParkingGuidance.Web\led-bridge\ParkingGuidance.LedBridge.exe
