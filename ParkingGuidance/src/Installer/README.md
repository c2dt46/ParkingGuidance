# Dong goi file cai dat ParkingGuidance

Dat thu muc `Installer` trong repo (ngang hang thu muc project Web), roi **bam dup `Build-Installer.cmd`**.
Nhap phien ban (hoac Enter de dung 1.0.0). Ket qua: `dist\ParkingGuidance-Setup-<phien ban>.exe`.

Muon them tuy chon (vi du khong dong goi san thu vien pip): chay `Build-Installer.cmd -NoWheels` trong cua so lenh,
hoac tao shortcut toi file do va them `-NoWheels` vao cuoi o Target.

## Can tren may build
- .NET SDK, Inno Setup 6.3+, Python 3.x (de tai san thu vien pip), internet.

## File cai dat lam gi
| Thanh phan | Noi dung |
|---|---|
| Chuong trinh | Ban publish win-x64 self-contained (khong can cai .NET). `appsettings.json` khong bi ghi de khi cap nhat. Kem `tools\train_*.py` va `tools\yolo11n.pt`. |
| Cong cu LED | `led-bridge\` (ParkingGuidance.LedBridge.exe + HDSDK.dll, x86) + Visual C++ x86. Bo qua thi chuong trinh tu chay che do mo phong. |
| Tesseract OCR | `tesseract.exe` (ban UB Mannheim 5.5.0, cai im) trong `tools\tesseract`, du lieu ngon ngu o `tessdata\` (lay tu repo neu co, khong thi tai eng.traineddata). `Start-ParkingGuidance.cmd` dat PATH va `TESSDATA_PREFIX` cho tien trinh chuong trinh. |
| Cong cu Train | Python 3.11 trong `tools\python`, moi truong ao `tools\.venv` (3 bo train tu tim python o day) + ultralytics, PyTorch, ONNX... + Visual C++ x64. |

Python, VC++ va goi pip duoc dong goi san (cai offline). Log cai thu vien: `<thu muc cai>\tools\pip-install.log`.

## Khoi dong
Shortcut tro toi `Start-ParkingGuidance.cmd`: chay chuong trinh o `http://localhost:5000` va mo trinh duyet.

## Go cai dat
Xoa chuong trinh va `tools\` (muc Tesseract trong Add/Remove Programs co the con sot lai); GIU thu muc `data\` va model da train.
