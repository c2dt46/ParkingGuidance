// ---------------------------------------------------------------------------
// Export / Import dữ liệu train (Train ký tự · Train biển số · Train xe) ra / vào một THƯ MỤC.
//   • Nút "Export..." ở tab lớn 🧠 Train  → hộp thoại chọn phần cần xuất + thư mục đích.
//   • Nút "Import..." ở tab 🧪 Mô phỏng   → hộp thoại chọn thư mục gói train + phần cần nhập.
// Thư mục nằm trên MÁY CHẠY ỨNG DỤNG (máy chủ), nên hộp thoại có bộ duyệt thư mục lấy từ API /api/system/train-transfer/browse.
// Import là GỘP (mục đã có được giữ nguyên, chỉ thêm mục mới). Import xong bắn sự kiện 'pg:train-data-imported' để các tab Train tải lại danh sách.
// ---------------------------------------------------------------------------
(function () {
    const API = '/api/system/train-transfer';
    const KINDS = [
        { key: 'char', icon: '🔤', label: 'Train ký tự', desc: 'mẫu ký tự đã gán + ảnh trong hàng chờ' },
        { key: 'plate', icon: '🔖', label: 'Train biển số', desc: 'ảnh + khung biển số' },
        { key: 'vehicle', icon: '🚗', label: 'Train xe', desc: 'ảnh + khung xe' }
    ];
    const LS = { export: 'pg.train.exportFolder', import: 'pg.train.importFolder' };

    async function jfetch(url, opts) {
        try {
            const res = await fetch(url, opts);
            const body = await res.json().catch(() => ({}));
            return { ok: res.ok, status: res.status, body };
        } catch (e) { return { ok: false, status: 0, body: { message: e.message } }; }
    }
    const post = (url, data) => jfetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });

    function h(tag, attrs, ...kids) {
        const el = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs || {})) {
            if (k === 'class') el.className = v;
            else if (k === 'text') el.textContent = v;
            else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
            else if (v === true) el.setAttribute(k, '');
            else if (v !== false && v != null) el.setAttribute(k, v);
        }
        kids.flat().forEach(c => { if (c != null) el.append(c); });
        return el;
    }
    const lsGet = k => { try { return localStorage.getItem(k) || ''; } catch { return ''; } };
    const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* bo qua */ } };

    let open = null;      // hộp thoại đang mở (chỉ 1)

    function openDialog(mode) {
        if (open) return;
        const isExport = mode === 'export';
        let info = {};                          // key -> KindInfo (số liệu hiện có / trong gói)
        let browsePath = '';
        let busy = false;

        // ---------- khung
        const pathInput = h('input', { type: 'text', class: 'tt-path', spellcheck: 'false', autocomplete: 'off',
            placeholder: isExport ? 'Thư mục sẽ chứa gói export, vd D:\\Backup\\Train' : 'Thư mục gói train (ParkingGuidance-train-…)' });
        const browseBtn = h('button', { type: 'button', class: 'btn secondary btn-sm', text: 'Duyệt…' });
        const browser = h('div', { class: 'tt-browser', hidden: true });
        const kindsBox = h('div', { class: 'tt-kinds' });
        const modelChk = h('input', { type: 'checkbox', id: 'ttModel' });
        const modelLbl = h('label', { class: 'checkbox-label tt-model', for: 'ttModel' }, modelChk,
            isExport ? ' Kèm model đã train (.onnx)' : ' Thay model đang chạy bằng model trong gói (bản cũ được sao lưu .bak-…)');
        const hint = h('div', { class: 'hint small tt-hint' });
        const result = h('div', { class: 'tt-result' });
        const goBtn = h('button', { type: 'button', class: 'btn primary btn-sm', text: isExport ? 'Export' : 'Import' });
        const closeBtn = h('button', { type: 'button', class: 'btn secondary btn-sm', text: 'Đóng' });

        const box = h('div', { class: 'tt-box', role: 'dialog', 'aria-modal': 'true' },
            h('div', { class: 'tt-head' }, h('b', { text: isExport ? 'Export dữ liệu train' : 'Import dữ liệu train' }),
                h('button', { type: 'button', class: 'btn secondary btn-sm', 'aria-label': 'Đóng', text: '✕', onclick: close })),
            h('div', { class: 'hint small tt-hint', text: isExport
                ? 'Xuất dữ liệu train ra một thư mục (trên máy chạy ứng dụng) để sao lưu hoặc mang sang máy khác. Ứng dụng tạo thư mục con “ParkingGuidance-train-<ngày giờ>” trong thư mục bạn chọn.'
                : 'Nhập dữ liệu train từ một gói do Export tạo ra. Nhập là GỘP: mục đã có giữ nguyên, chỉ thêm mục mới; không xoá gì của bạn.' }),
            h('div', { class: 'tt-label', text: isExport ? '1. Thư mục đích' : '1. Thư mục gói train' }),
            h('div', { class: 'tt-row' }, pathInput, browseBtn),
            browser,
            h('div', { class: 'tt-label', text: '2. Chọn phần' }),
            kindsBox, modelLbl, hint, result,
            h('div', { class: 'tt-foot' }, goBtn, closeBtn));
        const back = h('div', { class: 'tt-backdrop' }, box);
        back.addEventListener('mousedown', e => { if (e.target === back) close(); });
        document.body.append(back);
        const onKey = e => { if (e.key === 'Escape' && !busy) close(); };
        document.addEventListener('keydown', onKey);
        open = { close };

        function close() {
            if (busy) return;
            document.removeEventListener('keydown', onKey);
            back.remove(); open = null;
        }
        closeBtn.addEventListener('click', close);

        const setResult = (ok, lines, msg) => {
            result.replaceChildren();
            result.className = 'tt-result' + (ok === false ? ' bad' : ok ? ' good' : '');
            if (msg) result.append(h('div', { class: 'tt-msg', text: msg }));
            (lines || []).forEach(l => result.append(h('div', { text: l })));
        };

        // ---------- danh sách phần (checkbox)
        const checked = () => KINDS.map(k => k.key).filter(k => kindsBox.querySelector(`input[data-k="${k}"]`)?.checked);   // ô bị khoá thì luôn bỏ chọn
        function kindText(k) {
            const d = info[k.key]; if (!d) return '';
            const parts = [];
            if (k.key === 'char') parts.push(`${d.corrections} mẫu ký tự`, `${d.inbox} ảnh hàng chờ`);
            else parts.push(`${d.items} ảnh`);
            if (!isExport && d.newItems != null) parts.push(`${d.newItems} mục mới`);
            if (d.hasModel) parts.push('có model');
            return parts.join(' · ');
        }
        function renderKinds() {
            const prev = {};
            kindsBox.querySelectorAll('input[data-k]').forEach(i => { if (!i.disabled) prev[i.dataset.k] = i.checked; });   // chỉ nhớ ô người dùng thật sự bấm được
            kindsBox.replaceChildren();
            KINDS.forEach(k => {
                const d = info[k.key];
                const usable = !!d && d.available && (d.items > 0 || d.hasModel);
                const cb = h('input', { type: 'checkbox', 'data-k': k.key, disabled: !usable });
                cb.checked = usable && (k.key in prev ? prev[k.key] : true);
                cb.addEventListener('change', updateGo);
                kindsBox.append(h('label', { class: 'tt-kind' + (usable ? '' : ' off') }, cb,
                    h('span', { class: 'tt-kind-name', text: `${k.icon} ${k.label}` }),
                    h('span', { class: 'hint small tt-kind-desc', text: d ? (usable ? kindText(k) : (isExport ? 'chưa có dữ liệu' : 'không có trong gói')) : k.desc })));
            });
            updateGo();
        }
        function updateGo() {
            const sel = checked();
            const anyModel = sel.some(k => info[k]?.hasModel);
            modelChk.disabled = !anyModel;
            if (!anyModel) modelChk.checked = false;
            goBtn.disabled = busy || sel.length === 0 || !pathInput.value.trim();
        }
        pathInput.addEventListener('input', updateGo);

        // ---------- nạp số liệu
        async function loadExportSummary() {
            const r = await jfetch(`${API}/summary`);
            if (!r.ok) { setResult(false, [], r.body.message || 'Không đọc được số liệu train.'); return; }
            info = Object.fromEntries((r.body.kinds || []).map(k => [(k.kind || k.Kind), norm(k)]));
            if (!pathInput.value) pathInput.value = lsGet(LS.export) || r.body.defaultFolder || '';
            modelChk.checked = true;
            renderKinds();
        }
        const norm = k => ({ available: k.available ?? k.Available, items: k.items ?? k.Items ?? 0, newItems: k.newItems ?? k.NewItems ?? null,
            corrections: k.corrections ?? k.Corrections ?? 0, inbox: k.inbox ?? k.Inbox ?? 0, hasModel: k.hasModel ?? k.HasModel ?? false });
        let inspectSeq = 0;
        async function inspect(keepResult) {
            const folder = pathInput.value.trim(); const seq = ++inspectSeq;
            info = {}; renderKinds();
            if (!folder) { hint.textContent = ''; return; }
            hint.textContent = 'Đang đọc gói…';
            const r = await jfetch(`${API}/inspect?folder=${encodeURIComponent(folder)}`);
            if (seq !== inspectSeq) return;
            if (!r.ok) { hint.textContent = ''; setResult(false, [], r.body.message || 'Không đọc được gói train.'); return; }
            if (keepResult !== true) { result.replaceChildren(); result.className = 'tt-result'; }
            const when = r.body.createdAt ? new Date(r.body.createdAt).toLocaleString() : '';
            hint.textContent = when ? `Gói tạo lúc ${when}.` : '';
            info = Object.fromEntries((r.body.kinds || []).map(k => [(k.kind || k.Kind), norm(k)]));
            modelChk.checked = false;
            renderKinds();
        }
        if (!isExport) pathInput.addEventListener('change', () => inspect(false));

        // ---------- duyệt thư mục
        async function showBrowse(path) {
            const r = await jfetch(`${API}/browse?path=${encodeURIComponent(path || '')}`);
            browser.replaceChildren();
            if (!r.ok) { browser.append(h('div', { class: 'tt-msg bad', text: r.body.message || 'Không mở được thư mục.' })); return; }
            browsePath = r.body.path || '';
            const list = h('div', { class: 'tt-list' });
            (r.body.dirs || []).forEach(d => list.append(h('button', { type: 'button', class: 'tt-dir' + (d.isPackage ? ' pkg' : ''),
                text: (d.isPackage ? '📦 ' : '📁 ') + d.name, onclick: () => showBrowse(d.path) })));
            if (!(r.body.dirs || []).length) list.append(h('div', { class: 'hint small', text: '(không có thư mục con)' }));
            const upBtn = h('button', { type: 'button', class: 'btn secondary btn-sm', text: '⬆ Lên', disabled: !browsePath, onclick: () => showBrowse(r.body.parent || '') });
            const pickBtn = h('button', { type: 'button', class: 'btn primary btn-sm', text: '✔ Chọn thư mục này', disabled: !browsePath, onclick: () => {
                pathInput.value = browsePath; browser.hidden = true; if (isExport) updateGo(); else inspect(false); } });
            const bar = h('div', { class: 'tt-bar' }, upBtn, h('span', { class: 'tt-cur', text: browsePath || 'Ổ đĩa / thư mục thường dùng', title: browsePath }), pickBtn);
            if (isExport) bar.insertBefore(h('button', { type: 'button', class: 'btn secondary btn-sm', text: '＋ Thư mục mới', disabled: !browsePath, onclick: async () => {
                const name = prompt('Tên thư mục mới:'); if (!name) return;
                const m = await post(`${API}/mkdir`, { parent: browsePath, name });
                if (!m.ok) { alert(m.body.message || 'Không tạo được thư mục.'); return; }
                showBrowse(m.body.path);
            } }), pickBtn);
            browser.append(bar, list);
            if (r.body.isPackage) browser.append(h('div', { class: 'hint small', text: '📦 Thư mục này là một gói train — bấm “Chọn thư mục này” để import.' }));
        }
        browseBtn.addEventListener('click', () => {
            browser.hidden = !browser.hidden;
            if (!browser.hidden) showBrowse(pathInput.value.trim());
        });

        // ---------- thực hiện
        goBtn.addEventListener('click', async () => {
            const folder = pathInput.value.trim(); const sel = checked();
            if (!folder || !sel.length) return;
            if (!isExport && modelChk.checked && !confirm('Model đang chạy sẽ được thay bằng model trong gói (bản cũ được sao lưu). Tiếp tục?')) return;
            busy = true; goBtn.disabled = true; closeBtn.disabled = true;
            setResult(null, [], isExport ? '⏳ Đang export… (nhiều ảnh có thể mất ít phút)' : '⏳ Đang import…');
            const r = await post(`${API}/${isExport ? 'export' : 'import'}`, { folder, kinds: sel, includeModels: modelChk.checked });
            busy = false; closeBtn.disabled = false;
            const b = r.body || {};
            setResult(r.ok, b.lines || b.Lines || [], b.message || b.Message || (r.ok ? 'Xong.' : 'Thất bại.'));
            if (r.ok) {
                lsSet(isExport ? LS.export : LS.import, isExport ? folder : (b.folder || b.Folder || folder));
                if (!isExport) { document.dispatchEvent(new CustomEvent('pg:train-data-imported', { detail: { kinds: sel } })); inspect(true); }
            }
            updateGo();
        });

        // ---------- khởi tạo
        if (isExport) { pathInput.value = lsGet(LS.export); renderKinds(); loadExportSummary(); }
        else { pathInput.value = lsGet(LS.import); renderKinds(); if (pathInput.value) inspect(false); }
        pathInput.focus();
    }

    window.pgTrainTransfer = { openExport: () => openDialog('export'), openImport: () => openDialog('import') };
    document.getElementById('trExportBtn')?.addEventListener('click', () => openDialog('export'));
    document.getElementById('simImportBtn')?.addEventListener('click', () => openDialog('import'));
})();
