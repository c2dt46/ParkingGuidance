<#
  Dong goi ParkingGuidance thanh file cai dat (.exe).

  Chay tu thu muc Installer (nam trong repo, ngang hang voi thu muc cua project Web):
      powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
      powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 1.2.0
      powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -NoWheels      # khong dong goi san thu vien pip (cai online luc setup)
      powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SkipPrereqs   # dung lai prereq\ da tai

  Can tren may build: .NET SDK, Inno Setup 6.3+ (https://jrsoftware.org/isdl.php), Python 3.x (de tai goi pip), internet.
  Ket qua: dist\ParkingGuidance-Setup-<Version>.exe
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$RepoRoot = "",                                     # de trong = tu tim thu muc chua file .sln
    [string]$WebProject = "",                                   # de trong = tu tim ParkingGuidance.Web.csproj
    [string]$ExeName = "ParkingGuidance.Web.exe",
    [string]$LedBridgeExe = "ParkingGuidance.LedBridge.exe",
    [string]$LedBuildScript = "tools\build-led-bridge.cmd",
    [switch]$SkipPrereqs,
    [switch]$NoWheels
)

# $ScriptDir co the rong trong mot so moi truong -> tu tim thu muc chua script
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

# Thu muc goc repo = thu muc cha gan nhat (tinh tu Installer di len) co file .sln
if (-not $RepoRoot) {
    $d = $ScriptDir
    while ($d -and -not $RepoRoot) {
        if (Get-ChildItem $d -Filter "*.sln" -ErrorAction SilentlyContinue) { $RepoRoot = $d; break }
        $parent = Split-Path -Parent $d
        if (-not $parent) { break }
        $d = $parent
    }
    if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $ScriptDir }
}

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$stage  = Join-Path $ScriptDir "stage"
$prereq = Join-Path $ScriptDir "prereq"
$reqTxt = Join-Path $ScriptDir "requirements-train.txt"

function Step([string]$m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 0. Kiem tra cong cu
Step "Kiem tra cong cu"
Write-Host "Thu muc goc repo: $RepoRoot"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "Khong tim thay .NET SDK (lenh dotnet)." }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw "Khong tim thay Inno Setup 6 (ISCC.exe). Cai tu https://jrsoftware.org/isdl.php" }

if (-not $WebProject) {
    $hit = Get-ChildItem $RepoRoot -Recurse -Filter "ParkingGuidance.Web.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hit) { throw "Khong tim thay ParkingGuidance.Web.csproj trong $RepoRoot. Truyen -WebProject <duong dan>." }
    $WebProject = $hit.FullName
}
Write-Host "Project : $WebProject"
Write-Host "ISCC    : $iscc"

# ---------------------------------------------------------------- 1. Publish chuong trinh
Step "Publish chuong trinh (win-x64, self-contained: may khach khong can cai .NET)"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
$appOut = Join-Path $stage "app"

dotnet publish $WebProject -c Release -r win-x64 --self-contained true -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai." }
if (-not (Test-Path (Join-Path $appOut $ExeName))) {
    throw "Khong thay $ExeName trong ban publish. Kiem tra AssemblyName hoac truyen -ExeName."
}

# Bo nhung thu khong nen mang sang may khach
Remove-Item (Join-Path $appOut "appsettings.Development.json") -ErrorAction SilentlyContinue
Get-ChildItem $appOut -Recurse -Include *.pdb | Remove-Item -Force
$dataDir = Join-Path $appOut "data"
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force; Write-Host "Da bo thu muc data/ khoi ban publish (du lieu may dev khong dua sang may khach)." }

# Tao file khoi dong (them Python rieng vao PATH + mo trinh duyet)
(Get-Content (Join-Path $ScriptDir "Start-ParkingGuidance.cmd") -Raw).Replace("__EXE__", $ExeName) |
    Set-Content (Join-Path $appOut "Start-ParkingGuidance.cmd") -Encoding ASCII

# ---------------------------------------------------------------- 1b. Cong cu Train: script .py + trong so goi san
# Chuong trinh tim tools\train_vehicle.py / train_plate.py / train_plate_ocr.py tu thu muc cai dat di len,
# va chay Python voi thu muc lam viec = tools\ nen yolo11n.pt dat o do thi khong can tai tu internet.
Step "Cong cu Train (script Python + yolo11n.pt)"
$toolsOut = Join-Path $appOut "tools"
New-Item -ItemType Directory $toolsOut -Force | Out-Null
$trainScripts = @("train_vehicle.py", "train_plate.py", "train_plate_ocr.py")
foreach ($sn in $trainScripts) {
    if (Test-Path (Join-Path $toolsOut $sn)) { continue }
    $src = Get-ChildItem $RepoRoot -Recurse -Filter $sn -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -notlike "$stage*" -and $_.FullName -notlike "*\.venv\*" } | Select-Object -First 1
    if (-not $src) { Write-Warning "Khong thay $sn trong repo - tab Train tuong ung se khong chay duoc."; continue }
    # chep ca cac file .py / .yaml cung thu muc (module dung chung neu co)
    Get-ChildItem (Join-Path $src.DirectoryName "*") -File -Include *.py, *.yaml, *.yml -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName $toolsOut -Force }
}

$weights = Join-Path $toolsOut "yolo11n.pt"
if (-not (Test-Path $weights)) {
    $w = Get-ChildItem $RepoRoot -Recurse -Filter "yolo11n.pt" -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -notlike "$stage*" } | Select-Object -First 1
    if ($w) { Copy-Item $w.FullName $weights -Force }
    else {
        try {
            Write-Host "Tai yolo11n.pt ..."
            Invoke-WebRequest -Uri "https://github.com/ultralytics/assets/releases/download/v8.3.0/yolo11n.pt" -OutFile $weights -UseBasicParsing
        } catch { Write-Warning "Khong tai duoc yolo11n.pt - hay chep vao $toolsOut (neu thieu, may khach khong co mang se khong train duoc)." }
    }
}

# ---------------------------------------------------------------- 1c. Tesseract: du lieu ngon ngu (tessdata)
# Dat o {app}\tessdata (cung cho cho thu vien .NET lan tesseract.exe dung qua bien TESSDATA_PREFIX trong Start-ParkingGuidance.cmd).
Step "Du lieu Tesseract (tessdata)"
$tessOut = Join-Path $appOut "tessdata"
if (-not (Test-Path (Join-Path $tessOut "eng.traineddata"))) {
    $repoTess = Get-ChildItem $RepoRoot -Recurse -Directory -Filter "tessdata" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notlike "$stage*" -and $_.FullName -notlike "*\bin\*" -and $_.FullName -notlike "*\obj\*" } |
                Select-Object -First 1
    New-Item -ItemType Directory $tessOut -Force | Out-Null
    if ($repoTess) {
        Copy-Item (Join-Path $repoTess.FullName "*") $tessOut -Recurse -Force
        Write-Host "tessdata lay tu: $($repoTess.FullName)"
    }
    if (-not (Test-Path (Join-Path $tessOut "eng.traineddata"))) {
        try {
            Write-Host "Tai eng.traineddata (tessdata_best) ..."
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/eng.traineddata" -OutFile (Join-Path $tessOut "eng.traineddata") -UseBasicParsing
        } catch { Write-Warning "Khong tai duoc eng.traineddata - hay chep vao $tessOut." }
    }
}

# ---------------------------------------------------------------- 2. Cong cu LED
Step "Cong cu LED (led-bridge x86 + HDSDK.dll)"
$ledStage    = Join-Path $stage "led-bridge"
$bridgeInApp = Join-Path $appOut "led-bridge"
$bridgeDir   = $null

if (Test-Path (Join-Path $bridgeInApp $LedBridgeExe)) {
    $bridgeDir = $bridgeInApp                      # csproj da tu chep led-bridge vao ban publish
} else {
    $script = Join-Path $RepoRoot $LedBuildScript
    if (Test-Path $script) {
        Write-Host "Chay $LedBuildScript ..."
        & cmd /c "`"$script`""
    }
    $hit = Get-ChildItem $RepoRoot -Recurse -Filter $LedBridgeExe -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -notlike "$stage*" -and $_.FullName -notlike "*\obj\*" } |
           Select-Object -First 1
    if ($hit) { $bridgeDir = $hit.DirectoryName }
}

if ($bridgeDir) {
    New-Item -ItemType Directory $ledStage | Out-Null
    Copy-Item (Join-Path $bridgeDir "*") $ledStage -Recurse -Force
    if (-not (Test-Path (Join-Path $ledStage "HDSDK.dll"))) {
        $dll = Get-ChildItem $RepoRoot -Recurse -Filter "HDSDK.dll" -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notlike "$stage*" } | Select-Object -First 1
        if ($dll) { Copy-Item $dll.FullName $ledStage -Force } else { Write-Warning "Khong tim thay HDSDK.dll - hay chep vao $ledStage truoc khi dong goi." }
    }
    if (Test-Path $bridgeInApp) { Remove-Item $bridgeInApp -Recurse -Force }   # tranh chep trung vao {app}\led-bridge
    Write-Host "led-bridge lay tu: $bridgeDir"
} else {
    Write-Warning "Khong tim thay $LedBridgeExe - file cai dat se KHONG co cong cu LED (chuong trinh tu dung che do mo phong). Chay tools\build-led-bridge.cmd roi build lai."
}

# ---------------------------------------------------------------- 3. Bo cai tien quyet (de cai offline)
if (-not $SkipPrereqs) {
    Step "Tai bo cai tien quyet vao prereq\"
    New-Item -ItemType Directory $prereq -Force | Out-Null

    $downloads = @(
        @{ Name = "python-3.11.9-amd64.exe"; Url = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe" },
        @{ Name = "vc_redist.x64.exe";       Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe" },
        @{ Name = "vc_redist.x86.exe";       Url = "https://aka.ms/vs/17/release/vc_redist.x86.exe" },
        @{ Name = "tesseract-ocr-w64-setup-5.5.0.20241111.exe"; Optional = $true
           Url = "https://github.com/UB-Mannheim/tesseract/releases/download/v5.5.0/tesseract-ocr-w64-setup-5.5.0.20241111.exe" }
    )
    foreach ($d in $downloads) {
        $dest = Join-Path $prereq $d.Name
        if (Test-Path $dest) { Write-Host "Da co $($d.Name)"; continue }
        Write-Host "Tai $($d.Name) ..."
        try {
            Invoke-WebRequest -Uri $d.Url -OutFile $dest -UseBasicParsing
        } catch {
            if ($d.Optional) { Write-Warning "Khong tai duoc $($d.Name). Tai thu cong ($($d.Url)) va dat vao $prereq roi build lai - neu khong, file cai dat se khong co Tesseract." }
            else { throw }
        }
    }

    if (-not $NoWheels) {
        Step "Tai san thu vien Train (pip download, Windows x64, Python 3.11) - co the vai tram MB"
        $py = Get-Command python -ErrorAction SilentlyContinue
        if (-not $py) { throw "Can Python tren may build de tai goi pip (hoac chay lai voi -NoWheels)." }
        $wheels = Join-Path $prereq "wheels"
        & $py.Source -m pip download -r $reqTxt -d $wheels --only-binary=:all: --platform win_amd64 --python-version 3.11 --implementation cp
        if ($LASTEXITCODE -ne 0) { throw "pip download that bai." }
    } else {
        Remove-Item (Join-Path $prereq "wheels") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- 4. Bien dich file cai dat
Step "Dong goi bang Inno Setup"
& $iscc "/DAppVersion=$Version" "/DExeName=$ExeName" (Join-Path $ScriptDir "ParkingGuidance.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC that bai." }

Write-Host "`nXong: $(Join-Path $ScriptDir "dist\ParkingGuidance-Setup-$Version.exe")" -ForegroundColor Green
