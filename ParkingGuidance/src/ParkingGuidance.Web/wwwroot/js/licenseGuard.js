// ---------------------------------------------------------------------------
// Canh giữ bản quyền cho tài khoản cấp 2 / 3 (layout chỉ nạp file này cho 2 cấp đó; cấp 0/1 không bao giờ chạy).
// Mỗi 60 giây hỏi /api/license/status (chỉ đọc bộ nhớ ở máy chủ). Nếu giấy phép đang dùng được rồi bỗng KHÔNG dùng
// được (hết hạn, lệch phần cứng, dữ liệu hỏng) thì hiện thông báo → bấm "Xác nhận" → máy chủ tắt ứng dụng.
// ---------------------------------------------------------------------------
(function () {
    const POLL_MS = 60000;
    let wasOperational = null;      // null = chưa biết; chỉ báo khi chuyển từ "dùng được" sang "không dùng được"
    let shown = false;
    let timer = null;

    const MESSAGES = {
        Expired: 'Thời hạn sử dụng đã hết. Ứng dụng sẽ được tắt.',
        HardwareMismatch: 'Phần cứng máy không còn khớp với giấy phép (cần ít nhất 2 số seri trùng). Ứng dụng sẽ được tắt.',
        Tampered: 'Dữ liệu bản quyền bị hỏng hoặc bị chỉnh sửa. Ứng dụng sẽ được tắt.',
        NotActivated: 'Chưa có giấy phép hợp lệ và đã hết 15 phút chạy thử. Ứng dụng sẽ được tắt.'
    };

    function showNotice(state) {
        if (shown) return;
        shown = true;
        clearInterval(timer);

        const overlay = document.createElement('div');
        overlay.style.cssText = 'position:fixed;inset:0;z-index:99999;background:rgba(0,0,0,.72);display:grid;place-items:center;padding:24px;';
        const box = document.createElement('div');
        box.setAttribute('role', 'alertdialog');
        box.setAttribute('aria-modal', 'true');
        box.style.cssText = 'max-width:460px;width:100%;background:#17223b;color:#e6ebf5;border:1px solid #ef4444;border-radius:12px;padding:24px;font:inherit;';

        const title = document.createElement('h2');
        title.textContent = '⛔ Giấy phép hết hiệu lực';
        title.style.cssText = 'margin:0 0 12px;font-size:20px;';
        const msg = document.createElement('p');
        msg.textContent = (MESSAGES[state] || MESSAGES.NotActivated) + ' Liên hệ nhà cung cấp để nhận giấy phép mới.';
        msg.style.cssText = 'margin:0 0 20px;line-height:1.5;';
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = 'Xác nhận';
        btn.style.cssText = 'padding:10px 22px;border:0;border-radius:8px;background:#ef4444;color:#fff;font:inherit;font-weight:700;cursor:pointer;';

        btn.addEventListener('click', async () => {
            btn.disabled = true;
            try { await fetch('/api/license/shutdown', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' }); }
            catch { /* máy chủ có thể đã tắt */ }
            title.textContent = 'Ứng dụng đã tắt';
            msg.textContent = 'Bạn có thể đóng tab này.';
            btn.remove();
            try { window.close(); } catch { /* trình duyệt có thể chặn */ }
        });

        box.append(title, msg, btn);
        overlay.appendChild(box);
        document.body.appendChild(overlay);
        btn.focus();
    }

    async function check() {
        if (shown || document.hidden) return;
        try {
            const res = await fetch('/api/license/status', { cache: 'no-store' });
            if (!res.ok) return;
            const s = await res.json();
            if (s.bypass) { clearInterval(timer); return; }          // cấp 0/1: không kiểm tra
            if (s.operational) { wasOperational = true; return; }
            if (wasOperational === true) showNotice(s.state);          // vừa chuyển sang "không dùng được"
            else wasOperational = false;                              // mở trang khi đã hết: middleware lo, không bật thông báo
        } catch { /* mất kết nối: bỏ qua */ }
    }

    timer = setInterval(check, POLL_MS);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) check(); });
    check();
})();
