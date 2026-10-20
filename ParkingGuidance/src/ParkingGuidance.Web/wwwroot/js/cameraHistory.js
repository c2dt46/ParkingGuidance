// ---------------------------------------------------------------------------
// Camera -> tab "Lich su": su kien nhan dien (xe vao / cap nhat / xe ra), loc theo camera, realtime.
// ---------------------------------------------------------------------------
(function () {
    const body = document.getElementById('histBody');
    const camSel = document.getElementById('histCamera');
    if (!body || !camSel) return;

    let camNames = new Map();   // id camera -> dia chi IP
    let events = [];

    const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    // "B1-1" -> "Vị trí 1 · tầng B1"
    function formatSlot(slotId) {
        const m = /^(.*)-(\d+)$/.exec(slotId || '');
        return m ? `Vị trí ${m[2]} · tầng ${m[1]}` : (slotId || '');
    }
    const camLabel = id => camNames.get(id) || (id || '').slice(0, 8) || '—';

    async function loadCameras() {
        try {
            const list = await (await fetch('/api/system/cameras')).json();
            camNames = new Map(list.map(c => [c.id, c.ipAddress]));
            const keep = camSel.value;
            camSel.innerHTML = '<option value="">Tất cả camera</option>' +
                list.map(c => `<option value="${esc(c.id)}">${esc(c.ipAddress)}</option>`).join('');
            camSel.value = list.some(c => c.id === keep) ? keep : '';
        } catch { /* khong tai duoc: hien ma camera rut gon */ }
    }

    async function loadEvents() {
        try { events = await (await fetch('/api/slots/events?take=300')).json(); render(); }
        catch { /* mat ket noi tam thoi */ }
    }

    function render() {
        const id = camSel.value;
        const rows = id ? events.filter(e => e.cameraId === id) : events;
        document.getElementById('histCount').textContent = rows.length ? `${rows.length} sự kiện gần nhất` : '';
        if (!rows.length) { body.innerHTML = '<tr><td colspan="5" class="no-result">Chưa có sự kiện nào.</td></tr>'; return; }
        body.innerHTML = rows.map(e => {
            const t = new Date(e.timestamp);
            const isExit = e.eventType === 'VEHICLE_EXIT';
            const plate = !e.plateNumber ? '—' : (e.plateNumber === 'UNKNOWN' ? 'Không rõ biển' : e.plateNumber);
            return `<tr>
                <td>${esc(t.toLocaleDateString('vi-VN'))} ${esc(t.toLocaleTimeString('vi-VN'))}</td>
                <td>${esc(camLabel(e.cameraId))}</td>
                <td>${esc(formatSlot(e.slotId))}</td>
                <td>${esc(plate)}</td>
                <td class="${isExit ? 'hist-out' : 'hist-in'}">${isExit ? 'Xe ra' : 'Xe vào / cập nhật'}</td>
            </tr>`;
        }).join('');
    }

    camSel.addEventListener('change', render);
    document.getElementById('histRefreshBtn').addEventListener('click', async () => { await loadCameras(); loadEvents(); });
    document.querySelector('.tab-btn[data-tab="log"]')?.addEventListener('click', async () => { await loadCameras(); loadEvents(); });

    // Realtime: co su kien moi thi tai lai (SignalR duoc nap san trong _Layout)
    if (window.signalR) {
        const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/monitoring').withAutomaticReconnect().build();
        conn.on('ParkingEvent', loadEvents);
        conn.start().catch(() => { /* van dung duoc nut Tai lai */ });
    }

    loadCameras().then(loadEvents);
})();
