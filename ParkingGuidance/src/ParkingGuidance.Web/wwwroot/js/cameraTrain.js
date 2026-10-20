// ---------------------------------------------------------------------------
// Tab "Train": cong cu day lai nhan dien bien so.
//  • Hang cho anh: anh them tu tab Mo phong (nut ➕ Train ky tu / Train tat ca) hoac nap thang tu may.
//  • Ban lam viec: keo chuot cat vung bien (dom/chinh khung, zoom, dich anh), go ky tu dung, Enter de luu mau.
//    Khung + bien da doc tu Mo phong duoc dien san; 🤖 Tu do bien chay lai AI de goi y.
//  • 🧠 Train: day lai model doc ky tu tu cac mau (tien trinh rieng, uu tien thap) roi nap model moi ngay.
// Roi tab = giai phong het (khong goi may chu, khong giu anh/canvas, khong lang nghe ban phim) — xem release().
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/simulation';
    const ALPHABET = 'ABCDEFGHKLMNPRSTUVXYZ0123456789';
    const BAD_CHARS = /[IJOQW]/;
    const MIN_CROP_W = 20, MIN_CROP_H = 10;             // khung nho hon coi nhu bam nham
    const SUGGEST_MARGIN_X = 0.10, SUGGEST_MARGIN_Y = 0.15;   // giong cach he thong cat bien khi doc

    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const pct = x => (x == null ? '—' : (x * 100).toFixed(x >= 0.995 || x === 0 ? 0 : 1) + '%');
    const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
    const norm = s => String(s ?? '').toUpperCase().replace(/[^A-Z0-9]/g, '');
    const tabActive = () => !!window.pgTrainNav && window.pgTrainNav.isActive('char');   // tab lon Train dang mo VA tab con "Train ky tu" dang hien (xem trainHub.js)

    const canvas = $('trCanvas'), ctx = canvas.getContext('2d');
    let entered = false;

    let inbox = [];            // hang cho (server)
    let samples = [];          // mau da luu (server)
    let needFix = new Set();   // ma ngan (10 ky tu dau cua id mau) cua cac bien model moi van doc sai trong ket qua train gan nhat
    let hitTag = null;         // mau dang duoc tro toi tu bang ket qua train (to sang trong danh sach)
    let cur = null;            // anh dang lam: { item, img, objUrl, blob, regions, nextNo, sel, view, vehicles }
    let loadToken = 0;         // huy ket qua cu khi nguoi dung chuyen anh nhanh
    let detectAbort = null;
    let trainTimer = null;
    let trainRunning = false;
    let lastInstalledAt = null;
    let dragState = null;      // keo chuot dang dien ra
    let spaceDown = false;
    let cutMode = false;       // che do "✂️ Cat vung bien": moi lan keo chuot = ve khung moi (tu tat sau khi ve xong)
    let redrawQueued = false;
    let msgTimer = null;

    // ===================================================================== tien ich
    function say(html, ttl) {
        const m = $('trMsg');
        m.innerHTML = html || '';
        clearTimeout(msgTimer);
        if (html && ttl) msgTimer = setTimeout(() => { if ($('trMsg').innerHTML === html) $('trMsg').innerHTML = ''; }, ttl);
    }
    async function jfetch(url, opts) {
        const res = await fetch(url, opts);
        const body = await res.json().catch(() => ({}));
        return { ok: res.ok, status: res.status, body };
    }
    // 51D93240 -> 51D-932.40 ; 51D1-234.56 ; 51D-1234 (chi de hien thi)
    function fmtPlate(n) {
        let m;
        if ((m = /^(\d{2}[A-Z])(\d{3})(\d{2})$/.exec(n))) return `${m[1]}-${m[2]}.${m[3]}`;
        if ((m = /^(\d{2}[A-Z]\d)(\d{3})(\d{2})$/.exec(n))) return `${m[1]}-${m[2]}.${m[3]}`;
        if ((m = /^(\d{2}[A-Z])(\d{4})$/.exec(n))) return `${m[1]}-${m[2]}`;
        return n;
    }
    // { ok, msg }: 7-9 ky tu chu/so, khong dung I J O Q W
    function validate(text) {
        const n = norm(text);
        if (!n) return { ok: false, msg: 'Chưa nhập ký tự.' };
        const bad = [...new Set(n.split('').filter(c => BAD_CHARS.test(c)))];
        if (bad.length) return { ok: false, msg: `Biển VN không dùng ${bad.join(', ')} (hay nhầm O→0, I→1, Q→0, W→V).` };
        if (n.length < 7) return { ok: false, msg: `Mới ${n.length} ký tự — cần 7–9.` };
        if (n.length > 9) return { ok: false, msg: `${n.length} ký tự — tối đa 9.` };
        return { ok: true, msg: `${n.length} ký tự ✓` };
    }
    const quickFix = t => norm(t).replace(/[OQ]/g, '0').replace(/[IJ]/g, '1').replace(/W/g, 'V');

    // ===================================================================== tai du lieu
    async function loadInbox() {
        try {
            const d = await (await fetch(`${API}/inbox`)).json();
            inbox = d.items || [];
        } catch { /* bo qua */ }
        renderInbox();
    }
    async function loadSamples() {
        try {
            const d = await (await fetch(`${API}/corrections`)).json();
            samples = d.items || [];
        } catch { /* bo qua */ }
        renderSamples();
        updateTrainButtons();
    }
    async function loadAll() {
        await Promise.all([loadInbox(), loadSamples()]);
        updateCounts();
    }
    function updateCounts() {
        const wrong = samples.filter(s => s.changed).length;
        const pending = inbox.filter(i => !i.done).length;
        $('trCounts').textContent = `(hàng chờ: ${pending} ảnh chưa xử lý · mẫu: ${samples.length}${samples.length ? ` · ${wrong} mẫu đọc sai/không đọc được` : ''})`;
    }

    // ===================================================================== hang cho
    function filteredInbox() {
        const f = $('trFilter').value;
        return inbox.filter(i => f === 'all' ? true : f === 'done' ? i.done : !i.done);
    }
    function renderInbox() {
        const list = filteredInbox();
        $('trInboxCount').textContent = inbox.length ? `(${inbox.length} ảnh · ${inbox.filter(i => !i.done).length} chưa xử lý)` : '';
        const box = $('trInbox');
        if (!list.length) {
            box.innerHTML = `<div class="no-result">${inbox.length ? 'Không có ảnh nào ở bộ lọc này.' : 'Hàng chờ trống. Thêm ảnh từ tab 🧪 Mô phỏng (➕ Train ký tự) hoặc bấm 📂 Thêm ảnh từ máy.'}</div>`;
            updateNav();
            return;
        }
        box.innerHTML = list.map(i => `
            <div class="tr-card${cur && cur.item.id === i.id ? ' active' : ''}${i.done ? ' done' : ''}" data-id="${i.id}" title="${esc(i.imageName)}">
                <img loading="lazy" src="${API}/inbox/${i.id}/thumb" alt="">
                <div class="tr-card-name">${esc(i.imageName)}</div>
                <div class="tr-card-state">${i.done ? `✔ ${i.samples} mẫu` : (i.samples ? `✎ ${i.samples} mẫu (chưa xong)` : '⏳ chưa xử lý')}${i.source === 'upload' ? ' · 📂' : ''}</div>
                <button class="tr-card-del" title="Bỏ ảnh này khỏi hàng chờ (và xoá mẫu từ ảnh này)" data-del="${i.id}">✕</button>
            </div>`).join('');
        const active = box.querySelector('.tr-card.active');
        if (active) active.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        updateNav();
    }
    $('trInbox').addEventListener('click', e => {
        const del = e.target.closest('[data-del]');
        if (del) { e.stopPropagation(); dropImage(del.dataset.del); return; }
        const card = e.target.closest('.tr-card');
        if (card) openItem(card.dataset.id, true);
    });
    $('trFilter').addEventListener('change', renderInbox);
    $('trClearDoneBtn').addEventListener('click', async () => {
        const n = inbox.filter(i => i.done).length;
        if (!n) { say('Không có ảnh nào đã xong để gỡ.', 3000); return; }
        if (!confirm(`Gỡ ${n} ảnh đã xong khỏi hàng chờ? Các mẫu đã lưu vẫn được giữ để train.`)) return;
        await fetch(`${API}/inbox/clear-done`, { method: 'POST' });
        if (cur && cur.item.done) closeCurrent();
        await loadInbox(); updateCounts();
    });

    async function dropImage(id) {
        const it = inbox.find(i => i.id === id);
        if (!it) return;
        const msg = it.samples ? `Bỏ ảnh "${it.imageName}" và xoá ${it.samples} mẫu đã lưu từ ảnh này?` : `Bỏ ảnh "${it.imageName}" khỏi hàng chờ?`;
        if (!confirm(msg)) return;
        const wasCur = cur && cur.item.id === id;
        const next = wasCur ? neighbour(id, +1) || neighbour(id, -1) : null;
        await fetch(`${API}/inbox/${id}?withSamples=true`, { method: 'DELETE' });
        if (wasCur) closeCurrent();
        await loadAll();
        if (next) openItem(next.id);
    }
    $('trDropBtn').addEventListener('click', () => { if (cur) dropImage(cur.item.id); });

    // anh ke tiep / truoc trong danh sach dang loc (de duyet bang nut va phim mui ten)
    function neighbour(id, dir) {
        const list = filteredInbox();
        const i = list.findIndex(x => x.id === id);
        if (i < 0) return list[0] || null;
        return list[i + dir] || null;
    }
    // anh chua xu ly ke tiep sau anh `afterId` theo THU TU CU (order = danh sach id luc truoc khi tai lai hang cho); het thi lay anh chua xong dau tien
    function nextPending(afterId, order) {
        const pending = inbox.filter(i => !i.done && i.id !== afterId);
        if (!pending.length) return null;
        const ids = order || filteredInbox().map(x => x.id);
        const pos = ids.indexOf(afterId);
        if (pos >= 0) {
            for (let k = pos + 1; k < ids.length; k++) { const hit = pending.find(x => x.id === ids[k]); if (hit) return hit; }
        }
        return pending[0];
    }
    function updateNav() {
        const has = !!cur;
        const list = filteredInbox();
        const i = has ? list.findIndex(x => x.id === cur.item.id) : -1;
        $('trPrevBtn').disabled = !has || i <= 0;
        $('trNextBtn').disabled = !has || i < 0 || i >= list.length - 1;
        ['trDetectBtn', 'trCutBtn', 'trFitBtn', 'trZoomOutBtn', 'trZoomInBtn', 'trDoneBtn', 'trDropBtn'].forEach(id => { $(id).disabled = !has; });
        $('trImgInfo').textContent = has ? `— ${cur.item.imageName}${i >= 0 ? ` (${i + 1}/${list.length})` : ''}` : '';
    }
    $('trPrevBtn').addEventListener('click', () => { if (cur) { const n = neighbour(cur.item.id, -1); if (n) openItem(n.id); } });
    $('trNextBtn').addEventListener('click', () => { if (cur) { const n = neighbour(cur.item.id, +1); if (n) openItem(n.id); } });

    // nap anh tu may -> hang cho
    $('trUploadInput').addEventListener('change', async e => {
        const files = Array.from(e.target.files).filter(f => (f.type && f.type.startsWith('image/')) || /\.(jpe?g|png|bmp|webp)$/i.test(f.name));
        e.target.value = '';
        if (!files.length) { say('Không có ảnh hợp lệ (jpg, png, bmp, webp).', 4000); return; }
        let ok = 0, dup = 0, fail = 0, firstId = null;
        for (let k = 0; k < files.length; k++) {
            say(`⏳ Đang nạp ảnh ${k + 1}/${files.length}…`);
            const fd = new FormData();
            fd.append('image', files[k], files[k].name);
            fd.append('name', files[k].webkitRelativePath || files[k].name);
            fd.append('source', 'upload');
            try {
                const r = await jfetch(`${API}/inbox`, { method: 'POST', body: fd });
                if (!r.ok) { fail++; continue; }
                if (r.body.isNew) ok++; else dup++;
                if (!firstId) firstId = r.body.id;
            } catch { fail++; }
        }
        say(`📥 Đã thêm ${ok} ảnh${dup ? ` · ${dup} ảnh đã có sẵn` : ''}${fail ? ` · ${fail} ảnh lỗi` : ''}.`, 6000);
        await loadAll();
        if (firstId && !cur) openItem(firstId);
    });

    // ===================================================================== mo 1 anh
    function closeCurrent() {
        loadToken++;
        if (detectAbort) { detectAbort.abort(); detectAbort = null; }
        if (cur) {
            if (cur.img) { cur.img.onload = cur.img.onerror = null; cur.img.src = ''; }
            if (cur.objUrl) URL.revokeObjectURL(cur.objUrl);
        }
        cur = null;
        dragState = null;
        canvas.width = 0; canvas.height = 0;
        $('trEmpty').style.display = '';
        canvas.style.display = 'none';
        $('trRegions').innerHTML = '<div class="no-result">Chọn một ảnh ở hàng chờ bên dưới.</div>';
        $('trRegionCount').textContent = '';
        updateNav();
    }

    async function openItem(id, fromUser) {
        const item = inbox.find(i => i.id === id);
        if (!item) return;
        if (cur && cur.item.id === id) return;
        const my = ++loadToken;
        say('⏳ Đang mở ảnh…');
        let blob;
        try {
            const res = await fetch(`${API}/inbox/${id}/image`);
            if (!res.ok) throw new Error('HTTP ' + res.status);
            blob = await res.blob();
        } catch (err) { if (my === loadToken) say(`<span class="bad">Không mở được ảnh: ${esc(err.message)}</span>`); return; }
        if (my !== loadToken || !entered) return;
        const objUrl = URL.createObjectURL(blob);
        const img = new Image();
        try {
            await new Promise((res, rej) => { img.onload = res; img.onerror = () => rej(new Error('ảnh hỏng')); img.src = objUrl; });
        } catch (err) { URL.revokeObjectURL(objUrl); if (my === loadToken) say(`<span class="bad">Không mở được ảnh: ${esc(err.message)}</span>`); return; }
        if (my !== loadToken || !entered) { URL.revokeObjectURL(objUrl); return; }

        closeCurrent();
        loadToken = my;
        setCutMode(false);
        cur = { item, img, objUrl, blob, regions: [], nextNo: 1, sel: null, view: { s: 1, ox: 0, oy: 0 }, vehicles: [] };

        // 1) mau da luu cua anh nay (con khung trong meta) -> khoi phuc
        const prefix = item.id + '|r';
        samples.filter(s => s.key.startsWith(prefix)).forEach(s => {
            let rect = null;
            try { rect = JSON.parse(s.meta || 'null'); } catch { /* bo qua */ }
            const no = parseInt(s.key.slice(prefix.length), 10) || cur.nextNo;
            cur.nextNo = Math.max(cur.nextNo, no + 1);
            const hasRect = rect && rect.w > 0 && rect.h > 0;
            cur.regions.push({
                no, key: s.key, id: s.id, saved: true, dirty: false, suggested: false,
                x: hasRect ? rect.x : 0, y: hasRect ? rect.y : 0, w: hasRect ? rect.w : img.naturalWidth, h: hasRect ? rect.h : img.naturalHeight,
                noRect: !hasRect, text: fmtPlate(s.correct), recognized: s.recognized || '',
                chars: (rect && Array.isArray(rect.chars) && rect.chars.length) ? rect.chars : null
            });
        });
        // 2) chua co mau: goi y tu Mo phong (khung + bien da doc)
        let hint = null;
        try { hint = item.hint ? JSON.parse(item.hint) : null; } catch { /* bo qua */ }
        if (hint) {
            cur.vehicles = (hint.vehicles || []).filter(v => v && v.x2 > v.x1);
            if (!cur.regions.length) (hint.plates || []).forEach(p => addSuggested(p, p.text));
        }
        fitView();
        $('trEmpty').style.display = 'none';
        canvas.style.display = '';
        renderRegions();
        updateNav();
        renderInbox();
        paint();
        const sug = cur.regions.filter(r => r.suggested).length;
        if (sug) say(`💡 Đã điền sẵn ${sug} khung từ kết quả mô phỏng — kiểm tra khung và sửa ký tự nếu sai rồi Enter.`, 8000);
        else if (!cur.regions.length) say('Kéo chuột trên ảnh để cắt vùng biển số (hoặc bấm 🤖 Tự dò biển để AI gợi ý).', 8000);
        else say('', 0);
        if (fromUser) $('trWorkbench').scrollIntoView({ behavior: 'smooth', block: 'start' });   // chon anh o hang cho/mau -> dua ban lam viec len de lam ngay
        focusFirstUnsaved();
    }

    function addSuggested(p, text) {
        if (!cur) return null;
        const W = cur.img.naturalWidth, H = cur.img.naturalHeight;
        const bw = p.x2 - p.x1, bh = p.y2 - p.y1;
        if (!(bw > 4 && bh > 4)) return null;
        const x1 = clamp(p.x1 - bw * SUGGEST_MARGIN_X, 0, W), y1 = clamp(p.y1 - bh * SUGGEST_MARGIN_Y, 0, H);
        const x2 = clamp(p.x2 + bw * SUGGEST_MARGIN_X, 0, W), y2 = clamp(p.y2 + bh * SUGGEST_MARGIN_Y, 0, H);
        const rect = { x: Math.round(x1), y: Math.round(y1), w: Math.round(x2 - x1), h: Math.round(y2 - y1) };
        if (cur.regions.some(r => iou(r, rect) > 0.4)) return null;       // da co khung gan nhu trung
        const rec = norm(text);
        const r = { no: cur.nextNo++, key: `${cur.item.id}|r`, saved: false, dirty: true, suggested: true, text: rec ? fmtPlate(rec) : '', recognized: rec, ...rect };
        r.key = `${cur.item.id}|r${r.no}`;
        cur.regions.push(r);
        return r;
    }
    function iou(a, b) {
        const x1 = Math.max(a.x, b.x), y1 = Math.max(a.y, b.y), x2 = Math.min(a.x + a.w, b.x + b.w), y2 = Math.min(a.y + a.h, b.y + b.h);
        const inter = Math.max(0, x2 - x1) * Math.max(0, y2 - y1);
        const uni = a.w * a.h + b.w * b.h - inter;
        return uni > 0 ? inter / uni : 0;
    }

    // ===================================================================== tu do bien (AI)
    $('trDetectBtn').addEventListener('click', async () => {
        if (!cur) return;
        if (detectAbort) detectAbort.abort();
        detectAbort = new AbortController();
        const my = detectAbort, token = loadToken, c = cur;
        $('trDetectBtn').disabled = true;
        say('⏳ Đang dò biển… (nhường camera thật nếu hệ thống đang bận)');
        const fd = new FormData();
        fd.append('image', c.blob, c.item.imageName.split(/[\\/]/).pop() || 'anh.jpg');
        try {
            const res = await fetch(`${API}/analyze`, { method: 'POST', body: fd, signal: my.signal });
            const body = await res.json().catch(() => ({}));
            if (my.signal.aborted || token !== loadToken || cur !== c) return;
            if (!res.ok) { say(`<span class="bad">⚠️ ${esc(body.message || 'Lỗi ' + res.status)}</span>`); return; }
            const sx = body.frameWidth ? c.img.naturalWidth / body.frameWidth : 1, sy = body.frameHeight ? c.img.naturalHeight / body.frameHeight : 1;
            let added = 0, noPlate = 0;
            (body.vehicles || []).forEach(v => {
                if (v.plateBox) {
                    const r = addSuggested({ x1: v.plateBox.x1 * sx, y1: v.plateBox.y1 * sy, x2: v.plateBox.x2 * sx, y2: v.plateBox.y2 * sy }, v.plateNumber || '');
                    if (r) added++;
                } else if (!v.isRegion) {
                    noPlate++;
                    if (!c.vehicles.some(x => Math.abs(x.x1 - v.box.x1 * sx) < 4 && Math.abs(x.y1 - v.box.y1 * sy) < 4))
                        c.vehicles.push({ x1: v.box.x1 * sx, y1: v.box.y1 * sy, x2: v.box.x2 * sx, y2: v.box.y2 * sy, type: v.vehicleType });
                }
            });
            renderRegions(); paint();
            say(added ? `🤖 Gợi ý thêm ${added} khung biển${noPlate ? ` · ${noPlate} xe chưa dò được biển (khung xám nét đứt) — tự kéo khung biển cho xe đó` : ''}.`
                : (noPlate ? `🤖 AI thấy ${noPlate} xe nhưng chưa dò được biển (khung xám nét đứt) — hãy tự kéo khung quanh biển.`
                    : '🤖 AI không dò được xe/biển mới — hãy tự kéo khung quanh biển.'), 9000);
            if (added) focusFirstUnsaved();
        } catch (err) {
            if (err.name !== 'AbortError') say(`<span class="bad">Không gọi được máy chủ: ${esc(err.message)}</span>`);
        } finally {
            if (detectAbort === my) detectAbort = null;
            if (cur) $('trDetectBtn').disabled = false;
        }
    });

    // ===================================================================== danh sach khung (cot ben phai)
    function regionState(r) {
        const ch = r.chars ? ` · 🔤 ${r.chars.length} ô ký tự` : '';
        if (r.saved && !r.dirty) return '<span class="ok">💾 Đã lưu mẫu' + ch + '</span>' + (r.noRect ? ' <span class="hint small" style="margin:0;">(khung gốc không có — chỉnh khung rồi lưu lại nếu cần)</span>' : '');
        if (r.saved && r.dirty) return '<span class="warn">✎ Đã sửa — nhấn Enter để lưu lại' + ch + '</span>';
        if (r.suggested) return '<span class="warn">💡 Gợi ý — kiểm tra rồi Enter để lưu</span>';
        return '<span class="warn">✎ Chưa lưu — nhập ký tự rồi Enter' + ch + '</span>';
    }
    function chipsHtml(text) {
        const n = norm(text);
        if (!n) return '';
        return n.split('').map(c => `<span class="tr-chip${BAD_CHARS.test(c) ? ' bad' : ''}">${c}</span>`).join('');
    }
    function regionHtml(r) {
        const v = validate(r.text);
        const rec = r.recognized ? `Hệ thống đọc: <span class="plate-tag">${esc(r.recognized)}</span>` : 'Hệ thống: không đọc được';
        return `<div class="tr-region${cur && cur.sel === r.no ? ' sel' : ''}${r.saved && !r.dirty ? ' saved' : ''}" data-no="${r.no}">
            <div class="tr-region-top">
                <div class="tr-region-head"><strong>Biển #${r.no}</strong> <span class="hint small" style="margin:0;">${Math.round(r.w)}×${Math.round(r.h)} px</span></div>
                <div class="hint small tr-region-rec" style="margin:0;">${rec}</div>
            </div>
            <canvas class="tr-thumb" data-thumb="${r.no}" title="Bấm để phóng to khung này trên ảnh"></canvas>
            <div class="tr-region-fix">
                <input class="tr-input" type="text" maxlength="14" autocomplete="off" spellcheck="false" value="${esc(r.text)}" placeholder="Biển đúng, vd 51D-932.40" />
            </div>
            <div class="tr-chips">${chipsHtml(r.text)}</div>
            <div class="tr-valid ${v.ok ? 'ok' : 'bad'}">${esc(v.msg)}${/Biển VN không dùng/.test(v.msg) ? ' <button class="btn secondary btn-sm tr-fix-btn" type="button">Sửa nhanh</button>' : ''}</div>
            <div class="tr-region-state hint small">${regionState(r)}</div>
            <div class="tr-region-btns">
                <button class="btn primary btn-sm tr-save-btn" type="button" title="Lưu mẫu (Enter)">💾 Lưu</button>
                <button class="btn secondary btn-sm tr-chars-btn" type="button" title="Khoanh từng ký tự rồi gắn ký tự đúng cho từng ô — dùng khi hệ thống không nhận diện/tách được một số ký tự">🔤 Gắn từng ký tự</button>
                <button class="btn secondary btn-sm tr-zoom-btn" type="button" title="Phóng to khung này trên ảnh">🔍 Xem</button>
                <button class="btn secondary btn-sm tr-del-btn" type="button" title="Xoá khung (Del)">✕ Xoá</button>
            </div>
        </div>`;
    }
    function renderRegions() {
        const box = $('trRegions');
        if (!cur) return;
        $('trRegionCount').textContent = cur.regions.length ? `(${cur.regions.length})` : '';
        if (!cur.regions.length) {
            box.innerHTML = '<div class="no-result">Chưa có khung nào. Kéo chuột trên ảnh để cắt vùng biển số.</div>';
            return;
        }
        // giu o dang go: neu dang focus 1 o thi nho vi tri con tro de dung lai sau khi ve lai
        const ae = document.activeElement;
        const keep = ae && ae.classList && ae.classList.contains('tr-input') ? { no: +ae.closest('.tr-region').dataset.no, pos: ae.selectionStart } : null;
        box.innerHTML = cur.regions.map(regionHtml).join('');
        cur.regions.forEach(drawThumb);
        if (keep) {
            const inp = box.querySelector(`.tr-region[data-no="${keep.no}"] .tr-input`);
            if (inp) { inp.focus(); try { inp.setSelectionRange(keep.pos, keep.pos); } catch { /* bo qua */ } }
        }
    }
    function refreshRegionCard(r) {                      // ve lai 1 the nhung KHONG dung vao o nhap (de khong mat con tro)
        const card = $('trRegions').querySelector(`.tr-region[data-no="${r.no}"]`);
        if (!card) { renderRegions(); return; }
        const v = validate(r.text);
        card.classList.toggle('saved', r.saved && !r.dirty);
        card.querySelector('.tr-chips').innerHTML = chipsHtml(r.text);
        const val = card.querySelector('.tr-valid');
        val.className = 'tr-valid ' + (v.ok ? 'ok' : 'bad');
        val.innerHTML = esc(v.msg) + (/Biển VN không dùng/.test(v.msg) ? ' <button class="btn secondary btn-sm tr-fix-btn" type="button">Sửa nhanh</button>' : '');
        card.querySelector('.tr-region-state').innerHTML = regionState(r);
        card.querySelector('.tr-region-head').innerHTML = `<strong>Biển #${r.no}</strong> <span class="hint small" style="margin:0;">${Math.round(r.w)}×${Math.round(r.h)} px</span>`;
    }
    function setSelected(no) {
        if (!cur || cur.sel === no) return;
        cur.sel = no;
        $('trRegions').querySelectorAll('.tr-region').forEach(c => c.classList.toggle('sel', +c.dataset.no === no));
        paint();
    }
    function focusFirstUnsaved() {
        if (!cur) return;
        const r = cur.regions.find(x => !x.saved || x.dirty);
        if (!r) return;
        setSelected(r.no);
        const inp = $('trRegions').querySelector(`.tr-region[data-no="${r.no}"] .tr-input`);
        if (inp) { inp.focus(); inp.select(); }
    }
    const regionOf = el => { const c = el.closest('.tr-region'); return c && cur ? cur.regions.find(r => r.no === +c.dataset.no) : null; };

    $('trRegions').addEventListener('input', e => {
        const inp = e.target.closest('.tr-input'); if (!inp) return;
        const r = regionOf(inp); if (!r) return;
        const pos = inp.selectionStart;
        inp.value = inp.value.toUpperCase();
        try { inp.setSelectionRange(pos, pos); } catch { /* bo qua */ }
        r.text = inp.value;
        r.suggested = false;
        r.chars = null;                                      // go lai chuoi -> cac o ky tu da khoanh khong con khop
        if (r.saved) r.dirty = true;
        refreshRegionCard(r);
    });
    $('trRegions').addEventListener('keydown', e => {
        const inp = e.target.closest('.tr-input'); if (!inp) return;
        const r = regionOf(inp); if (!r) return;
        if (e.key === 'Enter') { e.preventDefault(); saveRegion(r, true); }
        else if (e.key === 'Escape') { inp.blur(); }
    });
    $('trRegions').addEventListener('focusin', e => {
        const r = regionOf(e.target); if (r) setSelected(r.no);
    });
    $('trRegions').addEventListener('click', e => {
        const r = regionOf(e.target); if (!r) return;
        setSelected(r.no);
        if (e.target.closest('.tr-save-btn')) saveRegion(r, true);
        else if (e.target.closest('.tr-del-btn')) deleteRegion(r);
        else if (e.target.closest('.tr-chars-btn')) openCharEditor(r);
        else if (e.target.closest('.tr-zoom-btn')) zoomToRegion(r);
        else if (e.target.closest('.tr-fix-btn')) {
            r.text = fmtPlate(quickFix(r.text)); r.suggested = false; r.chars = null; if (r.saved) r.dirty = true;
            const inp = e.target.closest('.tr-region').querySelector('.tr-input'); inp.value = r.text;
            refreshRegionCard(r);
        } else if (e.target.closest('.tr-thumb')) zoomToRegion(r);
    });

    // anh thu nho cua vung cat (ve tu anh goc, ap dung do sang/tuong phan de de nhin)
    function drawThumb(r) {
        const c = $('trRegions').querySelector(`canvas[data-thumb="${r.no}"]`);
        if (!c || !cur) return;
        const maxW = 560, maxH = 220;                        // anh biển to, dễ đối chiếu từng ký tự
        const k = Math.min(maxW / Math.max(1, r.w), maxH / Math.max(1, r.h), 6);
        c.width = Math.max(1, Math.round(r.w * k)); c.height = Math.max(1, Math.round(r.h * k));
        const g = c.getContext('2d');
        g.filter = viewFilter();
        g.drawImage(cur.img, r.x, r.y, r.w, r.h, 0, 0, c.width, c.height);
    }

    // ===================================================================== luu / xoa mau
    async function cropBlob(r) {
        const W = cur.img.naturalWidth, H = cur.img.naturalHeight;
        const x = clamp(Math.round(r.x), 0, W - 1), y = clamp(Math.round(r.y), 0, H - 1);
        const w = clamp(Math.round(r.w), 1, W - x), h = clamp(Math.round(r.h), 1, H - y);
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        c.getContext('2d').drawImage(cur.img, x, y, w, h, 0, 0, w, h);      // cat tren anh GOC: khong mat do phan giai, khong ap loc sang/tuong phan
        const blob = await new Promise(res => c.toBlob(res, 'image/png'));
        c.width = 0; c.height = 0;
        return { blob, rect: { x, y, w, h } };
    }

    let saving = 0;
    async function saveRegion(r, advance) {
        if (!cur) return false;
        const c = cur;
        const n = norm(r.text);
        const v = validate(r.text);
        const card = () => $('trRegions').querySelector(`.tr-region[data-no="${r.no}"] .tr-region-state`);
        if (!v.ok) { if (card()) card().innerHTML = `<span class="bad">${esc(v.msg)}</span>`; const inp = $('trRegions').querySelector(`.tr-region[data-no="${r.no}"] .tr-input`); if (inp) inp.focus(); return false; }
        if (r.w < MIN_CROP_W || r.h < MIN_CROP_H) { if (card()) card().innerHTML = '<span class="bad">Khung quá nhỏ — hãy kéo khung bao trọn biển số.</span>'; return false; }
        const { blob, rect } = await cropBlob(r);
        if (!blob) { if (card()) card().innerHTML = '<span class="bad">Không cắt được ảnh biển.</span>'; return false; }
        const fd = new FormData();
        fd.append('key', r.key);
        fd.append('imageName', c.item.imageName);
        fd.append('kind', 'manual');
        fd.append('correct', n);
        fd.append('recognized', r.recognized || '');
        fd.append('meta', JSON.stringify(r.chars ? { ...rect, chars: r.chars } : rect));
        fd.append('image', blob, 'plate.png');
        if (card()) card().innerHTML = '⏳ Đang lưu…';
        saving++;
        try {
            const res = await jfetch(`${API}/corrections`, { method: 'PUT', body: fd });
            if (cur !== c) return false;                                  // da chuyen anh trong luc luu: mau van duoc luu tren may chu
            if (!res.ok) { if (card()) card().innerHTML = `<span class="bad">${esc(res.body.message || 'Lỗi ' + res.status)}</span>`; return false; }
            r.saved = true; r.dirty = false; r.suggested = false; r.noRect = false; r.id = res.body.id;
            r.text = fmtPlate(n);
            const inp = $('trRegions').querySelector(`.tr-region[data-no="${r.no}"] .tr-input`); if (inp) inp.value = r.text;
            refreshRegionCard(r);
            paint();
            await afterChange(advance);
            return true;
        } catch (err) {
            if (card()) card().innerHTML = `<span class="bad">Không lưu được: ${esc(err.message)}</span>`;
            return false;
        } finally { saving--; }
    }

    async function deleteRegion(r) {
        if (!cur) return;
        const c = cur;
        if (r.saved) {
            try { await fetch(`${API}/corrections?key=${encodeURIComponent(r.key)}`, { method: 'DELETE' }); }
            catch (err) { say(`<span class="bad">Không xoá được mẫu: ${esc(err.message)}</span>`); return; }
        }
        if (cur !== c) return;
        c.regions = c.regions.filter(x => x !== r);
        if (c.sel === r.no) c.sel = null;
        renderRegions(); paint();
        await afterChange(false);
    }

    // sau khi luu/xoa: cap nhat mau, trang thai "xong" cua anh, va (neu bat) tu sang anh ke
    async function afterChange(advance) {
        if (!cur) return;
        const c = cur;
        const allSaved = c.regions.length > 0 && c.regions.every(x => x.saved && !x.dirty);
        const shouldBeDone = allSaved;
        const order = filteredInbox().map(x => x.id);        // thu tu hien tai (de sang dung anh ke sau khi tai lai)
        if (c.item.done !== shouldBeDone) {
            await fetch(`${API}/inbox/${c.item.id}/done?done=${shouldBeDone}`, { method: 'POST' });
        }
        await loadAll();                                   // lam moi hang cho (so mau, trang thai) + danh sach mau
        if (cur !== c) return;
        c.item = inbox.find(i => i.id === c.item.id) || c.item;
        updateNav();
        if (advance && allSaved && $('trAutoNext').checked) {
            const nxt = nextPending(c.item.id, order);
            if (nxt) { say(`✔ Đã lưu — sang ảnh kế: ${esc(nxt.imageName)}`, 3000); setTimeout(() => { if (cur === c && entered) openItem(nxt.id); }, 250); }
            else say('✔ Đã lưu. Hàng chờ không còn ảnh chưa xử lý — có thể bấm 🧠 Train.', 8000);
        } else if (advance) {
            focusFirstUnsaved();
        }
    }

    $('trDoneBtn').addEventListener('click', async () => {
        if (!cur) return;
        const c = cur;
        const pend = c.regions.filter(x => !x.saved || x.dirty);
        if (pend.length) {
            // khung chua luu: thu luu nhung khung hop le; khung trong thi bo qua
            for (const r of pend) {
                if (!norm(r.text)) continue;
                const ok = await saveRegion(r, false);
                if (!ok) { say('<span class="bad">Còn khung chưa hợp lệ — sửa ký tự hoặc xoá khung đó rồi bấm lại.</span>'); return; }
            }
            if (cur !== c) return;
            const empty = c.regions.filter(x => !x.saved);
            for (const r of empty) { c.regions = c.regions.filter(x => x !== r); }
            renderRegions(); paint();
        }
        const order = filteredInbox().map(x => x.id);
        await fetch(`${API}/inbox/${c.item.id}/done?done=true`, { method: 'POST' });
        await loadAll();
        if (cur !== c) return;
        c.item = inbox.find(i => i.id === c.item.id) || c.item;
        const nxt = nextPending(c.item.id, order);
        if (nxt) openItem(nxt.id); else say('✔ Hàng chờ không còn ảnh chưa xử lý.', 6000);
    });

    // ===================================================================== ban lam viec: ve + chuot
    const dpr = () => window.devicePixelRatio || 1;
    function stageSize() {
        const st = $('trStage');
        return { w: Math.max(200, st.clientWidth), h: Math.max(240, st.clientHeight) };
    }
    function sizeCanvas() {
        const { w, h } = stageSize(), d = dpr();
        const cw = Math.round(w * d), ch = Math.round(h * d);
        if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; }
        canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    }
    function fitView() {
        if (!cur) return;
        const { w, h } = stageSize();
        const iw = cur.img.naturalWidth, ih = cur.img.naturalHeight;
        const s = Math.min(w / iw, h / ih) * 0.98;
        cur.view = { s, ox: (w - iw * s) / 2, oy: (h - ih * s) / 2, fit: s };
    }
    function zoomAt(px, py, factor) {
        if (!cur) return;
        const v = cur.view, fit = v.fit || 0.05;
        const ns = clamp(v.s * factor, fit * 0.5, 24);
        const ix = (px - v.ox) / v.s, iy = (py - v.oy) / v.s;
        v.s = ns; v.ox = px - ix * ns; v.oy = py - iy * ns;
        schedulePaint();
    }
    function zoomToRegion(r) {
        if (!cur) return;
        const { w, h } = stageSize();
        const s = clamp(Math.min(w * 0.8 / Math.max(1, r.w), h * 0.8 / Math.max(1, r.h)), (cur.view.fit || 0.05) * 0.5, 24);
        cur.view.s = s;
        cur.view.ox = w / 2 - (r.x + r.w / 2) * s;
        cur.view.oy = h / 2 - (r.y + r.h / 2) * s;
        paint();
    }
    const toImg = (px, py) => ({ x: (px - cur.view.ox) / cur.view.s, y: (py - cur.view.oy) / cur.view.s });
    // Vi tri chuot theo pixel CSS cua canvas (he toa do ma paint() dung). KHONG duoc dung thang clientX - rect.left: getBoundingClientRect/clientX tinh theo pixel
    // MAN HINH, con canvas ve theo pixel CSS (canvas.style.width). Khi trang bi phong/thu — "Kich thuoc giao dien" (root.style.zoom trong prefs.js) khac 100%,
    // zoom trinh duyet... — hai he nay lech nhau dung bang ty le do: khung ve sai cho, khong keo/chinh duoc khung cu (tay cam lech khoi vi tri that).
    function evPt(e) {
        const b = canvas.getBoundingClientRect();
        const cw = parseFloat(canvas.style.width) || b.width, ch = parseFloat(canvas.style.height) || b.height;
        return { x: (e.clientX - b.left) * (cw / (b.width || cw)), y: (e.clientY - b.top) * (ch / (b.height || ch)) };
    }
    function viewFilter() {
        const b = +$('trBright').value, c = +$('trContrast').value;
        return (b === 100 && c === 100) ? 'none' : `brightness(${b}%) contrast(${c}%)`;
    }
    function schedulePaint() {
        if (redrawQueued) return;
        redrawQueued = true;
        requestAnimationFrame(() => { redrawQueued = false; paint(); });
    }

    const HANDLES = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];
    function handlePos(r, hname) {
        const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
        return { nw: [r.x, r.y], n: [cx, r.y], ne: [r.x + r.w, r.y], e: [r.x + r.w, cy], se: [r.x + r.w, r.y + r.h], s: [cx, r.y + r.h], sw: [r.x, r.y + r.h], w: [r.x, cy] }[hname];
    }
    function hitHandle(r, px, py) {                      // px,py: toa do man hinh
        const v = cur.view, R = 8;
        for (const hname of HANDLES) {
            const [hx, hy] = handlePos(r, hname);
            if (Math.abs(v.ox + hx * v.s - px) <= R && Math.abs(v.oy + hy * v.s - py) <= R) return hname;
        }
        return null;
    }
    const inside = (r, ix, iy) => ix >= r.x && ix <= r.x + r.w && iy >= r.y && iy <= r.y + r.h;
    const CURSORS = { nw: 'nwse-resize', se: 'nwse-resize', ne: 'nesw-resize', sw: 'nesw-resize', n: 'ns-resize', s: 'ns-resize', e: 'ew-resize', w: 'ew-resize' };

    function paint() {
        if (!cur) return;
        sizeCanvas();                                        // PHAI chay truoc: closeCurrent() dat canvas.width = 0, neu thoat som thi anh khong bao gio duoc ve
        if (!canvas.width) return;
        const d = dpr(), v = cur.view;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = '#05080f'; ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.setTransform(d, 0, 0, d, 0, 0);
        ctx.imageSmoothingEnabled = v.s < 3;                 // phong to nhieu thi hien pixel ro nhu 1 cong cu cat anh
        ctx.filter = viewFilter();
        ctx.drawImage(cur.img, v.ox, v.oy, cur.img.naturalWidth * v.s, cur.img.naturalHeight * v.s);
        ctx.filter = 'none';

        // xe AI thay nhung chua do duoc bien: khung xam net dut de biet can zoom vao dau
        ctx.save();
        ctx.setLineDash([6, 4]); ctx.lineWidth = 1.5; ctx.strokeStyle = 'rgba(203,213,225,.75)';
        cur.vehicles.forEach(x => ctx.strokeRect(v.ox + x.x1 * v.s, v.oy + x.y1 * v.s, (x.x2 - x.x1) * v.s, (x.y2 - x.y1) * v.s));
        ctx.restore();

        cur.regions.forEach(r => {
            const sel = cur.sel === r.no, ok = r.saved && !r.dirty;
            const x = v.ox + r.x * v.s, y = v.oy + r.y * v.s, w = r.w * v.s, h = r.h * v.s;
            const col = sel ? '#38bdf8' : ok ? '#22c55e' : '#facc15';
            ctx.save();
            ctx.lineWidth = sel ? 2.5 : 2; ctx.strokeStyle = col;
            if (!ok) ctx.setLineDash([8, 5]);
            ctx.strokeRect(x, y, w, h);
            ctx.restore();
            const label = `#${r.no}${r.text ? ' ' + norm(r.text) : ''}`;
            ctx.font = 'bold 12px sans-serif';
            const tw = ctx.measureText(label).width + 10;
            const ly = y - 18 >= 0 ? y - 18 : y;
            ctx.fillStyle = sel ? '#0369a1' : ok ? '#16a34a' : '#a16207';
            ctx.fillRect(x, ly, tw, 18);
            ctx.fillStyle = '#fff'; ctx.textBaseline = 'middle'; ctx.fillText(label, x + 5, ly + 9); ctx.textBaseline = 'alphabetic';
            if (sel) {
                ctx.fillStyle = '#fff'; ctx.strokeStyle = '#0369a1'; ctx.lineWidth = 1.5;
                HANDLES.forEach(hn => {
                    const [hx, hy] = handlePos(r, hn);
                    ctx.beginPath(); ctx.rect(v.ox + hx * v.s - 4, v.oy + hy * v.s - 4, 8, 8); ctx.fill(); ctx.stroke();
                });
            }
        });
        if (dragState && dragState.type === 'draw') {
            const a = dragState.rect;
            ctx.save();
            ctx.setLineDash([8, 5]); ctx.lineWidth = 2; ctx.strokeStyle = '#facc15';
            ctx.fillStyle = 'rgba(250,204,21,.12)';
            ctx.fillRect(v.ox + a.x * v.s, v.oy + a.y * v.s, a.w * v.s, a.h * v.s);
            ctx.strokeRect(v.ox + a.x * v.s, v.oy + a.y * v.s, a.w * v.s, a.h * v.s);
            ctx.restore();
        }
    }

    canvas.addEventListener('wheel', e => {
        if (!cur) return;
        e.preventDefault();
        const p = evPt(e);
        zoomAt(p.x, p.y, e.deltaY < 0 ? 1.18 : 1 / 1.18);
    }, { passive: false });
    canvas.addEventListener('contextmenu', e => e.preventDefault());

    canvas.addEventListener('mousedown', e => {
        if (!cur) return;
        const p = evPt(e), im = toImg(p.x, p.y);
        // dich anh: chuot giua / phai / giu Space
        if (e.button === 1 || e.button === 2 || (e.button === 0 && spaceDown)) {
            dragState = { type: 'pan', sx: p.x, sy: p.y, ox: cur.view.ox, oy: cur.view.oy };
            canvas.style.cursor = 'grabbing';
        } else if (e.button === 0 && cutMode) {
            dragState = { type: 'draw', x0: clamp(im.x, 0, cur.img.naturalWidth), y0: clamp(im.y, 0, cur.img.naturalHeight), rect: { x: im.x, y: im.y, w: 0, h: 0 } };
        } else if (e.button === 0) {
            const sel = cur.regions.find(r => r.no === cur.sel);
            const hn = sel ? hitHandle(sel, p.x, p.y) : null;
            if (sel && hn) {
                dragState = { type: 'resize', handle: hn, r: sel, start: { x: sel.x, y: sel.y, w: sel.w, h: sel.h } };
            } else {
                // chon khung duoc nhan (uu tien khung nho nam trong khung to)
                const hit = cur.regions.filter(r => inside(r, im.x, im.y)).sort((a, b) => a.w * a.h - b.w * b.h)[0];
                if (hit) {
                    setSelected(hit.no);
                    dragState = { type: 'move', r: hit, dx: im.x - hit.x, dy: im.y - hit.y, moved: false };
                } else {
                    dragState = { type: 'draw', x0: clamp(im.x, 0, cur.img.naturalWidth), y0: clamp(im.y, 0, cur.img.naturalHeight), rect: { x: im.x, y: im.y, w: 0, h: 0 } };
                }
            }
        } else return;
        window.addEventListener('mousemove', onDragMove);   // chi gan trong luc keo, go ra ngay khi tha chuot
        window.addEventListener('mouseup', onDragEnd);
        e.preventDefault();
    });

    function onDragMove(e) {
        if (!dragState || !cur) return;
        const p = evPt(e), im = toImg(p.x, p.y), W = cur.img.naturalWidth, H = cur.img.naturalHeight;
        const d = dragState;
        if (d.type === 'pan') {
            cur.view.ox = d.ox + (p.x - d.sx); cur.view.oy = d.oy + (p.y - d.sy);
        } else if (d.type === 'draw') {
            const x1 = clamp(im.x, 0, W), y1 = clamp(im.y, 0, H);
            d.rect = { x: Math.min(d.x0, x1), y: Math.min(d.y0, y1), w: Math.abs(x1 - d.x0), h: Math.abs(y1 - d.y0) };
        } else if (d.type === 'move') {
            const r = d.r;
            r.x = clamp(im.x - d.dx, 0, W - r.w); r.y = clamp(im.y - d.dy, 0, H - r.h);
            d.moved = true;
            regionChanged(r, true);
        } else if (d.type === 'resize') {
            const r = d.r, s0 = d.start;
            let x1 = s0.x, y1 = s0.y, x2 = s0.x + s0.w, y2 = s0.y + s0.h;
            const ix = clamp(im.x, 0, W), iy = clamp(im.y, 0, H);
            if (d.handle.includes('w')) x1 = Math.min(ix, x2 - 4);
            if (d.handle.includes('e')) x2 = Math.max(ix, x1 + 4);
            if (d.handle.includes('n')) y1 = Math.min(iy, y2 - 4);
            if (d.handle.includes('s')) y2 = Math.max(iy, y1 + 4);
            r.x = x1; r.y = y1; r.w = x2 - x1; r.h = y2 - y1;
            d.moved = true;
            regionChanged(r, true);
        }
        schedulePaint();
    }

    function onDragEnd() {
        window.removeEventListener('mousemove', onDragMove);
        window.removeEventListener('mouseup', onDragEnd);
        const d = dragState; dragState = null;
        canvas.style.cursor = spaceDown ? 'grab' : (cutMode ? 'crosshair' : '');
        if (!d || !cur) return;
        if (d.type === 'draw') {
            const a = d.rect;
            if (cutMode && a.w >= MIN_CROP_W && a.h >= MIN_CROP_H) setCutMode(false);      // ve duoc khung -> tu tat che do cat
            if (a.w * cur.view.s >= 8 && a.h * cur.view.s >= 6 && a.w >= MIN_CROP_W && a.h >= MIN_CROP_H) {
                const r = { no: cur.nextNo++, key: '', saved: false, dirty: true, suggested: false, text: '', recognized: '', x: Math.round(a.x), y: Math.round(a.y), w: Math.round(a.w), h: Math.round(a.h) };
                r.key = `${cur.item.id}|r${r.no}`;
                cur.regions.push(r);
                cur.sel = r.no;
                renderRegions();
                const inp = $('trRegions').querySelector(`.tr-region[data-no="${r.no}"] .tr-input`);
                if (inp) { inp.focus(); inp.scrollIntoView({ block: 'nearest' }); }
                say('✎ Gõ ký tự đúng của biển rồi nhấn Enter để lưu mẫu.', 6000);
            } else if (a.w > 0 || a.h > 0) {
                say('Khung quá nhỏ — hãy kéo khung bao trọn biển số (có thể lăn chuột để zoom vào biển nhỏ).', 5000);
            } else {
                cur.sel = null; $('trRegions').querySelectorAll('.tr-region.sel').forEach(c => c.classList.remove('sel'));   // bam vao cho trong = bo chon
            }
            paint();
        } else if ((d.type === 'move' || d.type === 'resize') && d.moved) {
            regionChanged(d.r, false);
        }
    }

    // khung doi vi tri/kich thuoc: danh dau "da sua" neu da luu, cap nhat anh thu nho
    function regionChanged(r, live) {
        if (r.saved) r.dirty = true;
        r.suggested = false;
        r.chars = null;                                      // khung doi -> cac o ky tu (toa do theo khung) khong con dung
        r.x = Math.round(r.x); r.y = Math.round(r.y); r.w = Math.round(r.w); r.h = Math.round(r.h);
        drawThumb(r);
        refreshRegionCard(r);
        if (!live) paint();
    }

    canvas.addEventListener('mousemove', e => {                          // doi con tro theo vung dang tro
        if (!cur || dragState) return;
        const p = evPt(e), im = toImg(p.x, p.y);
        if (spaceDown) { canvas.style.cursor = 'grab'; return; }
        if (cutMode) { canvas.style.cursor = 'crosshair'; return; }
        const sel = cur.regions.find(r => r.no === cur.sel);
        const hn = sel ? hitHandle(sel, p.x, p.y) : null;
        canvas.style.cursor = hn ? CURSORS[hn] : (cur.regions.some(r => inside(r, im.x, im.y)) ? 'move' : 'crosshair');
    });
    canvas.addEventListener('dblclick', e => {                           // nhay dup vao khung: phong to de kiem tra ky tu
        if (!cur) return;
        const im = toImg(evPt(e).x, evPt(e).y);
        const hit = cur.regions.find(r => inside(r, im.x, im.y));
        if (hit) zoomToRegion(hit);
    });

    function setCutMode(on) {
        cutMode = !!on && !!cur;
        const b = $('trCutBtn');
        b.classList.toggle('primary', cutMode); b.classList.toggle('secondary', !cutMode);
        b.textContent = cutMode ? '✂️ Đang cắt… (kéo chuột trên ảnh)' : '✂️ Cắt vùng biển';
        canvas.style.cursor = cutMode ? 'crosshair' : '';
        if (cutMode) say('✂️ Kéo chuột trên ảnh để cắt vùng biển số (lăn chuột để zoom vào biển nhỏ). <kbd>Esc</kbd> để thoát chế độ cắt.', 0);
    }
    $('trCutBtn').addEventListener('click', () => { if (cur) setCutMode(!cutMode); });
    $('trFitBtn').addEventListener('click', () => { fitView(); paint(); });
    $('trZoomInBtn').addEventListener('click', () => { const { w, h } = stageSize(); zoomAt(w / 2, h / 2, 1.4); });
    $('trZoomOutBtn').addEventListener('click', () => { const { w, h } = stageSize(); zoomAt(w / 2, h / 2, 1 / 1.4); });
    ['trBright', 'trContrast'].forEach(id => $(id).addEventListener('input', () => { schedulePaint(); if (cur) cur.regions.forEach(drawThumb); }));
    $('trBright').addEventListener('dblclick', () => { $('trBright').value = 100; schedulePaint(); if (cur) cur.regions.forEach(drawThumb); });
    $('trContrast').addEventListener('dblclick', () => { $('trContrast').value = 100; schedulePaint(); if (cur) cur.regions.forEach(drawThumb); });

    window.addEventListener('resize', () => { if (entered && cur) { const keepFit = cur.view.s <= (cur.view.fit || 0) * 1.001; sizeCanvas(); if (keepFit) fitView(); paint(); } });

    // ---- phim tat: chi gan khi dang o tab Train
    function onKeyDown(e) {
        if (!tabActive() || ced) return;                       // dang mo trinh gan ky tu: no tu xu ly phim
        const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || '') && document.activeElement.type !== 'range' && document.activeElement.type !== 'checkbox';
        if (e.key === ' ' && !typing) { spaceDown = true; if (!dragState) canvas.style.cursor = 'grab'; if (cur) e.preventDefault(); return; }
        if (typing) return;
        if ((e.key === 'c' || e.key === 'C') && cur && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); setCutMode(!cutMode); return; }
        if (e.key === 'Escape' && cutMode) { setCutMode(false); say('', 0); return; }
        if (e.key === 'ArrowRight') { const n = cur && neighbour(cur.item.id, +1); if (n) { e.preventDefault(); openItem(n.id); } }
        else if (e.key === 'ArrowLeft') { const n = cur && neighbour(cur.item.id, -1); if (n) { e.preventDefault(); openItem(n.id); } }
        else if ((e.key === 'f' || e.key === 'F') && cur) { e.preventDefault(); fitView(); paint(); }
        else if (e.key === 'Delete' && cur && cur.sel != null) {
            const r = cur.regions.find(x => x.no === cur.sel);
            if (r) { e.preventDefault(); deleteRegion(r); }
        } else if (e.key === 'Escape' && cur && cur.sel != null) { cur.sel = null; renderRegions(); paint(); }
    }
    function onKeyUp(e) { if (e.key === ' ') { spaceDown = false; if (!dragState) canvas.style.cursor = ''; } }


    // ===================================================================== 🔤 Gan tung ky tu (khoanh tay tung ky tu tren anh bien)
    // Dung khi he thong tu tach sai/thieu ky tu (mau bi bo qua khi train). Moi o = 1 ky tu; trinh train hoc THANG tu cac o nay.
    // O luu theo pixel cua anh bien da cat ({x,y,w,h,c}); thu tu doc = dong tren->duoi, trai->phai.
    let ced = null;            // { r, boxes, sel, k, drag, cw, ch }
    const cc = $('trCharCanvas'), cctx = cc.getContext('2d');
    const KEYPAD = ALPHABET.split('').sort((a, b) => (/\d/.test(a) === /\d/.test(b) ? a.localeCompare(b) : (/\d/.test(a) ? -1 : 1)));

    function readingOrder(boxes) {
        if (!boxes.length) return [];
        const hs = boxes.map(b => b.h).sort((a, b) => a - b), mh = hs[hs.length >> 1] || 1;
        const idx = boxes.map((_, i) => i).sort((a, b) => (boxes[a].y + boxes[a].h / 2) - (boxes[b].y + boxes[b].h / 2));
        const rows = [];
        idx.forEach(i => {
            const cy = boxes[i].y + boxes[i].h / 2;
            const row = rows.find(rw => Math.abs(rw.cy - cy) < 0.6 * mh);
            if (row) { row.items.push(i); row.cy = (row.cy * (row.items.length - 1) + cy) / row.items.length; }
            else rows.push({ cy, items: [i] });
        });
        rows.sort((a, b) => a.cy - b.cy);
        return rows.flatMap(rw => rw.items.sort((a, b) => boxes[a].x - boxes[b].x));
    }
    function boxesText() {
        return readingOrder(ced.boxes).map(i => ced.boxes[i].c || '?').join('');
    }
    // chia deu o theo chuoi: bien 1 dong (ti le rong/cao >= 2.2) hoac 2 dong (dong duoi 5 ky tu)
    function splitBoxes(text, w, h) {
        const n = norm(text);
        if (!n) return [];
        const two = (w / h) < 2.2 && n.length >= 7;
        const lines = two ? [n.slice(0, n.length - 5), n.slice(n.length - 5)] : [n];
        const bands = two ? [[0.06, 0.48], [0.52, 0.94]] : [[0.12, 0.88]];
        const out = [];
        lines.forEach((ln, li) => {
            const slot = (0.9 * w) / ln.length, [y0, y1] = bands[li];
            for (let i = 0; i < ln.length; i++) {
                const bw = Math.max(6, slot * 0.7);
                out.push({ x: Math.round(0.05 * w + slot * i + (slot - bw) / 2), y: Math.round(y0 * h), w: Math.round(bw), h: Math.max(8, Math.round((y1 - y0) * h)), c: ln[i] });
            }
        });
        return out;
    }

    function openCharEditor(r) {
        if (!cur || ced) return;
        if (r.w < MIN_CROP_W || r.h < MIN_CROP_H) { say('<span class="bad">Khung quá nhỏ — hãy chỉnh khung bao trọn biển số trước.</span>', 5000); return; }
        ced = { r, boxes: (r.chars || []).map(b => ({ x: b.x, y: b.y, w: b.w, h: b.h, c: b.c || '' })), sel: null, k: 1, drag: null, w: Math.round(r.w), h: Math.round(r.h) };
        $('trCharTitle').textContent = `🔤 Gắn từng ký tự — Biển #${r.no}`;
        $('trCharText').value = fmtPlate(norm(r.text)) || '';
        $('trCharModal').style.display = '';
        $('trCharKeypad').innerHTML = KEYPAD.map(c => `<button type="button" class="tr-key" data-c="${c}">${c}</button>`).join('') +
            '<button type="button" class="tr-key wide" data-c="" title="Bỏ ký tự của ô đang chọn">⌫</button>';
        if (!ced.boxes.length && norm(r.text)) { ced.boxes = splitBoxes(r.text, ced.w, ced.h); }     // lan dau: chia san theo chuoi da nhap
        document.addEventListener('keydown', onCharKey, true);
        sizeCharCanvas();
        const ord = readingOrder(ced.boxes);
        ced.sel = ord.length ? ord.find(i => !ced.boxes[i].c) ?? ord[0] : null;
        paintChars();
        updateCharInfo();
    }
    function closeCharEditor() {
        if (!ced) return;
        document.removeEventListener('keydown', onCharKey, true);
        window.removeEventListener('mousemove', onCharMove);
        window.removeEventListener('mouseup', onCharUp);
        cc.width = 0; cc.height = 0;
        ced = null;
        $('trCharModal').style.display = 'none';
        $('trCharKeypad').innerHTML = '';
    }
    function sizeCharCanvas() {
        const maxW = Math.min(980, window.innerWidth * 0.9 - 40), maxH = Math.max(160, window.innerHeight * 0.42);
        ced.k = Math.min(maxW / ced.w, maxH / ced.h, 10);
        const cssW = Math.round(ced.w * ced.k), cssH = Math.round(ced.h * ced.k), d = dpr();
        cc.width = Math.round(cssW * d); cc.height = Math.round(cssH * d);
        cc.style.width = cssW + 'px'; cc.style.height = cssH + 'px';
    }
    function paintChars() {
        if (!ced || !cur) return;
        const d = dpr(), k = ced.k, r = ced.r;
        cctx.setTransform(1, 0, 0, 1, 0, 0);
        cctx.clearRect(0, 0, cc.width, cc.height);
        cctx.setTransform(d, 0, 0, d, 0, 0);
        cctx.imageSmoothingEnabled = true;
        cctx.filter = viewFilter();
        cctx.drawImage(cur.img, r.x, r.y, r.w, r.h, 0, 0, ced.w * k, ced.h * k);
        cctx.filter = 'none';
        const ord = readingOrder(ced.boxes);
        ced.boxes.forEach((b, i) => {
            const sel = ced.sel === i, x = b.x * k, y = b.y * k, w = b.w * k, h = b.h * k;
            cctx.lineWidth = sel ? 2.5 : 1.8;
            cctx.strokeStyle = sel ? '#38bdf8' : (b.c ? '#22c55e' : '#ef4444');
            cctx.strokeRect(x, y, w, h);
            const label = (ord.indexOf(i) + 1) + (b.c ? ' ' + b.c : '');
            cctx.font = 'bold 13px sans-serif';
            const tw = cctx.measureText(label).width + 8, ly = y - 17 >= 0 ? y - 17 : y + h;
            cctx.fillStyle = sel ? '#0369a1' : (b.c ? '#16a34a' : '#b91c1c');
            cctx.fillRect(x, ly, tw, 16);
            cctx.fillStyle = '#fff'; cctx.textBaseline = 'middle'; cctx.fillText(label, x + 4, ly + 8); cctx.textBaseline = 'alphabetic';
            if (sel) {
                cctx.fillStyle = '#fff'; cctx.strokeStyle = '#0369a1'; cctx.lineWidth = 1.5;
                HANDLES.forEach(hn => { const [hx, hy] = handlePos(b, hn); cctx.beginPath(); cctx.rect(hx * k - 4, hy * k - 4, 8, 8); cctx.fill(); cctx.stroke(); });
            }
        });
        if (ced.drag && ced.drag.type === 'draw') {
            const a = ced.drag.rect;
            cctx.save(); cctx.setLineDash([6, 4]); cctx.strokeStyle = '#facc15'; cctx.lineWidth = 2;
            cctx.strokeRect(a.x * k, a.y * k, a.w * k, a.h * k); cctx.restore();
        }
    }
    function updateCharInfo() {
        if (!ced) return;
        const txt = boxesText(), n = ced.boxes.length, miss = ced.boxes.filter(b => !b.c).length;
        let msg, ok = false;
        if (!n) msg = 'Chưa có ô nào. Kéo chuột quanh từng ký tự, hoặc nhập chuỗi ký tự rồi bấm 📏 Tự chia ô theo chuỗi.';
        else if (miss) msg = `${n} ô — còn ${miss} ô chưa có ký tự (viền đỏ). Chọn ô rồi gõ phím hoặc bấm ký tự bên dưới.`;
        else { const v = validate(txt); ok = v.ok; msg = `${n} ô — biển: ${txt}${v.ok ? ' ✓' : ' — ' + v.msg}`; }
        const el = $('trCharInfo');
        el.className = 'tr-valid ' + (ok ? 'ok' : 'bad');
        el.textContent = msg;
        $('trCharApply').disabled = !ok;
        $('trCharKeypad').querySelectorAll('.tr-key').forEach(b => b.disabled = ced.sel == null);
    }
    function setBoxChar(c) {
        if (!ced || ced.sel == null) return;
        ced.boxes[ced.sel].c = c;
        if (c) {                                            // sang o ke tiep (theo thu tu doc) de nhap lien tuc
            const ord = readingOrder(ced.boxes), pos = ord.indexOf(ced.sel);
            const next = ord.slice(pos + 1).find(i => !ced.boxes[i].c) ?? ord[pos + 1];
            if (next != null) ced.sel = next;
        }
        paintChars(); updateCharInfo();
    }
    function onCharKey(e) {
        if (!ced) return;
        const typing = document.activeElement && document.activeElement.id === 'trCharText';
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeCharEditor(); return; }
        if (typing) { if (e.key === 'Enter') { e.preventDefault(); $('trCharSplit').click(); } return; }
        if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); if (!$('trCharApply').disabled) $('trCharApply').click(); return; }
        const ord = readingOrder(ced.boxes);
        if (e.key === 'Delete' && ced.sel != null) { e.preventDefault(); e.stopPropagation(); ced.boxes.splice(ced.sel, 1); ced.sel = null; paintChars(); updateCharInfo(); }
        else if (e.key === 'Backspace' && ced.sel != null) { e.preventDefault(); e.stopPropagation(); setBoxChar(''); }
        else if ((e.key === 'ArrowRight' || e.key === 'ArrowLeft') && ord.length) {
            e.preventDefault(); e.stopPropagation();
            const pos = ord.indexOf(ced.sel), np = clamp((pos < 0 ? 0 : pos + (e.key === 'ArrowRight' ? 1 : -1)), 0, ord.length - 1);
            ced.sel = ord[np]; paintChars(); updateCharInfo();
        } else if (e.key.length === 1 && /[A-Za-z0-9]/.test(e.key) && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault(); e.stopPropagation();
            const c = e.key.toUpperCase();
            if (ced.sel == null) { $('trCharInfo').textContent = 'Hãy chọn (hoặc vẽ) một ô trước khi gõ ký tự.'; return; }
            if (!ALPHABET.includes(c)) { $('trCharInfo').className = 'tr-valid bad'; $('trCharInfo').textContent = `Biển VN không dùng "${c}" (hay nhầm O→0, I→1, Q→0, W→V).`; return; }
            setBoxChar(c);
        }
    }
    $('trCharKeypad').addEventListener('click', e => {
        const b = e.target.closest('.tr-key'); if (!b || !ced) return;
        setBoxChar(b.dataset.c);
    });

    // (cung ly do nhu evPt) quy chuot ve pixel CSS cua canvas truoc, roi chia he so phong ced.k ra toa do anh bien
    const cEvPt = e => {
        const b = cc.getBoundingClientRect();
        const cw = parseFloat(cc.style.width) || b.width, ch = parseFloat(cc.style.height) || b.height;
        return { x: (e.clientX - b.left) * (cw / (b.width || cw)) / ced.k, y: (e.clientY - b.top) * (ch / (b.height || ch)) / ced.k };
    };
    function cHit(px, py) {                                  // tay cam o dang chon (toa do anh bien) — dung chung HANDLES voi ban lam viec
        const sel = ced.sel != null ? ced.boxes[ced.sel] : null;
        if (!sel) return null;
        const R = 8 / ced.k;
        for (const hn of HANDLES) { const [hx, hy] = handlePos(sel, hn); if (Math.abs(hx - px) <= R && Math.abs(hy - py) <= R) return hn; }
        return null;
    }
    cc.addEventListener('mousedown', e => {
        if (!ced || e.button !== 0) return;
        const p = cEvPt(e);
        const hn = cHit(p.x, p.y);
        if (hn) { const b = ced.boxes[ced.sel]; ced.drag = { type: 'resize', handle: hn, b, start: { x: b.x, y: b.y, w: b.w, h: b.h } }; }
        else {
            const hit = ced.boxes.map((b, i) => ({ b, i })).filter(o => p.x >= o.b.x && p.x <= o.b.x + o.b.w && p.y >= o.b.y && p.y <= o.b.y + o.b.h)
                .sort((a, b) => a.b.w * a.b.h - b.b.w * b.b.h)[0];
            if (hit) { ced.sel = hit.i; ced.drag = { type: 'move', b: hit.b, dx: p.x - hit.b.x, dy: p.y - hit.b.y }; updateCharInfo(); }
            else ced.drag = { type: 'draw', x0: clamp(p.x, 0, ced.w), y0: clamp(p.y, 0, ced.h), rect: { x: p.x, y: p.y, w: 0, h: 0 } };
        }
        window.addEventListener('mousemove', onCharMove);
        window.addEventListener('mouseup', onCharUp);
        paintChars();
        e.preventDefault();
    });
    function onCharMove(e) {
        if (!ced || !ced.drag) return;
        const p = cEvPt(e), d = ced.drag, W = ced.w, H = ced.h;
        if (d.type === 'draw') {
            const x1 = clamp(p.x, 0, W), y1 = clamp(p.y, 0, H);
            d.rect = { x: Math.min(d.x0, x1), y: Math.min(d.y0, y1), w: Math.abs(x1 - d.x0), h: Math.abs(y1 - d.y0) };
        } else if (d.type === 'move') {
            d.b.x = Math.round(clamp(p.x - d.dx, 0, W - d.b.w)); d.b.y = Math.round(clamp(p.y - d.dy, 0, H - d.b.h));
        } else if (d.type === 'resize') {
            const s0 = d.start;
            let x1 = s0.x, y1 = s0.y, x2 = s0.x + s0.w, y2 = s0.y + s0.h;
            const ix = clamp(p.x, 0, W), iy = clamp(p.y, 0, H);
            if (d.handle.includes('w')) x1 = Math.min(ix, x2 - 3);
            if (d.handle.includes('e')) x2 = Math.max(ix, x1 + 3);
            if (d.handle.includes('n')) y1 = Math.min(iy, y2 - 3);
            if (d.handle.includes('s')) y2 = Math.max(iy, y1 + 3);
            d.b.x = Math.round(x1); d.b.y = Math.round(y1); d.b.w = Math.round(x2 - x1); d.b.h = Math.round(y2 - y1);
        }
        paintChars();
    }
    function onCharUp() {
        window.removeEventListener('mousemove', onCharMove);
        window.removeEventListener('mouseup', onCharUp);
        if (!ced) return;
        const d = ced.drag; ced.drag = null;
        if (d && d.type === 'draw') {
            const a = d.rect;
            if (a.w >= 3 && a.h >= 6) {
                ced.boxes.push({ x: Math.round(a.x), y: Math.round(a.y), w: Math.max(3, Math.round(a.w)), h: Math.max(6, Math.round(a.h)), c: '' });
                ced.sel = ced.boxes.length - 1;
            } else ced.sel = null;                           // bam vao cho trong = bo chon
        }
        paintChars(); updateCharInfo();
    }
    cc.addEventListener('mousemove', e => {
        if (!ced || ced.drag) return;
        const p = cEvPt(e), hn = cHit(p.x, p.y);
        cc.style.cursor = hn ? CURSORS[hn] : (ced.boxes.some(b => p.x >= b.x && p.x <= b.x + b.w && p.y >= b.y && p.y <= b.y + b.h) ? 'move' : 'crosshair');
    });

    $('trCharSplit').addEventListener('click', () => {
        if (!ced) return;
        const n = norm($('trCharText').value);
        if (!n) { $('trCharInfo').className = 'tr-valid bad'; $('trCharInfo').textContent = 'Nhập chuỗi ký tự của biển trước (vd 51D-932.40).'; return; }
        if (ced.boxes.length && !confirm('Thay các ô hiện có bằng các ô chia đều theo chuỗi này?')) return;
        ced.boxes = splitBoxes(n, ced.w, ced.h);
        const ord = readingOrder(ced.boxes); ced.sel = ord[0] ?? null;
        paintChars(); updateCharInfo();
    });
    $('trCharAssign').addEventListener('click', () => {
        if (!ced) return;
        const n = norm($('trCharText').value), ord = readingOrder(ced.boxes);
        if (!ced.boxes.length) { $('trCharInfo').className = 'tr-valid bad'; $('trCharInfo').textContent = 'Chưa có ô nào để gán.'; return; }
        if (n.length !== ord.length) { $('trCharInfo').className = 'tr-valid bad'; $('trCharInfo').textContent = `Chuỗi có ${n.length} ký tự nhưng có ${ord.length} ô — hãy vẽ thêm/xoá ô cho khớp.`; return; }
        ord.forEach((bi, k) => { ced.boxes[bi].c = n[k]; });
        paintChars(); updateCharInfo();
    });
    $('trCharClear').addEventListener('click', () => {
        if (!ced || !ced.boxes.length) return;
        if (!confirm('Xoá hết các ô ký tự đã khoanh?')) return;
        ced.boxes = []; ced.sel = null; paintChars(); updateCharInfo();
    });
    $('trCharClose').addEventListener('click', closeCharEditor);
    $('trCharCancel').addEventListener('click', closeCharEditor);
    $('trCharModal').addEventListener('mousedown', e => { if (e.target === $('trCharModal')) closeCharEditor(); });
    $('trCharApply').addEventListener('click', async () => {
        if (!ced) return;
        const txt = boxesText(), v = validate(txt);
        if (ced.boxes.some(b => !b.c) || !v.ok) { updateCharInfo(); return; }
        const r = ced.r, W = r.w, H = r.h;
        r.chars = ced.boxes.map(b => {
            const x = clamp(Math.round(b.x), 0, W - 1), y = clamp(Math.round(b.y), 0, H - 1);
            return { x, y, w: clamp(Math.round(b.w), 1, W - x), h: clamp(Math.round(b.h), 1, H - y), c: b.c };
        });
        r.text = fmtPlate(txt); r.suggested = false; r.dirty = true;
        closeCharEditor();
        renderRegions(); paint();
        await saveRegion(r, true);
    });
    window.addEventListener('resize', () => { if (ced) { sizeCharCanvas(); paintChars(); } });

    // ===================================================================== mau da luu
    const tagOf = s => String(s.id).slice(0, 10);
    // khop theo bien (bo dau cach / gach / cham, khong phan biet hoa thuong) hoac theo ten anh
    function sampleMatches(s, qRaw) {
        const q = qRaw.trim(); if (!q) return true;
        const qn = norm(q);
        if (qn && [s.correct, s.recognized].some(x => norm(x).includes(qn))) return true;
        return String(s.imageName || '').toLowerCase().includes(q.toLowerCase());
    }
    function renderSamples() {
        const only = $('trOnlyWrong').checked;
        const fixOnly = $('trNeedFix').checked && needFix.size > 0;
        const q = $('trSampleSearch').value;
        const list = samples.filter(s => (!only || s.changed) && (!fixOnly || needFix.has(tagOf(s))) && sampleMatches(s, q));
        $('trSampleCount').textContent = samples.length ? `(${samples.length} mẫu · ${samples.filter(s => s.changed).length} mẫu đọc sai/không đọc được)` : '';
        $('trSampleShown').textContent = samples.length && list.length !== samples.length ? `Hiện ${list.length}/${samples.length} mẫu` : '';
        const box = $('trSamples');
        if (!list.length) { box.innerHTML = `<div class="no-result">${samples.length ? 'Không có mẫu nào khớp bộ lọc / từ khoá này.' : 'Chưa có mẫu. Cắt biển và nhập ký tự ở phần trên rồi nhấn Enter.'}</div>`; return; }
        box.innerHTML = list.slice(0, 300).map(s => {
            const itemId = s.key.includes('|r') ? s.key.split('|r')[0] : '';
            const canOpen = itemId && inbox.some(i => i.id === itemId);
            return `<div class="sim-corr-row${tagOf(s) === hitTag ? ' tr-hit' : ''}${needFix.has(tagOf(s)) ? ' tr-needfix' : ''}" data-key="${esc(s.key)}" data-tag="${esc(tagOf(s))}" data-item="${canOpen ? itemId : ''}">
                <img loading="lazy" src="${API}/corrections/${s.id}/image" alt="">
                <span class="sim-name" title="${esc(s.imageName)}">${esc(s.imageName)}</span>
                <span>${s.recognized ? esc(s.recognized) : '<em>không đọc được</em>'} → <strong class="${s.changed ? 'changed' : ''}">${esc(s.correct)}</strong></span>
                ${canOpen ? '<button class="btn secondary btn-sm tr-open-btn" title="Mở ảnh gốc để chỉnh khung/ký tự">🖼</button>' : ''}
                <button class="btn secondary btn-sm tr-sample-del" title="Bỏ mẫu này">✕</button>
            </div>`;
        }).join('') + (list.length > 300 ? `<div class="no-result">… và ${list.length - 300} mẫu nữa — gõ thêm vào ô tìm kiếm để thu hẹp</div>` : '');
    }
    $('trSampleSearch').addEventListener('input', () => { hitTag = null; renderSamples(); });
    $('trNeedFix').addEventListener('change', () => { hitTag = null; renderSamples(); });

    // Tu bang ket qua train: nhay toi dung mau trong "Mau da luu" (mo khung neu dang an, bo loc, tim theo bien, to sang dong do)
    function locateSample(tag) {
        const smp = samples.find(x => tagOf(x) === tag);
        if (!smp) { $('trTrainMsg').innerHTML = '<span style="color:#f59e0b;">⚠️ Mẫu này không còn trong danh sách mẫu đã lưu (có thể đã bị xoá).</span>'; return; }
        window.pgPanels?.show('tr-samples');
        $('trOnlyWrong').checked = false; $('trNeedFix').checked = false;
        $('trSampleSearch').value = fmtPlate(smp.correct);      // co nhieu mau cung bien thi hien het de so sanh
        hitTag = tag;
        renderSamples();
        const row = $('trSamples').querySelector('.tr-hit');
        if (row) {
            row.scrollIntoView({ block: 'center' });
            $('trSamples').closest('section').scrollIntoView({ block: 'nearest' });
        }
    }
    // Tu bang ket qua train: mo thang anh goc de chinh khung / ky tu
    async function openSampleFromReport(tag) {
        const smp = samples.find(x => tagOf(x) === tag);
        const itemId = smp && smp.key.includes('|r') ? smp.key.split('|r')[0] : '';
        if (!itemId || !inbox.some(i => i.id === itemId)) {
            $('trTrainMsg').innerHTML = '<span style="color:#f59e0b;">⚠️ Không còn ảnh gốc trong hàng chờ để mở — hãy bấm “🔎 Tìm mẫu” rồi xoá mẫu và cắt lại từ ảnh khác.</span>';
            return;
        }
        await openItem(itemId, true);
        $('trWorkbench').scrollIntoView({ block: 'start' });
    }

    $('trOnlyWrong').addEventListener('change', renderSamples);
    $('trSamples').addEventListener('click', async e => {
        const row = e.target.closest('.sim-corr-row'); if (!row) return;
        if (e.target.closest('.tr-open-btn')) {
            await openItem(row.dataset.item, true);
            return;
        }
        if (e.target.closest('.tr-sample-del')) {
            const key = row.dataset.key;
            await fetch(`${API}/corrections?key=${encodeURIComponent(key)}`, { method: 'DELETE' });
            // mau thuoc anh dang mo: bo luon khung de khong luu lai nham
            if (cur && key.startsWith(cur.item.id + '|r')) {
                const r = cur.regions.find(x => x.key === key);
                if (r) { cur.regions = cur.regions.filter(x => x !== r); if (cur.sel === r.no) cur.sel = null; renderRegions(); paint(); }
                const stillAll = cur.regions.length > 0 && cur.regions.every(x => x.saved && !x.dirty);
                if (cur.item.done && !stillAll) await fetch(`${API}/inbox/${cur.item.id}/done?done=false`, { method: 'POST' });
            } else {
                const itemId = row.dataset.item;
                const it = inbox.find(i => i.id === itemId);
                if (it && it.done && it.samples <= 1) await fetch(`${API}/inbox/${itemId}/done?done=false`, { method: 'POST' });
            }
            await loadAll();
        }
    });
    $('trClearSamplesBtn').addEventListener('click', async () => {
        if (!samples.length) return;
        if (!confirm(`Xoá tất cả ${samples.length} mẫu đã lưu? (không ảnh hưởng model hiện tại; ảnh trong hàng chờ vẫn giữ)`)) return;
        await fetch(`${API}/corrections/clear`, { method: 'POST' });
        if (cur) { cur.regions = cur.regions.filter(r => !r.saved); renderRegions(); paint(); }
        await loadAll();
    });

    // ===================================================================== train
    function updateTrainButtons() {
        $('trTrainBtn').disabled = trainRunning || samples.length === 0 || saving > 0;
        $('trTrainCancelBtn').style.display = trainRunning ? '' : 'none';
        $('trEpochs').disabled = trainRunning;
    }

    $('trTrainBtn').addEventListener('click', async () => {
        const n = samples.length, changed = samples.filter(s => s.changed).length;
        if (!n) return;
        const ep = clamp(parseInt($('trEpochs').value, 10) || 25, 3, 200);
        $('trEpochs').value = ep;
        const few = n < 10 ? '\n\n⚠️ Dưới 10 mẫu: model mới sẽ KHÔNG tự cài (tránh học lệch) — bạn vẫn có thể bấm "Vẫn cài bản mới" sau khi xem kết quả.' : '';
        if (!confirm(`Train từ ${n} mẫu (${changed} mẫu hệ thống đọc sai/không đọc được), ${ep} epoch?\n\nChạy nền ở mức ưu tiên thấp, có thể mất vài phút. Xong, nếu model mới không kém model hiện tại thì tự cài và áp dụng ngay (bản cũ được sao lưu).${few}`)) return;
        const r = await jfetch(`${API}/train`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ epochs: ep }) });
        if (!r.ok) { $('trTrainMsg').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.body.message || 'Lỗi ' + r.status)}</span>`; return; }
        lastInstalledAt = null;
        $('trTrainResult').innerHTML = '';
        fetchTrainStatus();
    });
    $('trTrainCancelBtn').addEventListener('click', async () => { await fetch(`${API}/train/cancel`, { method: 'POST' }); fetchTrainStatus(); });
    // Xoa ket qua lan train gan nhat (bang chi tiet tung bien, so sanh model cu/moi). Khong dong toi model dang dung va cac mau da luu.
    async function clearTrainResult() {
        if (trainRunning) return;
        if (!confirm('Xoá kết quả lần train gần nhất (so sánh model cũ/mới và chi tiết từng biển)?\n\nKhông ảnh hưởng model đang dùng và các mẫu đã lưu. Sau khi xoá sẽ không còn nút "Vẫn cài bản mới" cho lần train này.')) return;
        const r = await jfetch(`${API}/train/clear`, { method: 'POST' });
        if (!r.ok) { $('trTrainMsg').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.body.message || 'Không xoá được')}</span>`; return; }
        lastInstalledAt = null;
        fetchTrainStatus();
    }
    $('trTrainClearBtn').addEventListener('click', clearTrainResult);
    $('trTrainResult').addEventListener('change', e => {
        if (!e.target.classList.contains('tr-only-bad-chk')) return;
        reportOnlyBad = e.target.checked;                    // chi la loc hien thi, khong goi may chu
        const wrap = $('trTrainResult').querySelector('.tr-report-wrap');
        if (wrap) wrap.classList.toggle('tr-only-bad', reportOnlyBad);
    });
    $('trTrainResult').addEventListener('click', e => {
        if (e.target.closest('.tr-clear-result')) { clearTrainResult(); return; }
        const find = e.target.closest('.tr-find-sample, .tr-report-img');
        if (find) { locateSample(find.dataset.tag); return; }
        const open = e.target.closest('.tr-open-sample');
        if (open) { openSampleFromReport(open.dataset.tag); return; }
        const t = e.target.closest('.tr-toggle-report');
        if (t) {                                                    // an / hien bang chi tiet tung bien
            reportHidden = !reportHidden;
            const wrap = $('trTrainResult').querySelector('.tr-report-wrap');
            if (wrap) wrap.style.display = reportHidden ? 'none' : '';
            t.textContent = toggleLabel();
        }
    });

    $('trTrainInstallBtn').addEventListener('click', async () => {
        if (!confirm('Cài model mới dù chưa vượt model hiện tại ở mọi chỉ số? Bản cũ được sao lưu nên có thể hoàn tác.')) return;
        const r = await jfetch(`${API}/train/install`, { method: 'POST' });
        $('trTrainMsg').innerHTML = r.ok ? `✅ ${esc(r.body.message)}` : `<span style="color:#ef4444;">⚠️ ${esc(r.body.message || 'Lỗi')}</span>`;
        if (r.ok) window.pgModelEpoch = Date.now();
        fetchTrainStatus();
    });

    function stopTrainPolling() { clearTimeout(trainTimer); trainTimer = null; }
    async function fetchTrainStatus() {
        stopTrainPolling();
        if (!entered) return;
        let s;
        try { s = await (await fetch(`${API}/train/status`)).json(); }
        catch { return; }
        if (!entered) return;
        renderTrain(s);
        if (s.running) trainTimer = setTimeout(fetchTrainStatus, 1500);   // chi hoi khi dang train; xong/khong train thi KHONG hoi nua
    }

    // ---- bang chan doan tung bien sau khi train: model cu/moi doc ra gi va LOI O BUOC NAO ----
    function diagText(d, label) {
        if (!d) return '—';
        const plate = d.plate ? `<span class="plate-tag">${esc(d.plate)}</span> ` : '<em>không đọc được</em> ';
        if (d.ok) return plate + '<span class="ok">✅ đúng</span>';
        if (d.stage === 'segmentation') return plate + `<span class="bad">❌ tách được ${d.segCount ?? 0} ký tự, biển có ${label.length}</span>`;
        if (d.stage === 'classification')
            return plate + '<span class="warn">❌ ' + (d.diffs || []).map(x => `vị trí ${x.pos}: đúng “${esc(x.expected)}”, đọc “${esc(x.read)}”${x.pExpected != null ? ` (đúng chỉ ${Math.round(x.pExpected * 100)}%)` : ''}`).join('; ') + '</span>';
        return plate + '<span class="warn">❌ ký tự đúng nhưng bị loại do định dạng biển</span>';
    }
    let reportOnlyBad = false;         // chi hien cac bien model moi van doc sai
    let reportHidden = false;          // nguoi dung an bang chi tiet tung bien (chi an hien, khong xoa gi)
    const toggleLabel = () => (reportHidden ? '👁 Hiện bảng' : '🙈 Ẩn bảng');
    function sampleReportHtml(rows) {
        const cnt = { ok: 0, segmentation: 0, classification: 0, format: 0 };
        rows.forEach(r => { cnt[r.candidate.ok ? 'ok' : r.candidate.stage] = (cnt[r.candidate.ok ? 'ok' : r.candidate.stage] || 0) + 1; });
        let html = `<div class="tr-report-sum"><strong>Chi tiết từng biển bạn đã dạy (${rows.length}):</strong> ` +
            `<span class="ok">${cnt.ok} đọc đúng</span>` +
            (cnt.classification ? ` · <span class="warn">${cnt.classification} sai ký tự (train thêm mẫu sẽ cải thiện)</span>` : '') +
            (cnt.segmentation ? ` · <span class="bad">${cnt.segmentation} lỗi TÁCH ký tự (train không sửa được)</span>` : '') +
            (cnt.format ? ` · ${cnt.format} sai định dạng` : '') +
            ` <label class="checkbox-label" style="display:inline-flex;margin-left:10px;"><input type="checkbox" class="tr-only-bad-chk"${reportOnlyBad ? ' checked' : ''}> Chỉ biển còn sai</label>` +
            ` <button type="button" class="btn secondary btn-sm tr-toggle-report" style="margin-left:6px;">${toggleLabel()}</button>` +
            ' <button type="button" class="btn secondary btn-sm tr-clear-result" style="margin-left:6px;">🗑 Xoá thông tin này</button></div>';
        html += `<div class="tr-report-wrap${reportOnlyBad ? ' tr-only-bad' : ''}"${reportHidden ? ' style="display:none;"' : ''}><table class="tr-report"><thead><tr><th>Ảnh biển</th><th>Biển đúng</th><th>Model cũ</th><th>Model mới</th><th>Lỗi ở bước nào / cách xử lý</th></tr></thead><tbody>`;
        rows.forEach(r => {
            const smp = samples.find(x => String(x.id).slice(0, 10) === r.tag);
            const img = smp ? `<img class="tr-report-img" data-tag="${esc(r.tag)}" title="Bấm để tìm mẫu này trong “Mẫu đã lưu”" src="${API}/corrections/${smp.id}/image" alt="">` : '';
            if (!r.candidate.ok) needFix.add(r.tag);
            const actions = smp ? `<div class="tr-report-actions"><button type="button" class="btn secondary btn-sm tr-find-sample" data-tag="${esc(r.tag)}" title="Tìm và tô sáng mẫu này trong “Mẫu đã lưu”">🔎 Tìm mẫu</button>` +
                `<button type="button" class="btn secondary btn-sm tr-open-sample" data-tag="${esc(r.tag)}" title="Mở ảnh gốc để chỉnh khung / ký tự">🖼 Mở ảnh để chỉnh</button></div>` : '';
            const c = r.candidate;
            let hint = '';
            if (c.ok) hint = '<span class="ok">Không cần xử lý.</span>';
            else if (c.stage === 'segmentation') hint = `Lỗi ở bước <strong>tách ký tự</strong> (xử lý ảnh, không phải model) nên train không sửa được. Mở lại ảnh (🖼 ở danh sách mẫu) và chỉnh khung <strong>vừa khít biển</strong>, bỏ phần viền/ốc vít/khung xe; biển 2 dòng cần cắt đủ cả hai dòng.`;
            else if (c.stage === 'classification') hint = 'Lỗi ở bước <strong>đọc ký tự</strong> — train thêm nhiều mẫu có đúng các ký tự bị sai này (ngày/đêm, nghiêng, mờ) rồi train lại.';
            else hint = 'Ký tự đọc đúng nhưng bị chuẩn hoá theo định dạng biển VN — kiểm tra lại ký tự bạn nhập.';
            html += `<tr data-ok="${c.ok ? 1 : 0}"><td>${img}</td><td><strong>${esc(fmtPlate(r.label))}</strong></td><td>${diagText(r.current, r.label)}</td><td>${diagText(c, r.label)}</td><td class="tr-report-hint">${hint}${actions}</td></tr>`;
        });
        return html + '</tbody></table></div>';
    }

    function renderTrain(s) {
        trainRunning = !!s.running;
        needFix = new Set();                                  // se duoc dien lai khi dung bang chi tiet tung bien
        const st = s.status;
        const bar = $('trTrainBar'), wrap = $('trTrainBarWrap'), msg = $('trTrainMsg'), res = $('trTrainResult'), warn = $('trTrainerWarn');
        $('trTrainInstallBtn').style.display = s.canInstall ? '' : 'none';
        // co ket qua / thong bao cua lan train truoc va khong dang train thi cho phep xoa
        $('trTrainClearBtn').style.display = (!s.running && (s.status || s.cancelled || s.installed)) ? '' : 'none';
        updateTrainButtons();

        if (!s.trainerFound) {
            warn.style.display = '';
            warn.innerHTML = '⚠️ Không tìm thấy thư mục <code>PlateTrainer</code> cạnh dự án — Train chưa dùng được. Đặt đường dẫn ở <code>Trainer:Path</code> trong appsettings.json.';
            wrap.style.display = 'none'; msg.textContent = '';
            $('trTrainBtn').disabled = true;
            return;
        }
        warn.style.display = 'none';
        if (s.running && st) {
            wrap.style.display = ''; bar.style.width = (st.progress || 0) + '%';
            msg.textContent = `⏳ ${st.stageLabel || 'Đang chạy'} — ${st.message || ''}`;
        } else if (s.running) {
            wrap.style.display = ''; bar.style.width = '2%'; msg.textContent = '⏳ Đang khởi động…';
        } else if (s.cancelled) {
            wrap.style.display = 'none'; msg.textContent = '⏹ Đã huỷ train. Model hiện tại không bị thay đổi.';
        } else if (s.interrupted) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#f59e0b;">⚠️ Lần train trước bị dừng giữa chừng. Model hiện tại không bị thay đổi.</span>${s.stderr ? `<pre class="hint small">${esc(s.stderr)}</pre>` : ''}`;
        } else if (st && st.error) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#ef4444;">⚠️ Train thất bại: ${esc(st.error)}</span>`;
        } else if (st && st.finished) {
            wrap.style.display = ''; bar.style.width = '100%';
            msg.textContent = s.installed?.ok ? '✔ Train xong — model mới đã được cài và đang dùng.'
                : (st.result && !st.result.recommendInstall ? '✔ Train xong — chưa cài model mới (xem chi tiết bên dưới).' : '✔ ' + (st.message || 'Hoàn tất'));
        } else if (!st && s.stderr) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#ef4444;">⚠️ Không chạy được trình train:</span><pre class="hint small">${esc(s.stderr)}</pre>`;
        } else {
            wrap.style.display = 'none'; msg.textContent = '';
        }

        let html = '';
        if (st && st.import) {
            const im = st.import;
            html += `<div class="hint small">Dùng <strong>${im.used}/${im.total}</strong> mẫu · ${im.chars} ký tự học được, trong đó <strong>${im.hardChars}</strong> ký tự hệ thống đã đọc sai (học kỹ hơn).` +
                (im.skipped?.length ? ` Bỏ qua ${im.skipped.length} mẫu: ${im.skipped.slice(0, 3).map(x => esc((x.label || x.key || '') + ' — ' + x.reason)).join('; ')}${im.skipped.length > 3 ? '…' : ''}` : '') + '</div>';
            if (im.skipped?.length) html += '<div class="hint small">💡 Mẫu bị bỏ qua thường do ảnh cắt chưa sát/khung dính viền nên hệ thống tách sai số ký tự — mở lại ảnh (🖼 ở danh sách mẫu), chỉnh khung vừa khít biển rồi lưu lại.</div>';
        }
        if (st && st.result && st.finished && !st.error) {
            const r = st.result;
            html += `<div class="sim-result-grid">
                <span>Đúng cả biển (${r.plates} biển đã cấp)</span><span>model cũ <strong>${pct(r.plateAccCurrent)}</strong> → model mới <strong>${pct(r.plateAccCandidate)}</strong></span>
                <span>Đúng từng ký tự (${r.valChars} ký tự chưa từng học)</span><span>model cũ <strong>${pct(r.charAccCurrent)}</strong> → model mới <strong>${pct(r.charAccCandidate)}</strong></span>
            </div>`;
        }
        if (st && st.result && st.finished && !st.error && st.result.samples && st.result.samples.length) html += sampleReportHtml(st.result.samples);
        if (s.installed) {
            const i = s.installed;
            html += i.ok ? `<div style="color:#16a34a;">✅ ${esc(i.message)}</div>` : `<div style="color:#ef4444;">⚠️ Không cài được: ${esc(i.message)}</div>`;
            if (i.ok && lastInstalledAt !== i.at) {          // model doi -> bao tab Mo phong bo ket qua nhan dien cu
                lastInstalledAt = i.at;
                window.pgModelEpoch = Date.now();
                html += '<div class="hint small">Tab 🧪 Mô phỏng sẽ tự xoá kết quả cũ và nhận diện lại bằng model mới khi bạn quay lại.</div>';
            }
        } else if (st && st.result && st.finished && !st.error && !st.result.recommendInstall) {
            html += `<div style="color:#f59e0b;">⚠️ ${esc(st.result.reason)} Model hiện tại được giữ nguyên.${s.canInstall ? ' Bạn vẫn có thể bấm "Vẫn cài bản mới".' : ''}</div>`;
        }
        res.innerHTML = html;
        updateNeedFixUi();
    }

    // o loc "Chi mau can chinh theo ket qua train" chi hien khi lan train gan nhat con bien model moi doc sai
    function updateNeedFixUi() {
        const n = [...needFix].filter(t => samples.some(s => tagOf(s) === t)).length;
        $('trNeedFixLbl').style.display = n ? '' : 'none';
        $('trNeedFixText').textContent = `Chỉ mẫu cần chỉnh theo kết quả train (${n})`;
        if (!n && $('trNeedFix').checked) { $('trNeedFix').checked = false; renderSamples(); }
    }

    // ===================================================================== vao / roi tab
    // Quy tac: KHONG o tab Train = khong ton gi: khong goi may chu, khong hoi tien do train, khong giu anh/canvas,
    // khong lang nghe ban phim. (Dang train thi tien trinh train van chay nen; quay lai tab se thay tien do.)
    function release() {
        stopTrainPolling();
        clearTimeout(msgTimer);
        window.removeEventListener('mousemove', onDragMove);
        window.removeEventListener('mouseup', onDragEnd);
        closeCharEditor();
        closeCurrent();
        inbox = []; samples = []; needFix = new Set(); hitTag = null;
        $('trSampleSearch').value = '';
        $('trInbox').innerHTML = ''; $('trSamples').innerHTML = ''; $('trTrainResult').innerHTML = '';
        say('');
    }

    function enter() {
        if (entered || !tabActive()) return;
        entered = true;
        document.addEventListener('keydown', onKeyDown);
        document.addEventListener('keyup', onKeyUp);
        $('trInbox').innerHTML = '<div class="no-result">Đang tải…</div>';
        loadAll();
        fetchTrainStatus();
    }
    function leave() {
        if (!entered) return;
        entered = false;
        document.removeEventListener('keydown', onKeyDown);
        document.removeEventListener('keyup', onKeyUp);
        release();
    }
    // trainHub.js bao sau MOI lan doi tab (tab lon hoac tab con): phan nay hien thi thi vao, bi an thi roi
    document.addEventListener('pg:train-nav', () => { if (tabActive()) enter(); else leave(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) stopTrainPolling(); else if (entered) fetchTrainStatus(); });
    window.addEventListener('pagehide', release);
    updateNav();
})();
