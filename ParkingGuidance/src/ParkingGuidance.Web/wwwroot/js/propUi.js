/* ===========================================================================
 * propUi.js — Bộ chọn màu kiểu Office dùng cho hộp thoại thuộc tính của Layout Editor.
 *
 * Cách dùng: PgColor.autoEnhance(container) — mọi <input type="color"> nằm trong container (kể cả
 * được thêm sau này khi hộp thoại vẽ lại) sẽ được thay bằng 1 nút "ô màu + mã HEX". Bấm nút mở bảng:
 *   • Màu giao diện (Theme Colors): 10 màu gốc × 6 sắc độ (sáng dần / tối dần)
 *   • Màu chuẩn (Standard Colors): 10 màu cơ bản
 *   • Màu gần đây (lưu trên trình duyệt)
 *   • Màu khác…: chọn theo RGB (thanh trượt R/G/B, ô nhập số, mã HEX, bảng chọn của hệ thống)
 *
 * <input type="color"> gốc vẫn nằm trong DOM (ẩn) và giữ nguyên giá trị #rrggbb, đồng thời phát sự kiện
 * 'input' / 'change' như khi chọn màu thường → mã cũ đọc .value và xem trước trực tiếp không phải sửa.
 * =========================================================================== */
(function () {
    'use strict';

    const THEME_BASE = ['#FFFFFF', '#000000', '#E7E6E6', '#44546A', '#4472C4', '#ED7D31', '#A5A5A5', '#FFC000', '#5B9BD5', '#70AD47'];
    const THEME_NAMES = ['Trắng', 'Đen', 'Xám nhạt', 'Xanh than', 'Xanh dương', 'Cam', 'Xám', 'Vàng', 'Xanh da trời', 'Xanh lá'];
    const STANDARD = ['#C00000', '#FF0000', '#FFC000', '#FFFF00', '#92D050', '#00B050', '#00B0F0', '#0070C0', '#002060', '#7030A0'];
    const STANDARD_NAMES = ['Đỏ đậm', 'Đỏ', 'Cam', 'Vàng', 'Xanh lá nhạt', 'Xanh lá', 'Xanh lơ', 'Xanh dương', 'Xanh navy', 'Tím'];

    const RECENT_KEY = 'pg.recentColors';
    const RECENT_MAX = 10;

    // ---------- chuyển đổi màu ----------
    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
    const isHex = (s) => /^#[0-9a-f]{6}$/i.test(s || '');

    function hexToRgb(hex) {
        const n = parseInt(hex.slice(1), 16);
        return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
    }
    function rgbToHex(r, g, b) {
        return '#' + [r, g, b].map(v => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0')).join('');
    }
    function rgbToHsl(r, g, b) {
        r /= 255; g /= 255; b /= 255;
        const max = Math.max(r, g, b), min = Math.min(r, g, b);
        let h = 0, s = 0;
        const l = (max + min) / 2;
        if (max !== min) {
            const d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max === g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return { h, s, l };
    }
    function hslToRgb(h, s, l) {
        if (s === 0) { const v = l * 255; return { r: v, g: v, b: v }; }
        const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        const f = (t) => {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1 / 6) return p + (q - p) * 6 * t;
            if (t < 1 / 2) return q;
            if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
            return p;
        };
        return { r: f(h + 1 / 3) * 255, g: f(h) * 255, b: f(h - 1 / 3) * 255 };
    }

    // Sắc độ kiểu Office: "sáng hơn x%" = L·(1−x)+x ; "tối hơn x%" = L·(1−x)
    function shade(hex, kind, pct) {
        const { r, g, b } = hexToRgb(hex), { h, s, l } = rgbToHsl(r, g, b), p = pct / 100;
        const nl = kind === 'lighter' ? l * (1 - p) + p : l * (1 - p);
        const o = hslToRgb(h, s, nl);
        return rgbToHex(o.r, o.g, o.b).toUpperCase();
    }

    // Bảng 6 hàng × 10 cột: hàng 0 = màu gốc, hàng 1..5 = sắc độ của từng cột
    function themeColumns() {
        return THEME_BASE.map((base, i) => {
            let tints;
            if (i === 0) tints = [['darker', 5], ['darker', 15], ['darker', 25], ['darker', 35], ['darker', 50]];
            else if (i === 1) tints = [['lighter', 50], ['lighter', 35], ['lighter', 25], ['lighter', 15], ['lighter', 5]];
            else tints = [['lighter', 80], ['lighter', 60], ['lighter', 40], ['darker', 25], ['darker', 50]];
            return [base, ...tints.map(([k, p]) => shade(base, k, p))];
        });
    }
    const THEME_GRID = themeColumns();

    // ---------- màu gần đây ----------
    function loadRecent() {
        try {
            const a = JSON.parse(localStorage.getItem(RECENT_KEY) || '[]');
            return Array.isArray(a) ? a.filter(isHex).slice(0, RECENT_MAX) : [];
        } catch { return []; }
    }
    function pushRecent(hex) {
        hex = hex.toUpperCase();
        const all = [...THEME_GRID.flat(), ...STANDARD].map(x => x.toUpperCase());
        if (all.includes(hex)) return;          // màu có sẵn trong bảng thì không cần nhớ
        const list = loadRecent().filter(x => x.toUpperCase() !== hex);
        list.unshift(hex);
        try { localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, RECENT_MAX))); } catch { /* không có storage */ }
    }

    // ---------- nút thay cho <input type="color"> ----------
    function refreshButton(input) {
        const btn = input._pgcBtn;
        if (!btn) return;
        const v = isHex(input.value) ? input.value.toUpperCase() : '#000000';
        btn.querySelector('.pgc-sw').style.background = v;
        btn.querySelector('.pgc-hex').textContent = v;
    }

    function enhanceOne(input) {
        if (input.dataset.pgc) return;
        input.dataset.pgc = '1';
        const wrap = document.createElement('span');
        wrap.className = 'pgc';
        input.parentNode.insertBefore(wrap, input);
        wrap.appendChild(input);
        input.classList.add('pgc-native');
        input.tabIndex = -1;

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'pgc-btn';
        btn.title = 'Chọn màu';
        btn.innerHTML = '<span class="pgc-sw"></span><span class="pgc-hex"></span><span class="pgc-caret">▾</span>';
        wrap.appendChild(btn);
        input._pgcBtn = btn;
        refreshButton(input);
        btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); togglePopup(input, btn); });
    }

    function enhance(root) {
        (root || document).querySelectorAll('input[type="color"]:not([data-pgc])').forEach(enhanceOne);
    }

    // ---------- bảng chọn màu ----------
    let pop = null;   // { el, input, anchor, close }

    function closePopup() {
        if (!pop) return;
        pop.el.remove();
        document.removeEventListener('mousedown', onDocDown, true);
        window.removeEventListener('keydown', onKey, true);
        window.removeEventListener('resize', closePopup);
        document.removeEventListener('scroll', onScroll, true);
        pop = null;
    }
    function onDocDown(e) {
        if (!pop) return;
        if (pop.el.contains(e.target) || pop.anchor.contains(e.target)) return;
        closePopup();
    }
    function onScroll(e) {
        if (pop && !pop.el.contains(e.target)) closePopup();
    }
    function onKey(e) {
        if (e.key === 'Escape' && pop) { e.stopPropagation(); e.preventDefault(); closePopup(); }
    }

    function place() {
        if (!pop) return;
        const r = pop.anchor.getBoundingClientRect(), p = pop.el;
        const w = p.offsetWidth, h = p.offsetHeight, m = 8;
        let left = Math.min(Math.max(m, r.left), window.innerWidth - w - m);
        let top = r.bottom + 4;
        if (top + h > window.innerHeight - m) top = Math.max(m, r.top - h - 4);   // hết chỗ phía dưới → mở lên trên
        p.style.left = left + 'px';
        p.style.top = top + 'px';
    }

    function setColor(input, hex, final) {
        hex = hex.toLowerCase();
        input.value = hex;
        refreshButton(input);
        input.dispatchEvent(new Event('input', { bubbles: true }));
        if (final) input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    const swatch = (hex, name, cur) =>
        `<button type="button" class="pgc-cell${hex.toUpperCase() === cur ? ' sel' : ''}" data-c="${hex}" style="background:${hex}" title="${name ? name + ' · ' : ''}${hex.toUpperCase()}" aria-label="${hex}"></button>`;

    function togglePopup(input, anchor) {
        if (pop && pop.input === input) { closePopup(); return; }
        closePopup();

        const cur = isHex(input.value) ? input.value.toUpperCase() : '#000000';
        const recent = loadRecent();

        const theme = [];
        for (let row = 0; row < 6; row++)
            for (let col = 0; col < 10; col++)
                theme.push(swatch(THEME_GRID[col][row], row === 0 ? THEME_NAMES[col] : THEME_NAMES[col] + ' (sắc độ ' + row + ')', cur));

        const el = document.createElement('div');
        el.className = 'pgc-pop';
        el.setAttribute('role', 'dialog');
        el.setAttribute('aria-label', 'Chọn màu');
        el.innerHTML = `
            <div class="pgc-sec">Màu giao diện</div>
            <div class="pgc-theme">${theme.join('')}</div>
            <div class="pgc-sec">Màu chuẩn</div>
            <div class="pgc-row">${STANDARD.map((c, i) => swatch(c, STANDARD_NAMES[i], cur)).join('')}</div>
            ${recent.length ? `<div class="pgc-sec">Màu gần đây</div><div class="pgc-row pgc-recent">${recent.map(c => swatch(c, '', cur)).join('')}</div>` : ''}
            <button type="button" class="pgc-more"><span class="pgc-more-ico">🎨</span><span>Màu khác (RGB)…</span><span class="pgc-more-caret">▸</span></button>
            <div class="pgc-rgb" hidden>
                <div class="pgc-rgb-top">
                    <div class="pgc-prev" title="Màu đang chọn"></div>
                    <label class="pgc-sys" title="Mở bảng chọn màu của hệ thống (có ống nhỏ giọt)">
                        <input type="color" class="pgc-sysinp" tabindex="-1" /> Bảng màu hệ thống
                    </label>
                </div>
                ${['r', 'g', 'b'].map(k => `
                <div class="pgc-ch"><span class="pgc-chl ${k}">${k.toUpperCase()}</span>
                    <input type="range" min="0" max="255" step="1" data-k="${k}" class="pgc-rng ${k}" />
                    <input type="number" min="0" max="255" step="1" data-k="${k}" class="pgc-num" /></div>`).join('')}
                <div class="pgc-ch"><span class="pgc-chl hex">HEX</span>
                    <input type="text" class="pgc-hexinp" maxlength="7" spellcheck="false" /></div>
            </div>`;
        document.body.appendChild(el);
        pop = { el, input, anchor };

        // --- chọn ô màu ---
        el.addEventListener('click', (e) => {
            const c = e.target.closest('.pgc-cell');
            if (!c) return;
            setColor(input, c.dataset.c, true);
            pushRecent(c.dataset.c);
            closePopup();
        });

        // --- RGB ---
        const rgb = el.querySelector('.pgc-rgb');
        const prev = el.querySelector('.pgc-prev');
        const sys = el.querySelector('.pgc-sysinp');
        const hexInp = el.querySelector('.pgc-hexinp');
        const rngs = [...el.querySelectorAll('.pgc-rng')], nums = [...el.querySelectorAll('.pgc-num')];
        const fill = (hex) => {
            const { r, g, b } = hexToRgb(hex), v = { r, g, b };
            rngs.forEach(x => { x.value = v[x.dataset.k]; });
            nums.forEach(x => { x.value = v[x.dataset.k]; });
            hexInp.value = hex.toUpperCase();
            prev.style.background = hex;
            sys.value = hex.toLowerCase();
            el.querySelectorAll('.pgc-cell.sel').forEach(n => n.classList.remove('sel'));
        };
        const apply = (hex, final) => { fill(hex); setColor(input, hex, final); if (final) pushRecent(hex); };
        const fromChannels = (src, final) => {
            const k = src.dataset.k, v = { r: 0, g: 0, b: 0 };
            rngs.forEach(x => { v[x.dataset.k] = clamp(parseInt(x.value, 10) || 0, 0, 255); });
            v[k] = clamp(parseInt(src.value, 10) || 0, 0, 255);
            apply(rgbToHex(v.r, v.g, v.b), final);
        };
        rngs.concat(nums).forEach(x => {
            x.addEventListener('input', () => fromChannels(x, false));
            x.addEventListener('change', () => fromChannels(x, true));
        });
        hexInp.addEventListener('input', () => {
            let s = hexInp.value.trim();
            if (/^[0-9a-f]{6}$/i.test(s)) s = '#' + s;
            if (isHex(s)) apply(s, false);
        });
        hexInp.addEventListener('change', () => {
            let s = hexInp.value.trim();
            if (/^[0-9a-f]{6}$/i.test(s)) s = '#' + s;
            if (isHex(s)) apply(s, true); else fill(isHex(input.value) ? input.value : '#000000');
        });
        sys.addEventListener('input', () => apply(sys.value, false));
        sys.addEventListener('change', () => apply(sys.value, true));

        el.querySelector('.pgc-more').addEventListener('click', () => {
            rgb.hidden = !rgb.hidden;
            el.querySelector('.pgc-more-caret').textContent = rgb.hidden ? '▸' : '▾';
            fill(isHex(input.value) ? input.value : '#000000');
            place();
        });

        document.addEventListener('mousedown', onDocDown, true);
        window.addEventListener('keydown', onKey, true);
        window.addEventListener('resize', closePopup);
        document.addEventListener('scroll', onScroll, true);
        place();
    }

    // ---------- tự bọc các ô màu được vẽ động ----------
    function autoEnhance(container) {
        if (!container) return;
        enhance(container);
        new MutationObserver(() => {
            enhance(container);
            if (pop && !pop.input.isConnected) closePopup();   // hộp thoại vẽ lại (đổi đối tượng): đóng bảng màu cũ
        }).observe(container, { childList: true, subtree: true });
    }

    window.PgColor = { enhance, autoEnhance, close: closePopup, refresh: refreshButton };
})();
