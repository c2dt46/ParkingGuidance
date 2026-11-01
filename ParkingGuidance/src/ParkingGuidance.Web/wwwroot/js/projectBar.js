// ---------------------------------------------------------------------------
// DU AN: nut LUU CHUNG + MO / MOI / LUU THANH (thanh o dau sidebar, co mat o moi trang)
//
//  - SUA THONG SO = AP DUNG NGAY lam thong so chay (vao bo nho cua chuong trinh), CHUA ghi file.
//    Moi trang dang ky ham ap dung cua minh bang PGS.registerSave(ten, ham, hamKiemTraDaSua, { auto: true }):
//    khi nguoi dung sua xong (dung tay ~1,2 giay) ham duoc goi tu dong.
//  - Chi khi bam nut "Luu" (hoac Ctrl+S) moi ghi toan bo du lieu du an xuong thu muc du an (POST /api/project/save).
//    Ham ap dung tra ve false neu that bai (loi nhap / loi may chu) -> dung lai, khong ghi du an.
//  - "Mo": chon thu muc du an (co project.pgs.json) -> nap lai toan bo du lieu, khong can khoi dong lai chuong trinh.
// ---------------------------------------------------------------------------
(function () {
    'use strict';

    const PGS = (window.PGS = window.PGS || {});
    const savers = [];

    /** Dang ky 1 phan cai dat cua trang. fn: async () => true|false. isDirty: () => bool (khong bat buoc, dung de hien "co thay doi chua luu"). */
    PGS.registerSave = function (label, fn, isDirty, opts) {
        savers.push({ label, fn, isDirty, auto: !!(opts && opts.auto), running: false, blockedUntil: 0 });
    };
    /** Form tren trang con sua do, chua ap dung vao thong so chay. */
    PGS.isPageDirty = function () {
        return savers.some(s => { try { return !!(s.isDirty && s.isDirty()); } catch (e) { return false; } });
    };
    /** Co thay doi chua ghi vao file du an (o trang nay hoac bat ky noi nao: thong so da ap dung nhung chua bam Luu). */
    PGS.isDirty = function () { return PGS.isPageDirty() || serverDirty; };

    const $ = id => document.getElementById(id);
    let cur = null;          // du an dang mo { name, path, savedUtc }
    let info = null;         // tra loi day du cua GET /api/project
    let saving = false;
    let serverDirty = false;     // may chu: co thong so da ap dung nhung chua ghi file
    let lastActivity = 0;
    let msg = '', msgErr = false, msgUntil = 0, lastSaved = '';

    async function api(url, method, body) {
        const res = await fetch(url, {
            method: method || 'GET',
            headers: body ? { 'Content-Type': 'application/json' } : undefined,
            body: body ? JSON.stringify(body) : undefined
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(data.message || ('Lỗi ' + res.status));
        return data;
    }

    // ------------------------------------------------------------------ thanh trang thai
    function say(text, err) { msg = text; msgErr = !!err; msgUntil = Date.now() + (err ? 15000 : 6000); paintState(); }

    function paintState() {
        const st = $('pgProjState'), box = $('pgProject');
        if (!st || !box) return;
        const dirty = PGS.isDirty();
        box.classList.toggle('dirty', dirty);
        st.classList.remove('err', 'warn');
        if (Date.now() < msgUntil && msg) { st.textContent = msg; if (msgErr) st.classList.add('err'); return; }
        if (dirty) { st.textContent = '● Có thay đổi chưa ghi vào file — bấm Lưu (Ctrl+S)'; st.classList.add('warn'); return; }
        st.textContent = lastSaved;
    }

    function paintProject() {
        if (!cur) return;
        const n = $('pgProjName'), p = $('pgProjPath');
        if (n) { n.textContent = '📁 ' + cur.name; n.title = cur.name; }
        if (p) { p.textContent = cur.path; p.title = cur.path + '\n(Dữ liệu hệ thống — bản quyền, train — nằm riêng ở: ' + (info?.systemDirectory || '') + ')'; }
        if (cur.savedUtc) lastSaved = 'Lưu lần cuối: ' + new Date(cur.savedUtc).toLocaleString('vi-VN');
        paintState();
    }

    async function refresh() {
        try { info = await api('/api/project'); cur = info.current; serverDirty = !!info.unsaved; paintProject(); }
        catch (e) { say('⚠️ Không đọc được dự án: ' + e.message, true); }
    }

    // ------------------------------------------------------------------ AP DUNG + LUU CHUNG
    /** Ap dung moi thong so dang sua do cua trang vao thong so chay (chua ghi file). false = co phan that bai. */
    async function applyAll() {
        for (const s of savers) {
            let ok;
            try { ok = await s.fn(); } catch (e) { console.error(e); ok = false; }
            if (ok === false) { say('⚠️ Chưa áp dụng được phần "' + s.label + '" — xem thông báo trên trang.', true); return false; }
        }
        return true;
    }

    async function saveAll() {
        if (saving) return false;
        saving = true;
        const btn = $('pgSaveBtn'); if (btn) btn.disabled = true;
        say('⏳ Đang lưu…');
        try {
            if (!(await applyAll())) return false;
            const r = await api('/api/project/save', 'POST');
            info = r; cur = r.current; serverDirty = !!r.unsaved;
            lastSaved = '';
            paintProject();
            say('✅ Đã lưu vào file lúc ' + new Date().toLocaleTimeString('vi-VN'));
            return true;
        } catch (e) {
            say('⚠️ ' + e.message, true);
            return false;
        } finally {
            saving = false;
            if (btn) btn.disabled = false;
            paintState();
        }
    }
    PGS.saveAll = saveAll;

    // ------------------------------------------------------------------ hop thoai chon thu muc
    let dlg = null, mode = 'open', browsePath = '', browseData = null, selected = null;

    function buildDialog() {
        dlg = document.createElement('dialog');
        dlg.className = 'pg-dlg';
        dlg.innerHTML =
            '<h3 id="pgDlgTitle"></h3>' +
            '<div id="pgDlgRecentBox" class="pg-dlg-recent"></div>' +
            '<div class="pg-dlg-bar"><button type="button" class="btn secondary" id="pgDlgUp" title="Lên thư mục cha">⬆</button>' +
            '<input id="pgDlgPath" type="text" spellcheck="false" placeholder="Nhập đường dẫn rồi Enter">' +
            '<button type="button" class="btn secondary" id="pgDlgGo" title="Chọn thư mục bằng hộp thoại của Windows">📂 Browse…</button></div>' +
            '<div id="pgDlgList" class="pg-dlg-list"></div>' +
            '<div id="pgDlgNameRow" class="pg-dlg-name"><label for="pgDlgName">Tên dự án</label><input id="pgDlgName" type="text" maxlength="80" placeholder="VD: BaiXe_ToaNhaA"></div>' +
            '<div id="pgDlgMsg" class="hint small"></div>' +
            '<div class="pg-dlg-actions"><button type="button" class="btn secondary" id="pgDlgMkdir">📁 Thư mục mới</button><span style="flex:1"></span>' +
            '<button type="button" class="btn secondary" id="pgDlgCancel">Huỷ</button><button type="button" class="btn primary" id="pgDlgOk"></button></div>';
        document.body.appendChild(dlg);

        $('pgDlgCancel').addEventListener('click', () => dlg.close());
        $('pgDlgUp').addEventListener('click', () => { if (browseData) browse(browseData.parent || ''); });
        $('pgDlgGo').addEventListener('click', async () => {
            // Browse thật: hộp thoại chọn thư mục của Windows (hiện trên máy chạy chương trình)
            const btn = $('pgDlgGo'); btn.disabled = true; dlgMsg('⏳ Đang mở hộp thoại chọn thư mục của Windows… (nếu chưa thấy, kiểm tra thanh taskbar)');
            try {
                const r = await api('/api/project/pick-folder', 'POST', { initial: browsePath || '' });
                if (r.path) await browse(r.path); else dlgMsg('');
            } catch (e) { dlgMsg(e.message, true); }
            finally { btn.disabled = false; }
        });
        $('pgDlgPath').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); browse($('pgDlgPath').value.trim()); } });
        $('pgDlgOk').addEventListener('click', onOk);
        $('pgDlgMkdir').addEventListener('click', onMkdir);
        $('pgDlgList').addEventListener('click', e => {
            const li = e.target.closest('[data-path]'); if (!li) return;
            if (mode === 'open' && li.dataset.project === '1') { selected = li.dataset.path; markSelected(); updateOk(); }
            else browse(li.dataset.path);
        });
        $('pgDlgList').addEventListener('dblclick', e => {
            const li = e.target.closest('[data-path]'); if (li) browse(li.dataset.path);
        });
        dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });
    }

    function dlgMsg(text, err) { const m = $('pgDlgMsg'); m.textContent = text || ''; m.classList.toggle('err', !!err); }

    function markSelected() {
        $('pgDlgList').querySelectorAll('[data-path]').forEach(li => li.classList.toggle('sel', li.dataset.path === selected));
    }

    function updateOk() {
        const ok = $('pgDlgOk');
        if (mode === 'open') {
            const target = selected || (browseData && browseData.isProject ? browseData.path : '');
            ok.disabled = !target;
            ok.textContent = '📂 Mở dự án' + (target ? '' : ' (chọn thư mục 📦)');
        } else {
            ok.disabled = !(browseData && browseData.path) || !$('pgDlgName').value.trim();
            ok.textContent = mode === 'new' ? '🆕 Tạo dự án' : '📑 Lưu thành';
        }
    }

    async function browse(path) {
        dlgMsg('');
        try {
            const d = await api('/api/project/browse?path=' + encodeURIComponent(path || ''));
            browseData = d; browsePath = d.path || ''; selected = null;
            $('pgDlgPath').value = browsePath;
            $('pgDlgUp').disabled = !browsePath;
            const list = $('pgDlgList'); list.textContent = '';
            if (!d.dirs.length) {
                const e = document.createElement('div'); e.className = 'pg-dlg-empty'; e.textContent = 'Không có thư mục con.'; list.appendChild(e);
            }
            d.dirs.forEach(x => {
                const li = document.createElement('div');
                li.className = 'pg-dlg-item' + (x.isProject ? ' proj' : '');
                li.dataset.path = x.path; li.dataset.project = x.isProject ? '1' : '0';
                li.textContent = (x.isProject ? '📦 ' : '📁 ') + x.name;
                li.title = x.path;
                list.appendChild(li);
            });
            if (mode === 'open' && d.isProject) dlgMsg('📦 Thư mục này là một dự án PGS.');
            updateOk();
        } catch (e) {
            dlgMsg(e.message, true);
            if (path) browse('');          // duong dan sai: ve danh sach o dia / thu muc thuong dung
        }
    }

    async function onMkdir() {
        if (!browsePath) { dlgMsg('Hãy chọn một ổ đĩa / thư mục trước.', true); return; }
        const name = prompt('Tên thư mục mới trong\n' + browsePath);
        if (!name) return;
        try { const r = await api('/api/project/mkdir', 'POST', { parent: browsePath, name }); await browse(r.path); }
        catch (e) { dlgMsg(e.message, true); }
    }

    async function confirmUnsaved() {
        if (!PGS.isDirty()) return true;
        if (confirm('Có thay đổi chưa ghi vào file dự án.\n\nOK = Lưu rồi tiếp tục\nHuỷ = chọn tiếp (bỏ thay đổi / ở lại)')) return await saveAll();
        return confirm('Bỏ các thay đổi chưa lưu và tiếp tục?');
    }

    async function onOk() {
        const ok = $('pgDlgOk');
        try {
            if (mode === 'open') {
                const target = selected || (browseData.isProject ? browseData.path : '');
                if (!target) return;
                await openProject(target);
            } else if (mode === 'new') {
                if (!(await confirmUnsaved())) return;
                ok.disabled = true; dlgMsg('⏳ Đang tạo dự án…');
                await api('/api/project/new', 'POST', { parent: browsePath, name: $('pgDlgName').value.trim() });
                location.href = '/';
            } else {
                dlg.close();
                if (!(await applyAll())) return;      // chỉ áp dụng ô đang sửa dở; dự án gốc không bị ghi
                say('⏳ Đang sao chép dự án…');
                const r = await api('/api/project/save-as', 'POST', { parent: browsePath, name: $('pgDlgName').value.trim() });
                info = r; cur = r.current; lastSaved = ''; paintProject();
                say('✅ Đã lưu thành "' + cur.name + '" và đang làm việc trên bản sao này.');
            }
        } catch (e) {
            if (dlg.open) { dlgMsg(e.message, true); updateOk(); } else say('⚠️ ' + e.message, true);
        }
    }

    async function openProject(path) {
        if (!(await confirmUnsaved())) return;
        if (dlg && dlg.open) { dlgMsg('⏳ Đang mở dự án…'); $('pgDlgOk').disabled = true; }
        else say('⏳ Đang mở dự án…');
        try {
            await api('/api/project/open', 'POST', { path });
            location.href = '/';           // mat bang / camera / layout da doi: ve trang Giam sat
        } catch (e) {
            if (dlg && dlg.open) { dlgMsg(e.message, true); updateOk(); } else say('⚠️ ' + e.message, true);
        }
    }

    function openDialog(m) {
        if (!dlg) buildDialog();
        mode = m;
        $('pgDlgTitle').textContent = m === 'open' ? '📂 Mở dự án' : (m === 'new' ? '🆕 Dự án mới' : '📑 Lưu dự án thành bản sao');
        $('pgDlgNameRow').style.display = m === 'open' ? 'none' : '';
        $('pgDlgName').value = '';
        $('pgDlgName').oninput = updateOk;
        $('pgDlgMkdir').style.display = m === 'open' ? 'none' : '';

        // Dự án gần đây (chỉ khi mở)
        const rb = $('pgDlgRecentBox'); rb.textContent = '';
        const recent = (info && info.recent) || [];
        if (m === 'open' && recent.length) {
            const t = document.createElement('div'); t.className = 'pg-dlg-sub'; t.textContent = 'Dự án gần đây'; rb.appendChild(t);
            recent.forEach(r => {
                const b = document.createElement('button'); b.type = 'button'; b.className = 'pg-dlg-recent-item';
                b.textContent = '📦 ' + r.name; b.title = r.path;
                b.addEventListener('click', () => openProject(r.path));
                rb.appendChild(b);
            });
        }
        rb.style.display = rb.childNodes.length ? '' : 'none';

        dlgMsg(m === 'open' ? 'Chọn thư mục có biểu tượng 📦 (thư mục chứa project.pgs.json). Bấm đúp để đi vào bên trong.'
            : 'Chọn thư mục cha, đặt tên dự án — chương trình tạo thư mục con mang tên đó.');
        const start = m === 'open'
            ? (cur ? cur.path.replace(/[\\/][^\\/]+[\\/]?$/, '') : '')
            : (info && info.defaultParent) || '';
        dlg.showModal();
        browse(start);
    }

    // ------------------------------------------------------------------ khoi dong
    document.addEventListener('DOMContentLoaded', () => {
        if (!$('pgProject')) return;                     // trang khong co thanh du an (vd Login)
        $('pgSaveBtn').addEventListener('click', saveAll);
        $('pgOpenBtn').addEventListener('click', () => openDialog('open'));
        $('pgNewBtn').addEventListener('click', () => openDialog('new'));
        $('pgSaveAsBtn').addEventListener('click', () => openDialog('saveas'));
        refresh();
        setInterval(paintState, 800);

        // Hoi may chu 3 giay/lan: da co thong so ap dung nhung chua ghi file chua?
        setInterval(async () => {
            if (saving || document.hidden) return;
            try { const r = await api('/api/project'); serverDirty = !!r.unsaved; cur = r.current; } catch (e) { /* mat ket noi tam thoi */ }
        }, 3000);

        // Tu dong AP DUNG khi nguoi dung sua xong (dung tay ~1,2 giay) — thong so ap dung la thong so chay, nhung chua ghi file
        ['input', 'change', 'keyup', 'pointerup', 'click'].forEach(ev => document.addEventListener(ev, () => { lastActivity = Date.now(); }, true));
        setInterval(async () => {
            if (saving || Date.now() - lastActivity < 1200) return;
            for (const s of savers) {
                if (!s.auto || s.running || Date.now() < s.blockedUntil) continue;
                let dirty = false;
                try { dirty = !!(s.isDirty && s.isDirty()); } catch (e) { }
                if (!dirty) continue;
                s.running = true;
                try {
                    const ok = await s.fn(true);                 // true = tu dong: khong hien hop thoai bao loi
                    if (ok === false) s.blockedUntil = Date.now() + 15000;      // dang nhap do / chua hop le: thu lai sau
                    else serverDirty = true;
                } catch (e) { s.blockedUntil = Date.now() + 15000; }
                finally { s.running = false; }
            }
        }, 500);
    });

    document.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 's' && $('pgProject')) {
            e.preventDefault();
            saveAll();
        }
    });

    window.addEventListener('beforeunload', e => {
        if (PGS.isPageDirty()) { e.preventDefault(); e.returnValue = ''; }
    });
})();
