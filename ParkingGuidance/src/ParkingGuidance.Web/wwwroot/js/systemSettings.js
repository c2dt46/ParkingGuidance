// ---------------------------------------------------------------------------
// Tab "Cau hinh chung": cai dat nhan dien (luu lai) + trang thai hang doi AI / camera chay nen
// ---------------------------------------------------------------------------
const settingFields = {
    intervalSeconds: 'setInterval',
    defaultMotionThresholdPercent: 'setMotion',
    confidenceThreshold: 'setConf',
    detectCar: 'setDetectCar',
    detectBus: 'setDetectBus',
    detectTruck: 'setDetectTruck',
    detectMotorcycle: 'setDetectMotorcycle',
    openTimeoutSeconds: 'setOpenTimeout',
    frameReadTimeoutSeconds: 'setReadTimeout',
    maxRetryBackoffSeconds: 'setBackoff',
    queueWarnSeconds: 'setQueueWarn',
    vacancyConfirmFrames: 'setVacancy',
    plateDetector: 'setPlateDetector',
    charCombine: 'setCharCombine',
    plateBoxConfidence: 'setPlateBoxConf',
    charConfidence: 'setCharConf',
    plateMinOverlap: 'setPlateOverlap',
    processMaxSide: 'setProcessMaxSide',
    plateReadHeight: 'setPlateReadHeight',
    plateFullSearch: 'setPlateFullSearch',
    plateBoxMode: 'setPlateBoxMode',
    plateBoxOffsetPercent: 'setPlateBoxOffset'
};
let queueTimer = null;

// ---- Nut Luu: sang khi co thay doi chua luu, toi (vo hieu) khi khong con thay doi nao ----
let settingsBaseline = null;   // gia tri cua tung o ngay sau khi tai / luu thanh cong
let userTouched = false;       // nguoi dung da doi o nao do (dung khi chua co gia tri goc de so sanh)
let savedText = '';            // dong trang thai hien khi khong co thay doi (vd "Da luu luc ...")

function settingsSnapshot() {
    const snap = {};
    for (const id of Object.values(settingFields)) snap[id] = document.getElementById(id).value;
    return snap;
}

function updateSaveState() {
    const btn = document.getElementById('saveSettingsBtn');
    const status = document.getElementById('settingsStatus');
    const cur = settingsSnapshot();
    // Co gia tri goc: so tung o voi gia tri da luu (doi roi doi lai nhu cu = khong con thay doi). Chua co gia tri goc: coi la co thay doi khi nguoi dung da sua.
    const dirty = settingsBaseline !== null ? Object.keys(cur).some(id => cur[id] !== settingsBaseline[id]) : userTouched;
    btn.disabled = !dirty;
    btn.classList.toggle('save-dirty', dirty);
    btn.classList.toggle('save-idle', !dirty);
    status.textContent = dirty ? '● Có thay đổi chưa lưu' : savedText;
}

function markSettingsClean() {
    settingsBaseline = settingsSnapshot();
    userTouched = false;
    updateSaveState();
}

function esc(v) {
    return String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

async function loadSettings() {
    try {
        const res = await fetch('/api/system/settings');
        const data = await res.json();
        for (const [key, id] of Object.entries(settingFields))
            document.getElementById(id).value = data.settings[key];
        document.getElementById('dataDirText').textContent = data.dataDirectory;
        markSettingsClean();
    } catch (err) {
        document.getElementById('settingsStatus').textContent = '⚠️ Không tải được cài đặt: ' + err.message;
    }
}

document.getElementById('saveSettingsBtn').addEventListener('click', async () => {
    const status = document.getElementById('settingsStatus');
    const body = {};
    for (const [key, id] of Object.entries(settingFields)) {
        const v = parseFloat(document.getElementById(id).value);
        if (Number.isNaN(v)) { status.textContent = '⚠️ Có ô chưa nhập số hợp lệ.'; return; }
        body[key] = v;
    }
    try {
        const res = await fetch('/api/system/settings', {
            method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
        });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        const data = await res.json();
        for (const [key, id] of Object.entries(settingFields))
            document.getElementById(id).value = data.settings[key]; // hien lai gia tri da duoc chuan hoa
        savedText = '✅ Đã lưu lúc ' + new Date().toLocaleTimeString('vi-VN');
        markSettingsClean();                       // nut Luu toi lai: khong con thay doi nao chua luu
    } catch (err) {
        status.textContent = '⚠️ Lưu thất bại: ' + err.message;
    }
});

// Moi lan nguoi dung doi gia tri mot o → so voi gia tri da luu de bat / tat nut Luu
for (const id of Object.values(settingFields)) {
    const el = document.getElementById(id);
    const onEdit = () => { userTouched = true; updateSaveState(); };
    el.addEventListener('input', onEdit);
    el.addEventListener('change', onEdit);
}

updateSaveState();   // trang thai ban dau: toi

async function refreshQueue() {
    try {
        const res = await fetch('/api/system/queue');
        const { queue, cameras } = await res.json();

        const running = queue.running
            ? `Đang xử lý: <strong>${esc(queue.running)}</strong> (${Math.round(queue.runningSeconds)}s)`
            : 'AI đang rảnh';
        document.getElementById('queueSummary').innerHTML =
            `${running} · Đang chờ: <strong>${queue.pendingBackground + queue.pendingInteractive}</strong> việc ` +
            `(${queue.pendingInteractive} ưu tiên) · Đã xử lý: ${queue.processed} · Khung cũ bị thay thế: ${queue.coalesced}` +
            (queue.lastDurationMs ? ` · Việc gần nhất: ${(queue.lastDurationMs / 1000).toFixed(1)}s` : '');

        const box = document.getElementById('queueCameras');
        if (!cameras.length) {
            box.innerHTML = '<div class="no-result">Chưa có camera nào được thêm vào hệ thống bãi đỗ.</div>';
            return;
        }
        const ago = s => (s === null || s === undefined) ? '—' : `${s}s trước`;
        box.innerHTML = `<table class="data-table"><thead><tr>
            <th>Camera</th><th>Trạng thái</th><th>Ảnh gần nhất</th><th>Nhận diện gần nhất</th><th>Thay đổi ảnh</th><th>Lỗi</th>
        </tr></thead><tbody>` + cameras.map(c => `<tr>
            <td>${esc(c.ipAddress)}${c.floor ? ' · ' + esc(c.floor) : ''}</td>
            <td>${esc(c.state)}</td>
            <td>${ago(c.lastFrameSecondsAgo)}</td>
            <td>${ago(c.lastProcessedSecondsAgo)}${c.lastProcessMs ? ' (' + (c.lastProcessMs / 1000).toFixed(1) + 's)' : ''}</td>
            <td>${c.lastChangePercent ?? '—'}${c.lastChangePercent != null ? '%' : ''}</td>
            <td>${c.failures ? '×' + c.failures + ' ' : ''}${esc(c.lastError || '')}</td>
        </tr>`).join('') + '</tbody></table>';
    } catch { /* tam thoi mat ket noi toi server: lan lam moi ke tiep se thu lai */ }
}

function startQueuePolling() {
    stopQueuePolling();
    refreshQueue();
    queueTimer = setInterval(refreshQueue, 3000);
}
function stopQueuePolling() {
    if (queueTimer) { clearInterval(queueTimer); queueTimer = null; }
}

// Chi lam moi khi dang mo tab nay (tiet kiem tai)
document.querySelectorAll('.tab-btn').forEach(btn => btn.addEventListener('click', () => {
    if (btn.dataset.tab === 'config') { loadSettings(); startQueuePolling(); }
    else stopQueuePolling();
}));
