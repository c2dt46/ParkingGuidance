// ---------------------------------------------------------------------------
// DU AN: nut LUU CHUNG + MO / MOI / LUU THANH (thanh o dau sidebar, co mat o moi trang)
//
//  - SUA THONG SO = AP DUNG NGAY lam thong so chay (vao bo nho cua chuong trinh), CHUA ghi file.
//    Moi trang dang ky ham ap dung cua minh bang PGS.registerSave(ten, ham, hamKiemTraDaSua, { auto: true }):
//    khi nguoi dung sua xong (dung tay ~1,2 giay) ham duoc goi tu dong.
//  - Chi khi bam nut "Luu" (hoac Ctrl+S) moi ghi toan bo du lieu du an xuong thu muc du an (POST /api/project/save).
//    Ham ap dung tra ve false neu that bai (loi nhap / loi may chu) -> dung lai, khong ghi du an.
//  - "Mo": chon thu muc du an (co project.pgs.json) -> nap lai toan bo du lieu, khong can khoi dong lai chuong trinh.
//  - Khoi dong lai chuong trinh: may chu tu CHAY LAI du an mo gan nhat (neu con nguyen ven). Chi khi chua tung mo du an nao (hoac du an gan nhat khong con)
//    thi sau khi dang nhap giao dien tu moi mo / tao du an (info.needsProject) — 1 lan moi phien lam viec cua tab.
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
    let promptMode = false;                          // hop thoai dang mo vi CHUA co du an nao (khong phai do nguoi dung bam Mo / Moi)
    const PROMPT_KEY = 'pg.projectPrompted';         // sessionStorage: da moi / bi tu choi trong tab nay roi thi khong moi lai o moi trang

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
        const brand = $('pgBrand'); if (brand) brand.classList.toggle('dirty', dirty);   // chấm cam trên logo: có thay đổi chưa ghi vào file
        st.classList.remove('err', 'warn');
        if (Date.now() < msgUntil && msg) { st.textContent = msg; if (msgErr) st.classList.add('err'); return; }
        if (dirty) { st.textContent = '● Có thay đổi chưa ghi vào file — bấm Lưu (Ctrl+S)'; st.classList.add('warn'); return; }
        st.textContent = lastSaved;
    }

    function paintProject() {
        if (!cur) return;
        const n = $('pgProjName'), p = $('pgProjPath');
        if (n) { n.textContent = (info && info.needsProject ? '📁 (chưa mở dự án) ' : '📁 ') + cur.name; n.title = cur.name; }
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
            '<div class="pg-dlg-actions" style="flex-wrap:wrap"><button type="button" class="btn secondary" id="pgDlgMkdir">📁 Thư mục mới</button>' +
            '<button type="button" class="btn secondary" id="pgDlgSwitch" style="display:none"></button>' +
            '<button type="button" class="btn secondary" id="pgDlgUseDefault" style="display:none" title="Dùng luôn thư mục dự án mặc định đang chạy làm dự án đang mở">Dùng dự án mặc định</button><span style="flex:1"></span>' +
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
        $('pgDlgSwitch').addEventListener('click', () => openDialog(mode === 'open' ? 'new' : 'open', true));   // chi hien khi chua co du an: chuyen qua lai giua Mo va Tao moi
        $('pgDlgUseDefault').addEventListener('click', onUseDefault);
        dlg.addEventListener('close', () => {
            // Dong hop thoai "chua co du an" (Để sau / Esc / bam ra ngoai): khong hoi lai o cac trang khac trong tab nay
            if (promptMode) { try { sessionStorage.setItem(PROMPT_KEY, '1'); } catch (e) { /* khong co storage */ } }
            promptMode = false;
        });
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

    async function onUseDefault() {
        const b = $('pgDlgUseDefault'); b.disabled = true; dlgMsg('⏳ Đang áp dụng…');
        try {
            const r = await api('/api/project/use-current', 'POST');
            info = r; cur = r.current; serverDirty = !!r.unsaved; lastSaved = '';
            paintProject();
            dlg.close();
            say('✅ Đang dùng dự án mặc định "' + cur.name + '".');
        } catch (e) { dlgMsg(e.message, true); }
        finally { b.disabled = false; }
    }

    /** Noi dung moi khi CHUA co du an nao dang mo. */
    function promptText() {
        if (info && info.missingLastProject)
            return '⚠️ Không tìm thấy dự án mở gần nhất: ' + info.missingLastProject + ' (thư mục bị xoá / đổi tên hoặc ổ đĩa chưa sẵn sàng). Hãy mở một dự án có sẵn hoặc tạo dự án mới.';
        return 'Chưa mở dự án nào. Hãy mở một dự án có sẵn hoặc tạo dự án mới (hoặc dùng dự án mặc định).';
    }

    /** Sau khi dang nhap: chua tung mo du an nao (hoac du an gan nhat khong con) thi moi mo / tao du an. Co du an roi thi may chu da tu chay lai du an do. */
    function promptIfNeeded() {
        if (!info || !info.needsProject) return;
        try { if (sessionStorage.getItem(PROMPT_KEY) === '1') return; } catch (e) { /* khong co storage */ }
        if (dlg && dlg.open) return;
        openDialog('open', true);
    }

    function openDialog(m, required) {
        if (!dlg) buildDialog();
        mode = m;
        promptMode = !!required;
        $('pgDlgTitle').textContent = m === 'open' ? '📂 Mở dự án' : (m === 'new' ? '🆕 Dự án mới' : '📑 Lưu dự án thành bản sao');
        $('pgDlgNameRow').style.display = m === 'open' ? 'none' : '';
        $('pgDlgName').value = '';
        $('pgDlgName').oninput = updateOk;
        $('pgDlgMkdir').style.display = m === 'open' ? 'none' : '';
        $('pgDlgCancel').textContent = promptMode ? 'Để sau' : 'Huỷ';
        $('pgDlgUseDefault').style.display = promptMode ? '' : 'none';
        const sw = $('pgDlgSwitch');
        sw.style.display = promptMode ? '' : 'none';
        sw.textContent = m === 'open' ? '🆕 Tạo dự án mới…' : '📂 Mở dự án có sẵn…';

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

        const intro = m === 'open' ? 'Chọn thư mục có biểu tượng 📦 (thư mục chứa project.pgs.json). Bấm đúp để đi vào bên trong.'
            : 'Chọn thư mục cha, đặt tên dự án — chương trình tạo thư mục con mang tên đó.';
        dlgMsg(intro);
        const start = m === 'open'
            ? (cur ? cur.path.replace(/[\\/][^\\/]+[\\/]?$/, '') : '')
            : (info && info.defaultParent) || '';
        if (!dlg.open) dlg.showModal();
        // browse() xoa dong thong bao khi bat dau nen dat lai SAU khi duyet xong (khong de de len thong bao loi / "thu muc nay la du an PGS")
        browse(start).then(() => {
            if (promptMode) dlgMsg(promptText(), !!(info && info.missingLastProject));
            else if (!$('pgDlgMsg').textContent) dlgMsg(intro);
        });
    }

    // ------------------------------------------------------------------ MENU DU AN (bam logo, kieu "nut Office")
    // Cot trai: Moi / Mo / Luu / Luu thanh (chu gach chan = phim tat khi menu dang mo). Cot phai: du an gan day (phim 1-9).
    // Phim tat chung: Ctrl+S Luu | Ctrl+O Mo | Ctrl+Shift+S Luu thanh | Ctrl+Alt+N Du an moi | Alt+P mo/dong menu | Esc dong menu.
    let menuEl = null, brandEl = null;
    const inRunMode = () => document.body.classList.contains('layout-run-mode');   // che do Chay: khong co menu du an
    const menuOpen = () => !!menuEl && menuEl.classList.contains('open');
    const samePath = (a, b) => String(a || '').replace(/[\\/]+$/, '').toLowerCase() === String(b || '').replace(/[\\/]+$/, '').toLowerCase();

    // px MAN HINH (getBoundingClientRect) -> px CSS (style.left/top): lech nhau dung bang "Kich thuoc giao dien" (root.style.zoom trong prefs.js)
    function cssPerScreenPx() {
        const p = document.createElement('div');
        p.style.cssText = 'position:fixed;left:0;top:0;width:100px;height:1px;visibility:hidden;pointer-events:none';
        document.body.appendChild(p);
        const w = p.getBoundingClientRect().width;
        p.remove();
        return w > 0 ? 100 / w : 1;
    }

    function placeMenu() {
        if (!menuOpen()) return;
        const r = brandEl.getBoundingClientRect(), mr = menuEl.getBoundingClientRect(), k = cssPerScreenPx();
        const left = Math.max(4, Math.min(r.left, window.innerWidth - mr.width - 4));
        const top = Math.max(4, Math.min(r.bottom + 6, window.innerHeight - mr.height - 4));
        menuEl.style.left = `${left * k}px`; menuEl.style.top = `${top * k}px`;
    }

    function paintRecent() {
        const box = $('pgRecentList'); if (!box) return;
        box.textContent = '';
        const recent = ((info && info.recent) || []).slice(0, 9);
        if (!recent.length) {
            const e = document.createElement('div'); e.className = 'pg-fm-empty'; e.textContent = 'Chưa có dự án nào gần đây.'; box.appendChild(e);
            return;
        }
        recent.forEach((r, i) => {
            const isCur = !!(cur && samePath(r.path, cur.path));
            const b = document.createElement('button');
            b.type = 'button'; b.className = 'pg-fm-ritem' + (isCur ? ' current' : '');
            b.setAttribute('role', 'menuitem'); b.dataset.path = r.path; b.title = r.path;
            const num = document.createElement('span'); num.className = 'pg-fm-rnum'; num.textContent = String(i + 1);
            const body = document.createElement('span'); body.className = 'pg-fm-rbody';
            const nm = document.createElement('div'); nm.className = 'pg-fm-rname'; nm.textContent = r.name;
            const pa = document.createElement('div'); pa.className = 'pg-fm-rpath'; pa.textContent = r.path;
            body.append(nm, pa); b.append(num, body);
            b.addEventListener('click', () => { closeMenu(); if (!isCur) openProject(r.path); });
            box.appendChild(b);
        });
    }

    function closeMenu() {
        if (!menuOpen()) return;
        menuEl.classList.remove('open'); brandEl.classList.remove('open'); brandEl.setAttribute('aria-expanded', 'false');
    }

    function openMenu() {
        if (inRunMode() || menuOpen() || (dlg && dlg.open)) return;
        menuEl.classList.add('open'); brandEl.classList.add('open'); brandEl.setAttribute('aria-expanded', 'true');
        paintRecent(); placeMenu();
        menuEl.querySelector('.pg-fm-item')?.focus();
        refresh().then(() => { if (menuOpen()) { paintRecent(); placeMenu(); } });      // danh sach gan day moi nhat tu may chu
    }
    const toggleMenu = () => (menuOpen() ? closeMenu() : openMenu());

    function initMenu() {
        menuEl = $('pgProject'); brandEl = $('pgBrand');
        if (!menuEl || !brandEl) return;
        document.body.appendChild(menuEl);          // ra khoi sidebar: khong bi cat / che boi ngu canh xep lop cua sidebar

        brandEl.addEventListener('click', toggleMenu);
        brandEl.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleMenu(); } });
        menuEl.addEventListener('click', e => { if (e.target.closest('.pg-fm-item')) closeMenu(); });   // chon lenh xong: dong menu (lenh do handler rieng cua nut thuc hien)
        document.addEventListener('mousedown', e => { if (menuOpen() && !menuEl.contains(e.target) && !brandEl.contains(e.target)) closeMenu(); }, true);
        window.addEventListener('resize', placeMenu);
        window.addEventListener('blur', closeMenu);

        // Phim khi menu dang mo: Esc, mui ten, chu gach chan (M/N, O, L/S, T/A), so 1-9 = du an gan day
        document.addEventListener('keydown', e => {
            if (!menuOpen() || e.ctrlKey || e.metaKey) return;
            const items = [...menuEl.querySelectorAll('.pg-fm-item:not(:disabled), .pg-fm-ritem')];
            const idx = items.indexOf(document.activeElement);
            const handled = () => { e.preventDefault(); e.stopImmediatePropagation(); };
            if (e.key === 'Escape') { handled(); closeMenu(); brandEl.focus(); return; }
            if (e.key === 'ArrowDown') { handled(); items[(idx + 1) % items.length]?.focus(); return; }
            if (e.key === 'ArrowUp') { handled(); items[(idx - 1 + items.length) % items.length]?.focus(); return; }
            if (e.key === 'ArrowRight') { handled(); menuEl.querySelector('.pg-fm-ritem')?.focus(); return; }
            if (e.key === 'ArrowLeft') { handled(); menuEl.querySelector('.pg-fm-item')?.focus(); return; }
            if (e.altKey || e.key.length !== 1) return;
            const ch = e.key.toLowerCase();
            if (ch >= '1' && ch <= '9') {
                const r = menuEl.querySelectorAll('.pg-fm-ritem')[Number(ch) - 1];
                if (r) { handled(); r.click(); }
                return;
            }
            const it = [...menuEl.querySelectorAll('.pg-fm-item')].find(x => (x.dataset.keys || '').includes(ch));
            if (it && !it.disabled) { handled(); it.click(); }
        }, true);

        // Phim tat chung (dung ma phim vat ly e.code de khong phu thuoc kieu go / bo go)
        document.addEventListener('keydown', e => {
            if (inRunMode() || (dlg && dlg.open)) return;
            const ctrl = e.ctrlKey || e.metaKey;
            if (e.altKey && !ctrl && !e.shiftKey && e.code === 'KeyP') { e.preventDefault(); toggleMenu(); }
            else if (ctrl && !e.altKey && !e.shiftKey && e.code === 'KeyO') { e.preventDefault(); closeMenu(); openDialog('open'); }
            else if (ctrl && !e.altKey && e.shiftKey && e.code === 'KeyS') { e.preventDefault(); closeMenu(); openDialog('saveas'); }
            else if (ctrl && e.altKey && !e.shiftKey && e.code === 'KeyN') { e.preventDefault(); closeMenu(); openDialog('new'); }   // Ctrl+N thuong bi trinh duyet giu
        });
    }

    // ------------------------------------------------------------------ khoi dong
    document.addEventListener('DOMContentLoaded', () => {
        if (!$('pgProject')) return;                     // trang khong co thanh du an (vd Login)
        $('pgSaveBtn').addEventListener('click', saveAll);
        $('pgOpenBtn').addEventListener('click', () => openDialog('open'));
        $('pgNewBtn').addEventListener('click', () => openDialog('new'));
        $('pgSaveAsBtn').addEventListener('click', () => openDialog('saveas'));
        initMenu();
        refresh().then(promptIfNeeded);                  // sau khi dang nhap: chua co du an nao thi moi mo / tao du an
        // Dang xuat: lan dang nhap sau (cung tab) neu van chua co du an thi moi lai
        document.querySelectorAll('.account-logout-form').forEach(f => f.addEventListener('submit', () => { try { sessionStorage.removeItem(PROMPT_KEY); } catch (e) { /* khong co storage */ } }));
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
