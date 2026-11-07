// (Doi ten tu floors.js -> floorManager.js de trinh duyet/ban build cu khong con dung nham file cu.)
// Quản lý mặt bằng dùng chung cho cả bản đồ ô đỗ mặc định (dashboard.js) và
// Layout Editor (layout.js). Cả 2 file đó lắng nghe sự kiện 'floorchange' trên window.

window.currentFloor = null;
window.currentFloorData = null;
window.floorList = [];                      // danh sách mặt bằng mới nhất (layout.js dùng để tìm Header / Navigation)
const PANEL = window.PG_PANEL || null;      // 'Header' | 'Navigation' khi trang này là iframe của màn hình Header / Navigation lúc chạy
const KIND_LABEL = { Header: 'Header', Navigation: 'Navigation' };

const floorSelect = document.getElementById('floorSelect');
const addFloorBtn = document.getElementById('addFloorBtn');
const deleteFloorBtn = document.getElementById('deleteFloorBtn');

async function fetchFloors() {
    const res = await fetch('/api/floors');
    return res.json();
}

async function loadFloors(preferredId) {
    const floors = await fetchFloors();
    window.floorList = floors;

    window.dispatchEvent(new CustomEvent('floorlistchange', { detail: floors }));   // menu trái cập nhật mục con của "Giám sát"

    floorSelect.innerHTML = '';
    floors.forEach(f => {
        const opt = document.createElement('option');
        opt.value = f.id;
        opt.textContent = KIND_LABEL[f.kind] ? f.name : `${f.isStartup ? '★ ' : ''}${f.id} — ${f.name}`;   // Header / Navigation: chỉ hiện tên
        floorSelect.appendChild(opt);
    });

    // Iframe Header / Navigation: chỉ hiện đúng màn hình của loại đó (không có thì để trống)
    if (PANEL) {
        const pf = floors.find(f => f.kind === PANEL && f.enabled !== false);
        if (pf) { floorSelect.value = pf.id; setCurrentFloor(pf.id, floors); }
        return;
    }

    // Không chỉ định mặt bằng (mở trang lần đầu / vừa xoá mặt bằng đang chọn) -> mặt bằng khởi động, rồi mới tới mặt bằng đầu danh sách.
    // Đang ở chế độ Chạy thì không bao giờ mở Header / Navigation như 1 mặt bằng (2 màn hình này có khung riêng).
    const running = document.body.classList.contains('layout-run-mode');
    const usable = floors.filter(f => !running || !f.kind || f.kind === 'Normal');
    const targetId = (preferredId && usable.some(f => f.id === preferredId))
        ? preferredId
        : (usable.find(f => f.isStartup)?.id ?? usable[0]?.id ?? floors[0]?.id ?? null);

    if (targetId) {
        floorSelect.value = targetId;
        setCurrentFloor(targetId, floors);
    }
}

// Nhớ mặt bằng đang xem (trong bộ nhớ chương trình, mất khi khởi động lại): chuyển mục khác rồi quay lại vẫn đúng mặt bằng đã chọn
async function fetchSelectedFloor() {
    try {
        const r = await fetch('/api/floors/session/selected', { cache: 'no-store' });
        return r.ok ? ((await r.json()).id || null) : null;
    } catch { return null; }
}
function rememberFloor(id) {
    fetch('/api/floors/session/selected', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id })
    }).catch(() => { /* bỏ qua: lần sau mở lại mặt bằng khởi động */ });
}

function setCurrentFloor(id, floors) {
    window.currentFloor = id;
    window.currentFloorData = (floors || []).find(f => f.id === id) || null;
    window.floorList = floors || window.floorList;
    if (!PANEL) rememberFloor(id);       // iframe Header / Navigation không được ghi đè mặt bằng đang xem
    syncStartupCheckbox();
    syncKindControls();
    window.dispatchEvent(new CustomEvent('floorchange', { detail: id }));
}

// Ô "Mặt bằng khởi động đầu tiên": tích khi mặt bằng đang chọn chính là mặt bằng khởi động.
// Đang tích thì khoá (luôn phải có đúng 1 mặt bằng khởi động) — muốn đổi thì chọn mặt bằng khác rồi tích vào đó.
const startupChk = document.getElementById('startupFloorChk');

function syncStartupCheckbox() {
    if (!startupChk) return;
    const isStartup = !!window.currentFloorData?.isStartup;
    startupChk.checked = isStartup;
    const isPanel = !!window.currentFloorData?.kind && window.currentFloorData.kind !== 'Normal';
    startupChk.disabled = !window.currentFloorData || isStartup || isPanel;
    startupChk.title = isStartup
        ? 'Đây đang là mặt bằng khởi động. Chọn mặt bằng khác rồi tích vào đó để chuyển.'
        : (isPanel ? 'Header / Navigation không thể là mặt bằng khởi động.' : '');
}

// ---- Header / Navigation (2 màn hình mặc định, luôn có sẵn, tên cố định) + màu nền của mọi mặt bằng ----
const panelGroup = document.getElementById('floorPanelGroup');
const enabledChk = document.getElementById('floorEnabled');
const panelW = document.getElementById('floorPanelW');
const panelH = document.getElementById('floorPanelH');
const acctField = document.getElementById('floorShowAccountField');
const acctChk = document.getElementById('floorShowAccount');
const fullChk = document.getElementById('floorFullSize');
const bgColor = document.getElementById('floorBgColor');
if (bgColor && window.PgColor) PgColor.enhance(bgColor.parentElement);   // bang chon mau Theme / Standard / RGB
const bgColorReset = document.getElementById('floorBgColorReset');
const DEFAULT_PANEL_SIZE = { Header: [1920, 100], Navigation: [300, 980] };

const isPanelData = (d) => !!d?.kind && d.kind !== 'Normal';

// Toàn kích thước: Header không cần Rộng, Navigation không cần Cao (tự lấp đầy)
function syncFullDisable() {
    const kind = window.currentFloorData?.kind;
    const full = !!fullChk?.checked;
    if (panelW) panelW.disabled = full && kind === 'Header';
    if (panelH) panelH.disabled = full && kind === 'Navigation';
}
fullChk?.addEventListener('change', syncFullDisable);

function rgbToHex(rgb) {
    const m = /rgba?\((\d+)[ ,]+(\d+)[ ,]+(\d+)/.exec(rgb || '');
    return m ? '#' + [m[1], m[2], m[3]].map(n => (+n).toString(16).padStart(2, '0')).join('') : '#ffffff';
}

function syncKindControls() {
    const d = window.currentFloorData;
    const kind = d?.kind || 'Normal';
    const panel = isPanelData(d);
    if (panelGroup) panelGroup.hidden = !panel;
    if (enabledChk) enabledChk.checked = d?.enabled !== false;
    const def = DEFAULT_PANEL_SIZE[kind] || [0, 0];
    if (panelW) panelW.value = d?.panelWidth || def[0] || '';
    if (panelH) panelH.value = d?.panelHeight || def[1] || '';
    if (acctField) acctField.hidden = kind !== 'Navigation';
    if (acctChk) acctChk.checked = kind === 'Navigation' && !!d?.showAccount;
    if (fullChk) fullChk.checked = panel && !!d?.fullSize;
    syncFullDisable();
    document.body.classList.toggle('floor-is-panel', panel);
    document.body.classList.toggle('show-account', kind === 'Navigation' && !!d?.showAccount);   // iframe Navigation: hiện khối tài khoản
    // Header / Navigation là mặt bằng mặc định: không đổi tên, không xoá
    const ren = document.getElementById('renameFloorBtn');
    if (ren) ren.disabled = panel;
    if (deleteFloorBtn) deleteFloorBtn.disabled = panel;
    // Màu nền: chưa đặt thì ô chọn hiện màu nền hiện tại của khung
    if (bgColor) {
        bgColor.value = d?.backgroundColor || rgbToHex(getComputedStyle(document.querySelector('.layout-canvas') || document.body).backgroundColor);
        if (window.PgColor) PgColor.refresh(bgColor);
    }
}

let panelSaving = Promise.resolve();
async function savePanel() {
    const id = window.currentFloor;
    if (!id || !isPanelData(window.currentFloorData)) return;
    const kind = window.currentFloorData.kind;
    const body = {
        enabled: !!enabledChk?.checked,
        width: parseInt(panelW.value, 10) || null,
        height: parseInt(panelH.value, 10) || null,
        fullSize: !!fullChk?.checked,
    };
    if (kind === 'Navigation') body.showAccount = !!acctChk?.checked;
    const res = await fetch(`/api/floors/${encodeURIComponent(id)}/panel`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });
    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || (res.status === 404 || res.status === 405
            ? 'Máy chủ chưa phải bản mới. Hãy build lại và chạy lại ứng dụng .NET (cần cả các file .cs đã sửa).'
            : 'Không lưu được thiết lập.'));
        syncKindControls();
        return;
    }
    await loadFloors(id);    // nạp lại: tỉ lệ khung, danh sách...
}

// Tự lưu ngay khi đổi từng thiết lập (không cần nút Áp dụng); các lần đổi liên tiếp được xếp hàng để không ghi chồng nhau
const queuePanelSave = () => { panelSaving = panelSaving.then(savePanel).catch(() => { }); };
[enabledChk, fullChk, acctChk, panelW, panelH].forEach(el => el?.addEventListener('change', queuePanelSave));
[panelW, panelH].forEach(el => el?.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); el.blur(); } }));

async function saveFloorColor(color) {
    const id = window.currentFloor;
    if (!id) return;
    const res = await fetch(`/api/floors/${encodeURIComponent(id)}/color`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ color: color || '' })
    });
    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Máy chủ chưa phải bản mới. Hãy build lại và chạy lại ứng dụng .NET.');
        syncKindControls();
        return;
    }
    await loadFloors(id);    // layout.js nhận 'floorchange' và vẽ lại với màu mới
}
bgColor?.addEventListener('change', () => saveFloorColor(bgColor.value));
bgColorReset?.addEventListener('click', () => saveFloorColor(''));

startupChk?.addEventListener('change', async () => {
    if (!window.currentFloor || !startupChk.checked) { syncStartupCheckbox(); return; }
    const res = await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}/startup`, { method: 'PUT' });
    if (!res.ok) {
        alert('Không đặt được mặt bằng khởi động.');
        syncStartupCheckbox();
        return;
    }
    await loadFloors(window.currentFloor);   // nạp lại để dấu ★ chuyển sang mặt bằng này
});

floorSelect.addEventListener('change', async () => {
    const floors = await fetchFloors();
    setCurrentFloor(floorSelect.value, floors);
});

addFloorBtn.addEventListener('click', async () => {
    const id = prompt('Mã mặt bằng mới (ví dụ: B2):');
    if (!id || !id.trim()) return;
    const name = prompt('Tên hiển thị (ví dụ: Tầng hầm B2):', id.trim()) || id.trim();

    const res = await fetch('/api/floors', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: id.trim(), name })
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không thêm được mặt bằng.');
        return;
    }

    await loadFloors(id.trim());
});

document.getElementById('renameFloorBtn').addEventListener('click', async () => {
    if (!window.currentFloor) return;
    const current = window.currentFloorData?.name || window.currentFloor;
    const name = prompt(`Tên hiển thị mới cho mặt bằng ${window.currentFloor}:`, current);
    if (!name || !name.trim()) return;

    await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: window.currentFloor, name: name.trim() })
    });
    await loadFloors(window.currentFloor);
});

deleteFloorBtn.addEventListener('click', async () => {
    if (!window.currentFloor) return;
    // Được phép xoá mọi mặt bằng (kể cả B1). Khi xoá mặt bằng cuối cùng, server tự tạo "Mặt bằng mặc định".
    const isPanelFloor = !!window.currentFloorData?.kind && window.currentFloorData.kind !== 'Normal';
    const isLast = !isPanelFloor && window.floorList.filter(f => !f.kind || f.kind === 'Normal').length <= 1;
    const warn = 'Toàn bộ dữ liệu của mặt bằng này sẽ bị xoá vĩnh viễn: đối tượng trên bản đồ, ô đỗ và lịch sử đỗ xe, '
        + 'vị trí nhận diện của camera (camera được giữ lại nhưng bị gỡ khỏi mặt bằng), ảnh nền. Không thể hoàn tác.';
    const msg = isLast
        ? `Xoá mặt bằng "${window.currentFloor}"?\n\n${warn}\n\nĐây là mặt bằng cuối cùng nên hệ thống sẽ tự tạo lại "Mặt bằng mặc định".`
        : `Xoá mặt bằng "${window.currentFloor}"?\n\n${warn}`;
    if (!confirm(msg)) return;

    const res = await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}`, { method: 'DELETE' });
    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không xoá được mặt bằng.');
        return;
    }
    await loadFloors();
});

// Vào từ mục con của menu trái: /?floor=MÃ -> mở đúng mặt bằng đó (không có thì dùng mặt bằng khởi động)
const urlFloor = new URLSearchParams(location.search).get('floor');
if (urlFloor) history.replaceState(null, '', location.pathname);   // bỏ ?floor khỏi thanh địa chỉ để tải lại không bị ghim
// Ưu tiên: mặt bằng ghim trên địa chỉ (menu trái) > mặt bằng đã chọn lần trước trong phiên chạy > mặt bằng khởi động
(async () => { loadFloors(urlFloor || (await fetchSelectedFloor()) || undefined); })();
