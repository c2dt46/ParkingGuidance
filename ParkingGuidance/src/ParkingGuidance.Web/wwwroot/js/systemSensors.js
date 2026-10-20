// ---------------------------------------------------------------------------
// He thong > Cam bien : MGT200 (mac dinh) / Keytop theo PGS (PM200Setting, Keytop)
//   Cai dat ket noi + kich hoat ZCU, giam sat tung ZCU - tung cam bien (poll 1 giay), thu khung du lieu.
// ---------------------------------------------------------------------------
const $ = id => document.getElementById(id);
const API = '/api/system/sensors';
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

async function api(url, options) {
    const res = await fetch(url, options);
    let data = null;
    try { data = await res.json(); } catch { /* khong co noi dung */ }
    if (!res.ok) throw new Error(data?.message || ('HTTP ' + res.status));
    return data;
}
const send = (url, method, body) => api(url, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

let settings = null;      // cai dat dang ap dung (camelCase theo API)
let status = null;        // anh chup trang thai gan nhat
let selDevice = 1;        // ZCU / Keytop dang xem (so thu tu hien thi, tu 1)
let enabledEdit = new Array(32).fill(false);

const currentType = () => document.querySelector('input[name="SensorType"]:checked')?.value || 'Pm200';

// ----------------------------------------------------------------- form cai dat
function showTypeBoxes() {
    const t = currentType();
    $('pm200Box').style.display = t === 'Pm200' ? '' : 'none';
    $('keytopBox').style.display = t === 'Keytop' ? '' : 'none';
    const tl = $('SnTestType');                                   // tab "Thu khung du lieu" hien loai dang chon (chon o tab Cau hinh)
    if (tl) tl.textContent = t === 'Keytop' ? 'Keytop' : 'MGT200';
}

function renderEnableGrid() {
    const box = $('zcuEnable');
    box.innerHTML = enabledEdit.map((on, i) =>
        `<label class="sn-en"><input type="checkbox" data-i="${i}" ${on ? 'checked' : ''} /> ${i}</label>`).join('');
    box.querySelectorAll('input').forEach(cb => cb.addEventListener('change', () => { enabledEdit[+cb.dataset.i] = cb.checked; }));
}

function fillForm(s) {
    document.querySelector(`input[name="SensorType"][value="${s.type}"]`).checked = true;
    $('Pm200Ip').value = s.pm200Ip;
    $('Pm200Port').value = s.pm200Port;
    $('Pm200Wait').value = s.pm200ReplyWaitMs;
    enabledEdit = Array.from({ length: 32 }, (_, i) => !!s.pm200Enabled?.[i]);
    renderEnableGrid();
    $('KeytopPort').value = s.keytopPort;
    $('KeytopIp0').value = s.keytopZcu0Ip;
    $('KeytopCount').value = s.keytopCount;
    $('KeytopTimeout').value = s.keytopTimeoutSeconds;
    showTypeBoxes();
}

function readForm() {
    const num = (id, def) => { const v = parseInt($(id).value, 10); return Number.isNaN(v) ? def : v; };
    return {
        type: currentType(),
        pm200Ip: $('Pm200Ip').value.trim(),
        pm200Port: num('Pm200Port', 0),
        pm200ReplyWaitMs: num('Pm200Wait', 250),
        pm200Enabled: enabledEdit.slice(),
        keytopPort: num('KeytopPort', 0),
        keytopZcu0Ip: $('KeytopIp0').value.trim(),
        keytopCount: num('KeytopCount', 1),
        keytopTimeoutSeconds: num('KeytopTimeout', 50)
    };
}

async function saveSettings() {
    const state = $('SnSaveState');
    try {
        const saved = await send(API + '/settings', 'PUT', readForm());
        settings = saved;
        fillForm(saved);                       // hien lai gia tri da duoc chuan hoa (vd gioi han so luong)
        selDevice = 1;
        state.textContent = '✅ Đã lưu lúc ' + new Date().toLocaleTimeString('vi-VN');
        await refreshStatus();
    } catch (e) {
        state.textContent = '⚠️ Lưu thất bại: ' + e.message;
    }
}

// ----------------------------------------------------------------- giam sat
function ago(iso) {
    if (!iso) return 'chưa có dữ liệu';
    const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 2) return 'vừa xong';
    if (sec < 120) return sec + ' giây trước';
    return Math.round(sec / 60) + ' phút trước';
}

function renderStatus() {
    if (!status) return;
    const keytop = status.type === 'Keytop';
    $('cmdPanel').style.display = keytop ? 'none' : '';
    if (keytop) { stopAuto(); $('CmdAuto').checked = false; }
    const name = 'ZCU'; // PGS gọi cả MGT200 lẫn Keytop là ZCU, đánh số từ 0

    // ket noi chung
    let conn;
    if (keytop) conn = status.listening ? '🟢 Server đang lắng nghe' : '🔴 Server chưa chạy';
    else conn = status.converterOk ? '🟢 Kết nối converter tốt' : '🔴 Chưa kết nối được converter';
    $('snConn').innerHTML = `<b>${keytop ? 'Keytop' : 'MGT200'}</b> — ${conn}` + (status.note ? ` <span class="hint small">· ${esc(status.note)}</span>` : '');

    $('snTotals').innerHTML =
        `<span class="sn-chip s0">Trống <b>${status.free}</b></span>` +
        `<span class="sn-chip s1">Có xe <b>${status.occupied}</b></span>` +
        `<span class="sn-chip s2">Lỗi <b>${status.error}</b></span>` +
        (status.unknown ? `<span class="sn-chip sU">Chưa có dữ liệu <b>${status.unknown}</b></span>` : '');

    // danh sach thiet bi
    const devs = status.devices;
    if (selDevice > devs.length) selDevice = 1;
    $('snDevices').className = 'sn-devices' + (keytop ? '' : ' pm');   // MGT200: 32 nút ZCU chia đều hết bề rộng
    $('snDevices').innerHTML = devs.map(d => {
        const cls = !d.enabled ? 'dis' : d.online ? 'on' : 'off';
        return `<button type="button" class="sn-dev ${cls} ${d.no === selDevice ? 'sel' : ''}" data-no="${d.no}" title="${name} ${d.no - 1}">${d.no - 1}</button>`;
    }).join('');
    $('snDevices').querySelectorAll('.sn-dev').forEach(b => b.addEventListener('click', () => { selDevice = +b.dataset.no; renderStatus(); }));

    // chi tiet thiet bi dang chon
    const d = devs[selDevice - 1];
    if (!d) return;
    const state = !d.enabled ? 'không kích hoạt' : d.online ? 'đang kết nối' : 'mất kết nối';
    $('snDetailHead').innerHTML =
        `<b>${name} ${d.no - 1}</b> — ${state} · cập nhật ${ago(d.lastSeen)}` +
        (d.enabled && d.online ? ` · trống ${d.free} · có xe ${d.occupied} · lỗi ${d.error}` + (d.unknown ? ` · chưa có dữ liệu ${d.unknown}` : '') : '');

    const off = !d.enabled || !d.online;
    const cellCls = c => off ? 'off' : c === '0' ? 's0' : c === '1' ? 's1' : c === '2' ? 's2' : 'sU';
    const cellTip = (c, label) => `${label}: ` + (off ? 'không có kết nối' : c === '0' ? 'trống' : c === '1' ? 'có xe' : c === '2' ? 'lỗi' : 'chưa có dữ liệu');
    const grid = $('snGrid');
    if (keytop) {
        grid.className = 'sn-grid';
        grid.innerHTML = [...d.states].map((c, i) =>
            `<div class="sn-cell ${cellCls(c)}" title="${cellTip(c, 'Cảm biến ' + (i + 1))}">${i + 1}</div>`).join('');
    } else {
        // MGT200: 5 Line (CN 1–5) × 32 cảm biến (ID 0–31)
        grid.className = 'sn-grid pm';
        let h = '';
        for (let line = 0; line < 5; line++) {
            h += `<div class="sn-rowlabel">CN ${line + 1}</div>`;
            for (let id = 0; id < 32; id++) {
                const c = d.states[line * 32 + id] ?? '?';
                h += `<div class="sn-cell ${cellCls(c)}" title="${cellTip(c, `CN ${line + 1} · ID ${id}`)}">${id}</div>`;
            }
        }
        grid.innerHTML = h;
    }
    $('snFrame').textContent = d.lastFrame ? 'Khung dữ liệu gần nhất: ' + d.lastFrame : '';
}

async function refreshStatus() {
    try { status = await api(API + '/status'); renderStatus(); }
    catch (e) { $('snConn').textContent = '⚠️ Không đọc được trạng thái: ' + e.message; }
}

// ----------------------------------------------------------------- cai dat cam bien MGT200 (PM200Setting > Cai dat cam bien)
const CMDS = [
    { name: '1. Ground auto set', hint: 'Tự đo và lưu khoảng cách tới mặt đất.' },
    { name: '2. Ground manual set (100-600 cm)', hint: 'Đặt khoảng cách tới mặt đất bằng tay.', data: { label: 'Chọn khoảng cách mặt đất (cm)', from: 100, to: 600 } },
    { name: '3. Saved Ground Time Report', hint: 'Báo cáo thời gian đã lưu mặt đất.' },
    { name: '4. Ground threshold set (0-200 cm)', hint: 'Đặt chiều cao nhận diện (ngưỡng so với mặt đất).', data: { label: 'Chọn chiều cao nhận diện (cm)', from: 0, to: 200 } },
    { name: '5. Saved Active Time Report', hint: 'Báo cáo thời gian hoạt động đã lưu.' },
    { name: '6. Lamp Mode Set', hint: 'Đặt chế độ màu đèn của cảm biến (0–9).', data: { label: 'Chọn chế độ màu cảm biến', from: 0, to: 9 } },
    { name: '7. Report Detect_Distance', hint: 'Đọc khoảng cách đo được của 32 cảm biến trên Line đã chọn.' }
];
let cmdBusy = false;
let cmdTimer = null;

// Bang mau den cua lenh 6 (Lamp Mode Set): lua chon 0-9 -> mau khi TRONG / khi DO. 5 va 6 khong duoc dinh nghia.
const LAMP = { g: ['green', 'Xanh - Green'], b: ['blue', 'Xanh - Blue'], r: ['red', 'Đỏ - Red'], o: ['off', 'OFF'] };
const LAMP_MODES = [
    ['g', 'r'], ['g', 'o'], ['o', 'o'], ['b', 'r'], ['b', 'o'],
    null, null,
    ['r', 'r'], ['g', 'g'], ['b', 'b']
];
function lampCell(k) {
    const [cls, text] = LAMP[k];
    return `<td class="lc lc-${cls}"><i class="led"></i>${text}</td>`;
}
function initLampTable() {
    const row = i => {
        const m = LAMP_MODES[i];
        return `<tr data-mode="${i}"><td class="lm">${i}</td>` +
            (m ? lampCell(m[0]) + lampCell(m[1]) : '<td class="lc lc-na" colspan="2">Không sử dụng</td>') + '</tr>';
    };
    $('colorBody1').innerHTML = [0, 1, 2, 3, 4].map(row).join('');
    $('colorBody2').innerHTML = [5, 6, 7, 8, 9].map(row).join('');
}
function highlightLamp() {
    const mode = +$('CmdData').value;
    document.querySelectorAll('#figColor tr[data-mode]').forEach(tr => tr.classList.toggle('sel', +tr.dataset.mode === mode));
    if (+$('CmdCommand').value === 6)
        $('CmdHint').textContent = LAMP_MODES[mode] ? CMDS[5].hint : CMDS[5].hint + ' Lưu ý: chế độ ' + mode + ' không được định nghĩa trong bảng màu.';
}

// Anh minh hoa: lenh 1, 2, 4 -> so do lap dat (sang dung thong so cua lenh dang chon); lenh 6 -> bang mau den
function showFigure(cmd) {
    const ground = cmd === 1 || cmd === 2 || cmd === 4;
    const lamp = cmd === 6;
    $('CmdFigure').style.display = ground || lamp ? '' : 'none';
    $('figGround').style.display = ground ? '' : 'none';
    $('figColor').style.display = lamp ? '' : 'none';
    if (ground) {
        $('figGroundSvg').dataset.active = cmd === 1 ? 'auto' : cmd === 2 ? 'manual' : 'thresh';
        $('figGroundLegend').dataset.active = $('figGroundSvg').dataset.active;
    }
    if (lamp) highlightLamp();
}

const fillSelect = (id, items) => { $(id).innerHTML = items.map(([v, t]) => `<option value="${v}">${esc(t)}</option>`).join(''); };

function initCmdPanel() {
    fillSelect('CmdZcu', Array.from({ length: 32 }, (_, i) => [i, 'ZCU ' + i]));
    fillSelect('CmdCommand', CMDS.map((c, i) => [i + 1, c.name]));
    fillSelect('CmdSensor', [...Array.from({ length: 32 }, (_, i) => [i, 'ID ' + i]), [99, 'ALL ID']]);
    onCmdChange();
}

function onCmdChange() {
    const cmd = +$('CmdCommand').value;
    const def = CMDS[cmd - 1];
    const isDist = cmd === 7;

    // Line: lệnh 7 chỉ đọc 1 Line nên bỏ lựa chọn "tất cả"
    const keepCh = $('CmdChannel').value;
    const lines = [1, 2, 3, 4, 5].map(n => [n, 'CN ' + n]);
    if (!isDist) lines.push([9, 'ALL (CN 1 - CN 5)']);
    fillSelect('CmdChannel', lines);
    if ([...$('CmdChannel').options].some(o => o.value === keepCh)) $('CmdChannel').value = keepCh;

    $('CmdSensorWrap').style.display = isDist ? 'none' : '';
    $('CmdDataWrap').style.display = def.data ? '' : 'none';
    if (def.data) {
        $('CmdDataLabel').textContent = def.data.label;
        const opts = [];
        for (let v = def.data.from; v <= def.data.to; v++) opts.push([v, v]);
        fillSelect('CmdData', opts);
    }
    $('CmdHint').textContent = def.hint;
    $('CmdAutoWrap').style.display = isDist ? '' : 'none';
    $('distBox').style.display = isDist ? '' : 'none';
    $('CmdRun').innerHTML = isDist ? '<span class="ico">📏</span> Đọc khoảng cách' : '<span class="ico">▶️</span> Thực hiện';
    if (!isDist) { $('CmdAuto').checked = false; stopAuto(); }
    $('CmdOut').textContent = ''; $('CmdState').textContent = '';
    showFigure(cmd);
}

function renderDistances(cm) {
    $('distGrid').innerHTML = cm.map((v, id) => {
        const bad = v <= 0;
        return `<div class="sn-dcell ${bad ? 'bad' : ''}" title="ID ${id}"><span>ID ${id}</span><b>${v > 0 ? v + ' cm' : v === 0 ? '?' : '??'}</b></div>`;
    }).join('');
}

async function runCommand(silent) {
    if (cmdBusy) return;
    const cmd = +$('CmdCommand').value;
    const body = {
        zcu: +$('CmdZcu').value, command: cmd, channel: +$('CmdChannel').value,
        sensor: cmd === 7 ? 0 : +$('CmdSensor').value, data: CMDS[cmd - 1].data ? +$('CmdData').value : 0
    };
    // lệnh ghi cho nhiều cảm biến một lúc: hỏi lại cho chắc (PGS gửi ngay)
    if (!silent && [1, 2, 4, 6].includes(cmd) && (body.sensor === 99 || body.channel === 9)
        && !confirm('Lệnh này sẽ áp dụng cho NHIỀU cảm biến cùng lúc. Tiếp tục?')) return;

    cmdBusy = true;
    $('CmdRun').disabled = true;
    if (!silent) $('CmdState').textContent = '⏳ Đang gửi…';
    try {
        const r = await send(API + '/command', 'POST', body);
        $('CmdState').textContent = (r.ok ? '✅ ' : '⚠️ ') + r.message;
        $('CmdOut').textContent = `Lệnh gửi: ${r.frame}\nPhản hồi: ${r.reply || '(không có)'}`;
        if (cmd === 7 && r.ok && r.distances) renderDistances(r.distances);
    } catch (e) {
        $('CmdState').textContent = '⚠️ ' + e.message;
        stopAuto(); $('CmdAuto').checked = false;
    } finally {
        cmdBusy = false;
        $('CmdRun').disabled = false;
    }
}

function stopAuto() { if (cmdTimer) { clearInterval(cmdTimer); cmdTimer = null; } }
function toggleAuto() {
    stopAuto();
    if ($('CmdAuto').checked) { runCommand(true); cmdTimer = setInterval(() => runCommand(true), 1500); }
}

// ----------------------------------------------------------------- thu khung du lieu
async function testFrame() {
    const out = $('SnTestOut'), st = $('SnTestState');
    out.textContent = ''; st.textContent = '';
    try {
        const r = await send(API + '/test', 'POST', { type: currentType(), frame: $('SnTestFrame').value });
        st.textContent = (r.ok ? '✅ ' : '⚠️ ') + r.message;
        out.textContent = (r.lines || []).join('\n');
    } catch (e) { st.textContent = '⚠️ ' + e.message; }
}

// ----------------------------------------------------------------- chuyen tab (Cau hinh / Giam sat / Thu khung du lieu)
// Chi tab "Giam sat" (va tab "Du lieu", tu hoi rieng) can cap nhat trang thai moi giay va "Doc lien tuc" cam bien: roi tab do thi ca hai dung han
// (khong hoi may chu, khong gui lenh xuong phan cung khi khong ai xem); quay lai thi cap nhat ngay.
const TAB_KEY = 'pg.snTab';
let activeTab = 'cfg';
function setTab(name) {
    if (!document.getElementById(name + 'Tab')) name = 'cfg';
    const leavingMon = activeTab === 'mon' && name !== 'mon';
    activeTab = name;
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
    document.querySelectorAll('.sys-tab-panel').forEach(p => p.classList.toggle('active', p.id === name + 'Tab'));
    try { sessionStorage.setItem(TAB_KEY, name); } catch { /* bo qua */ }
    if (leavingMon) { stopAuto(); $('CmdAuto').checked = false; }
    if (name === 'mon') refreshStatus();
    document.dispatchEvent(new CustomEvent('sensortab', { detail: { name } }));   // tab "Du lieu" chi hoi may chu khi dang duoc xem
}
document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => setTab(b.dataset.tab)));
document.addEventListener('visibilitychange', () => { if (!document.hidden && activeTab === 'mon') refreshStatus(); });

// ----------------------------------------------------------------- khoi dong
document.querySelectorAll('input[name="SensorType"]').forEach(r => r.addEventListener('change', showTypeBoxes));
$('SnAll').addEventListener('click', () => { enabledEdit.fill(true); renderEnableGrid(); });
$('SnNone').addEventListener('click', () => { enabledEdit.fill(false); renderEnableGrid(); });
$('SnSave').addEventListener('click', saveSettings);
$('SnTest').addEventListener('click', testFrame);
initLampTable();
initCmdPanel();
$('CmdData').addEventListener('change', highlightLamp);
['CmdCommand'].forEach(id => $(id).addEventListener('change', onCmdChange));
['CmdZcu', 'CmdChannel'].forEach(id => $(id).addEventListener('change', () => { if ($('CmdAuto').checked) $('distGrid').innerHTML = ''; }));
$('CmdRun').addEventListener('click', () => runCommand(false));
$('CmdAuto').addEventListener('change', toggleAuto);

(async () => {
    try { settings = await api(API + '/settings'); fillForm(settings); }
    catch (e) { $('SnSaveState').textContent = '⚠️ Không tải được cài đặt: ' + e.message; }
    await refreshStatus();
    setInterval(() => { if (activeTab === 'mon' && !document.hidden) refreshStatus(); }, 1000);   // chi poll khi dang xem tab Giam sat
})();
try { setTab(sessionStorage.getItem(TAB_KEY) || 'cfg'); } catch { setTab('cfg'); }               // nho tab dang xem khi tai lai trang
