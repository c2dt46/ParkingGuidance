// ---------------------------------------------------------------------------
// (Cai dat > Giao dien & ngon ngu) Chon trinh duyet mo giao dien Giam sat (Chrome / Edge / Firefox / Safari...).
// May chu tim va khoi chay trinh duyet duoc chon; neu khong ton tai thi mo trinh duyet mac dinh.
// Lua chon duoc luu vao settings.json (khong mat khi khoi dong lai).
// ---------------------------------------------------------------------------
(function () {
    const select = document.getElementById('browserSelect');
    const openBtn = document.getElementById('openBrowserBtn');
    const status = document.getElementById('browserStatus');
    if (!select || !openBtn) return;

    const escHtml = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    let statusTimer = null;
    function say(text, kind) {
        status.textContent = text || '';
        status.className = 'header-status' + (kind ? ' ' + kind : '');
        clearTimeout(statusTimer);
        if (text) statusTimer = setTimeout(() => { status.textContent = ''; status.className = 'header-status'; }, 8000);
    }

    async function loadBrowsers() {
        try {
            const res = await fetch('/api/system/browser');
            if (!res.ok) throw new Error('HTTP ' + res.status);
            const data = await res.json();
            select.innerHTML = data.browsers.map(b =>
                `<option value="${escHtml(b.id)}">${escHtml(b.name)}${b.installed ? '' : ' (không tìm thấy)'}</option>`).join('');
            select.value = data.browsers.some(b => b.id === data.selected) ? data.selected : 'default';
        } catch (err) {
            select.innerHTML = '<option value="default">Mặc định của hệ thống</option>';
            say('⚠️ Không tải được danh sách trình duyệt.', 'warn');
        }
    }

    select.addEventListener('change', async () => {
        try {
            const res = await fetch('/api/system/browser', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ browser: select.value })
            });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            const opt = select.options[select.selectedIndex];
            say(opt.text.includes('không tìm thấy')
                ? 'Đã lưu. Máy chưa có trình duyệt này → khi mở sẽ dùng trình duyệt mặc định.'
                : 'Đã lưu lựa chọn.', opt.text.includes('không tìm thấy') ? 'warn' : 'ok');
        } catch (err) {
            say('⚠️ Không lưu được lựa chọn: ' + err.message, 'warn');
        }
    });

    openBtn.addEventListener('click', async () => {
        openBtn.disabled = true;
        try {
            const res = await fetch('/api/system/browser/open', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ browser: select.value, url: location.origin + '/' })
            });
            const data = await res.json().catch(() => ({}));
            if (!res.ok || data.success === false) {
                say('⚠️ ' + (data.message || ('Lỗi HTTP ' + res.status)), 'warn');
            } else {
                say((data.fellBack ? '⚠️ ' : '✅ ') + data.message, data.fellBack ? 'warn' : 'ok');
            }
        } catch (err) {
            say('⚠️ Không mở được trình duyệt: ' + err.message, 'warn');
        } finally {
            openBtn.disabled = false;
        }
    });

    loadBrowsers();
})();
