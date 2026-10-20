// ---------------------------------------------------------------------------
// An / hien cac bang lon. Them thuoc tinh data-collapsible="<ma>" vao <section class="panel">: nut "Ẩn bảng / Hiện bảng" duoc chen
// vao tieu de <h2>, an di moi thu con lai cua khung (tieu de va so dem van hien). Lua chon duoc nho theo trinh duyet.
// An chi la thu gon (class pc-hidden), khong xoa du lieu va khong dung toi cac doan JS khac dang bat / tat phan tu ben trong.
// ---------------------------------------------------------------------------
(function () {
    const KEY = 'pg.collapsed.';
    const api = {};                       // window.pgPanels.show(ma): mo khung dang an (vd khi "tim mau" tu bang ket qua train)
    window.pgPanels = { show: id => { if (api[id]) api[id](false); } };
    document.querySelectorAll('section[data-collapsible]').forEach(sec => {
        const id = sec.dataset.collapsible;
        const head = sec.querySelector(':scope > h2');
        if (!head) return;

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn secondary btn-sm panel-toggle';
        head.appendChild(btn);

        let hidden = false;
        try { hidden = localStorage.getItem(KEY + id) === '1'; } catch { /* che do rieng tu: khong nho */ }

        function apply() {
            [...sec.children].forEach(ch => { if (ch !== head) ch.classList.toggle('pc-hidden', hidden); });
            btn.textContent = hidden ? '👁 Hiện bảng' : '🙈 Ẩn bảng';
            btn.setAttribute('aria-expanded', String(!hidden));
        }
        apply();

        api[id] = value => { hidden = value; apply(); try { localStorage.setItem(KEY + id, hidden ? '1' : '0'); } catch { /* bo qua */ } };

        btn.addEventListener('click', () => {
            hidden = !hidden;
            apply();
            try { localStorage.setItem(KEY + id, hidden ? '1' : '0'); } catch { /* bo qua */ }
        });
    });
})();
