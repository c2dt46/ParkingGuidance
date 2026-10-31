// ---------------------------------------------------------------------------
// CÀI ĐẶT & HỆ THỐNG > Giấy phép: 3 tab (Tạo yêu cầu · Nhận giấy phép · Tạo giấy phép [cấp 0/1]).
// Chỉ gọi /api/license/*; không đụng tới camera / cảm biến / LED / bãi xe.
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/license';
    const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const KIND = { Disk: '💽 Ổ cứng', Network: '🌐 Card mạng', Board: '🧩 Main' };

    async function api(url, method, body) {
        const res = await fetch(url, {
            method: method || 'GET',
            headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        let data = null;
        try { data = await res.json(); } catch { /* không có nội dung */ }
        if (!res.ok) throw new Error(data?.message || ('HTTP ' + res.status));
        return data;
    }

    async function copyText(text) {
        try { await navigator.clipboard.writeText(text); return true; }
        catch {
            const ta = document.createElement('textarea');
            ta.value = text; document.body.appendChild(ta); ta.select();
            let ok = false;
            try { ok = document.execCommand('copy'); } catch { /* bỏ qua */ }
            ta.remove();
            return ok;
        }
    }

    function fmtDuration(sec) {
        if (sec < 0) return 'Vĩnh viễn';
        const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
        return d + ' ngày ' + h + ' giờ ' + m + ' phút';
    }

    function hwTable(list, withMatch) {
        if (!list || !list.length) return '';
        const rows = list.map((h, i) => `
            <tr>
                <td>${i + 1}</td>
                <td>${KIND[h.kind] || esc(h.kind)}</td>
                <td class="lc-serial">${esc(h.serial)}</td>
                ${withMatch ? `<td>${h.match ? '<span class="lc-ok">✔ Khớp</span>' : '<span class="lc-bad">✖ Không khớp</span>'}</td>` : ''}
            </tr>`).join('');
        return `<table class="data-table lc-table"><thead><tr>
            <th style="width:50px">STT</th><th style="width:130px">Loại</th><th>Số seri</th>${withMatch ? '<th style="width:150px">Máy này</th>' : ''}
            </tr></thead><tbody>${rows}</tbody></table>`;
    }

    // ------------------------------------------------------------------ tab
    function showTab(name) {
        document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
        ['request', 'receive', 'issue'].forEach(t => {
            const el = $('tab-' + t);
            if (el) el.style.display = t === name ? '' : 'none';
        });
        try { sessionStorage.setItem('pg.lic.tab', name); } catch { /* bỏ qua */ }
        if (name === 'receive') loadStatus();
    }
    document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));

    // ------------------------------------------------------------ trạng thái
    function renderStatus(s) {
        const banner = $('lcBanner');
        let cls = 'lc-banner ';
        let text = s.message || '';
        if (s.bypass) {
            cls += 'ok';
            text = '🛡️ Đăng nhập cấp ' + s.level + ': bản quyền mặc định đã được cấp và không bao giờ bị kiểm tra.';
        } else if (s.state === 'Trial' && s.operational) {
            cls += 'bad';
            text = '⏳ Chưa có giấy phép — chạy thử, còn lại ' + fmtDuration(s.remainingSeconds) + '. Hết thời gian này ứng dụng sẽ tắt.';
        } else if (s.operational) {
            cls += 'ok';
            text = '✅ ' + (s.state === 'Permanent' ? 'Giấy phép vĩnh viễn.' : 'Giấy phép còn hạn — còn lại ' + fmtDuration(s.remainingSeconds) + '.');
        } else {
            cls += 'bad';
            text = '⛔ ' + text + (s.state === 'NotActivated' || s.state === 'Expired' || s.state === 'Tampered'
                ? ' Tạo yêu cầu, gửi cho nhà cung cấp rồi kích hoạt giấy phép mới ở tab "Nhận giấy phép".' : '');
        }
        banner.className = cls;
        banner.textContent = text;

        const cur = $('lcCurrent');
        if (!s.durationCode) { cur.innerHTML = '<span class="hint small">Chưa kích hoạt giấy phép nào.</span>'; return; }
        const used = fmtDuration(s.consumedSeconds);
        const left = s.remainingSeconds < 0 ? 'Vĩnh viễn' : fmtDuration(s.remainingSeconds);
        cur.innerHTML =
            `<div class="lc-line"><b>Thời hạn</b>: ${esc(s.durationLabel)}</div>` +
            (s.remainingSeconds < 0 ? '' :
                `<div class="lc-line"><b>Đã dùng</b>: ${esc(used)} · <b>Còn lại</b>: ${esc(left)}</div>`) +
            `<div class="lc-line"><b>Số seri trùng máy này</b>: ${s.matched}/${s.required} (tối thiểu)</div>` +
            hwTable(s.hardware, true);
    }

    async function loadStatus() {
        try { renderStatus(await api(API + '/status')); }
        catch (e) { $('lcBanner').className = 'lc-banner bad'; $('lcBanner').textContent = 'Không đọc được trạng thái: ' + e.message; }
    }

    // ------------------------------------------------------- tab: tạo yêu cầu
    $('lcReqBtn').addEventListener('click', async () => {
        const state = $('lcReqState');
        $('lcReqBtn').disabled = true;
        state.textContent = 'Đang đọc số seri phần cứng…';
        try {
            const r = await api(API + '/request', 'POST', {});
            $('lcReqOut').value = r.request;
            $('lcReqCopy').disabled = false;
            $('lcReqHw').innerHTML = '<h3 class="lc-sub">Số seri có trong yêu cầu</h3>' + hwTable(r.hardware, false);
            state.textContent = 'Đã tạo yêu cầu.';
        } catch (e) {
            state.textContent = '';
            $('lcReqOut').value = '';
            $('lcReqCopy').disabled = true;
            $('lcReqHw').innerHTML = `<div class="lc-msg bad">${esc(e.message)}</div>`;
        } finally {
            $('lcReqBtn').disabled = false;
        }
    });
    $('lcReqCopy').addEventListener('click', async () => {
        $('lcReqState').textContent = (await copyText($('lcReqOut').value)) ? 'Đã sao chép.' : 'Không sao chép được, hãy bôi đen và Ctrl+C.';
    });

    // ----------------------------------------------------- tab: nhận giấy phép
    let canActivate = false;
    let previewSeq = 0;
    let previewTimer = null;

    function clearPreview() {
        previewSeq++;
        canActivate = false;
        $('lcActivate').disabled = true;
        $('lcPreview').style.display = 'none';
        $('lcPvBody').innerHTML = '';
        $('lcPvMsg').className = 'lc-msg';
        $('lcPvMsg').textContent = '';
    }

    async function runPreview() {
        const text = $('lcLicIn').value.trim();
        if (!text) { clearPreview(); return; }
        const seq = ++previewSeq;
        try {
            const r = await api(API + '/preview', 'POST', { license: text });
            if (seq !== previewSeq) return;
            $('lcPvDuration').textContent = r.durationLabel;
            $('lcPvBody').innerHTML = r.hardware.map((h, i) => `
                <tr><td>${i + 1}</td><td>${KIND[h.kind] || esc(h.kind)}</td><td class="lc-serial">${esc(h.serial)}</td>
                <td>${h.match ? '<span class="lc-ok">✔ Khớp</span>' : '<span class="lc-bad">✖ Không khớp</span>'}</td></tr>`).join('');
            $('lcPreview').style.display = '';
            canActivate = !!r.canActivate;
            $('lcActivate').disabled = !canActivate;
            $('lcPvMsg').className = 'lc-msg ' + (canActivate ? 'ok' : 'bad');
            $('lcPvMsg').textContent = r.message || '';
        } catch (e) {
            if (seq !== previewSeq) return;
            canActivate = false;
            $('lcActivate').disabled = true;
            $('lcPreview').style.display = 'none';
            $('lcPvMsg').className = 'lc-msg bad';
            $('lcPvMsg').textContent = e.message;
        }
    }

    $('lcLicIn').addEventListener('input', () => {
        clearTimeout(previewTimer);
        previewTimer = setTimeout(runPreview, 400);
    });

    $('lcCancel').addEventListener('click', () => {
        clearTimeout(previewTimer);
        $('lcLicIn').value = '';
        clearPreview();
    });

    $('lcActivate').addEventListener('click', async () => {
        if (!canActivate) return;
        $('lcActivate').disabled = true;
        try {
            const r = await api(API + '/activate', 'POST', { license: $('lcLicIn').value.trim() });
            $('lcLicIn').value = '';
            clearPreview();
            $('lcPvMsg').className = 'lc-msg ok';
            $('lcPvMsg').textContent = r.message;
            await loadStatus();
        } catch (e) {
            $('lcPvMsg').className = 'lc-msg bad';
            $('lcPvMsg').textContent = e.message;
            $('lcActivate').disabled = !canActivate;
        }
    });

    // ------------------------------------------------ tab: tạo giấy phép (0/1)
    if ($('lcIssBtn')) {
        let infoTimer = null;
        let infoSeq = 0;

        $('lcIssReq').addEventListener('input', () => {
            clearTimeout(infoTimer);
            infoTimer = setTimeout(async () => {
                const text = $('lcIssReq').value.trim();
                const seq = ++infoSeq;
                if (!text) { $('lcIssHw').innerHTML = ''; return; }
                try {
                    const r = await api(API + '/request-info', 'POST', { request: text });
                    if (seq === infoSeq) $('lcIssHw').innerHTML = hwTable(r.hardware, false);
                } catch (e) {
                    if (seq === infoSeq) $('lcIssHw').innerHTML = `<div class="lc-msg bad">${esc(e.message)}</div>`;
                }
            }, 400);
        });

        $('lcIssBtn').addEventListener('click', async () => {
            const state = $('lcIssState');
            $('lcIssBtn').disabled = true;
            state.textContent = '';
            try {
                const r = await api(API + '/issue', 'POST', { request: $('lcIssReq').value.trim(), duration: $('lcIssDur').value });
                $('lcIssOut').value = r.license;
                $('lcIssCopy').disabled = false;
                state.textContent = 'Đã tạo giấy phép (' + r.durationLabel + ').';
            } catch (e) {
                $('lcIssOut').value = '';
                $('lcIssCopy').disabled = true;
                state.textContent = e.message;
            } finally {
                $('lcIssBtn').disabled = false;
            }
        });

        $('lcIssCopy').addEventListener('click', async () => {
            $('lcIssState').textContent = (await copyText($('lcIssOut').value)) ? 'Đã sao chép.' : 'Không sao chép được, hãy bôi đen và Ctrl+C.';
        });
    }

    // ------------------------------------------------------------------ khởi động
    // Làm mới trạng thái mỗi 10 giây, chỉ khi tab trình duyệt đang hiện (không tốn tài nguyên khi ẩn).
    setInterval(() => { if (!document.hidden) loadStatus(); }, 10000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) loadStatus(); });

    let startTab = 'request';
    try { startTab = sessionStorage.getItem('pg.lic.tab') || 'request'; } catch { /* bỏ qua */ }
    if (!document.querySelector('.tab-btn[data-tab="' + startTab + '"]')) startTab = 'request';
    showTab(startTab);
    loadStatus();
})();
