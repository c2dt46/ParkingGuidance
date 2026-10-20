// ---------------------------------------------------------------------------
// Trang Camera › tab lớn "🧠 Train" gồm 3 phần (tab con):
//   🔖 Train biển số (plate)  →  panel #ptrainTab   (plateTrain.js)
//   🚗 Train xe      (vehicle) →  panel #vtrainTab   (vehicleTrain.js)
//   🔤 Train ký tự   (char)    →  panel #trainTab    (cameraTrain.js)
//
// File này chỉ lo việc CHUYỂN TAB CON và báo cho 3 script trên biết khi nào phần của chúng đang hiện / bị ẩn:
//   • window.pgTrainNav.isActive('plate'|'vehicle'|'char') — tab lớn Train đang mở VÀ tab con đó đang hiện?
//   • sự kiện 'pg:train-nav' (trên document) — bắn sau MỌI lần đổi tab (tab lớn hoặc tab con). Các script nghe sự kiện này
//     để vào/ra phần của mình (nạp dữ liệu, gắn phím tắt, dừng hỏi máy chủ…), thay cho việc nghe riêng từng nút tab.
// Nút tab con dùng class "sub-tab-btn" (KHÔNG dùng "tab-btn") để systemCameras.js không coi là tab lớn.
// Tab con đang xem được nhớ trên trình duyệt (localStorage 'pg.cam.trainSub').
// ---------------------------------------------------------------------------
(function () {
    const SUBS = ['plate', 'vehicle', 'char'];
    const PANEL = { plate: 'ptrainTab', vehicle: 'vtrainTab', char: 'trainTab' };
    const KEY = 'pg.cam.trainSub';

    const hub = document.getElementById('trainhubTab');
    if (!hub) { window.pgTrainNav = { isActive: () => false, current: () => null }; return; }

    let current = 'plate';
    try { const v = localStorage.getItem(KEY); if (SUBS.includes(v)) current = v; } catch { /* trinh duyet chan localStorage */ }

    const hubActive = () => hub.classList.contains('active');
    const panel = sub => document.getElementById(PANEL[sub]);

    window.pgTrainNav = {
        /** Tab lớn Train đang mở và tab con <sub> đang hiện. */
        isActive: sub => hubActive() && current === sub && panel(sub)?.classList.contains('active') === true,
        current: () => current,
        /** Chuyển tới tab con (và mở tab lớn Train nếu chưa mở). */
        open: sub => {
            if (!SUBS.includes(sub)) return;
            if (!hubActive()) document.querySelector('.tab-btn[data-tab="trainhub"]')?.click();
            show(sub);
        }
    };

    function notify() {
        // setTimeout 0: cho listener đổi tab lớn (systemCameras.js, đăng ký trước) bật class 'active' xong rồi mới báo
        setTimeout(() => document.dispatchEvent(new CustomEvent('pg:train-nav', { detail: { sub: current, active: hubActive() } })), 0);
    }

    function show(sub) {
        current = sub;
        try { localStorage.setItem(KEY, sub); } catch { /* bo qua */ }
        SUBS.forEach(k => panel(k)?.classList.toggle('active', k === sub));
        hub.querySelectorAll('.sub-tab-btn').forEach(b => {
            const on = b.dataset.sub === sub;
            b.classList.toggle('active', on);
            b.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        notify();
    }

    hub.querySelectorAll('.sub-tab-btn').forEach(b => b.addEventListener('click', () => show(b.dataset.sub)));

    // đổi tab lớn bất kỳ (kể cả rời / vào tab Train) → báo lại trạng thái
    document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', notify));

    // trạng thái ban đầu: chỉ bật class cho tab con đã nhớ (tab lớn Train vẫn ẩn cho tới khi người dùng bấm)
    SUBS.forEach(k => panel(k)?.classList.toggle('active', k === current));
    hub.querySelectorAll('.sub-tab-btn').forEach(b => {
        const on = b.dataset.sub === current;
        b.classList.toggle('active', on);
        b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
})();
