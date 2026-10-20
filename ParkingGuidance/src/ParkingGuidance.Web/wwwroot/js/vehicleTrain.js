// ---------------------------------------------------------------------------
// Trang Camera › tab "Train xe": gan khung xe tren anh, huan luyen them model nhan dien xe YOLO11.
//   • Them anh (tai len) — tuy chon tu goi y khung bang model dang chay.
//   • Chinh khung tren canvas: keo de ve khung moi, keo khung / goc de chinh, Delete de xoa, phim 1-4 doi loai xe, Enter de luu & sang anh ke.
//   • Train chay o tien trinh Python rieng (may chu); trang chi hoi trang thai 2 giay/lan khi dang xem tab nay va dang train.
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/vehicle-train';
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const COLORS = { car: '#22c55e', motorcycle: '#f59e0b', bus: '#3b82f6', truck: '#ef4444' };
    const CLS_BY_KEY = { '1': 'car', '2': 'motorcycle', '3': 'bus', '4': 'truck' };
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

    const canvas = $('vtCanvas'), ctx = canvas.getContext('2d');
    const tabActive = () => !!window.pgTrainNav && window.pgTrainNav.isActive('vehicle');   // tab lon Train dang mo VA tab con "Train xe" dang hien (xem trainHub.js)

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
        $('vtModelInfo').textContent = d.detector.ready ? d.detector.info : `chưa nạp model${d.detector.error ? ' — ' + d.detector.error : ''}`;
        $('vtRestore').style.display = d.detector.custom ? '' : 'none';
        $('vtCount').textContent = `(${d.counts.total} ảnh · ${d.counts.reviewed} đã duyệt · ${d.counts.boxes} khung` +
            `${d.counts.empty ? ` · ${d.counts.empty} ảnh không có xe` : ''}; cần ≥ ${d.minSamples} ảnh đã duyệt để train)`;
    }

    async function loadSamples() {
        const r = await jfetch(`${API}/samples`);
        if (r.ok) samples = r.body;
        renderGrid();
        loadInfo();
    }

    const visible = () => {
        const f = $('vtFilter').value;
        return samples.filter(s => f === 'all' || (f === 'todo' ? !s.reviewed : s.reviewed));
    };

    function renderGrid() {
        const list = visible();
        const box = $('vtGrid');
        if (!samples.length) { box.innerHTML = '<div class="hint small" style="grid-column:1/-1">Chưa có ảnh nào — bấm “Thêm ảnh”.</div>'; return; }
        if (!list.length) { box.innerHTML = '<div class="hint small" style="grid-column:1/-1">Không có ảnh nào ở bộ lọc này.</div>'; return; }
        box.innerHTML = list.slice(0, MAX_LIST).map(s =>
            `<div class="vt-item${cur && cur.id === s.id ? ' sel' : ''}" data-id="${s.id}" title="${esc(s.name)}">
                <img loading="lazy" src="${API}/samples/${s.id}/thumb" alt="">
                <div class="meta"><span class="${s.reviewed ? 'ok' : 'todo'}">${s.reviewed ? '✓ đã duyệt' : 'chưa duyệt'}</span><span>${s.boxes} khung</span></div>
            </div>`).join('') + (list.length > MAX_LIST ? `<div class="hint small" style="grid-column:1/-1">… và ${list.length - MAX_LIST} ảnh nữa (dùng bộ lọc để thu hẹp)</div>` : '');
    }
    $('vtFilter').addEventListener('change', renderGrid);
    $('vtGrid').addEventListener('click', e => {
        const it = e.target.closest('.vt-item'); if (it) openSample(it.dataset.id);
    });

    // ----------------------------------------------------------------- them / xoa anh
    $('vtFiles').addEventListener('change', async e => {
        const files = [...e.target.files]; e.target.value = '';
        if (!files.length) return;
        const suggest = $('vtSuggest').checked;
        let added = 0, existing = 0; const failed = [];
        const BATCH = 6;                                   // gui theo lo nho: khong vuot gioi han kich thuoc, thay duoc tien do
        for (let i = 0; i < files.length; i += BATCH) {
            say('vtListMsg', `⏳ Đang thêm ${Math.min(i + BATCH, files.length)}/${files.length} ảnh${suggest ? ' (đang gán khung tự động…)' : ''}`);
            const fd = new FormData();
            files.slice(i, i + BATCH).forEach(f => fd.append('files', f));
            const r = await jfetch(`${API}/samples?suggest=${suggest}`, { method: 'POST', body: fd });
            if (!r.ok) { failed.push(r.body.message || `lỗi ${r.status}`); continue; }
            added += r.body.added.length; existing += r.body.existing; failed.push(...r.body.failed.map(f => `${f.name}: ${f.error}`));
        }
        say('vtListMsg', `Đã thêm ${added} ảnh${existing ? ` · ${existing} ảnh đã có` : ''}${failed.length ? ` · ${failed.length} lỗi (${esc(failed[0])})` : ''}.`, failed.length > 0 && !added);
        await loadSamples();
    });

    $('vtClear').addEventListener('click', async () => {
        if (!samples.length) return;
        if (!confirm(`Xoá TẤT CẢ ${samples.length} ảnh huấn luyện (kể cả khung đã gán)? Không ảnh hưởng model đang dùng.`)) return;
        await jfetch(`${API}/samples`, { method: 'DELETE' });
        closeEditor(true);
        say('vtListMsg', 'Đã xoá hết ảnh huấn luyện.');
        loadSamples();
    });

    // ----------------------------------------------------------------- trinh chinh khung
    async function openSample(id) {
        if (dirty && !confirm('Ảnh đang chỉnh có thay đổi chưa lưu. Bỏ thay đổi và mở ảnh khác?')) return;
        const r = await jfetch(`${API}/samples/${id}`);
        if (!r.ok) { say('vtListMsg', r.body.message || 'Không mở được ảnh.', true); return; }
        const s = r.body;
        const image = new Image();
        image.onload = () => {
            cur = { id: s.id, name: s.imageName, width: s.width, height: s.height, reviewed: s.reviewed };
            boxes = (s.boxes || []).map(b => ({ cls: b.cls, x1: b.x1, y1: b.y1, x2: b.x2, y2: b.y2 }));
            img = image; selected = -1; dirty = false; drag = null;
            $('vtEditorPanel').style.display = '';
            $('vtEditTitle').textContent = `Chỉnh khung — ${cur.name} (${cur.width}×${cur.height})${cur.reviewed ? ' · đã duyệt' : ''}`;
            say('vtEditMsg', '');
            layout(); redraw(); renderGrid();
            $('vtEditorPanel').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        };
        image.onerror = () => say('vtListMsg', 'Không tải được ảnh.', true);
        image.src = `${API}/samples/${id}/image`;
    }

    function closeEditor(force) {
        if (!force && dirty && !confirm('Ảnh đang chỉnh có thay đổi chưa lưu. Đóng và bỏ thay đổi?')) return;
        cur = null; img = null; boxes = []; dirty = false; selected = -1; drag = null;
        $('vtEditorPanel').style.display = 'none';
        renderGrid();
    }
    $('vtCloseEditor').addEventListener('click', () => closeEditor(false));

    // canvas vua khung chieu rong khung chua, cao toi da ~70% man hinh
    function layout() {
        if (!img) return;
        const maxW = Math.max(320, $('vtStage').clientWidth - 2);
        const maxH = Math.max(240, Math.floor(window.innerHeight * 0.7));
        scale = Math.min(maxW / cur.width, maxH / cur.height);
        canvas.width = Math.round(cur.width * scale);
        canvas.height = Math.round(cur.height * scale);
    }
    window.addEventListener('resize', () => { if (img && tabActive()) { layout(); redraw(); } });

    const toImg = e => {
        const r = canvas.getBoundingClientRect();
        return { x: (e.clientX - r.left) * (cur.width / r.width), y: (e.clientY - r.top) * (cur.height / r.height) };
    };
    const clampX = x => Math.max(0, Math.min(cur.width, x));
    const clampY = y => Math.max(0, Math.min(cur.height, y));

    function redraw() {
        if (!img) return;
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        const all = drag && drag.mode === 'draw' && drag.box ? [...boxes, drag.box] : boxes;
        all.forEach((b, i) => {
            const c = COLORS[b.cls] || '#fff';
            const x = Math.min(b.x1, b.x2) * scale, y = Math.min(b.y1, b.y2) * scale;
            const w = Math.abs(b.x2 - b.x1) * scale, h = Math.abs(b.y2 - b.y1) * scale;
            const sel = i === selected;
            ctx.lineWidth = sel ? 3 : 2; ctx.strokeStyle = c; ctx.strokeRect(x, y, w, h);
            ctx.fillStyle = c + (sel ? '33' : '1a'); ctx.fillRect(x, y, w, h);
            ctx.font = '12px sans-serif'; const label = `${i + 1} ${b.cls}`;
            const tw = ctx.measureText(label).width + 8;
            ctx.fillStyle = c; ctx.fillRect(x, Math.max(0, y - 16), tw, 16);
            ctx.fillStyle = '#000'; ctx.fillText(label, x + 4, Math.max(12, y - 4));
            if (sel) [[x, y], [x + w, y], [x, y + h], [x + w, y + h]].forEach(([hx, hy]) => { ctx.fillStyle = '#fff'; ctx.fillRect(hx - 4, hy - 4, 8, 8); ctx.strokeRect(hx - 4, hy - 4, 8, 8); });
        });
        $('vtBoxInfo').textContent = boxes.length
            ? `${boxes.length} khung: ` + ['car', 'motorcycle', 'bus', 'truck'].map(k => [k, boxes.filter(b => b.cls === k).length]).filter(([, n]) => n).map(([k, n]) => `${k} ${n}`).join(' · ')
            : 'Chưa có khung nào. Nếu ảnh thật sự không có xe, bấm “✓ Lưu & duyệt” để dạy model đây là ảnh không có xe.';
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
                selected = i; $('vtCls').value = boxes[i].cls;
                drag = { mode: 'move', start: p, orig: { ...boxes[i] } };
            } else {
                selected = -1;
                drag = { mode: 'draw', start: p, box: null };
            }
        }
        redraw(); e.preventDefault();
    });
    window.addEventListener('mousemove', e => {
        if (!drag || !img) return;
        const p = toImg(e);
        if (drag.mode === 'draw') {
            drag.box = { cls: $('vtCls').value, x1: clampX(drag.start.x), y1: clampY(drag.start.y), x2: clampX(p.x), y2: clampY(p.y) };
        } else if (drag.mode === 'move') {
            const o = drag.orig, b = boxes[selected];
            const w = Math.abs(o.x2 - o.x1), h = Math.abs(o.y2 - o.y1);
            const dx = p.x - drag.start.x, dy = p.y - drag.start.y;
            const nx = Math.max(0, Math.min(cur.width - w, Math.min(o.x1, o.x2) + dx));
            const ny = Math.max(0, Math.min(cur.height - h, Math.min(o.y1, o.y2) + dy));
            Object.assign(b, { x1: nx, y1: ny, x2: nx + w, y2: ny + h });
            dirty = true;
        } else if (drag.mode === 'resize') {
            const b = boxes[selected];
            Object.assign(b, { x1: drag.fixedX, y1: drag.fixedY, x2: clampX(p.x), y2: clampY(p.y) });
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

    $('vtCls').addEventListener('change', () => {
        if (selected >= 0) { boxes[selected].cls = $('vtCls').value; dirty = true; redraw(); }
    });
    $('vtDelBox').addEventListener('click', delSelected);
    function delSelected() {
        if (selected < 0) return;
        boxes.splice(selected, 1); selected = -1; dirty = true; redraw();
    }
    $('vtClearBoxes').addEventListener('click', () => {
        if (!boxes.length) return;
        if (confirm('Xoá hết khung của ảnh này?')) { boxes = []; selected = -1; dirty = true; redraw(); }
    });

    // phim tat (chi khi tab dang hien, dang chinh anh va khong go trong o nhap)
    window.addEventListener('keydown', e => {
        if (!tabActive() || !cur) return;
        const tag = (e.target.tagName || '').toLowerCase();
        if (tag === 'input' || tag === 'select' || tag === 'textarea') return;
        if (e.key === 'Delete' || e.key === 'Backspace') { delSelected(); e.preventDefault(); }
        else if (CLS_BY_KEY[e.key]) {
            $('vtCls').value = CLS_BY_KEY[e.key];
            if (selected >= 0) { boxes[selected].cls = CLS_BY_KEY[e.key]; dirty = true; redraw(); }
        } else if (e.key === 'Enter') { saveCurrent(true); e.preventDefault(); }
        else if (e.key === 'Escape') { selected = -1; redraw(); }
    });

    $('vtSuggestAgain').addEventListener('click', async () => {
        if (!cur) return;
        if (boxes.length && !confirm('Thay các khung hiện có bằng khung gợi ý từ model?')) return;
        say('vtEditMsg', '⏳ Đang chạy model…');
        const r = await jfetch(`${API}/samples/${cur.id}/suggest`, { method: 'POST' });
        if (!r.ok) { say('vtEditMsg', esc(r.body.message || 'Không gợi ý được.'), true); return; }
        boxes = r.body.boxes || []; selected = -1; dirty = true;
        say('vtEditMsg', `Model gợi ý ${boxes.length} khung — hãy kiểm tra và sửa cho đúng.`);
        redraw();
    });

    // luu anh dang chinh; reviewed = true -> da duyet va sang anh chua duyet ke tiep
    async function saveCurrent(reviewed) {
        if (!cur) return;
        say('vtEditMsg', '⏳ Đang lưu…');
        const r = await jfetch(`${API}/samples/${cur.id}`, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ boxes, reviewed })
        });
        if (!r.ok) { say('vtEditMsg', esc(r.body.message || 'Không lưu được.'), true); return; }
        dirty = false;
        const idx = samples.findIndex(s => s.id === cur.id);
        if (idx >= 0) { samples[idx].boxes = r.body.boxes.length; samples[idx].reviewed = r.body.reviewed; }
        cur.reviewed = r.body.reviewed;
        loadInfo();
        if (reviewed) { nextSample(true); } else { say('vtEditMsg', '✔ Đã lưu (chưa duyệt).'); renderGrid(); }
    }
    $('vtSaveNext').addEventListener('click', () => saveCurrent(true));
    $('vtSave').addEventListener('click', () => saveCurrent(false));

    // anh ke tiep: uu tien anh CHUA duyet dung sau anh hien tai, neu het thi dong trinh chinh
    function nextSample(afterSave) {
        const list = visible();
        const i = list.findIndex(s => cur && s.id === cur.id);
        const after = list.slice(i + 1).find(s => !s.reviewed) || list.find(s => !s.reviewed && (!cur || s.id !== cur.id));
        if (after) { dirty = false; openSample(after.id); }
        else {
            dirty = false;
            if (afterSave) say('vtListMsg', '🎉 Đã duyệt hết ảnh trong danh sách.');
            closeEditor(true);
        }
    }
    $('vtSkip').addEventListener('click', () => nextSample(false));
    $('vtDelSample').addEventListener('click', async () => {
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
        preparing: 'Đang chuẩn bị dữ liệu…', 'evaluating-base': 'Đang đo model hiện tại…', training: 'Đang huấn luyện…',
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
        return `<table class="vt-metrics"><thead><tr><th>Trên tập kiểm tra</th><th>Model hiện tại</th><th>Model mới</th><th>Chênh (điểm %)</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    function renderStatus(s) {
        const running = !!s.running;
        const st = s.status || null;
        $('vtModelInfo').textContent = s.modelInfo || $('vtModelInfo').textContent;
        $('vtRestore').style.display = s.usingCustom ? '' : 'none';
        $('vtTrain').disabled = running; $('vtScratch').disabled = running; $('vtEpochs').disabled = running;
        $('vtCancel').style.display = running ? '' : 'none';
        $('vtInstall').style.display = s.canInstall ? '' : 'none';
        $('vtResultClear').style.display = (!running && (st || s.cancelled || s.install)) ? '' : 'none';
        if (!s.scriptFound) say('vtTrainMsg', '⚠️ Không tìm thấy tools/train_vehicle.py — đặt đường dẫn ở VehicleTrainer:Script trong appsettings.json.', true);

        const prog = $('vtProgress');
        let msg = '';
        if (running || (st && st.state && st.state !== 'done' && st.state !== 'error')) {
            const ep = st && st.epoch || 0, eps = st && st.epochs || 0;
            prog.style.display = st && st.state === 'training' && eps ? '' : 'none';
            $('vtProgressBar').style.width = eps ? `${Math.min(100, ep / eps * 100)}%` : '0%';
            msg = `⏳ ${esc((st && (st.message || STATE_TEXT[st.state])) || 'Đang khởi động Python…')}`;
        } else {
            prog.style.display = 'none';
            if (s.cancelled) msg = 'Đã huỷ huấn luyện.';
            else if (st && st.state === 'error') msg = `<span style="color:#ef4444;">❌ ${esc(st.message || 'Lỗi')}</span>`;
            else if (st && st.state === 'done') msg = '✔ Huấn luyện xong.';
        }
        if (st && st.images) msg += ` <span class="hint small">(${st.images.train} ảnh train · ${st.images.val} ảnh kiểm tra · ${st.images.boxes} khung${st.images.negatives ? ` · ${st.images.negatives} ảnh không có xe` : ''})</span>`;
        if (s.install) msg += `<br>${s.install.ok ? '✅' : '❌'} ${esc(s.install.message)}`;
        else if (st && st.state === 'done' && !running)
            msg += `<br><span style="color:#d97706;">Model mới chưa vượt model đang chạy nên chưa được cài tự động. Bấm “✔ Vẫn cài bản mới” nếu muốn dùng.</span>`;
        if (s.scriptFound) say('vtTrainMsg', msg);
        $('vtResult').innerHTML = (st ? metricsTable(st.metrics) : '') +
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

    $('vtTrain').addEventListener('click', async () => {
        const epochs = parseInt($('vtEpochs').value, 10) || 30;
        const r = await jfetch(`${API}/train/start`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ epochs, fromScratch: $('vtScratch').checked })
        });
        if (!r.ok) { say('vtTrainMsg', esc(r.body.message || 'Không bắt đầu được.'), true); return; }
        say('vtTrainMsg', '⏳ Đang khởi động…');
        fetchStatus();
    });
    $('vtCancel').addEventListener('click', async () => {
        if (!confirm('Huỷ huấn luyện đang chạy?')) return;
        await jfetch(`${API}/train/cancel`, { method: 'POST' });
        fetchStatus();
    });
    $('vtInstall').addEventListener('click', async () => {
        if (!confirm('Cài model mới dù chỉ số chưa vượt model đang chạy? Bản cũ được sao lưu.')) return;
        const r = await jfetch(`${API}/train/install`, { method: 'POST' });
        say('vtTrainMsg', esc(r.body.message || ''), !r.ok);
        fetchStatus(); loadInfo();
    });
    $('vtResultClear').addEventListener('click', async () => {
        if (!confirm('Xoá kết quả lần train gần nhất? Không ảnh hưởng model đang dùng và các ảnh huấn luyện.')) return;
        await jfetch(`${API}/train/clear`, { method: 'POST' });
        fetchStatus();
    });
    $('vtRestore').addEventListener('click', async () => {
        if (!confirm('Bỏ model đã train thêm và quay về model gốc? (Bản đã train được giữ lại trong thư mục data/vehicle-train/active)')) return;
        const r = await jfetch(`${API}/train/restore`, { method: 'POST' });
        say('vtTrainMsg', esc(r.body.message || ''), !r.ok);
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
