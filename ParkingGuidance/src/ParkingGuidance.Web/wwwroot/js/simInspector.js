/* 🔎 Soi ảnh — trình xem ảnh để KIỂM CHỨNG biển đọc đúng hay sai (tab Mô phỏng).
   • Phóng to bằng con lăn (đến 64 lần), kéo để di chuyển, nhấp đúp để phóng to tại điểm trỏ.
   • ✂️ Chọn vùng để zoom: quét một khung trên ảnh → phóng đúng vùng đó. 💾 Lưu vùng đang xem ra PNG (pixel gốc).
   • Chỉnh sáng / tương phản / gamma / làm nét / xám / đảo màu — CHỈ để nhìn, không đổi ảnh gốc hay kết quả nhận diện.
   • Ảnh biển cắt sẵn ở cột bên phải đặt cạnh chuỗi đọc được, và danh sách các xe/biển để nhảy tới từng biển (◀ ▶).
   Dùng: window.SimInspector.open({ source, title, boxes:[{x,y,w,h,color,dashed,label}], targets:[{title,rect:{x,y,w,h},text,conf,stage,hasPlateBox,index}], start, focus:{x,y,scale} })
   Toạ độ boxes/targets tính theo pixel của source. */
(function () {
    'use strict';
    const MAX_SCALE = 64;
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const srcW = s => s.naturalWidth || s.width, srcH = s => s.naturalHeight || s.height;
    const defaults = () => ({ bright: 100, contrast: 100, gamma: 100, sharp: 0, gray: false, inv: false });

    let root = null, els = {}, st = null;

    const CSS = `
.si-root{position:fixed;inset:0;z-index:10000;display:none;align-items:center;justify-content:center;background:rgba(2,6,15,.82);}
.si-box{display:flex;flex-direction:column;width:min(1500px,98vw);height:min(920px,96vh);background:#0f172a;color:#e5e7eb;border:1px solid #334155;border-radius:10px;overflow:hidden;font:13px/1.4 system-ui,Segoe UI,sans-serif;}
.si-head,.si-foot{display:flex;align-items:center;gap:8px;padding:8px 12px;background:#111c33;flex-wrap:wrap;}
.si-head{border-bottom:1px solid #334155;} .si-foot{border-top:1px solid #334155;color:#94a3b8;font-size:12px;}
.si-sp{flex:1;} .si-dim{color:#94a3b8;}
.si-body{display:flex;flex:1;min-height:0;}
.si-stage{position:relative;flex:1;min-width:0;background:#0b1220;}
.si-stage canvas{position:absolute;inset:0;width:100%;height:100%;display:block;touch-action:none;}
.si-side{width:300px;flex:none;overflow:auto;border-left:1px solid #334155;background:#0f172a;padding:8px 10px;}
.si-sec{margin-bottom:12px;} .si-sec-t{font-weight:600;color:#cbd5e1;margin-bottom:4px;}
.si-read{font:700 26px/1.2 Consolas,Menlo,monospace;letter-spacing:1px;color:#4ade80;word-break:break-all;} .si-read.none{color:#fbbf24;font-size:15px;letter-spacing:0;}
.si-plate{display:block;max-width:100%;margin-top:6px;border:1px solid #334155;background:#000;border-radius:4px;}
.si-row{display:flex;gap:6px;flex-wrap:wrap;margin-bottom:6px;}
.si-btn{background:#1e293b;color:#e5e7eb;border:1px solid #475569;border-radius:6px;padding:5px 9px;cursor:pointer;font:inherit;}
.si-btn:hover{background:#334155;} .si-btn:disabled{opacity:.45;cursor:default;} .si-btn.on{background:#2563eb;border-color:#3b82f6;color:#fff;}
.si-list{display:flex;flex-direction:column;gap:4px;max-height:170px;overflow:auto;}
.si-item{text-align:left;background:#1e293b;color:#e5e7eb;border:1px solid #334155;border-radius:6px;padding:5px 8px;cursor:pointer;font:inherit;}
.si-item:hover{background:#334155;} .si-item.cur{border-color:#38bdf8;background:#0c4a6e;}
.si-sl{display:grid;grid-template-columns:78px 1fr 38px;gap:6px;align-items:center;margin:2px 0;} .si-sl input{width:100%;} .si-sl span:last-child{text-align:right;color:#94a3b8;}
.si-chk{display:block;margin:3px 0;cursor:pointer;}
@media (max-width:900px){.si-body{flex-direction:column;}.si-side{width:auto;max-height:42%;border-left:0;border-top:1px solid #334155;}}
`;

    function build() {
        const style = document.createElement('style');
        style.textContent = CSS;
        document.head.appendChild(style);

        root = document.createElement('div');
        root.className = 'si-root';
        root.innerHTML = `
<div class="si-box" role="dialog" aria-modal="true" aria-label="Soi ảnh">
  <div class="si-head">
    <strong id="siTitle">🔎 Soi ảnh</strong><span class="si-sp"></span>
    <button class="si-btn" id="siPrev" title="Biển trước (←)">◀ Biển trước</button>
    <span class="si-dim" id="siNav"></span>
    <button class="si-btn" id="siNext" title="Biển sau (→)">Biển sau ▶</button>
    <button class="si-btn" id="siClose" title="Đóng (Esc)">✕ Đóng</button>
  </div>
  <div class="si-body">
    <div class="si-stage" id="siStage"><canvas id="siCanvas"></canvas></div>
    <aside class="si-side">
      <div class="si-sec"><div class="si-sec-t">Biển hệ thống đọc được</div>
        <div id="siRead" class="si-read none">—</div><div id="siReadSub" class="si-dim"></div>
        <canvas id="siPlate" class="si-plate" style="display:none;"></canvas></div>
      <div class="si-sec"><div class="si-sec-t">Xe / biển trong ảnh</div><div id="siList" class="si-list"></div></div>
      <div class="si-sec"><div class="si-sec-t">Cắt &amp; zoom</div>
        <div class="si-row"><button class="si-btn" id="siSel" title="Bật rồi quét một khung trên ảnh để phóng đúng vùng đó (phím Z)">✂️ Chọn vùng để zoom</button><button class="si-btn" id="siFit" title="Vừa khung (phím 0)">⛶ Vừa khung</button></div>
        <div class="si-row"><button class="si-btn" id="siZin" title="Phóng to (+)">＋</button><button class="si-btn" id="siZout" title="Thu nhỏ (−)">−</button><button class="si-btn" id="siOne" title="Mỗi pixel ảnh = 1 pixel màn hình (phím 1)">1:1</button><button class="si-btn" id="siSave" title="Lưu đúng vùng đang nhìn thấy, ở pixel gốc (phím S)">💾 Lưu vùng đang xem</button></div>
        <label class="si-chk"><input type="checkbox" id="siBoxes" checked> Hiện khung &amp; nhãn (phím B)</label>
        <label class="si-chk"><input type="checkbox" id="siPix"> Thấy rõ từng điểm ảnh (không làm mịn)</label></div>
      <div class="si-sec"><div class="si-sec-t">Chỉnh ảnh <span class="si-dim">(chỉ để nhìn)</span></div>
        <div class="si-sl"><span>Sáng</span><input type="range" id="siBright" min="40" max="300" value="100"><span id="siBrightV">100</span></div>
        <div class="si-sl"><span>Tương phản</span><input type="range" id="siContrast" min="40" max="400" value="100"><span id="siContrastV">100</span></div>
        <div class="si-sl"><span>Gamma</span><input type="range" id="siGamma" min="30" max="300" value="100"><span id="siGammaV">100</span></div>
        <div class="si-sl"><span>Làm nét</span><input type="range" id="siSharp" min="0" max="300" value="0"><span id="siSharpV">0</span></div>
        <label class="si-chk"><input type="checkbox" id="siGray"> Ảnh xám</label>
        <label class="si-chk"><input type="checkbox" id="siInv"> Đảo màu (chữ sáng ↔ tối)</label>
        <button class="si-btn" id="siReset" title="Đặt lại các chỉnh sửa (phím R)">↺ Đặt lại</button></div>
    </aside>
  </div>
  <div class="si-foot" id="siFoot"></div>
</div>`;
        document.body.appendChild(root);
        root.querySelectorAll('[id^="si"]').forEach(e => { els[e.id] = e; });

        els.siClose.onclick = close;
        root.addEventListener('mousedown', e => { if (e.target === root) close(); });
        els.siPrev.onclick = () => step(-1);
        els.siNext.onclick = () => step(1);
        els.siFit.onclick = fit;
        els.siZin.onclick = () => zoomAt(st.cw / 2, st.ch / 2, 1.5);
        els.siZout.onclick = () => zoomAt(st.cw / 2, st.ch / 2, 1 / 1.5);
        els.siOne.onclick = () => setScaleCentered(1);
        els.siSel.onclick = () => setTool(st.tool === 'select' ? 'pan' : 'select');
        els.siSave.onclick = saveView;
        els.siBoxes.onchange = () => { st.showBoxes = els.siBoxes.checked; redraw(); };
        els.siPix.onchange = () => { st.smooth = !els.siPix.checked; redraw(); };
        els.siReset.onclick = resetAdj;
        [['siBright', 'bright'], ['siContrast', 'contrast'], ['siGamma', 'gamma'], ['siSharp', 'sharp']].forEach(([id, key]) => {
            els[id].oninput = () => { st.adj[key] = Number(els[id].value); els[id + 'V'].textContent = els[id].value; redraw(); renderPlate(); };
        });
        els.siGray.onchange = () => { st.adj.gray = els.siGray.checked; redraw(); renderPlate(); };
        els.siInv.onchange = () => { st.adj.inv = els.siInv.checked; redraw(); renderPlate(); };

        const c = els.siCanvas;
        c.addEventListener('wheel', e => {
            e.preventDefault();
            const r = c.getBoundingClientRect();
            zoomAt(e.clientX - r.left, e.clientY - r.top, e.deltaY < 0 ? 1.2 : 1 / 1.2);
        }, { passive: false });
        c.addEventListener('mousedown', onDown);
        c.addEventListener('mousemove', e => { if (!st.drag) { st.cursor = pos(e); updateFoot(); } });
        c.addEventListener('mouseleave', () => { if (!st.drag) { st.cursor = null; updateFoot(); } });
        c.addEventListener('dblclick', e => { const p = pos(e); zoomAt(p.x, p.y, e.shiftKey ? 1 / 2.5 : 2.5); });
        window.addEventListener('resize', () => { if (st) { resizeCanvas(); redraw(); } });
    }

    // ------------------------------------------------------------------------------------------------ mở / đóng
    function open(o) {
        if (!o || !o.source) return;
        if (!root) build();
        st = {
            src: o.source, iw: srcW(o.source), ih: srcH(o.source), title: o.title || '',
            boxes: o.boxes || [], targets: o.targets || [], cur: -1,
            scale: 1, tx: 0, ty: 0, cw: 0, ch: 0, dpr: 1, tool: 'pan', smooth: true, showBoxes: true,
            adj: defaults(), drag: null, sel: null, cursor: null, raf: 0,
        };
        root.style.display = 'flex';
        document.documentElement.style.overflow = 'hidden';
        els.siTitle.textContent = '🔎 Soi ảnh' + (st.title ? ' — ' + st.title : '');
        els.siBoxes.checked = true; els.siPix.checked = false;
        resetAdj(true);
        setTool('pan');
        renderList();
        window.addEventListener('keydown', onKey, true);
        resizeCanvas();
        fit();
        if (o.focus) {
            st.scale = Math.min(MAX_SCALE, o.focus.scale || 4);
            st.tx = st.cw / 2 - o.focus.x * st.scale;
            st.ty = st.ch / 2 - o.focus.y * st.scale;
            redraw();
        }
        if (typeof o.start === 'number' && o.start >= 0 && o.start < st.targets.length) goTarget(o.start);
        else { renderSide(); redraw(); }
    }

    function close() {
        if (!root || !st) return;
        window.removeEventListener('keydown', onKey, true);
        window.removeEventListener('mousemove', onMove);
        window.removeEventListener('mouseup', onUp);
        root.style.display = 'none';
        document.documentElement.style.overflow = '';
        if (st.raf) cancelAnimationFrame(st.raf);
        st = null;           // nhả tham chiếu tới ảnh
    }

    function resizeCanvas() {
        const stage = els.siStage;
        st.dpr = window.devicePixelRatio || 1;
        st.cw = Math.max(50, stage.clientWidth); st.ch = Math.max(50, stage.clientHeight);
        els.siCanvas.width = Math.round(st.cw * st.dpr); els.siCanvas.height = Math.round(st.ch * st.dpr);
    }

    // ------------------------------------------------------------------------------------------------ phép biến đổi
    function pos(e) { const r = els.siCanvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
    const toImg = (x, y) => ({ x: (x - st.tx) / st.scale, y: (y - st.ty) / st.scale });
    const fitScale = () => Math.min(st.cw / st.iw, st.ch / st.ih);

    function fit() {
        st.scale = fitScale();
        st.tx = (st.cw - st.iw * st.scale) / 2;
        st.ty = (st.ch - st.ih * st.scale) / 2;
        redraw();
    }
    function zoomAt(x, y, f) {
        const ns = Math.max(Math.min(fitScale() * 0.5, 0.05), Math.min(MAX_SCALE, st.scale * f));
        const p = toImg(x, y);
        st.scale = ns; st.tx = x - p.x * ns; st.ty = y - p.y * ns;
        redraw();
    }
    function setScaleCentered(s) {
        const c = toImg(st.cw / 2, st.ch / 2);
        st.scale = s; st.tx = st.cw / 2 - c.x * s; st.ty = st.ch / 2 - c.y * s;
        redraw();
    }
    function zoomToRect(r, margin) {
        const m = margin || 0;
        const w = Math.max(4, r.w * (1 + 2 * m)), h = Math.max(4, r.h * (1 + 2 * m));
        const s = Math.min(MAX_SCALE, st.cw / w, st.ch / h);
        const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
        st.scale = s; st.tx = st.cw / 2 - cx * s; st.ty = st.ch / 2 - cy * s;
        redraw();
    }

    // ------------------------------------------------------------------------------------------------ chuột
    function setTool(t) {
        st.tool = t;
        els.siSel.classList.toggle('on', t === 'select');
        els.siCanvas.style.cursor = t === 'select' ? 'crosshair' : 'grab';
    }
    function onDown(e) {
        if (e.button !== 0) return;
        e.preventDefault();
        const p = pos(e);
        st.drag = st.tool === 'select' ? { mode: 'sel', x0: p.x, y0: p.y } : { mode: 'pan', x0: p.x, y0: p.y, tx: st.tx, ty: st.ty };
        if (st.drag.mode === 'sel') st.sel = { x0: p.x, y0: p.y, x1: p.x, y1: p.y }; else els.siCanvas.style.cursor = 'grabbing';
        window.addEventListener('mousemove', onMove);       // chỉ gắn trong lúc kéo
        window.addEventListener('mouseup', onUp);
    }
    function onMove(e) {
        if (!st || !st.drag) return;
        const p = pos(e);
        if (st.drag.mode === 'pan') { st.tx = st.drag.tx + (p.x - st.drag.x0); st.ty = st.drag.ty + (p.y - st.drag.y0); }
        else { st.sel.x1 = Math.max(0, Math.min(st.cw, p.x)); st.sel.y1 = Math.max(0, Math.min(st.ch, p.y)); }
        st.cursor = p;
        redraw();
    }
    function onUp() {
        window.removeEventListener('mousemove', onMove);
        window.removeEventListener('mouseup', onUp);
        if (!st || !st.drag) return;
        const d = st.drag; st.drag = null;
        if (d.mode === 'sel') {
            const s = st.sel; st.sel = null;
            const x = Math.min(s.x0, s.x1), y = Math.min(s.y0, s.y1), w = Math.abs(s.x1 - s.x0), h = Math.abs(s.y1 - s.y0);
            if (w >= 6 && h >= 6) {
                const a = toImg(x, y), b = toImg(x + w, y + h);
                zoomToRect({ x: a.x, y: a.y, w: b.x - a.x, h: b.y - a.y }, 0.02);
                setTool('pan');
            } else redraw();
        } else els.siCanvas.style.cursor = 'grab';
    }

    function onKey(e) {
        if (!st) return;
        const inField = e.target && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox' && e.target.type !== 'range';
        if (inField) return;
        const k = e.key;
        let used = true;
        if (k === 'Escape') close();
        else if (k === 'ArrowLeft') step(-1);
        else if (k === 'ArrowRight') step(1);
        else if (k === '+' || k === '=') zoomAt(st.cw / 2, st.ch / 2, 1.5);
        else if (k === '-' || k === '_') zoomAt(st.cw / 2, st.ch / 2, 1 / 1.5);
        else if (k === '0') fit();
        else if (k === '1') setScaleCentered(1);
        else if (k === 'z' || k === 'Z') setTool(st.tool === 'select' ? 'pan' : 'select');
        else if (k === 'b' || k === 'B') { els.siBoxes.checked = !els.siBoxes.checked; st.showBoxes = els.siBoxes.checked; redraw(); }
        else if (k === 'r' || k === 'R') resetAdj();
        else if (k === 's' || k === 'S') saveView();
        else used = false;
        if (used) { e.preventDefault(); e.stopPropagation(); }
    }

    // ------------------------------------------------------------------------------------------------ biển / danh sách
    function renderList() {
        els.siList.innerHTML = st.targets.length
            ? st.targets.map((t, i) => `<button class="si-item" data-i="${i}">${esc(t.title)} · ${t.text ? `<b>${esc(t.text)}</b>${t.conf != null ? ' ' + Math.round(t.conf * 100) + '%' : ''}` : (t.stage === 'unread' ? '🔶 chưa đọc được' : '🔴 không dò được biển')}</button>`).join('')
            : '<span class="si-dim">Không có xe/biển nào được nhận diện — hãy tự zoom vào ảnh.</span>';
        els.siList.querySelectorAll('.si-item').forEach(b => { b.onclick = () => goTarget(Number(b.dataset.i)); });
    }
    function step(d) {
        if (!st.targets.length) return;
        goTarget(st.cur < 0 ? (d > 0 ? 0 : st.targets.length - 1) : (st.cur + d + st.targets.length) % st.targets.length);
    }
    function goTarget(i) {
        const t = st.targets[i];
        if (!t) return;
        st.cur = i;
        zoomToRect(t.rect, t.hasPlateBox ? 0.35 : 0.08);
        renderSide();
    }
    function renderSide() {
        const n = st.targets.length, t = st.cur >= 0 ? st.targets[st.cur] : null;
        els.siPrev.disabled = els.siNext.disabled = n === 0;
        els.siNav.textContent = n ? (t ? `${st.cur + 1}/${n}` : `${n} xe/biển`) : '';
        els.siList.querySelectorAll('.si-item').forEach((b, i) => b.classList.toggle('cur', i === st.cur));
        if (t && t.text) {
            els.siRead.className = 'si-read'; els.siRead.textContent = t.text;
            els.siReadSub.textContent = (t.conf != null ? `Độ tin cậy ${Math.round(t.conf * 100)}% · ` : '') + 'so từng ký tự với ảnh biển bên dưới';
        } else {
            els.siRead.className = 'si-read none';
            els.siRead.textContent = t ? (t.stage === 'unread' ? 'Có vùng biển, chưa đọc được' : 'Không dò được vùng biển') : '— (chọn một xe/biển) —';
            els.siReadSub.textContent = t && !t.text ? 'Hãy tự zoom vào ảnh để tìm và đọc biển bằng mắt.' : '';
        }
        renderPlate();
    }

    // ảnh biển cắt sẵn (đặt cạnh chuỗi đọc được); chịu cùng chỉnh sáng / tương phản
    function renderPlate() {
        const c = els.siPlate, t = st && st.cur >= 0 ? st.targets[st.cur] : null;
        if (!t || !t.hasPlateBox) { c.style.display = 'none'; return; }
        const m = 0.06, r = t.rect;
        const x0 = Math.max(0, r.x - r.w * m), y0 = Math.max(0, r.y - r.h * m);
        const x1 = Math.min(st.iw, r.x + r.w * (1 + m)), y1 = Math.min(st.ih, r.y + r.h * (1 + m));
        const w = Math.max(1, x1 - x0), h = Math.max(1, y1 - y0);
        const k = Math.min(10, 278 / w);                       // phóng cho vừa chiều ngang cột bên phải
        c.width = Math.max(1, Math.round(w * k)); c.height = Math.max(1, Math.round(h * k));
        const g = c.getContext('2d', { willReadFrequently: true });
        g.imageSmoothingEnabled = st.smooth; g.imageSmoothingQuality = 'high';
        g.drawImage(st.src, x0, y0, w, h, 0, 0, c.width, c.height);
        if (adjActive()) { const im = g.getImageData(0, 0, c.width, c.height); enhance(im, st.adj); g.putImageData(im, 0, 0); }
        c.style.display = '';
    }

    // ------------------------------------------------------------------------------------------------ chỉnh ảnh
    const adjActive = () => st && (st.adj.bright !== 100 || st.adj.contrast !== 100 || st.adj.gamma !== 100 || st.adj.sharp > 0 || st.adj.gray || st.adj.inv);
    function resetAdj(silent) {
        st.adj = defaults();
        els.siBright.value = 100; els.siContrast.value = 100; els.siGamma.value = 100; els.siSharp.value = 0;
        els.siBrightV.textContent = '100'; els.siContrastV.textContent = '100'; els.siGammaV.textContent = '100'; els.siSharpV.textContent = '0';
        els.siGray.checked = false; els.siInv.checked = false;
        if (silent !== true) { redraw(); renderPlate(); }
    }
    function enhance(im, a) {
        const d = im.data, lut = new Uint8ClampedArray(256), b = a.bright / 100, c = a.contrast / 100, gm = 1 / (a.gamma / 100);
        for (let v = 0; v < 256; v++) {
            let x = (v / 255) * b;
            x = (x - 0.5) * c + 0.5;
            x = Math.min(1, Math.max(0, x));
            lut[v] = Math.pow(x, gm) * 255;
        }
        for (let i = 0; i < d.length; i += 4) {
            let r = lut[d[i]], g = lut[d[i + 1]], bl = lut[d[i + 2]];
            if (a.gray) { r = g = bl = (0.299 * r + 0.587 * g + 0.114 * bl) | 0; }
            if (a.inv) { r = 255 - r; g = 255 - g; bl = 255 - bl; }
            d[i] = r; d[i + 1] = g; d[i + 2] = bl;
        }
        if (a.sharp > 0) sharpen(im, a.sharp / 100);
    }
    function sharpen(im, amt) {
        const w = im.width, h = im.height, d = im.data, s = new Uint8ClampedArray(d), row = w * 4;
        for (let y = 1; y < h - 1; y++) {
            for (let x = 1; x < w - 1; x++) {
                const p = (y * w + x) * 4;
                for (let ch = 0; ch < 3; ch++) {
                    const c = s[p + ch];
                    d[p + ch] = c + amt * (4 * c - s[p - 4 + ch] - s[p + 4 + ch] - s[p - row + ch] - s[p + row + ch]);
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ vẽ
    function redraw() { if (st && !st.raf) st.raf = requestAnimationFrame(draw); }

    function draw() {
        if (!st) return;
        st.raf = 0;
        const c = els.siCanvas, g = c.getContext('2d', { willReadFrequently: true }), dpr = st.dpr;
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.fillStyle = '#0b1220'; g.fillRect(0, 0, c.width, c.height);
        g.imageSmoothingEnabled = st.smooth; g.imageSmoothingQuality = 'high';
        g.setTransform(dpr * st.scale, 0, 0, dpr * st.scale, dpr * st.tx, dpr * st.ty);
        g.drawImage(st.src, 0, 0);
        g.setTransform(1, 0, 0, 1, 0, 0);
        if (adjActive()) { const im = g.getImageData(0, 0, c.width, c.height); enhance(im, st.adj); g.putImageData(im, 0, 0); }

        g.setTransform(dpr, 0, 0, dpr, 0, 0);
        if (st.showBoxes) drawBoxes(g);
        if (st.sel) {
            const s = st.sel, x = Math.min(s.x0, s.x1), y = Math.min(s.y0, s.y1), w = Math.abs(s.x1 - s.x0), h = Math.abs(s.y1 - s.y0);
            g.fillStyle = 'rgba(56,189,248,.18)'; g.fillRect(x, y, w, h);
            g.strokeStyle = '#38bdf8'; g.lineWidth = 1.5; g.setLineDash([5, 4]); g.strokeRect(x, y, w, h); g.setLineDash([]);
        }
        updateFoot();
    }

    function drawBoxes(g) {
        const cur = st.cur >= 0 ? st.targets[st.cur] : null;
        g.font = 'bold 13px sans-serif'; g.textBaseline = 'middle';
        st.boxes.forEach(b => {
            const x = st.tx + b.x * st.scale, y = st.ty + b.y * st.scale, w = b.w * st.scale, h = b.h * st.scale;
            if (x > st.cw || y > st.ch || x + w < 0 || y + h < 0) return;
            g.save();
            if (b.dashed) g.setLineDash([6, 4]);
            g.lineWidth = 2; g.strokeStyle = b.color || '#22c55e';
            g.strokeRect(x, y, w, h);
            g.restore();
            if (b.label) {
                const tw = g.measureText(b.label).width + 10, th = 20, ly = y - th >= 0 ? y - th : y;
                g.fillStyle = b.color || '#22c55e'; g.globalAlpha = 0.9; g.fillRect(x, ly, tw, th); g.globalAlpha = 1;
                g.fillStyle = '#04130a'; g.fillText(b.label, x + 5, ly + th / 2);
            }
        });
        if (cur) {                                    // khung đang chọn: viền vàng bao quanh
            const r = cur.rect;
            g.strokeStyle = '#facc15'; g.lineWidth = 3;
            g.strokeRect(st.tx + r.x * st.scale - 3, st.ty + r.y * st.scale - 3, r.w * st.scale + 6, r.h * st.scale + 6);
        }
        g.textBaseline = 'alphabetic';
    }

    function visibleRect() {
        const a = toImg(0, 0), b = toImg(st.cw, st.ch);
        const x0 = Math.max(0, a.x), y0 = Math.max(0, a.y), x1 = Math.min(st.iw, b.x), y1 = Math.min(st.ih, b.y);
        return { x: x0, y: y0, w: Math.max(0, x1 - x0), h: Math.max(0, y1 - y0) };
    }
    function updateFoot() {
        if (!st) return;
        const v = visibleRect();
        const cur = st.cursor ? toImg(st.cursor.x, st.cursor.y) : null;
        els.siFoot.innerHTML =
            `<span>Phóng <b>${Math.round(st.scale * 100)}%</b></span>` +
            `<span>Vùng đang xem: ${Math.round(v.w)}×${Math.round(v.h)} px gốc (x ${Math.round(v.x)}–${Math.round(v.x + v.w)}, y ${Math.round(v.y)}–${Math.round(v.y + v.h)})</span>` +
            `<span>Ảnh gốc ${st.iw}×${st.ih}</span>` +
            (cur && cur.x >= 0 && cur.y >= 0 && cur.x < st.iw && cur.y < st.ih ? `<span>Con trỏ: (${Math.round(cur.x)}, ${Math.round(cur.y)})</span>` : '') +
            `<span class="si-sp"></span><span>Lăn chuột = zoom · kéo = di chuyển · nhấp đúp = phóng · Z = chọn vùng · ←/→ = biển kế · Esc = đóng</span>`;
    }

    // ------------------------------------------------------------------------------------------------ lưu vùng đang xem (pixel gốc)
    function saveView() {
        const v = visibleRect();
        const w = Math.round(v.w), h = Math.round(v.h);
        if (w < 1 || h < 1) return;
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        const g = c.getContext('2d', { willReadFrequently: true });
        g.drawImage(st.src, Math.round(v.x), Math.round(v.y), w, h, 0, 0, w, h);
        if (adjActive()) { const im = g.getImageData(0, 0, w, h); enhance(im, st.adj); g.putImageData(im, 0, 0); }
        const name = `soi-${(st.title || 'anh').replace(/\.[a-z0-9]{2,4}$/i, '').replace(/[^\w.-]+/g, '_').slice(0, 40)}-x${Math.round(v.x)}y${Math.round(v.y)}-${w}x${h}.png`;
        c.toBlob(blob => {
            if (!blob) return;
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob); a.download = name;
            document.body.appendChild(a); a.click(); a.remove();
            setTimeout(() => URL.revokeObjectURL(a.href), 2000);
        }, 'image/png');
    }

    window.SimInspector = { open, close, isOpen: () => !!st };
})();
