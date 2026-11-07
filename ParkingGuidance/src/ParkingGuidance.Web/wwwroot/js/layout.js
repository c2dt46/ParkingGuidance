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
let selectedId = null;       // doi tuong CHINH (chon sau cung) - dung cho panel thuoc tinh / khung co gian
let selectedIds = new Set(); // moi doi tuong dang chon (chon nhieu: quet chuot hoac Ctrl/Shift + nhap); luon chua selectedId

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
    selectedId = null; selectedIds.clear(); closeCtxMenu();
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

function setCanvasAspect(ratio, wide) {
    const lo = wide ? 0.0005 : 0.5, hi = wide ? 2000 : 4;   // wide: Header / Navigation (khung có thể rất dẹt hoặc rất cao)
    const r = Number.isFinite(ratio) && ratio > 0 ? Math.min(hi, Math.max(lo, ratio)) : DEFAULT_ASPECT;
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

const isPanelFloor = (f) => !!f && (f.kind === 'Header' || f.kind === 'Navigation');

// Vùng mặt bằng thực tế khi CHẠY toàn màn hình = màn hình trừ Header (trên) và Navigation / thanh bên (trái).
// Build dùng đúng tỉ lệ này nên Build và Chạy khớp nhau, không bị méo.
const SIDEBAR_RUN_W = 260;
function areaAspect() {
    const sw = (window.screen && screen.width) || window.innerWidth || 1920;
    const sh = (window.screen && screen.height) || window.innerHeight || 1080;
    const list = window.floorList || [];
    const hdr = list.find(f => f.kind === 'Header' && f.enabled !== false);
    const nav = list.find(f => f.kind === 'Navigation' && f.enabled !== false);
    const hdrH = hdr ? Math.max(5, hdr.panelHeight || 0) : 0;
    const navW = nav ? Math.max(5, nav.panelWidth || 0) : (document.documentElement.classList.contains('sidebar-hidden') ? 0 : SIDEBAR_RUN_W);
    const w = sw - navW, h = sh - hdrH;
    return w > 100 && h > 100 ? w / h : DEFAULT_ASPECT;
}
function refreshFloorAspect() {
    if (window.PG_PANEL || isPanelFloor(window.currentFloorData)) return;
    setCanvasAspect(areaAspect());
}
window.addEventListener('resize', refreshFloorAspect);
window.addEventListener('floorlistchange', refreshFloorAspect);

function applyFloorBackground(floor) {
    const token = ++bgAspectToken;
    canvas.style.backgroundColor = floor?.backgroundColor || '';   // màu nền riêng của mặt bằng / Header / Navigation (rỗng = mặc định)
    if (isPanelFloor(floor)) {
        // Header / Navigation: khung đúng tỉ lệ rộng × cao đã thiết lập; ảnh nền (nếu có) kéo vừa khung
        const w = floor.panelWidth || 1920, h = floor.panelHeight || 100;
        setCanvasAspect(w / h, true);
        canvas.style.backgroundImage = floor.backgroundImageUrl ? `url('${floor.backgroundImageUrl}')` : '';
        canvas.style.backgroundSize = '100% 100%';
        canvas.style.backgroundPosition = 'center';
        canvas.style.backgroundRepeat = 'no-repeat';
        return;
    }
    // Mặt bằng thường: khung = vùng mặt bằng THỰC TẾ trên màn hình (areaAspect), không phụ thuộc tỉ lệ ảnh.
    // Ảnh nền luôn bắt đầu từ điểm (0,0) của khung và được kéo VỪA KHÍT cả khung (chế độ "zoom": thừa hay thiếu đều khớp);
    // toạ độ % của đối tượng nên luôn trùng đúng điểm trên ảnh, ở cả Build lẫn Chạy.
    canvas.style.backgroundImage = floor?.backgroundImageUrl ? `url('${floor.backgroundImageUrl}')` : '';
    canvas.style.backgroundSize = floor?.backgroundImageUrl ? '100% 100%' : '';
    canvas.style.backgroundPosition = floor?.backgroundImageUrl ? '0 0' : '';
    canvas.style.backgroundRepeat = floor?.backgroundImageUrl ? 'no-repeat' : '';
    setCanvasAspect(areaAspect());
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
    refreshLampStates();
    syncDisplayBar();
    if (selectedId && elements.some(x => x.id === selectedId)) selectElement(selectedId);
    else { selectedId = null; selectedIds.clear(); if (propsOpen) propsPanel.innerHTML = EMPTY_PROPS_HTML; }
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
    elements.forEach(el => {
        const n = buildElementNode(el);
        if (selectedIds.has(el.id)) n.classList.add('selected');
        canvas.appendChild(n);
    });
    updateResizeFrame();
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
            {   // can le chu tren nut (.layout-el la flex dang cot: can ngang bang align-items)
                const jc = { left: 'flex-start', right: 'flex-end', center: 'center' }[el.textAlign];
                if (jc) { div.style.alignItems = jc; div.style.textAlign = el.textAlign; if (el.textAlign !== 'center') div.style.padding = '0 calc(10 * var(--px))'; }
            }
            break;

        case 'Image':
            if (el.imageUrl) {
                // "Khong nen" (backgroundColor = transparent): bo nen + vien mac dinh, hien du anh (contain) de giu phan trong suot
                if (el.backgroundColor === 'transparent') div.classList.add('img-nobg');
                div.innerHTML = `<img src="${escHtml(el.imageUrl)}" alt="" draggable="false" />`;
            } else {
                div.innerHTML = `<span class="layout-el-placeholder">🖼️ Chưa có ảnh</span>`;
            }
            break;

        case 'Text':
        case 'Number':
            renderValueNode(div, el);      // Text (co the gan bien) / Number: gia tri bien cap nhat gan realtime
            break;

        case 'DateTime':
            renderDateTimeNode(div, el);   // Ngay gio: dong ho cap nhat moi giay (tickDateTimes)
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
    applyAnimToNode(div, el);          // hieu ung dong (chi o che do Chay)
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
    // Co chi dinh bien: do sai -> doi mau rieng (tim/noi bat), khac voi do thuong
    const wrongPark = status === 'Occupied' && slot.plateCheck === 'Invalid';
    div.classList.add(el.showStatusColor === false ? 'neutral' : (wrongPark ? 'wrong' : SLOT_STATUS_CLASS[status]));
    if (wasSelected) div.classList.add('selected');

    const row = (k, v) => `<div class="slot-row"><span class="k">${k}</span><span class="v">${escHtml(v)}</span></div>`;
    let html = `<div class="slot-id" title="${floorZoneCount > 1 ? 'Zone ' + escHtml(slotZoneOf(el) || '—') + ' · ' : ''}vị trí ${escHtml(slotNumberOf(el))}">${escHtml(slotLabelOf(el))}</div>`;
    let statusText = SLOT_STATUS_LABEL[status] ?? status;
    if (status === 'Occupied' && slot.plateCheck === 'Invalid') statusText = 'Đỗ sai';
    else if (status === 'Occupied' && slot.plateCheck === 'Valid') statusText = 'Đỗ đúng';
    if (el.showStatusText !== false) html += `<div class="slot-status-tag">${statusText}</div>`;

    // Dang Loi (camera mat ket noi): bien/gio vao la cua xe do truoc khi loi, van hien de biet xe nao dang o do
    const hasVehicle = (status === 'Occupied' || status === 'Error') && slot.plateNumber;
    if (el.showPlate !== false) html += row('Biển', hasVehicle ? fmtPlate(slot.plateNumber) : '—');
    if (el.showPlate !== false && slot.assignedPlate) html += row('Chỉ định', slot.assignedPlate);
    if (el.showEntryTime !== false) html += row('Vào', hasVehicle ? fmtDateTime(slot.parkedAt) : '—');
    if (el.showLastExitPlate !== false) html += row('Ra gần nhất', fmtPlate(slot.lastExitPlate));
    if (el.showExitTime !== false) html += row('Giờ ra', fmtDateTime(slot.lastExitTime));

    div.innerHTML = html;
    div.title = status === 'Error' ? (slot.errorMessage || 'Lỗi camera')
        : (status === 'Occupied' && slot.assignedPlate && slot.plateCheck
            ? (slot.plateCheck === 'Invalid' ? `Đỗ sai: ô chỉ định cho biển ${slot.assignedPlate}`
                : slot.plateCheck === 'Valid' ? `Đỗ đúng biển chỉ định ${slot.assignedPlate}`
                : `Chờ xác nhận biển số (chỉ định ${slot.assignedPlate})`)
            : '');
    applyCommonStyle(div, el);
}

// ---------------------------------------------------------------------------
// Doi tuong "Thong ke cho": so cho trong / so cho co xe / so loi khong lay duoc anh cua tang
// ---------------------------------------------------------------------------
function renderStatsNode(div, el) {
    const st = el.stats || { free: 0, occupied: 0, error: 0, camerasError: 0 };
    const wasSelected = div.classList.contains('selected');
    const horiz = el.statsOrientation === 'Horizontal';
    div.className = 'layout-el layout-el-slotstats as-stats ' + (horiz ? 'stats-h' : 'stats-v');
    if (wasSelected) div.classList.add('selected');

    // Co chu so (fontSize) va co chu nhan (labelFontSize): theo "px thiet ke" (--px) nhu cac doi tuong khac
    if (el.fontSize) div.style.setProperty('--stat-num-n', String(Number(el.fontSize))); else div.style.removeProperty('--stat-num-n');
    if (el.labelFontSize) div.style.setProperty('--stat-label-n', String(Number(el.labelFontSize))); else div.style.removeProperty('--stat-label-n');

    const item = (cls, num, label, sub) =>
        `<div class="stat-item ${cls}"><span class="stat-num">${num}</span><span class="stat-label">${label}</span>${sub ? `<span class="stat-sub">${sub}</span>` : ''}</div>`;

    // "Loi anh" = camera mat ket noi; neu da cong bien MS (cam bien) vao so loi thi goi chung la "Loi"
    const errLabel = (el.statErrorVars || []).length ? 'Lỗi' : 'Lỗi ảnh';
    const errSub = st.camerasError && el.statErrorCamera !== false ? `${st.camerasError} camera` : '';

    let html = '';
    if (el.showStatFree !== false) html += item('stat-free', st.free, 'Trống');
    if (el.showStatOccupied !== false) html += item('stat-occupied', st.occupied, 'Có xe');
    if (el.showStatError !== false) html += item('stat-error', st.error, errLabel, errSub);

    div.innerHTML = html || '<span class="layout-el-placeholder">Chưa chọn số liệu</span>';
    div.title = 'Thống kê chỗ đỗ của tầng ' + CURRENT_FLOOR +
        ` — trống ${st.free}${st.sensorFree ? ` (cảm biến ${st.sensorFree})` : ''}, có xe ${st.occupied}${st.sensorOccupied ? ` (cảm biến ${st.sensorOccupied})` : ''}, lỗi ${st.error}${st.sensorError ? ` (cảm biến ${st.sensorError})` : ''}` +
        (st.camerasError ? ` — ${st.camerasError} camera không lấy được ảnh` : '');
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

// ---- Den (Lamp) nguon CAM BIEN / VI TRI DO CHI DINH TREN CAMERA: 4 trang thai Da do / Con trong / Loi / Chua su dung ----
const LAMP_STATES = ['Parked', 'Free', 'Error', 'Unused'];
const LAMP_LABEL = { Parked: 'Đã đỗ', Free: 'Còn trống', Error: 'Lỗi', Unused: 'Chưa sử dụng' };
const LAMP_POLL_MS = 1000;
let lampStates = {};           // id doi tuong -> 'Parked' | 'Free' | 'Error' | 'Unused' (may chu tinh); thieu = chua co du lieu
let sensorMeta = null;         // { type: 'Pm200'|'Keytop', deviceCount, sensorsPerDevice, devices[] } - tu /api/system/sensors/status (nap khi mo panel)

const bitSourceOf = (el) => bitModeOf(el) === 'Lamp' && (el.bitSource === 'Sensor' || el.bitSource === 'CameraSlot') ? el.bitSource : 'Tag';
const lampStateOf = (el) => LAMP_STATES.includes(lampStates[el.id]) ? lampStates[el.id] : null;
const bitSigOf = (el) => bitSourceOf(el) === 'Tag' ? String(bitLogical(el)) : 'L:' + (lampStateOf(el) ?? 'null');

// Mo ta nguon cua den (hien o tooltip): "ZCU 0 · CN2 · ID 5" / "Vị trí 5 (Zone 2)"
function lampSourceText(el) {
    if (bitSourceOf(el) === 'Sensor') {
        const d = el.sensorDevice ?? 0, i = el.sensorIndex ?? 0;
        if (sensorMeta?.type === 'Keytop') return `Keytop ${d} · cảm biến ${i + 1}`;
        return `ZCU ${d} · CN${Math.floor(i / 32) + 1} · ID ${i % 32}`;
    }
    return el.lampPosition ? `Vị trí ${el.lampPosition}${el.lampZone ? ` (Zone ${el.lampZone})` : ''}` : 'Chưa chọn vị trí';
}

// Trang thai hien thi (da tinh "Dao tin hieu"): 1 / 0; null = chua chon tag hoac khong doc duoc
function bitLogical(el) {
    if (!el.readTag) return null;
    const raw = bitStates[bitKey(el.readTag)];
    if (raw === undefined) return null;
    return (raw ? 1 : 0) ^ (el.invertSignal ? 1 : 0);
}

function renderLampStateNode(div, el) {
    const st = lampStateOf(el);                       // null = chua co du lieu (lan doc dau chua ve)
    const img = st ? el['stateImage' + st] : null;
    const text = st ? el['stateText' + st] : null;
    const face = img
        ? `<img class="bit-img" src="${escHtml(img)}" alt="" draggable="false" />`
        : '<span class="bit-lamp-dot"></span>';

    div.classList.add('as-bit', 'bit-lamp', 'bit-state-lamp');
    div.classList.remove('bit-switch', 'has-action', 'bit-on', 'bit-off');
    div.classList.toggle('bit-unknown', st === null);
    LAMP_STATES.forEach(k => div.classList.toggle('bit-s-' + k.toLowerCase(), st === k));
    div.innerHTML = `<div class="bit-face">${face}</div>${text ? `<div class="bit-text">${escHtml(text)}</div>` : ''}`;
    div.dataset.bitSig = bitSigOf(el);

    const what = bitSourceOf(el) === 'Sensor' ? 'Cảm biến' : 'Vị trí đỗ (camera)';
    div.title = (el.comment ? el.comment + ' — ' : '') + `${what} ${lampSourceText(el)}: ${st ? LAMP_LABEL[st] : 'chưa có dữ liệu'}`;
    applyCommonStyle(div, el);
}

function renderBitNode(div, el) {
    if (bitSourceOf(el) !== 'Tag') { renderLampStateNode(div, el); return; }
    div.classList.remove('bit-state-lamp', ...LAMP_STATES.map(k => 'bit-s-' + k.toLowerCase()));
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
    div.dataset.bitSig = bitSigOf(el);

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
        if (!node || node.dataset.bitSig === bitSigOf(el)) return;
        renderBitNode(node, el);
    });
}

async function pollBits() {
    try {
        if (!document.hidden) {
            const names = [...new Set(elements.filter(e => e.type === 'BitLampSwitch' && bitSourceOf(e) === 'Tag' && e.readTag).map(e => e.readTag))];
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

// Trang thai den nguon Cam bien / Vi tri do tren camera: may chu tinh san (cam bien that + o do theo camera)
async function refreshLampStates() {
    if (!elements.some(e => e.type === 'BitLampSwitch' && bitSourceOf(e) !== 'Tag')) return;
    try {
        const r = await fetch(`/api/layout/lamp-states?floor=${encodeURIComponent(CURRENT_FLOOR)}`, { cache: 'no-store' });
        if (!r.ok) throw new Error('lamp-states ' + r.status);
        lampStates = await r.json();
        updateBitNodes();
    } catch { /* mat ket noi tam thoi: giu trang thai cu, lan sau thu lai */ }
}
async function pollLampStates() {
    if (!document.hidden) await refreshLampStates();
    setTimeout(pollLampStates, LAMP_POLL_MS);
}

// ---- Thong tin vi tri do khi bam vao den Cam bien / Vi tri do camera (che do Chay) ----
// Cam bien: loai + so thiet bi, Line/ID (MGT200) hoac so cam bien (Keytop), ket noi, lan cuoi nhan du lieu.
// Camera: Zone + vi tri, trang thai, bien so dang do / bien so chi dinh + ket qua doi chieu, gio vao, camera phu trach, xe roi gan nhat, loi.
// clientX/clientY và getBoundingClientRect() tính theo px MÀN HÌNH, còn left/top/width trong style tính theo px CSS. Hai hệ này lệch nhau đúng bằng
// "Kích thước giao diện" (root.style.zoom trong prefs.js) khác 100%: khung/cửa sổ vẽ sai chỗ so với con trỏ. Đo trực tiếp bằng 1 phần tử thử.
function cssPerScreenPx() {
    const p = document.createElement('div');
    p.style.cssText = 'position:fixed;left:0;top:0;width:100px;height:1px;visibility:hidden;pointer-events:none';
    document.body.appendChild(p);
    const w = p.getBoundingClientRect().width;
    p.remove();
    return w > 0 ? 100 / w : 1;
}

let lampPop = null;       // { id, pop, timer }
let lampPopPos = null;    // vị trí cửa sổ do người dùng kéo tới ({left, top}); giữ nguyên cho mọi lần mở sau cho tới khi tải lại trang
function closeLampInfo() {
    if (!lampPop) return;
    clearInterval(lampPop.timer);
    lampPop.pop.remove();
    lampPop = null;
}

function fmtSpan(ms) {           // 1 gio 5 phut / 12 phut / 30 giay
    const sec = Math.max(0, Math.floor(ms / 1000)), h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
    if (h > 0) return `${h} giờ ${m} phút`;
    return m > 0 ? `${m} phút` : `${sec} giây`;
}
const fmtSince = (iso) => { const d = iso ? new Date(iso) : null; return d && !isNaN(d) ? fmtSpan(Date.now() - d.getTime()) : ''; };

function lampInfoHtml(d) {
    const row = (k, v) => `<div class="lamp-info-row"><span>${k}</span><b>${v}</b></div>`;
    const st = LAMP_STATES.includes(d.state) ? d.state : 'Unused';
    let title = '', rows = '';

    if (d.kind === 'Sensor') {
        const s = d.sensor || {}, keytop = String(s.type).toLowerCase() === 'keytop';
        title = keytop ? `Cảm biến ${s.number} · Keytop ${s.device}` : `Cảm biến · ZCU ${s.device} · CN${s.line} · ID ${s.id}`;
        rows += row('Loại thiết bị', keytop ? 'Keytop' : 'MGT200 (ZCU)');
        rows += row('Thiết bị', `${keytop ? 'Keytop' : 'ZCU'} ${s.device}`);
        rows += keytop ? row('Cảm biến số', s.number) : row('Line / ID', `CN${s.line} / ID ${s.id}`);
        rows += row('Kích hoạt', s.deviceEnabled ? 'Có' : 'Không');
        rows += row('Kết nối', s.deviceEnabled ? (s.deviceOnline ? 'Đang kết nối' : '<span class="li-bad">Mất kết nối</span>') : '—');
        rows += row('Dữ liệu cuối', s.lastSeen ? `${escHtml(fmtDateTime(s.lastSeen))} (${escHtml(fmtSince(s.lastSeen))} trước)` : 'Chưa nhận');
    } else {
        const s = d.slot || {};
        title = `Vị trí đỗ ${escHtml(s.position ?? '—')}${s.zone ? ` · Zone ${escHtml(s.zone)}` : ''} · tầng ${escHtml(s.floor ?? CURRENT_FLOOR)}`;
        if (s.declared) {
            const parked = s.status === 'Occupied';
            const unknownPlate = !s.plateNumber || String(s.plateNumber).toUpperCase() === 'UNKNOWN';
            const plateText = (p, unknownText) => !p || String(p).toUpperCase() === 'UNKNOWN' ? unknownText : escHtml(p);
            rows += row('Biển số đang đỗ', parked ? plateText(s.plateNumber, 'Chưa đọc được biển') : '—');
            rows += row('Giờ vào', parked && s.parkedAt ? `${escHtml(fmtDateTime(s.parkedAt))} (đã đỗ ${escHtml(fmtSince(s.parkedAt))})` : '—');
            rows += row('Biển số chỉ định', s.assignedPlate ? escHtml(s.assignedPlate) : 'Không chỉ định');
            if (parked && s.assignedPlate) {
                const chk = { Valid: '<span class="li-ok">Đỗ đúng</span>', Invalid: '<span class="li-bad">Đỗ sai biển chỉ định</span>', Pending: 'Đang kiểm tra' }[s.plateCheck];
                if (chk) rows += row('Đối chiếu', chk);
            }
            const cams = (s.cameras || []).map(c => escHtml(c.ip + (c.brand || c.model ? ` (${[c.brand, c.model].filter(Boolean).join(' ')})` : ''))).join('<br>');
            rows += row('Camera', cams || '—');
            rows += row('Biển số xe gần nhất ra', s.lastExitTime || s.lastExitPlate ? plateText(s.lastExitPlate, 'Chưa rõ biển') : '—');
            rows += row('Giờ ra', s.lastExitTime ? escHtml(fmtDateTime(s.lastExitTime)) : '—');
        }
    }
    if (d.reason) rows += row(st === 'Error' ? 'Lỗi' : 'Ghi chú trạng thái', `<span class="${st === 'Error' ? 'li-bad' : ''}">${escHtml(d.reason)}</span>`);
    if (d.comment) rows += row('Ghi chú', escHtml(d.comment));

    return `<div class="lamp-info-head" title="Kéo để di chuyển"><span class="lamp-info-grip">⠿</span><span class="lamp-info-dot bit-s-${st.toLowerCase()}"></span>
            <div class="lamp-info-title">${title}</div>
            <button type="button" class="lamp-info-x" title="Đóng (Esc)">✕</button></div>
        <div class="lamp-info-state li-${st.toLowerCase()}">${LAMP_LABEL[st]}</div>
        <div class="lamp-info-body">${rows}</div>`;
}

async function showLampInfo(el, div) {
    closeLampInfo();
    const pop = document.createElement('div');
    pop.className = 'lamp-info-pop';
    pop.innerHTML = '<div class="lamp-info-body">Đang tải…</div>';
    document.body.appendChild(pop);
    const me = { id: el.id, pop, timer: null, moved: !!lampPopPos, html: '' };
    lampPop = me;

    // Đặt cửa sổ theo px MÀN HÌNH (kẹp trong màn hình), tự đổi sang px CSS khi ghi style -> đúng vị trí dù "Kích thước giao diện" ≠ 100%
    const setPos = (left, top) => {
        const pr = pop.getBoundingClientRect();
        const L = Math.max(4, Math.min(left, window.innerWidth - pr.width - 4));
        const T = Math.max(4, Math.min(top, window.innerHeight - pr.height - 4));
        const k = cssPerScreenPx();
        pop.style.left = `${L * k}px`; pop.style.top = `${T * k}px`;
        return { left: L, top: T };
    };

    // Kéo thanh tiêu đề để di chuyển cửa sổ (gắn trên pop vì nội dung bên trong được dựng lại mỗi giây)
    pop.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || !e.target.closest('.lamp-info-head') || e.target.closest('.lamp-info-x')) return;
        e.preventDefault();
        const sx = e.clientX, sy = e.clientY, r0 = pop.getBoundingClientRect();
        pop.setPointerCapture(e.pointerId);
        pop.classList.add('dragging');
        const onEnd = () => {
            pop.classList.remove('dragging');
            pop.removeEventListener('pointermove', onMove);
            pop.removeEventListener('pointerup', onEnd);
            pop.removeEventListener('pointercancel', onEnd);
            pop.removeEventListener('lostpointercapture', onEnd);
        };
        function onMove(ev) {
            if (ev.buttons === 0) { onEnd(); return; }         // đã nhả chuột mà không nhận được pointerup: dừng kéo, không để cửa sổ bám theo chuột
            lampPopPos = setPos(r0.left + ev.clientX - sx, r0.top + ev.clientY - sy);   // nhớ vị trí (px màn hình): popup luôn ở đúng chỗ người dùng đã đặt
            me.moved = true;
        }
        pop.addEventListener('pointermove', onMove);
        pop.addEventListener('pointerup', onEnd);
        pop.addEventListener('pointercancel', onEnd);
        pop.addEventListener('lostpointercapture', onEnd);
    });

    const place = () => {       // dat canh den; tran man hinh thi chuyen sang ben trai / keo vao trong
        if (me.moved && lampPopPos) { setPos(lampPopPos.left, lampPopPos.top); return; }   // nguoi dung da keo di noi khac: giu dung cho do
        const r = div.getBoundingClientRect(), pr = pop.getBoundingClientRect();
        let left = r.right + 10;
        if (left + pr.width > window.innerWidth - 8) left = Math.max(8, r.left - pr.width - 10);
        setPos(left, Math.max(8, Math.min(r.top, window.innerHeight - pr.height - 8)));
    };
    const load = async () => {
        if (editMode) { closeLampInfo(); return; }
        try {
            const r = await fetch(`/api/layout/lamp-info/${encodeURIComponent(el.id)}?floor=${encodeURIComponent(CURRENT_FLOOR)}`, { cache: 'no-store' });
            if (!r.ok) throw new Error('lamp-info ' + r.status);
            const d = await r.json();
            if (lampPop !== me) return;
            const html = lampInfoHtml(d);
            if (html !== me.html) {
                me.html = html;
                pop.innerHTML = html;
                pop.querySelector('.lamp-info-x').addEventListener('click', closeLampInfo);
            }
        } catch {
            if (lampPop !== me) return;
            me.html = '';
            pop.innerHTML = '<div class="lamp-info-body">Không tải được thông tin vị trí đỗ.</div>';
        }
        place();
    };
    place();
    await load();
    if (lampPop === me) me.timer = setInterval(load, 1000);       // thong tin cap nhat truc tiep trong luc dang mo
}
document.addEventListener('mousedown', (e) => { if (lampPop && !e.target.closest('.lamp-info-pop')) closeLampInfo(); }, true);
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && lampPop) { e.stopImmediatePropagation(); closeLampInfo(); }      // Esc dong cua so truoc, khong thoat che do Chay
}, true);
window.addEventListener('floorchange', closeLampInfo);

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
// ---------------------------------------------------------------------------
// Text gan bien + Number (hien thi / nhap): System tag, Project tag, bien MS, bien MC
// ---------------------------------------------------------------------------
let valStates = {};            // ten (chu thuong) -> { v: 'chuoi', t: 'Integer'|'Real'|'Boolean'|'String' }; thieu = chua doc duoc / khong ton tai
let varCatalog = [];           // danh muc bien chon duoc: { name, source, type, writable, description }
const valLocal = {};           // ten -> { st, until }: gia tri vua nhap, giu tam de tranh nhap nhay
const VAL_POLL_MS = 300;
const valKey = (n) => String(n || '').toLowerCase();
const numModeOf = (el) => el.valueMode === 'Input' ? 'Input' : 'Display';
const numWriteTagOf = (el) => el.separateWriteTag ? el.writeTag : el.readTag;
const varInfo = (name) => varCatalog.find(v => valKey(v.name) === valKey(name));

async function loadVarCatalog() {
    try {
        const r = await fetch('/api/tasks/tags/variables', { cache: 'no-store' });
        if (r.ok) varCatalog = await r.json();
    } catch { /* giu danh muc cu */ }
}

// 1234 voi shift 2 -> "12.34" (xu ly bang chuoi de khong sai so voi so nguyen lon)
function shiftIntString(raw, shift) {
    const m = /^(-?)(\d+)$/.exec(raw);
    if (!m) return raw;
    let digits = m[2].replace(/^0+(?=\d)/, '');
    if (digits.length <= shift) digits = '0'.repeat(shift - digits.length + 1) + digits;
    return m[1] + digits.slice(0, digits.length - shift) + '.' + digits.slice(digits.length - shift);
}

// Gia tri da dinh dang (chua kem tien to / hau to) cua bien so; null = chua co gia tri
function formatNumberBody(el, st) {
    if (!st) return null;
    const raw = String(st.v ?? '').trim();
    const dec = el.decimals;
    const hasDec = dec !== null && dec !== undefined;
    if (st.t === 'Integer') {
        const shift = Math.max(0, Math.min(9, el.decimalShift || 0));
        if (!hasDec) return shift > 0 ? shiftIntString(raw, shift) : raw;
        const n = Number(raw) / Math.pow(10, shift);
        return Number.isFinite(n) ? n.toFixed(Math.max(0, Math.min(9, dec))) : raw;
    }
    if (st.t === 'Real') {
        const n = Number(raw);
        if (!Number.isFinite(n)) return raw;
        return hasDec ? n.toFixed(Math.max(0, Math.min(9, dec))) : String(n);
    }
    return raw;
}

function valueBodyText(el) {
    const st = el.readTag ? valStates[valKey(el.readTag)] : undefined;
    if (el.type === 'Number') return formatNumberBody(el, st);          // null = chua co
    if (!el.readTag) return el.content || 'Text';                        // Text khong gan bien: chu co dinh nhu truoc
    return st ? String(st.v ?? '') : null;
}

function valueFullText(el) {
    const body = valueBodyText(el);
    if (el.type === 'Text' && !el.readTag) return body;
    return (el.prefix || '') + (body === null ? '—' : body) + (el.suffix || '');
}

function numCanWrite(el) {
    const tag = numWriteTagOf(el);
    if (!tag) return false;
    const info = varInfo(tag);
    return info ? !!info.writable : true;       // chua tai duoc danh muc: de may chu tu choi neu sai
}

function renderValueNode(div, el) {
    const isNum = el.type === 'Number';
    const inputMode = isNum && numModeOf(el) === 'Input';
    const asField = inputMode && !editMode;                  // o nhap that chi o che do Chay (che do Build de keo-tha)
    div.classList.add(isNum ? 'as-number' : 'as-text');
    div.classList.toggle('num-input', inputMode);
    div.classList.toggle('num-readonly', asField && !numCanWrite(el));
    div.classList.toggle('val-missing', !!el.readTag && valueBodyText(el) === null);
    const jc = { left: 'flex-start', right: 'flex-end', center: 'center' }[el.textAlign];
    // .layout-el là flex dạng cột: Text căn ngang bằng align-items; Number (hàng) bằng justify-content
    if (isNum) { div.style.justifyContent = jc || 'center'; div.style.alignItems = 'center'; }
    else { div.style.alignItems = jc || ''; }
    div.style.textAlign = el.textAlign || '';

    const full = valueFullText(el);
    div.dataset.valSig = full + '|' + (asField ? 'i' : 'd');

    if (asField) {
        const body = valueBodyText(el);
        const ro = !numCanWrite(el);
        div.innerHTML = `${el.prefix ? `<span class="num-affix">${escHtml(el.prefix)}</span>` : ''}` +
            `<input class="num-field" type="text" inputmode="decimal" autocomplete="off" spellcheck="false" ${ro ? 'readonly' : ''} value="${escHtml(body ?? '')}" />` +
            `${el.suffix ? `<span class="num-affix">${escHtml(el.suffix)}</span>` : ''}`;
        const inp = div.querySelector('input');
        // Phim / chuot / cham trong o nhap KHONG duoc lan ra ngoai: phim ESC o ngoai se thoat che do Chay, chuot ngoai la keo-tha doi tuong...
        ['keydown', 'keyup', 'keypress', 'mousedown', 'mouseup', 'click', 'dblclick', 'touchstart', 'pointerdown']
            .forEach(ev => inp.addEventListener(ev, (e) => e.stopPropagation()));
        inp.addEventListener('beforeinput', (e) => { if (e.data && /[^0-9.,\-]/.test(e.data)) e.preventDefault(); });   // chi so, dau am, dau cham/phay
        inp.addEventListener('focus', () => inp.select());
        inp.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); inp.blur(); }
            else if (e.key === 'Escape') { inp.value = valueBodyText(el) ?? ''; inp.blur(); }
        });
        inp.addEventListener('change', () => commitNumberInput(el, div, inp));
        if (ro) inp.title = 'Biến này chỉ đọc (System tag / biến MS / biến MC) nên không nhập được. Hãy chọn Project tag.';
    } else {
        div.textContent = full;
    }

    const info = !el.readTag ? (isNum ? 'Chưa chọn biến' : 'Text cố định')
        : valueBodyText(el) === null ? `Biến "${el.readTag}" không tồn tại hoặc chưa có giá trị`
        : `${el.readTag} = ${valStates[valKey(el.readTag)]?.v}`;
    div.title = (el.comment ? el.comment + ' — ' : '') + info;
}

// Nhap so -> doi nguoc "doi dau cham" -> ghi ve Project tag
async function commitNumberInput(el, div, inp) {
    const revert = () => { inp.value = valueBodyText(el) ?? ''; };
    const tag = numWriteTagOf(el);
    if (!tag) { bitFlashError(el.id, 'Chưa chọn biến để ghi'); revert(); return; }
    const txt = inp.value.trim().replace(',', '.');
    if (txt === '') { revert(); return; }
    let v = Number(txt);
    if (!Number.isFinite(v)) { bitFlashError(el.id, 'Giá trị không phải là số'); revert(); return; }
    if (el.minValue !== null && el.minValue !== undefined && v < el.minValue) v = el.minValue;
    if (el.maxValue !== null && el.maxValue !== undefined && v > el.maxValue) v = el.maxValue;

    const type = (varInfo(tag)?.type) || valStates[valKey(tag)]?.t || 'Real';
    let raw;
    if (type === 'Integer') raw = String(Math.round(v * Math.pow(10, Math.max(0, Math.min(9, el.decimalShift || 0)))));
    else if (type === 'Boolean') raw = v ? '1' : '0';
    else raw = String(v);

    const k = valKey(tag);
    const st = { v: raw, t: type };
    valStates[k] = st;                                       // hien ngay, khong cho vong doc ke tiep
    valLocal[k] = { st, until: Date.now() + 800 };
    try {
        const r = await sendBit(tag, raw);
        if (!r.ok) {
            const err = await r.json().catch(() => ({}));
            delete valLocal[k];
            bitFlashError(el.id, err.message || `Không ghi được ${tag}`);
            revert();
            return;
        }
        valLocal[k] = { st, until: Date.now() + 300 };
    } catch {
        delete valLocal[k];
        bitFlashError(el.id, `Không ghi được ${tag} (mất kết nối)`);
        revert();
        return;
    }
    updateValueNodes(true);
}

function isValueEl(e) { return e.type === 'Number' || (e.type === 'Text' && e.readTag); }

// Chi ve lai doi tuong nao doi gia tri (khong thay node; khong dong vao o nhap dang go)
function updateValueNodes(force) {
    elements.forEach(el => {
        if (!isValueEl(el)) return;
        const node = canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
        if (!node) return;
        if (node.contains(document.activeElement) && !force) return;
        const asField = el.type === 'Number' && numModeOf(el) === 'Input' && !editMode;
        if (node.dataset.valSig === valueFullText(el) + '|' + (asField ? 'i' : 'd')) return;
        renderValueNode(node, el);
    });
}

// ===========================================================================
// HIEU UNG DONG gan bien (Animation): Hien thi / Vi tri / Mau sac theo gia tri bien. CHI chay o che do Chay.
//   el.animation = { visibilityTag, visibilityInvert,
//                    horizontal / vertical: { tag, valueMin, valueMax, posMin, posMax, reference: start|center|end },
//                    sliderEnabled, sliderDisableTag,
//                    colors: { tag, fill, border, text, stops: [{ limit, color, blink: none|slow|fast }] } }
// ===========================================================================
let animTick = 0;                                  // nhip 200 ms de nhap nhay
const animHas = (el) => { const a = el.animation; return !!a && !!(a.visibilityTag || a.horizontal?.tag || a.vertical?.tag || a.colors?.tag); };
const isAnimVar = (v) => v.type === 'Integer' || v.type === 'Real' || v.type === 'Boolean';

function animTagsOf(el) {
    const a = el.animation;
    if (!a) return [];
    return [a.visibilityTag, a.horizontal?.tag, a.vertical?.tag, a.colors?.tag, a.sliderEnabled ? a.sliderDisableTag : null].filter(Boolean);
}

// Gia tri so cua bien (Boolean: 1/0); null = chua doc duoc / khong ton tai / khong phai so
function animValue(tag) {
    const st = valStates[valKey(tag)];
    if (!st) return null;
    const raw = String(st.v ?? '').trim();
    if (st.t === 'Boolean' || /^(true|false)$/i.test(raw)) return /^(1|true|on)$/i.test(raw) ? 1 : 0;
    const n = parseFloat(raw.replace(',', '.'));
    return Number.isFinite(n) ? n : null;
}

// Gia tri bien -> vi tri (%) cua canh trai / tren cua doi tuong
function animAxisPos(ax, v, size) {
    let r = (v - ax.valueMin) / ((ax.valueMax - ax.valueMin) || 1);
    r = Math.max(0, Math.min(1, r));
    const pos = ax.posMin + r * (ax.posMax - ax.posMin);
    return ax.reference === 'center' ? pos - size / 2 : ax.reference === 'end' ? pos - size : pos;
}
// Nguoc lai: vi tri diem tham chieu (%) -> gia tri bien (dung cho Slider)
function animPosToValue(ax, refPos) {
    let r = (refPos - ax.posMin) / ((ax.posMax - ax.posMin) || 1);
    r = Math.max(0, Math.min(1, r));
    return ax.valueMin + r * (ax.valueMax - ax.valueMin);
}

// "By Limit": gia tri >= nguong lon nhat thoa man -> mau cua nguong do; nho hon moi nguong -> khong doi mau
function animStopFor(c, v) {
    let best = null;
    (c.stops || []).forEach(s => { if (v >= s.limit && (!best || s.limit >= best.limit)) best = s; });
    return best;
}
function animBlinkOn(stop) {
    if (!stop || !stop.blink || stop.blink === 'none') return true;
    return stop.blink === 'fast' ? Math.floor(animTick / 2) % 2 === 0 : Math.floor(animTick / 5) % 2 === 0;
}

// Ghi de style (!important) nhung nho gia tri goc de khoi phuc khi het hieu ung / pha tat cua nhap nhay
function animSet(root, node, prop, value) {
    node._ab = node._ab || {};
    if (!(prop in node._ab)) node._ab[prop] = [node.style.getPropertyValue(prop), node.style.getPropertyPriority(prop)];
    node.style.setProperty(prop, value, 'important');
    (root._abNodes = root._abNodes || new Set()).add(node);
}
function animRestore(root) {
    (root._abNodes || []).forEach(n => {
        Object.entries(n._ab || {}).forEach(([p, [v, pr]]) => { if (v) n.style.setProperty(p, v, pr); else n.style.removeProperty(p); });
        n._ab = null;
    });
    root._abNodes = null;
}

function animPaint(div, el, c, color) {
    if (el.type === 'Line') {                      // duong thang: doi mau net ve + dau mui ten
        div.querySelectorAll('.line-stroke').forEach(n => animSet(div, n, 'border-top-color', color));
        div.querySelectorAll('.line-head.end').forEach(n => animSet(div, n, 'border-left-color', color));
        div.querySelectorAll('.line-head.start').forEach(n => animSet(div, n, 'border-right-color', color));
        return;
    }
    if (c.fill) { animSet(div, div, 'background-color', color); animSet(div, div, 'background-image', 'none'); }
    if (c.border) {
        const cs = getComputedStyle(div);
        if (cs.borderTopStyle === 'none' || parseFloat(cs.borderTopWidth) === 0) {
            animSet(div, div, 'border-style', 'solid');
            animSet(div, div, 'border-width', 'max(2px, calc(2 * var(--px)))');
        }
        animSet(div, div, 'border-color', color);
    }
    if (c.text) animSet(div, div, 'color', color);
}

function applyAnimToNode(div, el) {
    if (!div) return;
    animRestore(div);
    if (editMode || !el.animation) return;
    const a = el.animation;
    if (a.visibilityTag) {
        const v = animValue(a.visibilityTag);
        if (v !== null) div.style.visibility = ((v !== 0) !== !!a.visibilityInvert) ? '' : 'hidden';
    }
    if (a.horizontal?.tag) {
        const v = animValue(a.horizontal.tag);
        if (v !== null) div.style.left = `${animAxisPos(a.horizontal, v, el.width)}%`;
    }
    if (a.vertical?.tag) {
        const v = animValue(a.vertical.tag);
        if (v !== null) div.style.top = `${animAxisPos(a.vertical, v, el.height)}%`;
    }
    if (a.colors?.tag) {
        const v = animValue(a.colors.tag);
        const stop = v === null ? null : animStopFor(a.colors, v);
        if (stop && animBlinkOn(stop)) animPaint(div, el, a.colors, stop.color);
    }
    div.classList.toggle('anim-slider', !!a.sliderEnabled);
}

function updateAnimNodes() {
    if (editMode) return;
    elements.forEach(el => { if (animHas(el)) applyAnimToNode(canvas.querySelector(`.layout-el[data-id="${el.id}"]`), el); });
}

// Nhap nhay: chi ve lai doi tuong co nguong nhap nhay
setInterval(() => {
    animTick++;
    if (editMode) return;
    elements.forEach(el => {
        const c = el.animation?.colors;
        if (c && (c.stops || []).some(s => s.blink && s.blink !== 'none')) applyAnimToNode(canvas.querySelector(`.layout-el[data-id="${el.id}"]`), el);
    });
}, 200);

// ---- Slider / Gestures: keo doi tuong o che do Chay de ghi gia tri nguoc ve bien (Project tag) ----
let sliderDrag = null;
const animWriteTimer = {}, animWriteRaw = {};

function animWriteValue(el, tag, v) {
    const type = varInfo(tag)?.type || valStates[valKey(tag)]?.t || 'Real';
    if (type === 'Boolean') return;
    const raw = type === 'Integer' ? String(Math.round(v)) : String(Math.round(v * 1e4) / 1e4);
    const k = valKey(tag);
    const st = { v: raw, t: type };
    valStates[k] = st;                                        // hien ngay, khong cho vong doc ke tiep
    valLocal[k] = { st, until: Date.now() + 800 };
    updateAnimNodes();
    animWriteRaw[k] = raw;
    if (animWriteTimer[k]) return;                            // gop: toi da ~12 lan ghi / giay
    animWriteTimer[k] = setTimeout(async () => {
        animWriteTimer[k] = null;
        const val = animWriteRaw[k];
        try {
            const r = await sendBit(tag, val);
            if (!r.ok) { const err = await r.json().catch(() => ({})); bitFlashError(el.id, err.message || `Không ghi được ${tag}`); }
            else valLocal[k] = { st: { v: val, t: type }, until: Date.now() + 300 };
        } catch { bitFlashError(el.id, `Không ghi được ${tag} (mất kết nối)`); }
    }, 80);
}

canvas.addEventListener('pointerdown', (e) => {
    if (editMode || e.button !== 0) return;
    const node = e.target.closest('.layout-el');
    if (!node) return;
    const el = elements.find(x => x.id === node.dataset.id);
    const a = el?.animation;
    if (!a?.sliderEnabled) return;
    if (a.sliderDisableTag && animValue(a.sliderDisableTag)) return;          // dang bi vo hieu hoa
    const axes = [['horizontal', el.width], ['vertical', el.height]].filter(([k]) => a[k]?.tag);
    if (!axes.length) return;
    const bad = axes.find(([k]) => !varInfo(a[k].tag)?.writable);
    if (bad) { bitFlashError(el.id, `Biến "${a[bad[0]].tag}" không ghi được (chỉ Project tag)`); return; }

    const rect = canvas.getBoundingClientRect();
    const cx = (e.clientX - rect.left) / rect.width * 100, cy = (e.clientY - rect.top) / rect.height * 100;
    sliderDrag = {
        el, rect, node,
        axes: axes.map(([k, size]) => {
            const ax = a[k];
            const start = parseFloat(node.style[k === 'horizontal' ? 'left' : 'top']) || 0;
            const ref = start + (ax.reference === 'center' ? size / 2 : ax.reference === 'end' ? size : 0);
            return { k, ax, off: (k === 'horizontal' ? cx : cy) - ref };      // giu nguyen do lech luc bat dau keo
        })
    };
    try { node.setPointerCapture(e.pointerId); } catch { /* bo qua */ }
    e.preventDefault();
});
canvas.addEventListener('pointermove', (e) => {
    if (!sliderDrag) return;
    const { rect, el, axes } = sliderDrag;
    const cx = (e.clientX - rect.left) / rect.width * 100, cy = (e.clientY - rect.top) / rect.height * 100;
    axes.forEach(({ k, ax, off }) => animWriteValue(el, ax.tag, animPosToValue(ax, (k === 'horizontal' ? cx : cy) - off)));
});
const endSliderDrag = () => { sliderDrag = null; };
canvas.addEventListener('pointerup', endSliderDrag);
canvas.addEventListener('pointercancel', endSliderDrag);

async function pollValues() {
    try {
        if (!document.hidden) {
            const names = [...new Set([
                ...elements.filter(isValueEl).filter(e => e.readTag).map(e => e.readTag),
                ...(editMode ? [] : elements.flatMap(animTagsOf))      // bien cua hieu ung dong (chi o che do Chay)
            ])];
            if (names.length) {
                const next = {};
                for (let i = 0; i < names.length; i += 64) {
                    const r = await fetch(`/api/tasks/tags/variable-values?names=${encodeURIComponent(names.slice(i, i + 64).join(','))}`, { cache: 'no-store' });
                    if (!r.ok) throw new Error('variable-values ' + r.status);
                    const data = await r.json();
                    Object.entries(data.values || {}).forEach(([k, o]) => { next[valKey(k)] = { v: o.v, t: o.t }; });
                }
                const now = Date.now();
                Object.entries(valLocal).forEach(([k, l]) => { if (l.until > now) next[k] = l.st; else delete valLocal[k]; });
                valStates = next;
                updateValueNodes();
                updateAnimNodes();
            }
        }
    } catch { /* mat ket noi tam thoi: giu gia tri cu, lan sau thu lai */ }
    setTimeout(pollValues, VAL_POLL_MS);
}

// ---- Panel thuoc tinh: chon bien ----
function varSelectHtml(id, selected, filter, noneLabel) {
    const groups = { System: [], Project: [], MS: [], MC: [] };
    varCatalog.filter(filter).forEach(v => (groups[v.source] || (groups[v.source] = [])).push(v));
    const labels = { System: 'System tags', Project: 'Project tags', MS: 'Biến MS (cảm biến)', MC: 'Biến MC (camera)' };
    let html = `<option value="">${noneLabel}</option>`;
    let known = !selected;
    Object.keys(groups).forEach(g => {
        if (!groups[g].length) return;
        html += `<optgroup label="${labels[g] || g}">` + groups[g].map(v => {
            const sel = valKey(v.name) === valKey(selected);
            if (sel) known = true;
            return `<option value="${escHtml(v.name)}" ${sel ? 'selected' : ''}>${escHtml(v.name)} · ${v.type}${v.description ? ' — ' + escHtml(v.description) : ''}</option>`;
        }).join('') + '</optgroup>';
    });
    if (!known) html += `<option value="${escHtml(selected)}" selected>${escHtml(selected)} (không còn tồn tại / không phù hợp)</option>`;
    return `<select id="${id}">${html}</select>`;
}

const isNumericVar = (v) => v.type === 'Integer' || v.type === 'Real';

function alignSelectHtml(el, def) {
    const cur = el.textAlign || def;
    const o = (v, text) => `<option value="${v}" ${cur === v ? 'selected' : ''}>${text}</option>`;
    return `<select id="propAlign">${o('left', 'Trái')}${o('center', 'Giữa')}${o('right', 'Phải')}</select>`;
}

function fontFieldsHtml(el, defColor = '#e6ebf5') {
    return `
        <div class="prop-grid-2">
            <div class="prop-field"><label>Màu chữ</label>
                <input id="propTextColor" type="color" value="${escHtml(el.textColor || defColor)}" /></div>
            <div class="prop-field"><label>Cỡ chữ (px)</label>
                <input id="propFontSize" type="number" min="1" max="200" step="1" placeholder="mặc định" value="${el.fontSize ?? ''}" /></div>
        </div>`;
}

function applyFontFromForm(el, defColor = '#e6ebf5') {
    const tc = document.getElementById('propTextColor').value;
    el.textColor = (tc.toLowerCase() !== defColor || el.textColor) ? tc : null;
    const fs = parseFloat(document.getElementById('propFontSize').value);
    el.fontSize = Number.isFinite(fs) && fs > 0 ? fs : null;
}

const clampInt = (v, lo, hi) => Math.max(lo, Math.min(hi, parseInt(v, 10) || 0));
const numOrNull = (id) => { const x = parseFloat(document.getElementById(id).value); return Number.isFinite(x) ? x : null; };


// ---------------------------------------------------------------------------
// Ngay gio (DateTime): dong ho hien thi ngay / gio hien tai, chay lien tuc (ca Build lan Chay).
// Tuy chon (el.dtOptions): show, dateFormat, timeFormat, showSeconds, weekday, layout, timeFirst, separator, language, timeZone, customPattern.
// Mau dinh dang: yyyy yy | MMMM MMM MM M | dddd ddd dd d | HH H hh h | mm m | ss s | fff | tt t ; chu trong 'nhay don' giu nguyen.
// ---------------------------------------------------------------------------
const DT_DEFAULTS = {
    show: 'DateTime', dateFormat: 'dd/MM/yyyy', timeFormat: '24h', showSeconds: true, weekday: 'none',
    layout: 'OneLine', timeFirst: false, separator: '  ', language: 'vi', timeZone: 'local', customPattern: null
};
const DT_NAMES = {
    vi: {
        weekLong: ['Chủ nhật', 'Thứ hai', 'Thứ ba', 'Thứ tư', 'Thứ năm', 'Thứ sáu', 'Thứ bảy'],
        weekShort: ['CN', 'T2', 'T3', 'T4', 'T5', 'T6', 'T7'],
        monLong: Array.from({ length: 12 }, (_, i) => `tháng ${i + 1}`),
        monShort: Array.from({ length: 12 }, (_, i) => `Th${i + 1}`),
        am: 'SA', pm: 'CH'
    },
    en: {
        weekLong: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
        weekShort: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
        monLong: ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'],
        monShort: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
        am: 'AM', pm: 'PM'
    }
};
const DT_DATE_PRESETS = ['dd/MM/yyyy', 'dd-MM-yyyy', 'dd.MM.yyyy', 'dd/MM/yy', 'd/M/yyyy', 'MM/dd/yyyy', 'yyyy-MM-dd', 'yyyy/MM/dd', 'dd MMM yyyy', 'd MMMM yyyy', 'MMMM d, yyyy'];
const DT_ZONES = [['local', 'Giờ máy tính (mặc định)'], ['utc', 'UTC'], ['+07:00', 'UTC+07:00 — Việt Nam, Thái Lan']]
    .concat(['-12:00', '-11:00', '-10:00', '-09:00', '-08:00', '-07:00', '-06:00', '-05:00', '-04:00', '-03:00', '-02:00', '-01:00',
        '+01:00', '+02:00', '+03:00', '+03:30', '+04:00', '+04:30', '+05:00', '+05:30', '+05:45', '+06:00', '+06:30',
        '+08:00', '+09:00', '+09:30', '+10:00', '+11:00', '+12:00', '+13:00', '+14:00'].map(z => [z, `UTC${z}`]));

const dtOpts = (el) => Object.assign({}, DT_DEFAULTS, el?.dtOptions || {});

// Cac thanh phan ngay / gio theo muoi gio da chon (de doc bang getUTC*)
function dtNowParts(tz, now = new Date()) {
    let d;
    if (tz === 'local') d = { y: now.getFullYear(), M: now.getMonth() + 1, d: now.getDate(), H: now.getHours(), m: now.getMinutes(), s: now.getSeconds(), ms: now.getMilliseconds(), dow: now.getDay() };
    else {
        let off = 0;
        const m = /^([+-])(\d\d):(\d\d)$/.exec(tz || '');
        if (m) off = (m[1] === '-' ? -1 : 1) * (parseInt(m[2], 10) * 60 + parseInt(m[3], 10)) * 60000;
        const u = new Date(now.getTime() + off);
        d = { y: u.getUTCFullYear(), M: u.getUTCMonth() + 1, d: u.getUTCDate(), H: u.getUTCHours(), m: u.getUTCMinutes(), s: u.getUTCSeconds(), ms: u.getUTCMilliseconds(), dow: u.getUTCDay() };
    }
    return d;
}

function dtFormat(pattern, p, lang) {
    const nm = DT_NAMES[lang] || DT_NAMES.vi;
    const pad = (n, w = 2) => String(n).padStart(w, '0');
    const h12 = p.H % 12 === 0 ? 12 : p.H % 12;
    let out = '';
    for (let i = 0; i < pattern.length;) {
        const c = pattern[i];
        if (c === "'") {                                  // chu nguyen van trong 'nhay don'
            const j = pattern.indexOf("'", i + 1);
            if (j < 0) { out += pattern.slice(i + 1); break; }
            out += j === i + 1 ? "'" : pattern.slice(i + 1, j);
            i = j + 1;
            continue;
        }
        if (!'yMdHhmsft'.includes(c)) { out += c; i++; continue; }
        let n = 1;
        while (pattern[i + n] === c) n++;
        i += n;
        switch (c) {
            case 'y': out += n === 2 ? pad(p.y % 100) : n === 1 ? String(p.y) : pad(p.y, 4); break;
            case 'M': out += n >= 4 ? nm.monLong[p.M - 1] : n === 3 ? nm.monShort[p.M - 1] : n === 2 ? pad(p.M) : String(p.M); break;
            case 'd': out += n >= 4 ? nm.weekLong[p.dow] : n === 3 ? nm.weekShort[p.dow] : n === 2 ? pad(p.d) : String(p.d); break;
            case 'H': out += n >= 2 ? pad(p.H) : String(p.H); break;
            case 'h': out += n >= 2 ? pad(h12) : String(h12); break;
            case 'm': out += n >= 2 ? pad(p.m) : String(p.m); break;
            case 's': out += n >= 2 ? pad(p.s) : String(p.s); break;
            case 'f': out += pad(p.ms, 3).slice(0, Math.min(n, 3)); break;
            case 't': { const t = p.H < 12 ? nm.am : nm.pm; out += n >= 2 ? t : t[0]; break; }
        }
    }
    return out;
}

// Danh sach dong chu cua doi tuong tai thoi diem hien tai (1 hoac 2 dong)
function dtLines(el, now = new Date()) {
    const o = dtOpts(el);
    const p = dtNowParts(o.timeZone, now);
    const f = (pat) => dtFormat(pat, p, o.language);
    if (o.customPattern) return [f(o.customPattern)];
    const wk = o.weekday === 'long' ? 'dddd, ' : o.weekday === 'short' ? 'ddd, ' : '';
    const datePat = wk + (o.dateFormat || DT_DEFAULTS.dateFormat);
    const sec = o.showSeconds ? ':ss' : '';
    const timePat = o.timeFormat === '12h' ? `hh:mm${sec} tt` : `HH:mm${sec}`;
    if (o.show === 'Date') return [f(datePat)];
    if (o.show === 'Time') return [f(timePat)];
    const parts = o.timeFirst ? [f(timePat), f(datePat)] : [f(datePat), f(timePat)];
    return o.layout === 'TwoLines' ? parts : [parts.join(o.separator ?? '  ')];
}

function renderDateTimeNode(div, el) {
    div.classList.add('as-datetime');
    const jc = { left: 'flex-start', right: 'flex-end', center: 'center' }[el.textAlign];
    div.style.alignItems = jc || 'center';
    div.style.textAlign = el.textAlign || 'center';
    const lines = dtLines(el);
    const last = lines.length - 1;
    div.innerHTML = lines.map((t, i) =>
        `<span class="dt-line">${i === 0 && el.prefix ? escHtml(el.prefix) : ''}${escHtml(t)}${i === last && el.suffix ? escHtml(el.suffix) : ''}</span>`).join('');
    div.dataset.dtSig = lines.join('|');
    div.title = el.comment || '';
}

// Cap nhat dong ho (4 lan / giay; chi sua node khi chu doi)
function tickDateTimes() {
    if (!elements.some(e => e.type === 'DateTime')) return;
    const now = new Date();
    elements.forEach(el => {
        if (el.type !== 'DateTime') return;
        const node = canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
        if (!node) return;
        const lines = dtLines(el, now);
        if (node.dataset.dtSig === lines.join('|')) return;
        const spans = node.querySelectorAll('.dt-line');
        if (spans.length !== lines.length) { renderDateTimeNode(node, el); return; }
        const last = lines.length - 1;
        lines.forEach((t, i) => { spans[i].textContent = (i === 0 && el.prefix ? el.prefix : '') + t + (i === last && el.suffix ? el.suffix : ''); });
        node.dataset.dtSig = lines.join('|');
    });
}
setInterval(tickDateTimes, 250);

async function renderDateTimeProps(el) {
    const o = dtOpts(el);
    const sel = (id, cur, opts) => `<select id="${id}">${opts.map(([v, t]) => `<option value="${escHtml(v)}" ${String(cur) === String(v) ? 'selected' : ''}>${escHtml(t)}</option>`).join('')}</select>`;
    const now = new Date();
    const ex = (pat) => dtFormat(pat, dtNowParts(o.timeZone, now), o.language);
    const datePresets = DT_DATE_PRESETS.includes(o.dateFormat) ? DT_DATE_PRESETS : [o.dateFormat, ...DT_DATE_PRESETS];
    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'show', label: 'Hiển thị', html:
            ppCard('Nội dung hiển thị', `
                <div class="prop-field"><label>Nội dung</label>
                    ${sel('dtShow', o.show, [['DateTime', 'Ngày + giờ'], ['Date', 'Chỉ ngày'], ['Time', 'Chỉ giờ']])}</div>
                <div id="dtDateBox">
                    <div class="prop-field"><label>Định dạng ngày</label>
                        ${sel('dtDateFormat', o.dateFormat, datePresets.map(p => [p, `${p}   →   ${ex(p)}`]))}</div>
                    <div class="prop-field"><label>Thứ trong tuần (đứng trước ngày)</label>
                        ${sel('dtWeekday', o.weekday, [['none', 'Không hiện'], ['long', 'Đầy đủ (Thứ tư / Wednesday)'], ['short', 'Viết tắt (T4 / Wed)']])}</div>
                </div>
                <div id="dtTimeBox">
                    <div class="prop-grid-2">
                        <div class="prop-field"><label>Định dạng giờ</label>
                            ${sel('dtTimeFormat', o.timeFormat, [['24h', '24 giờ (13:45)'], ['12h', '12 giờ (01:45 CH)']])}</div>
                        <div class="prop-field"><label>&nbsp;</label>
                            <label class="checkbox-label"><input type="checkbox" id="dtSeconds" ${o.showSeconds ? 'checked' : ''} /> Hiện giây</label></div>
                    </div>
                </div>`)
            + ppCard('Bố cục, ngôn ngữ & múi giờ', `
                <div id="dtBothBox">
                    <div class="prop-grid-2">
                        <div class="prop-field"><label>Bố cục</label>
                            ${sel('dtLayout', o.layout, [['OneLine', '1 dòng'], ['TwoLines', '2 dòng (trên / dưới)']])}</div>
                        <div class="prop-field"><label>Thứ tự</label>
                            ${sel('dtOrder', o.timeFirst ? '1' : '0', [['0', 'Ngày trước, giờ sau'], ['1', 'Giờ trước, ngày sau']])}</div>
                    </div>
                    <div class="prop-field" id="dtSepBox"><label>Chữ ngăn cách ngày và giờ (khi 1 dòng)</label>
                        <input id="dtSeparator" type="text" maxlength="8" value="${escHtml(o.separator)}" /></div>
                </div>
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Ngôn ngữ</label>
                        ${sel('dtLang', o.language, [['vi', 'Tiếng Việt'], ['en', 'English']])}</div>
                    <div class="prop-field"><label>Múi giờ</label>
                        ${sel('dtZone', o.timeZone, DT_ZONES)}</div>
                </div>`)
            + ppCard('Mẫu tuỳ chọn (nâng cao)', `
                <div class="prop-field"><label>Mẫu thay cho các lựa chọn ngày / giờ ở trên</label>
                    <input id="dtCustom" type="text" maxlength="80" placeholder="vd: dddd, dd/MM/yyyy HH:mm:ss" value="${escHtml(o.customPattern || '')}" /></div>
                ${ppHint("Ký tự mẫu: <b>yyyy</b> năm · <b>MM / MMM / MMMM</b> tháng · <b>dd</b> ngày · <b>ddd / dddd</b> thứ · <b>HH</b> giờ 24 · <b>hh</b> giờ 12 · <b>mm</b> phút · <b>ss</b> giây · <b>fff</b> mili giây · <b>tt</b> SA/CH (AM/PM). Chữ nguyên văn đặt trong 'nháy đơn'.")}`, 'wide') },
        { id: 'font', label: 'Chữ', html:
            ppCard('Chữ kèm theo', `
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Chữ đứng trước</label>
                        <input id="propPrefix" type="text" maxlength="32" value="${escHtml(el.prefix)}" /></div>
                    <div class="prop-field"><label>Chữ đứng sau</label>
                        <input id="propSuffix" type="text" maxlength="32" value="${escHtml(el.suffix)}" /></div>
                </div>`)
            + ppCard('Định dạng chữ', `
                <div class="prop-field"><label>Căn lề</label>${alignSelectHtml(el, 'center')}</div>
                ${fontFieldsHtml(el)}
                <div class="prop-inline">
                    <label class="checkbox-label"><input type="checkbox" id="dtBold" ${el.fontBold === true ? 'checked' : ''} /> In đậm</label>
                    <label class="checkbox-label"><input type="checkbox" id="dtItalic" ${el.fontItalic ? 'checked' : ''} /> In nghiêng</label>
                </div>`)
            + ppCard('Ghi chú', `
                <div class="prop-field"><label>Ghi chú (chỉ để nhận biết, không hiển thị)</label>
                    <input id="propComment" type="text" maxlength="200" value="${escHtml(el.comment)}" /></div>`, 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    // Chi hien cac o co y nghia theo "Nội dung" / "Bố cục" / mau tuy chon
    const $d = (id) => document.getElementById(id);
    const syncVisibility = () => {
        const custom = $d('dtCustom').value.trim() !== '';
        const show = $d('dtShow').value;
        $d('dtShow').disabled = custom;
        $d('dtDateBox').hidden = custom || show === 'Time';
        $d('dtTimeBox').hidden = custom || show === 'Date';
        $d('dtBothBox').hidden = custom || show !== 'DateTime';
        $d('dtSepBox').hidden = $d('dtLayout').value === 'TwoLines';
    };
    ['dtShow', 'dtLayout'].forEach(id => $d(id).addEventListener('change', syncVisibility));
    $d('dtCustom').addEventListener('input', syncVisibility);
    syncVisibility();

    const applyForm = (el) => {
        el.dtOptions = {
            show: $d('dtShow').value, dateFormat: $d('dtDateFormat').value, timeFormat: $d('dtTimeFormat').value,
            showSeconds: $d('dtSeconds').checked, weekday: $d('dtWeekday').value, layout: $d('dtLayout').value,
            timeFirst: $d('dtOrder').value === '1', separator: $d('dtSeparator').value,
            language: $d('dtLang').value, timeZone: $d('dtZone').value, customPattern: $d('dtCustom').value.trim() || null
        };
        el.prefix = $d('propPrefix').value || null;
        el.suffix = $d('propSuffix').value || null;
        el.textAlign = $d('propAlign').value || null;
        el.comment = $d('propComment').value || null;
        el.fontBold = $d('dtBold').checked ? true : null;
        el.fontItalic = $d('dtItalic').checked ? true : null;
        applyFontFromForm(el);
        applyStyleFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    $d('savePropsBtn').addEventListener('click', async () => { applyForm(el); await persistAndRefresh(el); });
    $d('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
    fitPropsDialog();
}

async function renderTextProps(el) {
    await loadVarCatalog();
    if (selectedId !== el.id) return;
    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'content', label: 'Nội dung', html:
            ppCard('Biến hiển thị (tuỳ chọn)', `
                <div class="prop-field"><label>Biến — String / char / số… (System, Project, MS, MC)</label>
                    ${varSelectHtml('propReadTag', el.readTag, () => true, '— không gắn biến (chữ cố định) —')}</div>
                ${ppHint('Gắn biến thì Text hiển thị giá trị hiện tại của biến (cập nhật liên tục). Biến dạng String/char hiển thị nguyên văn; biến số hiển thị dạng chữ. "Nội dung" chỉ dùng khi không gắn biến.')}`)
            + ppCard('Nội dung & chữ kèm theo', `
                <div class="prop-field"><label>Nội dung (khi không gắn biến)</label>
                    <textarea id="propContent" rows="2">${escHtml(el.content)}</textarea></div>
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Chữ đứng trước giá trị</label>
                        <input id="propPrefix" type="text" maxlength="32" value="${escHtml(el.prefix)}" /></div>
                    <div class="prop-field"><label>Chữ đứng sau giá trị</label>
                        <input id="propSuffix" type="text" maxlength="32" value="${escHtml(el.suffix)}" /></div>
                </div>`) },
        { id: 'font', label: 'Chữ', html: ppCard('Định dạng chữ', `
                <div class="prop-field"><label>Căn lề</label>${alignSelectHtml(el, 'center')}</div>
                ${fontFieldsHtml(el)}`)
            + ppCard('Ghi chú', `
                <div class="prop-field"><label>Ghi chú (chỉ để nhận biết, không hiển thị)</label>
                    <input id="propComment" type="text" maxlength="200" value="${escHtml(el.comment)}" /></div>`) },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    const applyForm = (el) => {
        el.readTag = document.getElementById('propReadTag').value || null;
        el.content = document.getElementById('propContent').value;
        el.prefix = document.getElementById('propPrefix').value || null;
        el.suffix = document.getElementById('propSuffix').value || null;
        el.textAlign = document.getElementById('propAlign').value || null;
        el.comment = document.getElementById('propComment').value || null;
        applyFontFromForm(el);
        applyStyleFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    document.getElementById('savePropsBtn').addEventListener('click', async () => { applyForm(el); await persistAndRefresh(el); });
    document.getElementById('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
    fitPropsDialog();
}

async function renderNumberProps(el) {
    await loadVarCatalog();
    if (selectedId !== el.id) return;
    const mode = numModeOf(el);
    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'var', label: 'Biến & chế độ', html:
            ppCard('Biến', `
                <div class="prop-field"><label>Biến số (Integer / Real — System, Project, MS, MC)</label>
                    ${varSelectHtml('propReadTag', el.readTag, isNumericVar, '— chọn biến —')}</div>
                <div class="prop-field"><label>Chế độ</label>
                    <select id="propValueMode">
                        <option value="Display" ${mode === 'Display' ? 'selected' : ''}>Chỉ hiển thị</option>
                        <option value="Input" ${mode === 'Input' ? 'selected' : ''}>Cho phép nhập (ở chế độ Chạy)</option>
                    </select></div>`)
            + `<div id="numInputBox" class="pp-contents" ${mode === 'Input' ? '' : 'hidden'}>` + ppCard('Nhập giá trị', `
                <label class="checkbox-label" style="margin-bottom:10px;">
                    <input type="checkbox" id="propSepWrite" ${el.separateWriteTag ? 'checked' : ''} />
                    Ghi vào biến khác (không ghi vào chính biến hiển thị)
                </label>
                <div class="prop-field" id="numWriteBox" ${el.separateWriteTag ? '' : 'hidden'}><label>Biến để ghi (chỉ Project tag)</label>
                    ${varSelectHtml('propWriteTag', el.writeTag, v => v.writable && isNumericVar(v), '— chọn biến —')}</div>
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Nhỏ nhất cho phép</label>
                        <input id="propMin" type="number" step="any" placeholder="không giới hạn" value="${el.minValue ?? ''}" /></div>
                    <div class="prop-field"><label>Lớn nhất cho phép</label>
                        <input id="propMax" type="number" step="any" placeholder="không giới hạn" value="${el.maxValue ?? ''}" /></div>
                </div>
                <p class="hint small pp-hint" id="numWriteHint">Chỉ Project tag mới ghi được; System tag, biến MS, MC chỉ đọc. Giới hạn tính theo giá trị đang HIỂN THỊ (sau khi dời dấu chấm).</p>`) + `</div>` },
        { id: 'format', label: 'Định dạng', html:
            ppCard('Định dạng số', `
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Dời dấu chấm (lần)</label>
                        <input id="propShift" type="number" min="0" max="9" step="1" value="${el.decimalShift ?? 0}" /></div>
                    <div class="prop-field"><label>Số chữ số sau dấu chấm</label>
                        <input id="propDecimals" type="number" min="0" max="9" step="1" placeholder="tự động" value="${el.decimals ?? ''}" /></div>
                </div>
                <p class="hint small pp-hint" id="numShiftHint"></p>
                <div class="prop-grid-2" style="margin-top:10px;">
                    <div class="prop-field"><label>Chữ đứng trước</label>
                        <input id="propPrefix" type="text" maxlength="32" value="${escHtml(el.prefix)}" /></div>
                    <div class="prop-field"><label>Đơn vị / chữ đứng sau</label>
                        <input id="propSuffix" type="text" maxlength="32" placeholder="vd  °C" value="${escHtml(el.suffix)}" /></div>
                </div>`)
            + ppCard('Định dạng chữ', `
                <div class="prop-field"><label>Căn lề</label>${alignSelectHtml(el, 'center')}</div>
                ${fontFieldsHtml(el)}`)
            + ppCard('Ghi chú', `
                <div class="prop-field"><label>Ghi chú (chỉ để nhận biết, không hiển thị)</label>
                    <input id="propComment" type="text" maxlength="200" value="${escHtml(el.comment)}" /></div>`, 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    const $p = (id) => document.getElementById(id);
    const syncUi = () => {
        const input = $p('propValueMode').value === 'Input';
        $p('numInputBox').hidden = !input;
        $p('numWriteBox').hidden = !$p('propSepWrite').checked;
        const info = varInfo($p('propReadTag').value);
        const isInt = !info || info.type === 'Integer';
        $p('propShift').disabled = !isInt;
        const shift = clampInt($p('propShift').value, 0, 9);
        $p('numShiftHint').textContent = !isInt
            ? 'Biến số thực: không dời dấu chấm (chỉ chỉnh "số chữ số sau dấu chấm").'
            : (shift > 0 ? `Số nguyên 12345 hiển thị là ${shiftIntString('12345', shift)}; khi nhập thì tự nhân lại 10^${shift}.` : 'Dời dấu chấm 0 = hiển thị đúng giá trị số nguyên. Ví dụ dời 2 lần: 12345 → 123.45.');
        if (input) {
            const w = varInfo($p('propSepWrite').checked ? $p('propWriteTag').value : $p('propReadTag').value);
            $p('numWriteHint').textContent = w && !w.writable
                ? `⚠️ "${w.name}" là ${w.source === 'Project' ? 'biến' : w.source + ' (chỉ đọc)'} — không ghi được. Hãy chọn Project tag.`
                : 'Chỉ Project tag mới ghi được; System tag, biến MS, MC chỉ đọc. Giới hạn tính theo giá trị đang HIỂN THỊ (sau khi dời dấu chấm).';
        }
    };
    ['propReadTag', 'propValueMode', 'propSepWrite', 'propWriteTag', 'propShift'].forEach(id => { const n = $p(id); if (n) n.addEventListener('change', syncUi); });
    $p('propShift').addEventListener('input', syncUi);
    syncUi();

    const applyForm = (el) => {
        el.readTag = $p('propReadTag').value || null;
        el.valueMode = $p('propValueMode').value === 'Input' ? 'Input' : 'Display';
        el.separateWriteTag = el.valueMode === 'Input' && $p('propSepWrite').checked;
        el.writeTag = el.separateWriteTag ? ($p('propWriteTag').value || null) : null;
        el.minValue = numOrNull('propMin');
        el.maxValue = numOrNull('propMax');
        el.decimalShift = $p('propShift').disabled ? 0 : clampInt($p('propShift').value, 0, 9);
        const d = $p('propDecimals').value;
        el.decimals = d === '' ? null : clampInt(d, 0, 9);
        el.prefix = $p('propPrefix').value || null;
        el.suffix = $p('propSuffix').value || null;
        el.textAlign = $p('propAlign').value || null;
        el.comment = $p('propComment').value || null;
        applyFontFromForm(el);
        applyStyleFieldsFromForm(el);
        applyEffectsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    $p('savePropsBtn').addEventListener('click', async () => { applyForm(el); await persistAndRefresh(el); });
    $p('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
    fitPropsDialog();
}

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

// Nut chuyen mat bang: to mau khi mat bang DANG XEM chinh la mat bang dich cua nut (de nhan biet dang o dau)
const BTN_ACTIVE_BG = '#16a34a', BTN_ACTIVE_FG = '#ffffff';
// Mặt bằng ĐANG XEM: bình thường là mặt bằng đang mở; trong iframe Header / Navigation là mặt bằng đỗ xe đang hiện ở khung chính
function viewedFloor() {
    if (!window.PG_PANEL) return CURRENT_FLOOR;
    if (window.pgViewedFloor) return window.pgViewedFloor;
    try { return window.parent.currentFloor || CURRENT_FLOOR; } catch { return CURRENT_FLOOR; }
}
function isButtonActive(el) {
    return el.type === 'Button' && el.buttonActiveEnabled !== false && !!el.targetFloorId
        && String(el.targetFloorId).toLowerCase() === String(viewedFloor()).toLowerCase();
}
function applyButtonActiveStyle(div, el) {
    const on = isButtonActive(el);
    div.classList.toggle('is-active', on);
    if (!on) return;
    if (el.backgroundColor === 'transparent') {
        // Nut "Khong to nen": trong suot o MOI mat bang (ke ca mat bang dich) - khong to nen, khong vong sang.
        // Chi doi mau chu khi nguoi dung chu dong chon "Mau chu khi dang o do".
        div.style.backgroundColor = 'transparent';
        div.style.boxShadow = 'none';
        if (el.activeTextColor) div.style.color = el.activeTextColor;
    } else {
        const bg = el.activeBackgroundColor || BTN_ACTIVE_BG;
        div.style.backgroundColor = bg;
        div.style.color = el.activeTextColor || BTN_ACTIVE_FG;
        div.style.boxShadow = `0 0 0 max(2px, calc(3 * var(--px))) ${bg}66`;      // vong sang quanh nut (cung mau nen, trong suot ~40%)
    }
    div.title = (el.label ? el.label + ' — ' : '') + 'đang ở mặt bằng này';
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
    if ((el.type === 'Rectangle' || el.type === 'Button' || el.type === 'Image') && el.cornerRadius !== null && el.cornerRadius !== undefined)
        div.style.borderRadius = `calc(${Number(el.cornerRadius)} * var(--px))`;
    if (el.textColor) div.style.color = el.textColor;
    if (el.fontSize) div.style.fontSize = `calc(${Number(el.fontSize)} * var(--px))`;
    if (el.fontBold === false) div.style.fontWeight = '400';
    if (el.type === 'DateTime' && el.fontBold === true) div.style.fontWeight = '700';
    if (el.fontItalic) div.style.fontStyle = 'italic';
    if (el.type === 'Button') applyButtonActiveStyle(div, el);
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
    if (isPanelFloor(window.currentFloorData) && (type === 'ParkingSlot' || type === 'SlotStats')) return;   // không có cảm biến / camera / ô đỗ
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

    if (type === 'Number') {
        Object.assign(payload, {
            width: 10, height: 6, valueMode: 'Display', decimalShift: 0, textAlign: 'center', fontSize: 16,
            backgroundColor: '#1a2540', borderColor: '#3b82f6', borderStyle: 'solid', borderWidth: 1
        });
    }

    if (type === 'DateTime') {
        Object.assign(payload, {
            width: 18, height: 8, textAlign: 'center', fontSize: 20,
            backgroundColor: '#1a2540', borderColor: '#3b82f6', borderStyle: 'solid', borderWidth: 1,
            dtOptions: Object.assign({}, DT_DEFAULTS)
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
        } else if (target?.type === 'BitLampSwitch' && e.button === 0 && bitSourceOf(target) !== 'Tag') {
            e.preventDefault();                       // den Cam bien / Vi tri do camera: bam de xem thong tin vi tri do
            showLampInfo(target, div);
        } else if (target?.type === 'BitLampSwitch' && e.button === 0) {
            e.preventDefault();
            onBitPress(target, div, 'mouse');
        }
        return;
    }

    const canvasRect = canvas.getBoundingClientRect();
    const pid = div.dataset.id;

    // Chuot phai: chi chon doi tuong (neu chua nam trong vung chon) de menu Can chinh hien dung; khong keo
    if (e.button === 2) { if (!selectedIds.has(pid)) selectElement(pid); return; }
    if (e.button !== 0) return;
    const ae = document.activeElement;
    if (ae && ae !== document.body && ae.matches?.('input, textarea, select')) ae.blur();

    // Ctrl / Shift (hoac Cmd) + nhap: them / bo doi tuong khoi vung chon
    if (e.ctrlKey || e.metaKey || e.shiftKey) { e.preventDefault(); toggleSelection(pid); return; }

    // Dang chon nhieu doi tuong va bam vao 1 doi tuong trong do: keo CA NHOM
    if (selectedIds.size > 1 && selectedIds.has(pid)) {
        const items = [...selectedIds].map(i => {
            const n = canvas.querySelector(`.layout-el[data-id="${i}"]`);
            return n && { id: i, node: n, left: parseFloat(n.style.left), top: parseFloat(n.style.top),
                          w: parseFloat(n.style.width), h: parseFloat(n.style.height) };
        }).filter(Boolean);
        drag = { group: true, id: pid, items, startX: e.clientX, startY: e.clientY, moved: false, canvasRect };
        document.addEventListener('mousemove', onDragMove);
        document.addEventListener('mouseup', onDragEnd);
        e.preventDefault();
        return;
    }

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

    if (drag.group) {      // keo nhom: giu nguyen khoang cach giua cac doi tuong, khong cho doi tuong nao ra khoi khung
        let gx = (dx / drag.canvasRect.width) * 100, gy = (dy / drag.canvasRect.height) * 100;
        const loX = Math.max(...drag.items.map(i => -i.left)), hiX = Math.min(...drag.items.map(i => 100 - i.left - i.w));
        const loY = Math.max(...drag.items.map(i => -i.top)), hiY = Math.min(...drag.items.map(i => 100 - i.top - i.h));
        gx = loX <= hiX ? Math.max(loX, Math.min(hiX, gx)) : 0;
        gy = loY <= hiY ? Math.max(loY, Math.min(hiY, gy)) : 0;
        drag.items.forEach(i => { i.node.style.left = `${i.left + gx}%`; i.node.style.top = `${i.top + gy}%`; });
        return;
    }

    const dxPct = (dx / drag.canvasRect.width) * 100;
    const dyPct = (dy / drag.canvasRect.height) * 100;

    let newLeftPct = drag.originLeftPct + dxPct;
    let newTopPct = drag.originTopPct + dyPct;

    newLeftPct = Math.max(0, Math.min(newLeftPct, 100 - drag.widthPct));
    newTopPct = Math.max(0, Math.min(newTopPct, 100 - drag.heightPct));

    drag.el.style.left = `${newLeftPct}%`;
    drag.el.style.top = `${newTopPct}%`;
    updateResizeFrame();
}

async function onDragEnd() {
    document.removeEventListener('mousemove', onDragMove);
    document.removeEventListener('mouseup', onDragEnd);
    if (!drag) return;

    if (drag.group) {
        const g = drag;
        drag = null;
        if (!g.moved) { selectElement(g.id); return; }     // chi nhap (khong keo): thu ve chon 1 doi tuong
        const changed = [];
        g.items.forEach(i => {
            const el = elements.find(x => x.id === i.id);
            if (el) { el.x = parseFloat(i.node.style.left); el.y = parseFloat(i.node.style.top); changed.push(el); }
        });
        await Promise.all(changed.map(el => persistElement(el).catch(() => { /* loi mang: bo qua */ })));
        return;
    }

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
// Chon NHIEU doi tuong (che do Build): quet chuot (keo khung chon tren vung trong) hoac Ctrl / Shift + nhap chuot trai.
// Chuot phai -> menu Can chinh (Align / Distribute / Rotate / Resize).
// selectedId = doi tuong CHINH (chon sau cung); Resize "cung kich thuoc" lay theo doi tuong chinh.
// ---------------------------------------------------------------------------
function clearSelection() {
    if (live) { clearTimeout(live.timer); live = null; }
    flushPending();
    selectedId = null;
    selectedIds.clear();
    document.querySelectorAll('.layout-el.selected').forEach(n => n.classList.remove('selected'));
    updateResizeFrame();
    if (propsOpen) {
        propsPanel.innerHTML = EMPTY_PROPS_HTML;
        const t = document.getElementById('propsDialogTitleText');
        if (t) t.textContent = 'Thuộc tính đối tượng';
    }
}

// ids: mang id doi tuong; primary: doi tuong chinh (mac dinh la phan tu cuoi)
function setSelection(ids, primary) {
    ids = [...new Set(ids)].filter(id => elements.some(x => x.id === id));
    if (ids.length === 0) { clearSelection(); return; }
    if (ids.length === 1) { selectElement(ids[0]); return; }

    if (live) { clearTimeout(live.timer); live = null; }
    flushPending();                                   // doi tuong dang sua do dang chua luu -> luu truoc
    selectedIds = new Set(ids);
    selectedId = ids.includes(primary) ? primary : ids[ids.length - 1];
    document.querySelectorAll('.layout-el').forEach(n => n.classList.toggle('selected', selectedIds.has(n.dataset.id)));
    updateResizeFrame();                              // chon nhieu: an khung co gian, chi to vien tung doi tuong

    if (propsOpen) {
        const t = document.getElementById('propsDialogTitleText');
        if (t) t.textContent = 'Thuộc tính — nhiều đối tượng';
        propsPanel.innerHTML = `<p class="no-result">Đã chọn <b>${ids.length}</b> đối tượng.<br>` +
            'Nhấp chuột phải để căn chỉnh (Align / Distribute / Rotate / Resize), hoặc kéo để di chuyển cả nhóm. ' +
            'Nhấp vào 1 đối tượng (không giữ Ctrl) để xem thuộc tính của nó.</p>';
    }
}

function toggleSelection(id) {
    const ids = new Set(selectedIds);
    if (ids.has(id)) ids.delete(id); else ids.add(id);
    setSelection([...ids], ids.has(id) ? id : undefined);
}

async function deleteSelected() {
    const ids = [...selectedIds];
    if (ids.length === 0) return;
    if (!confirm(`Xoá ${ids.length} đối tượng đã chọn khỏi bản đồ?`)) return;
    await Promise.all(ids.map(id => fetch(`/api/layout/${id}`, { method: 'DELETE' }).catch(() => { /* bo qua */ })));
    selectedId = null; selectedIds.clear();
    if (live) { clearTimeout(live.timer); live = null; }
    pending = null;
    propsPanel.innerHTML = EMPTY_PROPS_HTML;
    await loadLayout();
}

// ---- Quet chuot: keo 1 khung tren vung trong cua ban do ----
canvas.addEventListener('mousedown', (e) => {
    if (!editMode || e.button !== 0) return;
    if (e.target.closest('.layout-el, .layout-resize-frame')) return;   // bam vao doi tuong / tay nam: da co xu ly rieng
    e.preventDefault();
    closeCtxMenu();
    document.activeElement?.blur?.();      // preventDefault giu nguyen o nhap dang focus -> bo focus de thay doi o panel duoc ghi nhan

    const additive = e.ctrlKey || e.metaKey || e.shiftKey;     // giu Ctrl/Shift: cong them vao vung chon hien co
    const base = additive ? [...selectedIds] : [];
    const x0 = e.clientX, y0 = e.clientY;
    let box = null, moved = false;

    const rectOf = (ev) => ({ l: Math.min(x0, ev.clientX), t: Math.min(y0, ev.clientY), r: Math.max(x0, ev.clientX), b: Math.max(y0, ev.clientY) });
    // Chọn đối tượng có TÂM nằm trong khung quét (chạm mép không tính) -> vùng chọn đúng bằng vùng nhìn thấy trong khung
    const hitsOf = (q) => [...canvas.querySelectorAll('.layout-el')].filter(n => {
        const r = n.getBoundingClientRect();
        const cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
        return cx >= q.l && cx <= q.r && cy >= q.t && cy <= q.b;
    }).map(n => n.dataset.id);
    const clearPreview = () => canvas.querySelectorAll('.layout-el.marquee-pre').forEach(n => n.classList.remove('marquee-pre'));

    const move = (ev) => {
        if (!moved && Math.abs(ev.clientX - x0) < 4 && Math.abs(ev.clientY - y0) < 4) return;
        // Khung vẽ bằng PHẦN TRĂM của canvas: tỉ lệ không có đơn vị nên không lệch dù "Kích thước giao diện" (zoom toàn trang) khác 100%
        if (!moved) { moved = true; box = document.createElement('div'); box.className = 'layout-marquee'; canvas.appendChild(box); }
        const q = rectOf(ev), cr = canvas.getBoundingClientRect();
        const pc = (v, total) => `${(v / (total || 1)) * 100}%`;
        Object.assign(box.style, { left: pc(q.l - cr.left, cr.width), top: pc(q.t - cr.top, cr.height), width: pc(q.r - q.l, cr.width), height: pc(q.b - q.t, cr.height) });
        // Xem trước: đối tượng sẽ được chọn sáng viền ngay trong lúc quét
        const ids = new Set(hitsOf(q));
        canvas.querySelectorAll('.layout-el').forEach(n => n.classList.toggle('marquee-pre', ids.has(n.dataset.id)));
    };
    const up = (ev) => {
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
        box?.remove();
        clearPreview();
        if (!moved) { if (!additive) clearSelection(); return; }   // nhap vao vung trong: bo chon
        setSelection([...base, ...hitsOf(rectOf(ev))]);
    };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', up);
});

// ---- Can chinh: tinh theo KHUNG NHIN THAY (ke ca khi doi tuong dang xoay) cua tung doi tuong, tinh bang px ----
function visualBox(el, W, H) {
    const w = (el.width / 100) * W, h = (el.height / 100) * H;
    const cx = ((el.x + el.width / 2) / 100) * W, cy = ((el.y + el.height / 2) / 100) * H;
    const th = ((Number(el.rotation) || 0) * Math.PI) / 180;
    const c = Math.abs(Math.cos(th)), s = Math.abs(Math.sin(th));
    const hw = (w * c + h * s) / 2, hh = (w * s + h * c) / 2;
    return { el, cx, cy, l: cx - hw, r: cx + hw, t: cy - hh, b: cy + hh };
}

const selectedElements = () => [...selectedIds].map(id => elements.find(x => x.id === id)).filter(Boolean);

// Ve lai + luu cac doi tuong da doi (giu nguyen vung chon)
async function commitChanges(list) {
    if (!list.length) return;
    list.forEach(el => refreshNode(el));
    updateResizeFrame();
    await Promise.all(list.map(el => persistElement(el).catch(() => { /* loi mang: bo qua */ })));
}

async function alignSelected(kind) {
    const W = canvas.clientWidth, H = canvas.clientHeight;
    const boxes = selectedElements().map(el => visualBox(el, W, H));
    if (boxes.length < 2) return;
    const L = Math.min(...boxes.map(b => b.l)), R = Math.max(...boxes.map(b => b.r));
    const T = Math.min(...boxes.map(b => b.t)), B = Math.max(...boxes.map(b => b.b));
    const changed = [];
    boxes.forEach(b => {
        let dx = 0, dy = 0;
        if (kind === 'left') dx = L - b.l;
        else if (kind === 'center') dx = (L + R) / 2 - b.cx;
        else if (kind === 'right') dx = R - b.r;
        else if (kind === 'top') dy = T - b.t;
        else if (kind === 'middle') dy = (T + B) / 2 - b.cy;
        else if (kind === 'bottom') dy = B - b.b;
        if (Math.abs(dx) > 0.01 || Math.abs(dy) > 0.01) {
            b.el.x += (dx / W) * 100; b.el.y += (dy / H) * 100;
            changed.push(b.el);
        }
    });
    await commitChanges(changed);
}

// Chia deu khoang cach giua cac doi tuong (can >= 3): 2 doi tuong ngoai cung giu nguyen
async function distributeSelected(axis) {
    const W = canvas.clientWidth, H = canvas.clientHeight;
    const horiz = axis === 'h';
    const boxes = selectedElements().map(el => visualBox(el, W, H)).sort((a, b) => horiz ? a.cx - b.cx : a.cy - b.cy);
    if (boxes.length < 3) return;
    const lo = b => horiz ? b.l : b.t, hi = b => horiz ? b.r : b.b;
    const span = hi(boxes[boxes.length - 1]) - lo(boxes[0]);
    const total = boxes.reduce((n, b) => n + (hi(b) - lo(b)), 0);
    const gap = (span - total) / (boxes.length - 1);
    let cursor = lo(boxes[0]);
    const changed = [];
    boxes.forEach(b => {
        const d = cursor - lo(b);
        if (Math.abs(d) > 0.01) {
            if (horiz) b.el.x += (d / W) * 100; else b.el.y += (d / H) * 100;
            changed.push(b.el);
        }
        cursor += (hi(b) - lo(b)) + gap;
    });
    await commitChanges(changed);
}

// Xoay tung doi tuong quanh tam cua chinh no: +90 (phai) / -90 (trai) / 0 (ve goc)
async function rotateSelected(deg) {
    const changed = [];
    selectedElements().forEach(el => {
        let r = deg === 0 ? 0 : (((Number(el.rotation) || 0) + deg) % 360 + 360) % 360;
        if (r > 180) r -= 360;
        el.rotation = r === 0 ? null : r;
        changed.push(el);
    });
    await commitChanges(changed);
}

// Cung chieu rong / chieu cao / kich thuoc theo doi tuong CHINH (chon sau cung); giu goc tren-trai, khong tran ra ngoai khung
async function resizeSelected(kind) {
    const ref = elements.find(x => x.id === selectedId);
    if (!ref) return;
    const changed = [];
    selectedElements().forEach(el => {
        if (el === ref) return;
        const w = kind === 'height' ? el.width : Math.min(ref.width, 100 - el.x);
        const h = kind === 'width' ? el.height : Math.min(ref.height, 100 - el.y);
        if (Math.abs(w - el.width) > 0.001 || Math.abs(h - el.height) > 0.001) { el.width = w; el.height = h; changed.push(el); }
    });
    await commitChanges(changed);
}

// ---- Sao chep / Dan (Ctrl+C, Ctrl+V hoac menu chuot phai) ----
// Bo nho tam nam trong trang (khong dung clipboard he thong) nen van dan duoc sang mat bang khac sau khi doi tang.
// Vung nhan dien (ParkingSlot) khong sao chep duoc: moi vi tri chi duoc dat 1 lan tren ban do cua tang.
let layoutClipboard = null;     // { items: [ban sao doi tuong], x, y, w, h (khung bao, %), seq: so lan dan lien tiep }
let pasting = false;

function showLayoutToast(msg) {
    document.querySelectorAll('.layout-toast').forEach(n => n.remove());
    const t = document.createElement('div');
    t.className = 'layout-toast';
    t.textContent = msg;
    document.body.appendChild(t);
    setTimeout(() => t.remove(), 2800);
}

function copySelected() {
    if (!editMode) return false;
    const all = elements.filter(el => selectedIds.has(el.id));      // giu thu tu lop cua ban do
    if (!all.length) return false;
    const copyable = all.filter(el => el.type !== 'ParkingSlot');
    if (!copyable.length) {
        showLayoutToast('Vùng nhận diện (ô đỗ) không sao chép được: mỗi vị trí chỉ đặt 1 lần trên bản đồ của tầng.');
        return false;
    }
    const items = copyable.map(el => { const c = cloneObj(el); delete c.id; delete c.slot; delete c.stats; return c; });
    const x = Math.min(...items.map(i => i.x)), y = Math.min(...items.map(i => i.y));
    const r = Math.max(...items.map(i => i.x + i.width)), b = Math.max(...items.map(i => i.y + i.height));
    layoutClipboard = { items, x, y, w: r - x, h: b - y, seq: 0 };
    const skipped = all.length - items.length;
    showLayoutToast(`Đã sao chép ${items.length} đối tượng` + (skipped ? ` (bỏ qua ${skipped} vùng nhận diện)` : '') + ' — Ctrl+V để dán.');
    return true;
}

// at = { x, y } (% so voi khung) la goc tren-trai cua nhom dan (dan tai con tro chuot); khong co = dan lech 1 chut so voi ban goc
async function pasteClipboard(at) {
    if (!editMode || !layoutClipboard || pasting) return;
    pasting = true;
    try {
        const cb = layoutClipboard;
        let ox, oy;
        if (at) { ox = at.x; oy = at.y; }
        else {
            cb.seq++;
            const ar = canvas.clientWidth / (canvas.clientHeight || 1);
            ox = cb.x + 2 * cb.seq; oy = cb.y + 2 * cb.seq * ar;
        }
        ox = Math.max(0, Math.min(ox, 100 - cb.w));          // nhom luon nam tron trong khung
        oy = Math.max(0, Math.min(oy, 100 - cb.h));
        const dx = ox - cb.x, dy = oy - cb.y, r2 = (v) => Math.round(v * 100) / 100;

        const created = [], failed = [];
        for (const it of cb.items) {                          // lan luot de giu thu tu lop
            const payload = { ...cloneObj(it), floor: CURRENT_FLOOR, x: r2(it.x + dx), y: r2(it.y + dy) };
            try {
                const res = await fetch('/api/layout', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
                });
                if (res.ok) created.push((await res.json()).id);
                else failed.push((await res.json().catch(() => ({}))).message || `lỗi ${res.status}`);
            } catch { failed.push('mất kết nối'); }
        }

        clearSelection();
        await loadLayout();
        if (created.length) setSelection(created);
        if (failed.length) alert(`Không dán được ${failed.length}/${cb.items.length} đối tượng: ${[...new Set(failed)].join(' | ')}`);
    } finally { pasting = false; }
}

document.addEventListener('keydown', (e) => {
    if (!editMode || !(e.ctrlKey || e.metaKey) || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k !== 'c' && k !== 'v') return;
    if (e.target?.closest?.('input, textarea, select, [contenteditable="true"]')) return;   // dang go chu: de trinh duyet xu ly
    const zm = document.getElementById('zonePickModal');
    if (zm && getComputedStyle(zm).display !== 'none') return;
    if (k === 'c') {
        if (String(window.getSelection?.() ?? '').trim()) return;                           // dang boi den chu thuong
        if (copySelected()) e.preventDefault();
    } else if (layoutClipboard) {
        e.preventDefault();
        pasteClipboard();
    }
});

// ---- Xoa bang phim Delete: chi can tro chuot vao doi tuong (khong can chon) roi nhan Delete ----
let hoverId = null;      // doi tuong dang duoc tro chuot toi
canvas.addEventListener('mouseover', (e) => { hoverId = e.target.closest?.('.layout-el')?.dataset.id ?? null; });
canvas.addEventListener('mouseleave', () => { hoverId = null; });

document.addEventListener('keydown', (e) => {
    if (e.key !== 'Delete' || !editMode || e.ctrlKey || e.metaKey || e.altKey || e.repeat || drag || rsz) return;
    if (e.target?.closest?.('input, textarea, select, [contenteditable="true"]')) return;   // dang go chu: Delete xoa ky tu
    const zm = document.getElementById('zonePickModal');
    if (zm && getComputedStyle(zm).display !== 'none') return;

    const hovered = hoverId && elements.some(x => x.id === hoverId) ? hoverId : null;
    if (hovered) {
        e.preventDefault();
        // tro vao 1 doi tuong nam trong nhom dang chon -> xoa ca nhom; tro vao doi tuong khac -> chi xoa doi tuong do
        if (selectedIds.size > 1 && selectedIds.has(hovered)) deleteSelected(); else deleteElement(hovered);
    } else if (selectedIds.size > 0) {
        e.preventDefault();
        if (selectedIds.size > 1) deleteSelected(); else deleteElement(selectedId);
    }
});

// ---- Di chuyen bang phim mui ten (che do Build): moi lan nhan = 1 don vi (0.1 % khung, dung buoc cua o X / Y trong bang thuoc tinh);
//      giu Shift = 10 don vi. Ap dung cho doi tuong / nhom dang chon. Ve ngay, luu (gop) sau khi ngung nhan ~0.25 s. ----
const NUDGE_UNIT = 0.1;
const nudgePending = new Set();
let nudgeTimer = null;

function flushNudge() {
    nudgeTimer = null;
    const list = [...nudgePending].map(id => elements.find(x => x.id === id)).filter(Boolean);
    nudgePending.clear();
    list.forEach(el => persistElement(el).catch(() => { /* loi mang: bo qua */ }));
}

document.addEventListener('keydown', (e) => {
    const dir = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
    if (!dir || !editMode || e.ctrlKey || e.metaKey || e.altKey || drag || rsz) return;
    if (e.target?.closest?.('input, textarea, select, [contenteditable="true"]')) return;   // dang go chu / chon: mui ten dung cho o do
    const zm = document.getElementById('zonePickModal');
    if (zm && getComputedStyle(zm).display !== 'none') return;
    const sel = selectedElements();
    if (!sel.length) return;
    e.preventDefault();

    const step = NUDGE_UNIT * (e.shiftKey ? 10 : 1);
    const r3 = (v) => Math.round(v * 1000) / 1000;
    sel.forEach(el => {
        const w = Number(el.width) || 0, h = Number(el.height) || 0;
        const nx = r3(Math.min(Math.max(0, 100 - w), Math.max(0, Number(el.x) + dir[0] * step)));
        const ny = r3(Math.min(Math.max(0, 100 - h), Math.max(0, Number(el.y) + dir[1] * step)));
        if (nx === el.x && ny === el.y) return;
        el.x = nx; el.y = ny;
        refreshNode(el);
        nudgePending.add(el.id);
    });
    updateResizeFrame();
    clearTimeout(nudgeTimer);
    nudgeTimer = setTimeout(flushNudge, 250);
});

// ---- Menu chuot phai ----
let ctxMenu = null;
function closeCtxMenu() { if (ctxMenu) { ctxMenu.remove(); ctxMenu = null; } }

function alignIcon(k) {
    const g = {
        left:   '<path d="M2 2v12"/><rect x="4" y="3" width="9" height="4"/><rect x="4" y="9" width="6" height="4"/>',
        center: '<path d="M8 2v12"/><rect x="3" y="3" width="10" height="4"/><rect x="5" y="9" width="6" height="4"/>',
        right:  '<path d="M14 2v12"/><rect x="3" y="3" width="9" height="4"/><rect x="6" y="9" width="6" height="4"/>',
        top:    '<path d="M2 2h12"/><rect x="3" y="4" width="4" height="9"/><rect x="9" y="4" width="4" height="6"/>',
        middle: '<path d="M2 8h12"/><rect x="3" y="3" width="4" height="10"/><rect x="9" y="5" width="4" height="6"/>',
        bottom: '<path d="M2 14h12"/><rect x="3" y="3" width="4" height="9"/><rect x="9" y="6" width="4" height="6"/>'
    }[k];
    return g ? `<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.2" aria-hidden="true">${g}</svg>` : '';
}

const CTX_MENU = [
    { label: 'Sao chép (Copy)', hint: 'Ctrl+C', icon: '⧉', enabled: () => selectedIds.size > 0, run: () => copySelected() },
    { label: 'Dán (Paste)', hint: 'Ctrl+V', icon: '📋', enabled: () => !!layoutClipboard, run: (at) => pasteClipboard(at) },
    { sep: true },
    { label: 'Căn chỉnh (Align)', min: 2, items: [
        { label: 'Căn trái', icon: alignIcon('left'), run: () => alignSelected('left') },
        { label: 'Căn giữa (ngang)', icon: alignIcon('center'), run: () => alignSelected('center') },
        { label: 'Căn phải', icon: alignIcon('right'), run: () => alignSelected('right') },
        { sep: true },
        { label: 'Căn trên', icon: alignIcon('top'), run: () => alignSelected('top') },
        { label: 'Căn giữa (dọc)', icon: alignIcon('middle'), run: () => alignSelected('middle') },
        { label: 'Căn dưới', icon: alignIcon('bottom'), run: () => alignSelected('bottom') }] },
    { label: 'Phân bố (Distribute)', min: 3, items: [
        { label: 'Đều khoảng cách ngang', icon: '↔', run: () => distributeSelected('h') },
        { label: 'Đều khoảng cách dọc', icon: '↕', run: () => distributeSelected('v') }] },
    { label: 'Xoay (Rotate)', min: 1, items: [
        { label: 'Xoay phải 90°', icon: '⟳', run: () => rotateSelected(90) },
        { label: 'Xoay trái 90°', icon: '⟲', run: () => rotateSelected(-90) },
        { label: 'Về góc 0°', icon: '⊘', run: () => rotateSelected(0) }] },
    { label: 'Kích thước (Resize)', min: 2, items: [
        { label: 'Cùng chiều rộng', icon: '↔', run: () => resizeSelected('width') },
        { label: 'Cùng chiều cao', icon: '↕', run: () => resizeSelected('height') },
        { label: 'Cùng kích thước', icon: '⤢', run: () => resizeSelected('both') }] }
];

function showCtxMenu(x, y, at) {
    closeCtxMenu();
    const n = selectedIds.size;
    const menu = document.createElement('div');
    menu.className = 'pg-ctxmenu';
    menu.addEventListener('contextmenu', (ev) => ev.preventDefault());

    CTX_MENU.forEach(group => {
        if (group.sep) { const hr = document.createElement('div'); hr.className = 'pg-ctx-sep'; menu.appendChild(hr); return; }
        if (!group.items) {      // muc don (Sao chep / Dan)
            const row = document.createElement('div');
            row.className = 'pg-ctx-item' + (group.enabled && !group.enabled() ? ' disabled' : '');
            row.innerHTML = `<span class="pg-ctx-ico">${group.icon || ''}</span><span class="pg-ctx-lbl">${group.label}</span><span class="pg-ctx-hint">${group.hint || ''}</span>`;
            row.addEventListener('click', () => { closeCtxMenu(); group.run(at); });
            menu.appendChild(row);
            return;
        }
        const item = document.createElement('div');
        item.className = 'pg-ctx-item has-sub' + (n < group.min ? ' disabled' : '');
        item.title = n < group.min ? `Cần chọn từ ${group.min} đối tượng` : '';
        item.innerHTML = `<span class="pg-ctx-ico"></span><span class="pg-ctx-lbl">${group.label}</span><span class="pg-ctx-arrow">▸</span>`;

        const sub = document.createElement('div');
        sub.className = 'pg-ctxmenu pg-ctx-sub';
        group.items.forEach(it => {
            if (it.sep) { const hr = document.createElement('div'); hr.className = 'pg-ctx-sep'; sub.appendChild(hr); return; }
            const row = document.createElement('div');
            row.className = 'pg-ctx-item';
            row.innerHTML = `<span class="pg-ctx-ico">${it.icon || ''}</span><span class="pg-ctx-lbl">${it.label}</span>`;
            row.addEventListener('click', () => { closeCtxMenu(); it.run(); });
            sub.appendChild(row);
        });
        item.appendChild(sub);
        // Mo submenu sang trai neu bi tran man hinh ben phai
        item.addEventListener('mouseenter', () => {
            sub.classList.remove('flip');
            if (item.getBoundingClientRect().right + sub.offsetWidth > window.innerWidth) sub.classList.add('flip');
        });
        menu.appendChild(item);
    });

    document.body.appendChild(menu);
    const mw = menu.offsetWidth, mh = menu.offsetHeight;
    menu.style.left = `${Math.max(4, Math.min(x, window.innerWidth - mw - 4))}px`;
    menu.style.top = `${Math.max(4, Math.min(y, window.innerHeight - mh - 4))}px`;
    ctxMenu = menu;
}

canvas.addEventListener('contextmenu', (e) => {
    if (!editMode) return;
    e.preventDefault();
    if (selectedIds.size === 0 && !layoutClipboard) { closeCtxMenu(); return; }
    const cr = canvas.getBoundingClientRect();     // vi tri con tro (% khung): "Dan" dat nhom tai day
    showCtxMenu(e.clientX, e.clientY, { x: ((e.clientX - cr.left) / cr.width) * 100, y: ((e.clientY - cr.top) / cr.height) * 100 });
});
document.addEventListener('mousedown', (e) => { if (ctxMenu && !e.target.closest('.pg-ctxmenu')) closeCtxMenu(); }, true);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeCtxMenu(); });
window.addEventListener('blur', closeCtxMenu);
viewport.addEventListener('scroll', closeCtxMenu);

// ---------------------------------------------------------------------------
// Co gian doi tuong bang chuot (chi o che do Build): doi tuong dang chon co khung 8 tay nam
// (4 goc + 4 canh). Keo tay nam de doi kich thuoc; Shift + keo tay nam o GOC de giu ty le.
// Duong thang chi co 2 tay nam 2 dau (doi chieu dai). Doi tuong dang xoay van co gian dung theo truc cua no.
// Khung nam trong canvas, KHONG nam trong doi tuong nen khong bi mat khi doi tuong ve lai noi dung.
// ---------------------------------------------------------------------------
const RESIZE_HANDLES = [['nw', -1, -1], ['n', 0, -1], ['ne', 1, -1], ['e', 1, 0], ['se', 1, 1], ['s', 0, 1], ['sw', -1, 1], ['w', -1, 0]];
let resizeFrame = null;
let rsz = null;

function ensureResizeFrame() {
    if (resizeFrame && resizeFrame.parentNode === canvas) return resizeFrame;
    resizeFrame = document.createElement('div');
    resizeFrame.className = 'layout-resize-frame';
    resizeFrame.hidden = true;
    RESIZE_HANDLES.forEach(([name, hx, hy]) => {
        const h = document.createElement('span');
        h.className = `resize-handle rh-${name}`;
        h.dataset.hx = hx;
        h.dataset.hy = hy;
        h.dataset.h = name;
        h.addEventListener('mousedown', onResizeHandleDown);
        resizeFrame.appendChild(h);
    });
    canvas.appendChild(resizeFrame);
    return resizeFrame;
}

// Dat khung theo dung vi tri / kich thuoc / goc xoay hien tai cua doi tuong dang chon (doc tu style cua node).
function updateResizeFrame() { updateResizeFrameCore(); syncFormatTab(); }   // khung co gian + dong bo tab ribbon "Dinh dang"

function updateResizeFrameCore() {
    const frame = ensureResizeFrame();
    const div = editMode && selectedId && selectedIds.size <= 1 ? canvas.querySelector(`.layout-el[data-id="${selectedId}"]`) : null;
    if (!div) { frame.hidden = true; return; }

    const el = elements.find(x => x.id === selectedId);
    const isLine = el?.type === 'Line';
    frame.style.left = div.style.left;
    frame.style.top = div.style.top;
    frame.style.width = div.style.width;
    frame.style.height = div.style.height;
    frame.style.transform = div.style.transform;
    frame.querySelectorAll('.resize-handle').forEach(h => {
        h.hidden = isLine && (h.dataset.hy !== '0');   // duong thang: chi 2 dau (doi chieu dai)
    });
    frame.hidden = false;
}

function onResizeHandleDown(e) {
    if (!editMode || e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();

    const el = elements.find(x => x.id === selectedId);
    const div = el && canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
    if (!div) return;

    const W = canvas.clientWidth, H = canvas.clientHeight;
    const w0 = (parseFloat(div.style.width) / 100) * W;
    const h0 = (parseFloat(div.style.height) / 100) * H;
    const left0 = (parseFloat(div.style.left) / 100) * W;
    const top0 = (parseFloat(div.style.top) / 100) * H;

    rsz = {
        id: el.id, div, W, H, w0, h0, left0, top0,
        cx0: left0 + w0 / 2, cy0: top0 + h0 / 2,
        hx: Number(e.currentTarget.dataset.hx), hy: Number(e.currentTarget.dataset.hy),
        th: ((Number(el.rotation) || 0) * Math.PI) / 180,
        rotated: (((Number(el.rotation) || 0) % 360) + 360) % 360 !== 0,
        startX: e.clientX, startY: e.clientY,
        minW: Math.max(W * 0.01, 6), minH: Math.max(H * 0.01, 6),
        last: null
    };
    document.body.classList.add('is-resizing');
    document.addEventListener('mousemove', onResizeMove);
    document.addEventListener('mouseup', onResizeEnd);
}

function onResizeMove(e) {
    if (!rsz) return;
    const s = rsz;
    const dx = e.clientX - s.startX, dy = e.clientY - s.startY;
    const cos = Math.cos(s.th), sin = Math.sin(s.th);
    const lx = dx * cos + dy * sin;       // do doi cua chuot theo truc cua doi tuong (da xoay nguoc goc xoay)
    const ly = -dx * sin + dy * cos;

    let w = s.w0 + s.hx * lx;
    let h = s.h0 + s.hy * ly;

    // Gioi han: khong nho hon toi thieu va khong tran ra ngoai khung (canh dang keo bi chan o mep khung)
    const rightRoom = s.W - s.left0, leftRoom = s.left0 + s.w0;
    const bottomRoom = s.H - s.top0, topRoom = s.top0 + s.h0;
    const maxW = s.rotated ? s.W : (s.hx === 1 ? rightRoom : s.hx === -1 ? leftRoom : s.w0);
    const maxH = s.rotated ? s.H : (s.hy === 1 ? bottomRoom : s.hy === -1 ? topRoom : s.h0);

    if (e.shiftKey && s.hx !== 0 && s.hy !== 0) {
        // Giu ty le (chi o 4 goc)
        let k = Math.max(w / s.w0, h / s.h0);
        k = Math.min(k, maxW / s.w0, maxH / s.h0);
        k = Math.max(k, s.minW / s.w0, s.minH / s.h0);
        w = s.w0 * k; h = s.h0 * k;
    } else {
        w = s.hx === 0 ? s.w0 : Math.min(Math.max(w, s.minW), maxW);
        h = s.hy === 0 ? s.h0 : Math.min(Math.max(h, s.minH), maxH);
    }

    // Diem doi dien voi tay nam dung yen: tinh lai tam moi theo he truc da xoay
    const ax = (-s.hx * s.w0) / 2, ay = (-s.hy * s.h0) / 2;
    const bx = (-s.hx * w) / 2, by = (-s.hy * h) / 2;
    const ddx = ax - bx, ddy = ay - by;
    const cx = s.cx0 + (ddx * cos - ddy * sin);
    const cy = s.cy0 + (ddx * sin + ddy * cos);

    let left = cx - w / 2, top = cy - h / 2;
    left = Math.min(Math.max(left, 0), s.W - w);
    top = Math.min(Math.max(top, 0), s.H - h);

    const r = v => Math.round(v * 100) / 100;
    s.last = { x: r((left / s.W) * 100), y: r((top / s.H) * 100), width: r((w / s.W) * 100), height: r((h / s.H) * 100) };
    s.div.style.left = `${s.last.x}%`;
    s.div.style.top = `${s.last.y}%`;
    s.div.style.width = `${s.last.width}%`;
    s.div.style.height = `${s.last.height}%`;
    updateResizeFrame();
}

async function onResizeEnd() {
    document.removeEventListener('mousemove', onResizeMove);
    document.removeEventListener('mouseup', onResizeEnd);
    document.body.classList.remove('is-resizing');
    const s = rsz;
    rsz = null;
    if (!s || !s.last) return;

    const el = elements.find(x => x.id === s.id);
    if (!el) return;
    Object.assign(el, s.last);
    await persistElement(el);
    selectElement(el.id);          // cap nhat o nhap X/Y/Rong/Cao trong hop thoai thuoc tinh (neu dang mo)
    refreshNode(el);               // ve lai noi dung theo kich thuoc moi (chu, net ve, ...)
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
    if (selectedIds.size > 1) deleteSelected(); else deleteElement(selectedId);
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
    selectedIds = new Set([id]);
    const el = elements.find(x => x.id === id);
    if (!el) return;
    if (!pending || pending.id !== id) pending = { id, snapshot: cloneObj(el), dirty: false };

    document.querySelectorAll('.layout-el').forEach(n => n.classList.toggle('selected', n.dataset.id === id));
    updateResizeFrame();

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
    } else if (el.type === 'Number') {
        renderNumberProps(el);
    } else if (el.type === 'Text') {
        renderTextProps(el);
    } else if (el.type === 'DateTime') {
        renderDateTimeProps(el);
    } else {
        renderGenericProps(el);
    }
    fitPropsDialog();
}

// Chuyen sang mat bang khac bang cach dung lai bo chon mat bang cua floors.js
// (dat gia tri roi phat su kien 'change' de floors.js tu cap nhat + phat 'floorchange').
function navigateToFloor(floorId) {
    // Iframe Header / Navigation: nút ấn điều khiển khung chính (trang cha)
    if (window.PG_PANEL) { try { window.parent.navigateToFloor(floorId); } catch { /* khác nguồn: bỏ qua */ } return; }
    const tgt = (window.floorList || []).find(f => f.id === floorId);
    if (!editMode && tgt && tgt.kind && tgt.kind !== 'Normal') return;   // chạy: Header / Navigation có khung riêng, không mở như mặt bằng
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
    updateResizeFrame();
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
    lastFlush = persistElement(el).catch(() => { /* loi mang: bo qua */ })
        .then(() => { if (el.type === 'SlotStats') refreshLayoutSlots(); });   // so lieu thong ke do may chu tinh theo cau hinh vua luu
    return lastFlush;
}

function bindLivePreview(el, applyForm) {
    if (live) clearTimeout(live.timer);
    live = { el, applyForm: (e) => { applyForm(e); applyAnimFromForm(e); }, timer: null };
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

// ---------------------------------------------------------------------------
// Tab "Hiển thị / Vị trí" và "Màu sắc" (hiệu ứng động gắn biến) — có ở mọi loại đối tượng
// ---------------------------------------------------------------------------
const ANIM_BLINK = [['none', 'Không'], ['slow', 'Chậm'], ['fast', 'Nhanh']];
const ANIM_STOP_DEFAULT_COLORS = ['#ef4444', '#22c55e', '#eab308', '#3b82f6'];

function animStopRowHtml(s, i) {
    return `<div class="anim-stop-row">
        <input class="anim-limit" type="number" step="any" placeholder="ngưỡng ≥" value="${s?.limit ?? ''}" />
        <input class="anim-color" type="color" value="${escHtml(s?.color || ANIM_STOP_DEFAULT_COLORS[i % 4])}" />
        <select class="anim-blink">${ANIM_BLINK.map(([v, t]) => `<option value="${v}" ${(s?.blink || 'none') === v ? 'selected' : ''}>${t}</option>`).join('')}</select>
    </div>`;
}

function animAxisCard(p, title, refs, ax) {
    const n = (id, v, d) => `<input id="anim${p}${id}" type="number" step="any" value="${v ?? d}" />`;
    const ref = ax?.reference || 'start';
    return ppCard(title, `
        <div class="prop-field"><label>Biến / giá trị điều khiển vị trí</label>
            ${varSelectHtml(`anim${p}Tag`, ax?.tag, isAnimVar, '— không dùng —')}</div>
        <div class="prop-grid-2">
            <div class="prop-field"><label>Giá trị từ</label>${n('VMin', ax?.valueMin, 0)}</div>
            <div class="prop-field"><label>đến</label>${n('VMax', ax?.valueMax, 100)}</div>
            <div class="prop-field"><label>Vị trí từ (%)</label>${n('PMin', ax?.posMin, 0)}</div>
            <div class="prop-field"><label>đến (%)</label>${n('PMax', ax?.posMax, 100)}</div>
        </div>
        <div class="prop-field"><label>Điểm tham chiếu của đối tượng</label>
            <select id="anim${p}Ref">${refs.map(([v, t]) => `<option value="${v}" ${ref === v ? 'selected' : ''}>${t}</option>`).join('')}</select></div>`);
}

function animTabs(el) {
    const a = el.animation || {};
    const c = a.colors || {};
    const stops = (c.stops || []).slice().sort((x, y) => x.limit - y.limit);
    const rows = Math.max(4, stops.length);
    return [
        { id: 'anim-pos', label: 'Hiển thị / Vị trí', html:
            ppCard('Hiển thị (Visibility)', `
                <div class="prop-field"><label>Biến điều khiển ẩn / hiện</label>
                    ${varSelectHtml('animVisTag', a.visibilityTag, isAnimVar, '— luôn hiển thị —')}</div>
                <label class="checkbox-label"><input type="checkbox" id="animVisInv" ${a.visibilityInvert ? 'checked' : ''} /> Đảo: ẩn khi biến khác 0</label>
                ${ppHint('Biến khác 0 (hoặc true) → hiện đối tượng; bằng 0 → ẩn. Hiệu ứng chỉ chạy ở chế độ ▶️ Chạy.')}`, 'wide')
            + animAxisCard('H', 'Vị trí ngang (Horizontal)', [['start', 'TRÁI (Left)'], ['center', 'GIỮA (Center)'], ['end', 'PHẢI (Right)']], a.horizontal)
            + animAxisCard('V', 'Vị trí dọc (Vertical)', [['start', 'TRÊN (Top)'], ['center', 'GIỮA (Center)'], ['end', 'DƯỚI (Bottom)']], a.vertical)
            + ppCard('Slider / Gestures', `
                <label class="checkbox-label" style="margin-bottom:10px;"><input type="checkbox" id="animSlider" ${a.sliderEnabled ? 'checked' : ''} /> Cho kéo đối tượng ở chế độ Chạy (ghi giá trị về biến)</label>
                <div class="prop-field"><label>Vô hiệu hoá khi biến khác 0</label>
                    ${varSelectHtml('animSliderDis', a.sliderDisableTag, isAnimVar, '— luôn cho kéo —')}</div>
                ${ppHint('Slider cần chọn biến ở Vị trí ngang và / hoặc dọc, và biến đó phải là <b>Project tag</b> (System tag, biến MS, MC chỉ đọc). Kéo trong dải Vị trí thì giá trị biến chạy trong dải Giá trị.')}`, 'wide') },
        { id: 'anim-color', label: 'Màu sắc', html:
            ppCard('Đổi màu theo ngưỡng (By Limit)', `
                <div class="prop-field"><label>Biến / giá trị điều khiển màu</label>
                    ${varSelectHtml('animCTag', c.tag, isAnimVar, '— không đổi màu —')}</div>
                <div class="prop-field"><label>Áp dụng màu cho</label>
                    <div class="prop-inline">
                        <label class="checkbox-label"><input type="checkbox" id="animCFill" ${c.fill === false ? '' : 'checked'} /> Nền</label>
                        <label class="checkbox-label"><input type="checkbox" id="animCBorder" ${c.border ? 'checked' : ''} /> Viền</label>
                        <label class="checkbox-label"><input type="checkbox" id="animCText" ${c.text ? 'checked' : ''} /> Chữ</label>
                    </div></div>
                ${ppHint('Giá trị biến ≥ ngưỡng nào (lấy ngưỡng lớn nhất thoả mãn) thì dùng màu của ngưỡng đó; nhỏ hơn mọi ngưỡng thì giữ màu gốc. Dòng để trống ngưỡng sẽ bị bỏ qua. Đường thẳng đổi màu nét vẽ.')}`)
            + ppCard('Bảng ngưỡng màu', `
                <div class="anim-stop-row anim-stop-head"><span>Ngưỡng (≥)</span><span>Màu</span><span>Nhấp nháy</span></div>
                <div id="animStops">${Array.from({ length: rows }, (_, i) => animStopRowHtml(stops[i], i)).join('')}</div>
                <button type="button" class="btn secondary btn-sm" id="animAddStop" style="margin-top:8px;">＋ Thêm ngưỡng</button>`) }
    ];
}

// Doc 2 tab hieu ung dong tu form -> el.animation (null khi khong dung hieu ung nao)
function applyAnimFromForm(el) {
    const $a = (id) => document.getElementById(id);
    if (!$a('animVisTag')) return;                         // hop thoai khong co tab hieu ung
    const num = (id, d) => { const v = parseFloat(String($a(id).value).replace(',', '.')); return Number.isFinite(v) ? v : d; };
    const axis = (p) => {
        const tag = $a(`anim${p}Tag`).value || null;
        if (!tag) return null;
        return { tag, valueMin: num(`anim${p}VMin`, 0), valueMax: num(`anim${p}VMax`, 100), posMin: num(`anim${p}PMin`, 0), posMax: num(`anim${p}PMax`, 100), reference: $a(`anim${p}Ref`).value };
    };
    const stops = [];
    document.querySelectorAll('#animStops .anim-stop-row').forEach(r => {
        const lim = parseFloat(String(r.querySelector('.anim-limit').value).replace(',', '.'));
        if (Number.isFinite(lim)) stops.push({ limit: lim, color: r.querySelector('.anim-color').value, blink: r.querySelector('.anim-blink').value });
    });
    stops.sort((x, y) => x.limit - y.limit);
    const ctag = $a('animCTag').value || null;
    const a = {
        visibilityTag: $a('animVisTag').value || null,
        visibilityInvert: $a('animVisInv').checked,
        horizontal: axis('H'),
        vertical: axis('V'),
        sliderEnabled: $a('animSlider').checked,
        sliderDisableTag: $a('animSliderDis').value || null,
        colors: ctag && stops.length ? { tag: ctag, fill: $a('animCFill').checked, border: $a('animCBorder').checked, text: $a('animCText').checked, stops } : null
    };
    if (!a.visibilityTag) a.visibilityInvert = false;
    if (!a.horizontal && !a.vertical) { a.sliderEnabled = false; }
    if (!a.sliderEnabled) a.sliderDisableTag = null;
    el.animation = (a.visibilityTag || a.horizontal || a.vertical || a.colors) ? a : null;
}

// "＋ Thêm ngưỡng": them 1 dong vao bang (toi da 8)
propsPanel.addEventListener('click', (e) => {
    if (e.target.id !== 'animAddStop') return;
    const box = document.getElementById('animStops');
    const n = box.querySelectorAll('.anim-stop-row').length;
    if (n >= 8) { e.target.disabled = true; return; }
    box.insertAdjacentHTML('beforeend', animStopRowHtml(null, n));
    if (n + 1 >= 8) e.target.disabled = true;
    fitPropsDialog();
});

// Chuyen tab trong hop thoai thuoc tinh (nho tab dang xem theo loai doi tuong)
propsPanel.addEventListener('click', (e) => {
    const tab = e.target.closest('.pp-tab');
    if (!tab) return;
    propsPanel.querySelectorAll('.pp-tab').forEach(t => { const on = t === tab; t.classList.toggle('active', on); t.setAttribute('aria-selected', on ? 'true' : 'false'); });
    propsPanel.querySelectorAll('.pp-pane').forEach(p => { p.hidden = p.dataset.pane !== tab.dataset.pane; });
    const cur = elements.find(x => x.id === selectedId);
    if (cur) PP_TAB_MEMORY[cur.type] = tab.dataset.pane;
    fitPropsDialog();
});

// Bo chon mau kieu Office (propUi.js): thay moi <input type="color"> trong hop thoai bang bang mau Theme / Standard / RGB
if (window.PgColor) PgColor.autoEnhance(propsPanel);

const DIRTY_NOTE_HTML = '<p id="propDirtyNote" class="prop-dirty-note" hidden>● Đã thay đổi — sẽ tự lưu khi bạn chọn đối tượng khác (hoặc bấm 💾 Lưu ngay).</p>';

async function persistAndRefresh(el) {
    await persistElement(el);
    renderLayout();
    refreshLampStates();
    refreshLayoutSlots();
    selectElement(el.id);
}

// ---------------------------------------------------------------------------
// Cac o nhap dung chung cho MOI loai doi tuong: vi tri, kich thuoc, mau sac, vien
// ---------------------------------------------------------------------------
// --- Bố cục hộp thuộc tính: tab + thẻ (card). Vị trí / kích thước / xoay / lớp KHÔNG nằm ở đây
//     mà ở tab ribbon "Định dạng" (xem phần FORMAT TAB bên dưới). ---
const PP_TAB_MEMORY = {};   // nhớ tab đang xem theo loại đối tượng
const ppCard = (title, body, cls = '', attrs = '') =>
    `<section class="pp-card ${cls}" ${attrs}>${title ? `<header class="pp-card-h">${title}</header>` : ''}<div class="pp-card-b">${body}</div></section>`;
const ppHint = (t) => `<p class="hint small pp-hint">${t}</p>`;

// tabs: [{ id, label, html }] — nhiều tab thì hiện dải tab, 1 tab thì chỉ hiện nội dung
function ppTabs(type, tabs) {
    const cur = elements.find(x => x.id === selectedId);
    if (cur && !tabs.some(t => t.id === 'anim-pos')) tabs = [...tabs, ...animTabs(cur)];   // moi loai doi tuong deu co hieu ung dong
    let act = PP_TAB_MEMORY[type];
    if (!tabs.some(t => t.id === act)) act = tabs[0].id;
    const strip = tabs.length > 1
        ? `<div class="pp-tabs" role="tablist">${tabs.map(t =>
            `<button type="button" class="pp-tab ${t.id === act ? 'active' : ''}" role="tab" aria-selected="${t.id === act}" data-pane="${t.id}">${t.label}</button>`).join('')}</div>`
        : '';
    return strip + tabs.map(t => `<div class="pp-pane" data-pane="${t.id}" ${t.id === act ? '' : 'hidden'}>${t.html}</div>`).join('');
}

const ppFooter = (delLabel = '🗑️ Xoá', saveId = 'savePropsBtn') => `
    ${DIRTY_NOTE_HTML}
    <div class="pp-footer">
        <button id="deleteElBtn" class="btn secondary">${delLabel}</button>
        <button id="${saveId}" class="btn primary">✓ Áp dụng</button>
    </div>`;

// Vùng xem ảnh + chọn / tải / bỏ ảnh (dùng cho Hình ảnh và các ảnh trạng thái của Đèn / Công tắc)
// p: { urlId, upId, clrId, fileId, viewId, value, placeholder, small, emptyHtml }
function imgPickHtml(p) {
    return `
        <div class="pp-img ${p.small ? 'sm' : ''}">
            <div class="pp-img-view" id="${p.viewId}" title="Vùng xem trước ảnh"></div>
            <div class="pp-img-ctrl">
                <input id="${p.urlId}" type="text" placeholder="${escHtml(p.placeholder || 'Dán URL ảnh hoặc tải lên…')}" value="${escHtml(p.value)}" />
                <div class="prop-inline">
                    <button type="button" class="btn secondary" id="${p.upId}">📁 Tải ảnh lên</button>
                    <button type="button" class="btn secondary" id="${p.clrId}">✕ Bỏ ảnh</button>
                </div>
                <input type="file" id="${p.fileId}" accept=".png,.jpg,.jpeg,.gif,.webp,.bmp" hidden />
                <div class="pp-img-meta" id="${p.viewId}Meta"></div>
            </div>
        </div>`;
}

// emptyHtml: nội dung vùng xem khi chưa có ảnh (vd chấm màu mặc định của đèn)
function bindImgPick(p) {
    const q = (id) => document.getElementById(id);
    const inp = q(p.urlId), view = q(p.viewId), meta = q(p.viewId + 'Meta'), file = q(p.fileId);
    const show = () => {
        const v = inp.value.trim();
        view.textContent = ''; meta.textContent = ''; meta.classList.remove('err');
        if (!v) {
            const e = document.createElement('div');
            e.className = 'pp-img-empty';
            e.innerHTML = p.emptyHtml || '<span>🖼️</span><span>Chưa có ảnh</span>';
            view.appendChild(e);
            return;
        }
        const img = document.createElement('img');
        img.alt = ''; img.draggable = false;
        img.onload = () => { meta.textContent = `${img.naturalWidth} × ${img.naturalHeight} px`; };
        img.onerror = () => { meta.textContent = 'Không tải được ảnh — kiểm tra lại URL.'; meta.classList.add('err'); };
        img.src = v;
        view.appendChild(img);
    };
    show();
    inp.addEventListener('input', show);
    q(p.upId).addEventListener('click', () => file.click());
    q(p.clrId).addEventListener('click', () => { inp.value = ''; inp.dispatchEvent(new Event('input', { bubbles: true })); });
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
}

// Tương thích mã cũ: vị trí / kích thước nay chỉnh ở tab ribbon "Định dạng" nên hộp thuộc tính không còn ô nhập
function applyPositionFieldsFromForm() { /* không còn dùng */ }

function styleFieldsHtml(el, opts = {}) {
    const style = el.borderStyle || 'solid';
    const hasBg = !!el.backgroundColor && el.backgroundColor !== 'transparent';
    const shapeFill = opts.shape ? `
        <label class="checkbox-label" style="margin-top:10px;">
            <input type="checkbox" id="propNoFill" ${el.backgroundColor === 'transparent' ? 'checked' : ''} />
            Không tô nền (trong suốt)
        </label>
        <div class="prop-field" style="margin-top:10px;">
            <label>Độ đậm màu nền: <span id="propFillOpacityVal">${el.fillOpacity ?? 100}</span>%</label>
            <input id="propFillOpacity" type="range" min="0" max="100" step="5" value="${el.fillOpacity ?? 100}" />
        </div>` : '';
    const bg = ppCard('Màu nền', `
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
        ${shapeFill}`);
    const border = ppCard('Đường viền', `
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
        ${ppHint('Để trống "Độ dày viền" = giữ kiểu viền mặc định của loại đối tượng.')}`);
    return (opts.corner ? cornerFieldsHtml(el) : '') + bg + border;
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
    applyEffectsFromForm(el);
}

// ---------------------------------------------------------------------------
// Bo goc cho Nut an / Hinh anh
//   cornerRadius: null = mac dinh theo loai | 0 = vuong goc | N = N "px thiet ke" | 500 = bo tron het co
//   (nut -> vien thuoc, anh vuong -> hinh tron). Phong theo khung giong chu / vien (--px).
// ---------------------------------------------------------------------------
const CORNER_FULL = 500;

function cornerFieldsHtml(el) {
    const r = el.cornerRadius;
    const mode = r === null || r === undefined ? 'default' : r <= 0 ? 'square' : r >= CORNER_FULL ? 'full' : 'custom';
    const opt = (v, t) => `<option value="${v}" ${mode === v ? 'selected' : ''}>${t}</option>`;
    return ppCard('Góc bo tròn', `
        <div class="prop-grid-2">
            <div class="prop-field"><label>Kiểu bo góc</label>
                <select id="propCornerMode">
                    ${opt('default', 'Mặc định')}
                    ${opt('square', 'Vuông góc (không bo)')}
                    ${opt('custom', 'Tuỳ chọn (px)')}
                    ${opt('full', 'Bo tròn hết cỡ')}
                </select></div>
            <div class="prop-field" id="propCornerPxBox" style="${mode === 'custom' ? '' : 'display:none;'}">
                <label>Bán kính (px)</label>
                <input id="propCornerPx" type="number" min="1" max="${CORNER_FULL - 1}" step="1" value="${mode === 'custom' ? r : 12}" /></div>
        </div>
        ${ppHint('"Bo tròn hết cỡ": nút thành hình viên thuốc, ảnh vuông thành hình tròn.')}`);
}

function applyCornerFromForm(el) {
    const modeEl = document.getElementById('propCornerMode');
    if (!modeEl) return;
    const mode = modeEl.value;
    if (mode === 'square') el.cornerRadius = 0;
    else if (mode === 'full') el.cornerRadius = CORNER_FULL;
    else if (mode === 'custom') {
        const v = parseFloat(document.getElementById('propCornerPx').value);
        el.cornerRadius = Number.isFinite(v) && v > 0 ? Math.min(v, CORNER_FULL - 1) : null;
    } else el.cornerRadius = null;
}

function bindCornerFields() {
    const m = document.getElementById('propCornerMode'), box = document.getElementById('propCornerPxBox');
    if (m && box) m.addEventListener('change', () => { box.style.display = m.value === 'custom' ? '' : 'none'; });
}

function bindFillOpacityLabel() {
    const fo = document.getElementById('propFillOpacity');
    if (fo) fo.addEventListener('input', () => { document.getElementById('propFillOpacityVal').textContent = fo.value; });
}

async function saveCommonAndRefresh(el) {
    applyStyleFieldsFromForm(el);
    await persistAndRefresh(el);
}

// ---------------------------------------------------------------------------
// Panel: Thong ke cho
// ---------------------------------------------------------------------------
const STAT_DEFS = [
    { key: 'Free',     show: 'showStatFree',     cam: 'statFreeCamera',     vars: 'statFreeVars',     count: 'Free',     text: 'Số chỗ trống (Còn trống)' },
    { key: 'Occupied', show: 'showStatOccupied', cam: 'statOccupiedCamera', vars: 'statOccupiedVars', count: 'Occupied', text: 'Số chỗ có xe (Đã đỗ)' },
    { key: 'Error',    show: 'showStatError',    cam: 'statErrorCamera',    vars: 'statErrorVars',    count: 'Error',    text: 'Số lỗi (camera mất kết nối / cảm biến lỗi)' }
];

async function renderStatsProps(el) {
    let msVars = [];
    try {
        const r = await fetch('/api/system/sensors/variables', { cache: 'no-store' });
        if (r.ok) msVars = (await r.json()).variables || [];
    } catch { /* khong co danh sach bien MS: van sua duoc cac thuoc tinh khac */ }
    if (selectedId !== el.id) return;   // da chon doi tuong khac trong luc cho tai danh sach bien MS

    const st = el.stats || { free: 0, occupied: 0, error: 0, camerasError: 0 };
    const orient = el.statsOrientation === 'Horizontal' ? 'Horizontal' : 'Vertical';
    const vars = {};                                           // danh sach bien MS dang sua cua tung so lieu
    STAT_DEFS.forEach(d => { vars[d.key] = [...(el[d.vars] || [])]; });

    const defBlock = (d) => `
        <div class="stat-cfg">
            <label class="checkbox-label"><input type="checkbox" id="propStat${d.key}" ${el[d.show] === false ? '' : 'checked'} /> <b>${d.text}</b></label>
            <label class="checkbox-label stat-sub-opt"><input type="checkbox" id="propStat${d.key}Cam" ${el[d.cam] === false ? '' : 'checked'} /> Tính từ camera (vùng nhận diện)</label>
            <div class="stat-sub-opt">
                <div class="hint small" style="margin:2px 0;">Cộng thêm biến MS (cảm biến):</div>
                <div class="stat-chips" id="propStat${d.key}Vars"></div>
                <div class="prop-inline">
                    <select id="propStat${d.key}Sel" style="flex:1;min-width:0;"></select>
                    <button type="button" class="btn secondary" id="propStat${d.key}Add" style="padding:6px 10px;">＋ Thêm</button>
                </div>
            </div>
        </div>`;

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'values', label: 'Số liệu', html:
            ppCard(`Thống kê chỗ đỗ · tầng ${escHtml(CURRENT_FLOOR)}`, `
                <p class="hint small pp-hint">Hiện: <strong>${st.free}</strong> trống · <strong>${st.occupied}</strong> có xe · <strong>${st.error}</strong> lỗi
                (camera: ${st.cameraFree ?? st.free} / ${st.cameraOccupied ?? st.occupied} / ${st.cameraError ?? st.error}
                · cảm biến: ${st.sensorFree ?? 0} / ${st.sensorOccupied ?? 0} / ${st.sensorError ?? 0};
                ${st.camerasError} camera không lấy được ảnh). Số liệu = camera + tổng các biến MS đã thêm, cập nhật realtime.</p>`, 'wide')
            + STAT_DEFS.map(d => ppCard('', defBlock(d))).join('')
            + ppCard('', ppHint('Chỉ liệt kê biến MS đếm đúng trạng thái của số liệu (tạo ở Hệ thống › Cảm biến › Dữ liệu). Bỏ chọn "Tính từ camera" nếu chỉ muốn dùng cảm biến.'), 'wide') },
        { id: 'display', label: 'Cách hiển thị', html: ppCard('Cách hiển thị', `
            <div class="prop-field"><label>Hướng sắp xếp</label>
                <select id="propStatOrient">
                    <option value="Vertical" ${orient === 'Vertical' ? 'selected' : ''}>Dọc (xếp từ trên xuống)</option>
                    <option value="Horizontal" ${orient === 'Horizontal' ? 'selected' : ''}>Ngang (xếp từ trái sang phải)</option>
                </select></div>
            <div class="prop-grid-2">
                <div class="prop-field"><label>Cỡ chữ số (px)</label><input id="propStatNumSize" type="number" min="1" max="200" step="1" placeholder="mặc định 30" value="${el.fontSize ?? ''}" /></div>
                <div class="prop-field"><label>Cỡ chữ nhãn (px)</label><input id="propStatLblSize" type="number" min="1" max="200" step="1" placeholder="mặc định 12" value="${el.labelFontSize ?? ''}" /></div>
            </div>`, 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    const q = (id) => document.getElementById(id);
    const lower = (n) => String(n).toLowerCase();

    // Ve lai danh sach "chip" + o chon bien cho 1 so lieu
    const renderVars = (d) => {
        const box = q(`propStat${d.key}Vars`), sel = q(`propStat${d.key}Sel`), list = vars[d.key];
        box.innerHTML = list.length ? list.map(n => {
            const v = msVars.find(x => lower(x.name) === lower(n));
            const bad = !v || v.count !== d.count;
            const tip = !v ? 'Biến MS không còn tồn tại (tính là 0)' : bad ? 'Biến này đếm trạng thái khác với số liệu' : (v.description || '');
            const val = v && v.value !== null && v.value !== undefined ? ` = ${v.value}` : '';
            return `<span class="stat-chip${bad ? ' bad' : ''}" title="${escHtml(tip)}">${escHtml(n)}${val}<button type="button" data-rm="${escHtml(n)}" title="Bỏ biến này">✕</button></span>`;
        }).join('') : '<span class="hint small">Chưa có biến MS nào.</span>';

        const avail = msVars.filter(v => v.count === d.count && !list.some(n => lower(n) === lower(v.name)));
        sel.innerHTML = avail.length
            ? avail.map(v => `<option value="${escHtml(v.name)}">${escHtml(v.name)}${v.description ? ' — ' + escHtml(v.description) : ''}${v.value !== null && v.value !== undefined ? ` (hiện ${v.value})` : ''}</option>`).join('')
            : '<option value="">— Không còn biến MS phù hợp —</option>';
        q(`propStat${d.key}Add`).disabled = !avail.length;
    };
    const changed = (target) => target.dispatchEvent(new Event('change', { bubbles: true }));   // kich hoat xem truoc truc tiep

    STAT_DEFS.forEach(d => {
        renderVars(d);
        q(`propStat${d.key}Add`).addEventListener('click', (e) => {
            const name = q(`propStat${d.key}Sel`).value;
            if (!name) return;
            vars[d.key].push(name);
            renderVars(d);
            changed(e.currentTarget);
        });
        q(`propStat${d.key}Vars`).addEventListener('click', (e) => {
            const b = e.target.closest('button[data-rm]');
            if (!b) return;
            vars[d.key] = vars[d.key].filter(n => lower(n) !== lower(b.dataset.rm));
            renderVars(d);
            changed(q(`propStat${d.key}Add`));
        });
    });

    const applyForm = (el) => {
        STAT_DEFS.forEach(d => {
            el[d.show] = q(`propStat${d.key}`).checked;
            el[d.cam] = q(`propStat${d.key}Cam`).checked;
            el[d.vars] = [...vars[d.key]];
        });
        el.statsOrientation = q('propStatOrient').value;
        const fs = parseFloat(q('propStatNumSize').value), ls = parseFloat(q('propStatLblSize').value);
        el.fontSize = Number.isFinite(fs) && fs > 0 ? fs : null;
        el.labelFontSize = Number.isFinite(ls) && ls > 0 ? ls : null;
        applyStyleFieldsFromForm(el);
    };
    bindLivePreview(el, applyForm);
    q('savePropsBtn').addEventListener('click', async () => {
        applyForm(el);
        await persistAndRefresh(el);
    });
    q('deleteElBtn').addEventListener('click', () => deleteElement(el.id));
    fitPropsDialog();
}

// ---------------------------------------------------------------------------
// Panel: Text / Hinh anh
// ---------------------------------------------------------------------------
// Chon anh tu may: tai len /api/layout/upload-image roi dien URL tra ve vao o (kich hoat xem truoc truc tiep)
function bindImageUpload() {
    const fo = document.getElementById('propFillOpacity');
    if (fo) fo.addEventListener('input', () => { document.getElementById('propFillOpacityVal').textContent = fo.value; });
    bindImgPick({ urlId: 'propImageUrl', upId: 'propImgUp', clrId: 'propImgClr', fileId: 'propImgFile', viewId: 'propImgView' });
}

function renderGenericProps(el) {
    // Hiện chỉ còn loại Hình ảnh đi qua đây (Text có hàm riêng)
    const imageTab = ppCard('Ảnh hiển thị', imgPickHtml({
        urlId: 'propImageUrl', upId: 'propImgUp', clrId: 'propImgClr', fileId: 'propImgFile', viewId: 'propImgView', value: el.imageUrl
    }) + ppHint('PNG/JPG/GIF/WEBP/BMP, tối đa 5 MB. Ảnh PNG/WEBP nền trong suốt nên tích "Không tô nền (trong suốt)" ở tab Kiểu dáng.'), 'wide');

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'image', label: 'Hình ảnh', html: imageTab },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el, { shape: true, corner: true }) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    bindImageUpload(); bindCornerFields(); bindFillOpacityLabel();

    const applyForm = (el) => {
        el.imageUrl = document.getElementById('propImageUrl').value;
        applyCornerFromForm(el);
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
// Độ mờ chung (xoay / lớp nằm ở tab ribbon "Định dạng")
// ---------------------------------------------------------------------------
function effectsFieldsHtml(el) {
    return ppCard('Độ mờ', `
        <div class="prop-field"><label>Độ mờ chung của đối tượng (%)</label>
            <input id="propOpacity" type="number" min="0" max="100" step="5" value="${el.opacity ?? 100}" /></div>
        ${ppHint('100% = rõ nét, 0% = trong suốt hoàn toàn.')}`);
}

function bindEffectsButtons() { /* không còn dùng: Xoay / Lớp nằm ở tab ribbon "Định dạng" */ }

function applyEffectsFromForm(el) {
    const o = document.getElementById('propOpacity');
    if (!o) return;
    const op = parseFloat(o.value);
    el.opacity = Number.isFinite(op) && op < 100 ? Math.max(0, op) : null;
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

    const shapeCard = isCircle ? '' : ppCard('Hình dạng', `
        <div class="prop-field"><label>Bo góc (px)</label>
            <input id="propCorner" type="number" min="0" max="500" step="1" placeholder="vuông góc" value="${el.cornerRadius ?? ''}" /></div>`);

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'content', label: 'Chữ trong hình', html: ppCard('Chữ trong hình (tuỳ chọn)', `
            <div class="prop-field"><label>Nội dung</label>
                <textarea id="propContent" rows="3">${escHtml(el.content)}</textarea></div>
            ${fontFieldsHtml(el)}`, 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: shapeCard + styleFieldsHtml(el, { shape: true }) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    bindFillOpacityLabel();

    const applyForm = (el) => {
        el.content = document.getElementById('propContent').value || null;
        applyFontFromForm(el);
        if (!isCircle) {
            const cr = parseFloat(document.getElementById('propCorner').value);
            el.cornerRadius = Number.isFinite(cr) && cr > 0 ? cr : null;
        }
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
// Panel: Duong thang (mau / do day / kieu net / mui ten / chieu dai / goc xoay)
// ---------------------------------------------------------------------------
function renderLineProps(el) {
    const dash = el.borderStyle && el.borderStyle !== 'none' ? el.borderStyle : 'solid';
    const arrow = el.lineArrow || 'none';
    const opt = (v, cur, text) => `<option value="${v}" ${v === cur ? 'selected' : ''}>${text}</option>`;

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'stroke', label: 'Nét vẽ', html: ppCard('Nét vẽ', `
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
                </select></div>`)
            + effectsFieldsHtml(el)
            + ppCard('Gợi ý', ppHint('Chiều dài, vị trí và <b>góc xoay</b> chỉnh ở tab <b>Định dạng</b> trên thanh công cụ. Dùng góc 90° để vẽ đường đứng; "Cao" chỉ là độ cao vùng chọn để dễ bấm chọn.'), 'wide') }
    ]) + ppFooter();

    const applyForm = (el) => {
        el.borderColor = document.getElementById('propLineColor').value;
        el.borderWidth = Math.max(1, Math.min(30, parseFloat(document.getElementById('propLineWidth').value) || 3));
        el.borderStyle = document.getElementById('propLineStyle').value;
        el.lineArrow = document.getElementById('propLineArrow').value;
        el.backgroundColor = null;
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
        .concat(floors.filter(f => !isPanelFloor(f)).map(f =>   // nút chỉ chuyển tới mặt bằng đỗ xe (Header / Navigation luôn hiển thị)
            `<option value="${f.id}" ${el.targetFloorId === f.id ? 'selected' : ''}>${f.id} — ${f.name}</option>`))
        .join('');

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'button', label: 'Nút ấn', html:
            ppCard('Nhãn & hành động', `
                <div class="prop-field">
                    <label>Nhãn nút</label>
                    <input id="propLabel" type="text" value="${escHtml(el.label)}" />
                </div>
                <div class="prop-field">
                    <label>Hành động khi bấm: chuyển đến mặt bằng</label>
                    <select id="propTargetFloor">${floorOptions}</select>
                </div>
                ${ppHint('Hành động chỉ hoạt động ở chế độ "▶️ Chạy". Ở chế độ Build, bấm vào nút để chỉnh sửa.')}`)
            + ppCard('Nhận biết mặt bằng đang xem', `
                <label class="checkbox-label" style="margin-bottom:10px;"><input type="checkbox" id="propBtnActiveOn" ${el.buttonActiveEnabled === false ? '' : 'checked'} /> Đổi màu nút khi đang ở mặt bằng đích</label>
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Màu nền khi đang ở đó</label><input id="propBtnActiveBg" type="color" value="${el.activeBackgroundColor || BTN_ACTIVE_BG}" /></div>
                    <div class="prop-field"><label>Màu chữ khi đang ở đó</label><input id="propBtnActiveFg" type="color" value="${el.activeTextColor || BTN_ACTIVE_FG}" /></div>
                </div>
                ${ppHint('Nút được tô màu (kèm vòng sáng) khi mặt bằng đang xem chính là mặt bằng đích của nút. Nếu nút đang chọn "Không tô nền" thì luôn trong suốt ở mọi mặt bằng. Xem thử: chọn mặt bằng đích trùng với tầng đang thiết kế.')}`) },
        { id: 'text', label: 'Chữ', html: ppCard('Chữ trên nút', `
                <div class="prop-field"><label>Căn lề</label>${alignSelectHtml(el, 'center')}</div>
                ${fontFieldsHtml(el, '#ffffff')}
                <div class="prop-inline">
                    <label class="checkbox-label"><input type="checkbox" id="propBtnBold" ${el.fontBold === false ? '' : 'checked'} /> In đậm</label>
                    <label class="checkbox-label"><input type="checkbox" id="propBtnItalic" ${el.fontItalic ? 'checked' : ''} /> In nghiêng</label>
                </div>`, 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el, { shape: true, corner: true }) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    bindCornerFields();
    bindFillOpacityLabel();

    const applyForm = (el) => {
        el.label = document.getElementById('propLabel').value;
        el.targetFloorId = document.getElementById('propTargetFloor').value || null;
        el.textAlign = document.getElementById('propAlign').value || null;
        applyFontFromForm(el, '#ffffff');
        el.fontBold = document.getElementById('propBtnBold').checked ? null : false;
        el.fontItalic = document.getElementById('propBtnItalic').checked ? true : null;
        el.buttonActiveEnabled = document.getElementById('propBtnActiveOn').checked;
        const abg = document.getElementById('propBtnActiveBg').value, afg = document.getElementById('propBtnActiveFg').value;
        el.activeBackgroundColor = abg && abg.toLowerCase() !== BTN_ACTIVE_BG ? abg : null;      // = mac dinh thi khong luu
        el.activeTextColor = afg && afg.toLowerCase() !== BTN_ACTIVE_FG ? afg : null;
        applyCornerFromForm(el);
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

    const LBL = { showStatusColor: 'Màu trạng thái (đỏ = có xe, vàng = lỗi camera, xanh = trống)', showStatusText: 'Chữ trạng thái (Lỗi / Đã đỗ / Còn trống)',
        showPlate: 'Biển số xe đang đỗ', showEntryTime: 'Thời gian xe vào', showLastExitPlate: 'Biển xe gần nhất ra khỏi vị trí', showExitTime: 'Thời gian ra' };

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'status', label: 'Trạng thái', html:
            ppCard(`Vị trí nhận diện${floorZoneCount > 1 ? ' · Zone ' + escHtml(slotZoneOf(el) || '—') : ''} · số ${escHtml(slotNumberOf(el))}`, `
                <div class="slot-live-status ${SLOT_STATUS_CLASS[slot.status]}">${SLOT_STATUS_LABEL[slot.status]}</div>
                <div class="prop-field">
                    <label>Trạng thái</label>
                    <select id="slotStatusSelect">
                        <option value="Empty" ${slot.status === 'Empty' ? 'selected' : ''}>Còn trống</option>
                        <option value="Occupied" ${slot.status === 'Occupied' ? 'selected' : ''}>Đã đỗ</option>
                        <option value="Error" ${slot.status === 'Error' ? 'selected' : ''}>Lỗi</option>
                    </select>
                </div>
                <div id="occupiedFields" style="display:${slot.status === 'Occupied' ? 'block' : 'none'}">
                    <div class="prop-field"><label>Biển số xe đã đỗ</label>
                        <input id="slotPlate" type="text" value="${slot.plateNumber ?? ''}" placeholder="51K-207.69" /></div>
                    <div class="prop-field"><label>Thời điểm đỗ</label>
                        <input id="slotParkedAt" type="datetime-local" value="${parkedAtLocal}" /></div>
                </div>
                <div id="errorFields" class="prop-field" style="display:${slot.status === 'Error' ? 'block' : 'none'}">
                    <label>Mô tả lỗi</label>
                    <textarea id="slotError" rows="2" placeholder="Ví dụ: Camera mất kết nối">${slot.errorMessage ?? ''}</textarea>
                </div>`)
            + ppCard('Lịch sử gần nhất', `
                <div class="slot-last-exit" style="margin:0;">Xe ra gần nhất: <strong>${escHtml(fmtPlate(slot.lastExitPlate))}</strong><br>Lúc ${escHtml(fmtDateTime(slot.lastExitTime))}</div>
                ${ppHint('Chỉnh tay trạng thái để test giao diện khi chưa có camera / model AI thật. Khi AI hoạt động, trạng thái tự cập nhật realtime và ghi đè giá trị chỉnh tay.')}`) },
        { id: 'display', label: 'Hiển thị', html: ppCard('Hiển thị trên bản đồ', DISPLAY_FIELDS.map(f => `
            <label class="checkbox-label">
                <input type="checkbox" class="slot-display-chk" data-field="${f}" ${el[f] === false ? '' : 'checked'} /> ${LBL[f]}
            </label>`).join(''), 'wide') },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el)
            + ppCard('Gợi ý', ppHint('Màu nền / viền mặc định của ô đỗ đổi theo trạng thái; chỉ đặt màu tuỳ chỉnh nếu muốn cố định.'), 'wide') }
    ]) + ppFooter('🗑️ Gỡ khỏi bản đồ', 'saveSlotBtn');

    const applyForm = (el) => {
        document.querySelectorAll('.slot-display-chk').forEach(c => { el[c.dataset.field] = c.checked; });
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
    let bits = [], camZones = [];
    try {
        const r = await fetch('/api/tasks/tags/bits');
        if (r.ok) bits = await r.json();
    } catch { /* van cho sua cac thuoc tinh khac */ }
    try {
        const r = await fetch('/api/system/sensors/status', { cache: 'no-store' });
        if (r.ok) sensorMeta = await r.json();
    } catch { /* khong co thong tin cam bien: dung mac dinh MGT200 */ }
    try {
        const r = await fetch(`/api/layout/zones?floor=${encodeURIComponent(CURRENT_FLOOR)}`);
        if (r.ok) camZones = await r.json();
    } catch { /* khong co danh sach vi tri */ }
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
    // key: '0' | '1' (tag bit) hoac 'Parked' | 'Free' | 'Error' | 'Unused' (den cam bien / vi tri do camera)
    // Mỗi trạng thái 1 thẻ có vùng xem ảnh (nền ô vuông); chưa chọn ảnh thì vùng xem hiện hình mặc định
    const DEF_DOT = { '0': '#64748b', '1': '#22c55e', Parked: '#ef4444', Free: '#22c55e', Error: '#eab308', Unused: '#334155' };
    const imgBlock = (key, title, grp) => ppCard(title, imgPickHtml({
        urlId: `propBitImg${key}`, upId: `propBitUp${key}`, clrId: `propBitClr${key}`, fileId: `propBitFile${key}`, viewId: `propBitPrev${key}`,
        value: el['stateImage' + key], small: true, placeholder: 'URL ảnh hoặc tải lên…'
    }), '', `data-grp="${grp}"`);
    const LAMP_DOT_HINT = { Parked: '🔴', Free: '🟢', Error: '🟡', Unused: '⚪' };
    const sensorKeytop = sensorMeta?.type === 'Keytop';
    const sensorDevCount = Math.max(sensorMeta?.deviceCount || 32, (el.sensorDevice || 0) + 1);
    const sensorPer = sensorMeta?.sensorsPerDevice || (sensorKeytop ? 60 : 160);
    const sIdx = Math.max(0, el.sensorIndex || 0);
    const devOpt = (d) => {
        const info = sensorMeta?.devices?.[d];
        return `<option value="${d}" ${d === (el.sensorDevice || 0) ? 'selected' : ''}>${sensorKeytop ? 'Keytop' : 'ZCU'} ${d}${info && !info.enabled ? ' (không kích hoạt)' : ''}</option>`;
    };
    const rangeOpts = (from, to, sel, fmt) => Array.from({ length: to - from + 1 }, (_, k) => from + k)
        .map(v => `<option value="${v}" ${v === sel ? 'selected' : ''}>${fmt(v)}</option>`).join('');
    const camSel = el.lampPosition ? `${el.lampZone || '1'}|${el.lampPosition}` : '';
    const camKnown = camZones.some(z => `${z.zone}|${z.number}` === camSel);
    const camOptions = `<option value="">— Chọn vị trí đỗ —</option>`
        + (camSel && !camKnown ? `<option value="${escHtml(camSel)}" selected>⚠ Zone ${escHtml(el.lampZone || '1')} · vị trí ${escHtml(el.lampPosition)} (chưa khai báo trên camera)</option>` : '')
        + camZones.map(z => `<option value="${escHtml(z.zone)}|${escHtml(z.number)}" ${`${z.zone}|${z.number}` === camSel ? 'selected' : ''}>Zone ${escHtml(z.zone)} · vị trí ${escHtml(z.number)}${z.cameraIp ? ' — camera ' + escHtml(z.cameraIp) : ''}</option>`).join('');
    const curSrc = bitSourceOf(el);

    propsPanel.innerHTML = ppTabs(el.type, [
        { id: 'general', label: 'Chung', html:
            ppCard('Chế độ hoạt động', `
                <div class="prop-field"><label>Ghi chú (Comment)</label>
                    <input id="propBitComment" type="text" maxlength="200" value="${escHtml(el.comment)}" /></div>
                <div class="prop-field">
                    <label class="checkbox-label"><input type="radio" name="propBitMode" value="Lamp" ${mode === 'Lamp' ? 'checked' : ''} /> Bit Lamp (chỉ hiển thị)</label>
                    <label class="checkbox-label"><input type="radio" name="propBitMode" value="Switch" ${mode === 'Switch' ? 'checked' : ''} /> Toggle Switch (bấm để ghi, ở chế độ Chạy)</label>
                </div>
                <div class="prop-field" id="propBitSourceRow">
                    <label>Nguồn dữ liệu của đèn</label>
                    <select id="propBitSource">
                        <option value="Tag" ${curSrc === 'Tag' ? 'selected' : ''}>Tag bit (0 / 1)</option>
                        ${isPanelFloor(window.currentFloorData) ? '' : `<option value="Sensor" ${curSrc === 'Sensor' ? 'selected' : ''}>Cảm biến (Đã đỗ / Còn trống / Lỗi / Chưa sử dụng)</option>
                        <option value="CameraSlot" ${curSrc === 'CameraSlot' ? 'selected' : ''}>Vị trí đỗ chỉ định trên camera (4 trạng thái)</option>`}
                    </select>
                </div>`)
            + `<div id="propBitSensorGroup" class="pp-contents">` + ppCard('Cảm biến', `
                <div class="prop-field"><label>Thiết bị</label>
                    <select id="propSnDevice">${Array.from({ length: sensorDevCount }, (_, d) => devOpt(d)).join('')}</select></div>
                <div class="prop-grid-2" id="propSnPmRow">
                    <div class="prop-field"><label>Line (CN)</label>
                        <select id="propSnLine">${rangeOpts(1, 5, Math.floor(sIdx / 32) + 1, v => 'CN ' + v)}</select></div>
                    <div class="prop-field"><label>ID cảm biến</label>
                        <select id="propSnId">${rangeOpts(0, 31, sIdx % 32, v => 'ID ' + v)}</select></div>
                </div>
                <div class="prop-field" id="propSnKtRow"><label>Cảm biến số</label>
                    <select id="propSnNum">${rangeOpts(1, sensorPer, sIdx + 1, v => 'Cảm biến ' + v)}</select></div>
                ${ppHint('Có xe → <b>Đã đỗ</b> · Trống → <b>Còn trống</b> · Lỗi hoặc thiết bị mất kết nối → <b>Lỗi</b> · Thiết bị không kích hoạt / ngoài số thiết bị đang dùng / cảm biến chưa báo trạng thái → <b>Chưa sử dụng</b>.')}`) + `</div>`
            + `<div id="propBitCamGroup" class="pp-contents">` + ppCard('Vị trí đỗ chỉ định trên camera', `
                <div class="prop-field"><label>Vị trí nhận diện (tầng ${escHtml(CURRENT_FLOOR)})</label>
                    <select id="propCamPos">${camOptions}</select></div>
                ${ppHint('Lấy từ các vị trí đã vẽ ở Hệ thống › Camera › Vị trí đỗ. Xe trong vùng → <b>Đã đỗ</b> · không có xe → <b>Còn trống</b> · camera mất kết nối → <b>Lỗi</b> · vị trí chưa khai báo (đã bị xoá khỏi camera) → <b>Chưa sử dụng</b>.')}`) + `</div>`
            + `<div id="propBitTagGroup" class="pp-contents">` + ppCard('Đọc / Ghi', `
                <label class="checkbox-label" style="margin-bottom:10px;"><input type="checkbox" id="propBitSep" ${el.separateWriteTag ? 'checked' : ''} /> Đọc và ghi dùng tag khác nhau</label>
                <div class="prop-field"><label id="propBitReadLbl">Tag</label><select id="propBitReadTag"></select></div>
                <div class="prop-field" id="propBitWriteRow"><label>Tag ghi (Project tag)</label><select id="propBitWriteTag"></select></div>
                <label class="checkbox-label" style="margin-bottom:8px;"><input type="checkbox" id="propBitInvert" ${el.invertSignal ? 'checked' : ''} /> Đảo tín hiệu (Invert signal)</label>
                <label class="checkbox-label"><input type="checkbox" id="propBitRelease" ${el.writeOnRelease ? 'checked' : ''} /> Chỉ ghi khi nhả nút</label>`)
            + ppCard('Kiểu công tắc', `
                <div class="prop-field"><label>Kiểu công tắc (Switch style)</label>
                    <select id="propBitStyle">
                        <option value="SetOn" ${style === 'SetOn' ? 'selected' : ''}>Set ON — luôn ghi ON</option>
                        <option value="SetOff" ${style === 'SetOff' ? 'selected' : ''}>Set OFF — luôn ghi OFF</option>
                        <option value="Toggle" ${style === 'Toggle' ? 'selected' : ''}>Toggle — đảo trạng thái</option>
                        <option value="Momentary" ${style === 'Momentary' ? 'selected' : ''}>Momentary — giữ = ON, nhả = OFF</option>
                    </select></div>
                ${ppHint('System tags chỉ để đọc (đèn). Muốn bấm để ghi, tag ghi phải là Project tag kiểu Boolean (TASKS › Tags).')}`) + `</div>` },
        { id: 'shape', label: 'Hình ảnh', html:
            imgBlock('0', 'Trạng thái 0 (OFF)', 'tag')
            + imgBlock('1', 'Trạng thái 1 (ON)', 'tag')
            + LAMP_STATES.map(k => imgBlock(k, `${LAMP_DOT_HINT[k]} ${LAMP_LABEL[k]}`, 'lamp')).join('')
            + ppCard('', ppHint('Không chọn ảnh = vẽ hình mặc định (đèn tròn / công tắc; đèn 4 trạng thái: đỏ = Đã đỗ, xanh = Còn trống, vàng = Lỗi, xám = Chưa sử dụng). Ảnh PNG/JPG/GIF/WEBP/BMP, tối đa 5 MB. Ô vuông xám trắng trong vùng xem là phần ảnh trong suốt.'), 'wide') },
        { id: 'label', label: 'Nhãn', html:
            ppCard('Chữ hiển thị theo trạng thái', `
                <div class="prop-field" data-grp="tag"><label>Chữ khi trạng thái 0 (OFF)</label><input id="propBitText0" type="text" maxlength="64" value="${escHtml(el.stateText0)}" /></div>
                <div class="prop-field" data-grp="tag"><label>Chữ khi trạng thái 1 (ON)</label><input id="propBitText1" type="text" maxlength="64" value="${escHtml(el.stateText1)}" /></div>
                ${LAMP_STATES.map(k => `<div class="prop-field" data-grp="lamp"><label>Chữ khi ${LAMP_LABEL[k].toLowerCase()}</label><input id="propBitText${k}" type="text" maxlength="64" value="${escHtml(el['stateText' + k])}" /></div>`).join('')}`)
            + ppCard('Định dạng chữ', `
                <div class="prop-grid-2">
                    <div class="prop-field"><label>Màu chữ</label><input id="propBitTextColor" type="color" value="${escHtml(el.textColor || '#e6ebf5')}" /></div>
                    <div class="prop-field"><label>Cỡ chữ (px)</label><input id="propBitFont" type="number" min="1" max="200" step="1" placeholder="mặc định" value="${el.fontSize ?? ''}" /></div>
                </div>`) },
        { id: 'style', label: 'Kiểu dáng', html: styleFieldsHtml(el) + effectsFieldsHtml(el) }
    ]) + ppFooter();

    // --- danh sach tag + bat/tat o theo che do ---
    const curMode = () => propsPanel.querySelector('input[name="propBitMode"]:checked')?.value || 'Switch';
    q('propBitReadTag').innerHTML = tagOptions(el.readTag, mode === 'Switch' && !el.separateWriteTag);
    q('propBitWriteTag').innerHTML = tagOptions(el.writeTag, true);

    const curSource = () => curMode() === 'Lamp' ? (q('propBitSource').value || 'Tag') : 'Tag';
    const syncForm = () => {
        const src = curSource();
        q('propBitSourceRow').style.display = curMode() === 'Lamp' ? '' : 'none';
        q('propBitTagGroup').style.display = src === 'Tag' ? '' : 'none';
        q('propBitSensorGroup').style.display = src === 'Sensor' ? '' : 'none';
        q('propBitCamGroup').style.display = src === 'CameraSlot' ? '' : 'none';
        q('propSnPmRow').style.display = sensorKeytop ? 'none' : '';
        q('propSnKtRow').style.display = sensorKeytop ? '' : 'none';
        propsPanel.querySelectorAll('[data-grp]').forEach(n => { n.hidden = (n.dataset.grp === 'lamp') !== (src !== 'Tag'); });
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
    q('propBitSource').addEventListener('change', () => { syncForm(); fitPropsDialog(); });
    q('propBitStyle').addEventListener('change', syncForm);
    syncForm();

    // --- hinh anh theo trang thai (vung xem truoc + tai len / bo anh) ---
    ['0', '1', ...LAMP_STATES].forEach(i => bindImgPick({
        urlId: `propBitImg${i}`, upId: `propBitUp${i}`, clrId: `propBitClr${i}`, fileId: `propBitFile${i}`, viewId: `propBitPrev${i}`,
        emptyHtml: `<span class="pp-img-dot" style="background:${DEF_DOT[i]}"></span><span>Mặc định</span>`
    }));

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

        // Nguon Cam bien / Vi tri do tren camera (chi o che do Lamp)
        const src = curSource();
        el.bitSource = src === 'Tag' ? null : src;
        el.sensorDevice = parseInt(q('propSnDevice').value, 10) || 0;
        el.sensorIndex = sensorKeytop
            ? Math.max(0, (parseInt(q('propSnNum').value, 10) || 1) - 1)
            : ((parseInt(q('propSnLine').value, 10) || 1) - 1) * 32 + (parseInt(q('propSnId').value, 10) || 0);
        const camVal = q('propCamPos').value;
        el.lampZone = camVal ? camVal.split('|')[0] : null;
        el.lampPosition = camVal ? camVal.split('|')[1] : null;
        LAMP_STATES.forEach(k => {
            el['stateImage' + k] = q('propBitImg' + k).value.trim() || null;
            el['stateText' + k] = q('propBitText' + k).value || null;
        });
        const tc = q('propBitTextColor').value;
        el.textColor = (tc.toLowerCase() !== '#e6ebf5' || el.textColor) ? tc : null;
        const fs = parseFloat(q('propBitFont').value);
        el.fontSize = Number.isFinite(fs) && fs > 0 ? fs : null;
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

// ---------------------------------------------------------------------------
// FORMAT TAB (ribbon "Dinh dang"): Vi tri X/Y, Kich thuoc Rong/Cao, Goc xoay, Lop cua doi tuong dang chon.
// Tach khoi hop thoai thuoc tinh; sua o day xem truoc ngay tren ban do va tu luu.
// ---------------------------------------------------------------------------
const FMT_INPUTS = { fmtX: 'x', fmtY: 'y', fmtW: 'w', fmtH: 'h', fmtRot: 'rot', fmtZ: 'z' };
const FMT_BUTTONS = { fmtCenterH: 'centerH', fmtCenterV: 'centerV', fmtRotL: 'rotL', fmtRotR: 'rotR', fmtBack: 'back', fmtFront: 'front' };
const fmtLock = { id: null, on: false, ratio: 1 };   // khoa ti le Rong : Cao cua doi tuong dang chon
let fmtSaveTimer = null, fmtDebounce = null;
const fmtById = (id) => document.getElementById(id);
const fmtRound = (v, n = 2) => { const k = 10 ** n; return Math.round(v * k) / k; };
const fmtCurrent = () => (editMode && selectedIds.size === 1 ? elements.find(x => x.id === selectedId) || null : null);

function syncFormatTab(force) {
    const panel = fmtById('fmtPanel');
    if (!panel) return;
    const el = fmtCurrent();
    panel.classList.toggle('fmt-off', !el);
    [...Object.keys(FMT_INPUTS), ...Object.keys(FMT_BUTTONS), 'fmtLock'].forEach(id => { fmtById(id).disabled = !el; });

    const name = fmtById('fmtSelName'), hint = fmtById('fmtSelHint');
    if (!el) {
        const many = selectedIds.size > 1;
        name.textContent = many ? `${selectedIds.size} đối tượng` : 'Chưa chọn đối tượng';
        hint.textContent = many ? 'Chuột phải để căn chỉnh nhóm' : 'Nhấp vào 1 đối tượng trên bản đồ';
        Object.keys(FMT_INPUTS).forEach(id => { fmtById(id).value = ''; });
        fmtById('fmtLock').checked = false;
        return;
    }
    name.textContent = typeLabel(el.type);
    hint.textContent = `Mặt bằng ${CURRENT_FLOOR}`;

    // Doi tuong moi duoc chon: hinh tron dang tron deu thi tu khoa ti le
    if (fmtLock.id !== el.id) {
        fmtLock.id = el.id;
        fmtLock.ratio = el.height / el.width || 1;
        fmtLock.on = el.type === 'Circle' && Math.abs(el.height - el.width * canvasAspect()) < 0.3;
    }
    const isLine = el.type === 'Line';
    fmtById('fmtLock').checked = fmtLock.on && !isLine;
    fmtById('fmtLock').disabled = isLine;
    fmtById('fmtWLbl').textContent = isLine ? 'Dài:' : 'Rộng:';
    fmtById('fmtHLbl').textContent = isLine ? 'Vùng chọn:' : 'Cao:';

    // Vi tri / kich thuoc doc tu node (dang keo / co gian thi chua ghi vao el)
    const node = canvas.querySelector(`.layout-el[data-id="${el.id}"]`);
    const g = (prop, fb) => { const v = node ? parseFloat(node.style[prop]) : NaN; return Number.isFinite(v) ? v : fb; };
    const vals = { fmtX: g('left', el.x), fmtY: g('top', el.y), fmtW: g('width', el.width), fmtH: g('height', el.height),
        fmtRot: Number(el.rotation ?? 0), fmtZ: Number(el.zIndex ?? 0) };
    Object.entries(vals).forEach(([id, v]) => {
        const n = fmtById(id);
        if (force || document.activeElement !== n) n.value = String(fmtRound(v));
    });
}

function fmtApply(src) {
    const el = fmtCurrent();
    if (!el) return;
    const num = (id, fb) => { const v = parseFloat(fmtById(id).value); return Number.isFinite(v) ? v : fb; };
    const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
    const normAngle = (a) => { a = ((a % 360) + 360) % 360; return a > 180 ? a - 360 : a; };

    if (src === 'w' || src === 'h') {
        let w = clamp(num('fmtW', el.width), 1, 100), h = clamp(num('fmtH', el.height), 1, 100);
        if (fmtLock.on && el.type !== 'Line') {
            if (src === 'w') h = w * fmtLock.ratio; else w = h / fmtLock.ratio;
            if (h > 100) { h = 100; w = h / fmtLock.ratio; }
            if (w > 100) { w = 100; h = w * fmtLock.ratio; }
        }
        el.width = fmtRound(w); el.height = fmtRound(h);
    }
    if (src === 'x') el.x = num('fmtX', el.x);
    if (src === 'y') el.y = num('fmtY', el.y);
    if (src === 'centerH') el.x = (100 - el.width) / 2;
    if (src === 'centerV') el.y = (100 - el.height) / 2;
    el.x = fmtRound(clamp(el.x, 0, 100 - el.width));
    el.y = fmtRound(clamp(el.y, 0, 100 - el.height));

    if (src === 'rot' || src === 'rotL' || src === 'rotR') {
        const cur = Number(el.rotation) || 0;
        const a = src === 'rot' ? clamp(num('fmtRot', cur), -360, 360) : normAngle(cur + (src === 'rotR' ? 90 : -90));
        el.rotation = a !== 0 ? a : null;
    }
    if (src === 'z') el.zIndex = clamp(Math.round(num('fmtZ', el.zIndex || 0)), -1000, 1000);
    if (src === 'back') el.zIndex = Math.min(0, ...elements.map(e => e.zIndex || 0)) - 1;
    if (src === 'front') el.zIndex = Math.max(0, ...elements.map(e => e.zIndex || 0)) + 1;

    refreshNode(el);                 // ve lai + cap nhat khung co gian + dong bo lai cac o cua tab
    clearTimeout(fmtSaveTimer);
    fmtSaveTimer = setTimeout(() => persistElement(el).catch(() => { /* loi mang: bo qua */ }), 300);
}

Object.entries(FMT_INPUTS).forEach(([id, src]) => {
    const inp = fmtById(id);
    if (!inp) return;
    inp.addEventListener('input', () => { clearTimeout(fmtDebounce); fmtDebounce = setTimeout(() => fmtApply(src), 120); });
    inp.addEventListener('change', () => { clearTimeout(fmtDebounce); fmtApply(src); syncFormatTab(true); });   // Enter / roi o: chuan hoa gia tri hien thi
});
Object.entries(FMT_BUTTONS).forEach(([id, src]) => {
    const b = fmtById(id);
    if (b) b.addEventListener('click', () => { fmtApply(src); syncFormatTab(true); });
});
fmtById('fmtLock')?.addEventListener('change', (e) => {
    const el = fmtCurrent();
    if (!el) return;
    fmtLock.on = e.target.checked;
    fmtLock.ratio = el.height / el.width || 1;
});

async function persistElement(el) {
    if (live && live.el === el) applyAnimFromForm(el);      // gom ca thay doi o 2 tab hieu ung dong chua kip xem truoc
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
    selectedId = null; selectedIds.clear();
    if (live) { clearTimeout(live.timer); live = null; }
    pending = null;
    propsPanel.innerHTML = EMPTY_PROPS_HTML;
    await loadLayout();
}

function typeLabel(type) {
    return { Button: 'Nút ấn', Image: 'Hình ảnh', Text: 'Text', ParkingSlot: 'Vùng nhận diện', SlotStats: 'Thống kê chỗ', BitLampSwitch: 'Đèn / Công tắc bit', Number: 'Số (Number)',
        Rectangle: 'Hình chữ nhật', Circle: 'Hình tròn', Line: 'Đường thẳng', DateTime: 'Ngày giờ' }[type] || type;
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
const runModeBtn = document.getElementById('runModeBtn');

runModeBtn.addEventListener('click', enterRunMode);

// Nho che do dang dung (theo tai khoan) de lan sau mo trang / chuyen trang van giu nguyen; tai khoan User luon Chay nen khong can nho
function saveLayoutMode(mode) {
    if (window.PG_FORCE_RUN || !window.PG_MODE_KEY) return;
    try { localStorage.setItem(window.PG_MODE_KEY, mode); } catch { /* bo qua */ }
}
function savedLayoutMode() {
    try { return window.PG_MODE_KEY ? localStorage.getItem(window.PG_MODE_KEY) : null; } catch { return null; }
}

// ---------------------------------------------------------------------------
// Header + Navigation khi CHẠY: mỗi màn hình là 1 iframe (/?panel=Header | Navigation) có kích thước pixel đã thiết lập.
//   Header     : góc trên-trái
//   Navigation : sát mép trái, ngay dưới Header
//   Mặt bằng đỗ xe: bắt đầu từ góc trên-trái của vùng còn lại (dưới Header, bên phải Navigation).
//   Toàn kích thước: Header rộng hết màn hình / Navigation cao hết phần còn lại.
// Tài khoản + nút Đăng xuất giữ nguyên vị trí cũ (góc dưới-trái); phần menu còn lại của thanh bên bị ẩn.
// ---------------------------------------------------------------------------
const PANEL_FRAMES = { Header: 'pgHeaderFrame', Navigation: 'pgNavFrame' };

// ---------------------------------------------------------------------------
// Chừa đáy màn hình khi CHẠY: thanh taskbar của Windows có thể che phần dưới cửa sổ (toàn màn hình / kiosk).
// Mặc định tự tính phần cửa sổ lấn xuống dưới vùng làm việc của màn hình; có thể nhập tay (px) ở ô "Chừa đáy khi chạy",
// lưu riêng từng máy. Trang bị trừ phần này: khung mặt bằng, Navigation, khối tài khoản.
// ---------------------------------------------------------------------------
const RUN_INSET_KEY = 'pg.runBottomInset';
let lastRunInset = -1;
// Toàn màn hình: Fullscreen API (document.fullscreenElement) hoặc F11 của trình duyệt (cửa sổ trùng kích thước màn hình)
function isRunFullscreen() {
    if (document.fullscreenElement) return true;
    try { return window.innerWidth >= screen.width - 1 && window.innerHeight >= screen.height - 1; } catch { return false; }
}
function computeRunInset() {
    if (isRunFullscreen()) return 0;       // toàn màn hình: taskbar đã bị che, không cần chừa
    try {
        const manual = localStorage.getItem(RUN_INSET_KEY);
        if (manual !== null && manual.trim() !== '' && Number.isFinite(+manual)) return Math.min(400, Math.max(0, Math.round(+manual)));
    } catch { /* bỏ qua */ }
    try {
        const bottom = (window.screenY ?? window.screenTop ?? 0) + window.outerHeight;           // đáy cửa sổ trên màn hình
        const availBottom = (screen.availTop ?? 0) + screen.availHeight;                         // đáy vùng làm việc (trên taskbar)
        return Math.min(200, Math.max(0, Math.round(bottom - availBottom)));
    } catch { return 0; }
}
function applyRunInset() {
    if (window.PG_PANEL) return;
    const v = computeRunInset();
    if (v === lastRunInset) return;
    lastRunInset = v;
    document.documentElement.style.setProperty('--run-bottom', `${v}px`);
    window.dispatchEvent(new Event('resize'));       // khung mặt bằng tính lại vừa vùng còn lại (lần sau v không đổi nên không lặp)
}
window.addEventListener('resize', applyRunInset);
window.addEventListener('resize', () => updateFsPrompt());      // F11 cũng đổi kích thước cửa sổ

// ---------------------------------------------------------------------------
// Toàn màn hình khi CHẠY (mặc định bật): ứng dụng che luôn thanh taskbar. Trình duyệt chỉ cho vào toàn màn hình sau 1 thao tác của người dùng:
// bấm nút Chạy là được ngay; mở lại trang đang ở chế độ Chạy thì chờ lần bấm chuột / phím đầu tiên.
// Thoát: ESC (trình duyệt thoát toàn màn hình trước, rồi chế độ Chạy cũng thoát theo — chỉ cần 1 lần ESC).
// ---------------------------------------------------------------------------
const RUN_FS_KEY = 'pg.runFullscreen';
const runFsEnabled = () => true;        // CỐ ĐỊNH: chạy luôn ở toàn màn hình (khung mặt bằng chỉ đúng tỉ lệ khi toàn màn hình)
let runFsActive = false;       // toàn màn hình do chế độ Chạy yêu cầu
let fsArm = null;              // AbortController của bộ chờ thao tác đầu tiên

function requestRunFullscreen() {
    if (window.PG_PANEL || !runFsEnabled() || isRunFullscreen()) return;
    const el = document.documentElement;
    if (!el.requestFullscreen) return;
    try { Promise.resolve(el.requestFullscreen({ navigationUI: 'hide' })).catch(() => { /* chưa có thao tác người dùng: bỏ qua */ }); } catch { /* bỏ qua */ }
}
function armRunFullscreen() {
    if (window.PG_PANEL || !runFsEnabled() || fsArm) return;
    fsArm = new AbortController();
    const go = () => { fsArm?.abort(); fsArm = null; if (!editMode) requestRunFullscreen(); };
    window.addEventListener('pointerdown', go, { capture: true, signal: fsArm.signal });
    window.addEventListener('keydown', go, { capture: true, signal: fsArm.signal });
}
// Chưa ở toàn màn hình (trình duyệt chỉ cho vào sau 1 thao tác của người dùng): hiện thanh nhắc, bấm / nhấn phím là vào ngay
function updateFsPrompt() {
    const need = !window.PG_PANEL && !editMode && document.body.classList.contains('layout-run-mode') && !isRunFullscreen();
    const cur = document.getElementById('pgFsPrompt');
    if (!need) { cur?.remove(); return; }
    if (cur) return;
    const b = document.createElement('button');
    b.id = 'pgFsPrompt'; b.type = 'button';
    b.innerHTML = '<span class="fs-ico">⛶</span><span>Chế độ Chạy chỉ hoạt động ở toàn màn hình<br><small>Nhấn vào đây để tiếp tục</small></span>';
    b.addEventListener('click', () => { requestRunFullscreen(); });
    document.body.appendChild(b);
}
function leaveRunFullscreen() {
    fsArm?.abort(); fsArm = null;
    runFsActive = false;
    if (document.fullscreenElement) { try { Promise.resolve(document.exitFullscreen()).catch(() => { }); } catch { /* bỏ qua */ } }
}
document.addEventListener('fullscreenchange', () => {
    setTimeout(applyRunInset, 150);
    updateFsPrompt();
    if (window.PG_PANEL) return;
    if (document.fullscreenElement) { if (!editMode) runFsActive = true; return; }
    if (runFsActive) {                               // vừa thoát toàn màn hình (thường do ESC)
        runFsActive = false;
        if (!editMode && !window.PG_FORCE_RUN) exitRunMode();   // admin: thoát luôn chế độ Chạy; tài khoản User luôn ở chế độ Chạy (bấm lại sẽ vào toàn màn hình)
        else if (!editMode) armRunFullscreen();
    }
});
const fsChk = document.getElementById('runFullscreenChk');
if (fsChk) {
    fsChk.checked = runFsEnabled();
    fsChk.addEventListener('change', () => {
        try { localStorage.setItem(RUN_FS_KEY, fsChk.checked ? '1' : '0'); } catch { /* bỏ qua */ }
        if (!fsChk.checked) leaveRunFullscreen(); else if (!editMode) { requestRunFullscreen(); armRunFullscreen(); }
    });
}
const insetInput = document.getElementById('runBottomInset');
if (insetInput) {
    try { insetInput.value = localStorage.getItem(RUN_INSET_KEY) ?? ''; } catch { /* bỏ qua */ }
    insetInput.addEventListener('change', () => {
        try { localStorage.setItem(RUN_INSET_KEY, insetInput.value.trim()); } catch { /* bỏ qua */ }
        applyRunInset();
    });
}

function syncPanels() {
    if (window.PG_PANEL) return;                       // chính iframe thì không tự nhúng thêm
    const list = window.floorList || [];
    const fl = {   // Header / Navigation luôn có sẵn trong dự án; chỉ nhúng khi được chọn "Sử dụng"
        Header: list.find(f => f.kind === 'Header' && f.enabled !== false),
        Navigation: list.find(f => f.kind === 'Navigation' && f.enabled !== false),
    };
    const sizeOf = (f) => [Math.max(5, f.panelWidth || 0), Math.max(5, f.panelHeight || 0)];
    const run = !editMode;
    const body = document.body;
    const hdr = run ? fl.Header : null, nav = run ? fl.Navigation : null;
    const hdrH = hdr ? sizeOf(hdr)[1] : 0;             // chiều cao Header: Navigation nằm ngay dưới
    const navW = nav ? sizeOf(nav)[0] : 0;             // chiều rộng Navigation: mặt bằng đỗ xe bắt đầu ngay bên phải

    for (const kind of ['Header', 'Navigation']) {
        const id = PANEL_FRAMES[kind];
        let fr = document.getElementById(id);
        const f = run ? fl[kind] : null;
        if (!f) { fr?.remove(); continue; }
        const [w, h] = sizeOf(f);
        const full = !!f.fullSize;                     // Toàn kích thước: Header rộng hết màn hình, Navigation cao hết phần còn lại
        const sig = `${f.id}|${w}|${h}|${full}`;
        if (fr && fr.dataset.sig !== sig) { fr.remove(); fr = null; }
        if (!fr) {
            fr = document.createElement('iframe');
            fr.id = id; fr.className = `pg-panel-frame pg-panel-${kind.toLowerCase()}`;
            fr.title = kind; fr.tabIndex = -1; fr.dataset.sig = sig;
            fr.src = `/?panel=${kind}`;
            fr.addEventListener('load', notifyPanels);
            body.appendChild(fr);
        }
        if (kind === 'Header') {
            fr.style.width = full ? '100vw' : `${w}px`; fr.style.height = `${h}px`;
        } else {
            fr.style.width = `${w}px`; fr.style.height = full ? `calc(100vh - ${hdrH}px - var(--run-bottom, 0px))` : `${h}px`;
        }
    }

    body.classList.toggle('has-panels', !!(hdr || nav));
    body.classList.toggle('nav-has-account', !!(nav && nav.showAccount));   // tài khoản + Đăng xuất nằm trong Navigation -> bỏ khối ở góc dưới-trái
    body.style.setProperty('--pnl-top', `${hdrH}px`);
    body.style.setProperty('--pnl-left', `${navW}px`);
    body.style.setProperty('--pnl-left-w', nav ? `${navW}px` : '260px');   // khối tài khoản ở góc dưới-trái co theo bề rộng Navigation
    window.dispatchEvent(new Event('resize'));
}

// Báo cho Header / Navigation biết mặt bằng đỗ xe đang xem (để nút ấn tương ứng sáng lên)
function notifyPanels() {
    document.querySelectorAll('.pg-panel-frame').forEach(fr => {
        try { fr.contentWindow?.postMessage({ pgFloor: CURRENT_FLOOR }, location.origin); } catch { /* bỏ qua */ }
    });
}
window.addEventListener('floorchange', notifyPanels);
window.addEventListener('floorlistchange', () => { if (!editMode) syncPanels(); });

window.addEventListener('message', (e) => {
    if (e.origin !== location.origin) return;
    const d = e.data || {};
    if (window.PG_PANEL) {
        if (d.pgFloor) { window.pgViewedFloor = d.pgFloor; renderLayout(); }
    } else if (d.pgEsc && !editMode && !window.PG_FORCE_RUN) {
        exitRunMode();                                  // ESC bấm khi đang chọn trong khung Header / Navigation
    }
});
if (window.PG_PANEL) {
    document.querySelectorAll('#pgNavAccount form').forEach(f => { f.target = '_top'; });   // Đăng xuất: chuyển cả trang về đăng nhập
    window.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') { try { window.parent.postMessage({ pgEsc: 1 }, location.origin); } catch { /* bỏ qua */ } }
    }, true);
}

function enterRunMode() {
    saveLayoutMode('run');
    closePropsDialog();      // luu thay doi dang do va dong hop thoai thuoc tinh
    clearSelection();        // bo chon (xoa vien .selected + khung co gian) - o che do Chay khong duoc con duong bao cua che do Build
    editMode = false;
    updateResizeFrame();
    updateValueNodes(true);          // Number cho nhap: doi sang o nhap that
    updateAnimNodes();               // hieu ung dong bat dau tac dung
    document.body.classList.add('layout-run-mode');
    // Đang mở Header / Navigation để thiết kế mà bấm Chạy: 2 màn hình này có khung riêng -> chuyển về mặt bằng đỗ xe khởi động
    if (!window.PG_PANEL && isPanelFloor(window.currentFloorData)) {
        const nf = (window.floorList || []).find(f => f.isStartup && !isPanelFloor(f)) || (window.floorList || []).find(f => !isPanelFloor(f));
        if (nf) navigateToFloor(nf.id);
    }
    syncPanels();
    requestRunFullscreen();          // bấm Chạy: vào toàn màn hình ngay (che taskbar)
    armRunFullscreen();              // mở lại trang ở chế độ Chạy: chờ lần bấm / phím đầu tiên
    setTimeout(updateFsPrompt, 400);
    applyRunInset();
    showRunExitButton();
}

function exitRunMode() {
    if (window.PG_FORCE_RUN) return;   // tai khoan User: luon o che do Chay
    // Mỗi bước chạy độc lập (try/catch): 1 bước lỗi cũng không được làm kẹt giao diện ở chế độ Chạy.
    const step = (fn) => { try { fn(); } catch (err) { console.error('exitRunMode:', err); } };
    editMode = true;
    document.body.classList.remove('layout-run-mode', 'has-panels', 'nav-has-account');   // gỡ giao diện Chạy TRƯỚC TIÊN
    step(() => saveLayoutMode('build'));
    step(() => hideRunExitButton());
    step(() => leaveRunFullscreen());
    step(() => document.querySelectorAll('.pg-panel-frame').forEach(fr => fr.remove()));   // gỡ khung Header / Navigation
    step(() => releaseAllMomentary());
    step(() => updateResizeFrame());
    step(() => renderLayout());          // go moi hieu ung dong (an / dich vi tri / doi mau) -> ve lai dung thiet ke
    step(() => updateValueNodes(true));
    step(() => syncPanels());
    step(() => window.dispatchEvent(new Event('resize')));
}

// Thoat che do Chay bang phim ESC (khong con nut tren man hinh)
function showRunExitButton() {
    showRunHint();
    showRunAccountBox();
    // Phòng khi ESC không tới được trang (đang ở toàn màn hình của trình duyệt, tiêu điểm nằm trong khung Header / Navigation...):
    // góc trên-phải có nút nhỏ, chỉ hiện rõ khi rê chuột vào, bấm để thoát chế độ Chạy.
    if (window.PG_FORCE_RUN || window.PG_PANEL || document.getElementById('pgRunExit')) return;
    const b = document.createElement('button');
    b.id = 'pgRunExit'; b.type = 'button'; b.title = 'Thoát chế độ Chạy (hoặc nhấn ESC)'; b.textContent = '✕ Thoát';
    b.addEventListener('click', exitRunMode);
    document.body.appendChild(b);
}
function hideRunExitButton() {
    document.getElementById('pgRunAcct')?.remove();
    document.getElementById('pgFsPrompt')?.remove();
    document.getElementById('layoutRunHint')?.remove();
    document.getElementById('pgRunExit')?.remove();
}

// Goi y nho "ESC de thoat", tu mo dan sau ~2.5s de khong che ban do
function showRunHint() {
    if (window.PG_FORCE_RUN) return;   // User khong thoat duoc che do Chay nen khong hien goi y ESC
    document.getElementById('layoutRunHint')?.remove();
    const tip = document.createElement('div');
    tip.id = 'layoutRunHint';
    tip.className = 'layout-run-hint';
    tip.textContent = 'Nhấn ESC để thoát chế độ chạy';
    document.body.appendChild(tip);
    setTimeout(() => tip.classList.add('fade'), 2500);
    setTimeout(() => tip.remove(), 3300);
}

window.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || editMode || window.PG_FORCE_RUN || window.PG_PANEL) return;
    e.preventDefault();
    exitRunMode();
}, true);

// ---------------------------------------------------------------------------
// Góc dưới-phải màn hình Chạy: tài khoản đang chạy + Đăng xuất + Thoát ứng dụng (mọi tài khoản).
// Tài khoản User không có Build: muốn vào Build thì Đăng xuất rồi đăng nhập bằng tài khoản quản trị.
// ---------------------------------------------------------------------------
function doLogout() {
    if (!confirm('Đăng xuất khỏi tài khoản hiện tại?')) return;
    const f = document.querySelector('.account-logout-form');
    if (f) { try { f.requestSubmit(); } catch { f.submit(); } }
    else location.href = '/Login';
}
async function doExitApp() {
    if (!confirm('Thoát ứng dụng? Chương trình giám sát sẽ TẮT hoàn toàn.')) return;
    let res;
    try {
        res = await fetch('/api/system/app/exit', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    } catch { res = null; }                          // máy chủ đã tắt ngay: coi như thành công
    if (res && !res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không tắt được ứng dụng.');
        return;
    }
    try { if (document.fullscreenElement) await document.exitFullscreen(); } catch { /* bỏ qua */ }
    const ov = document.createElement('div');
    ov.className = 'pg-exit-overlay';
    ov.innerHTML = '<div><h2>Ứng dụng đã tắt</h2><p>Bạn có thể đóng cửa sổ này.</p></div>';
    document.body.appendChild(ov);
    try { window.close(); } catch { /* trình duyệt có thể chặn */ }
}
function showRunAccountBox() {
    if (window.PG_PANEL || document.getElementById('pgRunAcct')) return;
    const u = window.PG_USER || {};
    const box = document.createElement('div');
    box.id = 'pgRunAcct';
    const who = document.createElement('span');
    who.className = 'acct-who';
    who.textContent = `👤 ${u.name || '—'}${u.role ? ' · ' + u.role : ''}`;
    who.title = 'Tài khoản đang chạy';
    const mk = (text, cls, fn) => {
        const b = document.createElement('button');
        b.type = 'button'; b.className = cls; b.textContent = text;
        b.addEventListener('click', fn);
        return b;
    };
    box.append(who, mk('↪ Đăng xuất', 'acct-btn', doLogout), mk('⏻ Thoát ứng dụng', 'acct-btn danger', doExitApp));
    document.body.appendChild(box);
}

applyFloorBackground(window.currentFloorData);
// User: luon Chay. Tai khoan khac: giu che do da dung lan truoc (Chay cho toi khi tu thoat bang ESC / nut Build)
if (window.PG_FORCE_RUN || savedLayoutMode() === 'run') enterRunMode();
loadLayout();
if (!editMode) syncPanels();     // danh sách mặt bằng có thể đã nạp xong trước khi layout.js chạy
loadVarCatalog();
pollBits();
pollLampStates();
pollValues();

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
