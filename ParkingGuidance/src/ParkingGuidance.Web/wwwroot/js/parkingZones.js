// ---------------------------------------------------------------------------
// Trang thai
// ---------------------------------------------------------------------------
let zoneCamera = null;      // camera dang chon (day du du lieu tu server)
let zones = [];             // ban lam viec cac vung (chua luu cho den khi bam "Luu")
let drawing = false;
let currentImageFile = null;
let lastTestResult = null;  // ket qua nhan dien gan nhat (ve lai khi anh moi khong thay doi)
let usedElsewhere = new Map();   // so thu tu vi tri (da chuan hoa) -> noi dang dung: camera khac CUNG TANG va CUNG ZONE
let allFloorSlots = [];     // danh sach o do cua tang chua camera nay (goi y khi gan SlotId)

const zoneStage = document.getElementById('zoneStage');
const zonePreviewImg = document.getElementById('zonePreviewImg');
const zoneOverlay = document.getElementById('zoneOverlay');
const zoneCtx = zoneOverlay.getContext('2d');
const zoneStagePlaceholder = document.getElementById('zoneStagePlaceholder');

const ZONE_COLORS = ['#22c55e', '#3b82f6', '#f59e0b', '#a855f7', '#ec4899', '#14b8a6', '#ef4444'];
const zoneColor = (i) => ZONE_COLORS[i % ZONE_COLORS.length];

// ---------------------------------------------------------------------------
// So thu tu vi tri nhan dien: SO TU NHIEN NGUYEN DUONG (1, 2, 3...) va KHONG DUOC TRUNG trong cung 1 Zone cua tang
// ("B1", "A-2", "1.5", "0", "-3" la khong hop le; Zone khac / tang khac duoc dung lai cung so)
// ---------------------------------------------------------------------------
const SLOT_NUMBER_RULE = 'Số thứ tự vị trí phải là số tự nhiên nguyên dương: 1, 2, 3... (không dùng chữ hay ký hiệu, ví dụ "B1" là không hợp lệ).';

// Mo ta noi lap dat cua camera dang chon, vd "tầng B1, Zone 2"
function placeLabel() {
    return `tầng ${zoneCamera?.floor || 'B1'}, Zone ${zoneCamera?.zone || '1'}`;
}

// Tra ve so o dang chuan (bo so 0 dung dau) hoac null neu khong hop le.
function parseSlotNumber(raw) {
    const s = (raw ?? '').toString().trim();
    if (!/^[0-9]+$/.test(s)) return null;
    const t = s.replace(/^0+/, '');
    return (t.length >= 1 && t.length <= 6) ? t : null;
}

// Tra ve thong bao loi neu `raw` khong dung duoc cho vung o vi tri ignoreIndex (null = vung moi), nguoc lai null.
function checkSlotId(raw, ignoreIndex) {
    const n = parseSlotNumber(raw);
    if (n === null) return `${SLOT_NUMBER_RULE}\nGiá trị đã nhập: "${(raw ?? '').toString().trim()}"`;
    if (zones.some((z, i) => i !== ignoreIndex && parseSlotNumber(z.slotId) === n))
        return `Số thứ tự ${n} đã có ở vị trí khác trong ${placeLabel()}. Trong 1 Zone không thể có số thứ tự giống nhau.`;
    const other = usedElsewhere.get(n);
    if (other) return `Số thứ tự ${n} ${other}`;
    return null;
}

// So nho nhat con trong cua Zone (1, 2, 3...)
function nextFreeSlotId() {
    for (let n = 1; n < 1000000; n++) {
        const cand = String(n);
        if (!zones.some(z => parseSlotNumber(z.slotId) === cand) && !usedElsewhere.has(cand)) return cand;
    }
    return '';
}

function loadUsedSlotIds(allCameras) {
    usedElsewhere = new Map();
    const floor = (zoneCamera.floor || 'B1').toUpperCase();
    const zoneNo = parseSlotNumber(zoneCamera.zone) || '1';

    // Chi vi tri cua camera KHAC o CUNG tang va CUNG Zone moi chiem so (Zone khac / tang khac duoc dung lai)
    allCameras.filter(c => c.id !== zoneCamera.id
            && (c.floor || 'B1').toUpperCase() === floor
            && (parseSlotNumber(c.zone) || '1') === zoneNo).forEach(c => {
        (c.zones || []).forEach(z => {
            const n = parseSlotNumber(z.slotId);
            if (n) usedElsewhere.set(n, `đã được dùng ở ${placeLabel()} (camera ${c.ipAddress}). Trong 1 Zone không thể có số thứ tự giống nhau.`);
        });
    });
}

// ---------------------------------------------------------------------------
// Chon camera
// ---------------------------------------------------------------------------
async function loadZoneCameras() {
    const res = await fetch('/api/system/cameras');
    const all = await res.json();
    const nat = (a, b) => String(a ?? '').localeCompare(String(b ?? ''), undefined, { numeric: true, sensitivity: 'base' });
    // sap theo tang, Zone, IP de de tim khi co nhieu camera
    const committed = all.filter(c => c.isCommitted)
        .sort((a, b) => nat(a.floor, b.floor) || nat(a.zone, b.zone) || nat(a.ipAddress, b.ipAddress));

    const select = document.getElementById('zoneCameraSelect');
    const keep = select.value;                      // camera dang chon (giu lai khi lam moi danh sach)
    select.innerHTML = '<option value="">— Chọn camera —</option>' +
        committed.map(c => `<option value="${c.id}">${c.ipAddress} · ${c.brand || ''} ${c.floor ? '(' + c.floor + (c.zone ? ' · Zone ' + c.zone : '') + ')' : ''}</option>`).join('');

    document.getElementById('zoneEditorEmpty').textContent = committed.length === 0
        ? 'Chưa có camera nào trong bãi đỗ. Sang tab "Danh sách" để thêm và "Thêm vào hệ thống bãi đỗ" trước.'
        : 'Chọn 1 camera ở trên để bắt đầu.';

    if (!keep) return;
    const cam = committed.find(c => c.id === keep);
    if (!cam) {                                      // camera vua bi xoa / go khoi bai do
        select.value = '';
        select.dispatchEvent(new Event('change'));
        return;
    }
    select.value = keep;
    if (zoneCamera && zoneCamera.id === keep) {
        if ((cam.floor || '') !== (zoneCamera.floor || '') || (cam.zone || '') !== (zoneCamera.zone || ''))
            select.dispatchEvent(new Event('change'));   // doi tang / Zone: nap lai vung theo khoa vi tri moi
        else
            loadUsedSlotIds(all);                        // camera khac co the vua doi tang / Zone / so vi tri
    }
}

document.getElementById('zoneCameraSelect').addEventListener('change', async (e) => {
    const id = e.target.value;
    if (!id) {
        document.getElementById('zoneEditor').style.display = 'none';
        document.getElementById('zoneEditorEmpty').style.display = 'block';
        return;
    }

    const res = await fetch('/api/system/cameras');
    const all = await res.json();
    zoneCamera = all.find(c => c.id === id);
    if (!zoneCamera) return;

    zones = JSON.parse(JSON.stringify(zoneCamera.zones || []));
    loadUsedSlotIds(all);
    document.getElementById('motionThresholdInput').value = zoneCamera.motionThreshold ?? '';
    applyModeUi(zoneCamera.detectionMode || 'Automatic');

    // Goi y danh sach o do co san tren tang cua camera nay (de gan SlotId khong go nham)
    allFloorSlots = [];
    if (zoneCamera.floor) {
        const slotsRes = await fetch(`/api/slots?floor=${encodeURIComponent(zoneCamera.floor)}`);
        allFloorSlots = await slotsRes.json();
    }

    document.getElementById('zoneEditorEmpty').style.display = 'none';
    document.getElementById('zoneEditor').style.display = 'block';
    resetStage();
    renderZoneList();
    document.getElementById('testDetectResults').innerHTML = '';

    // Khoi phuc thiet lap giao dien DA LUU cua camera nay (nguon anh, chu ky tu lay anh, tu nhan dien)
    restoringUi = true;
    document.getElementById('captureIntervalInput').value = zoneCamera.uiCaptureIntervalSeconds || 10;
    document.getElementById('autoDetectChk').checked = zoneCamera.uiAutoDetect !== false;
    document.getElementById('registerChk').checked = zoneCamera.uiRegisterToParking !== false;
    const savedMode = (zoneCamera.uiSourceMode || 'Pc').toLowerCase() === 'camera' ? 'camera' : 'pc';
    setImageSource(savedMode);
    restoringUi = false;
    if (savedMode === 'camera' && zoneCamera.uiAutoCapture) startAutoCapture();
});

// ---------------------------------------------------------------------------
// Luu thiet lap giao dien cua camera (de mo lai van nhu cu)
// ---------------------------------------------------------------------------
let restoringUi = false;
let saveUiTimer = null;

function registerEnabled() {
    const c = document.getElementById('registerChk');
    return !!(c && c.checked);
}

function saveUiSettings() {
    if (restoringUi || !zoneCamera) return;
    clearTimeout(saveUiTimer);
    saveUiTimer = setTimeout(async () => {
        const body = {
            sourceMode: imageSource === 'camera' ? 'Camera' : 'Pc',
            captureIntervalSeconds: Math.max(2, parseInt(document.getElementById('captureIntervalInput').value, 10) || 10),
            autoCapture: !!autoCaptureTimer,
            autoDetect: document.getElementById('autoDetectChk').checked,
            registerToParking: registerEnabled()
        };
        try {
            const res = await fetch(`/api/system/cameras/${zoneCamera.id}/ui-settings`, {
                method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
            });
            if (res.ok) {
                const cam = await res.json();
                zoneCamera.uiSourceMode = cam.uiSourceMode;
                zoneCamera.uiCaptureIntervalSeconds = cam.uiCaptureIntervalSeconds;
                zoneCamera.uiAutoCapture = cam.uiAutoCapture;
                zoneCamera.uiAutoDetect = cam.uiAutoDetect;
                zoneCamera.uiRegisterToParking = cam.uiRegisterToParking;
            }
        } catch { /* khong luu duoc thiet lap giao dien: bo qua, khong anh huong chuc nang */ }
    }, 400);
}

document.getElementById('captureIntervalInput').addEventListener('change', saveUiSettings);
document.getElementById('autoDetectChk').addEventListener('change', saveUiSettings);
document.getElementById('registerChk').addEventListener('change', saveUiSettings);

// Khung anh co theo chieu cao man hinh (CSS dung bien --ar = rong/cao cua anh) de luon nhin thay tron ven
function setStageRatio(w, h) {
    zoneStage.style.aspectRatio = `${w} / ${h}`;
    zoneStage.style.setProperty('--ar', (w / h).toFixed(4));
    zoneStage.closest('.zone-main')?.style.setProperty('--ar', (w / h).toFixed(4));   // de khung thong tin xe rong bang anh
}

// Danh sach vung luon hien ben phai anh.
//  - Nut "Hien vung" o tieu de danh sach: an/hien duong vien (xanh duong, net dut) cua TAT CA vung tren anh.
//  - Nut o tung vung: an/hien toa do diem dau - diem cuoi cua RIENG vung do.
const zoneListSide = document.getElementById('zoneListSide');
const SHOW_ZONES_KEY = 'pg.zoneShowOutlines';
const ZONE_LINE_COLOR = '#2563eb';
let showZoneOutlines = true;
try { showZoneOutlines = localStorage.getItem(SHOW_ZONES_KEY) !== '0'; } catch { }
const pointsHiddenZones = new WeakSet(); // vung (doi tuong) dang an diem dau/cuoi

function syncZoneOutlineBtn() {
    const btn = document.getElementById('toggleZoneLinesBtn');
    if (!btn) return;
    btn.setAttribute('aria-pressed', showZoneOutlines ? 'true' : 'false');
    btn.classList.toggle('mode-active', showZoneOutlines);
    btn.textContent = showZoneOutlines ? '👁 Vùng: Hiện' : '🚫 Vùng: Ẩn';
}
document.getElementById('toggleZoneLinesBtn').addEventListener('click', () => {
    showZoneOutlines = !showZoneOutlines;
    try { localStorage.setItem(SHOW_ZONES_KEY, showZoneOutlines ? '1' : '0'); } catch { }
    syncZoneOutlineBtn();
    redrawCanvas();
});
syncZoneOutlineBtn();

function resetStage() {
    zonePreviewImg.style.display = 'none';
    zoneOverlay.width = 0; zoneOverlay.height = 0;
    zoneStagePlaceholder.style.display = 'flex';
    currentImageFile = null;
    lastTestResult = null;
    stopAutoCapture();
    exitDrawing();
}

// ---------------------------------------------------------------------------
// Chon nguon anh: Camera that (lay anh theo chu ky) hoac PC (anh mo phong)
// ---------------------------------------------------------------------------
let imageSource = 'pc'; // 'pc' | 'camera'
let autoCaptureTimer = null;

document.getElementById('srcPcBtn').addEventListener('click', () => { setImageSource('pc'); saveUiSettings(); });
document.getElementById('srcCameraBtn').addEventListener('click', () => { setImageSource('camera'); saveUiSettings(); });

function setImageSource(mode) {
    imageSource = mode;
    document.getElementById('srcPcBtn').classList.toggle('mode-active', mode === 'pc');
    document.getElementById('srcCameraBtn').classList.toggle('mode-active', mode === 'camera');

    const chooseLabel = document.getElementById('chooseImageLabel');
    const fileInput = document.getElementById('zoneImageInput');
    // Chi khi chon "Tu PC" thi nut "Chon anh mo phong" moi co tac dung (dung yeu cau)
    fileInput.disabled = mode !== 'pc';
    chooseLabel.classList.toggle('disabled', mode !== 'pc');
    chooseLabel.style.opacity = mode === 'pc' ? '1' : '.45';
    chooseLabel.style.pointerEvents = mode === 'pc' ? 'auto' : 'none';

    document.getElementById('cameraSourceControls').style.display = mode === 'camera' ? 'flex' : 'none';

    if (mode === 'pc') stopAutoCapture();
}

// fetch co gioi han thoi gian: camera khong phan hoi thi bo qua, khong treo giao dien
async function fetchWithTimeout(url, options = {}, timeoutMs = 10000) {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
        return await fetch(url, { ...options, signal: ctrl.signal });
    } finally {
        clearTimeout(timer);
    }
}

let snapshotBusy = false;

async function captureSnapshotNow() {
    if (!zoneCamera) { alert('Chọn 1 camera trước.'); return; }
    if (snapshotBusy) return; // lan lay truoc chua xong (camera cham/treo) -> bo qua chu ky nay
    snapshotBusy = true;
    const status = document.getElementById('captureStatus');
    status.textContent = '⏳ Đang lấy ảnh...';

    try {
        const res = await fetchWithTimeout(`/api/system/cameras/${zoneCamera.id}/snapshot?t=${Date.now()}`, {}, 12000);
        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            status.textContent = `⚠️ ${err.message || 'Không lấy được ảnh từ camera.'}`;
            return;
        }
        const blob = await res.blob();
        currentImageFile = blob; // dung chung bien voi luong "Chon anh mo phong" de testDetectBtn hoat dong khong doi

        const url = URL.createObjectURL(blob);
        zonePreviewImg.src = url;
        zonePreviewImg.onload = () => {
            setStageRatio(zonePreviewImg.naturalWidth, zonePreviewImg.naturalHeight);
            zoneOverlay.width = zonePreviewImg.naturalWidth;
            zoneOverlay.height = zonePreviewImg.naturalHeight;
            zonePreviewImg.style.display = 'block';
            zoneStagePlaceholder.style.display = 'none';
            redrawCanvas();
            autoDetectIfChanged(); // anh moi tu camera -> tu nhan dien neu thay doi vuot nguong
        };
        status.textContent = `✅ Đã lấy ảnh lúc ${new Date().toLocaleTimeString('vi-VN')}`;
    } catch (err) {
        status.textContent = err.name === 'AbortError'
            ? '⚠️ Camera không phản hồi (quá thời gian chờ), sẽ thử lại ở chu kỳ sau.'
            : '⚠️ Lỗi khi lấy ảnh: ' + err.message;
    } finally {
        snapshotBusy = false;
    }
}

document.getElementById('captureNowBtn').addEventListener('click', captureSnapshotNow);

document.getElementById('toggleAutoCaptureBtn').addEventListener('click', () => {
    if (autoCaptureTimer) stopAutoCapture();
    else startAutoCapture();
    saveUiSettings();
});

function startAutoCapture() {
    const seconds = Math.max(2, parseInt(document.getElementById('captureIntervalInput').value, 10) || 10);
    captureSnapshotNow(); // lay ngay 1 anh, khong doi het chu ky dau tien
    autoCaptureTimer = setInterval(captureSnapshotNow, seconds * 1000);
    document.getElementById('toggleAutoCaptureBtn').textContent = '⏸️ Dừng tự động';
}

function stopAutoCapture() {
    if (autoCaptureTimer) {
        clearInterval(autoCaptureTimer);
        autoCaptureTimer = null;
    }
    const btn = document.getElementById('toggleAutoCaptureBtn');
    if (btn) btn.textContent = '▶️ Bắt đầu tự động';
}

// ---------------------------------------------------------------------------
// Chon anh mo phong tu may tinh
// ---------------------------------------------------------------------------
document.getElementById('zoneImageInput').addEventListener('change', () => {
    const input = document.getElementById('zoneImageInput');
    const file = input.files[0];
    if (!file) return;

    currentImageFile = file;
    const url = URL.createObjectURL(file);
    zonePreviewImg.src = url;
    zonePreviewImg.onload = () => {
        setStageRatio(zonePreviewImg.naturalWidth, zonePreviewImg.naturalHeight);
        zoneOverlay.width = zonePreviewImg.naturalWidth;
        zoneOverlay.height = zonePreviewImg.naturalHeight;
        zonePreviewImg.style.display = 'block';
        zoneStagePlaceholder.style.display = 'none';
        redrawCanvas();
        autoDetectIfChanged(); // anh moi tu PC -> tu nhan dien neu thay doi vuot nguong
    };
});

// ---------------------------------------------------------------------------
// Tu dong nhan dien khi anh MOI (tu camera hoac PC) thay doi vuot nguong
// ---------------------------------------------------------------------------
let autoDetectBusy = false;
let autoDetectPending = false;

async function autoDetectIfChanged() {
    const chk = document.getElementById('autoDetectChk');
    if (!zoneCamera || !currentImageFile || !chk || !chk.checked) return;

    // Dang nhan dien anh truoc -> chi nho "co anh moi", xong se xu ly anh moi nhat (khong chay chong nhau)
    if (autoDetectBusy) { autoDetectPending = true; return; }
    autoDetectBusy = true;

    const camId = zoneCamera.id;
    const status = document.getElementById('autoDetectStatus');
    status.textContent = '⏳ Đang so sánh ảnh...';

    try {
        const form = new FormData();
        form.append('image', currentImageFile);
        const res = await fetchWithTimeout(
            `/api/system/cameras/${camId}/test-detect?onlyIfChanged=true&register=${registerEnabled()}`, { method: 'POST', body: form }, 60000);

        if (!zoneCamera || zoneCamera.id !== camId) return; // nguoi dung da chuyen sang camera khac

        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            status.textContent = `⚠️ ${err.message || 'Tự động nhận diện thất bại.'}`;
            return;
        }

        const result = await res.json();
        const time = new Date().toLocaleTimeString('vi-VN');
        if (result.skipped) {
            status.textContent = `⏭️ ${time} — ảnh chỉ thay đổi ${result.changePercent}% (ngưỡng ${result.threshold}%), bỏ qua.`;
            if (lastTestResult) drawTestResults(lastTestResult); // giu khung ket qua cu tren anh moi
        } else {
            lastTestResult = result;
            drawTestResults(result);
            renderTestSummary(result);
            status.textContent = `✅ ${time} — ảnh thay đổi vượt ngưỡng, đã nhận diện` +
                (result.registered || result.vacated ? ` · đã cập nhật bãi đỗ (${result.registered} ô có xe, ${result.vacated} ô trống)` : '') + '.';
        }
    } catch (err) {
        status.textContent = err.name === 'AbortError'
            ? '⚠️ Nhận diện quá thời gian chờ.'
            : '⚠️ Lỗi tự động nhận diện: ' + err.message;
    } finally {
        autoDetectBusy = false;
        if (autoDetectPending) { autoDetectPending = false; autoDetectIfChanged(); }
    }
}

// ---------------------------------------------------------------------------
// Ve vung moi: keo chuot tu diem dau toi diem cuoi (hinh chu nhat, ke ca nghieng theo % anh)
// ---------------------------------------------------------------------------
let dragStart = null; // {x, y} % - null khi khong dang keo

document.getElementById('drawZoneBtn').addEventListener('click', () => {
    if (!currentImageFile) { alert('Chọn ảnh mô phỏng trước.'); return; }
    drawing = true;
    dragStart = null;
    document.getElementById('drawZoneBtn').style.display = 'none';
    document.getElementById('cancelZoneBtn').style.display = 'inline-block';
    document.getElementById('drawHint').style.display = 'block';
    zoneOverlay.style.cursor = 'crosshair';
});

document.getElementById('cancelZoneBtn').addEventListener('click', exitDrawing);

function exitDrawing() {
    drawing = false;
    dragStart = null;
    zoneOverlay.style.cursor = 'default';
    document.getElementById('drawZoneBtn').style.display = 'inline-block';
    document.getElementById('cancelZoneBtn').style.display = 'none';
    document.getElementById('drawHint').style.display = 'none';
    redrawCanvas();
}

function pointFromEvent(e) {
    const rect = zoneOverlay.getBoundingClientRect();
    const xPct = ((e.clientX - rect.left) / rect.width) * 100;
    const yPct = ((e.clientY - rect.top) / rect.height) * 100;
    return { x: Math.max(0, Math.min(100, xPct)), y: Math.max(0, Math.min(100, yPct)) };
}

/// Tao 4 dinh hinh chu nhat tu 2 diem goc bat ky (khong can sap xep truoc).
function rectFromCorners(p1, p2) {
    const left = Math.min(p1.x, p2.x), right = Math.max(p1.x, p2.x);
    const top = Math.min(p1.y, p2.y), bottom = Math.max(p1.y, p2.y);
    return [{ x: left, y: top }, { x: right, y: top }, { x: right, y: bottom }, { x: left, y: bottom }];
}

zoneOverlay.addEventListener('mousedown', (e) => {
    if (!drawing) return;
    dragStart = pointFromEvent(e);
});

zoneOverlay.addEventListener('mousemove', (e) => {
    if (!drawing || !dragStart) return;
    const current = pointFromEvent(e);
    redrawCanvas();
    drawPolygon(rectFromCorners(dragStart, current), zoneOverlay.width, zoneOverlay.height, '#ffffff', true, null);
});

zoneOverlay.addEventListener('mouseup', (e) => {
    if (!drawing || !dragStart) return;
    const end = pointFromEvent(e);

    if (Math.abs(end.x - dragStart.x) < 1.5 || Math.abs(end.y - dragStart.y) < 1.5) {
        dragStart = null; // vung qua nho (co the chi la cu nhap chuot nham) - bo qua, van o che do ve
        redrawCanvas();
        return;
    }

    const points = rectFromCorners(dragStart, end);
    dragStart = null;

    // So thu tu phai la so nguyen duong va KHONG TRUNG trong tang: goi y so ke tiep con trong, hoi lai den khi hop le hoac huy
    let slotId = null;
    let proposal = nextFreeSlotId();
    while (true) {
        const input = prompt(
            `Số thứ tự của vị trí này? (số nguyên dương: 1, 2, 3... — không được trùng trong ${placeLabel()})`, proposal);
        if (input === null || !input.trim()) { redrawCanvas(); return; }
        const err = checkSlotId(input, null);
        if (!err) { slotId = parseSlotNumber(input); break; }
        alert(err);
        proposal = nextFreeSlotId();
    }

    zones.push({
        id: 'tmp_' + Date.now() + '_' + Math.floor(Math.random() * 1000),
        name: `Ô ${slotId}`,
        slotId,
        points
    });

    exitDrawing();
    renderZoneList();
});

// ---------------------------------------------------------------------------
// Ve len canvas: cac vung da hoan tat + vung dang ve dang do
// ---------------------------------------------------------------------------
function redrawCanvas() {
    zoneCtx.clearRect(0, 0, zoneOverlay.width, zoneOverlay.height);
    const W = zoneOverlay.width, H = zoneOverlay.height;
    if (!W || !H) return;

    if (!showZoneOutlines) return;
    zones.forEach(z => {
        drawPolygon(z.points, W, H, ZONE_LINE_COLOR, true, z.slotId, true);
    });
}

function drawPolygon(points, W, H, color, closed, label, dashed = false) {
    if (points.length === 0) return;
    zoneCtx.beginPath();
    points.forEach((p, i) => {
        const x = p.x / 100 * W, y = p.y / 100 * H;
        if (i === 0) zoneCtx.moveTo(x, y); else zoneCtx.lineTo(x, y);
    });
    if (closed) zoneCtx.closePath();
    zoneCtx.strokeStyle = color;
    zoneCtx.lineWidth = Math.max(2, W / 350);
    zoneCtx.setLineDash(dashed ? [Math.max(8, W / 80), Math.max(5, W / 130)] : []);
    zoneCtx.stroke();
    zoneCtx.setLineDash([]);
    if (closed) {
        zoneCtx.fillStyle = color + '33';
        zoneCtx.fill();
    }

    if (label) {
        const cx = points.reduce((s, p) => s + p.x, 0) / points.length / 100 * W;
        const cy = points.reduce((s, p) => s + p.y, 0) / points.length / 100 * H;
        const fs = Math.max(14, W / 40);
        zoneCtx.font = `bold ${fs}px sans-serif`;
        zoneCtx.fillStyle = color;
        zoneCtx.textAlign = 'center';
        zoneCtx.fillText(label, cx, cy);
        zoneCtx.textAlign = 'start';
    }
}

// ---------------------------------------------------------------------------
// Danh sach vung + chinh diem bang number up/down
// ---------------------------------------------------------------------------
function renderZoneList() {
    const container = document.getElementById('zoneList');
    document.getElementById('zoneCount').textContent = zones.length;
    if (zones.length === 0) {
        container.innerHTML = '<p class="no-result">Chưa có vùng nào. Bấm "➕ Vẽ vùng mới".</p>';
        redrawCanvas();
        return;
    }

    container.innerHTML = zones.map((z, zi) => {
        const xs = z.points.map(p => p.x), ys = z.points.map(p => p.y);
        const x1 = Math.min(...xs), x2 = Math.max(...xs);
        const y1 = Math.min(...ys), y2 = Math.max(...ys);
        return `
        <div class="zone-item${pointsHiddenZones.has(z) ? ' hide-points' : ''}" style="border-left:4px solid ${ZONE_LINE_COLOR}">
            <div class="zone-item-header">
                <input class="zone-slot-input" data-zi="${zi}" type="text" inputmode="numeric" pattern="[0-9]*" value="${z.slotId || ''}" placeholder="Số thứ tự" title="Số thứ tự ô đỗ (1, 2, 3...)" />
                <button class="btn secondary btn-sm zone-points-toggle${pointsHiddenZones.has(z) ? '' : ' mode-active'}" data-togpts="${zi}" aria-pressed="${pointsHiddenZones.has(z) ? 'false' : 'true'}" title="Ẩn / hiện toạ độ điểm đầu - điểm cuối của vùng này">📐</button>
                <button class="btn secondary btn-sm" data-delzone="${zi}">🗑️</button>
            </div>
            <table class="zone-points-table">
                <tbody>
                    <tr>
                        <td>Đầu</td>
                        <td><input type="number" step="0.5" min="0" max="100" value="${x1.toFixed(1)}" data-zi="${zi}" data-corner="x1" title="X (%)" class="zone-corner-input" /></td>
                        <td><input type="number" step="0.5" min="0" max="100" value="${y1.toFixed(1)}" data-zi="${zi}" data-corner="y1" title="Y (%)" class="zone-corner-input" /></td>
                    </tr>
                    <tr>
                        <td>Cuối</td>
                        <td><input type="number" step="0.5" min="0" max="100" value="${x2.toFixed(1)}" data-zi="${zi}" data-corner="x2" title="X (%)" class="zone-corner-input" /></td>
                        <td><input type="number" step="0.5" min="0" max="100" value="${y2.toFixed(1)}" data-zi="${zi}" data-corner="y2" title="Y (%)" class="zone-corner-input" /></td>
                    </tr>
                </tbody>
            </table>
        </div>`;
    }).join('');

    container.querySelectorAll('.zone-slot-input').forEach(input => {
        input.addEventListener('change', () => {
            const zi = parseInt(input.dataset.zi, 10);
            const err = checkSlotId(input.value, zi);
            if (err) {
                alert(err);
                input.value = zones[zi].slotId || ''; // tra ve so thu tu cu
                return;
            }
            const n = parseSlotNumber(input.value);
            zones[zi].slotId = n;
            zones[zi].name = `Ô ${n}`;
            input.value = n;
            redrawCanvas();
        });
    });

    container.querySelectorAll('.zone-corner-input').forEach(input => {
        input.addEventListener('input', () => {
            const zi = parseInt(input.dataset.zi, 10);
            const xs = zones[zi].points.map(p => p.x), ys = zones[zi].points.map(p => p.y);
            const corners = { x1: Math.min(...xs), y1: Math.min(...ys), x2: Math.max(...xs), y2: Math.max(...ys) };
            corners[input.dataset.corner] = Math.max(0, Math.min(100, parseFloat(input.value) || 0));
            zones[zi].points = rectFromCorners({ x: corners.x1, y: corners.y1 }, { x: corners.x2, y: corners.y2 });
            redrawCanvas();
        });
    });

    container.querySelectorAll('[data-togpts]').forEach(btn => {
        btn.addEventListener('click', () => {
            const z = zones[parseInt(btn.dataset.togpts, 10)];
            if (pointsHiddenZones.has(z)) pointsHiddenZones.delete(z); else pointsHiddenZones.add(z);
            const hidden = pointsHiddenZones.has(z);
            btn.closest('.zone-item').classList.toggle('hide-points', hidden);
            btn.classList.toggle('mode-active', !hidden);
            btn.setAttribute('aria-pressed', hidden ? 'false' : 'true');
        });
    });

    container.querySelectorAll('[data-delzone]').forEach(btn => {
        btn.addEventListener('click', () => {
            zones.splice(parseInt(btn.dataset.delzone, 10), 1);
            renderZoneList();
        });
    });

    redrawCanvas();
}

// ---------------------------------------------------------------------------
// Luu vung + nguong chuyen dong
// ---------------------------------------------------------------------------
document.getElementById('saveZonesBtn').addEventListener('click', async () => {
    if (!zoneCamera) return;
    for (let i = 0; i < zones.length; i++) {
        const z = zones[i];
        const err = checkSlotId(z.slotId, i);
        if (err) { alert(`Vùng thứ ${i + 1}: ${err}`); return; }
        z.slotId = parseSlotNumber(z.slotId);
        z.name = `Ô ${z.slotId}`;
        if (z.points.length < 3) { alert(`Vùng ${z.slotId} cần ít nhất 3 điểm.`); return; }
    }

    const res = await fetch(`/api/system/cameras/${zoneCamera.id}/zones`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ zones })
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không lưu được vùng nhận diện.');
        return;
    }
    alert('Đã lưu vùng nhận diện cho camera này.');
});

document.getElementById('saveMotionBtn').addEventListener('click', async () => {
    if (!zoneCamera) return;
    const raw = document.getElementById('motionThresholdInput').value;
    const value = raw === '' ? null : parseFloat(raw);

    await fetch(`/api/system/cameras/${zoneCamera.id}/motion-threshold`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ motionThreshold: value })
    });
    alert('Đã lưu ngưỡng tối ưu.');
});

// ---------------------------------------------------------------------------
// Che do nhan dien: Tu dong / Thu cong + nut "Nhan dien ngay"
// ---------------------------------------------------------------------------
function applyModeUi(mode) {
    const autoBtn = document.getElementById('modeAutoBtn');
    const manualBtn = document.getElementById('modeManualBtn');
    const detectNowBtn = document.getElementById('detectNowBtn');

    autoBtn.classList.toggle('mode-active', mode !== 'Manual');
    manualBtn.classList.toggle('mode-active', mode === 'Manual');
    detectNowBtn.style.display = mode === 'Manual' ? 'inline-block' : 'none';
    document.getElementById('detectNowStatus').textContent = '';
}

async function setDetectionMode(mode) {
    if (!zoneCamera) return;
    const res = await fetch(`/api/system/cameras/${zoneCamera.id}/detection-mode`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode })
    });
    if (res.ok) {
        zoneCamera.detectionMode = mode;
        applyModeUi(mode);
    }
}

document.getElementById('modeAutoBtn').addEventListener('click', () => setDetectionMode('Automatic'));
document.getElementById('modeManualBtn').addEventListener('click', () => setDetectionMode('Manual'));

document.getElementById('detectNowBtn').addEventListener('click', async () => {
    if (!zoneCamera) return;
    const btn = document.getElementById('detectNowBtn');
    const status = document.getElementById('detectNowStatus');
    btn.disabled = true;
    btn.textContent = '⏳ Đang nhận diện...';
    status.textContent = '';

    try {
        const res = await fetch(`/api/system/cameras/${zoneCamera.id}/detect-now`, { method: 'POST' });
        const result = await res.json().catch(() => ({}));
        status.textContent = res.ok
            ? result.message
            : `⚠️ ${result.message || 'Nhận diện thất bại.'} (Cần khai báo "Nguồn video" hợp lệ ở tab Danh sách.)`;
        if (res.ok && typeof loadSlots === 'function') loadSlots();
    } catch (err) {
        status.textContent = 'Có lỗi khi gọi API: ' + err.message;
    } finally {
        btn.disabled = false;
        btn.textContent = '▶️ Nhận diện ngay';
    }
});

// ---------------------------------------------------------------------------
// Chay thu nhan dien (dung dung cac vung da ve, chua can Luu truoc)
// ---------------------------------------------------------------------------
document.getElementById('testDetectBtn').addEventListener('click', async () => {
    if (!zoneCamera) return;
    if (!currentImageFile) { alert('Chọn ảnh mô phỏng trước.'); return; }

    const btn = document.getElementById('testDetectBtn');
    btn.disabled = true;
    btn.textContent = '⏳ Đang nhận diện...';

    try {
        // Luu tam vung hien tai truoc khi test, de lo cot AI dung dung ban ve moi nhat
        await fetch(`/api/system/cameras/${zoneCamera.id}/zones`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ zones: zones.filter(z => z.slotId && z.points.length >= 3) })
        });

        const form = new FormData();
        form.append('image', currentImageFile);
        const res = await fetch(`/api/system/cameras/${zoneCamera.id}/test-detect?register=${registerEnabled()}`, { method: 'POST', body: form });
        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            alert(err.message || 'Nhận diện thất bại.');
            return;
        }
        const result = await res.json();
        lastTestResult = result;
        drawTestResults(result);
        renderTestSummary(result);
    } catch (err) {
        alert('Có lỗi khi gọi API: ' + err.message);
    } finally {
        btn.disabled = false;
        btn.textContent = '🔍 Chạy thử nhận diện';
    }
});

function drawTestResults(result) {
    redrawCanvas(); // ve lai cac vung truoc
    const unit = Math.max(1, zoneOverlay.width / 700);
    const lw = Math.max(2, Math.round(2.5 * unit));
    const fs = Math.max(13, Math.round(15 * unit));

    result.vehicles.forEach(v => {
        const { x1, y1, x2, y2 } = v.box;
        const matched = !!v.slotId;
        zoneCtx.strokeStyle = matched ? '#22c55e' : '#94a3b8';
        zoneCtx.lineWidth = lw;
        zoneCtx.strokeRect(x1, y1, x2 - x1, y2 - y1);

        const label = matched
            ? `${v.slotId} · ${v.plateNumber || '?'} (${Math.round(v.coverage * 100)}%)`
            : `${v.vehicleType.toLowerCase()} · ${v.reason === 'zoneTaken' ? 'vùng đã có xe khác' : 'ngoài ô đỗ'}`;
        zoneCtx.font = `bold ${fs}px sans-serif`;
        const tw = zoneCtx.measureText(label).width + 10 * unit;
        zoneCtx.fillStyle = matched ? '#16a34a' : '#475569';
        zoneCtx.fillRect(x1, Math.max(0, y1 - fs - 8 * unit), tw, fs + 8 * unit);
        zoneCtx.fillStyle = '#fff';
        zoneCtx.textBaseline = 'middle';
        zoneCtx.fillText(label, x1 + 5 * unit, Math.max(0, y1 - fs - 8 * unit) + (fs + 8 * unit) / 2);
        zoneCtx.textBaseline = 'alphabetic';
    });
}

function renderTestSummary(result) {
    const box = document.getElementById('testDetectResults');
    const summary = `🚗 Phát hiện ${result.vehicleCount} xe · ✅ Khớp ô đỗ: ${result.matchedCount}` +
        (registerEnabled() ? ` · 🗺️ Đã cập nhật bãi đỗ/bản đồ: ${result.registered} ô có xe, ${result.vacated} ô vừa trống` : ' · (chưa ghi nhận vào bãi đỗ)');
    const rows = result.vehicles.map(v => `
        <div class="detection-row">
            <span class="detection-index">${v.slotId ? '✅' : '—'}</span>
            <span>${v.slotId ? `<strong>${v.slotId}</strong>` : (v.reason === 'zoneTaken' ? 'Vùng đã có xe khác (bỏ qua)' : 'Ngoài mọi ô đỗ')}</span>
            <span>${v.plateNumber ? `<span class="plate-tag">${v.plateNumber}</span>` : ''}</span>
            <span>Độ che phủ: ${Math.round(v.coverage * 100)}%</span>
            <span>Loại: ${v.vehicleType.toLowerCase()}</span>
        </div>
    `).join('');
    box.innerHTML = `<div class="detection-summary">${summary}</div>${rows}`;
}

// ---------------------------------------------------------------------------
// Khoi tao khi chuyen sang tab nay
// ---------------------------------------------------------------------------
// Moi lan vao tab deu nap lai danh sach camera (camera moi them / vua dua vao bai do / doi tang-Zone phai hien ngay, khong can tai lai trang)
document.querySelector('[data-tab="zones"]').addEventListener('click', () => { loadZoneCameras(); });
