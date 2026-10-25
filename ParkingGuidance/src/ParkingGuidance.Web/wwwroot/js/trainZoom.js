/* trainZoom.js — phóng to bằng CHUỘT PHẢI cho các canvas khoanh vùng ở tab Train (Train biển, Train xe).
   • LĂN CHUỘT → phóng to / thu nhỏ quanh vị trí con trỏ (giống tab Train ký tự).
   • Giữ chuột PHẢI, kéo một khung → phóng to đúng vùng đó. Bấm chuột phải (không kéo) → về toàn ảnh.
   • Giữ phím SPACE rồi kéo chuột trái, hoặc giữ chuột GIỮA kéo → di chuyển ảnh khi đang phóng.
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
        // Phong/thu quanh diem (cx, cy) tinh theo pixel canvas: diem anh duoi con tro giu nguyen vi tri tren man hinh
        v.zoomAt = (cx, cy, f) => {
            if (!size()) return;
            const s0 = v.scale(), ix = v.vx + cx / s0, iy = v.vy + cy / s0;
            const s1 = clamp(s0 * f, v.base, v.base * MAX_Z);
            v.z = s1 / v.base;
            v.vx = ix - cx / s1; v.vy = iy - cy / s1;
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

        // ------------------------------------------------------------------ con lan: zoom quanh con tro
        canvas.addEventListener('wheel', e => {
            if (!size() || e.ctrlKey) return;                // Ctrl + lan: de trinh duyet tu phong/thu trang
            e.preventDefault();
            const p = v.pt(e);
            v.zoomAt(p.x, p.y, clamp(Math.pow(1.0015, -e.deltaY * (e.deltaMode === 1 ? 33 : 1)), 0.8, 1.25));   // ~1.16 moi nac cua chuot thuong; muot voi touchpad
        }, { passive: false });

        // ------------------------------------------------------------------ phim Space + keo chuot trai = di chuyen anh
        let spaceDown = false, hover = false;
        const typing = t => t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
        canvas.addEventListener('mouseenter', () => { hover = true; });
        canvas.addEventListener('mouseleave', () => { hover = false; });
        window.addEventListener('keydown', e => {
            if (e.code !== 'Space' || typing(e.target) || !size()) return;
            spaceDown = true;
            if (hover) { e.preventDefault(); canvas.style.cursor = 'grab'; }     // khong de Space cuon trang khi dang tro vao anh
        });
        window.addEventListener('keyup', e => { if (e.code === 'Space') { spaceDown = false; if (!v.pan) canvas.style.cursor = ''; } });
        window.addEventListener('blur', () => { spaceDown = false; });

        // ------------------------------------------------------------------ chuot phai / giua
        const block = e => e.preventDefault();               // chan menu chuot phai ke ca khi tha chuot ngoai canvas
        canvas.addEventListener('contextmenu', e => e.preventDefault());
        canvas.addEventListener('mousedown', e => {
            const spacePan = e.button === 0 && spaceDown;
            if (!size() || (e.button !== 2 && e.button !== 1 && !spacePan)) return;
            e.preventDefault();
            if (spacePan) e.stopImmediatePropagation();      // Space + chuot trai la di chuyen anh, khong phai ve khung
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
            v.sel = null; v.pan = null; canvas.style.cursor = spaceDown && hover ? 'grab' : '';
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
            v.barEl.querySelector('.pg-zoom-fit').style.visibility = v.z > 1.01 ? 'visible' : 'hidden';   // giu cho: thanh khong doi chieu cao -> anh khong bi day xuong khi bat dau phong
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
                '<span style="opacity:.85;">🔍 <b>Lăn chuột</b> = zoom tại con trỏ · <b>chuột phải</b> kéo khung = phóng vùng đó, bấm chuột phải = về toàn ảnh · <b>Space</b> + kéo (hoặc chuột giữa) = di chuyển ảnh' +
                (q ? ' · <span class="pg-q-hint"></span>' : '') + '</span>' +
                '<span class="pg-zoom-lbl" style="font-weight:600;display:inline-block;min-width:150px;"></span>' +
                '<button type="button" class="btn secondary btn-sm pg-zoom-fit" style="visibility:hidden;">⛶ Vừa khung</button>';
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

    // Hai nut lon "Anh truoc" / "Anh sau" nam o HAI BEN anh (giua chieu cao) + nhan "3 / 15" o day anh.
    // stage: phan tu chua canvas. o = { onPrev, onNext, prevTitle, nextTitle }. Tra ve { update(coAnhTruoc, coAnhSau, nhan) }.
    function navButtons(stage, o) {
        if (getComputedStyle(stage).position === 'static') stage.style.position = 'relative';
        const base = 'position:absolute;top:50%;transform:translateY(-50%);z-index:6;width:40px;height:84px;border:0;border-radius:10px;' +
            'background:rgba(15,23,42,.55);color:#fff;font-size:34px;line-height:1;cursor:pointer;padding:0;transition:opacity .15s,background .15s;';
        const mk = (side, text, title, fn) => {
            const b = document.createElement('button');
            b.type = 'button'; b.className = 'pg-nav pg-nav-' + side; b.textContent = text; b.title = title;
            b.style.cssText = base + side + ':6px;opacity:.7;';
            b.addEventListener('mouseenter', () => { if (!b.disabled) { b.style.opacity = '1'; b.style.background = 'rgba(37,99,235,.85)'; } });
            b.addEventListener('mouseleave', () => { b.style.opacity = b.disabled ? '.2' : '.7'; b.style.background = 'rgba(15,23,42,.55)'; });
            b.addEventListener('mousedown', e => e.stopPropagation());     // bam nut khong tac dong len anh
            b.addEventListener('click', e => { e.stopPropagation(); if (fn) fn(); });
            return b;
        };
        const prev = mk('left', '‹', o.prevTitle || 'Ảnh trước (←)', o.onPrev), next = mk('right', '›', o.nextTitle || 'Ảnh sau (→)', o.onNext);
        const pill = document.createElement('div');
        pill.style.cssText = 'position:absolute;left:50%;bottom:8px;transform:translateX(-50%);z-index:6;padding:2px 12px;border-radius:12px;' +
            'background:rgba(15,23,42,.6);color:#fff;font-size:12px;pointer-events:none;display:none;';
        stage.append(prev, next, pill);
        prev.disabled = next.disabled = true; prev.style.opacity = next.style.opacity = '.2';
        return {
            update(canPrev, canNext, label) {
                prev.disabled = !canPrev; next.disabled = !canNext;
                prev.style.opacity = canPrev ? '.7' : '.2'; next.style.opacity = canNext ? '.7' : '.2';
                prev.style.cursor = canPrev ? 'pointer' : 'default'; next.style.cursor = canNext ? 'pointer' : 'default';
                pill.textContent = label || ''; pill.style.display = label ? '' : 'none';
            },
        };
    }

    window.PgZoom = { attach, orderQuad, navButtons };
})();
