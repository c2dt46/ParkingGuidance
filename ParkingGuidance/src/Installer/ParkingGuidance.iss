; =====================================================================
;  ParkingGuidance - file cai dat (Inno Setup 6.3 tro len)
;  Khong chay truc tiep: dung build-installer.ps1 (no publish, chuan bi file roi goi ISCC)
; =====================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef ExeName
  #define ExeName "ParkingGuidance.Web.exe"
#endif
#define AppName "ParkingGuidance"
#define StageDir "stage"
#define PrereqDir "prereq"
#define PythonInstaller "python-3.11.9-amd64.exe"
#define TesseractInstaller "tesseract-ocr-w64-setup-5.5.0.20241111.exe"

[Setup]
AppId={{9F4B2C1E-6A3D-4E85-B7F2-5D8C1A0E3B96}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Megatech
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#ExeName}

; Muon giao dien cai dat tieng Viet: dat file Vietnamese.isl (ban dich khong chinh thuc cua Inno Setup) canh file nay.
#ifexist "Vietnamese.isl"
[Languages]
Name: "vi"; MessagesFile: "Vietnamese.isl"
#endif

[Types]
Name: "full"; Description: "{cm:TypeFull}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "app"; Description: "{cm:CompApp}"; Types: full custom; Flags: fixed
Name: "led"; Description: "{cm:CompLed}"; Types: full
Name: "train"; Description: "{cm:CompTrain}"; Types: full
Name: "tesseract"; Description: "{cm:CompTesseract}"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Chuong trinh ghi du lieu (data/, model train, anh train...) ngay trong thu muc cai dat -> cho phep nguoi dung thuong ghi
Name: "{app}"; Permissions: users-modify
Name: "{app}\data"; Permissions: users-modify

[Files]
; --- Chuong trinh chinh (appsettings.json chi chep khi chua co, de khong ghi de cau hinh cua khach) ---
Source: "{#StageDir}\app\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
Source: "{#StageDir}\app\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist; Components: app

; --- Cong cu LED: led-bridge (x86) + HDSDK.dll. Chuong trinh tim o {app}\led-bridge\ (xem Led:BridgePath) ---
Source: "{#StageDir}\led-bridge\*"; DestDir: "{app}\led-bridge"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Components: led

; --- Cong cu Train ---
Source: "requirements-train.txt"; DestDir: "{app}\tools"; Flags: ignoreversion; Components: train

; --- Bo cai tien quyet: giai nen tam thoi, xoa sau khi cai ---
Source: "{#PrereqDir}\vc_redist.x86.exe"; DestDir: "{tmp}\prereq"; Flags: deleteafterinstall skipifsourcedoesntexist; Components: led
Source: "{#PrereqDir}\vc_redist.x64.exe"; DestDir: "{tmp}\prereq"; Flags: deleteafterinstall skipifsourcedoesntexist; Components: train tesseract
Source: "{#PrereqDir}\{#PythonInstaller}"; DestDir: "{tmp}\prereq"; Flags: deleteafterinstall skipifsourcedoesntexist; Components: train
Source: "{#PrereqDir}\{#TesseractInstaller}"; DestDir: "{tmp}\prereq"; Flags: deleteafterinstall skipifsourcedoesntexist; Components: tesseract
Source: "{#PrereqDir}\wheels\*"; DestDir: "{tmp}\prereq\wheels"; Flags: deleteafterinstall recursesubdirs createallsubdirs skipifsourcedoesntexist; Components: train

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Start-ParkingGuidance.cmd"; IconFilename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Flags: runminimized
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Start-ParkingGuidance.cmd"; IconFilename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Flags: runminimized; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\Start-ParkingGuidance.cmd"; IconFilename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Flags: runminimized; Tasks: autostart

[Run]
; 1) Visual C++ Runtime: x86 cho HDSDK.dll / led-bridge, x64 cho PyTorch / ONNX Runtime
Filename: "{tmp}\prereq\vc_redist.x86.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:StatusVc86}"; Flags: waituntilterminated; Components: led; Check: HasPrereq('vc_redist.x86.exe')
Filename: "{tmp}\prereq\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:StatusVc64}"; Flags: waituntilterminated; Components: train tesseract; Check: HasPrereq('vc_redist.x64.exe')

; 2) Python goc dat trong {app}\tools\python (khong dung PATH he thong, khong dung Python khac tren may)
Filename: "{tmp}\prereq\{#PythonInstaller}"; Parameters: "/quiet InstallAllUsers=0 TargetDir=""{app}\tools\python"" PrependPath=0 Include_launcher=0 Include_test=0 Include_doc=0 Include_tcltk=0 Shortcuts=0 AssociateFiles=0"; StatusMsg: "{cm:StatusPython}"; Flags: waituntilterminated; Components: train; Check: NeedPython

; 2b) Tesseract OCR (ban Windows 64-bit cua UB Mannheim) cai im vao {app}\tools\tesseract. Du lieu ngon ngu nam o {app}\tessdata
Filename: "{tmp}\prereq\{#TesseractInstaller}"; Parameters: "/S /D={app}\tools\tesseract"; StatusMsg: "{cm:StatusTesseract}"; Flags: waituntilterminated; Components: tesseract; Check: NeedTesseract

; 3) Tao moi truong ao {app}\tools\.venv - chuong trinh Train tu tim python.exe o day (khong can PATH hay cau hinh)
Filename: "{app}\tools\python\python.exe"; Parameters: "-m venv ""{app}\tools\.venv"""; StatusMsg: "{cm:StatusVenv}"; Flags: runhidden waituntilterminated; Components: train; Check: NeedVenv

; 4) Thu vien Train (YOLO11 / ultralytics, PyTorch, ONNX...). Co san goi pip trong bo cai -> cai offline; khong co -> cai tu internet
Filename: "{app}\tools\.venv\Scripts\python.exe"; Parameters: "-m pip install --no-index --find-links ""{tmp}\prereq\wheels"" -r ""{app}\tools\requirements-train.txt"" --log ""{app}\tools\pip-install.log"""; StatusMsg: "{cm:StatusPip}"; Flags: runhidden waituntilterminated; Components: train; Check: InstallFromWheels
Filename: "{app}\tools\.venv\Scripts\python.exe"; Parameters: "-m pip install -r ""{app}\tools\requirements-train.txt"" --log ""{app}\tools\pip-install.log"""; StatusMsg: "{cm:StatusPip}"; Flags: runhidden waituntilterminated; Components: train; Check: InstallOnline

; 5) Mo chuong trinh sau khi cai
Filename: "{app}\Start-ParkingGuidance.cmd"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser runminimized

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#ExeName}"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
; Python + thu vien sinh ra luc cai. Thu muc data/ va model da train KHONG bi xoa khi go cai dat.
Type: filesandordirs; Name: "{app}\tools"

[CustomMessages]
default.TypeFull=Cài đặt đầy đủ (chương trình + công cụ LED + công cụ Train)
default.TypeCustom=Tùy chọn
default.CompApp=Chương trình ParkingGuidance
default.CompLed=Công cụ LED: led-bridge (x86), HDSDK.dll và Visual C++ x86
default.CompTrain=Công cụ Train: Python 3.11, YOLO11 (ultralytics), PyTorch và Visual C++ x64
default.TaskAutostart=Tự khởi động ParkingGuidance cùng Windows
default.StatusVc86=Đang cài Visual C++ Runtime (x86) cho bảng LED...
default.StatusVc64=Đang cài Visual C++ Runtime (x64)...
default.CompTesseract=Tesseract OCR (nhận diện ký tự): tesseract.exe + dữ liệu ngôn ngữ tessdata
default.StatusTesseract=Đang cài Tesseract OCR...
default.StatusPython=Đang cài Python cho công cụ Train...
default.StatusVenv=Đang tạo môi trường Python cho công cụ Train...
default.StatusPip=Đang cài thư viện Train (YOLO11, PyTorch...). Bước này có thể mất vài phút...
default.LaunchApp=Mở ParkingGuidance
default.CloseApp=ParkingGuidance đang chạy. Hãy đóng cửa sổ chương trình rồi bấm Install lại.

[Code]
function HasPrereq(const Name: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\prereq\') + Name);
end;

function NeedPython: Boolean;
begin
  Result := HasPrereq('{#PythonInstaller}')
    and not FileExists(ExpandConstant('{app}\tools\python\python.exe'));
end;

function NeedTesseract: Boolean;
begin
  Result := HasPrereq('{#TesseractInstaller}')
    and not FileExists(ExpandConstant('{app}\tools\tesseract\tesseract.exe'));
end;

function HasWheels: Boolean;
begin
  Result := DirExists(ExpandConstant('{tmp}\prereq\wheels'));
end;

function VenvPythonExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\tools\.venv\Scripts\python.exe'));
end;

function NeedVenv: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\tools\python\python.exe')) and (not VenvPythonExists);
end;

function InstallFromWheels: Boolean;
begin
  Result := HasWheels and VenvPythonExists;
end;

function InstallOnline: Boolean;
begin
  Result := (not HasWheels) and VenvPythonExists;
end;

// Khong cai de len khi chuong trinh dang chay (file dang bi khoa)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq {#ExeName}" | find /I "{#ExeName}" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Result := CustomMessage('CloseApp');
end;
