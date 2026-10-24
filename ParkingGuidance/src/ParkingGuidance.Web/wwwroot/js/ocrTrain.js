/* Tab Train → "Train đọc cả chuỗi biển (OCR)": bấm train, xem tiến trình, cài model. Gọi /api/system/ocr-train/*.
   Model đọc cả dòng chữ (CRNN + CTC) nên không cần tách ký tự. Mọi việc nặng chạy ở tiến trình Python riêng (tools/train_plate_ocr.py). */
(function () {
    'use strict';
    const API = '/api/system/ocr-train';
    const $ = id => document.getElementById(id);
    if (!$('otPanel')) return;
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const pct = v => (v == null ? '—' : Math.round(v * 100) + '%');

    async function jfetch(url, opts) {
        const res = await fetch(url, opts);
        let body = null;
        try { body = await res.json(); } catch { /* khong co noi dung */ }
        return { ok: res.ok, body };
    }

    function render(s) {
        const st = s.status;
        const busy = s.running || s.starting;
        const bar = $('otBar'), wrap = $('otBarWrap'), msg = $('otMsg'), res = $('otResult'), warn = $('otWarn');

        $('otCancelBtn').style.display = busy ? '' : 'none';
        $('otInstallBtn').style.display = s.canInstall ? '' : 'none';
        $('otClearBtn').style.display = (!busy && (st || s.cancelled || s.installed || s.startError)) ? '' : 'none';
        $('otTrainBtn').disabled = busy || !s.scriptFound || (s.corrections || 0) === 0;
        $('otTrainBtn').title = (s.corrections || 0) === 0 ? 'Chưa có mẫu nào — hãy lưu biển đúng cho ít nhất 1 xe ở phần bên dưới.' : '';
        $('otModel').innerHTML = `Mẫu đã lưu: <strong>${s.corrections || 0}</strong> · ` + (s.currentModel
            ? `Model OCR đang dùng: <code>${esc(s.currentModel)}</code>`
            : 'Chưa có model OCR (<code>plate_ocr.onnx</code>) — ứng dụng đang đọc bằng bộ phân loại ký tự / Tesseract. Train xong và cài model này thì sẽ đọc cả chuỗi.');

        warn.style.display = 'none';
        if (!s.scriptFound) {
            warn.style.display = '';
            warn.innerHTML = '⚠️ Không tìm thấy <code>tools\\train_plate_ocr.py</code> cạnh dự án — chép file vào thư mục <code>tools</code> hoặc đặt đường dẫn ở <code>Trainer:OcrScript</code> trong appsettings.json.';
        } else if (s.startError) {
            warn.style.display = '';
            warn.innerHTML = '⚠️ ' + esc(s.startError);
        }

        let html = '';
        if (busy) {
            wrap.style.display = ''; bar.style.width = ((st && st.progress) || 2) + '%';
            msg.textContent = st ? `⏳ ${st.stageLabel || 'Đang chạy'} — ${st.message || ''}`
                                 : '⏳ Đang kiểm tra Python và khởi động… (lần đầu có thể mất vài chục giây)';
        } else if (s.cancelled) {
            wrap.style.display = 'none'; msg.textContent = '⏹ Đã huỷ train. Model hiện tại không bị thay đổi.';
        } else if (s.interrupted) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#f59e0b;">⚠️ Lần train trước bị dừng giữa chừng. Model hiện tại không bị thay đổi.</span>${s.stderr ? `<pre class="hint small">${esc(s.stderr)}</pre>` : ''}`;
        } else if (st && st.error) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#ef4444;">⚠️ Train thất bại: ${esc(st.error)}</span>${s.stderr ? `<pre class="hint small">${esc(s.stderr)}</pre>` : ''}`;
        } else if (st && st.finished) {
            wrap.style.display = ''; bar.style.width = '100%';
            msg.textContent = s.installed?.ok ? '✔ Train xong — model OCR mới đã được cài và đang dùng.'
                : (st.result && !st.result.recommendInstall ? '✔ Train xong — chưa tự cài model mới (xem lý do bên dưới).' : '✔ ' + (st.message || 'Hoàn tất'));
            const r = st.result;
            if (r) {
                html += `<div class="hint small">Đọc đúng <strong>trọn biển</strong> (chưa từng học): giả lập <strong>${pct(r.synthAcc)}</strong>` +
                    (r.realPlates ? `, thật <strong>${pct(r.realAcc)}</strong> (${r.realPlates} biển)` : ', thật: chưa có tập kiểm tra') +
                    (r.uniquePlates != null ? ` · ${r.uniquePlates} biển thật khác nhau trong ${r.samples} mẫu` : '') + '.</div>';
                if (r.reason) html += `<div class="hint small">${r.recommendInstall ? '✅' : '💡'} ${esc(r.reason)}</div>`;
            }
        } else if (!st && s.stderr) {
            wrap.style.display = 'none';
            msg.innerHTML = `<span style="color:#ef4444;">⚠️ Không chạy được trình train:</span><pre class="hint small">${esc(s.stderr)}</pre>`;
        } else {
            wrap.style.display = 'none'; msg.textContent = '';
        }
        if (s.installed && !s.installed.ok) html += `<div class="hint small" style="color:#ef4444;">⚠️ ${esc(s.installed.message)}</div>`;
        else if (s.installed?.ok) html += `<div class="hint small">${esc(s.installed.message)}</div>`;
        res.innerHTML = html;
    }

    // ---- An / hien phan train ky tu (cu): mac dinh AN, nho lua chon cua nguoi dung ----
    const OLD_KEY = 'ocrTrain.showOldTrain';
    const style = document.createElement('style');
    style.textContent = '#trainTab.old-train-off .tr-chars-btn, #trainTab.old-train-off #trNeedFixLbl { display: none !important; }';
    document.head.appendChild(style);
    function applyOld(show) {
        $('oldTrainWrap').style.display = show ? '' : 'none';
        $('trainTab').classList.toggle('old-train-off', !show);
        $('otOldToggle').textContent = show ? '🔧 Ẩn train ký tự (cũ)' : '🔧 Hiện train ký tự (cũ)';
    }
    let showOld = false;
    try { showOld = localStorage.getItem(OLD_KEY) === '1'; } catch { /* che do rieng tu: khong nho */ }
    applyOld(showOld);
    $('otOldToggle').addEventListener('click', () => {
        showOld = !showOld;
        applyOld(showOld);
        try { localStorage.setItem(OLD_KEY, showOld ? '1' : '0'); } catch { /* bo qua */ }
    });

    let inFlight = false;
    async function refresh() {
        if (inFlight || document.visibilityState !== 'visible' || !$('otPanel').offsetParent) return;   // chi hoi khi dang nhin thay
        inFlight = true;
        try {
            const r = await jfetch(`${API}/status`);
            if (r.ok && r.body) render(r.body);
        } catch { /* mat ket noi tam thoi */ }
        finally { inFlight = false; }
    }

    $('otTrainBtn').addEventListener('click', async () => {
        const epochs = Math.max(3, Math.min(200, parseInt($('otEpochs').value, 10) || 20));
        const synth = Math.max(0, Math.min(30000, parseInt($('otSynth').value, 10) || 0));
        $('otTrainBtn').disabled = true;
        const r = await jfetch(`${API}/start`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ epochs, synth }) });
        if (!r.ok) $('otMsg').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.body?.message || 'Không bắt đầu được.')}</span>`;
        refresh();
    });
    $('otCancelBtn').addEventListener('click', async () => { await fetch(`${API}/cancel`, { method: 'POST' }); refresh(); });
    $('otInstallBtn').addEventListener('click', async () => {
        $('otInstallBtn').disabled = true;
        const r = await jfetch(`${API}/install`, { method: 'POST' });
        $('otInstallBtn').disabled = false;
        if (!r.ok) $('otMsg').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.body?.message || 'Không cài được.')}</span>`;
        refresh();
    });
    $('otClearBtn').addEventListener('click', async () => {
        const r = await jfetch(`${API}/clear`, { method: 'POST' });
        if (!r.ok) $('otMsg').innerHTML = `<span style="color:#ef4444;">⚠️ ${esc(r.body?.message || 'Không xoá được.')}</span>`;
        refresh();
    });

    refresh();
    setInterval(refresh, 2500);
})();
