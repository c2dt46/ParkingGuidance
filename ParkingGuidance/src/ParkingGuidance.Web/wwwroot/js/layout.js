let CURRENT_FLOOR = 'B1';
let editMode = true; // false khi o che do "Chay" (xem, khong chinh sua duoc)

const canvas = document.getElementById('layoutCanvas');
const viewport = document.getElementById('layoutViewport');
const propsPanel = document.getElementById('layoutProps');
const propsDialog = document.getElementById('propsDialog');
const EMPTY_PROPS_HTML = '<p class="no-result">Nhấp vào 1 đối tượng trên bản đồ để xem thuộc tính.</p>';
let propsOpen = false;       // hop thoai thuoc tinh dang hien
let floorZoneCount = 0;      // so Zone cua tang dang xem: chi hien nhan Zone tren ban do khi > 1

let elements = [];   // Dữ liệu các đối tượng layout (đã merge trạng thái ô đỗ sống)
let selectedId = null;

// Xem truoc truc tiep thuoc tinh (chua bam Luu):
//  live    = ngu canh cua panel thuoc tinh dang mo {el, applyForm, timer}
//  pending = ban chup luc chon doi tuong {id, snapshot, dirty}; chon doi tuong khac ma chua Luu thi hoan nguyen
let live = null;
let pending = null;
const cloneObj = (o) => JSON.parse(JSON.stringify(o));

const SLOT_STATUS_LABEL = { Empty: 'Còn trống', Occupied: 'Đã đỗ', Error: 'Lỗi' };
const SLOT_STATUS_CLASS = { Empty: 'empty', Occupied: 'occupied', Error: 'error' };

// ---------------------------------------------------------------------------
// Theo doi mat bang dang chon (dieu phoi boi floors.js)
// ---------------------------------------------------------------------------
window.addEventListener('floorchange', (e) => {
    CURRENT_FLOOR = e.detail;
    applyFloorBackground(window.currentFloorData);
    selectedId = null;
    if (live) { clearTimeout(live.timer); live = null; }
    flushPending();
    propsPanel.innerHTML = EMPTY_PROPS_HTML;
    loadLayout();
});

// Ty le khung ban do (rong/cao). GIONG NHAU o Build va Chay (xem --ar trong site.css) nen vi tri
// theo % + kich thuoc chu (theo --px) luon dong dang. Co anh nen thi lay theo ty le anh de cac doi
// tuong khop dung mat bang; khong co anh thi dung 16:9.
const DEFAULT_ASPECT = 16 / 9;
let bgAspectToken = 0;

function setCanvasAspect(ratio) {
    const r = Number.isFinite(ratio) && ratio > 0 ? Math.min(4, Math.max(0.5, ratio)) : DEFAULT_ASPECT;
    canvas.style.setProperty('--ar', r.toFixed(4));
    updateCanvasFit();
}

// ---------------------------------------------------------------------------
// Zoom rieng cho vung thiet ke (khong anh huong toan trang) + thanh cuon
// Kich thuoc canvas = kich thuoc "vua khung" x Zoom, luon giu ty le --ar.
// Vi tri doi tuong tinh theo %, chu/vien theo --px nen moi thu phong to dong dang.
// ---------------------------------------------------------------------------
const ZOOM_STEPS = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];
const zoomSelect = document.getElementById('zoomSelect');
let zoom = 1;

function updateCanvasFit() {
    const ar = parseFloat(canvas.style.getPropertyValue('--ar')) || DEFAULT_ASPECT;
    // Viewport luon hien thanh cuon (overflow: scroll) nen clientWidth/Height on dinh, khong bi nhay khi Zoom
    const w = viewport.clientWidth, h = viewport.clientHeight;
    if (!w || !h) return;   // dang an (che do Chay)
    const pad = 2;
    const fitW = Math.max(120, Math.min(w - pad, (h - pad) * ar));
    viewport.style.setProperty('--fit-w', `${fitW.toFixed(1)}px`);
}

function setZoom(z, anchor) {
    z = Math.min(ZOOM_STEPS[ZOOM_STEPS.length - 1], Math.max(ZOOM_STEPS[0], z));
    if (z === zoom) return;
    // Giu nguyen diem dang nhin (tam khung, hoac vi tri con tro khi Ctrl+lan chuot)
    const rect = viewport.getBoundingClientRect();
    const ax = anchor ? anchor.x - rect.left : viewport.clientWidth / 2;
    const ay = anchor ? anchor.y - rect.top : viewport.clientHeight / 2;
    const fx = (viewport.scrollLeft + ax) / viewport.scrollWidth;
    const fy = (viewport.scrollTop + ay) / viewport.scrollHeight;

    zoom = z;
    viewport.style.setProperty('--zoom', String(z));
    syncZoomSelect();

    viewport.scrollLeft = fx * viewport.scrollWidth - ax;
    viewport.scrollTop = fy * viewport.scrollHeight - ay;
}

function syncZoomSelect() {
    if (![...zoomSelect.options].some(o => parseFloat(o.value) === zoom)) {
        const o = document.createElement('option');
        o.value = String(zoom); o.textContent = `${Math.round(zoom * 100)}%`;
        zoomSelect.appendChild(o);
    }
    zoomSelect.value = String(zoom);
}

function stepZoom(dir, anchor) {
    let next;
    if (dir > 0) next = ZOOM_STEPS.find(v => v > zoom + 1e-6) ?? zoom;
    else next = [...ZOOM_STEPS].reverse().find(v => v < zoom - 1e-6) ?? zoom;
    setZoom(next, anchor);
}

zoomSelect.addEventListener('change', () => setZoom(parseFloat(zoomSelect.value)));
document.getElementById('zoomInBtn').addEventListener('click', () => stepZoom(1));
document.getElementById('zoomOutBtn').addEventListener('click', () => stepZoom(-1));

// Ctrl + lan chuot de Zoom tai vi tri con tro (chi o che do Build)
viewport.addEventListener('wheel', (e) => {
    if (!editMode || !e.ctrlKey) return;
    e.preventDefault();
    stepZoom(e.deltaY < 0 ? 1 : -1, { x: e.clientX, y: e.clientY });
}, { passive: false });

new ResizeObserver(updateCanvasFit).observe(viewport);
window.addEventListener('resize', updateCanvasFit);

function applyFloorBackground(floor) {
    const token = ++bgAspectToken;
    if (floor?.backgroundImageUrl) {
        canvas.style.backgroundImage = `url('${floor.backgroundImageUrl}')`;
        canvas.style.backgroundSize = 'contain';
        canvas.style.backgroundPosition = 'center';
        canvas.style.backgroundRepeat = 'no-repeat';

        const probe = new Image();
        probe.onload = () => { if (token === bgAspectToken) setCanvasAspect(probe.naturalWidth / probe.naturalHeight); };
        probe.onerror = () => { if (token === bgAspectToken) setCanvasAspect(DEFAULT_ASPECT); };
        probe.src = floor.backgroundImageUrl;
    } else {
        canvas.style.backgroundImage = '';
        canvas.style.backgroundSize = '';
        canvas.style.backgroundPosition = '';
        canvas.style.backgroundRepeat = '';
        setCanvasAspect(DEFAULT_ASPECT);
    }
}

// ---------------------------------------------------------------------------
// Tải & vẽ layout
// ---------------------------------------------------------------------------
async function loadLayout() {
    releaseAllMomentary();
    flushPending();
    await lastFlush;     // luu xong thay doi dang do roi moi tai lai tu server
    const res = await fetch(`/api/layout?floor=${encodeURIComponent(CURRENT_FLOOR)}`);
    elements = await res.json();
    await refreshZoneCount();
    if (live) { clearTimeout(live.timer); live = null; }
    pending = null;
    renderLayout();
    syncDisplayBar();
    if (selectedId && elements.some(x => x.id === selectedId)) selectElement(selectedId);
    else if (propsOpen) { selectedId = null; propsPanel.innerHTML = EMPTY_PROPS_HTML; }
}

// So Zone khac nhau cua tang dang xem (tu cac vi tri nhan dien da ve cho camera): tang chi co 1 Zone thi khong can hien Zone
async function refreshZoneCount() {
    try {
        const r = await fetch(`/api/layout/zones?floor=${encodeURIComponent(CURRENT_FLOOR)}`);
        const zs = r.ok ? await r.json() : [];
        floorZoneCount = new Set(zs.map(z => z.zone)).size;
    } catch { /* loi mang: giu gia tri cu */ }
}

function renderLayout() {
    canvas.innerHTML = '';
    elements.forEach(el => canvas.appendChild(buildElementNode(el)));
}

function buildElementNode(el) {
    const div = document.createElement('div');
    div.className = `layout-el layout-el-${el.type.toLowerCase()}`;
    div.style.left = `${el.x}%`;
    div.style.top = `${el.y}%`;
    div.style.width = `${el.width}%`;
    div.style.height = `${el.height}%`;
    div.dataset.id = el.id;

    switch (el.type) {
        case 'Button':
            div.classList.add('as-button');
            if (el.targetFloorId) div.classList.add('has-action');
            div.textContent = el.label || 'Nút ấn';
            break;

        case 'Image':
            if (el.imageUrl) {
                div.innerHTML = `<img src="${escHtml(el.imageUrl)}" alt="" />`;
            } else {
                div.innerHTML = `<span class="layout-el-placeholder">🖼️ Chưa có ảnh</span>`;
            }
            break;

        case 'Text':
            div.classList.add('as-text');
            div.textContent = el.content || 'Text';
            break;

        case 'Rectangle':
        case 'Circle':
            div.classList.add('as-shape');
            div.textContent = el.content || '';
            break;

        case 'Line':
            div.classList.add('as-line');   // net ve (va mui ten) duoc tao trong applyCommonStyle -> renderLineStroke
            break;

        case 'ParkingSlot':
            renderSlotNode(div, el);
            break;

        case 'SlotStats':
            renderStatsNode(div, el);
            break;

        case 'BitLampSwitch':
            renderBitNode(div, el);
            div.addEventListener('touchstart', onBitTouchStart, { passive: false });
            break;
    }

    applyCommonStyle(div, el);
    div.addEventListener('mousedown', onElementMouseDown);
    div.addEventListener('dblclick', onElementDblClick);
    return div;
}

// ---------------------------------------------------------------------------
// Vung nhan dien (o do) tren ban do: trang thai (mau + chu), bien so dang do, gio vao,
// bien xe ra gan nhat, gio ra — moi thuoc tinh bat/tat rieng (che do Build)
// ---------------------------------------------------------------------------
function escHtml(v) {
    return String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fmtDateTime(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '—';
    const pad = n => String(n).padStart(2, '0');
    return `${pad(d.getHours())}:${pad(d.getMinutes())} ${pad(d.getDate())}/${pad(d.getMonth() + 1)}`;
}

// So thu tu hien thi cua vi tri (1, 2, 3...): lay tu o do, neu thieu thi tu khoa "B1-2-5" -> "5"
function slotNumberOf(el) {
    return el.slot?.number || (String(el.slotId || '').match(/(\d+)\s*$/) || [])[1] || el.slotId || '';
}

// Zone cua vi tri: lay tu o do, neu thieu thi tu khoa "B1-2-5" -> "2" (khoa kieu cu khong co Zone -> '')
function slotZoneOf(el) {
    if (el.slot?.zone) return String(el.slot.zone);
    const m = String(el.slotId || '').match(/-(\d+)-\d+\s*$/);
    return m ? m[1] : '';
}

// Nhan hien thi tren ban do: "Z2·5" = Zone 2, vi tri 5 (so vi tri chi duy nhat trong 1 Zone nen phai kem Zone)
// Chi hien Zone khi tang co tu 2 Zone tro len; tang chi co 1 Zone thi chi hien so vi tri.
function slotLabelOf(el) {
    const z = slotZoneOf(el), n = slotNumberOf(el);
    return (z && floorZoneCount > 1) ? `Z${z}·${n}` : n;
}

function fmtPlate(p) {
    if (!p) return '—';
    return p === 'UNKNOWN' ? 'Không rõ' : p;
}

// (Re)ve noi dung + mau cua 1 doi tuong loai ParkingSlot, giu nguyen trang thai "selected".
function renderSlotNode(div, el) {
    const slot = el.slot ?? { status: 'Empty' };
    const status = slot.status ?? 'Empty';
    const wasSelected = div.classList.contains('selected');

    div.className = 'layout-el layout-el-parkingslot as-slot';
    div.classList.add(el.showStatusColor === false ? 'neutral' : SLOT_STATUS_CLASS[status]);
    if (wasSelected) div.classList.add('selected');

    const row = (k, v) => `<div class="slot-row"><span class="k">${k}</span><span class="v">${escHtml(v)}</span></div>`;
    let html = `<div class="slot-id" title="${floorZoneCount > 1 ? 'Zone ' + escHtml(slotZoneOf(el) || '—') + ' · ' : ''}vị trí ${escHtml(slotNumberOf(el))}">${escHtml(slotLabelOf(el))}</div>`;
    if (el.showStatusText !== false) html += `<div class="slot-status-tag">${SLOT_STATUS_LABEL[status] ?? status}</div>`;

    // Dang Loi (camera mat ket noi): bien/gio vao la cua xe do truoc khi loi, van hien de biet xe nao dang o do
    const hasVehicle = (status === 'Occupied' || status === 'Error') && slot.plateNumber;
    if (el.showPlate !== false) html += row('Biển', hasVehicle ? fmtPlate(slot.plateNumber) : '—');
    if (el.showEntryTime !== false) html += row('Vào', hasVehicle ? fmtDateTime(slot.parkedAt) : '—');
    if (el.showLastExitPlate !== false) html += row('Ra gần nhất', fmtPlate(slot.lastExitPlate));
    if (el.showExitTime !== false) html += row('Giờ ra', fmtDateTime(slot.lastExitTime));

    div.innerHTML = html;
    div.title = status === 'Error' ? (slot.errorMessage || 'Lỗi camera') : '';
    applyCommonStyle(div, el);
}

// ---------------------------------------------------------------------------
// Doi tuong "Thong ke cho": so cho trong / so cho co xe / so loi khong lay duoc anh cua tang
// ---------------------------------------------------------------------------
function renderStatsNode(div, el) {
    const st = el.stats || { free: 0, occupied: 0, error: 0, camerasError: 0 };
    const wasSelected = div.classList.contains('selected');
    div.className = 'layout-el layout-el-slotstats as-stats';
    if (wasSelected) div.classList.add('selected');

    const item = (cls, num, label, sub) =>
        `<div class="stat-item ${cls}"><span class="stat-num">${num}</span><span class="stat-label">${label}</span>${sub ? `<span class="stat-sub">${sub}</span>` : ''}</div>`;

    let html = '';
    if (el.showStatFree !== false) html += item('stat-free', st.free, 'Trống');
    if (el.showStatOccupied !== false) html += item('stat-occupied', st.occupied, 'Có xe');
    if (el.showStatError !== false)
        html += item('stat-error', st.error, 'Lỗi ảnh', st.camerasError ? `${st.camerasError} camera` : '');

    div.innerHTML = html || '<span class="layout-el-placeholder">Chưa chọn số liệu</span>';
    div.title = 'Thống kê chỗ đỗ của tầng ' + CURRENT_FLOOR +
        (st.error ? ` — ${st.error} ô đang lỗi do ${st.camerasError} camera không lấy được ảnh` : '');
    applyCommonStyle(div, el);
}

// ---------------------------------------------------------------------------
// Den bit / Cong tac bit (Bit Lamp / Toggle Switch): doc 1 tag dang bit (System tag hoac Project tag Boolean),
// hien hinh theo trang thai 0/1; o che do Chay, cong tac ghi gia tri ve Project tag.
// ---------------------------------------------------------------------------
let bitStates = {};            // ten tag (chu thuong) -> 0/1; thieu = tag khong ton tai / khong phai Boolean
const bitLocal = {};           // ten tag -> { v, until }: gia tri vua ghi, giu tam de tranh nhap nhay khi lan doc cu ve muon
const BIT_POLL_MS = 200;
const heldMomentary = new Map();   // id -> el: cong tac Momentary dang duoc giu
let bitWriteQueue = Promise.resolve();   // ghi tuan tu de lenh "nha" (OFF) khong bao gio toi truoc lenh "nhan" (ON)

const bitKey = (name) => String(name || '').toLowerCase();
const bitModeOf = (el) => el.bitMode === 'Lamp' ? 'Lamp' : 'Switch';
const bitStyleOf = (el) => ['SetOn', 'SetOff', 'Toggle', 'Momentary'].includes(el.switchStyle) ? el.switchStyle : 'Toggle';
const bitWriteTagOf = (el) => el.separateWriteTag ? el.writeTag : el.readTag;

// Trang thai hien thi (da tinh "Dao tin hieu"): 1 / 0; null = chua chon tag hoac khong doc duoc
function bitLogical(el) {
    if (!el.readTag) return null;
    const raw = bitStates[bitKey(el.readTag)];
    if (raw === undefined) return null;
    return (raw ? 1 : 0) ^ (el.invertSignal ? 1 : 0);
}

function renderBitNode(div, el) {
    const st = bitLogical(el);
    const mode = bitModeOf(el);
    const img = st === 1 ? el.stateImage1 : el.stateImage0;
    const text = st === 1 ? el.stateText1 : el.stateText0;

    let face;
    if (img) face = `<img class="bit-img" src="${escHtml(img)}" alt="" draggable="false" />`;
    else if (mode === 'Lamp') face = '<span class="bit-lamp-dot"></span>';
    else face = '<span class="bit-switch-track"><span class="bit-switch-knob"></span></span>';

    div.classList.add('as-bit');
    div.classList.toggle('bit-lamp', mode === 'Lamp');
    div.classList.toggle('bit-switch', mode === 'Switch');
    div.classList.toggle('bit-on', st === 1);
    div.classList.toggle('bit-off', st === 0);
    div.classList.toggle('bit-unknown', st === null);
    div.classList.toggle('has-action', mode === 'Switch');
    div.innerHTML = `<div class="bit-face">${face}</div>${text ? `<div class="bit-text">${escHtml(text)}</div>` : ''}`;
    div.dataset.bitSig = String(st);

    const tagInfo = !el.readTag ? 'Chưa chọn tag'
        : st === null ? `Tag "${el.readTag}" không tồn tại hoặc không phải Boolean`
        : `${el.readTag} = ${bitStates[bitKey(el.readTag)]}${el.invertSignal ? ' (đảo)' : ''}`;
    div.title = (el.comment ? el.comment + ' — ' : '') + tagInfo;
    applyCommonStyle(div, el);
}

// Chi ve lai doi tuong nao doi trang thai (khong thay node -> khong mat lua chon / keo-tha dang dien ra)
function updateBitNodes() {
    elements.forEach(el => {
        if (el.type !== 'BitLampSwitch') return;
        const node = canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
        if (!node || node.dataset.bitSig === String(bitLogical(el))) return;
        renderBitNode(node, el);
    });
}

async function pollBits() {
    try {
        if (!document.hidden) {
            const names = [...new Set(elements.filter(e => e.type === 'BitLampSwitch' && e.readTag).map(e => e.readTag))];
            if (names.length) {
                const next = {};
                for (let i = 0; i < names.length; i += 64) {     // may chu nhan toi da 64 ten moi lan
                    const r = await fetch(`/api/tasks/tags/bit-values?names=${encodeURIComponent(names.slice(i, i + 64).join(','))}`, { cache: 'no-store' });
                    if (!r.ok) throw new Error('bit-values ' + r.status);
                    const data = await r.json();
                    Object.entries(data.values || {}).forEach(([k, v]) => { next[bitKey(k)] = v ? 1 : 0; });
                }
                const now = Date.now();
                Object.entries(bitLocal).forEach(([k, l]) => {
                    if (l.until > now) next[k] = l.v; else delete bitLocal[k];
                });
                bitStates = next;
                updateBitNodes();
            }
        }
    } catch { /* mat ket noi tam thoi: giu trang thai cu, lan sau thu lai */ }
    setTimeout(pollBits, BIT_POLL_MS);
}

function sendBit(tag, raw) {
    return fetch(`/api/tasks/tags/project/${encodeURIComponent(tag)}/value`, {
        method: 'PUT', keepalive: true,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: String(raw) })
    });
}

function bitFlashError(id, msg) {
    console.warn('Bit switch:', msg);
    const node = canvas.querySelector(`.layout-el[data-id="${id}"]`);
    if (!node) return;
    node.classList.add('bit-error');
    node.title = msg;
    setTimeout(() => node.classList.remove('bit-error'), 1500);
}

// logical = trang thai mong muon (1 = ON). Gia tri ghi that = logical XOR "Dao tin hieu".
function writeBit(el, logical) {
    const tag = bitWriteTagOf(el);
    if (!tag) { bitFlashError(el.id, 'Chưa chọn tag ghi'); return Promise.resolve(); }
    const raw = logical ^ (el.invertSignal ? 1 : 0);
    const k = bitKey(tag);
    bitStates[k] = raw;                                   // hien ngay, khong cho vong doc ke tiep
    bitLocal[k] = { v: raw, until: Date.now() + 800 };
    updateBitNodes();

    bitWriteQueue = bitWriteQueue.then(async () => {
        try {
            const r = await sendBit(tag, raw);
            if (!r.ok) {
                const err = await r.json().catch(() => ({}));
                delete bitLocal[k];
                bitFlashError(el.id, err.message || `Không ghi được tag ${tag}`);
                return;
            }
            bitLocal[k] = { v: raw, until: Date.now() + 300 };
        } catch {
            delete bitLocal[k];
            bitFlashError(el.id, `Không ghi được tag ${tag} (mất kết nối)`);
        }
    });
    return bitWriteQueue;
}

function releaseMomentary(id) {
    const el = heldMomentary.get(id);
    if (!el) return;
    heldMomentary.delete(id);
    writeBit(el, 0);
}
function releaseAllMomentary() { [...heldMomentary.keys()].forEach(releaseMomentary); }

// Nhan cong tac o che do Chay. kind: 'mouse' | 'touch'
function onBitPress(el, div, kind) {
    if (bitModeOf(el) !== 'Switch') return;
    const style = bitStyleOf(el);
    const upEvents = kind === 'touch' ? ['touchend', 'touchcancel'] : ['mouseup'];
    const onRelease = (fn) => {
        const h = (ev) => { upEvents.forEach(n => document.removeEventListener(n, h)); fn(ev); };
        upEvents.forEach(n => document.addEventListener(n, h));
    };
    const insideRelease = (ev) => ev.type !== 'touchcancel' && (kind === 'touch' || div.contains(ev.target));
    const fire = () => {
        const cur = bitLogical(el);
        writeBit(el, style === 'SetOn' ? 1 : style === 'SetOff' ? 0 : (cur === 1 ? 0 : 1));
    };

    div.classList.add('bit-pressed');
    if (style === 'Momentary') {
        heldMomentary.set(el.id, el);
        writeBit(el, 1);
        onRelease(() => { div.classList.remove('bit-pressed'); releaseMomentary(el.id); });
    } else if (el.writeOnRelease) {
        onRelease((ev) => { div.classList.remove('bit-pressed'); if (insideRelease(ev)) fire(); });
    } else {
        fire();
        onRelease(() => div.classList.remove('bit-pressed'));
    }
}

function onBitTouchStart(e) {
    if (editMode) return;                         // che do Build: giu nguyen hanh vi cu
    const div = e.currentTarget;
    const el = elements.find(x => x.id === div.dataset.id);
    if (!el || bitModeOf(el) !== 'Switch') return;
    e.preventDefault();                           // chan su kien chuot gia lap sau cham (tranh ghi 2 lan)
    onBitPress(el, div, 'touch');
}

// Mat tieu diem / an tab / dong trang khi dang giu Momentary -> tu nha ve OFF, khong de ket o ON
window.addEventListener('blur', releaseAllMomentary);
document.addEventListener('visibilitychange', () => { if (document.hidden) releaseAllMomentary(); });
window.addEventListener('pagehide', () => {
    heldMomentary.forEach(el => { const t = bitWriteTagOf(el); if (t) sendBit(t, el.invertSignal ? 1 : 0).catch(() => {}); });
    heldMomentary.clear();
});

// Cap nhat TAI CHO du lieu trang thai cac vung (khong ve lai ca ban do, khong lam mat lua chon/keo-tha dang dien ra)
async function refreshLayoutSlots() {
    if (drag) return;
    try {
        const res = await fetch(`/api/layout?floor=${encodeURIComponent(CURRENT_FLOOR)}`);
        if (!res.ok) return;
        const fresh = await res.json();
        fresh.forEach(f => {
            if (f.type !== 'ParkingSlot' && f.type !== 'SlotStats') return;
            const local = elements.find(x => x.id === f.id);
            const node = canvas.querySelector(`.layout-el[data-id="${f.id}"]`);
            if (!local || !node) return;
            if (f.type === 'SlotStats') { local.stats = f.stats; renderStatsNode(node, local); return; }
            local.slot = f.slot;
            renderSlotNode(node, local);
        });
    } catch { /* mat ket noi tam thoi: lan lam moi sau se thu lai */ }
}
setInterval(refreshLayoutSlots, 5000);

// Áp dụng thuộc tính kiểu dáng chung (màu nền, viền) lên MỌI loại đối tượng.
// Giá trị null = không tuỳ chỉnh, giữ nguyên mặc định theo loại (CSS class) — ví dụ ô đỗ xe
// vẫn giữ màu theo trạng thái Chưa đỗ/Đã đỗ/Lỗi trừ khi người dùng chủ động đổi.
function hexToRgba(hex, alphaPct) {
    const m = /^#([0-9a-f]{6})$/i.exec(hex || '');
    if (!m) return hex;                       // vd "transparent"
    const n = parseInt(m[1], 16);
    return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${Math.min(100, Math.max(0, alphaPct)) / 100})`;
}

// Do mo chung, goc xoay, lop (z-index): ap dung cho moi loai doi tuong (null/0 = mac dinh)
function applyTransformStyle(div, el) {
    div.style.opacity = el.opacity === null || el.opacity === undefined ? '' : String(el.opacity / 100);
    div.style.transform = el.rotation ? `rotate(${Number(el.rotation)}deg)` : '';
    div.style.zIndex = el.zIndex ? String(el.zIndex) : '';
}

// Duong thang: net ve = 1 duong vien tren (mau/do day/kieu net lay tu borderColor/borderWidth/borderStyle),
// tuy chon mui ten o 2 dau. Dung DOM + style property (khong ghep chuoi HTML) nen an toan voi du lieu tuy y.
function renderLineStroke(div, el) {
    const w = Math.max(0.5, Number(el.borderWidth ?? 3));
    const color = el.borderColor || '#e6ebf5';
    const dash = el.borderStyle && el.borderStyle !== 'none' ? el.borderStyle : 'solid';
    const arrow = el.lineArrow || 'none';
    const hasStart = arrow === 'start' || arrow === 'both';
    const hasEnd = arrow === 'end' || arrow === 'both';
    const headHalf = `max(5px, calc(${w * 3} * var(--px)))`;     // nua do rong day mui ten
    const headLen = `max(8px, calc(${w * 4.5} * var(--px)))`;    // chieu dai mui ten

    div.querySelectorAll('.line-stroke, .line-head').forEach(n => n.remove());

    const stroke = document.createElement('span');
    stroke.className = 'line-stroke';
    stroke.style.left = hasStart ? headLen : '0px';     // net ngan lai de khong nho ra khoi dau mui ten
    stroke.style.right = hasEnd ? headLen : '0px';
    stroke.style.borderTop = `max(1px, calc(${w} * var(--px))) ${dash} ${color}`;
    div.appendChild(stroke);

    const head = (side) => {
        const h = document.createElement('span');
        h.className = `line-head ${side}`;
        h.style.borderTop = `${headHalf} solid transparent`;
        h.style.borderBottom = `${headHalf} solid transparent`;
        if (side === 'end') h.style.borderLeft = `${headLen} solid ${color}`;
        else h.style.borderRight = `${headLen} solid ${color}`;
        div.appendChild(h);
    };
    if (hasStart) head('start');
    if (hasEnd) head('end');
}

function applyCommonStyle(div, el) {
    applyTransformStyle(div, el);
    if (el.type === 'Line') { renderLineStroke(div, el); return; }   // duong thang khong co nen/vien hop

    if (el.backgroundColor) {
        const fo = el.fillOpacity;
        div.style.backgroundColor = (fo !== null && fo !== undefined && fo < 100)
            ? hexToRgba(el.backgroundColor, fo) : el.backgroundColor;
    }

    if (el.borderWidth !== null && el.borderWidth !== undefined) {
        const w = Number(el.borderWidth);
        // do day vien theo thang do cua khung (--px), toi thieu 1px de khong bien mat khi khung nho
        div.style.borderWidth = w > 0 ? `max(1px, calc(${w} * var(--px)))` : '0px';
        div.style.borderStyle = w > 0 ? (el.borderStyle || 'solid') : 'none';
        if (el.borderColor) div.style.borderColor = el.borderColor;
    }

    // Bo goc (hinh chu nhat), mau chu / co chu ben trong hinh — theo thang do cua khung (--px)
    if (el.type === 'Rectangle' && el.cornerRadius !== null && el.cornerRadius !== undefined)
        div.style.borderRadius = `calc(${Number(el.cornerRadius)} * var(--px))`;
    if (el.textColor) div.style.color = el.textColor;
    if (el.fontSize) div.style.fontSize = `calc(${Number(el.fontSize)} * var(--px))`;
}

// ---------------------------------------------------------------------------
// Thêm đối tượng mới
// ---------------------------------------------------------------------------
document.querySelectorAll('.layout-toolbar [data-add]').forEach(btn => {
    btn.addEventListener('click', () => addElement(btn.dataset.add));
});

// Ty le rong/cao cua khung ban do (de ve hinh tron deu)
function canvasAspect() {
    return canvas.clientWidth && canvas.clientHeight ? canvas.clientWidth / canvas.clientHeight : 16 / 9;
}

async function addElement(type) {
    if (type === 'ParkingSlot') { openZonePicker(); return; } // vung nhan dien: chon tu danh sach vung da ve
    const payload = {
        type,
        floor: CURRENT_FLOOR,
        x: 5 + Math.round(Math.random() * 55),
        y: 5 + Math.round(Math.random() * 45),
        width: type === 'SlotStats' ? 26 : 15,
        height: type === 'SlotStats' ? 14 : 9,
    };

    const ar = canvasAspect();
    if (type === 'Rectangle' || type === 'Circle') {
        Object.assign(payload, {
            backgroundColor: '#3b82f6', fillOpacity: 20,
            borderColor: '#3b82f6', borderStyle: 'solid', borderWidth: 2
        });
        if (type === 'Rectangle') { payload.width = 16; payload.height = 12; payload.cornerRadius = 0; }
        else { payload.width = 9; payload.height = Math.round(9 * ar * 10) / 10; }   // tron deu (cao % = rong % x ty le khung)
    }
    if (type === 'Line') {
        Object.assign(payload, {
            width: 20, height: 3,
            borderColor: '#e6ebf5', borderStyle: 'solid', borderWidth: 3, lineArrow: 'none'
        });
    }

    if (type === 'BitLampSwitch') {
        Object.assign(payload, {
            width: 6, height: Math.min(100, Math.round(6 * ar * 10) / 10),   // o vuong theo ty le khung
            bitMode: 'Switch', switchStyle: 'Toggle', stateText0: 'OFF', stateText1: 'ON'
        });
    }

    if (type === 'Button') payload.label = 'Nút mới';
    if (type === 'Text') payload.content = 'Nội dung text';
    if (type === 'Image') payload.imageUrl = '';

    const res = await fetch('/api/layout', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không thêm được đối tượng.');
        return;
    }

    await loadLayout();
}

// ---------------------------------------------------------------------------
// Cong cu "Them vung nhan dien": chon cac vung da ve (cua camera tren tang nay) de dat len ban do
// ---------------------------------------------------------------------------
let zonePickData = [];

async function openZonePicker() {
    const modal = document.getElementById('zonePickModal');
    const list = document.getElementById('zonePickList');
    document.getElementById('zonePickError').textContent = '';
    list.innerHTML = '<div class="hint small">Đang tải...</div>';
    modal.style.display = 'flex';

    try {
        const res = await fetch(`/api/layout/zones?floor=${encodeURIComponent(CURRENT_FLOOR)}`);
        zonePickData = await res.json();
    } catch (err) {
        list.innerHTML = '<div class="no-result">Không tải được danh sách vùng.</div>';
        return;
    }

    if (!zonePickData.length) {
        list.innerHTML = `<div class="no-result">Tầng ${escHtml(CURRENT_FLOOR)} chưa có vị trí nhận diện nào.<br>
            Hãy vẽ vùng ở <a href="/System/Cameras">Hệ thống → Camera → Vị trí đỗ</a> (camera phải gán tầng ${escHtml(CURRENT_FLOOR)} và Zone).</div>`;
        return;
    }

    floorZoneCount = new Set(zonePickData.map(z => z.zone)).size;
    const multiZone = floorZoneCount > 1;
    list.innerHTML = zonePickData.map((z, i) => `
        <label class="zone-pick-item ${z.onLayout ? 'disabled' : ''}">
            <input type="checkbox" data-zi="${i}" ${z.onLayout ? 'disabled' : ''} />
            <strong>${multiZone ? 'Zone ' + escHtml(z.zone) + ' · vị trí' : 'Vị trí'} ${escHtml(z.number)}</strong>
            <span class="hint small" style="margin:0;">camera ${escHtml(z.cameraIp)}${z.onLayout ? ' · đã có trên bản đồ' : ''}</span>
        </label>`).join('');
}

function closeZonePicker() {
    document.getElementById('zonePickModal').style.display = 'none';
}

document.getElementById('zonePickCancel').addEventListener('click', closeZonePicker);
document.getElementById('zonePickAll').addEventListener('click', () => {
    document.querySelectorAll('#zonePickList input[type=checkbox]:not(:disabled)').forEach(c => { c.checked = true; });
});

document.getElementById('zonePickConfirm').addEventListener('click', async () => {
    const picked = [...document.querySelectorAll('#zonePickList input[type=checkbox]:checked')]
        .map(c => zonePickData[parseInt(c.dataset.zi, 10)]);
    const err = document.getElementById('zonePickError');
    if (!picked.length) { err.textContent = 'Chọn ít nhất 1 vị trí.'; return; }

    // Xep luoi de cac vung moi khong chong len nhau
    const already = elements.filter(e => e.type === 'ParkingSlot').length;
    const failed = [];
    for (let i = 0; i < picked.length; i++) {
        const n = already + i, w = 14, h = 18;
        const payload = {
            type: 'ParkingSlot', floor: CURRENT_FLOOR, zone: picked[i].zone, slotId: picked[i].number,
            x: Math.min(100 - w, 3 + (n % 6) * 15.5), y: Math.min(100 - h, 3 + Math.floor(n / 6) * 20),
            width: w, height: h
        };
        const res = await fetch('/api/layout', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
        });
        if (!res.ok) {
            const e = await res.json().catch(() => ({}));
            failed.push(`Zone ${picked[i].zone} · vị trí ${picked[i].number}: ${e.message || 'lỗi'}`);
        }
    }

    if (failed.length) { err.textContent = failed.join(' | '); }
    else closeZonePicker();
    await loadLayout();
});

// ---------------------------------------------------------------------------
// Kéo-thả + chọn đối tượng (chỉ hoạt động ở chế độ Build)
// ---------------------------------------------------------------------------
let drag = null;

function onElementMouseDown(e) {
    const div = e.currentTarget;

    // Che do "Chay": khong keo-tha/chinh sua; rieng Nut an co gan mat bang thi bam la chuyen mat bang.
    if (!editMode) {
        const target = elements.find(x => x.id === div.dataset.id);
        if (target?.type === 'Button' && target.targetFloorId) {
            e.preventDefault();
            navigateToFloor(target.targetFloorId);
        } else if (target?.type === 'BitLampSwitch' && e.button === 0) {
            e.preventDefault();
            onBitPress(target, div, 'mouse');
        }
        return;
    }

    const canvasRect = canvas.getBoundingClientRect();

    drag = {
        id: div.dataset.id,
        el: div,
        startX: e.clientX,
        startY: e.clientY,
        originLeftPct: parseFloat(div.style.left),
        originTopPct: parseFloat(div.style.top),
        widthPct: parseFloat(div.style.width),
        heightPct: parseFloat(div.style.height),
        moved: false,
        canvasRect
    };

    document.addEventListener('mousemove', onDragMove);
    document.addEventListener('mouseup', onDragEnd);
    e.preventDefault();
}

function onDragMove(e) {
    if (!drag) return;
    const dx = e.clientX - drag.startX;
    const dy = e.clientY - drag.startY;

    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) drag.moved = true;
    if (!drag.moved) return;

    const dxPct = (dx / drag.canvasRect.width) * 100;
    const dyPct = (dy / drag.canvasRect.height) * 100;

    let newLeftPct = drag.originLeftPct + dxPct;
    let newTopPct = drag.originTopPct + dyPct;

    newLeftPct = Math.max(0, Math.min(newLeftPct, 100 - drag.widthPct));
    newTopPct = Math.max(0, Math.min(newTopPct, 100 - drag.heightPct));

    drag.el.style.left = `${newLeftPct}%`;
    drag.el.style.top = `${newTopPct}%`;
}

async function onDragEnd() {
    document.removeEventListener('mousemove', onDragMove);
    document.removeEventListener('mouseup', onDragEnd);
    if (!drag) return;

    const el = elements.find(x => x.id === drag.id);
    if (drag.moved && el) {
        el.x = parseFloat(drag.el.style.left);
        el.y = parseFloat(drag.el.style.top);
        await persistElement(el);
    }

    selectElement(drag.id);
    drag = null;
}

// ---------------------------------------------------------------------------
// Hop thoai thuoc tinh doi tuong (noi, keo duoc):
//  - Chua mo: nhay dup vao doi tuong de mo hop thoai voi thuoc tinh cua doi tuong do.
//  - Dang mo: chi can nhap vao doi tuong khac, thuoc tinh cua doi tuong do se thay the noi dung hop thoai.
//  - Dong bang nut ✕ hoac phim Esc (thay doi chua Luu duoc tu dong luu).
// ---------------------------------------------------------------------------
const PROPS_DIALOG_POS_KEY = 'layoutPropsDialogPos2';   // v2: hop thoai ngang (vi tri cu cua hop thoai doc khong con hop)

function onElementDblClick(e) {
    if (!editMode) return;
    e.preventDefault();
    openPropsDialog(e.currentTarget.dataset.id);
}

function setPropsDialogPos(left, top) {
    const w = propsDialog.offsetWidth || 340, h = propsDialog.offsetHeight || 40;
    left = Math.min(Math.max(0, left), Math.max(0, window.innerWidth - w));
    top = Math.min(Math.max(0, top), Math.max(0, window.innerHeight - h));
    propsDialog.style.left = `${left}px`;
    propsDialog.style.top = `${top}px`;
}

function placePropsDialog() {
    if (propsDialog.dataset.placed) return;
    let pos = null;
    try { pos = JSON.parse(localStorage.getItem(PROPS_DIALOG_POS_KEY) || 'null'); } catch { /* khong co storage */ }
    // Mac dinh: giua man hinh, sat day (hop thoai ngang) de it che vung thiet ke; keo thanh tieu de de doi cho
    const w = propsDialog.offsetWidth || 340, h = propsDialog.offsetHeight || 200;
    setPropsDialogPos(pos ? pos.left : (window.innerWidth - w) / 2, pos ? pos.top : window.innerHeight - h - 24);
    propsDialog.dataset.placed = '1';
}

// Giu hop thoai nam tron trong man hinh sau khi noi dung (va chieu cao) thay doi
function fitPropsDialog() {
    if (!propsOpen || !propsDialog.dataset.placed) return;
    setPropsDialogPos(parseInt(propsDialog.style.left, 10) || 0, parseInt(propsDialog.style.top, 10) || 0);
}

function syncPropsToggle() {
    document.getElementById('propsToggleBtn')?.classList.toggle('active', propsOpen);
}

// id = null: mo hop thoai cho doi tuong dang chon (neu co), khong thi hien huong dan "nhap vao doi tuong"
function openPropsDialog(id) {
    if (!editMode) return;
    propsOpen = true;
    propsDialog.style.display = 'block';
    const target = id || selectedId;
    if (target && elements.some(x => x.id === target)) {
        selectElement(target);
    } else {
        propsPanel.innerHTML = EMPTY_PROPS_HTML;
        const t = document.getElementById('propsDialogTitleText');
        if (t) t.textContent = 'Thuộc tính đối tượng';
    }
    placePropsDialog();
    fitPropsDialog();
    syncPropsToggle();
}

function closePropsDialog() {
    if (live) { clearTimeout(live.timer); live = null; }
    flushPending();
    propsOpen = false;
    propsDialog.style.display = 'none';
    propsPanel.innerHTML = EMPTY_PROPS_HTML;
    syncPropsToggle();
}

// Nut tren thanh cong cu (ribbon): mo/dong hop thoai thuoc tinh; xoa doi tuong dang chon
document.getElementById('propsToggleBtn').addEventListener('click', () => {
    if (!editMode) return;
    if (propsOpen) closePropsDialog(); else openPropsDialog(null);
});
document.getElementById('deleteSelectedBtn').addEventListener('click', () => {
    if (!editMode) return;
    if (!selectedId) { alert('Chọn 1 đối tượng trên bản đồ trước.'); return; }
    deleteElement(selectedId);
});

document.getElementById('propsDialogClose').addEventListener('click', closePropsDialog);

document.getElementById('propsDialogTitle').addEventListener('mousedown', (e) => {
    if (e.target.closest('button')) return;
    const startX = e.clientX, startY = e.clientY;
    const rect = propsDialog.getBoundingClientRect();
    const move = (ev) => setPropsDialogPos(rect.left + ev.clientX - startX, rect.top + ev.clientY - startY);
    const up = () => {
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
        try {
            localStorage.setItem(PROPS_DIALOG_POS_KEY,
                JSON.stringify({ left: parseInt(propsDialog.style.left, 10), top: parseInt(propsDialog.style.top, 10) }));
        } catch { /* khong co storage */ }
    };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', up);
    e.preventDefault();
});

document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || !editMode || !propsOpen) return;
    if (document.getElementById('zonePickModal').style.display !== 'none') return;
    closePropsDialog();
});

window.addEventListener('resize', () => {
    if (propsOpen) setPropsDialogPos(parseInt(propsDialog.style.left, 10) || 0, parseInt(propsDialog.style.top, 10) || 0);
});

// ---------------------------------------------------------------------------
// Bảng thuộc tính (property panel)
// ---------------------------------------------------------------------------
function selectElement(id) {
    if (!editMode) return;

    if (live) { clearTimeout(live.timer); live = null; }
    if (pending && pending.id !== id) flushPending();   // chua bam Luu ma chon doi tuong khac -> tu dong luu

    selectedId = id;
    const el = elements.find(x => x.id === id);
    if (!el) return;
    if (!pending || pending.id !== id) pending = { id, snapshot: cloneObj(el), dirty: false };

    document.querySelectorAll('.layout-el').forEach(n => n.classList.toggle('selected', n.dataset.id === id));

    // Hop thoai thuoc tinh dang dong: chi chon (to vien), khong hien thuoc tinh. Nhay dup vao doi tuong de mo.
    if (!propsOpen) return;
    const titleText = document.getElementById('propsDialogTitleText');
    if (titleText) titleText.textContent = `Thuộc tính — ${typeLabel(el.type)}`;

    if (el.type === 'ParkingSlot') {
        renderSlotProps(el);
    } else if (el.type === 'SlotStats') {
        renderStatsProps(el);
    } else if (el.type === 'BitLampSwitch') {
        renderBitProps(el);
    } else if (el.type === 'Button') {
        renderButtonProps(el);
    } else if (el.type === 'Rectangle' || el.type === 'Circle') {
        renderShapeProps(el);
    } else if (el.type === 'Line') {
        renderLineProps(el);
    } else {
        renderGenericProps(el);
    }
    fitPropsDialog();
}

// Chuyen sang mat bang khac bang cach dung lai bo chon mat bang cua floors.js
// (dat gia tri roi phat su kien 'change' de floors.js tu cap nhat + phat 'floorchange').
function navigateToFloor(floorId) {
    const sel = document.getElementById('floorSelect');
    if (!sel) return;
    if ([...sel.options].some(o => o.value === floorId)) {
        sel.value = floorId;
        sel.dispatchEvent(new Event('change'));
    }
}

// ---------------------------------------------------------------------------
// Xem truoc truc tiep: sua thuoc tinh o panel -> doi tuong tren ban do doi theo ngay (chua can bam Luu).
// Nut Luu moi ghi xuong server; chon doi tuong khac ma chua Luu thi thay doi duoc hoan nguyen.
// ---------------------------------------------------------------------------
function refreshNode(el) {
    const old = canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
    if (!old) return;
    const fresh = buildElementNode(el);
    if (old.classList.contains('selected')) fresh.classList.add('selected');
    old.replaceWith(fresh);
}

// Roi khoi doi tuong (chon doi tuong khac / doi mat bang / chuyen che do Chay / dong trang) ma thuoc tinh da doi
// nhung chua bam Luu -> TU DONG luu xuong server, khong de mat thay doi.
let lastFlush = Promise.resolve();
function flushPending() {
    const p = pending;
    pending = null;
    if (!p || !p.dirty) return lastFlush;
    const el = elements.find(x => x.id === p.id);
    if (!el) return lastFlush;
    lastFlush = persistElement(el).catch(() => { /* loi mang: bo qua */ });
    return lastFlush;
}

function bindLivePreview(el, applyForm) {
    if (live) clearTimeout(live.timer);
    live = { el, applyForm, timer: null };
}

function onPropsEdit(e) {
    if (!live || !editMode) return;
    const t = e.target;
    // chon mau nen / bo "khong to nen" -> tu bat "Dung mau tuy chinh" de thay doi co hieu luc ngay
    if (t.id === 'propBgColor') {
        const en = document.getElementById('propBgColorEnabled'); if (en) en.checked = true;
        const nf = document.getElementById('propNoFill'); if (nf) nf.checked = false;
    }
    const ctx = live;
    clearTimeout(ctx.timer);
    ctx.timer = setTimeout(() => {
        if (live !== ctx) return;                 // da chon doi tuong khac
        try { ctx.applyForm(ctx.el); } catch (err) { console.error(err); return; }
        if (pending && pending.id === ctx.el.id) pending.dirty = true;
        refreshNode(ctx.el);
        const note = document.getElementById('propDirtyNote');
        if (note) note.hidden = false;
    }, 60);
}
propsPanel.addEventListener('input', onPropsEdit);
propsPanel.addEventListener('change', onPropsEdit);

const DIRTY_NOTE_HTML = '<p id="propDirtyNote" class="prop-dirty-note" hidden>● Đã thay đổi — sẽ tự lưu khi bạn chọn đối tượng khác (hoặc bấm 💾 Lưu ngay).</p>';

async function persistAndRefresh(el) {
    await persistElement(el);
    renderLayout();
    selectElement(el.id);
}

// ---------------------------------------------------------------------------
// Cac o nhap dung chung cho MOI loai doi tuong: vi tri, kich thuoc, mau sac, vien
// ---------------------------------------------------------------------------
function positionFieldsHtml(el, opts = {}) {
    const wLabel = opts.line ? 'Chiều dài' : 'Rộng';
    const hLabel = opts.line ? 'Vùng chọn (cao)' : 'Cao';
    return `
        <div class="prop-section-title">Vị trí &amp; kích thước (%)</div>
        <div class="prop-grid-2">
            <div class="prop-field"><label>X</label><input id="propX" type="number" step="0.1" value="${Number(el.x).toFixed(1)}" /></div>
            <div class="prop-field"><label>Y</label><input id="propY" type="number" step="0.1" value="${Number(el.y).toFixed(1)}" /></div>
            <div class="prop-field"><label>${wLabel}</label><input id="propWidth" type="number" step="0.1" min="1" value="${Number(el.width).toFixed(1)}" /></div>
            <div class="prop-field"><label>${hLabel}</label><input id="propHeight" type="number" step="0.1" min="1" value="${Number(el.height).toFixed(1)}" /></div>
        </div>
    `;
}

function applyPositionFieldsFromForm(el) {
    const num = (id, fallback) => {
        const v = parseFloat(document.getElementById(id).value);
        return Number.isFinite(v) ? v : fallback;
    };
    el.width = Math.min(100, Math.max(1, num('propWidth', el.width)));
    el.height = Math.min(100, Math.max(1, num('propHeight', el.height)));
    el.x = Math.min(100 - el.width, Math.max(0, num('propX', el.x)));
    el.y = Math.min(100 - el.height, Math.max(0, num('propY', el.y)));
}

function styleFieldsHtml(el, opts = {}) {
    const style = el.borderStyle || 'solid';
    const hasBg = !!el.backgroundColor && el.backgroundColor !== 'transparent';
    const shapeFill = opts.shape ? `
        <div class="prop-field">
            <label class="checkbox-label">
                <input type="checkbox" id="propNoFill" ${el.backgroundColor === 'transparent' ? 'checked' : ''} />
                Không tô nền (trong suốt)
            </label>
        </div>
        <div class="prop-field">
            <label>Độ đậm màu nền: <span id="propFillOpacityVal">${el.fillOpacity ?? 100}</span>%</label>
            <input id="propFillOpacity" type="range" min="0" max="100" step="5" value="${el.fillOpacity ?? 100}" />
        </div>` : '';
    return `
        <div class="prop-section-title">Màu sắc &amp; viền</div>
        <div class="prop-field">
            <label>Màu nền</label>
            <div class="prop-inline">
                <input id="propBgColor" type="color" value="${hasBg ? el.backgroundColor : '#1a2540'}" />
                <label class="checkbox-label">
                    <input type="checkbox" id="propBgColorEnabled" ${hasBg ? 'checked' : ''} />
                    Dùng màu tuỳ chỉnh
                </label>
            </div>
        </div>
        ${shapeFill}
        <div class="prop-field">
            <label>Loại đường viền</label>
            <select id="propBorderStyle">
                <option value="solid" ${style === 'solid' ? 'selected' : ''}>Liền nét</option>
                <option value="dashed" ${style === 'dashed' ? 'selected' : ''}>Nét đứt</option>
                <option value="dotted" ${style === 'dotted' ? 'selected' : ''}>Nét chấm</option>
                <option value="none" ${style === 'none' ? 'selected' : ''}>Không viền</option>
            </select>
        </div>
        <div class="prop-grid-2">
            <div class="prop-field">
                <label>Màu viền</label>
                <input id="propBorderColor" type="color" value="${el.borderColor || '#3b82f6'}" />
            </div>
            <div class="prop-field">
                <label>Độ dày viền (px)</label>
                <input id="propBorderWidth" type="number" min="0" max="20" step="1"
                       placeholder="mặc định" value="${el.borderWidth ?? ''}" />
            </div>
        </div>
        <p class="hint small">Để trống "Độ dày viền" = giữ kiểu viền mặc định của loại đối tượng.</p>
    `;
}

function applyStyleFieldsFromForm(el) {
    const bgEnabled = document.getElementById('propBgColorEnabled').checked;
    el.backgroundColor = bgEnabled ? document.getElementById('propBgColor').value : null;

    // Hinh chu nhat / tron: "Khong to nen" + do dam mau nen
    const noFill = document.getElementById('propNoFill');
    if (noFill) {
        if (noFill.checked) el.backgroundColor = 'transparent';
        const fo = parseFloat(document.getElementById('propFillOpacity').value);
        el.fillOpacity = Number.isFinite(fo) && fo < 100 ? Math.max(0, fo) : null;
    }

    const bw = document.getElementById('propBorderWidth').value;
    el.borderWidth = bw === '' ? null : Math.max(0, parseFloat(bw) || 0);
    el.borderStyle = document.getElementById('propBorderStyle').value;
    el.borderColor = document.getElementById('propBorderColor').value;
}

async function saveCommonAndRefresh(el) {
    applyPositionFieldsFromForm(el);
    applyStyleFieldsFromForm(el);
    await persistAndRefresh(el);
}

// ---------------------------------------------------------------------------
// Panel: Thong ke cho
// ---------------------------------------------------------------------------
function renderStatsProps(el) {
    const chk = (id, on, text) =>
        `<label class="checkbox-label" style="margin-bottom:6px;"><input type="checkbox" id="${id}" ${on === false ? '' : 'checked'} /> ${text}</label>`;
    const st = el.stats || { free: 0, occupied: 0, error: 0, camerasError: 0 };

    propsPanel.innerHTML = `
        <h3>Thống kê chỗ đỗ · tầng ${escHtml(CURRENT_FLOOR)}</h3>
        <p class="hint small" style="margin-top:0;">
            Hiện: <strong>${st.free}</strong> trống · <strong>${st.occupied}</strong> có xe · <strong>${st.error}</strong> lỗi
            (${st.camerasError} camera không lấy được ảnh). Tính trên các vùng nhận diện đã vẽ của tầng, cập nhật realtime.
        </p>

        <div class="prop-section-title">Số liệu hiển thị</div>
        ${chk('propStatFree', el.showStatFree, 'Số chỗ trống')}
        ${chk('propStatOcc', el.showStatOccupied, 'Số chỗ có xe')}
        ${chk('propStatErr', el.showStatError, 'Số lỗi không lấy được ảnh (camera mất kết nối)')}

        ${positionFieldsHtml(el)}
        ${styleFieldsHtml(el)}
        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;

    const applyForm = (el) => {
        el.showStatFree = document.getElementById('propStatFree').checked;
        el.showStatOccupied = document.getElementById('propStatOcc').checked;
        el.showStatError = document.getElementById('propStatErr').checked;
        applyPositionFieldsFromForm(el);
        applyStyleFieldsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });
    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Panel: Text / Hinh anh
// ---------------------------------------------------------------------------
function renderGenericProps(el) {
    let fieldHtml = '';
    if (el.type === 'Text') {
        fieldHtml = `<div class="prop-field"><label>Nội dung</label><textarea id="propContent" rows="3">${escHtml(el.content)}</textarea></div>`;
    } else if (el.type === 'Image') {
        fieldHtml = `<div class="prop-field"><label>URL hình ảnh</label><input id="propImageUrl" type="text" placeholder="https://..." value="${escHtml(el.imageUrl)}" /></div>`;
    }

    propsPanel.innerHTML = `
        <h3>${typeLabel(el.type)}</h3>
        ${fieldHtml}
        ${positionFieldsHtml(el)}
        ${styleFieldsHtml(el)}
        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;

    const applyForm = (el) => {
        if (el.type === 'Text') el.content = document.getElementById('propContent').value;
        if (el.type === 'Image') el.imageUrl = document.getElementById('propImageUrl').value;
        applyPositionFieldsFromForm(el);
        applyStyleFieldsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });

    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Xoay / do mo / lop — dung chung cho Hinh chu nhat, Hinh tron, Duong thang
// ---------------------------------------------------------------------------
function effectsFieldsHtml(el) {
    return `
        <div class="prop-section-title">Xoay, độ mờ &amp; lớp</div>
        <div class="prop-grid-2">
            <div class="prop-field"><label>Góc xoay (°)</label>
                <input id="propRotation" type="number" min="-360" max="360" step="1" value="${Number(el.rotation ?? 0)}" /></div>
            <div class="prop-field"><label>Độ mờ chung (%)</label>
                <input id="propOpacity" type="number" min="0" max="100" step="5" value="${el.opacity ?? 100}" /></div>
            <div class="prop-field"><label>Lớp (z-index)</label>
                <input id="propZIndex" type="number" step="1" value="${el.zIndex ?? 0}" /></div>
            <div class="prop-field"><label>Thứ tự lớp</label>
                <div class="prop-inline">
                    <button type="button" id="sendBackBtn" class="btn secondary" style="padding:6px;" title="Đưa ra sau cùng (dưới các đối tượng khác)">⬇ Sau</button>
                    <button type="button" id="bringFrontBtn" class="btn secondary" style="padding:6px;" title="Đưa lên trên cùng">⬆ Trước</button>
                </div></div>
        </div>
        <p class="hint small">Số lớp lớn nằm trên. Đưa hình xuống "Sau" để làm nền cho các ô đỗ / nút.</p>
    `;
}

function bindEffectsButtons() {
    const z = document.getElementById('propZIndex');
    document.getElementById('sendBackBtn').addEventListener('click', () => {
        z.value = Math.min(0, ...elements.map(e => e.zIndex || 0)) - 1;
    });
    document.getElementById('bringFrontBtn').addEventListener('click', () => {
        z.value = Math.max(0, ...elements.map(e => e.zIndex || 0)) + 1;
    });
}

function applyEffectsFromForm(el) {
    const rot = parseFloat(document.getElementById('propRotation').value);
    el.rotation = Number.isFinite(rot) && rot !== 0 ? Math.max(-360, Math.min(360, rot)) : null;

    const op = parseFloat(document.getElementById('propOpacity').value);
    el.opacity = Number.isFinite(op) && op < 100 ? Math.max(0, op) : null;

    const z = parseInt(document.getElementById('propZIndex').value, 10);
    el.zIndex = Number.isFinite(z) ? Math.max(-1000, Math.min(1000, z)) : 0;
}

function clampIntoCanvas(el) {
    el.x = Math.min(Math.max(0, el.x), 100 - el.width);
    el.y = Math.min(Math.max(0, el.y), 100 - el.height);
}

// ---------------------------------------------------------------------------
// Panel: Hinh chu nhat / Hinh tron
// ---------------------------------------------------------------------------
function renderShapeProps(el) {
    const isCircle = el.type === 'Circle';
    const ar = canvasAspect();
    const isRound = isCircle && Math.abs(el.height - el.width * ar) < 0.3;

    const cornerField = isCircle ? '' : `
        <div class="prop-field"><label>Bo góc (px)</label>
            <input id="propCorner" type="number" min="0" max="500" step="1" placeholder="vuông góc" value="${el.cornerRadius ?? ''}" /></div>`;
    const roundField = isCircle ? `
        <label class="checkbox-label" style="margin:2px 0 8px;">
            <input type="checkbox" id="propKeepRound" ${isRound ? 'checked' : ''} />
            Giữ hình tròn đều (tự tính chiều cao theo chiều rộng)
        </label>` : '';

    propsPanel.innerHTML = `
        <h3>${typeLabel(el.type)}</h3>

        <div class="prop-section-title">Chữ trong hình (tuỳ chọn)</div>
        <div class="prop-field"><label>Nội dung</label>
            <textarea id="propContent" rows="2">${escHtml(el.content)}</textarea></div>
        <div class="prop-grid-2">
            <div class="prop-field"><label>Màu chữ</label>
                <input id="propTextColor" type="color" value="${escHtml(el.textColor || '#e6ebf5')}" /></div>
            <div class="prop-field"><label>Cỡ chữ (px)</label>
                <input id="propFontSize" type="number" min="1" max="200" step="1" placeholder="mặc định" value="${el.fontSize ?? ''}" /></div>
        </div>

        ${positionFieldsHtml(el)}
        ${roundField}
        ${cornerField ? `<div class="prop-grid-2">${cornerField}</div>` : ''}
        ${styleFieldsHtml(el, { shape: true })}
        ${effectsFieldsHtml(el)}

        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;

    bindEffectsButtons();
    const fo = document.getElementById('propFillOpacity');
    fo.addEventListener('input', () => { document.getElementById('propFillOpacityVal').textContent = fo.value; });

    if (isCircle) {
        const keep = document.getElementById('propKeepRound');
        const syncHeight = () => {
            if (!keep.checked) return;
            const w = parseFloat(document.getElementById('propWidth').value) || el.width;
            document.getElementById('propHeight').value = Math.min(100, w * ar).toFixed(1);
        };
        keep.addEventListener('change', syncHeight);
        document.getElementById('propWidth').addEventListener('input', syncHeight);
    }

    const applyForm = (el) => {
        el.content = document.getElementById('propContent').value || null;

        const tc = document.getElementById('propTextColor').value;
        el.textColor = (tc.toLowerCase() !== '#e6ebf5' || el.textColor) ? tc : null;
        const fs = parseFloat(document.getElementById('propFontSize').value);
        el.fontSize = Number.isFinite(fs) && fs > 0 ? fs : null;

        if (!isCircle) {
            const cr = parseFloat(document.getElementById('propCorner').value);
            el.cornerRadius = Number.isFinite(cr) && cr > 0 ? cr : null;
        }

        applyPositionFieldsFromForm(el);
        if (isCircle && document.getElementById('propKeepRound').checked) {
            el.height = el.width * ar;
            if (el.height > 100) { el.height = 100; el.width = 100 / ar; }
            clampIntoCanvas(el);
        }
        applyStyleFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });
    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Panel: Duong thang (mau / do day / kieu net / mui ten / chieu dai / goc xoay)
// ---------------------------------------------------------------------------
function renderLineProps(el) {
    const dash = el.borderStyle && el.borderStyle !== 'none' ? el.borderStyle : 'solid';
    const arrow = el.lineArrow || 'none';
    const opt = (v, cur, text) => `<option value="${v}" ${v === cur ? 'selected' : ''}>${text}</option>`;

    propsPanel.innerHTML = `
        <h3>Đường thẳng</h3>

        <div class="prop-section-title">Nét vẽ</div>
        <div class="prop-grid-2">
            <div class="prop-field"><label>Màu đường</label>
                <input id="propLineColor" type="color" value="${escHtml(el.borderColor || '#e6ebf5')}" /></div>
            <div class="prop-field"><label>Độ dày (px)</label>
                <input id="propLineWidth" type="number" min="1" max="30" step="1" value="${el.borderWidth ?? 3}" /></div>
        </div>
        <div class="prop-field"><label>Kiểu nét</label>
            <select id="propLineStyle">
                ${opt('solid', dash, 'Liền nét')}${opt('dashed', dash, 'Nét đứt')}${opt('dotted', dash, 'Nét chấm')}
            </select></div>
        <div class="prop-field"><label>Mũi tên</label>
            <select id="propLineArrow">
                ${opt('none', arrow, 'Không có')}${opt('end', arrow, 'Ở đầu cuối →')}
                ${opt('start', arrow, 'Ở đầu đầu ←')}${opt('both', arrow, 'Cả hai đầu ↔')}
            </select></div>

        ${positionFieldsHtml(el, { line: true })}
        <p class="hint small">Chiều dài tính theo % chiều rộng bản đồ. Dùng "Góc xoay" để vẽ đường đứng (90°) hoặc chéo; "Vùng chọn" chỉ là độ cao khung để dễ bấm chọn.</p>
        ${effectsFieldsHtml(el)}

        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;

    bindEffectsButtons();

    const applyForm = (el) => {
        el.borderColor = document.getElementById('propLineColor').value;
        el.borderWidth = Math.max(1, Math.min(30, parseFloat(document.getElementById('propLineWidth').value) || 3));
        el.borderStyle = document.getElementById('propLineStyle').value;
        el.lineArrow = document.getElementById('propLineArrow').value;
        el.backgroundColor = null;

        applyPositionFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });
    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Panel: Nut an (co hanh dong chuyen mat bang)
// ---------------------------------------------------------------------------
async function renderButtonProps(el) {
    let floors = [];
    try {
        floors = await (await fetch('/api/floors')).json();
    } catch { /* bo qua: van cho chinh sua cac thuoc tinh khac */ }
    if (selectedId !== el.id) return;   // da chon doi tuong khac trong luc cho tai danh sach mat bang

    const floorOptions = ['<option value="">— Không có hành động —</option>']
        .concat(floors.map(f =>
            `<option value="${f.id}" ${el.targetFloorId === f.id ? 'selected' : ''}>${f.id} — ${f.name}</option>`))
        .join('');

    propsPanel.innerHTML = `
        <h3>Nút ấn</h3>
        <div class="prop-field">
            <label>Nhãn nút</label>
            <input id="propLabel" type="text" value="${escHtml(el.label)}" />
        </div>
        <div class="prop-field">
            <label>Hành động khi bấm: chuyển đến mặt bằng</label>
            <select id="propTargetFloor">${floorOptions}</select>
        </div>
        <p class="hint small">Hành động chỉ hoạt động ở chế độ "▶️ Chạy". Ở chế độ Build, bấm vào nút để chỉnh sửa.</p>
        ${positionFieldsHtml(el)}
        ${styleFieldsHtml(el)}
        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;

    const applyForm = (el) => {
        el.label = document.getElementById('propLabel').value;
        el.targetFloorId = document.getElementById('propTargetFloor').value || null;
        applyPositionFieldsFromForm(el);
        applyStyleFieldsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });

    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Panel: Lo do xe
// ---------------------------------------------------------------------------
function renderSlotProps(el) {
    const slot = el.slot ?? { status: 'Empty', plateNumber: null, parkedAt: null, errorMessage: null };
    const parkedAtLocal = slot.parkedAt ? toLocalDateTimeInputValue(slot.parkedAt) : '';

    propsPanel.innerHTML = `
        <h3>Vị trí nhận diện${floorZoneCount > 1 ? ' · Zone ' + escHtml(slotZoneOf(el) || '—') : ''} · số ${escHtml(slotNumberOf(el))}</h3>
        <div class="slot-live-status ${SLOT_STATUS_CLASS[slot.status]}">${SLOT_STATUS_LABEL[slot.status]}</div>

        <div class="prop-field">
            <label>Trạng thái</label>
            <select id="slotStatusSelect">
                <option value="Empty" ${slot.status === 'Empty' ? 'selected' : ''}>Còn trống</option>
                <option value="Occupied" ${slot.status === 'Occupied' ? 'selected' : ''}>Đã đỗ</option>
                <option value="Error" ${slot.status === 'Error' ? 'selected' : ''}>Lỗi</option>
            </select>
        </div>

        <div id="occupiedFields" class="prop-field" style="display:${slot.status === 'Occupied' ? 'block' : 'none'}">
            <label>Biển số xe đã đỗ</label>
            <input id="slotPlate" type="text" value="${slot.plateNumber ?? ''}" placeholder="51K-207.69" />
            <label>Thời điểm đỗ</label>
            <input id="slotParkedAt" type="datetime-local" value="${parkedAtLocal}" />
        </div>

        <div id="errorFields" class="prop-field" style="display:${slot.status === 'Error' ? 'block' : 'none'}">
            <label>Mô tả lỗi</label>
            <textarea id="slotError" rows="2" placeholder="Ví dụ: Camera mất kết nối">${slot.errorMessage ?? ''}</textarea>
        </div>

        <div class="slot-last-exit">
            Xe ra gần nhất: <strong>${escHtml(fmtPlate(slot.lastExitPlate))}</strong> · lúc ${escHtml(fmtDateTime(slot.lastExitTime))}
        </div>

        <div class="prop-section-title">Hiển thị trên bản đồ</div>
        ${DISPLAY_FIELDS.map(f => `
        <label class="checkbox-label" style="margin-bottom:6px;">
            <input type="checkbox" class="slot-display-chk" data-field="${f}" ${el[f] === false ? '' : 'checked'} />
            ${{ showStatusColor: 'Màu trạng thái (đỏ = có xe, vàng = lỗi camera, xanh = trống)', showStatusText: 'Chữ trạng thái (Lỗi / Đã đỗ / Còn trống)',
                showPlate: 'Biển số xe đang đỗ', showEntryTime: 'Thời gian xe vào', showLastExitPlate: 'Biển xe gần nhất ra khỏi vị trí',
                showExitTime: 'Thời gian ra' }[f]}
        </label>`).join('')}

        ${positionFieldsHtml(el)}
        ${styleFieldsHtml(el)}

        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="saveSlotBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Gỡ khỏi bản đồ</button>
        </div>
        <p class="hint small">
            Chỉnh tay trạng thái ở đây để test giao diện khi chưa có camera/model AI thật.
            Khi AI hoạt động, trạng thái sẽ tự cập nhật realtime và ghi đè giá trị chỉnh tay.
            Màu nền/viền mặc định của ô đỗ đổi theo trạng thái; chỉ đặt màu tuỳ chỉnh nếu muốn cố định.
        </p>
    `;

    const applyForm = (el) => {
        document.querySelectorAll('.slot-display-chk').forEach(c => { el[c.dataset.field] = c.checked; });
        applyPositionFieldsFromForm(el);
        applyStyleFieldsFromForm(el);
    };
    bindLivePreview(el, applyForm);

    document.getElementById('slotStatusSelect').addEventListener('change', e => {
        document.getElementById('occupiedFields').style.display = e.target.value === 'Occupied' ? 'block' : 'none';
        document.getElementById('errorFields').style.display = e.target.value === 'Error' ? 'block' : 'none';
    });

    document.getElementById('saveSlotBtn').addEventListener('click', async () => {
        const status = document.getElementById('slotStatusSelect').value;
        const body = {
            status,
            plateNumber: status === 'Occupied' ? document.getElementById('slotPlate').value : null,
            parkedAt: status === 'Occupied' ? fromLocalDateTimeInputValue(document.getElementById('slotParkedAt').value) : null,
            errorMessage: status === 'Error' ? document.getElementById('slotError').value : null
        };

        const res = await fetch(`/api/slots/${encodeURIComponent(el.slotId)}/manual`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        if (!res.ok) {
            alert('Không cập nhật được trạng thái ô đỗ.');
            return;
        }

        // Luu thuoc tinh hien thi + vi tri/kich thuoc/kieu dang cua doi tuong layout
        applyForm(el);
        await persistElement(el);

        await loadLayout();
        selectElement(el.id);

        // Dong bo lai su kien / thong ke tren trang Giam sat
        if (typeof loadEvents === 'function') loadEvents();
        if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots();
    });

    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
}

// ---------------------------------------------------------------------------
// Panel: Den bit / Cong tac bit (General / Shape / Label / Vi tri)
// ---------------------------------------------------------------------------
async function renderBitProps(el) {
    let bits = [];
    try {
        const r = await fetch('/api/tasks/tags/bits');
        if (r.ok) bits = await r.json();
    } catch { /* van cho sua cac thuoc tinh khac */ }
    if (selectedId !== el.id) return;   // da chon doi tuong khac trong luc cho tai danh sach tag

    const q = (id) => document.getElementById(id);
    const tagOptions = (selected, writableOnly) => {
        const pick = bits.filter(b => !writableOnly || b.writable);
        const grp = (label, src) => {
            const items = pick.filter(b => b.source === src);
            return items.length
                ? `<optgroup label="${label}">${items.map(b =>
                    `<option value="${escHtml(b.name)}" ${bitKey(b.name) === bitKey(selected) ? 'selected' : ''}>${escHtml(b.name)}${b.description ? ' — ' + escHtml(b.description) : ''}</option>`).join('')}</optgroup>`
                : '';
        };
        const known = pick.some(b => bitKey(b.name) === bitKey(selected));
        const orphan = selected && !known
            ? `<option value="${escHtml(selected)}" selected>⚠ ${escHtml(selected)} (không có / không hợp lệ ở chế độ này)</option>` : '';
        return `<option value="">— Chọn tag —</option>${orphan}${grp('System tags (chỉ đọc)', 'System')}${grp('Project tags', 'Project')}`;
    };

    const mode = bitModeOf(el), style = bitStyleOf(el);
    const imgBlock = (i) => `
        <div class="prop-section-title">Trạng thái ${i} (${i ? 'ON' : 'OFF'})</div>
        <div class="bit-shape-row">
            <div class="bit-shape-preview" id="propBitPrev${i}"></div>
            <div class="bit-shape-ctrl">
                <input id="propBitImg${i}" type="text" placeholder="URL ảnh hoặc tải lên…" value="${escHtml(i ? el.stateImage1 : el.stateImage0)}" />
                <div class="prop-inline">
                    <button type="button" class="btn secondary" id="propBitUp${i}" style="padding:6px;">📁 Tải ảnh lên</button>
                    <button type="button" class="btn secondary" id="propBitClr${i}" style="padding:6px;">✕ Bỏ ảnh</button>
                </div>
                <input type="file" id="propBitFile${i}" accept=".png,.jpg,.jpeg,.gif,.webp,.bmp" hidden />
            </div>
        </div>`;

    propsPanel.innerHTML = `
        <h3>Đèn bit / Công tắc bit</h3>
        <div class="prop-tabs">
            <button type="button" class="prop-tab active" data-pane="general">General</button>
            <button type="button" class="prop-tab" data-pane="shape">Shape</button>
            <button type="button" class="prop-tab" data-pane="label">Label</button>
            <button type="button" class="prop-tab" data-pane="pos">Vị trí</button>
        </div>

        <div class="bit-pane" data-pane="general">
            <div class="prop-field"><label>Comment</label>
                <input id="propBitComment" type="text" maxlength="200" value="${escHtml(el.comment)}" /></div>
            <div class="prop-field">
                <label class="checkbox-label"><input type="radio" name="propBitMode" value="Lamp" ${mode === 'Lamp' ? 'checked' : ''} /> Bit Lamp (chỉ hiển thị)</label>
                <label class="checkbox-label"><input type="radio" name="propBitMode" value="Switch" ${mode === 'Switch' ? 'checked' : ''} /> Toggle Switch (bấm để ghi, ở chế độ Chạy)</label>
            </div>
            <div class="prop-section-title">Đọc / Ghi</div>
            <label class="checkbox-label" style="margin-bottom:8px;"><input type="checkbox" id="propBitSep" ${el.separateWriteTag ? 'checked' : ''} /> Đọc và ghi dùng tag khác nhau</label>
            <div class="prop-field"><label id="propBitReadLbl">Tag</label><select id="propBitReadTag"></select></div>
            <div class="prop-field" id="propBitWriteRow"><label>Tag ghi (Project tag)</label><select id="propBitWriteTag"></select></div>
            <label class="checkbox-label" style="margin-bottom:8px;"><input type="checkbox" id="propBitInvert" ${el.invertSignal ? 'checked' : ''} /> Đảo tín hiệu (Invert signal)</label>
            <label class="checkbox-label" style="margin-bottom:8px;"><input type="checkbox" id="propBitRelease" ${el.writeOnRelease ? 'checked' : ''} /> Chỉ ghi khi nhả nút</label>
            <div class="prop-section-title">Thuộc tính</div>
            <div class="prop-field"><label>Kiểu công tắc (Switch style)</label>
                <select id="propBitStyle">
                    <option value="SetOn" ${style === 'SetOn' ? 'selected' : ''}>Set ON — luôn ghi ON</option>
                    <option value="SetOff" ${style === 'SetOff' ? 'selected' : ''}>Set OFF — luôn ghi OFF</option>
                    <option value="Toggle" ${style === 'Toggle' ? 'selected' : ''}>Toggle — đảo trạng thái</option>
                    <option value="Momentary" ${style === 'Momentary' ? 'selected' : ''}>Momentary — giữ = ON, nhả = OFF</option>
                </select></div>
            <p class="hint small">System tags chỉ để đọc (đèn). Muốn bấm để ghi, tag ghi phải là Project tag kiểu Boolean (TASKS › Tags).</p>
        </div>

        <div class="bit-pane" data-pane="shape" hidden>
            ${imgBlock(0)}
            ${imgBlock(1)}
            <p class="hint small">Không chọn ảnh = vẽ hình mặc định (đèn tròn / công tắc). Ảnh PNG/JPG/GIF/WEBP/BMP, tối đa 5 MB.</p>
        </div>

        <div class="bit-pane" data-pane="label" hidden>
            <div class="prop-field"><label>Chữ khi trạng thái 0 (OFF)</label><input id="propBitText0" type="text" maxlength="64" value="${escHtml(el.stateText0)}" /></div>
            <div class="prop-field"><label>Chữ khi trạng thái 1 (ON)</label><input id="propBitText1" type="text" maxlength="64" value="${escHtml(el.stateText1)}" /></div>
            <div class="prop-grid-2">
                <div class="prop-field"><label>Màu chữ</label><input id="propBitTextColor" type="color" value="${escHtml(el.textColor || '#e6ebf5')}" /></div>
                <div class="prop-field"><label>Cỡ chữ (px)</label><input id="propBitFont" type="number" min="1" max="200" step="1" placeholder="mặc định" value="${el.fontSize ?? ''}" /></div>
            </div>
        </div>

        <div class="bit-pane" data-pane="pos" hidden>
            ${positionFieldsHtml(el)}
            ${styleFieldsHtml(el)}
            ${effectsFieldsHtml(el)}
        </div>

        ${DIRTY_NOTE_HTML}
        <div class="prop-actions">
            <button id="savePropsBtn" class="btn primary">💾 Lưu</button>
            <button id="deleteElBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
    `;
    bindEffectsButtons();

    // --- tab ---
    propsPanel.querySelectorAll('.prop-tab').forEach(tab => tab.addEventListener('click', () => {
        propsPanel.querySelectorAll('.prop-tab').forEach(t => t.classList.toggle('active', t === tab));
        propsPanel.querySelectorAll('.bit-pane').forEach(p => { p.hidden = p.dataset.pane !== tab.dataset.pane; });
        fitPropsDialog();
    }));

    // --- danh sach tag + bat/tat o theo che do ---
    const curMode = () => propsPanel.querySelector('input[name="propBitMode"]:checked')?.value || 'Switch';
    q('propBitReadTag').innerHTML = tagOptions(el.readTag, mode === 'Switch' && !el.separateWriteTag);
    q('propBitWriteTag').innerHTML = tagOptions(el.writeTag, true);

    const syncForm = () => {
        const sw = curMode() === 'Switch', sep = q('propBitSep').checked, mom = q('propBitStyle').value === 'Momentary';
        q('propBitSep').disabled = !sw;
        q('propBitStyle').disabled = !sw;
        q('propBitRelease').disabled = !sw || mom;
        if (mom) q('propBitRelease').checked = false;
        q('propBitWriteRow').style.display = sw && sep ? '' : 'none';
        q('propBitReadLbl').textContent = !sw ? 'Tag bit (đọc)' : sep ? 'Tag đọc (hiển thị trạng thái)' : 'Tag đọc / ghi (Project tag)';
        const rs = q('propBitReadTag'), ws = q('propBitWriteTag');
        const rv = rs.value, wv = ws.value;
        rs.innerHTML = tagOptions(rv, sw && !sep);
        ws.innerHTML = tagOptions(wv, true);
    };
    propsPanel.querySelectorAll('input[name="propBitMode"]').forEach(r => r.addEventListener('change', syncForm));
    q('propBitSep').addEventListener('change', syncForm);
    q('propBitStyle').addEventListener('change', syncForm);
    syncForm();

    // --- hinh anh theo trang thai ---
    [0, 1].forEach(i => {
        const inp = q(`propBitImg${i}`), prev = q(`propBitPrev${i}`), file = q(`propBitFile${i}`);
        const showPrev = () => { prev.innerHTML = inp.value.trim() ? `<img src="${escHtml(inp.value.trim())}" alt="" />` : '<span>Mặc định</span>'; };
        showPrev();
        inp.addEventListener('input', showPrev);
        q(`propBitUp${i}`).addEventListener('click', () => file.click());
        q(`propBitClr${i}`).addEventListener('click', () => { inp.value = ''; inp.dispatchEvent(new Event('input', { bubbles: true })); });
        file.addEventListener('change', async () => {
            const f = file.files[0];
            if (!f) return;
            const fd = new FormData();
            fd.append('image', f);
            try {
                const r = await fetch('/api/layout/upload-image', { method: 'POST', body: fd });
                const d = await r.json().catch(() => ({}));
                if (!r.ok) throw new Error(d.message || 'Không tải được ảnh.');
                inp.value = d.url;
                inp.dispatchEvent(new Event('input', { bubbles: true }));
            } catch (err) { alert(err.message || 'Không tải được ảnh.'); }
            file.value = '';
        });
    });

    const applyForm = (el) => {
        el.comment = q('propBitComment').value.trim() || null;
        el.bitMode = curMode();
        el.separateWriteTag = q('propBitSep').checked;
        el.readTag = q('propBitReadTag').value || null;
        el.writeTag = el.separateWriteTag ? (q('propBitWriteTag').value || null) : null;
        el.invertSignal = q('propBitInvert').checked;
        el.switchStyle = q('propBitStyle').value;
        el.writeOnRelease = q('propBitRelease').checked && el.switchStyle !== 'Momentary';
        el.stateImage0 = q('propBitImg0').value.trim() || null;
        el.stateImage1 = q('propBitImg1').value.trim() || null;
        el.stateText0 = q('propBitText0').value || null;
        el.stateText1 = q('propBitText1').value || null;
        const tc = q('propBitTextColor').value;
        el.textColor = (tc.toLowerCase() !== '#e6ebf5' || el.textColor) ? tc : null;
        const fs = parseFloat(q('propBitFont').value);
        el.fontSize = Number.isFinite(fs) && fs > 0 ? fs : null;
        applyPositionFieldsFromForm(el);
        applyStyleFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    q('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });
    q('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
    fitPropsDialog();
}

async function persistElement(el) {
    await fetch(`/api/layout/${el.id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(el)
    });
    if (pending && pending.id === el.id) pending = { id: el.id, snapshot: cloneObj(el), dirty: false };
}

async function deleteElement(id) {
    if (!confirm('Xoá đối tượng này khỏi bản đồ?')) return;
    await fetch(`/api/layout/${id}`, { method: 'DELETE' });
    selectedId = null;
    if (live) { clearTimeout(live.timer); live = null; }
    pending = null;
    propsPanel.innerHTML = EMPTY_PROPS_HTML;
    await loadLayout();
}

function typeLabel(type) {
    return { Button: 'Nút ấn', Image: 'Hình ảnh', Text: 'Text', ParkingSlot: 'Vùng nhận diện', SlotStats: 'Thống kê chỗ', BitLampSwitch: 'Đèn / Công tắc bit',
        Rectangle: 'Hình chữ nhật', Circle: 'Hình tròn', Line: 'Đường thẳng' }[type] || type;
}

function toLocalDateTimeInputValue(isoString) {
    const d = new Date(isoString);
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fromLocalDateTimeInputValue(value) {
    return value ? new Date(value).toISOString() : null;
}

// ---------------------------------------------------------------------------
// Thanh bat/tat thuoc tinh hien thi cho TAT CA vung nhan dien cua tang (che do Build)
// ---------------------------------------------------------------------------
const DISPLAY_FIELDS = ['showStatusColor', 'showStatusText', 'showPlate', 'showEntryTime', 'showLastExitPlate', 'showExitTime'];

function syncDisplayBar() {
    const slots = elements.filter(e => e.type === 'ParkingSlot');
    const bar = document.getElementById('slotDisplayBar');
    if (!bar) return;
    bar.style.display = slots.length ? '' : 'none';
    bar.querySelectorAll('input[data-display]').forEach(chk => {
        const f = chk.dataset.display;
        const on = slots.filter(e => e[f] !== false).length;
        chk.checked = on === slots.length;
        chk.indeterminate = on > 0 && on < slots.length; // moi vung 1 kieu: hien dau "-"
    });
}

document.querySelectorAll('#slotDisplayBar input[data-display]').forEach(chk => {
    chk.addEventListener('change', async () => {
        const field = chk.dataset.display;
        const value = chk.checked;
        const res = await fetch(`/api/layout/slot-display?floor=${encodeURIComponent(CURRENT_FLOOR)}`, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ [field]: value })
        });
        if (!res.ok) { alert('Không lưu được thiết lập hiển thị.'); await loadLayout(); return; }

        elements.filter(e => e.type === 'ParkingSlot').forEach(e => { e[field] = value; });
        document.querySelectorAll('.layout-el-parkingslot').forEach(node => {
            const el = elements.find(x => x.id === node.dataset.id);
            if (el) renderSlotNode(node, el);
        });
        syncDisplayBar();
    });
});

// ---------------------------------------------------------------------------
// Ảnh nền mặt bằng
// ---------------------------------------------------------------------------
document.getElementById('floorBgInput').addEventListener('change', async () => {
    const input = document.getElementById('floorBgInput');
    const file = input.files[0];
    if (!file || !CURRENT_FLOOR) return;

    const form = new FormData();
    form.append('image', file);

    const res = await fetch(`/api/floors/${encodeURIComponent(CURRENT_FLOOR)}/background`, {
        method: 'POST',
        body: form
    });

    input.value = '';

    if (!res.ok) {
        alert('Không tải được ảnh nền.');
        return;
    }

    const floor = await res.json();
    window.currentFloorData = floor;
    applyFloorBackground(floor);
});

document.getElementById('clearBgBtn').addEventListener('click', async () => {
    if (!CURRENT_FLOOR) return;
    await fetch(`/api/floors/${encodeURIComponent(CURRENT_FLOOR)}/background`, { method: 'DELETE' });
    window.currentFloorData = { ...(window.currentFloorData || {}), backgroundImageUrl: null };
    applyFloorBackground(window.currentFloorData);
});

// ---------------------------------------------------------------------------
// Chế độ Build / Chạy
// ---------------------------------------------------------------------------
const buildModeBtn = document.getElementById('buildModeBtn');
const runModeBtn = document.getElementById('runModeBtn');

buildModeBtn.addEventListener('click', exitRunMode);
runModeBtn.addEventListener('click', enterRunMode);

function enterRunMode() {
    closePropsDialog();      // luu thay doi dang do va dong hop thoai thuoc tinh
    if (live) { clearTimeout(live.timer); live = null; }
    flushPending();
    editMode = false;
    document.body.classList.add('layout-run-mode');
    buildModeBtn.classList.remove('primary', 'active');
    buildModeBtn.classList.add('secondary');
    runModeBtn.classList.remove('secondary');
    runModeBtn.classList.add('primary', 'active');
    showRunExitButton();
}

function exitRunMode() {
    releaseAllMomentary();
    editMode = true;
    document.body.classList.remove('layout-run-mode');
    runModeBtn.classList.remove('primary', 'active');
    runModeBtn.classList.add('secondary');
    buildModeBtn.classList.remove('secondary');
    buildModeBtn.classList.add('primary', 'active');
    hideRunExitButton();
}

// Thoat che do Chay bang phim ESC (khong con nut tren man hinh)
function showRunExitButton() { showRunHint(); }
function hideRunExitButton() { document.getElementById('layoutRunHint')?.remove(); }

// Goi y nho "ESC de thoat", tu mo dan sau ~2.5s de khong che ban do
function showRunHint() {
    document.getElementById('layoutRunHint')?.remove();
    const tip = document.createElement('div');
    tip.id = 'layoutRunHint';
    tip.className = 'layout-run-hint';
    tip.textContent = 'Nhấn ESC để thoát chế độ chạy';
    document.body.appendChild(tip);
    setTimeout(() => tip.classList.add('fade'), 2500);
    setTimeout(() => tip.remove(), 3300);
}

document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || editMode) return;
    e.preventDefault();
    exitRunMode();
});

applyFloorBackground(window.currentFloorData);
loadLayout();
pollBits();

// Dong tab / tai lai trang khi con thay doi chua luu -> gui di bang keepalive de khong mat
window.addEventListener('pagehide', () => {
    const p = pending;
    if (!p || !p.dirty) return;
    const el = elements.find(x => x.id === p.id);
    if (!el) return;
    fetch(`/api/layout/${el.id}`, {
        method: 'PUT', keepalive: true,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(el)
    });
});
