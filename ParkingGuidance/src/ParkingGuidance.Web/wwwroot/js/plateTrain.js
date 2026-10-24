// ---------------------------------------------------------------------------
// Trang Camera › tab lon "Train" › "Train bien so": gan khung bien so tren anh, huan luyen them BO DO VUNG BIEN SO (YOLO11, 1 lop).
//   • Them anh (tai len) — tuy chon tu goi y khung bang bo do dang chay.
//   • Chinh khung tren canvas: keo de ve khung moi, keo khung / goc de chinh, Delete de xoa, Enter de luu & sang anh ke.
//   • Train chay o tien trinh Python rieng (may chu); trang chi hoi trang thai 2 giay/lan khi dang xem phan nay va dang train.
//   Cau truc giong "Train xe" (vehicleTrain.js) nhung chi co 1 lop (bien so) nen khong co chon loai khung.
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/plate-train';
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const COLOR = '#06b6d4';               // mau khung bien so
    const HANDLE = 7;                      // ban kinh vung bat goc (px man hinh)
    const MAX_LIST = 400;

    let samples = [];                      // danh sach tom tat (id, name, boxes, reviewed)
    let cur = null;                        // anh dang chinh: { id, name, width, height, reviewed }
    let boxes = [];                        // khung cua anh dang chinh (px anh goc)
    let dirty = false;
    let selected = -1;
    let img = null;                        // HTMLImageElement
    let scale = 1;                         // px man hinh / px anh goc
    let drag = null;                       // { mode: 'draw'|'move'|'resize', ... }
    let timer = null;
    let lastRunning = false;

    const canvas = $('ptCanvas'), ctx = canvas.getContext('2d');

    // ---- zoom bang chuot PHAI + chon khung bang 4 diem (xem trainZoom.js) ----
    const QUAD_KEY = 'pg.plate.quadMode';
    let quadMode = true;                   // true: chuot trai bam 4 GOC cua bien -> khung om sat dung bien; false: keo khung chu nhat
    try { quadMode = localStorage.getItem(QUAD_KEY) !== '0'; } catch { /* che do rieng tu */ }
    let pts = [];                          // cac diem dang chon (toi da 4) o che do 4 diem — px anh goc
    const view = PgZoom.attach(canvas, {
        size: () => (img && cur ? { w: cur.width, h: cur.height } : null),
        onChange: v => { scale = v.scale(); redraw(); },
        bar: { quad: { get: () => quadMode, set: on => { quadMode = on; pts = []; try { localStorage.setItem(QUAD_KEY, on ? '1' : '0'); } catch { /* bo qua */ } } } },
    });
    const tabActive = () => !!window.pgTrainNav && window.pgTrainNav.isActive('plate');   // tab lon Train dang mo VA tab con "Train bien so" dang hien (xem trainHub.js)

    async function jfetch(url, opts) {
        try {
            const res = await fetch(url, opts);
            const body = await res.json().catch(() => ({}));
            return { ok: res.ok, status: res.status, body };
        } catch (e) { return { ok: false, status: 0, body: { message: e.message } }; }
    }
    const say = (id, html, bad) => { const el = $(id); el.innerHTML = bad ? `<span style="color:#ef4444;">${html}</span>` : html; };

    // ----------------------------------------------------------------- thong tin chung + danh sach anh
    async function loadInfo() {
        const r = await jfetch(`${API}/info`);
        if (!r.ok) return;
        const d = r.body;
        $('ptModelInfo').textContent = d.detector.ready ? d.detector.info : `chưa có bộ dò biển${d.detector.error ? ' — ' + d.detector.error : ''}`;
        $('ptRestore').style.display = d.detector.custom ? '' : 'none';
        $('ptCount').textContent = `(${d.counts.total} ảnh · ${d.counts.reviewed} đã duyệt · ${d.counts.boxes} khung` +
            `${d.counts.empty ? ` · ${d.counts.empty} ảnh không có biển` : ''}; cần ≥ ${d.minSamples} ảnh đã duyệt để train)`;
    }

    async function loadSamples() {
        const r = await jfetch(`${API}/samples`);
        if (r.ok) samples = r.body;
        renderGrid();
        loadInfo();
    }

    const visible = () => {
        const f = $('ptFilter').value;
        return samples.filter(s => f === 'all' || (f === 'todo' ? !s.reviewed : s.reviewed));
    };

    function renderGrid() {
        const list = visible();
        const box = $('ptGrid');
        if (!samples.length) { box.innerHTML = '<div class="hint small" style="grid-column:1/-1">Chưa có ảnh nào — bấm “Thêm ảnh”.</div>'; return; }
        if (!list.length) { box.innerHTML = '<div class="hint small" style="grid-column:1/-1">Không có ảnh nào ở bộ lọc này.</div>'; return; }
        box.innerHTML = list.slice(0, MAX_LIST).map(s =>
            `<div class="vt-item${cur && cur.id === s.id ? ' sel' : ''}" data-id="${s.id}" title="${esc(s.name)}">
                <img loading="lazy" src="${API}/samples/${s.id}/thumb" alt="">
                <div class="meta"><span class="${s.reviewed ? 'ok' : 'todo'}">${s.reviewed ? '✓ đã duyệt' : 'chưa duyệt'}</span><span>${s.boxes} khung</span></div>
            </div>`).join('') + (list.length > MAX_LIST ? `<div class="hint small" style="grid-column:1/-1">… và ${list.length - MAX_LIST} ảnh nữa (dùng bộ lọc để thu hẹp)</div>` : '');
    }
    $('ptFilter').addEventListener('change', renderGrid);
    $('ptGrid').addEventListener('click', e => {
        const it = e.target.closest('.vt-item'); if (it) openSample(it.dataset.id);
    });

    // ----------------------------------------------------------------- them / xoa anh
    $('ptFiles').addEventListener('change', async e => {
        const files = [...e.target.files]; e.target.value = '';
        if (!files.length) return;
        const suggest = $('ptSuggest').checked;
        let added = 0, existing = 0; const failed = [];
        const BATCH = 6;                                   // gui theo lo nho: khong vuot gioi han kich thuoc, thay duoc tien do
        for (let i = 0; i < files.length; i += BATCH) {
            say('ptListMsg', `⏳ Đang thêm ${Math.min(i + BATCH, files.length)}/${files.length} ảnh${suggest ? ' (đang gán khung tự động…)' : ''}`);
            const fd = new FormData();
            files.slice(i, i + BATCH).forEach(f => fd.append('files', f));
            const r = await jfetch(`${API}/samples?suggest=${suggest}`, { method: 'POST', body: fd });
            if (!r.ok) { failed.push(r.body.message || `lỗi ${r.status}`); continue; }
            added += r.body.added.length; existing += r.body.existing; failed.push(...r.body.failed.map(f => `${f.name}: ${f.error}`));
        }
        say('ptListMsg', `Đã thêm ${added} ảnh${existing ? ` · ${existing} ảnh đã có` : ''}${failed.length ? ` · ${failed.length} lỗi (${esc(failed[0])})` : ''}.`, failed.length > 0 && !added);
        await loadSamples();
    });

    $('ptClear').addEventListener('click', async () => {
        if (!samples.length) return;
        if (!confirm(`Xoá TẤT CẢ ${samples.length} ảnh huấn luyện biển số (kể cả khung đã gán)? Không ảnh hưởng bộ dò đang dùng.`)) return;
        await jfetch(`${API}/samples`, { method: 'DELETE' });
        closeEditor(true);
        say('ptListMsg', 'Đã xoá hết ảnh huấn luyện.');
        loadSamples();
    });

    // ----------------------------------------------------------------- trinh chinh khung
    async function openSample(id) {
        if (dirty && !confirm('Ảnh đang chỉnh có thay đổi chưa lưu. Bỏ thay đổi và mở ảnh khác?')) return;
        const r = await jfetch(`${API}/samples/${id}`);
        if (!r.ok) { say('ptListMsg', r.body.message || 'Không mở được ảnh.', true); return; }
        const s = r.body;
        const image = new Image();
        image.onload = () => {
            cur = { id: s.id, name: s.imageName, width: s.width, height: s.height, reviewed: s.reviewed };
            boxes = (s.boxes || []).map(b => ({ x1: b.x1, y1: b.y1, x2: b.x2, y2: b.y2 }));
            img = image; selected = -1; dirty = false; drag = null;
            $('ptEditorPanel').style.display = '';
            $('ptEditTitle').textContent = `Chỉnh khung biển — ${cur.name} (${cur.width}×${cur.height})${cur.reviewed ? ' · đã duyệt' : ''}`;
            say('ptEditMsg', '');
            layout(); redraw(); renderGrid();
            $('ptEditorPanel').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        };
        image.onerror = () => say('ptListMsg', 'Không tải được ảnh.', true);
        image.src = `${API}/samples/${id}/image`;
    }

    function closeEditor(force) {
        if (!force && dirty && !confirm('Ảnh đang chỉnh có thay đổi chưa lưu. Đóng và bỏ thay đổi?')) return;
        cur = null; img = null; boxes = []; dirty = false; selected = -1; drag = null;
        $('ptEditorPanel').style.display = 'none';
        renderGrid();
    }
    $('ptCloseEditor').addEventListener('click', () => closeEditor(false));

    // canvas vua khung chieu rong khung chua, cao toi da ~70% man hinh
    function layout() {
        if (!img) return;
        const maxW = Math.max(320, $('ptStage').clientWidth - 2);
        const maxH = Math.max(240, Math.floor(window.innerHeight * 0.7));
        scale = Math.min(maxW / cur.width, maxH / cur.height);
        canvas.width = Math.round(cur.width * scale);
        canvas.height = Math.round(cur.height * scale);
        view.setBase(scale);
        pts = [];
    }
    window.addEventListener('resize', () => { if (img && tabActive()) { layout(); redraw(); } });

    const toImg = e => view.toImg(e);                       // da tinh ca vung dang phong to
    const clampX = x => Math.max(0, Math.min(cur.width, x));
    const clampY = y => Math.max(0, Math.min(cur.height, y));

    function redraw() {
        if (!img) return;
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        const zoomed = view.z > 1.001, ox = zoomed ? view.vx : 0, oy = zoomed ? view.vy : 0;   // goc tren-trai vung dang xem (px anh)
        if (zoomed) ctx.drawImage(img, ox, oy, canvas.width / scale, canvas.height / scale, 0, 0, canvas.width, canvas.height);
        else ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        const all = drag && drag.mode === 'draw' && drag.box ? [...boxes, drag.box] : boxes;
        all.forEach((b, i) => {
            const c = COLOR;
            const x = (Math.min(b.x1, b.x2) - ox) * scale, y = (Math.min(b.y1, b.y2) - oy) * scale;
            const w = Math.abs(b.x2 - b.x1) * scale, h = Math.abs(b.y2 - b.y1) * scale;
            const sel = i === selected;
            ctx.lineWidth = sel ? 3 : 2; ctx.strokeStyle = c; ctx.strokeRect(x, y, w, h);
            ctx.fillStyle = c + (sel ? '33' : '1a'); ctx.fillRect(x, y, w, h);
            ctx.font = '12px sans-serif'; const label = `${i + 1} biển`;
            const tw = ctx.measureText(label).width + 8;
            ctx.fillStyle = c; ctx.fillRect(x, Math.max(0, y - 16), tw, 16);
            ctx.fillStyle = '#000'; ctx.fillText(label, x + 4, Math.max(12, y - 4));
            if (sel) [[x, y], [x + w, y], [x, y + h], [x + w, y + h]].forEach(([hx, hy]) => { ctx.fillStyle = '#fff'; ctx.fillRect(hx - 4, hy - 4, 8, 8); ctx.strokeRect(hx - 4, hy - 4, 8, 8); });
        });
        // da giac 4 diem cua cac khung vua tao + cac diem dang chon
        const px = v => (v - ox) * scale, py = v => (v - oy) * scale;
        boxes.forEach(b => {
            if (!b.quad) return;
            ctx.save(); ctx.setLineDash([4, 3]); ctx.lineWidth = 1.5; ctx.strokeStyle = '#fff';
            ctx.beginPath(); b.quad.forEach((q, k) => (k ? ctx.lineTo(px(q.x), py(q.y)) : ctx.moveTo(px(q.x), py(q.y)))); ctx.closePath(); ctx.stroke(); ctx.restore();
        });
        if (pts.length) {
            ctx.save(); ctx.lineWidth = 1.5; ctx.strokeStyle = '#facc15';
            ctx.beginPath(); pts.forEach((q, k) => (k ? ctx.lineTo(px(q.x), py(q.y)) : ctx.moveTo(px(q.x), py(q.y)))); ctx.stroke();
            pts.forEach((q, k) => {
                ctx.beginPath(); ctx.arc(px(q.x), py(q.y), 6, 0, 7); ctx.fillStyle = '#facc15'; ctx.fill(); ctx.strokeStyle = '#000'; ctx.stroke();
                ctx.fillStyle = '#000'; ctx.font = 'bold 10px sans-serif'; ctx.fillText(String(k + 1), px(q.x) - 3, py(q.y) + 3.5);
            });
            ctx.restore();
        }
        view.drawOverlay(ctx);
        view.setHint(quadMode ? (pts.length ? `đã chọn ${pts.length}/4 điểm — bấm tiếp góc còn lại (Backspace: bỏ điểm cuối · Esc: bỏ hết)` : 'chuột TRÁI: bấm 4 GÓC của biển số') : '');
        $('ptBoxInfo').textContent = boxes.length
            ? `${boxes.length} khung biển. Khung nên ôm sát biển số (biển 2 dòng: một khung chứa cả hai dòng).`
            : 'Chưa có khung nào. Nếu ảnh thật sự không có biển số, bấm “✓ Lưu & duyệt” để dạy bộ dò đây là ảnh không có biển.';
    }

    function hitCorner(p) {
        if (selected < 0) return null;
        const b = boxes[selected];
        const xs = [Math.min(b.x1, b.x2), Math.max(b.x1, b.x2)], ys = [Math.min(b.y1, b.y2), Math.max(b.y1, b.y2)];
        for (const xi of [0, 1]) for (const yi of [0, 1])
            if (Math.hypot((p.x - xs[xi]) * scale, (p.y - ys[yi]) * scale) <= HANDLE + 3) return { xi, yi };
        return null;
    }
    function hitBox(p) {
        for (let i = boxes.length - 1; i >= 0; i--) {
            const b = boxes[i];
            if (p.x >= Math.min(b.x1, b.x2) && p.x <= Math.max(b.x1, b.x2) && p.y >= Math.min(b.y1, b.y2) && p.y <= Math.max(b.y1, b.y2)) return i;
        }
        return -1;
    }

    // Che do 4 diem: bam trai vao 4 GOC cua bien -> khung = hinh chu nhat nho nhat bao tron 4 diem (om sat dung bien, ke ca bien nghieng)
    function addPoint(p) {
        pts.push({ x: clampX(p.x), y: clampY(p.y) });
        if (pts.length < 4) { say('ptEditMsg', `📍 Đã chọn ${pts.length}/4 điểm — bấm tiếp các góc còn lại của biển số.`); return; }
        const xs = pts.map(q => q.x), ys = pts.map(q => q.y);
        const box = { x1: Math.min(...xs), y1: Math.min(...ys), x2: Math.max(...xs), y2: Math.max(...ys), quad: PgZoom.orderQuad(pts).map(q => ({ x: q.x, y: q.y })) };
        pts = [];
        if (box.x2 - box.x1 < 6 || box.y2 - box.y1 < 6) { say('ptEditMsg', 'Bốn điểm quá gần nhau — hãy chọn lại 4 góc của biển.', true); return; }
        boxes.push(box); selected = boxes.length - 1; dirty = true;
        say('ptEditMsg', '✔ Đã tạo khung ôm sát biển từ 4 điểm. Cần chỉnh thì kéo góc / kéo khung; xong bấm Enter để lưu &amp; duyệt.');
    }

    canvas.addEventListener('mousedown', e => {
        if (!img || e.button !== 0) return;
        const p = toImg(e);
        const corner = hitCorner(p);
        if (corner) {                                                   // keo goc de doi kich thuoc: dua canh doi dien thanh diem co dinh
            const b = boxes[selected];
            const x1 = Math.min(b.x1, b.x2), x2 = Math.max(b.x1, b.x2), y1 = Math.min(b.y1, b.y2), y2 = Math.max(b.y1, b.y2);
            Object.assign(b, { x1, y1, x2, y2 });
            drag = { mode: 'resize', fixedX: corner.xi ? x1 : x2, fixedY: corner.yi ? y1 : y2 };
        } else {
            const i = hitBox(p);
            if (i >= 0) {
                selected = i;
                drag = { mode: 'move', start: p, orig: { ...boxes[i] } };
            } else {
                selected = -1;
                if (quadMode) addPoint(p);                          // che do 4 diem: moi lan bam trai them 1 goc cua bien
                else drag = { mode: 'draw', start: p, box: null };
            }
        }
        redraw(); e.preventDefault();
    });
    window.addEventListener('mousemove', e => {
        if (!drag || !img) return;
        const p = toImg(e);
        if (drag.mode === 'draw') {
            drag.box = { x1: clampX(drag.start.x), y1: clampY(drag.start.y), x2: clampX(p.x), y2: clampY(p.y) };
        } else if (drag.mode === 'move') {
            const o = drag.orig, b = boxes[selected];
            const w = Math.abs(o.x2 - o.x1), h = Math.abs(o.y2 - o.y1);
            const dx = p.x - drag.start.x, dy = p.y - drag.start.y;
            const nx = Math.max(0, Math.min(cur.width - w, Math.min(o.x1, o.x2) + dx));
            const ny = Math.max(0, Math.min(cur.height - h, Math.min(o.y1, o.y2) + dy));
            Object.assign(b, { x1: nx, y1: ny, x2: nx + w, y2: ny + h });
            delete b.quad;
            dirty = true;
        } else if (drag.mode === 'resize') {
            const b = boxes[selected];
            Object.assign(b, { x1: drag.fixedX, y1: drag.fixedY, x2: clampX(p.x), y2: clampY(p.y) });
            delete b.quad;
            dirty = true;
        }
        redraw();
    });
    window.addEventListener('mouseup', () => {
        if (!drag) return;
        if (drag.mode === 'draw' && drag.box && Math.abs(drag.box.x2 - drag.box.x1) >= 6 && Math.abs(drag.box.y2 - drag.box.y1) >= 6) {
            boxes.push(drag.box); selected = boxes.length - 1; dirty = true;
        }
        drag = null; redraw();
    });

    $('ptDelBox').addEventListener('click', delSelected);
    function delSelected() {
        if (selected < 0) return;
        boxes.splice(selected, 1); selected = -1; dirty = true; redraw();
    }
    $('ptClearBoxes').addEventListener('click', () => {
        if (!boxes.length) return;
        if (confirm('Xoá hết khung biển của ảnh này?')) { boxes = []; selected = -1; dirty = true; redraw(); }
    });

    // phim tat (chi khi tab dang hien, dang chinh anh va khong go trong o nhap)
    window.addEventListener('keydown', e => {
        if (!tabActive() || !cur) return;
        const tag = (e.target.tagName || '').toLowerCase();
        if (tag === 'input' || tag === 'select' || tag === 'textarea') return;
        if (e.key === 'Escape' && pts.length) { pts = []; say('ptEditMsg', ''); redraw(); }
        else if (e.key === 'Backspace' && pts.length) { pts.pop(); redraw(); e.preventDefault(); }
        else if (e.key === 'Delete' || e.key === 'Backspace') { delSelected(); e.preventDefault(); }
        else if (e.key === 'Enter') { saveCurrent(true); e.preventDefault(); }
        else if (e.key === 'Escape') { selected = -1; redraw(); }
    });

    $('ptSuggestAgain').addEventListener('click', async () => {
        if (!cur) return;
        if (boxes.length && !confirm('Thay các khung hiện có bằng khung gợi ý từ bộ dò?')) return;
        say('ptEditMsg', '⏳ Đang chạy bộ dò…');
        const r = await jfetch(`${API}/samples/${cur.id}/suggest`, { method: 'POST' });
        if (!r.ok) { say('ptEditMsg', esc(r.body.message || 'Không gợi ý được.'), true); return; }
        boxes = (r.body.boxes || []).map(b => ({ x1: b.x1, y1: b.y1, x2: b.x2, y2: b.y2 })); selected = -1; dirty = true;
        say('ptEditMsg', `Bộ dò gợi ý ${boxes.length} khung — hãy kiểm tra, sửa cho ôm sát biển rồi duyệt.`);
        redraw();
    });

    // luu anh dang chinh; reviewed = true -> da duyet va sang anh chua duyet ke tiep
    async function saveCurrent(reviewed) {
        if (!cur) return;
        say('ptEditMsg', '⏳ Đang lưu…');
        const r = await jfetch(`${API}/samples/${cur.id}`, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ boxes: boxes.map(b => ({ x1: b.x1, y1: b.y1, x2: b.x2, y2: b.y2 })), reviewed })
        });
        if (!r.ok) { say('ptEditMsg', esc(r.body.message || 'Không lưu được.'), true); return; }
        dirty = false;
        const idx = samples.findIndex(s => s.id === cur.id);
        if (idx >= 0) { samples[idx].boxes = r.body.boxes.length; samples[idx].reviewed = r.body.reviewed; }
        cur.reviewed = r.body.reviewed;
        loadInfo();
        if (reviewed) { nextSample(true); } else { say('ptEditMsg', '✔ Đã lưu (chưa duyệt).'); renderGrid(); }
    }
    $('ptSaveNext').addEventListener('click', () => saveCurrent(true));
    $('ptSave').addEventListener('click', () => saveCurrent(false));

    // anh ke tiep: uu tien anh CHUA duyet dung sau anh hien tai, neu het thi dong trinh chinh
    function nextSample(afterSave) {
        const list = visible();
        const i = list.findIndex(s => cur && s.id === cur.id);
        const after = list.slice(i + 1).find(s => !s.reviewed) || list.find(s => !s.reviewed && (!cur || s.id !== cur.id));
        if (after) { dirty = false; openSample(after.id); }
        else {
            dirty = false;
            if (afterSave) say('ptListMsg', '🎉 Đã duyệt hết ảnh trong danh sách.');
            closeEditor(true);
        }
    }
    $('ptSkip').addEventListener('click', () => nextSample(false));
    $('ptDelSample').addEventListener('click', async () => {
        if (!cur || !confirm('Xoá ảnh này khỏi bộ huấn luyện?')) return;
        const id = cur.id;
        await jfetch(`${API}/samples/${id}`, { method: 'DELETE' });
        samples = samples.filter(s => s.id !== id);
        dirty = false;
        nextSample(false);
        loadSamples();
    });

    // ----------------------------------------------------------------- huan luyen
    const STATE_TEXT = {
        preparing: 'Đang chuẩn bị dữ liệu…', 'evaluating-base': 'Đang đo bộ dò hiện tại…', training: 'Đang huấn luyện…',
        'evaluating-new': 'Đang đo model mới…', exporting: 'Đang xuất model ONNX…', done: 'Hoàn tất', error: 'Lỗi'
    };
    const pct = v => (v == null ? '—' : (v * 100).toFixed(1) + '%');

    function metricsTable(m) {
        if (!m || (!m.base && !m.new)) return '';
        const keys = [['map5095', 'mAP50-95 (chỉ số chính)'], ['map50', 'mAP50'], ['precision', 'Precision (độ chính xác)'], ['recall', 'Recall (độ phủ)']];
        const rows = keys.map(([k, label]) => {
            const a = m.base ? m.base[k] : null, b = m.new ? m.new[k] : null;
            let delta = '—';
            if (a != null && b != null) { const d = (b - a) * 100; delta = `<span class="${d >= 0 ? 'vt-up' : 'vt-down'}">${d >= 0 ? '+' : ''}${d.toFixed(1)}</span>`; }
            return `<tr><td>${label}</td><td>${pct(a)}</td><td>${pct(b)}</td><td>${delta}</td></tr>`;
        }).join('');
        const note = (!m.base && m.new)
            ? '<div class="hint small" style="margin-top:8px;">Bộ dò hiện tại (YOLOv3-tiny mặc định) không đo được bằng cùng thước đo nên không có số so sánh — xem số của model mới để biết nó đã đủ tốt chưa.</div>'
            : '';
        return note + `<table class="vt-metrics"><thead><tr><th>Trên tập kiểm tra</th><th>Bộ dò hiện tại</th><th>Model mới</th><th>Chênh (điểm %)</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    function renderStatus(s) {
        const running = !!s.running;
        const st = s.status || null;
        $('ptModelInfo').textContent = s.modelInfo || $('ptModelInfo').textContent;
        $('ptRestore').style.display = s.usingCustom ? '' : 'none';
        $('ptTrain').disabled = running; $('ptScratch').disabled = running; $('ptEpochs').disabled = running;
        $('ptCancel').style.display = running ? '' : 'none';
        $('ptInstall').style.display = s.canInstall ? '' : 'none';
        $('ptResultClear').style.display = (!running && (st || s.cancelled || s.install)) ? '' : 'none';
        if (!s.scriptFound) say('ptTrainMsg', '⚠️ Không tìm thấy tools/train_plate.py — đặt đường dẫn ở PlateTrainer:Script trong appsettings.json.', true);

        const prog = $('ptProgress');
        let msg = '';
        if (running || (st && st.state && st.state !== 'done' && st.state !== 'error')) {
            const ep = st && st.epoch || 0, eps = st && st.epochs || 0;
            prog.style.display = st && st.state === 'training' && eps ? '' : 'none';
            $('ptProgressBar').style.width = eps ? `${Math.min(100, ep / eps * 100)}%` : '0%';
            msg = `⏳ ${esc((st && (st.message || STATE_TEXT[st.state])) || 'Đang khởi động Python…')}`;
        } else {
            prog.style.display = 'none';
            if (s.cancelled) msg = 'Đã huỷ huấn luyện.';
            else if (st && st.state === 'error') msg = `<span style="color:#ef4444;">❌ ${esc(st.message || 'Lỗi')}</span>`;
            else if (st && st.state === 'done') msg = '✔ Huấn luyện xong.';
        }
        if (st && st.images) msg += ` <span class="hint small">(${st.images.train} ảnh train · ${st.images.val} ảnh kiểm tra · ${st.images.boxes} khung${st.images.negatives ? ` · ${st.images.negatives} ảnh không có biển` : ''})</span>`;
        if (s.install) msg += `<br>${s.install.ok ? '✅' : '❌'} ${esc(s.install.message)}`;
        else if (st && st.state === 'done' && !running) {
            const hasBase = !!(st.metrics && st.metrics.base && st.metrics.base.map5095 != null);
            const need = Math.round((s.minAutoInstallMap50 || 0.6) * 100);
            msg += hasBase
                ? `<br><span style="color:#d97706;">Model mới chưa vượt bộ dò đang chạy nên chưa được cài tự động. Bấm “✔ Vẫn cài bản mới” nếu muốn dùng.</span>`
                : `<br><span style="color:#d97706;">Chưa có số đo của bộ dò hiện tại để so sánh, nên model mới chỉ tự cài khi mAP50 ≥ ${need}% — lần này chưa đạt. Thêm / sửa ảnh rồi train lại, hoặc bấm “✔ Vẫn cài bản mới” nếu muốn dùng thử.</span>`;
        }
        if (s.scriptFound) say('ptTrainMsg', msg);
        $('ptResult').innerHTML = (st ? metricsTable(st.metrics) : '') +
            ((s.log && s.log.length && st && st.state === 'error') ? `<pre class="hint small" style="white-space:pre-wrap;">${esc(s.log.join('\n'))}</pre>` : '');
    }

    async function fetchStatus() {
        const r = await jfetch(`${API}/train/status`);
        if (!r.ok) return;
        renderStatus(r.body);
        const was = lastRunning; lastRunning = !!r.body.running;
        if (was && !lastRunning) loadInfo();                                   // vua xong: model co the da doi
        updatePolling(r.body.running);
    }
    function updatePolling(running) {
        const need = running && tabActive() && !document.hidden;
        if (need && !timer) timer = setInterval(fetchStatus, 2000);
        else if (!need && timer) { clearInterval(timer); timer = null; }
    }

    $('ptTrain').addEventListener('click', async () => {
        const epochs = parseInt($('ptEpochs').value, 10) || 40;
        const r = await jfetch(`${API}/train/start`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ epochs, fromScratch: $('ptScratch').checked })
        });
        if (!r.ok) { say('ptTrainMsg', esc(r.body.message || 'Không bắt đầu được.'), true); return; }
        say('ptTrainMsg', '⏳ Đang khởi động…');
        fetchStatus();
    });
    $('ptCancel').addEventListener('click', async () => {
        if (!confirm('Huỷ huấn luyện đang chạy?')) return;
        await jfetch(`${API}/train/cancel`, { method: 'POST' });
        fetchStatus();
    });
    $('ptInstall').addEventListener('click', async () => {
        if (!confirm('Cài model biển số mới dù chỉ số chưa vượt bộ dò đang chạy? Bản cũ được sao lưu.')) return;
        const r = await jfetch(`${API}/train/install`, { method: 'POST' });
        say('ptTrainMsg', esc(r.body.message || ''), !r.ok);
        fetchStatus(); loadInfo();
    });
    $('ptResultClear').addEventListener('click', async () => {
        if (!confirm('Xoá kết quả lần train gần nhất? Không ảnh hưởng bộ dò đang dùng và các ảnh huấn luyện.')) return;
        await jfetch(`${API}/train/clear`, { method: 'POST' });
        fetchStatus();
    });
    $('ptRestore').addEventListener('click', async () => {
        if (!confirm('Bỏ model biển số đã train thêm và quay về bộ dò mặc định? (Bản đã train được giữ lại trong thư mục data/plate-train/active)')) return;
        const r = await jfetch(`${API}/train/restore`, { method: 'POST' });
        say('ptTrainMsg', esc(r.body.message || ''), !r.ok);
        fetchStatus(); loadInfo();
    });

    // ----------------------------------------------------------------- vao / roi tab
    let loaded = false;
    function sync() {
        if (tabActive()) {
            if (!loaded) { loaded = true; loadSamples(); }
            else loadInfo();
            fetchStatus();
            if (img) { layout(); redraw(); }          // canvas chi do duoc kich thuoc khi tab dang hien
        } else updatePolling(false);
    }
    document.addEventListener('pg:train-nav', sync);                 // trainHub.js bao sau MOI lan doi tab (tab lon hoac tab con)
    document.addEventListener('pg:train-data-imported', () => { if (loaded) loadSamples(); });   // vua Import (tab Mo phong): tai lai danh sach anh
    document.addEventListener('visibilitychange', () => { if (!document.hidden && tabActive()) fetchStatus(); else updatePolling(false); });
    window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
    sync();
})();
