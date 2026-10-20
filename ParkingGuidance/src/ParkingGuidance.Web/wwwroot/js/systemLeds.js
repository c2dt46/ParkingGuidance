// ---------------------------------------------------------------------------
// He thong > LED : giao dien dung theo Form HDCard cua PGS
//   Card so / IP / Enable / Rong / Cao / Loai Card / Mau / Gray Level, bang vung (Image/Text/Number),
//   Add Image / Add text / Add Num / Delete / Up / Down / Gui toi card, khung mo phong LED,
//   tu dong luu sau moi thay doi (nhu Save_1_card / Save_All_card).
// ---------------------------------------------------------------------------
const $ = id => document.getElementById(id);
const API = '/api/system/leds';
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let catalog = null;
const tt = (key, vi) => (window.PGPrefs ? window.PGPrefs.t(key, vi) : vi);
let cardNo = 1;
let card = null;          // LedCard dang sua (dung ten thuoc tinh camelCase cua API)
let selRow = -1;
let live = { ms: {}, mc: {}, tags: {}, statuses: [] };
let dataSources = { ms: [], mc: [], tags: [] };   // bien MS, bien MC, project tag (kieu so) de chon trong vung Number
let saveTimer = null;
let saving = false;
let loading = false;      // dang do du lieu len control -> khong tu luu (chan_dieu_chinh cua PGS)

async function api(url, options) {
    const res = await fetch(url, options);
    let data = null;
    try { data = await res.json(); } catch { /* khong co noi dung */ }
    if (!res.ok) throw new Error(data?.message || ('HTTP ' + res.status));
    return data;
}
const send = (url, method, body) => api(url, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });
const opts = (list, withIndex = true) => list.map((t, i) => `<option value="${withIndex ? i : t}">${esc(t)}</option>`).join('');
const pad = (v, n) => String(v).padStart(n, '0');

// ----------------------------------------------------------------- mac dinh 1 dong moi (Add1Row)
function newRow(type, x, y, w, h) {
    return {
        no: 0, type, originX: x, originY: y, width: w, height: h,
        ipath: '', ispeed: 1, istopover: 0, ieffects: 0,
        ttext: '', tspeed: 1, tstopover: 0, teffects: 0, tcolor: 0, tfont: 5, tfont_Type: '.Vn3DH',
        nMs: '', nMc: '', nTag: '', ncolor: 0, nmode: 0, nquantity: 2, nlimitY: 2000, nlimitR: 1000,
        nfont: 5, nfont_Type: '.Vn3DH', nFull: 0
    };
}

// ----------------------------------------------------------------- khoi tao
async function init() {
    catalog = await api(API + '/catalog');
    $('CardNo').innerHTML = Array.from({ length: 100 }, (_, i) => `<option value="${i + 1}">${i + 1}</option>`).join('');
    $('Card_model').innerHTML = catalog.models.map(m => `<option value="${m.index}">${esc(m.name)}</option>`).join('');

    const src = {
        effects: opts(catalog.effects), speeds: opts(catalog.speeds, false), stopovers: opts(catalog.stopovers, false),
        fontSizes: opts(catalog.fontSizes, false), fontTypes: opts(catalog.fontTypes, false),
        textColors: opts(catalog.textColors), digitCounts: opts(catalog.digitCounts, false)
    };
    document.querySelectorAll('select[data-src]:not([data-src^="src"])').forEach(el => { el.innerHTML = src[el.dataset.src]; });

    await loadDataSources();
    await loadCard(cardNo);
    await refreshLive();
    setInterval(refreshLive, 2000);
}

// ----------------------------------------------------------------- nguon so cua vung Number: bien MS + bien MC + project tag
function dataSourceOptions(kind) {
    const none = `<option value="">${esc(tt('led.none', 'Không'))}</option>`;
    return none + (dataSources[kind] || []).map(v =>
        `<option value="${esc(v.name)}">${esc(v.name)}${v.description ? ' — ' + esc(v.description) : ''}${v.type ? ' [' + esc(v.type) + ']' : ''} (= ${v.value})</option>`).join('');
}
async function loadDataSources() {
    try { dataSources = await api(API + '/data-sources'); } catch { /* giu danh sach cu */ }
    const kinds = { srcMs: 'ms', srcMc: 'mc', srcTag: 'tags' };
    document.querySelectorAll('select[data-src^="src"]').forEach(el => { el.innerHTML = dataSourceOptions(kinds[el.dataset.src]); });
    if (card && selRow >= 0) showPanel();   // danh sach vua doi -> dien lai gia tri dong dang chon
}

// ----------------------------------------------------------------- doc / ghi card
async function loadCard(no) {
    await flushSave();
    cardNo = no;
    $('CardNo').value = no;
    card = await api(`${API}/cards/${no}`);
    selRow = -1;
    paintCardPara();
    paintRows();
    paintLed();
    showPanel();
}

function paintCardPara() {
    loading = true;
    $('IPCard').value = card.para_IPAddress;
    $('EnnableCard').value = card.para_Enable ? '1' : '0';
    $('Card_model').value = card.para_Mode;
    fillColorGray();
    $('Color').value = card.para_Color;
    $('Graylevel').value = card.para_Gray;
    $('Width').value = card.para_Width;
    $('Height').value = card.para_Height;
    loading = false;
    $('hdCardHint').textContent = modelHint();
}

function modelHint() {
    const m = catalog.models[card.para_Mode];
    return `${m.name}: tối đa ${m.maxWidth}×${m.maxHeight} px, ${m.maxBlocks} khối (mỗi khối 32 cột × 16 hàng). Rộng là bội số của 32, cao là bội số của 16.`;
}

// color_check / gray_check cua HDCard
function fillColorGray() {
    const m = catalog.models[card.para_Mode];
    $('Color').innerHTML = m.colors.map(c => `<option value="${c.value}">${esc(c.label)}</option>`).join('');
    $('Graylevel').innerHTML = m.grays.map(g => `<option value="${g.value}">${esc(g.label)}</option>`).join('');
}

// _W_check / _H_check cua HDCard
function fitSize(changed) {
    const m = catalog.models[card.para_Mode];
    let w = Math.max(32, Math.round((parseInt($('Width').value, 10) || 32) / 32) * 32);
    let h = Math.max(16, Math.round((parseInt($('Height').value, 10) || 16) / 16) * 16);
    w = Math.min(w, Math.floor(m.maxWidth / 32) * 32);
    h = Math.min(h, Math.floor(m.maxHeight / 16) * 16);
    while ((h / 16) * (w / 32) > m.maxBlocks) {
        if (changed === 'h' && h > 16) h -= 16; else if (w > 32) w -= 32; else h -= 16;
    }
    card.para_Width = w; card.para_Height = h;
    $('Width').value = w; $('Height').value = h;
}

function scheduleSave() {
    if (loading || !card) return;
    $('hdSaveState').textContent = 'Đang lưu…';
    clearTimeout(saveTimer);
    saveTimer = setTimeout(doSave, 400);
}

async function doSave() {
    clearTimeout(saveTimer); saveTimer = null;
    if (!card) return;
    saving = true;
    try {
        card.areas.forEach((r, i) => r.no = i + 1);
        const saved = await send(`${API}/cards/${card.cardNo}`, 'PUT', card);
        $('hdSaveState').textContent = '✅ Đã lưu' + warnText(saved);
    } catch (err) {
        $('hdSaveState').textContent = '⚠️ ' + err.message;
    } finally { saving = false; }
}
async function flushSave() { if (saveTimer) await doSave(); }

function warnText(c) {
    const bad = c.areas.find(r => r.originX + r.width > c.para_Width || r.originY + r.height > c.para_Height);
    return bad ? ` — ⚠️ vùng số ${bad.no} vượt ra ngoài màn hình ${c.para_Width}×${c.para_Height} (gửi sẽ lỗi)` : '';
}

// ----------------------------------------------------------------- cac control thong so card
$('CardNo').addEventListener('change', () => loadCard(parseInt($('CardNo').value, 10)));

$('IPCard').addEventListener('blur', () => {
    const v = $('IPCard').value.trim();
    const ok = /^([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])(\.([0-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])){3}$/.test(v);
    if (!ok && v !== '') { alert('Giá trị bạn vừa nhập không phải là một IP hợp lệ'); $('IPCard').value = card.para_IPAddress; return; }
    if (v !== card.para_IPAddress) { card.para_IPAddress = v; scheduleSave(); }
});
$('EnnableCard').addEventListener('change', () => { card.para_Enable = $('EnnableCard').value === '1'; scheduleSave(); });
$('Card_model').addEventListener('change', () => {
    card.para_Mode = parseInt($('Card_model').value, 10);
    fillColorGray();
    const m = catalog.models[card.para_Mode];
    card.para_Color = m.defaultColor; card.para_Gray = 0;
    $('Color').value = card.para_Color; $('Graylevel').value = 0;
    fitSize();
    $('hdCardHint').textContent = modelHint();
    paintLed(); scheduleSave();
});
$('Color').addEventListener('change', () => { card.para_Color = parseInt($('Color').value, 10); scheduleSave(); });
$('Graylevel').addEventListener('change', () => { card.para_Gray = parseInt($('Graylevel').value, 10); scheduleSave(); });
$('Width').addEventListener('change', () => { fitSize('w'); paintLed(); scheduleSave(); });
$('Height').addEventListener('change', () => { fitSize('h'); paintLed(); scheduleSave(); });

$('Mybutton').addEventListener('click', () => {
    const el = $('DanhsachCard');
    el.style.display = el.style.display === 'none' ? '' : 'none';
    if (el.style.display !== 'none') paintCardList();
});

$('hdConn').addEventListener('click', async () => {
    $('hdConn').textContent = 'Đang kiểm tra…';
    try {
        const r = await send(`${API}/cards/${cardNo}/ping`, 'POST');
        $('hdSaveState').textContent = r.ok ? `✅ Card phản hồi (${r.elapsedMs} ms)` : `⚠️ ${r.error || 'Card không phản hồi'}`;
    } catch (e) { $('hdSaveState').textContent = '⚠️ ' + e.message; }
    paintConn();
});

// ----------------------------------------------------------------- bang vung (CommList)
// Chu hien thi theo ngon ngu da chon o Nguoi dung > Giao dien & ngon ngu (gia tri 'Image'/'Text'/'Number' ben trong khong doi)
const TYPE_VI = { Image: 'Hình ảnh', Text: 'Chữ', Number: 'Số' };
const typeLabel = t => tt('led.t.' + t, TYPE_VI[t] || t);
document.addEventListener('pgprefs', () => { try { if (typeof card !== 'undefined' && card) paintRows(); } catch { } });
function paintRows() {
    const body = document.querySelector('#CommList tbody');
    body.innerHTML = card.areas.map((r, i) =>
        `<tr data-i="${i}" class="${i === selRow ? 'sel' : ''}"><td>${i + 1}</td><td>${esc(typeLabel(r.type))}</td></tr>`).join('') ||
        `<tr><td colspan="2" class="no-result">${esc(tt('led.empty', 'Chưa có vùng hiển thị'))}</td></tr>`;
    $('btnUp').disabled = selRow <= 0;
    $('BtnDow').disabled = selRow < 0 || selRow >= card.areas.length - 1;
    $('btnDel').disabled = selRow < 0;
}

document.querySelector('#CommList tbody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-i]');
    if (tr) selectRow(parseInt(tr.dataset.i, 10));
});
document.querySelector('#CommList tbody').addEventListener('dblclick', e => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr) return;
    selectRow(parseInt(tr.dataset.i, 10));
    openLedProps();
});

// ----------------------------------------------------------------- hop thoai thuoc tinh vung (noi, keo duoc)
//  - Chua mo: nhay dup vao vung tren khung LED (hoac dong trong bang) de mo.
//  - Dang mo: chi can chon vung khac (nhap vao vung / dong bang) thi noi dung hop thoai doi theo vung do.
//  - Dong bang nut ✕ / phim Esc / nut "Thuoc tinh" tren thanh nut; thay doi duoc ghi ngay nhu truoc.
const LED_PROPS_POS_KEY = 'ledPropsDialogPos';
var ledProps = $('ledPropsDialog');   // var: showPanel() co the chay truoc dong nay, khong gay loi TDZ
var ledPropsOpen = false;

function setLedPropsPos(left, top) {
    const w = ledProps.offsetWidth || 600, h = ledProps.offsetHeight || 40;
    left = Math.min(Math.max(0, left), Math.max(0, window.innerWidth - w));
    top = Math.min(Math.max(0, top), Math.max(0, window.innerHeight - h));
    ledProps.style.left = left + 'px';
    ledProps.style.top = top + 'px';
}
function placeLedProps() {
    if (ledProps.dataset.placed) return;
    let pos = null;
    try { pos = JSON.parse(localStorage.getItem(LED_PROPS_POS_KEY) || 'null'); } catch { /* khong co storage */ }
    // Mac dinh: giua man hinh, sat day de it che khung LED
    const w = ledProps.offsetWidth || 600, h = ledProps.offsetHeight || 200;
    setLedPropsPos(pos ? pos.left : (window.innerWidth - w) / 2, pos ? pos.top : window.innerHeight - h - 24);
    ledProps.dataset.placed = '1';
}
// Giu hop thoai nam tron trong man hinh khi noi dung (chieu cao) thay doi
function fitLedProps() {
    if (!ledPropsOpen || !ledProps.dataset.placed) return;
    setLedPropsPos(parseInt(ledProps.style.left, 10) || 0, parseInt(ledProps.style.top, 10) || 0);
}
function syncLedPropsBtn() { $('btnProps').classList.toggle('active', ledPropsOpen); }
function openLedProps() {
    ledPropsOpen = true;
    ledProps.style.display = 'block';
    placeLedProps();
    fitLedProps();
    syncLedPropsBtn();
}
function closeLedProps() {
    ledPropsOpen = false;
    ledProps.style.display = 'none';
    syncLedPropsBtn();
}
$('btnProps').addEventListener('click', () => { if (ledPropsOpen) closeLedProps(); else openLedProps(); });
$('ledPropsClose').addEventListener('click', closeLedProps);
$('ledPropsTitle').addEventListener('mousedown', e => {
    if (e.target.closest('button')) return;
    const startX = e.clientX, startY = e.clientY;
    const rect = ledProps.getBoundingClientRect();
    const move = ev => setLedPropsPos(rect.left + ev.clientX - startX, rect.top + ev.clientY - startY);
    const up = () => {
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
        try { localStorage.setItem(LED_PROPS_POS_KEY, JSON.stringify({ left: parseInt(ledProps.style.left, 10), top: parseInt(ledProps.style.top, 10) })); } catch { /* khong co storage */ }
    };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', up);
    e.preventDefault();
});
document.addEventListener('keydown', e => { if (e.key === 'Escape' && ledPropsOpen) closeLedProps(); });
window.addEventListener('resize', fitLedProps);

function selectRow(i) {
    selRow = i;
    paintRows(); showPanel(); paintLed();
    if (card.areas[i]?.type === 'Number') loadDataSources();   // bien MS / MC / tag co the vua duoc them o trang khac
}

// Hien thi panel theo loai (update() cua HDCard)
function showPanel() {
    const r = card.areas[selRow];
    $('palArea').style.display = r ? '' : 'none';
    $('palImage2').style.display = r?.type === 'Image' ? '' : 'none';
    $('palText').style.display = r?.type === 'Text' ? '' : 'none';
    $('palNumber').style.display = r?.type === 'Number' ? '' : 'none';
    $('ledPropsEmpty').style.display = r ? 'none' : '';
    $('ledPropsTitleText').textContent = r
        ? `${tt('led.propsTitle', 'Thuộc tính vùng hiển thị')} — #${selRow + 1} ${typeLabel(r.type)}`
        : tt('led.propsTitle', 'Thuộc tính vùng hiển thị');
    if (!r) { fitLedProps(); return; }
    loading = true;
    document.querySelectorAll('#palArea [data-f]').forEach(el => {
        const v = r[el.dataset.f] ?? '';
        // Bien MS/MC/tag da chon nhung nay khong con (bi xoa/doi ten): van hien ten de nguoi dung biet, khong am tham chuyen thanh "Khong"
        if (el.tagName === 'SELECT' && el.dataset.src?.startsWith('src') && v !== '' && ![...el.options].some(o => o.value === v))
            el.insertAdjacentHTML('beforeend', `<option value="${esc(v)}">${esc(v)} (${esc(tt('led.missing', 'không còn'))})</option>`);
        el.value = v;
    });
    loading = false;
    fitLedProps();
}

// Moi control co data-f: sua la ghi thang vao dong dang chon, ve lai mo phong va tu luu
document.querySelectorAll('#palArea [data-f]').forEach(el => {
    el.addEventListener(el.tagName === 'SELECT' ? 'change' : 'input', () => {
        if (loading) return;
        const r = card.areas[selRow];
        if (!r) return;
        r[el.dataset.f] = el.dataset.t === 'int' ? (parseInt(el.value, 10) || 0) : el.value;
        paintLed(); scheduleSave();
    });
});

function add(type) {
    if (card.areas.length >= catalog.maxAreas) { alert('Quá 20 thông tin'); return; }
    const cur = card.areas[selRow];
    // Add1Row lay x, y, rong, cao tu cac o dang hien thi (mac dinh 0, 0, 32, 16)
    card.areas.push(newRow(type, cur?.originX ?? 0, cur?.originY ?? 0, cur?.width ?? 32, cur?.height ?? 16));
    selRow = card.areas.length - 1;
    paintRows(); showPanel(); paintLed(); scheduleSave();
}
$('btnAddFile').addEventListener('click', () => add('Image'));
$('btnAddText').addEventListener('click', () => add('Text'));
$('btnAddNum').addEventListener('click', () => add('Number'));

$('btnDel').addEventListener('click', () => {
    if (selRow < 0) return;
    card.areas.splice(selRow, 1);
    selRow = Math.min(selRow, card.areas.length - 1);
    paintRows(); showPanel(); paintLed(); scheduleSave();
});
function move(d) {
    const j = selRow + d;
    if (selRow < 0 || j < 0 || j >= card.areas.length) return;
    [card.areas[selRow], card.areas[j]] = [card.areas[j], card.areas[selRow]];
    selRow = j;
    paintRows(); paintLed(); scheduleSave();
}
$('btnUp').addEventListener('click', () => move(-1));
$('BtnDow').addEventListener('click', () => move(1));

// ----------------------------------------------------------------- timeout gui LED (luu vao cai dat he thong)
async function loadLedSettings() {
    try {
        const s = await api(API + '/settings');
        $('LedTimeout').value = s.requestTimeoutSeconds;
    } catch (e) { $('LedTimeoutState').textContent = '⚠️ Không tải được timeout: ' + e.message; }
}
async function saveLedSettings() {
    const v = parseFloat($('LedTimeout').value);
    if (Number.isNaN(v)) { $('LedTimeoutState').textContent = '⚠️ Timeout phải là một số (giây).'; return; }
    try {
        const s = await send(API + '/settings', 'PUT', { requestTimeoutSeconds: v });
        $('LedTimeout').value = s.requestTimeoutSeconds; // hien lai gia tri da duoc gioi han 1-120
        $('LedTimeoutState').textContent = '✅ Đã lưu timeout ' + s.requestTimeoutSeconds + ' giây lúc ' + new Date().toLocaleTimeString('vi-VN');
    } catch (e) { $('LedTimeoutState').textContent = '⚠️ Lưu thất bại: ' + e.message; }
}
$('LedTimeoutSave').addEventListener('click', saveLedSettings);
$('LedTimeout').addEventListener('keydown', e => { if (e.key === 'Enter') saveLedSettings(); });
loadLedSettings();

$('button4').addEventListener('click', async () => {
    await flushSave();
    try {
        await send(`${API}/cards/${cardNo}/send`, 'POST');
        $('hdSaveState').textContent = '📤 Đã yêu cầu gửi tới card…';
        setTimeout(refreshLive, 1500);
    } catch (e) { $('hdSaveState').textContent = '⚠️ ' + e.message; }
});

// Chon anh: tai len server
$('btnSeleImage2').addEventListener('click', () => $('imageFile').click());
$('imageFile').addEventListener('change', async () => {
    const f = $('imageFile').files[0];
    $('imageFile').value = '';
    if (!f || !card.areas[selRow]) return;
    const fd = new FormData(); fd.append('file', f);
    try {
        const res = await fetch(API + '/images', { method: 'POST', body: fd });
        const d = await res.json();
        if (!res.ok) throw new Error(d.message || 'Lỗi tải ảnh');
        card.areas[selRow].ipath = d.path;
        $('txtImage2').value = d.path;
        paintLed(); scheduleSave();
    } catch (e) { alert(e.message); }
});

// ----------------------------------------------------------------- khung mo phong LED (palLedBlock_paint)
const LED_COLORS = ['#ff2020', '#ffff00', '#20ff20'];
function numberValue(r) {
    const g = (map, name) => (name ? Number(map?.[name]) || 0 : 0);
    return Math.max(0, g(live.ms, r.nMs) + g(live.mc, r.nMc) + g(live.tags, r.nTag));
}
function numberShow(r) {
    const v = numberValue(r);
    const text = (v === 0 && r.nFull === 1) ? 'FULL' : pad(v, r.nquantity);
    const color = r.nmode === 0 ? r.ncolor : (v < r.nlimitR ? 0 : v < r.nlimitY ? 1 : 2);
    return { text, color };
}

// ----------------------------------------------------------------- khung mo phong dang MA TRAN DIEM LED (giong HD2020)
// Kich thuoc chuong trinh WxH chinh la ma tran WxH diem LED: moi vung duoc ve ra luoi diem LED dung bang
// kich thuoc vung (1 o = 1 diem LED), diem sang ve thanh cham tron tren nen cham toi.
let curScale = 1;
const imgCache = new Map();
function getImage(url) {
    let im = imgCache.get(url);
    if (!im) { im = new Image(); im.src = url; imgCache.set(url, im); }
    if (!im.complete) im.addEventListener('load', () => paintLed(), { once: true });
    return im;
}
// Font Windows ".VnArial", ".Vn3DH"... khong co tren trinh duyet -> bo tien to ".Vn" va dung font tuong tu
const cssFont = t => {
    const n = String(t || '').replace(/^\.Vn/i, '').replace(/H$/, '').trim();
    return (n ? `"${n}", ` : '') + 'Arial, sans-serif';
};
function areaKey(r, show) {
    return [r.type, r.width, r.height, show?.text, show?.color, r.type === 'Number' ? r.nfont : r.tfont,
        r.type === 'Number' ? r.nfont_Type : r.tfont_Type, r.ipath].join('|');
}
function areaShow(r) {
    if (r.type === 'Number') return numberShow(r);
    if (r.type === 'Text') return { text: r.ttext, color: r.tcolor };
    return null;
}
// Ve vung r len canvas cv (kich thuoc canvas = rong*scale x cao*scale)
function drawArea(r, cv, scale) {
    const w = Math.max(1, r.width | 0), h = Math.max(1, r.height | 0);
    const sw = Math.max(1, Math.round(w * scale)), sh = Math.max(1, Math.round(h * scale));
    if (cv.width !== sw) cv.width = sw;
    if (cv.height !== sh) cv.height = sh;
    const g = cv.getContext('2d');
    g.clearRect(0, 0, sw, sh);

    // 1) dung chu/anh o do phan giai 1 pixel = 1 diem LED
    const off = document.createElement('canvas'); off.width = w; off.height = h;
    const o = off.getContext('2d', { willReadFrequently: true });
    let fixedColor = null;
    if (r.type === 'Image') {
        const url = r.ipath?.startsWith('led-images/') ? `${API}/images/${encodeURIComponent(r.ipath.slice(11))}` : null;
        if (!url) return false;
        const im = getImage(url);
        if (!im.complete || !im.naturalWidth) return false;
        o.drawImage(im, 0, 0, w, h);                 // keo gian vua khit vung (nhu HD2020)
    } else {
        const n = r.type === 'Number';
        const show = areaShow(r);
        fixedColor = LED_COLORS[show.color] ?? LED_COLORS[0];
        const size = Math.max(1, (n ? r.nfont : r.tfont) | 0);
        o.fillStyle = '#fff';
        o.font = `${size}px ${cssFont(n ? r.nfont_Type : r.tfont_Type)}`;
        // Can chu vao GIUA vung (ca ngang lan doc) theo khung muc thuc te cua chuoi, lam tron ve diem LED nguyen
        const text = show.text ?? '';
        o.textAlign = 'left'; o.textBaseline = 'alphabetic';
        const m = o.measureText(text);
        const inkW = m.actualBoundingBoxLeft + m.actualBoundingBoxRight;
        const inkH = m.actualBoundingBoxAscent + m.actualBoundingBoxDescent;
        const tx = Math.round((w - inkW) / 2 + m.actualBoundingBoxLeft);
        const ty = Math.round((h - inkH) / 2 + m.actualBoundingBoxAscent);
        o.fillText(text, tx, ty);
    }
    const px = o.getImageData(0, 0, w, h).data;

    // 2) moi diem LED sang -> mot cham tron
    const rad = scale * 0.42;
    g.fillStyle = fixedColor || '#fff';
    if (fixedColor && scale >= 3) g.beginPath();
    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
            const k = (y * w + x) * 4;
            const a = px[k + 3];
            if (fixedColor) {
                if (a < 96) continue;                // nguong: chu khong khu rang cua
                if (scale >= 3) { g.moveTo((x + 0.5) * scale + rad, (y + 0.5) * scale); g.arc((x + 0.5) * scale, (y + 0.5) * scale, rad, 0, Math.PI * 2); }
                else g.fillRect(Math.floor(x * scale), Math.floor(y * scale), Math.ceil(scale), Math.ceil(scale));
            } else {
                const R = px[k], G = px[k + 1], B = px[k + 2];
                const mx = Math.max(R, G, B);
                if (a < 128 || mx < 60) continue;    // nen toi / trong suot = diem tat
                const f = 255 / mx;                  // tang sang cho dung do chói cua LED
                g.fillStyle = `rgb(${Math.min(255, R * f) | 0},${Math.min(255, G * f) | 0},${Math.min(255, B * f) | 0})`;
                if (scale >= 3) { g.beginPath(); g.arc((x + 0.5) * scale, (y + 0.5) * scale, rad, 0, Math.PI * 2); g.fill(); }
                else g.fillRect(Math.floor(x * scale), Math.floor(y * scale), Math.ceil(scale), Math.ceil(scale));
            }
        }
    }
    if (fixedColor && scale >= 3) g.fill();
    return true;
}


// ----------------------------------------------------------------- phong to / thu nho khu vuc thiet ke (theo %)
// 100% = 8 pixel man hinh cho moi diem LED. zoomMode: 'fit' = vua khung nhin; so = % co dinh.
const ZOOM_BASE = 8, ZOOM_MIN_PCT = 13, ZOOM_MAX_PCT = 800;
let zoomMode = 'fit';
function maxScaleFor(w, h) { return Math.max(1, Math.min(ZOOM_BASE * ZOOM_MAX_PCT / 100, 12000 / Math.max(w, h, 1))); }
function clampScale(sc, w, h) { return Math.max(ZOOM_BASE * ZOOM_MIN_PCT / 100, Math.min(maxScaleFor(w, h), sc)); }
function fitScale(w, h) {
    const wrap = $('ledWrap');
    const aw = Math.max(200, (wrap?.clientWidth || 820) - 40 - 20);
    const ah = Math.max(150, (wrap?.clientHeight || 400) - 60 - 40);
    return clampScale(Math.min(aw / w, ah / h), w, h);
}
function calcScale(w, h) {
    return zoomMode === 'fit' ? fitScale(w, h) : clampScale(ZOOM_BASE * zoomMode / 100, w, h);
}
function updateZoomUi(w, h, scale) {
    const pct = Math.round(scale / ZOOM_BASE * 100);
    $('ledZoomLabel').textContent = pct + '%' + (zoomMode === 'fit' ? ' (vừa)' : '');
    $('ledZoomRange').value = Math.max(ZOOM_MIN_PCT, Math.min(ZOOM_MAX_PCT, pct));
    $('ledZoomOut').disabled = pct <= ZOOM_MIN_PCT;
    $('ledZoomIn').disabled = scale >= maxScaleFor(w, h) - 1e-6;
    $('ledSizeInfo').textContent = `Màn hình ${w}×${h} điểm LED → ${Math.round(w * scale)}×${Math.round(h * scale)} px (${pct}%). Kéo thanh trượt ngang/dọc để xem hết, kéo góc dưới phải của khung để đổi cỡ khung.`;
}
function setZoomPct(pct) {
    if (!card) return;
    zoomMode = Math.max(ZOOM_MIN_PCT, Math.min(ZOOM_MAX_PCT, Math.round(pct)));
    paintLed();
}
const ZOOM_STEPS = [13, 17, 25, 33, 50, 67, 75, 100, 125, 150, 200, 300, 400, 600, 800];
function stepZoom(dir) {
    const cur = Math.round(curScale / ZOOM_BASE * 100);
    const next = dir > 0 ? ZOOM_STEPS.find(v => v > cur + 0.5) : [...ZOOM_STEPS].reverse().find(v => v < cur - 0.5);
    if (next) setZoomPct(next);
}
$('ledZoomIn').addEventListener('click', () => stepZoom(+1));
$('ledZoomOut').addEventListener('click', () => stepZoom(-1));
$('ledZoomRange').addEventListener('input', () => setZoomPct(parseInt($('ledZoomRange').value, 10)));
$('ledZoomFit').addEventListener('click', () => { zoomMode = 'fit'; paintLed(); });
$('ledZoom100').addEventListener('click', () => setZoomPct(100));
// Ctrl + lan chuot de phong to / thu nho (khong Ctrl: cuon binh thuong)
$('ledWrap').addEventListener('wheel', e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    stepZoom(e.deltaY < 0 ? +1 : -1);
}, { passive: false });
// Che do "vua khung" tu tinh lai khi doi kich thuoc cua so / keo gian khung
let zoomRaf = 0;
const zoomRedraw = () => { if (zoomMode !== 'fit' || !card) return; cancelAnimationFrame(zoomRaf); zoomRaf = requestAnimationFrame(() => { if (Math.abs(calcScale(card.para_Width, card.para_Height) - curScale) > 0.01) paintLed(); }); };
window.addEventListener('resize', zoomRedraw);
if (window.ResizeObserver) new ResizeObserver(zoomRedraw).observe($('ledWrap'));

function paintLed() {
    const box = $('palLedBlock');
    if (!card) return;
    const w = card.para_Width, h = card.para_Height;
    const scale = calcScale(w, h);
    curScale = scale;
    updateZoomUi(w, h, scale);
    box.style.width = (w * scale) + 'px';
    box.style.height = (h * scale) + 'px';
    // nen: luoi cham LED tat (mo)
    box.style.backgroundImage = 'radial-gradient(circle closest-side, #3a1515 0, #3a1515 72%, transparent 80%)';
    box.style.backgroundSize = scale + 'px ' + scale + 'px';
    box.innerHTML = '';
    card.areas.forEach((r, i) => {
        const d = document.createElement('div');
        d.className = 'hd-area' + (i === selRow ? ' sel' : '');
        d.style.cssText = `left:${r.originX * scale}px;top:${r.originY * scale}px;width:${r.width * scale}px;height:${r.height * scale}px;`;
        const cv = document.createElement('canvas');
        cv.className = 'hd-dots';
        const ok = drawArea(r, cv, scale);
        if (ok) { cv.dataset.k = areaKey(r, areaShow(r)); d.appendChild(cv); }
        else d.innerHTML = '<span class="hd-ph">Image</span>';
        d.addEventListener('pointerdown', e => startDrag(e, i, d, scale));
        d.addEventListener('dblclick', () => openLedProps());   // nhay dup: mo hop thoai thuoc tinh cua vung nay
        box.appendChild(d);
    });
    placeHandles(selRow);
}

// ----------------------------------------------------------------- tay nam co gian (8 huong) tren vung dang chon
const HANDLE_DIRS = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];
function placeHandles(i) {
    const box = $('palLedBlock');
    box.querySelectorAll('.hd-h').forEach(h => h.remove());
    const el = box.children[i];
    if (!el || !card.areas[i]) return;
    HANDLE_DIRS.forEach(dir => {
        const h = document.createElement('div');
        h.className = 'hd-h hd-h-' + dir;
        h.addEventListener('pointerdown', e => startResize(e, i, el, dir));
        el.appendChild(h);
    });
}

function startResize(e, i, el, dir) {
    if (e.button !== undefined && e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    const r = card.areas[i];
    const scale = curScale, W = card.para_Width, H = card.para_Height;
    const sx = e.clientX, sy = e.clientY;
    const x0 = r.originX, y0 = r.originY, w0 = r.width, h0 = r.height;
    const x1 = x0 + w0, y1 = y0 + h0;
    const handle = e.currentTarget;
    let changed = false;
    try { handle.setPointerCapture(e.pointerId); } catch { /* trinh duyet cu */ }
    el.classList.add('dragging');
    const cv = el.querySelector('canvas');

    const onMove = ev => {
        const dx = Math.round((ev.clientX - sx) / scale);
        const dy = Math.round((ev.clientY - sy) / scale);
        let nx = x0, ny = y0, nw = w0, nh = h0;
        if (dir.includes('w')) { nx = Math.max(0, Math.min(x1 - 1, x0 + dx)); nw = x1 - nx; }
        if (dir.includes('e')) { nw = Math.max(1, Math.min(W - x0, w0 + dx)); }
        if (dir.includes('n')) { ny = Math.max(0, Math.min(y1 - 1, y0 + dy)); nh = y1 - ny; }
        if (dir.includes('s')) { nh = Math.max(1, Math.min(H - y0, h0 + dy)); }
        if (nx === r.originX && ny === r.originY && nw === r.width && nh === r.height) return;
        changed = true;
        r.originX = nx; r.originY = ny; r.width = nw; r.height = nh;
        el.style.left = (nx * scale) + 'px'; el.style.top = (ny * scale) + 'px';
        el.style.width = (nw * scale) + 'px'; el.style.height = (nh * scale) + 'px';
        if (cv) drawArea(r, cv, scale);          // ve lai chu/anh theo kich thuoc moi ngay khi keo
        syncGeomInputs(r);
    };
    const onUp = () => {
        handle.removeEventListener('pointermove', onMove);
        handle.removeEventListener('pointerup', onUp);
        handle.removeEventListener('pointercancel', onUp);
        el.classList.remove('dragging');
        if (changed) { paintLed(); scheduleSave(); }
    };
    handle.addEventListener('pointermove', onMove);
    handle.addEventListener('pointerup', onUp);
    handle.addEventListener('pointercancel', onUp);
}

// Chon + keo tha vung tren khung mo phong: Origin X / Origin Y (o thong so ben duoi) doi theo, tu dong luu khi tha chuot.
// Vi tri bi chan trong man hinh (0 .. rong man hinh - rong vung) va lam tron theo diem LED.
function startDrag(e, i, el, scale) {
    if (e.button !== undefined && e.button !== 0) return;
    e.preventDefault();

    if (selRow !== i) {            // chon vung nay (khong ve lai de khong mat phan tu dang keo)
        selRow = i;
        box_markSelected(i);
        paintRows(); showPanel();
    }
    const r = card.areas[i];
    const startX = e.clientX, startY = e.clientY;
    const ox = r.originX, oy = r.originY;
    let moved = false;
    try { el.setPointerCapture(e.pointerId); } catch { /* trinh duyet cu */ }
    el.classList.add('dragging');

    const onMove = ev => {
        const dx = Math.round((ev.clientX - startX) / scale);
        const dy = Math.round((ev.clientY - startY) / scale);
        const nx = Math.max(0, Math.min(Math.max(0, card.para_Width - r.width), ox + dx));
        const ny = Math.max(0, Math.min(Math.max(0, card.para_Height - r.height), oy + dy));
        if (nx === r.originX && ny === r.originY) return;
        moved = true;
        r.originX = nx; r.originY = ny;
        el.style.left = (nx * scale) + 'px';
        el.style.top = (ny * scale) + 'px';
        syncGeomInputs(r);
    };
    const onUp = () => {
        el.removeEventListener('pointermove', onMove);
        el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp);
        el.classList.remove('dragging');
        if (moved) { paintLed(); scheduleSave(); }
    };
    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
}

function box_markSelected(i) {
    [...$('palLedBlock').children].forEach((c, k) => c.classList.toggle('sel', k === i));
    placeHandles(i);
}

function syncGeomInputs(r) {
    loading = true;   // chi cap nhat hien thi, khong kich hoat su kien tu luu cua tung o
    document.querySelectorAll('#palArea [data-f="originX"], #palArea [data-f="originY"], #palArea [data-f="width"], #palArea [data-f="height"]')
        .forEach(inp => { inp.value = r[inp.dataset.f]; });
    loading = false;
}

// ----------------------------------------------------------------- trang thai, danh sach card
function paintConn() {
    const b = $('hdConn');
    const st = live.statuses[cardNo - 1];
    if (!st) { b.textContent = '—'; return; }
    const map = { Online: ['🟢 Online', ''], Offline: ['🔴 Mất kết nối', st.lastError || ''], Pending: ['🟡 Chờ gửi', ''], Disabled: ['⚪ Tắt', ''] };
    const [t, tip] = map[st.state] ?? ['—', ''];
    b.textContent = t + (st.dirty && st.state !== 'Disabled' ? ' · đang chờ gửi' : '');
    b.title = tip || 'Bấm để kiểm tra kết nối';
    const note = st.configChanged ? '⚠️ Cấu hình card đã đổi so với lần gửi cuối — bấm "Gửi tới card" để áp dụng.' : '';
    $('hdCardHint').textContent = (note ? note + ' ' : '') + modelHint();
}

function paintCardList() {
    api(API + '/cards').then(list => {
        $('cardListBody').innerHTML = list.map(c => {
            const st = c.status;
            const state = { Online: '🟢 Online', Offline: '🔴 Mất kết nối', Pending: '🟡 Chờ gửi', Disabled: '⚪' }[st.state] ?? '';
            return `<tr data-no="${c.cardNo}" style="cursor:pointer"><td>${c.cardNo}</td><td>${esc(c.ip)}</td><td>${c.enabled ? 'Enable' : 'Disable'}</td>` +
                `<td>${c.width}×${c.height}</td><td>${esc(c.model)}</td><td>${c.areaCount}</td><td>${state}${st.configChanged ? ' ⚠️' : ''}</td></tr>`;
        }).join('');
    }).catch(() => { });
}
$('cardListBody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-no]');
    if (tr) loadCard(parseInt(tr.dataset.no, 10));
});

async function refreshLive() {
    try {
        live = await api(API + '/live');
        $('hdTransport').innerHTML = `Giao tiếp: <strong>${esc(live.transport)}</strong>` +
            (live.warning ? ` — <span style="color:#f59e0b">⚠️ ${esc(live.warning)}</span>` : '');
        paintConn();
        // cap nhat so tren khung mo phong (Luon_quet_Tick) ma khong ve lai toan bo
        if (card) {
            const els = $('palLedBlock').children;
            card.areas.forEach((r, i) => {
                if (r.type !== 'Number' || !els[i]) return;
                const cv = els[i].querySelector('canvas');
                if (!cv) return;
                const key = areaKey(r, numberShow(r));
                if (cv.dataset.k === key) return;   // khong doi -> khong ve lai
                drawArea(r, cv, curScale);
                cv.dataset.k = key;
            });
        }
        if ($('DanhsachCard').style.display !== 'none') paintCardList();
    } catch { /* thu lai o lan sau */ }
}

init().catch(err => { $('hdTransport').textContent = '⚠️ Không tải được: ' + err.message; });
