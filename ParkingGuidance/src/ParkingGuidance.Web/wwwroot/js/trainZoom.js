/* trainZoom.js — phóng to bằng CHUỘT PHẢI cho các canvas khoanh vùng ở tab Train (Train biển, Train xe).
   • Giữ chuột PHẢI, kéo một khung → phóng to đúng vùng đó (làm lại nhiều lần để phóng sâu hơn).
   • Bấm chuột phải (không kéo) → về toàn ảnh. Giữ chuột GIỮA kéo → di chuyển ảnh khi đang phóng.
   • Chuột TRÁI vẫn khoanh vùng như bình thường (trên ảnh đã phóng).
   Cách dùng (xem plateTrain.js):
     const view = PgZoom.attach(canvas, { size: () => ({ w, h }) | null, onChange: v => {...vẽ lại...}, bar: { quad: { get, set } } });
     – Khi bố trí lại canvas (ảnh mới / đổi cỡ): view.setBase(px canvas mỗi px ảnh khi chưa zoom).
     – Khi vẽ: tỉ lệ = view.scale(); góc trên-trái vùng đang xem = (view.vx, view.vy) theo px ảnh; cuối hàm vẽ gọi view.drawOverlay(ctx).
     – Đổi toạ độ chuột sang px ảnh: view.toImg(e). */
(function () {
    'use strict';
    const MAX_Z = 40, MIN_DRAG = 6;
    const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

    function attach(canvas, o) {
        const v = { z: 1, vx: 0, vy: 0, base: 1, sel: null, pan: null, barEl: null };
        const size = () => (o.size && o.size()) || null;
        const redraw = () => { if (o.onChange) o.onChange(v); };

        v.scale = () => v.base * v.z;
        // Vi tri chuot theo pixel cua canvas — theo TY LE kich thuoc hien thi, nen dung ca khi trang bi phong/thu (zoom trinh duyet, "Kich thuoc giao dien")
        v.pt = e => {
            const r = canvas.getBoundingClientRect();
            return { x: (e.clientX - r.left) * (canvas.width / (r.width || 1)), y: (e.clientY - r.top) * (canvas.height / (r.height || 1)) };
        };
        v.toImg = e => { const p = v.pt(e), s = v.scale(); return { x: v.vx + p.x / s, y: v.vy + p.y / s }; };

        function clampView() {
            const im = size();
            if (!im) return;
            const s = v.scale(), vw = canvas.width / s, vh = canvas.height / s;
            v.vx = vw >= im.w ? (im.w - vw) / 2 : clamp(v.vx, 0, im.w - vw);
            v.vy = vh >= im.h ? (im.h - vh) / 2 : clamp(v.vy, 0, im.h - vh);
        }
        function sync() { clampView(); updateBar(); redraw(); }

        v.setBase = b => { v.base = b; v.z = 1; v.vx = 0; v.vy = 0; v.sel = null; v.pan = null; updateBar(); };
        v.reset = () => { v.z = 1; v.vx = 0; v.vy = 0; sync(); };
        // Phong vao hinh chu nhat (toa do px anh)
        v.zoomRect = (x0, y0, x1, y1) => {
            if (!size()) return;
            const w = Math.max(4, Math.abs(x1 - x0)), h = Math.max(4, Math.abs(y1 - y0));
            const s = clamp(Math.min(canvas.width / w, canvas.height / h) * 0.97, v.base, v.base * MAX_Z);
            v.z = s / v.base;
            v.vx = (x0 + x1) / 2 - canvas.width / s / 2;
            v.vy = (y0 + y1) / 2 - canvas.height / s / 2;
            sync();
        };
        v.drawOverlay = ctx => {
            if (!v.sel) return;
            const a = v.sel, x = Math.min(a.x0, a.x1), y = Math.min(a.y0, a.y1), w = Math.abs(a.x1 - a.x0), h = Math.abs(a.y1 - a.y0);
            ctx.save();
            ctx.setLineDash([6, 4]); ctx.lineWidth = 2; ctx.strokeStyle = '#38bdf8'; ctx.fillStyle = 'rgba(56,189,248,.16)';
            ctx.fillRect(x, y, w, h); ctx.strokeRect(x, y, w, h);
            ctx.restore();
        };

        // ------------------------------------------------------------------ chuot phai / giua
        const block = e => e.preventDefault();               // chan menu chuot phai ke ca khi tha chuot ngoai canvas
        canvas.addEventListener('contextmenu', e => e.preventDefault());
        canvas.addEventListener('mousedown', e => {
            if (!size() || (e.button !== 2 && e.button !== 1)) return;
            e.preventDefault();
            const p = v.pt(e);
            if (e.button === 2) v.sel = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
            else { v.pan = { x: p.x, y: p.y, vx: v.vx, vy: v.vy }; canvas.style.cursor = 'grabbing'; }
            window.addEventListener('mousemove', onMove);
            window.addEventListener('mouseup', onUp);
            window.addEventListener('contextmenu', block, true);
        }, true);
        function onMove(e) {
            if (!size()) return;
            const p = v.pt(e);
            if (v.sel) {
                v.sel.x1 = clamp(p.x, 0, canvas.width); v.sel.y1 = clamp(p.y, 0, canvas.height);
                redraw();
            } else if (v.pan) {
                const s = v.scale();
                v.vx = v.pan.vx - (p.x - v.pan.x) / s; v.vy = v.pan.vy - (p.y - v.pan.y) / s;
                sync();
            }
        }
        function onUp() {
            window.removeEventListener('mousemove', onMove);
            window.removeEventListener('mouseup', onUp);
            setTimeout(() => window.removeEventListener('contextmenu', block, true), 500);
            const a = v.sel;
            v.sel = null; v.pan = null; canvas.style.cursor = '';
            if (!a || !size()) { redraw(); return; }
            if (Math.abs(a.x1 - a.x0) < MIN_DRAG && Math.abs(a.y1 - a.y0) < MIN_DRAG) { v.reset(); return; }   // bam chuot phai (khong keo) = ve toan anh
            const s = v.scale();
            v.zoomRect(v.vx + Math.min(a.x0, a.x1) / s, v.vy + Math.min(a.y0, a.y1) / s, v.vx + Math.max(a.x0, a.x1) / s, v.vy + Math.max(a.y0, a.y1) / s);
        }

        // ------------------------------------------------------------------ thanh huong dan phia tren canvas (+ nut chon che do 4 diem neu co)
        function updateBar() {
            if (!v.barEl) return;
            const lbl = v.barEl.querySelector('.pg-zoom-lbl');
            lbl.textContent = v.z > 1.01 ? `🔍 đang phóng ×${v.z.toFixed(1)}` : '';
            v.barEl.querySelector('.pg-zoom-fit').style.display = v.z > 1.01 ? '' : 'none';
            const q = o.bar && o.bar.quad;
            if (q) v.barEl.querySelectorAll('[data-q]').forEach(b => {
                const on = (b.dataset.q === '1') === !!q.get();
                b.classList.toggle('primary', on); b.classList.toggle('secondary', !on);
            });
        }
        if (o.bar) {
            const q = o.bar.quad;
            const el = document.createElement('div');
            el.className = 'pg-zoom-bar';
            el.style.cssText = 'display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:4px 0;font-size:12px;';
            el.innerHTML =
                (q ? '<span><button type="button" class="btn secondary btn-sm" data-q="1" title="Bấm chuột trái vào 4 GÓC của biển số → khung ôm sát đúng biển">📍 Chọn 4 điểm</button> ' +
                     '<button type="button" class="btn secondary btn-sm" data-q="0" title="Kéo một khung chữ nhật như trước">▭ Kéo khung</button></span>' : '') +
                '<span style="opacity:.85;">🔍 <b>Chuột phải</b> kéo một khung = phóng to vùng đó · bấm chuột phải = về toàn ảnh · giữ chuột giữa kéo = di chuyển' +
                (q ? ' · <span class="pg-q-hint"></span>' : '') + '</span>' +
                '<span class="pg-zoom-lbl" style="font-weight:600;"></span>' +
                '<button type="button" class="btn secondary btn-sm pg-zoom-fit" style="display:none;">⛶ Vừa khung</button>';
            canvas.parentElement.insertAdjacentElement('beforebegin', el);
            el.querySelector('.pg-zoom-fit').addEventListener('click', () => v.reset());
            if (q) el.querySelectorAll('[data-q]').forEach(b => b.addEventListener('click', () => { q.set(b.dataset.q === '1'); updateBar(); redraw(); }));
            v.barEl = el;
            updateBar();
        }
        v.setHint = t => { if (v.barEl) { const h = v.barEl.querySelector('.pg-q-hint'); if (h) h.textContent = t || ''; } };
        v.refreshBar = updateBar;
        return v;
    }

    // Sap xep 4 diem thanh: tren-trai, tren-phai, duoi-phai, duoi-trai (de ve da giac / luu 4 goc)
    function orderQuad(pts) {
        if (pts.length !== 4) return pts.slice();
        const sum = p => p.x + p.y, dif = p => p.x - p.y;
        const tl = pts.reduce((a, b) => (sum(b) < sum(a) ? b : a)), br = pts.reduce((a, b) => (sum(b) > sum(a) ? b : a));
        const tr = pts.reduce((a, b) => (dif(b) > dif(a) ? b : a)), bl = pts.reduce((a, b) => (dif(b) < dif(a) ? b : a));
        const out = [tl, tr, br, bl];
        return new Set(out).size === 4 ? out : pts.slice();   // diem trung / thang hang: giu thu tu bam
    }

    window.PgZoom = { attach, orderQuad };
})();
