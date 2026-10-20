// ---------------------------------------------------------------------------
// An / hien cac phan cua giao dien (luu rieng cho tung may trong localStorage):
//   - Thanh menu ben trai (sidebar)
//   - Toan man hinh = an thanh tab + thanh dia chi cua trinh duyet (Fullscreen API, tuong duong F11)
// Trinh duyet bat buoc phai co thao tac bam cua nguoi dung moi cho vao toan man hinh,
// nen che do nay khong tu bat lai khi tai lai trang (dung F11 neu muon giu).
// ---------------------------------------------------------------------------
(function () {
    const KEY = 'pg.hideSidebar';
    const root = document.documentElement;
    const read = () => { try { return localStorage.getItem(KEY) === '1'; } catch { return false; } };
    function applySidebar(hide) {
        root.classList.toggle('sidebar-hidden', hide);
        try { localStorage.setItem(KEY, hide ? '1' : '0'); } catch { }
        document.dispatchEvent(new CustomEvent('pgview'));
        // khung thiet ke dang o che do "vua khung" se tinh lai kich thuoc
        window.dispatchEvent(new Event('resize'));
    }
    root.classList.toggle('sidebar-hidden', read());   // ap dung ngay, khong nhay giao dien

    const isFull = () => !!document.fullscreenElement;
    async function toggleFull() {
        try {
            if (isFull()) await document.exitFullscreen();
            else await root.requestFullscreen();
        } catch (e) { alert('Trình duyệt không cho phép toàn màn hình: ' + e.message + ' (hãy thử nhấn F11)'); }
    }
    document.addEventListener('fullscreenchange', () => {
        root.classList.toggle('is-fullscreen', isFull());
        document.dispatchEvent(new CustomEvent('pgview'));
        window.dispatchEvent(new Event('resize'));
    });

    window.PGView = {
        isSidebarHidden: () => root.classList.contains('sidebar-hidden'),
        setSidebarHidden: applySidebar,
        toggleSidebar: () => applySidebar(!root.classList.contains('sidebar-hidden')),
        isFullscreen: isFull,
        toggleFullscreen: toggleFull
    };
    document.addEventListener('DOMContentLoaded', () => {
        const a = document.getElementById('fabSidebar'), b = document.getElementById('fabFull');
        if (a) a.addEventListener('click', () => window.PGView.toggleSidebar());
        if (b) b.addEventListener('click', toggleFull);
    });
})();
