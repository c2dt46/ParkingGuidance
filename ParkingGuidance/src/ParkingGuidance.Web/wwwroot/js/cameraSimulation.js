// ---------------------------------------------------------------------------
// Tab "Mo phong": chon thu muc anh -> nhan dien TUNG anh (tat ca xe, khong gioi han vung) -> "Tiep theo".
//  • ✂️ Cat/Zoom vung: quet chuot tren anh de cat vung (bien so xa, nho), phong to, nhan dien rieng vung do.
//  • ➕ Train xe / Train bien / Train ky tu / Train tat ca (nhom nut ngoai cung ben trai): anh khong nhan dien duoc / doc sai thi gui anh goc
//    sang tab "Train" tuong ung (xe, bien so: tu goi y khung; ky tu: hang cho de cat vung bien + go ky tu). Tab nay KHONG con phan train.
// Chi xem ket qua, KHONG ghi vao bai do. Roi tab = giai phong het (xem release()).
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/simulation';
    const folderInput = $('simFolderInput'), filesInput = $('simFilesInput');
    const prevBtns = document.querySelectorAll('#simTab .sim-prev'), nextBtns = document.querySelectorAll('#simTab .sim-next');   // 2 cap: thanh cong cu + khung thong tin ben phai
    const redoBtn = $('simRedoBtn'), cropBtn = $('simCropBtn'), autoBtn = $('simAutoBtn'), inspectBtn = $('simInspectBtn');
    const canvas = $('simCanvas'), ctx = canvas.getContext('2d');
    const zoomCanvas = $('simZoomCanvas'), zctx = zoomCanvas.getContext('2d');

    const TYPE_VI = { Car: 'Ô tô', Motorcycle: 'Xe máy', Bus: 'Xe buýt', Truck: 'Xe tải', Region: 'Vùng cắt', Unknown: 'Không thấy xe (chỉ thấy biển)' };
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const pct = x => (x == null ? '—' : (x * 100).toFixed(x >= 0.995 || x === 0 ? 0 : 1) + '%');
    const srcW = s => s.naturalWidth || s.width, srcH = s => s.naturalHeight || s.height;

    let files = [];            // File[] da loc + sap xep
    let idx = -1;              // anh dang xem
    const results = new Map(); // idx -> { ok:true, data } | { ok:false, message }
    let currentImg = null;     // HTMLImageElement cua anh dang xem
    let imgUrl = null;
    let aborter = null;        // huy yeu cau cu khi nguoi dung chuyen anh nhanh
    let busy = false;
    let entered = false;
    let epochSeen = window.pgModelEpoch || 0;   // tang moi khi tab Train cai model moi

    const tabActive = () => $('simTab').classList.contains('active');
    const relName = f => f.webkitRelativePath || f.name;

    // ----- the xe (moi the: 1 xe hoac 1 vung cat) -----------------------------------------------------------------------------
    const cards = { main: new Map(), zoom: new Map() };   // key -> { v, view }
    // Nhom nut "Them vao Train": moi loai co kho rieng ben tab Train. added[loai] = chi so anh da gui trong phien nay.
    const TRAIN_KINDS = ['vehicle', 'plate', 'char'];
    const TRAIN_LABEL = { vehicle: 'xe', plate: 'biển', char: 'ký tự (OCR)' };
    const TRAIN_BTN_TEXT = { vehicle: 'Train xe', plate: 'Train biển', char: 'Train ký tự', all: 'Train tất cả' };
    const TRAIN_SAMPLES_API = { vehicle: '/api/system/vehicle-train/samples', plate: '/api/system/plate-train/samples' };
    const added = { vehicle: new Set(), plate: new Set(), char: new Set() };
    let addingToTrain = false;
    const trainBtns = { vehicle: $('simTrainVehBtn'), plate: $('simTrainPlateBtn'), char: $('simTrainCharBtn'), all: $('simTrainAllBtn') };
    const addedAny = i => TRAIN_KINDS.some(k => added[k].has(i));

    function mainView() {
        const sx = (results.get(idx)?.data?.frameWidth) ? currentImg.naturalWidth / results.get(idx).data.frameWidth : 1;
        const sy = (results.get(idx)?.data?.frameHeight) ? currentImg.naturalHeight / results.get(idx).data.frameHeight : 1;
        return { name: 'main', src: currentImg, sx, sy, keyBase: relName(files[idx]), kind: 'vehicle', container: $('simVehicles') };
    }
    function zoomView() {
        return { name: 'zoom', src: cropCanvas, sx: 1, sy: 1, kind: 'crop', container: $('simZoomRows'),
                 keyBase: `${relName(files[idx])}|c${cropRect.x},${cropRect.y},${cropRect.w},${cropRect.h}` };
    }

    function cardHtml(v, view) {
        const head = `#${v.index} ${esc(TYPE_VI[v.vehicleType] || v.vehicleType)}` + (v.color ? ` · ${esc(v.color)}` : '') +
            (v.isRegion ? '' : ` · ${Math.round(v.confidence * 100)}%`);
        const read = v.plateNumber
            ? `<span class="plate-tag">${esc(v.plateNumber)}</span> <span class="hint small" style="margin:0;">${Math.round(v.plateConfidence * 100)}%</span>`
            : (v.plateStage === 'unread'
                ? `<span class="warn" style="margin:0;">🔶 dò được vùng biển nhưng chưa đọc được ký tự</span> <span class="hint small" style="margin:0;">(lỗi ở bước đọc → thêm vào Train để dạy)</span>`
                : `<span class="bad" style="margin:0;">🔴 không dò được vùng biển</span> <span class="hint small" style="margin:0;">(lỗi ở bước dò biển → thêm vào Train rồi tự cắt khung)</span>`);
        return `<div class="sim-veh">
            <div class="sim-veh-head">${head}</div>
            <div class="sim-veh-read">Đọc được: ${read}</div>
            <div class="sim-veh-fix"><button type="button" class="btn secondary btn-sm sim-inspect-btn" data-view="${view.name}" data-vi="${v.index}" title="Phóng to, kéo, cắt vùng, chỉnh sáng để đối chiếu biển này với chuỗi đọc được">🔎 Soi biển</button></div>
        </div>`;
    }

    function renderCards(view, vehicles, emptyMsg) {
        cards[view.name].clear();
        if (!vehicles.length) { view.container.innerHTML = `<div class="no-result">${esc(emptyMsg)}</div>`; return; }
        vehicles.forEach(v => cards[view.name].set(`${view.keyBase}|v${v.index}`, { v, view }));
        view.container.innerHTML = vehicles.map(v => cardHtml(v, view)).join('');
    }

    // ----- ➕ Them anh vao hang cho Train ----------------------------------------------------------------------------------------
    // Gui ANH GOC (day du do phan giai) + goi y: khung bien / bien da doc / xe khong do duoc bien (toa do pixel anh goc).
    function buildHint() {
        const hint = { plates: [], vehicles: [] };
        const r = results.get(idx);
        if (!r || !r.ok || !currentImg) return hint;
        const v = mainView();
        const rd = n => Math.round(n);
        r.data.vehicles.forEach(x => {
            if (x.isRegion) return;
            if (x.plateBox) {
                hint.plates.push({ x1: rd(x.plateBox.x1 * v.sx), y1: rd(x.plateBox.y1 * v.sy), x2: rd(x.plateBox.x2 * v.sx), y2: rd(x.plateBox.y2 * v.sy), text: x.plateNumber || '' });
            } else {
                hint.vehicles.push({ x1: rd(x.box.x1 * v.sx), y1: rd(x.box.y1 * v.sy), x2: rd(x.box.x2 * v.sx), y2: rd(x.box.y2 * v.sy), type: x.vehicleType });
            }
        });
        return hint;
    }

    // >>> TRAIN-RESULT-START
    // Gui anh `my` sang kho Train cua 1 loai, roi HOI LAI may chu de XAC NHAN anh da vao kho.
    // Tra ve { ok, isNew, msg, id?, state? ('pending'|'partial'|'done'|'todo'|'reviewed'), stat? }.
    async function sendToTrain(kind, my) {
        const file = files[my];
        try {
            if (kind === 'char') {
                // Hang cho "Train ky tu / OCR" (kem goi y khung bien tu ket qua nhan dien hien tai)
                const fd = new FormData();
                fd.append('image', file, file.name);
                fd.append('name', relName(file));
                fd.append('source', 'simulation');
                fd.append('hint', JSON.stringify(buildHint()));
                const res = await fetch(`${API}/inbox`, { method: 'POST', body: fd });
                const body = await res.json().catch(() => ({}));
                if (!res.ok) return { ok: false, msg: body.message || ('Lỗi ' + res.status) };
                // xac nhan: doc lai hang cho tu may chu va tim dung anh vua gui
                const lst = await fetch(`${API}/inbox`).then(r => r.json()).catch(() => null);
                const it = lst && (lst.items || []).find(x => x.id === body.id);
                if (!it) return { ok: false, msg: 'máy chủ báo đã nhận nhưng không thấy ảnh trong hàng chờ — hãy tải lại trang rồi thử lại' };
                const state = it.done ? 'done' : (it.samples ? 'partial' : 'pending');
                return { ok: true, isNew: !!body.isNew, id: body.id, state, samples: it.samples,
                         stat: `hàng chờ: ${lst.count} ảnh, ${lst.pending} chưa xử lý`, msg: '' };
            }
            // Train xe / Train bien so: them anh vao kho mau; may chu tu goi y khung (chua duyet) bang bo do dang chay
            const fd = new FormData();
            fd.append('files', file, file.name);
            const res = await fetch(`${TRAIN_SAMPLES_API[kind]}?suggest=true`, { method: 'POST', body: fd });
            const body = await res.json().catch(() => ({}));
            if (!res.ok) return { ok: false, msg: body.message || ('Lỗi ' + res.status) };
            if (body.failed && body.failed.length) return { ok: false, msg: body.failed[0].error || 'ảnh không hợp lệ' };
            const sample = body.added && body.added[0];
            const lst = await fetch(TRAIN_SAMPLES_API[kind]).then(r => r.json()).catch(() => null);
            const items = Array.isArray(lst) ? lst : null;
            const mine = sample && items ? items.find(x => x.id === sample.id) : null;
            if (sample && items && !mine) return { ok: false, msg: 'máy chủ báo đã nhận nhưng không thấy ảnh trong kho — hãy tải lại trang rồi thử lại' };
            return { ok: true, isNew: !!sample, id: sample ? sample.id : null, state: mine ? (mine.reviewed ? 'reviewed' : 'todo') : null,
                     boxes: sample ? sample.boxes : null,
                     stat: items ? `kho: ${items.length} ảnh, ${items.filter(x => !x.reviewed).length} chưa duyệt` : '',
                     msg: sample ? '' : 'ảnh này đã có sẵn trong kho mẫu' };
        } catch (err) {
            return { ok: false, msg: err.message };
        }
    }

    // ----- bang ket qua gui sang Train: GIU NGUYEN cho toi khi dong / gui anh khac (khong bi tin nhan khac ghi de) + nut mo thang tab Train -----
    const trainResultEl = document.createElement('div');
    trainResultEl.id = 'simTrainResult';
    trainResultEl.style.display = 'none';
    trainBtns.all.parentElement.insertAdjacentElement('afterend', trainResultEl);

    function trainRowText(kind, r) {
        if (!r.ok) return { icon: '⚠️', text: `chưa gửi được — ${r.msg}`, actions: '' };
        const open = `<button type="button" class="btn secondary btn-sm" data-open="${kind}" data-id="${esc(r.id || '')}">➡ Mở tab Train › ${TRAIN_LABEL[kind]}</button>`;
        if (kind === 'char') {
            const where = r.state === 'done' ? 'ảnh này ĐÃ XỬ LÝ XONG trước đó nên đang nằm ở mục “Đã xong” (bấm ↩ để đưa lại “Chưa xử lý”)'
                : r.state === 'partial' ? `ảnh đã có trong hàng chờ (✎ ${r.samples} mẫu, chưa xong)`
                : r.isNew ? 'đã vào hàng chờ (⏳ chưa xử lý)' : 'ảnh đã có sẵn trong hàng chờ (⏳ chưa xử lý)';
            const reset = r.state === 'done' ? `<button type="button" class="btn secondary btn-sm" data-reset="${esc(r.id)}">↩ Đưa lại “Chưa xử lý”</button>` : '';
            return { icon: '✔', text: `${where} · ${r.stat}`, actions: open + reset };
        }
        const what = r.isNew ? `đã thêm, ${r.boxes} khung gợi ý (chưa duyệt — vào tab Train để kiểm tra khung)` : r.msg;
        return { icon: '✔', text: `${what}${r.stat ? ' · ' + r.stat : ''}`, actions: open };
    }
    function showTrainResult(name, kinds, results) {
        const rows = kinds.map((k, i) => {
            const t = trainRowText(k, results[i]);
            return `<div style="margin:4px 0;">${t.icon} <strong>${TRAIN_LABEL[k]}</strong>: ${esc(t.text)} ${t.actions}</div>`;
        }).join('');
        trainResultEl.innerHTML = `<div style="border:1px solid var(--border);border-radius:8px;padding:8px 10px;margin:6px 0;background:var(--panel-light);">
            <div style="display:flex;justify-content:space-between;gap:8px;align-items:center;">
                <strong>🧠 Đã gửi sang Train: ${esc(name)}</strong>
                <button type="button" class="btn secondary btn-sm" data-close title="Đóng bảng này">✕</button>
            </div>${rows}</div>`;
        trainResultEl.style.display = '';
    }
    function openTrain(kind, id) {
        if (!window.pgTrainNav) { setStatus('Không mở được tab Train tự động — hãy bấm tab 🧠 Train.'); return; }
        // bao cho tab Train ky tu biet anh nao can mo (dat TRUOC khi chuyen tab; neu tab dang mo san thi no tu nap lai)
        if (kind === 'char' && id) document.dispatchEvent(new CustomEvent('pg:train-focus', { detail: { kind, id } }));
        window.pgTrainNav.open(kind);
    }
    trainResultEl.addEventListener('click', async e => {
        if (e.target.closest('[data-close]')) { trainResultEl.style.display = 'none'; return; }
        const o = e.target.closest('[data-open]');
        if (o) { openTrain(o.dataset.open, o.dataset.id); return; }
        const r = e.target.closest('[data-reset]');
        if (r) {
            r.disabled = true;
            const res = await fetch(`${API}/inbox/${r.dataset.reset}/done?done=false`, { method: 'POST' }).catch(() => null);
            r.textContent = res && res.ok ? '✔ Đã đưa lại “Chưa xử lý”' : '⚠️ Không đặt lại được';
            if (res && res.ok) document.dispatchEvent(new CustomEvent('pg:train-data-imported'));
        }
    });

    // kinds: ['vehicle'] | ['plate'] | ['char'] | tat ca. Gui lan luot tung loai, bao ket qua tung loai.
    async function addToTrain(kinds) {
        if (idx < 0 || !files[idx] || !currentImg || addingToTrain) return;
        const my = idx;
        addingToTrain = true;
        updateButtons();
        const names = kinds.map(k => TRAIN_LABEL[k]).join(', ');
        setStatus(`⏳ Đang thêm vào Train (${names})…`);
        const results = [];
        let failed = 0;
        try {
            for (const kind of kinds) {
                const r = await sendToTrain(kind, my);
                results.push(r);
                if (r.ok) added[kind].add(my); else failed++;
            }
            // Cac tab Train chi nap danh sach khi mo / khi nhan su kien nay -> bao de chung nap lai, ke ca hang cho Train ky tu
            if (results.some(r => r.ok)) document.dispatchEvent(new CustomEvent('pg:train-data-imported'));
            renderFileList();
            showTrainResult(relName(files[my]), kinds, results);
            setStatus(`🧠 ${failed ? 'Chưa thêm hết' : 'Đã gửi xong'} (${names}) — xem bảng kết quả bên dưới nút Train; bấm “Mở tab Train” để tới đúng chỗ.`);
        } finally {
            addingToTrain = false;
            updateButtons();
        }
    }
    // <<< TRAIN-RESULT-END
    trainBtns.vehicle.addEventListener('click', () => addToTrain(['vehicle']));
    trainBtns.plate.addEventListener('click', () => addToTrain(['plate']));
    trainBtns.char.addEventListener('click', () => addToTrain(['char']));
    trainBtns.all.addEventListener('click', () => addToTrain(TRAIN_KINDS));

    // ----- tu dong chuyen anh: nut ▶ Chay / ⏸ Dung + o so giay -------------------------------------------------------------
    // Chay: nhan dien xong anh nao thi dem nguoc roi tu sang anh ke. Dung: khong tu chuyen. Roi tab / het anh -> tu dung.
    const autoInput = $('simAutoSec'), autoLabel = $('simAuto');
    const AUTO_KEY = 'pg.simAutoSec';
    let autoOn = false;                // dang o che do tu chuyen (nguoi dung da bam ▶ Chay tu dong)
    let autoTimer = null, autoDeadline = 0;
    try { const v = localStorage.getItem(AUTO_KEY); if (v !== null && parseFloat(v) > 0) autoInput.value = v; } catch { /* bo qua */ }

    const autoSeconds = () => { const v = parseFloat(autoInput.value); return v > 0 ? v : 0; };
    // dang cat vung / dang gui anh sang Train: KHONG tu chuyen anh (de khong mat cong viec cua nguoi dung)
    const autoBlocked = () => addingToTrain || !!cropRect || !!cropDrag;
    function stopAuto(msg) { clearInterval(autoTimer); autoTimer = null; autoLabel.textContent = msg || ''; }

    function refreshAutoBtn() {
        autoBtn.textContent = autoOn ? '⏸ Dừng tự động' : '▶ Chạy tự động';
        autoBtn.classList.toggle('primary', autoOn);
        autoBtn.classList.toggle('secondary', !autoOn);
        autoBtn.title = autoOn ? 'Đang tự chuyển ảnh — bấm để dừng' : 'Tự động chuyển sang ảnh kế tiếp sau số giây ở ô bên cạnh';
    }
    function setAuto(on) {
        autoOn = on;
        if (on && !autoSeconds()) { autoInput.value = '3'; try { localStorage.setItem(AUTO_KEY, '3'); } catch { /* bo qua */ } }
        refreshAutoBtn();
        if (on) scheduleAuto(); else stopAuto();
    }

    function scheduleAuto() {
        stopAuto();
        if (!autoOn || idx < 0 || !files.length) return;
        const secs = autoSeconds();
        if (!secs) return;
        if (idx >= files.length - 1) { setAuto(false); autoLabel.textContent = '✔ Đã đến ảnh cuối — tự động đã dừng.'; return; }
        if (busy || !tabActive() || document.hidden) return;   // dang nhan dien / o tab khac / trinh duyet an: chua dem
        if (autoBlocked()) { autoLabel.textContent = '⏸ Tạm dừng tự chuyển (đang cắt vùng / thêm vào Train)'; return; }
        autoDeadline = Date.now() + secs * 1000;
        const tick = () => {
            if (busy || !tabActive()) { stopAuto(); return; }  // roi tab thi dung; quay lai tab se dem lai tu dau
            if (autoBlocked()) { stopAuto('⏸ Tạm dừng tự chuyển (đang cắt vùng / thêm vào Train)'); return; }
            const left = autoDeadline - Date.now();
            if (left <= 0) { stopAuto(); show(idx + 1); return; }
            autoLabel.textContent = `⏱ Tự chuyển ảnh sau ${(Math.ceil(left / 100) / 10).toFixed(1)} giây`;
        };
        tick();
        autoTimer = setInterval(tick, 100);
    }
    autoBtn.addEventListener('click', () => { if (idx < 0) return; setAuto(!autoOn); });
    autoInput.addEventListener('input', () => {
        try { localStorage.setItem(AUTO_KEY, autoInput.value); } catch { /* bo qua */ }
        if (autoOn) scheduleAuto();                            // doi so giay -> dem lai tu dau
    });

    const isImage = f => (f.type && f.type.startsWith('image/')) || /\.(jpe?g|png|bmp|webp)$/i.test(f.name);

    // ----- chon thu muc / nhieu anh ------------------------------------------------------------
    function onPick(list) {
        const picked = Array.from(list).filter(isImage)
            .sort((a, b) => relName(a).localeCompare(relName(b), undefined, { numeric: true, sensitivity: 'base' }));
        if (aborter) aborter.abort();
        stopAuto();
        clearCrop();
        results.clear();
        files = picked;
        idx = -1;
        renderFileList();
        if (!files.length) {
            setProgress('Không tìm thấy ảnh nào (jpg, png, bmp, webp) trong lựa chọn.');
            showEmpty('Không có ảnh.');
            updateButtons();
            return;
        }
        show(0);   // nhan dien ngay anh dau tien
    }
    folderInput.addEventListener('change', e => { onPick(e.target.files); e.target.value = ''; });
    filesInput.addEventListener('change', e => { onPick(e.target.files); e.target.value = ''; });

    // ----- chuyen anh --------------------------------------------------------------------------
    async function show(i) {
        if (i < 0 || i >= files.length) return;
        if (aborter) aborter.abort();
        stopAuto();
        clearCrop();                      // vung cat gan voi tung anh
        idx = i;
        busy = false;
        updateButtons();
        renderFileList();
        setProgress();

        const my = i;
        let img;
        try { img = await loadImage(files[i]); }
        catch { results.set(i, { ok: false, message: 'Không mở được ảnh này.' }); if (my === idx) { renderResult(); scheduleAuto(); } renderFileList(); return; }
        if (my !== idx) return;   // nguoi dung da chuyen sang anh khac
        currentImg = img;
        updateButtons();

        const cached = results.get(i);
        if (cached && cached.ok) { renderResult(); scheduleAuto(); return; }
        drawPlain();
        await analyze(i);
    }

    function loadImage(file) {
        return new Promise((resolve, reject) => {
            if (imgUrl) URL.revokeObjectURL(imgUrl);
            imgUrl = URL.createObjectURL(file);
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = reject;
            img.src = imgUrl;
        });
    }

    async function analyze(i) {
        const my = i;
        busy = true;
        updateButtons();
        setStatus('⏳ Đang nhận diện… (nếu hệ thống đang xử lý camera thật, ảnh này chờ đến lượt — mô phỏng luôn nhường camera)');
        $('simSummary').textContent = '⏳ Đang nhận diện ảnh này…';          // khong de ket qua cua anh truoc nam lai tren man hinh
        $('simVehicles').innerHTML = '<div class="no-result">—</div>';
        cards.main.clear();
        aborter = new AbortController();
        const form = new FormData();
        form.append('image', files[i], files[i].name);
        const conf = parseFloat($('simConf').value);
        const url = `${API}/analyze` + (conf > 0 ? `?conf=${conf}` : '');
        try {
            const res = await fetch(url, { method: 'POST', body: form, signal: aborter.signal });
            const body = await res.json().catch(() => ({}));
            if (my !== idx || !entered) return;
            results.set(i, res.ok ? { ok: true, data: body } : { ok: false, message: body.message || `Lỗi ${res.status}` });
        } catch (err) {
            if (err.name === 'AbortError' || my !== idx) return;
            results.set(i, { ok: false, message: 'Không gọi được máy chủ: ' + err.message });
        }
        busy = false;
        updateButtons();
        renderResult();
        renderFileList();
        scheduleAuto();                                        // nhan dien xong -> bat dau dem nguoc sang anh ke
    }

    prevBtns.forEach(b => b.addEventListener('click', () => show(idx - 1)));
    nextBtns.forEach(b => b.addEventListener('click', () => show(idx + 1)));
    redoBtn.addEventListener('click', () => { if (idx >= 0) { results.delete(idx); show(idx); } });

    // phim mui ten: CHI gan khi dang o tab Mo phong (roi tab thi go ra), khong chen vao o nhap lieu
    function onKey(e) {
        if (!tabActive()) return;
        if (/^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || '')) return;
        if (e.key === 'ArrowRight' && idx >= 0 && idx < files.length - 1) { e.preventDefault(); show(idx + 1); }
        else if (e.key === 'ArrowLeft' && idx > 0) { e.preventDefault(); show(idx - 1); }
        else if ((e.key === 't' || e.key === 'T') && !e.ctrlKey && !e.metaKey && !e.altKey && idx >= 0) { e.preventDefault(); addToTrain(['char']); }
    }

    // ----- ve (anh chinh) ----------------------------------------------------------------------------
    function showEmpty(msg) {
        $('simEmpty').textContent = msg;
        $('simEmpty').style.display = '';
        canvas.style.display = 'none';
        $('simVehicles').innerHTML = '<div class="no-result">—</div>';
        cards.main.clear();
        $('simSummary').textContent = 'Chưa có kết quả.';
        $('simTotals').textContent = '';
        setStatus('');
    }

    function drawPlain() {
        if (!currentImg) return;
        canvas.width = currentImg.naturalWidth;
        canvas.height = currentImg.naturalHeight;
        ctx.drawImage(currentImg, 0, 0);
        $('simEmpty').style.display = 'none';
        canvas.style.display = '';
    }

    // ve khung xe / khung bien cua 1 ket qua len context g (kich thuoc anh W), he so quy doi sx, sy
    function paintOverlay(g, W, data, sx, sy) {
        const unit = Math.max(1, W / 900);
        const lw = Math.max(2, Math.round(2.5 * unit));
        const fs = Math.max(13, Math.round(15 * unit));
        data.vehicles.forEach(v => {
            const x1 = v.box.x1 * sx, y1 = v.box.y1 * sy, x2 = v.box.x2 * sx, y2 = v.box.y2 * sy;
            const hasPlate = !!v.plateNumber;
            const col = hasPlate ? '#22c55e' : '#f59e0b';
            if (!v.isRegion) {                               // "vung cat" = ca anh, khong ve khung xe
                g.lineWidth = lw;
                g.strokeStyle = col;
                g.strokeRect(x1, y1, x2 - x1, y2 - y1);
            }
            if (v.plateBox) {
                g.save();
                g.setLineDash([6 * unit, 4 * unit]);
                g.lineWidth = Math.max(2, Math.round(2 * unit));
                g.strokeStyle = '#38bdf8';
                g.strokeRect(v.plateBox.x1 * sx, v.plateBox.y1 * sy, (v.plateBox.x2 - v.plateBox.x1) * sx, (v.plateBox.y2 - v.plateBox.y1) * sy);
                g.restore();
            }
            const label = `${v.index}. ${v.plateNumber || (v.plateStage === 'unread' ? '— có vùng biển, chưa đọc được' : '— không dò được biển')}`;
            g.font = `bold ${fs}px sans-serif`;
            const padX = 6 * unit, h = fs + 8 * unit;
            const tw = g.measureText(label).width + 2 * padX;
            const lx = v.isRegion ? (v.plateBox ? v.plateBox.x1 * sx : 0) : x1;
            const ly0 = v.isRegion ? (v.plateBox ? v.plateBox.y1 * sy : 0) : y1;
            const ly = ly0 - h >= 0 ? ly0 - h : ly0;         // nhan nam tren khung; sat mep tren thi dat vao trong
            g.fillStyle = hasPlate ? '#16a34a' : '#b45309';
            g.fillRect(lx, ly, tw, h);
            g.fillStyle = '#fff';
            g.textBaseline = 'middle';
            g.fillText(label, lx + padX, ly + h / 2);
            g.textBaseline = 'alphabetic';
        });
    }

    // Ve lai CHI anh chinh (anh + khung + vung cat). Khong dung vao the xe / o nhap biển dang go -> an toan khi keo chuot cat vung.
    function paintMain() {
        if (!currentImg) return;
        drawPlain();
        const r = results.get(idx);
        if (r && r.ok) { const v = mainView(); paintOverlay(ctx, canvas.width, r.data, v.sx, v.sy); }
        drawCropOverlay();
    }

    function renderResult() {
        if (!currentImg) return;      // da giai phong (roi tab) — khong ve gi nua
        drawPlain();
        const r = results.get(idx);
        if (!r) { drawCropOverlay(); return; }
        if (!r.ok) {
            setStatus('');
            $('simSummary').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.message)}</span>`;
            $('simVehicles').innerHTML = '<div class="no-result">—</div>';
            cards.main.clear();
            updateTotals();
            return;
        }
        const d = r.data;
        const view = mainView();
        paintOverlay(ctx, canvas.width, d, view.sx, view.sy);
        drawCropOverlay();

        $('simSummary').innerHTML =
            `🚗 Phát hiện <strong>${d.vehicleCount}</strong> xe · 🔖 Đọc được biển: <strong>${d.plateCount}</strong>` +
            ` · ⏱ ${(d.elapsedMs / 1000).toFixed(1)} giây`;
        renderCards(view, d.vehicles, 'Không phát hiện xe nào trong ảnh này. Dùng ✂️ Cắt/Zoom để cắt riêng biển số rồi nhập biển đúng.');
        setStatus('');
        updateTotals();
    }

    // ===== ✂️ Cat / Zoom vung ==============================================================================================
    let cropMode = false;
    let cropDrag = null;       // { x0, y0, x1, y1 } theo toa do canvas (= pixel anh goc) khi dang quet
    let cropRect = null;       // { x, y, w, h } pixel anh goc
    let cropCanvas = null;     // canvas ngoai man hinh chua vung cat (do phan giai goc)
    let zoomAborter = null;
    let zoomResult = null;
    let redrawQueued = false;

    function setCropMode(on) {
        cropMode = on && !!currentImg;
        cropBtn.classList.toggle('primary', cropMode);
        cropBtn.classList.toggle('secondary', !cropMode);
        canvas.style.cursor = cropMode ? 'crosshair' : '';
    }
    cropBtn.addEventListener('click', () => setCropMode(!cropMode));

    function canvasPt(e) {
        const b = canvas.getBoundingClientRect();
        return [(e.clientX - b.left) * canvas.width / b.width, (e.clientY - b.top) * canvas.height / b.height];
    }
    canvas.addEventListener('mousedown', e => {
        if (!cropMode || !currentImg || e.button !== 0) return;
        const [x, y] = canvasPt(e);
        cropDrag = { x0: x, y0: y, x1: x, y1: y };
        if (autoOn) stopAuto('⏸ Tạm dừng tự chuyển (đang cắt vùng / thêm vào Train)');
        window.addEventListener('mousemove', onCropMove);     // chi gan trong luc keo, go ra ngay khi tha chuot
        window.addEventListener('mouseup', onCropUp);
        e.preventDefault();
    });
    function onCropMove(e) {
        if (!cropDrag) return;
        const [x, y] = canvasPt(e);
        cropDrag.x1 = Math.max(0, Math.min(canvas.width, x));
        cropDrag.y1 = Math.max(0, Math.min(canvas.height, y));
        if (!redrawQueued) { redrawQueued = true; requestAnimationFrame(() => { redrawQueued = false; paintMain(); }); }
    }
    function onCropUp() {
        window.removeEventListener('mousemove', onCropMove);
        window.removeEventListener('mouseup', onCropUp);
        if (!cropDrag) return;
        const d = cropDrag; cropDrag = null;
        const x = Math.round(Math.min(d.x0, d.x1)), y = Math.round(Math.min(d.y0, d.y1));
        const w = Math.round(Math.abs(d.x1 - d.x0)), h = Math.round(Math.abs(d.y1 - d.y0));
        if (w >= 12 && h >= 8) openZoom({ x, y, w, h });
        else { paintMain(); scheduleAuto(); }
    }

    function drawCropOverlay() {
        const r = cropDrag
            ? { x: Math.min(cropDrag.x0, cropDrag.x1), y: Math.min(cropDrag.y0, cropDrag.y1), w: Math.abs(cropDrag.x1 - cropDrag.x0), h: Math.abs(cropDrag.y1 - cropDrag.y0) }
            : cropRect;
        if (!r) return;
        const unit = Math.max(1, canvas.width / 900);
        ctx.save();
        ctx.fillStyle = 'rgba(0,0,0,.35)';
        ctx.fillRect(0, 0, canvas.width, r.y);
        ctx.fillRect(0, r.y + r.h, canvas.width, canvas.height - r.y - r.h);
        ctx.fillRect(0, r.y, r.x, r.h);
        ctx.fillRect(r.x + r.w, r.y, canvas.width - r.x - r.w, r.h);
        ctx.setLineDash([8 * unit, 5 * unit]);
        ctx.lineWidth = Math.max(2, Math.round(2.5 * unit));
        ctx.strokeStyle = '#facc15';
        ctx.strokeRect(r.x, r.y, r.w, r.h);
        ctx.restore();
    }

    function openZoom(rect) {
        cropRect = rect;
        zoomResult = null;
        cropCanvas = document.createElement('canvas');
        cropCanvas.width = rect.w; cropCanvas.height = rect.h;
        cropCanvas.getContext('2d').drawImage(currentImg, rect.x, rect.y, rect.w, rect.h, 0, 0, rect.w, rect.h);
        $('simZoomPanel').style.display = '';
        $('simZoomInfo').textContent = `${rect.w}×${rect.h} px`;
        $('simZoomStatus').textContent = '';
        drawZoom();
        renderZoomRows();
        paintMain();                                      // ve khung vung cat tren anh chinh (khong ve lai the xe)
        $('simZoomPanel').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }

    function drawZoom() {
        if (!cropCanvas) return;
        zoomCanvas.width = cropCanvas.width; zoomCanvas.height = cropCanvas.height;
        zctx.drawImage(cropCanvas, 0, 0);
        if (zoomResult) paintOverlay(zctx, zoomCanvas.width, zoomResult, 1, 1);
        // phong to: vung nho hien it nhat ~480px rong (toi da 900px)
        zoomCanvas.style.width = Math.min(900, Math.max(zoomCanvas.width, 480)) + 'px';
        zoomCanvas.style.maxWidth = '100%';
    }

    function renderZoomRows() {
        if (!cropCanvas) return;
        const view = zoomView();
        view.recognized = !!zoomResult;
        const list = zoomResult && zoomResult.vehicles.length
            ? zoomResult.vehicles
            : [{ index: 1, isRegion: true, vehicleType: 'Region', confidence: 1, plateNumber: null, plateBox: null }];
        renderCards(view, list, '');
    }

    async function zoomAnalyze(mode) {
        if (!cropCanvas) return;
        if (zoomAborter) zoomAborter.abort();
        zoomAborter = new AbortController();
        const my = zoomAborter;
        $('simZoomStatus').textContent = '⏳ Đang nhận diện vùng… (nhường camera thật nếu hệ thống đang bận)';
        const blob = await new Promise(res => cropCanvas.toBlob(res, 'image/png'));
        const form = new FormData();
        form.append('image', blob, 'vung-cat.png');
        const conf = parseFloat($('simConf').value);
        const qs = [`mode=${mode}`].concat(conf > 0 ? [`conf=${conf}`] : []).join('&');
        try {
            const res = await fetch(`${API}/analyze?${qs}`, { method: 'POST', body: form, signal: my.signal });
            const body = await res.json().catch(() => ({}));
            if (my.signal.aborted || !cropCanvas) return;
            if (!res.ok) { $('simZoomStatus').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(body.message || 'Lỗi ' + res.status)}</span>`; return; }
            zoomResult = body;
            $('simZoomStatus').textContent = `🔖 Đọc được biển: ${body.plateCount} · ⏱ ${(body.elapsedMs / 1000).toFixed(1)} giây`;
            drawZoom();
            renderZoomRows();
        } catch (err) {
            if (err.name !== 'AbortError') $('simZoomStatus').textContent = 'Không gọi được máy chủ: ' + err.message;
        }
    }
    $('simZoomDetectBtn').addEventListener('click', () => zoomAnalyze('region'));
    $('simZoomPlateBtn').addEventListener('click', () => zoomAnalyze('plate'));
    $('simZoomCloseBtn').addEventListener('click', () => { clearCrop(); paintMain(); scheduleAuto(); });

    function clearCrop() {
        if (zoomAborter) { zoomAborter.abort(); zoomAborter = null; }
        window.removeEventListener('mousemove', onCropMove);
        window.removeEventListener('mouseup', onCropUp);
        cropDrag = null; cropRect = null; cropCanvas = null; zoomResult = null;
        zoomCanvas.width = 0; zoomCanvas.height = 0;       // tra bo nho dem cua canvas zoom
        cards.zoom.clear();
        $('simZoomRows').innerHTML = '';
        $('simZoomPanel').style.display = 'none';
    }

    // ----- danh sach / tien do -----------------------------------------------------------------
    function renderFileList() {
        const box = $('simFileList');
        $('simFileCount').textContent = files.length ? `(${files.length} ảnh)` : '';
        if (!files.length) { box.innerHTML = '<div class="no-result">Chưa có ảnh.</div>'; return; }
        box.innerHTML = files.map((f, i) => {
            const r = results.get(i);
            const st = !r ? '⏳ chưa nhận diện'
                : !r.ok ? '⚠️ lỗi'
                : `✅ ${r.data.vehicleCount} xe · ${r.data.plateCount} biển`;
            const mark = addedAny(i) ? ` · 🧠 đã thêm Train (${TRAIN_KINDS.filter(k => added[k].has(i)).map(k => TRAIN_LABEL[k]).join(', ')})` : '';
            return `<div class="sim-file${i === idx ? ' active' : ''}" data-i="${i}">
                <span class="sim-no">${i + 1}</span><span class="sim-name" title="${esc(relName(f))}">${esc(relName(f))}</span><span class="sim-state">${st}${mark}</span></div>`;
        }).join('');
        const active = box.querySelector('.sim-file.active');
        if (active) active.scrollIntoView({ block: 'nearest' });
    }
    $('simFileList').addEventListener('click', e => {
        const row = e.target.closest('.sim-file');
        if (row) show(+row.dataset.i);
    });

    function updateTotals() {
        let done = 0, vehicles = 0, plates = 0;
        results.forEach(r => { if (r.ok) { done++; vehicles += r.data.vehicleCount; plates += r.data.plateCount; } });
        $('simTotals').textContent = files.length
            ? `Đã nhận diện ${done}/${files.length} ảnh · tổng ${vehicles} xe · ${plates} biển số đọc được.` : '';
    }

    function setProgress(msg) {
        $('simProgress').textContent = msg ?? (idx >= 0
            ? `Ảnh ${idx + 1}/${files.length} — ${relName(files[idx])}${idx === files.length - 1 ? ' (ảnh cuối)' : ''}`
            : 'Chưa chọn thư mục.');
    }
    function setStatus(msg) { $('simStatus').textContent = msg || ''; }

    // ----- 🔎 Soi anh (simInspector.js): phong to / keo / cat vung / chinh sang de doi chieu bien voi chuoi doc duoc ------------------
    function inspectorInput(viewName) {
        const zoom = viewName === 'zoom';
        const src = zoom ? cropCanvas : currentImg;
        const data = zoom ? zoomResult : (results.get(idx)?.ok ? results.get(idx).data : null);
        if (!src || idx < 0) return null;
        const mv = zoom ? { sx: 1, sy: 1 } : mainView();
        const rect = b => ({ x: b.x1 * mv.sx, y: b.y1 * mv.sy, w: (b.x2 - b.x1) * mv.sx, h: (b.y2 - b.y1) * mv.sy });
        const boxes = [], targets = [];
        (data?.vehicles || []).forEach(v => {
            const label = `${v.index}. ` + (v.plateNumber || (v.plateStage === 'unread' ? 'chưa đọc được' : 'không dò được biển'));
            if (v.box && !v.isRegion) boxes.push({ ...rect(v.box), color: v.plateNumber ? '#22c55e' : '#f59e0b', label });
            if (v.plateBox) boxes.push({ ...rect(v.plateBox), color: '#38bdf8', dashed: true, label: (v.isRegion || !v.box) ? label : '' });
            const r = v.plateBox || v.box;
            if (!r) return;
            targets.push({ title: `#${v.index} ${TYPE_VI[v.vehicleType] || v.vehicleType}`, rect: rect(r), hasPlateBox: !!v.plateBox,
                           text: v.plateNumber || '', conf: v.plateNumber ? v.plateConfidence : null, stage: v.plateStage, index: v.index });
        });
        const name = relName(files[idx]);
        return { source: src, title: zoom ? `${name} (vùng cắt)` : name, boxes, targets };
    }
    function openInspector(viewName, vehicleIndex, focus) {
        if (!window.SimInspector) return;
        const inp = inspectorInput(viewName);
        if (!inp) return;
        const start = vehicleIndex == null ? -1 : inp.targets.findIndex(t => t.index === vehicleIndex);
        window.SimInspector.open({ ...inp, start, focus: start < 0 ? focus : undefined });
    }
    if (inspectBtn) inspectBtn.addEventListener('click', () => openInspector('main'));
    ['simVehicles', 'simZoomRows'].forEach(id => $(id).addEventListener('click', e => {
        const b = e.target.closest('.sim-inspect-btn');
        if (b) openInspector(b.dataset.view, Number(b.dataset.vi));
    }));
    canvas.addEventListener('dblclick', e => {            // nhap dup vao anh: mo trinh soi, phong 4 lan tai diem do
        if (cropMode || !currentImg || idx < 0) return;
        const r = canvas.getBoundingClientRect();
        openInspector('main', null, { x: (e.clientX - r.left) * currentImg.naturalWidth / r.width, y: (e.clientY - r.top) * currentImg.naturalHeight / r.height, scale: 4 });
    });
    zoomCanvas.addEventListener('dblclick', () => { if (cropCanvas) openInspector('zoom'); });

    function updateButtons() {
        prevBtns.forEach(b => { b.disabled = idx <= 0; });
        nextBtns.forEach(b => { b.disabled = idx < 0 || idx >= files.length - 1; });
        redoBtn.disabled = idx < 0 || busy;
        cropBtn.disabled = !currentImg || idx < 0;
        if (inspectBtn) inspectBtn.disabled = !currentImg || idx < 0;
        autoBtn.disabled = idx < 0;
        const trainOff = idx < 0 || !currentImg || addingToTrain;
        for (const k of TRAIN_KINDS) {
            trainBtns[k].disabled = trainOff;
            trainBtns[k].textContent = (added[k].has(idx) ? '✔ ' : '➕ ') + TRAIN_BTN_TEXT[k];
        }
        trainBtns.all.disabled = trainOff;
        trainBtns.all.textContent = '➕ ' + TRAIN_BTN_TEXT.all;
        updateTotals();
    }

    // ----- canh bao model chua nap ---------------------------------------------------------------
    async function checkModel() {
        const warn = $('simModelWarn');
        try {
            const s = await (await fetch(`${API}/status`)).json();
            if (!s.detectorReady) {
                warn.style.display = '';
                warn.innerHTML = '⚠️ Model AI nhận diện xe chưa được nạp' + (s.loadError ? `: ${esc(s.loadError)}` : '.') +
                    ` Mô phỏng sẽ báo lỗi cho tới khi model nạp được (${esc(s.modelPath || '')}).`;
            } else if (!s.anprReady) {
                warn.style.display = '';
                warn.textContent = '⚠️ Bộ đọc biển số chưa sẵn sàng — xe vẫn được nhận diện nhưng có thể không đọc được biển.';
            } else {
                warn.style.display = 'none';
            }
        } catch { /* bo qua: loi mang se hien khi nhan dien */ }
    }

    // ----- giai phong tai nguyen ------------------------------------------------------------------------------------
    // Quy tac: KHONG o tab Mo phong = khong ton gi: khong gui yeu cau nao toi may chu, khong dem gio, khong hoi tien do train,
    // khong giu anh/canvas/vung cat, khong lang nghe ban phim. (Neu dang train thi tien trinh train van chay nen; quay lai tab
    // se thay tien do.) Quay lai tab: nap lai anh dang xem tu file; ket qua da co duoc giu, khong chay lai AI.
    function release() {
        autoOn = false; refreshAutoBtn();                  // roi tab = tat che do tu dong (khong tu chay AI khi quay lai)
        stopAuto();                                        // dong ho dem + nhan dem nguoc
        if (aborter) { aborter.abort(); aborter = null; }  // huy yeu cau dang cho: may chu go viec CHUA chay khoi hang doi
        busy = false;
        clearCrop();
        setCropMode(false);
        if (currentImg) { currentImg.onload = currentImg.onerror = null; currentImg.src = ''; currentImg = null; }
        canvas.width = 0; canvas.height = 0;               // tra bo nho dem cua canvas (anh goc co the vai chuc MB)
        canvas.style.display = 'none';
        if (imgUrl) { URL.revokeObjectURL(imgUrl); imgUrl = null; }
        cards.main.clear(); cards.zoom.clear();
        $('simVehicles').innerHTML = '';
        setStatus('');
    }

    function clearAll() {                                  // nut "Xoa danh sach": quen luon thu muc + ket qua
        release();
        files = []; idx = -1; results.clear(); TRAIN_KINDS.forEach(k => added[k].clear());
        renderFileList();
        showEmpty('Chọn thư mục ảnh để bắt đầu.');
        setProgress('Chưa chọn thư mục.');
        updateButtons();
    }
    $('simClearBtn').addEventListener('click', clearAll);

    function enter() {
        if (entered || !tabActive()) return;
        entered = true;
        document.addEventListener('keydown', onKey);
        checkModel();
        if ((window.pgModelEpoch || 0) !== epochSeen) {   // tab Train vua cai model moi -> ket qua nhan dien cu la cua model cu: bo de nhan dien lai
            epochSeen = window.pgModelEpoch || 0;
            results.clear(); renderFileList(); updateTotals();
            setStatus('🔄 Model nhận diện ký tự vừa được cập nhật ở tab Train — kết quả cũ đã xoá, đang nhận diện lại bằng model mới.');
        }
        if (idx >= 0 && files.length) show(idx);           // nap lai anh dang xem (ket qua cu con thi khong goi may chu)
    }
    function leave() {
        if (!entered) return;
        entered = false;
        document.removeEventListener('keydown', onKey);
        release();
    }
    document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => {
        if (b.dataset.tab === 'sim') setTimeout(enter, 0);   // cho listener doi tab (systemCameras.js) bat class 'active' truoc
        else leave();
    }));
    // trinh duyet bi an / thu nho: tam dung dem gio (khong goi may chu khi khong ai nhin); hien lai thi dem lai
    document.addEventListener('visibilitychange', () => { if (document.hidden) stopAuto(); else if (entered) scheduleAuto(); });
    window.addEventListener('pagehide', release);
})();
