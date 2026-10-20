// ---------------------------------------------------------------------------
// Trang Camera › tab "Du lieu": bien MC. Moi bien chi dem MOT trang thai (Con trong · Da do · Loi) tren cac VI TRI DO
// (vung nhan dien khoanh tren anh camera) duoc chi dinh. Gia tri do may chu tinh tu trang thai o do that.
// Tiet kiem tai nguyen: chi hoi may chu moi giay khi dang xem tab nay (va trinh duyet dang hien); roi tab thi dung han.
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/camera-variables';
    const LABEL = { Free: 'Còn trống (Có chỗ đỗ)', Occupied: 'Đã đỗ (Không có chỗ đỗ)', Error: 'Lỗi' };
    const labelOf = c => LABEL[c] || 'Chưa chọn';
    const CLS = { Free: 's0', Occupied: 's1', Error: 's2' };
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    let rows = [];
    let sel = null;                 // ten bien dang chon trong bang
    let timer = null;

    // ----------------------------------------------------------------- bang danh sach
    async function refresh() {
        try {
            const res = await fetch(API);
            if (!res.ok) return;
            const d = await res.json();
            rows = d.variables || [];
            renderTable();
        } catch { /* mat ket noi tam thoi: lan hoi sau se duoc */ }
    }

    function renderTable() {
        if (sel && !rows.some(r => r.name === sel)) sel = null;
        const body = $('mcBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="hint small">Chưa có biến nào — bấm "Thêm biến".</td></tr>';
        } else {
            body.innerHTML = rows.map((r, i) => {
                const val = !r.count
                    ? '<span class="dv-val">—</span><span class="dv-sub">chưa chọn trạng thái đếm</span>'
                    : `<span class="dv-val ${CLS[r.count] || ''}">${r.value}</span><span class="dv-sub">trên ${r.selected} vị trí` +
                      `${r.missing ? ` · ${r.missing} không còn vùng nhận diện` : ''}</span>`;
                return `<tr data-name="${esc(r.name)}" class="${r.name === sel ? 'sel' : ''}">
                    <td>${i}</td><td><b>${esc(r.name)}</b></td>
                    <td>${labelOf(r.count)}</td>
                    <td>${esc(r.summary) || '<span class="dv-sub">chưa chọn vị trí</span>'}</td><td>${val}</td><td>${esc(r.description)}</td></tr>`;
            }).join('');
        }
        $('mcEdit').disabled = !sel;
        $('mcDel').disabled = !sel;
    }

    $('mcBody').addEventListener('click', e => {
        const tr = e.target.closest('tr[data-name]'); if (!tr) return;
        sel = tr.dataset.name; renderTable();
    });
    $('mcBody').addEventListener('dblclick', e => {
        const tr = e.target.closest('tr[data-name]'); if (!tr) return;
        sel = tr.dataset.name; renderTable(); openEditor(sel);
    });
    $('mcAdd').addEventListener('click', async () => {
        // Thêm ngay 1 biến MC (tên tự đặt MC0, MC1… lấp chỗ trống nhỏ nhất) — không mở form.
        $('mcAdd').disabled = true;
        try {
            const res = await fetch(`${API}/add`, { method: 'POST' });
            const data = await res.json().catch(() => ({}));
            if (!res.ok) { $('mcState').textContent = data.message || 'Không thêm được biến.'; return; }
            sel = data.name;
            $('mcState').textContent = `Đã thêm biến ${data.name}.`;
            await refresh();
        } catch { $('mcState').textContent = 'Không thêm được biến.'; }
        finally { $('mcAdd').disabled = false; }
    });
    $('mcEdit').addEventListener('click', () => { if (sel) openEditor(sel); });
    $('mcDel').addEventListener('click', async () => {
        if (!sel) return;
        if (!confirm(`Xoá biến ${sel}?`)) return;
        const res = await fetch(`${API}/${encodeURIComponent(sel)}`, { method: 'DELETE' });
        $('mcState').textContent = res.ok ? `Đã xoá biến ${sel}.` : 'Không xoá được.';
        sel = null;
        await refresh();
    });

    // ----------------------------------------------------------------- bo chon vi tri do
    // edit = { original, locked, cams: [{cameraId,label,floor,zone,positions:[{key,number,name}]}], cam: chi so camera dang xem (-1 = khong co),
    //          picked: Set<khoa o>, fFloor, fZone, fText, fOnly: bo loc camera }
    let edit = null;
    let painting = null;            // { val } khi dang keo chuot de chon / bo chon
    let anchor = null;              // { cam, i } o bam gan nhat — moc cho Shift+bam chon ca doan

    const curPositions = () => (edit.cams[edit.cam] ? edit.cams[edit.cam].positions : []);
    const countCam = c => c.positions.reduce((n, p) => n + (edit.picked.has(p.key) ? 1 : 0), 0);

    // chi so cac camera dang hien theo bo loc (tang / Zone / tu khoa / chi camera da chon)
    function filteredIdx() {
        const q = edit.fText.trim().toLowerCase();
        const out = [];
        edit.cams.forEach((c, i) => {
            if (edit.fFloor && c.floor !== edit.fFloor) return;
            if (edit.fZone && c.zone !== edit.fZone) return;
            if (q && !`${c.label} ${c.floorName} ${c.floor} zone ${c.zone}`.toLowerCase().includes(q)) return;
            if (edit.fOnly && countCam(c) === 0) return;
            out.push(i);
        });
        return out;
    }

    async function openEditor(name) {
        if (!name) return;
        let v = null, cat = { floors: [], cameras: [] };
        try {
            const [r1, r2] = await Promise.all([fetch(`${API}/${encodeURIComponent(name)}`), fetch(`${API}/catalog`)]);
            if (!r1.ok) { $('mcState').textContent = 'Không đọc được biến này.'; return; }
            v = await r1.json();
            if (r2.ok) cat = await r2.json();
        } catch { $('mcState').textContent = 'Không đọc được biến này.'; return; }

        anchor = null;
        const cams = cat.cameras || [];
        edit = { original: v.name, locked: !!v.count, cams, floors: cat.floors || [], cam: cams.length ? 0 : -1, picked: new Set(v.slots || []),
                 fFloor: '', fZone: '', fText: '', fOnly: false };
        // mo san camera dau tien co vi tri da chon, neu khong thi camera dau tien co vi tri
        let first = cams.findIndex(c => c.positions.some(p => edit.picked.has(p.key)));
        if (first < 0) first = cams.findIndex(c => c.positions.length);
        if (first >= 0) edit.cam = first;

        $('McSearch').value = ''; $('McOnly').checked = false;
        fillFloors();
        $('mcFormTitle').textContent = `Sửa biến ${v.name}`;
        // Mỗi biến chỉ đếm 1 trạng thái: đã xác định thì khoá ô chọn trạng thái.
        $('McCount').value = v.count || '';
        $('McCount').disabled = edit.locked;
        $('McDesc').value = v.description;
        $('mcFormState').textContent = '';
        $('mcForm').style.display = '';
        renderAll();
    }

    function closeEditor() {
        edit = null; painting = null; anchor = null;
        $('mcForm').style.display = 'none';
        $('mcGrid').innerHTML = ''; $('mcCams').innerHTML = '';        // giai phong DOM cua bo chon
    }

    // mau o duoc chon theo trang thai dang dem: Da do = do, Con trong = xanh la, Loi = vang; chua chon trang thai = xanh duong
    const stClass = () => { const v = $('McCount').value; return v ? ' st-' + v : ''; };
    function paintLegend() {
        const v = $('McCount').value;
        document.querySelectorAll('#mcLegend span[data-st]').forEach(s => s.classList.toggle('cur', s.dataset.st === v));
    }
    $('McCount').addEventListener('change', () => { if (edit) { renderGrid(); paintLegend(); } });

    // ---- bo loc camera
    const uniq = (arr, cmp) => [...new Set(arr)].sort(cmp);
    const natural = (a, b) => String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
    function fillFloors() {
        // mọi tầng hầm (mặt bằng đã khai báo + tầng ghi trên camera), kể cả tầng chưa có vị trí đỗ nào
        const floors = [...edit.floors].sort((a, b) => natural(a.name, b.name));
        $('McFloor').innerHTML = '<option value="">Tất cả</option>' + floors.map(f => `<option value="${esc(f.key)}">${esc(f.name)}</option>`).join('');
        $('McFloor').value = edit.fFloor;
        fillZones();
    }
    function fillZones() {
        const zones = uniq(edit.cams.filter(c => !edit.fFloor || c.floor === edit.fFloor).map(c => c.zone), natural);
        if (edit.fZone && !zones.includes(edit.fZone)) edit.fZone = '';
        $('McZone').innerHTML = '<option value="">Tất cả</option>' + zones.map(z => `<option value="${esc(z)}">Zone ${esc(z)}</option>`).join('');
        $('McZone').value = edit.fZone;
    }
    $('McFloor').addEventListener('change', () => { if (!edit) return; edit.fFloor = $('McFloor').value; fillZones(); renderAll(); });
    $('McZone').addEventListener('change', () => { if (!edit) return; edit.fZone = $('McZone').value; renderAll(); });
    $('McSearch').addEventListener('input', () => { if (!edit) return; edit.fText = $('McSearch').value; renderAll(); });
    $('McOnly').addEventListener('change', () => { if (!edit) return; edit.fOnly = $('McOnly').checked; renderAll(); });

    function renderAll() { renderCams(); renderGrid(); renderSummary(); paintLegend(); }

    function renderCams() {
        const idx = filteredIdx();
        // camera dang xem khong con trong bo loc thi chuyen sang camera dau tien dang hien
        if (!idx.includes(edit.cam)) { edit.cam = idx.length ? idx[0] : -1; anchor = null; }

        const box = $('mcCams');
        $('mcFilterInfo').textContent = edit.cams.length ? `Hiện ${idx.length}/${edit.cams.length} camera` : '';
        box.innerHTML = idx.length ? idx.map(i => {
            const c = edit.cams[i], n = countCam(c), t = c.positions.length;
            const empty = t === 0;
            return `<button type="button" class="mc-cam${i === edit.cam ? ' sel' : ''}${n ? ' has' : ''}${!empty && n === t ? ' full' : ''}${empty ? ' empty' : ''}" data-c="${i}" ` +
                   `title="${esc(c.label)} — ${empty ? 'chưa có vị trí đỗ nào' : n + '/' + t + ' vị trí được chọn'}"><span class="nm">${esc(c.floorName)} / Zone ${esc(c.zone)} · ${esc(c.label)}</span><span class="cnt">${empty ? 'chưa có vị trí' : n + '/' + t}</span></button>`;
        }).join('') : '';

        const c = edit.cams[edit.cam];
        $('mcCamTitle').textContent = c ? `${c.floorName} / Zone ${c.zone} — ${c.label}: ${countCam(c)}/${c.positions.length} vị trí được chọn` : '';
        $('McCamAll').disabled = $('McCamNone').disabled = !c;
    }

    function renderGrid() {
        const grid = $('mcGrid');
        grid.className = 'sn-grid dv-grid' + stClass();
        if (!edit.cams.length) {
            grid.innerHTML = '<div class="hint small" style="grid-column:1/-1">Chưa có camera nào trong bãi đỗ — thêm camera ở tab 📋 Danh sách rồi vẽ vùng nhận diện ở tab 🎯 Vị trí đỗ.</div>';
            return;
        }
        if (edit.cam < 0) {
            grid.innerHTML = '<div class="hint small" style="grid-column:1/-1">Không có camera nào phù hợp bộ lọc.</div>';
            return;
        }
        if (!curPositions().length) {
            grid.innerHTML = '<div class="hint small" style="grid-column:1/-1">Camera này chưa khoanh vùng vị trí đỗ nào — vào tab 🎯 Vị trí đỗ, chọn camera này và vẽ vùng nhận diện.</div>';
            return;
        }
        grid.innerHTML = curPositions().map((p, i) =>
            `<div class="sn-cell ${edit.picked.has(p.key) ? 'pick' : 'nopick'}" data-i="${i}" title="Vị trí ${esc(p.number)}${p.name ? ' — ' + esc(p.name) : ''} (${esc(p.key)})">${esc(p.number)}</div>`).join('');
    }

    function renderSummary() {
        // gom theo tang / Zone (nhieu camera thi gon hon liet ke tung camera)
        const groups = new Map(); let total = 0, camCount = 0;
        edit.cams.forEach(c => {
            const n = countCam(c); if (!n) return;
            total += n; camCount++;
            const g = `${c.floorName}/Z${c.zone}`; groups.set(g, (groups.get(g) || 0) + n);
        });
        const parts = [...groups].map(([g, n]) => `${g} (${n})`);
        $('mcPickSummary').innerHTML = total
            ? `Đã chọn <b>${total}</b> vị trí trên <b>${camCount}</b> camera: ${esc(parts.slice(0, 10).join(' · '))}${parts.length > 10 ? ` · … +${parts.length - 10} khu` : ''}`
            : 'Chưa chọn vị trí nào.';
    }

    function setCell(el, i, val) {
        const p = curPositions()[i]; if (!p) return;
        if (val) edit.picked.add(p.key); else edit.picked.delete(p.key);
        el.className = 'sn-cell ' + (val ? 'pick' : 'nopick');
    }

    // bam / keo chuot tren luoi de chon hoac bo chon (huong theo o dau tien bam)
    $('mcGrid').addEventListener('mousedown', e => {
        if (!edit || e.button !== 0) return;
        const cell = e.target.closest('.sn-cell'); if (!cell) return;
        const i = +cell.dataset.i;
        const pos = curPositions();

        // Shift + bam: chon (hoac bo chon) ca doan tu o truoc do den o nay, trong cung 1 camera.
        // Huong chon / bo chon theo trang thai cua o moc (o bam truoc do).
        if (e.shiftKey && anchor && anchor.cam === edit.cam && pos[anchor.i]) {
            const val = edit.picked.has(pos[anchor.i].key);
            for (let k = Math.min(anchor.i, i); k <= Math.max(anchor.i, i); k++)
                if (val) edit.picked.add(pos[k].key); else edit.picked.delete(pos[k].key);
            anchor = { cam: edit.cam, i };
            renderAll();
            e.preventDefault();
            return;
        }

        anchor = { cam: edit.cam, i };
        painting = { val: edit.picked.has(pos[i].key) ? 0 : 1 };
        setCell(cell, i, painting.val);
        window.addEventListener('mouseup', endPaint);            // chi gan trong luc keo
        e.preventDefault();
    });
    $('mcGrid').addEventListener('mouseover', e => {
        if (!painting) return;
        const cell = e.target.closest('.sn-cell');
        if (cell) setCell(cell, +cell.dataset.i, painting.val);
    });
    function endPaint() {
        window.removeEventListener('mouseup', endPaint);
        painting = null;
        if (edit) { renderCams(); renderSummary(); }
    }

    $('mcCams').addEventListener('click', e => {
        const b = e.target.closest('.mc-cam'); if (!b || !edit) return;
        edit.cam = +b.dataset.c; anchor = null; renderAll();
    });
    function setAll(positions, on) { positions.forEach(p => { if (on) edit.picked.add(p.key); else edit.picked.delete(p.key); }); renderAll(); }
    $('McCamAll').addEventListener('click', () => { if (edit) setAll(curPositions(), true); });
    $('McCamNone').addEventListener('click', () => { if (edit) setAll(curPositions(), false); });
    // "tat ca" / "bo chon" ap dung cho cac camera DANG HIEN theo bo loc (khong loc = tat ca camera)
    $('McSelAll').addEventListener('click', () => { if (edit) setAll(filteredIdx().flatMap(i => edit.cams[i].positions), true); });
    $('McSelNone').addEventListener('click', () => { if (edit) setAll(filteredIdx().flatMap(i => edit.cams[i].positions), false); });

    $('mcCancel').addEventListener('click', closeEditor);
    $('mcSave').addEventListener('click', async () => {
        if (!edit) return;
        // chi gui cac vi tri con ton tai tren camera
        const exist = new Set(); edit.cams.forEach(c => c.positions.forEach(p => exist.add(p.key)));
        const slots = [...edit.picked].filter(k => exist.has(k));
        const say = html => { $('mcFormState').innerHTML = html; };
        say('⏳ Đang lưu…');
        try {
            const res = await fetch(API, {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    originalName: edit.original, name: edit.original, description: $('McDesc').value.trim(),
                    count: $('McCount').value || null, slots
                })
            });
            const data = await res.json().catch(() => ({}));
            if (!res.ok) { say(`<span style="color:#ef4444;">${esc(data.message || 'Lỗi ' + res.status)}</span>`); return; }
            closeEditor();
            sel = data.name;
            $('mcState').textContent = `Đã lưu biến ${data.name}.`;
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
    const sync = () => { if ($('dataTab').classList.contains('active')) { if (!timer) start(); } else stop(); };

    // systemCameras.js doi tab truoc (no dang ky listener truoc), sau do moi toi day
    document.querySelectorAll('.tab-btn[data-tab]').forEach(b => b.addEventListener('click', () => setTimeout(sync, 0)));
    document.addEventListener('visibilitychange', () => { if (!document.hidden && timer) refresh(); });
    sync();
})();
