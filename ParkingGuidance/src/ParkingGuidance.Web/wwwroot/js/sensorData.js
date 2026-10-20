// ---------------------------------------------------------------------------
// Trang Cam bien › tab "Du lieu": bien MS. Moi bien chi dem MOT trang thai (Co cho do = Trong · Khong co cho do = Co xe · Loi)
// tren cac cam bien duoc chi dinh. Gia tri do may chu tinh tu trang thai cam bien that.
// Tiet kiem tai nguyen: chi hoi may chu moi giay khi dang xem tab nay (va trinh duyet dang hien); roi tab thi dung han.
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/sensors/variables';
    const LABEL = { Free: 'Có chỗ đỗ', Occupied: 'Không có chỗ đỗ', Error: 'Lỗi' };
    const labelOf = c => LABEL[c] || 'Chưa chọn';
    const SUB = { Free: 'cảm biến Trống', Occupied: 'cảm biến Có xe', Error: 'cảm biến Lỗi' };
    const CLS = { Free: 's0', Occupied: 's1', Error: 's2' };
    const subOf = c => (SUB[c] ? ` <span class="dv-sub" style="display:inline">(${SUB[c]})</span>` : '');
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const typeName = t => (t === 'Keytop' ? 'Keytop' : 'MGT200');

    let rows = [];
    let sel = null;                 // ten bien dang chon trong bang
    let sensorType = 'Pm200';       // loai cam bien dang dung
    let keytopCount = 1;
    let timer = null;

    // ----------------------------------------------------------------- bang danh sach
    async function refresh() {
        try {
            const res = await fetch(API);
            if (!res.ok) return;
            const d = await res.json();
            sensorType = d.sensorType || 'Pm200';
            keytopCount = d.keytopCount || 1;
            rows = d.variables || [];
            renderTable();
        } catch { /* mat ket noi tam thoi: lan hoi sau se duoc */ }
    }

    function renderTable() {
        if (sel && !rows.some(r => r.name === sel)) sel = null;
        const body = $('dvBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="7" class="hint small">Chưa có biến nào — bấm "Thêm biến".</td></tr>';
        } else {
            body.innerHTML = rows.map((r, i) => {
                const val = r.valid
                    ? `<span class="dv-val ${CLS[r.count] || ''}">${r.value}</span><span class="dv-sub">trên ${r.selected} cảm biến` +
                      `${r.unknown ? ` · ${r.unknown} chưa có dữ liệu` : ''}${r.inactive ? ` · ${r.inactive} ngoài vùng quét` : ''}</span>`
                    : `<span class="dv-val">—</span><span class="dv-sub">thuộc ${typeName(r.source)} — đang dùng ${typeName(sensorType)}</span>`;
                const valCell = r.valid && !r.count
                    ? '<span class="dv-val">—</span><span class="dv-sub">chưa chọn trạng thái đếm</span>' : val;
                return `<tr data-name="${esc(r.name)}" class="${r.name === sel ? 'sel' : ''}">
                    <td>${i}</td><td><b>${esc(r.name)}</b></td><td>MS</td>
                    <td>${labelOf(r.count)}${subOf(r.count)}</td>
                    <td>${esc(r.devices) || '<span class="dv-sub">chưa chọn cảm biến</span>'}</td><td>${valCell}</td><td>${esc(r.description)}</td></tr>`;
            }).join('');
        }
        $('dvEdit').disabled = !sel;
        $('dvDel').disabled = !sel;
    }

    $('dvBody').addEventListener('click', e => {
        const tr = e.target.closest('tr[data-name]'); if (!tr) return;
        sel = tr.dataset.name; renderTable();
    });
    $('dvBody').addEventListener('dblclick', e => {
        const tr = e.target.closest('tr[data-name]'); if (!tr) return;
        sel = tr.dataset.name; renderTable(); openEditor(sel);
    });
    $('dvAdd').addEventListener('click', async () => {
        // Thêm ngay 1 biến MS (tên tự đặt MS0, MS1… lấp chỗ trống nhỏ nhất) — không mở form.
        $('dvAdd').disabled = true;
        try {
            const res = await fetch(`${API}/add`, { method: 'POST' });
            const data = await res.json().catch(() => ({}));
            if (!res.ok) { $('dvState').textContent = data.message || 'Không thêm được biến.'; return; }
            sel = data.name;
            $('dvState').textContent = `Đã thêm biến ${data.name}.`;
            await refresh();
        } catch { $('dvState').textContent = 'Không thêm được biến.'; }
        finally { $('dvAdd').disabled = false; }
    });
    $('dvEdit').addEventListener('click', () => { if (sel) openEditor(sel); });
    $('dvDel').addEventListener('click', async () => {
        if (!sel) return;
        if (!confirm(`Xoá biến ${sel}?`)) return;
        const res = await fetch(`${API}/${encodeURIComponent(sel)}`, { method: 'DELETE' });
        $('dvState').textContent = res.ok ? `Đã xoá biến ${sel}.` : 'Không xoá được.';
        sel = null;
        await refresh();
    });

    // ----------------------------------------------------------------- bo chon cam bien
    // edit = { original, source, dev, mask: Map<so thiet bi, Uint8Array(per)> }
    let edit = null;
    let painting = null;            // { val } khi dang keo chuot de chon / bo chon
    let anchor = null;              // { dev, i } o bam gan nhat — moc cho Shift+bam chon ca doan

    const geo = source => (source === 'Keytop' ? { per: 60, lines: 0 } : { per: 160, lines: 5 });

    function deviceCount() {
        if (edit.source !== 'Keytop') return 32;
        let top = keytopCount;
        edit.mask.forEach((m, d) => { if (d + 1 > top) top = d + 1; });
        return Math.max(1, top);
    }
    function maskFor(dev) {
        let m = edit.mask.get(dev);
        if (!m) { m = new Uint8Array(geo(edit.source).per); edit.mask.set(dev, m); }
        return m;
    }
    const countOf = m => { let n = 0; for (const b of m) n += b; return n; };
    const countDev = dev => { const m = edit.mask.get(dev); return m ? countOf(m) : 0; };

    async function openEditor(name) {
        if (!name) return;
        let v = null;
        try {
            const res = await fetch(`${API}/${encodeURIComponent(name)}`);
            if (!res.ok) { $('dvState').textContent = 'Không đọc được biến này.'; return; }
            v = await res.json();
        } catch { $('dvState').textContent = 'Không đọc được biến này.'; return; }
        anchor = null;
        edit = { original: v.name, source: v.source, dev: 0, mask: new Map(), locked: !!v.count };
        v.selection.forEach(p => edit.mask.set(p.device, Uint8Array.from(p.mask, c => (c === '1' ? 1 : 0))));
        if (v.selection.length) edit.dev = v.selection[0].device;

        $('dvFormTitle').textContent = `Sửa biến ${v.name} (cảm biến ${typeName(edit.source)})`;
        // Mỗi biến chỉ đếm 1 trạng thái: đã xác định thì khoá ô chọn trạng thái.
        $('DvCount').value = v.count || '';
        $('DvCount').disabled = edit.locked;
        $('DvDesc').value = v.description;
        $('dvFormState').textContent = '';
        $('dvForm').style.display = '';
        renderAll();
    }

    function closeEditor() {
        edit = null; painting = null; anchor = null;
        $('dvForm').style.display = 'none';
        $('dvGrid').innerHTML = ''; $('dvDevices').innerHTML = '';     // giai phong DOM cua bo chon
    }

    // mau o duoc chon theo trang thai dang dem: Co xe = do, Trong = xanh la, Loi = vang; chua chon trang thai = xanh duong
    const stClass = () => { const v = $('DvCount').value; return v ? ' st-' + v : ''; };
    function paintLegend() {
        const v = $('DvCount').value;
        document.querySelectorAll('#dvLegend span[data-st]').forEach(s => s.classList.toggle('cur', s.dataset.st === v));
    }
    $('DvCount').addEventListener('change', () => { if (edit) { renderGrid(); paintLegend(); } });

    function renderAll() { renderDevices(); renderGrid(); renderSummary(); paintLegend(); }

    function renderDevices() {
        const n = deviceCount();
        if (edit.dev >= n) edit.dev = 0;
        const box = $('dvDevices');
        box.className = 'sn-devices' + (edit.source === 'Keytop' ? '' : ' pm');
        let h = '';
        for (let d = 0; d < n; d++) {
            const c = countDev(d);
            h += `<button type="button" class="sn-dev${d === edit.dev ? ' sel' : ''}${c ? ' has' : ''}" data-d="${d}" title="ZCU ${d}: ${c} cảm biến được chọn">${d}</button>`;
        }
        box.innerHTML = h;
        $('dvDevTitle').textContent = `ZCU ${edit.dev} — ${countDev(edit.dev)}/${geo(edit.source).per} cảm biến được chọn`;
    }

    function renderGrid() {
        const g = geo(edit.source), m = maskFor(edit.dev), grid = $('dvGrid');
        const cell = (i, label) => `<div class="sn-cell ${m[i] ? 'pick' : 'nopick'}" data-i="${i}">${label}</div>`;
        let h = '';
        if (g.lines) {                                           // MGT200: 5 Line (CN) x 32 cam bien (ID 0-31)
            grid.className = 'sn-grid pm dv-grid' + stClass();
            for (let line = 0; line < g.lines; line++) {
                h += `<div class="sn-rowlabel clickable" data-line="${line}" title="Chọn / bỏ cả Line CN ${line + 1}">CN ${line + 1}</div>`;
                for (let id = 0; id < 32; id++) h += cell(line * 32 + id, id);
            }
        } else {                                                 // Keytop: 60 cam bien
            grid.className = 'sn-grid dv-grid' + stClass();
            for (let i = 0; i < g.per; i++) h += cell(i, i + 1);
        }
        grid.innerHTML = h;
    }

    function renderSummary() {
        let total = 0; const parts = [];
        [...edit.mask.keys()].sort((a, b) => a - b).forEach(d => { const c = countDev(d); if (c) { total += c; parts.push(`ZCU ${d} (${c})`); } });
        $('dvPickSummary').innerHTML = total
            ? `Đã chọn <b>${total}</b> cảm biến trên <b>${parts.length}</b> ZCU: ${parts.slice(0, 12).join(' · ')}${parts.length > 12 ? ` · … +${parts.length - 12} ZCU` : ''}`
            : 'Chưa chọn cảm biến nào.';
    }

    function setCell(el, i, val) {
        maskFor(edit.dev)[i] = val;
        el.className = 'sn-cell ' + (val ? 'pick' : 'nopick');
    }

    // bam / keo chuot tren luoi de chon hoac bo chon (huong theo o dau tien bam)
    $('dvGrid').addEventListener('mousedown', e => {
        if (!edit || e.button !== 0) return;
        const lab = e.target.closest('.sn-rowlabel[data-line]');
        if (lab) { toggleLine(+lab.dataset.line); return; }
        const cell = e.target.closest('.sn-cell'); if (!cell) return;
        const i = +cell.dataset.i;

        // Shift + bam: chon (hoac bo chon) ca doan tu o truoc do den o nay, trong cung 1 Line (MGT200) / cung 1 Keytop.
        // Huong chon / bo chon theo trang thai cua o moc (o bam truoc do).
        const lineOf = k => (geo(edit.source).lines ? Math.floor(k / 32) : 0);
        if (e.shiftKey && anchor && anchor.dev === edit.dev && lineOf(anchor.i) === lineOf(i)) {
            const m = maskFor(edit.dev), val = m[anchor.i] ? 1 : 0;
            for (let k = Math.min(anchor.i, i); k <= Math.max(anchor.i, i); k++) m[k] = val;
            anchor = { dev: edit.dev, i };
            renderAll();
            e.preventDefault();
            return;
        }

        anchor = { dev: edit.dev, i };
        painting = { val: maskFor(edit.dev)[i] ? 0 : 1 };
        setCell(cell, i, painting.val);
        window.addEventListener('mouseup', endPaint);            // chi gan trong luc keo
        e.preventDefault();
    });
    $('dvGrid').addEventListener('mouseover', e => {
        if (!painting) return;
        const cell = e.target.closest('.sn-cell');
        if (cell) setCell(cell, +cell.dataset.i, painting.val);
    });
    function endPaint() {
        window.removeEventListener('mouseup', endPaint);
        painting = null;
        if (edit) { renderDevices(); renderSummary(); }
    }

    function toggleLine(line) {
        const m = maskFor(edit.dev);
        let all = true;
        for (let k = line * 32; k < line * 32 + 32; k++) if (!m[k]) { all = false; break; }
        for (let k = line * 32; k < line * 32 + 32; k++) m[k] = all ? 0 : 1;
        renderAll();
    }

    $('dvDevices').addEventListener('click', e => {
        const b = e.target.closest('.sn-dev'); if (!b || !edit) return;
        edit.dev = +b.dataset.d; renderAll();
    });
    $('DvDevAll').addEventListener('click', () => { if (edit) { maskFor(edit.dev).fill(1); renderAll(); } });
    $('DvDevNone').addEventListener('click', () => { if (edit) { maskFor(edit.dev).fill(0); renderAll(); } });
    $('DvSelAll').addEventListener('click', () => { if (edit) { for (let d = 0; d < deviceCount(); d++) maskFor(d).fill(1); renderAll(); } });
    $('DvSelNone').addEventListener('click', () => { if (edit) { edit.mask.clear(); renderAll(); } });

    $('dvCancel').addEventListener('click', closeEditor);
    $('dvSave').addEventListener('click', async () => {
        if (!edit) return;
        const selection = [];
        [...edit.mask.keys()].sort((a, b) => a - b).forEach(d => {
            let s = ''; for (const b of edit.mask.get(d)) s += b ? '1' : '0';
            if (s.includes('1')) selection.push({ device: d, mask: s });
        });
        const say = html => { $('dvFormState').innerHTML = html; };
        say('⏳ Đang lưu…');
        try {
            const res = await fetch(API, {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    originalName: edit.original, name: edit.original, description: $('DvDesc').value.trim(),
                    count: $('DvCount').value || null, source: edit.source, selection
                })
            });
            const data = await res.json().catch(() => ({}));
            if (!res.ok) { say(`<span style="color:#ef4444;">${esc(data.message || 'Lỗi ' + res.status)}</span>`); return; }
            closeEditor();
            sel = data.name;
            $('dvState').textContent = `Đã lưu biến ${data.name}.`;
            await refresh();
        } catch (err) {
            say(`<span style="color:#ef4444;">Không lưu được: ${esc(err.message)}</span>`);
        }
    });

    // ----------------------------------------------------------------- hoi may chu chi khi dang xem tab nay
    function start() {
        stop();
        refresh();
        timer = setInterval(() => { if (!document.hidden) refresh(); }, 1000);
    }
    function stop() { clearInterval(timer); timer = null; }

    document.addEventListener('sensortab', e => { if (e.detail.name === 'data') start(); else stop(); });
    document.addEventListener('visibilitychange', () => { if (!document.hidden && timer) refresh(); });
    if ($('dataTab').classList.contains('active')) start();       // trang mo lai dung tab nay
})();
