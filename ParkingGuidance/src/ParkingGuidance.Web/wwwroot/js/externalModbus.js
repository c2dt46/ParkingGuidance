// ---------------------------------------------------------------------------
// Thiet bi ngoai > Modbus TCP/IP. Hai tab:
//   Server: khai bao cong chuong trinh se mo.
//   Client: khai bao IP + cong thiet bi, chu ky gui (ms) va ban do bien MS -> dia chi thanh ghi cho tung thiet bi.
// Bam "Them" la them ngay 1 dong (so thu tu tu 0, lap cho trong nho nhat); sua truc tiep trong bang roi bam "Luu".
// Tiet kiem tai nguyen: trang chi doc khi mo, ghi khi bam Luu; trang thai chi hoi moi 2 giay khi trang dang hien (an trinh duyet thi dung).
// ---------------------------------------------------------------------------
(function () {
    const $ = id => document.getElementById(id);
    const API = '/api/system/modbus';
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    let max = 32;
    let varNames = [];                       // ten cac bien MS hien co (MS0, MS1...)
    let mapId = null;                        // Client dang mo bang map
    let tab = 'server';
    let statusTimer = null;
    const data = { server: [], client: [] };
    const dirty = { server: false, client: false };

    const KIND = {
        server: { api: 'servers', body: 'mbSvBody', state: 'mbSvState', label: 'Server' },
        client: { api: 'clients', body: 'mbClBody', state: 'mbClState', label: 'Client' }
    };

    // ----------------------------------------------------------------- tab
    function setTab(name) {
        tab = name;
        document.querySelectorAll('[data-mbtab]').forEach(b => b.classList.toggle('active', b.dataset.mbtab === name));
        $('mbServerTab').style.display = name === 'server' ? '' : 'none';
        $('mbClientTab').style.display = name === 'client' ? '' : 'none';
        updateStatusPolling();
    }
    document.querySelectorAll('[data-mbtab]').forEach(b => b.addEventListener('click', () => setTab(b.dataset.mbtab)));

    // ----------------------------------------------------------------- hien thi
    function say(kind, text, isError) {
        const el = $(KIND[kind].state);
        el.textContent = text || '';
        el.style.color = isError ? '#ef4444' : '';
    }
    function markDirty(kind) {
        dirty[kind] = true;
        say(kind, 'Có thay đổi chưa lưu — bấm "Lưu".');
    }

    function render(kind) {
        const rows = data[kind].slice().sort((a, b) => a.id - b.id);
        const body = $(KIND[kind].body);
        if (!rows.length) {
            body.innerHTML = `<tr><td colspan="9" class="hint small">Chưa có khai báo nào — bấm "Thêm ${KIND[kind].label}".</td></tr>`;
            return;
        }
        body.innerHTML = rows.map(r => {
            const common =
                `<td>${r.id}</td>` +
                `<td><input type="checkbox" data-f="enabled" ${r.enabled ? 'checked' : ''} /></td>`;
            const del = `<button type="button" class="btn secondary btn-sm" data-del title="Xoá khai báo này">🗑️</button>`;
            if (kind === 'server') {
                return `<tr data-id="${r.id}">${common}` +
                    `<td><input type="number" data-f="port" min="1" max="65535" step="1" value="${r.port}" /></td>` +
                    `<td><input type="text" data-f="description" maxlength="200" value="${esc(r.description)}" /></td>` +
                    `<td class="mb-status" data-sst="${r.id}">—</td><td>${del}</td></tr>`;
            }
            return `<tr data-id="${r.id}" class="${r.id === mapId ? 'sel' : ''}">${common}` +
                `<td><input type="text" data-f="ip" maxlength="15" placeholder="192.168.1.10" spellcheck="false" autocomplete="off" value="${esc(r.ip)}" /></td>` +
                `<td><input type="number" data-f="port" min="1" max="65535" step="1" value="${r.port}" /></td>` +
                `<td><input type="number" data-f="unitId" min="0" max="255" step="1" value="${r.unitId}" /></td>` +
                `<td><input type="number" data-f="intervalMs" min="100" max="3600000" step="100" value="${r.intervalMs}" /></td>` +
                `<td><input type="text" data-f="description" maxlength="200" value="${esc(r.description)}" /></td>` +
                `<td class="mb-status" data-st="${r.id}">—</td>` +
                `<td class="mb-actions"><button type="button" class="btn secondary btn-sm" data-map title="Gán biến (MS / MC) cho thanh ghi của thiết bị này">🔗 Biến (${(r.mappings || []).length})</button> ${del}</td></tr>`;
        }).join('');
        updateStatus();
    }

    // ----------------------------------------------------------------- thao tac chung (Server + Client)
    function nextId(kind) {
        const used = new Set(data[kind].map(r => r.id));
        let n = 0; while (used.has(n)) n++;       // lap cho trong nho nhat, bat dau tu 0
        return n;
    }

    function add(kind) {
        if (data[kind].length >= max) { say(kind, `Tối đa ${max} khai báo.`, true); return; }
        const id = nextId(kind);
        if (kind === 'server') {
            const ports = new Set(data.server.map(r => r.port));
            let port = 502; while (ports.has(port)) port++;
            data.server.push({ id, enabled: true, port, description: '' });
        } else {
            data.client.push({ id, enabled: true, ip: '', port: 502, unitId: 1, intervalMs: 1000, description: '', mappings: [] });
        }
        render(kind);
        markDirty(kind);
        const first = $(KIND[kind].body).querySelector(`tr[data-id="${id}"] input[type=text], tr[data-id="${id}"] input[type=number]`);
        if (first) first.focus();
    }

    function bind(kind) {
        const body = $(KIND[kind].body);
        const rowOf = el => {
            const tr = el.closest('tr[data-id]');
            return tr ? data[kind].find(r => r.id === +tr.dataset.id) : null;
        };
        const onEdit = e => {
            const f = e.target.dataset.f; if (!f) return;
            const row = rowOf(e.target); if (!row) return;
            if (f === 'enabled') row.enabled = e.target.checked;
            else if (f === 'description' || f === 'ip') row[f] = e.target.value;
            else row[f] = e.target.value === '' ? 0 : +e.target.value;        // port / unitId / intervalMs
            if (kind === 'client' && row.id === mapId) renderMapTitle();
            markDirty(kind);
        };
        body.addEventListener('input', onEdit);
        body.addEventListener('change', onEdit);
        body.addEventListener('click', e => {
            const row = rowOf(e.target); if (!row) return;
            if (e.target.closest('[data-del]')) {
                data[kind] = data[kind].filter(r => r !== row);
                if (kind === 'client' && row.id === mapId) closeMap();
                render(kind);
                markDirty(kind);
            } else if (kind === 'client' && e.target.closest('[data-map]')) {
                openMap(row.id);
            }
        });
    }

    async function save(kind) {
        say(kind, '⏳ Đang lưu…');
        try {
            const res = await fetch(`${API}/${KIND[kind].api}`, {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(data[kind])
            });
            const d = await res.json().catch(() => ({}));
            if (!res.ok) { say(kind, d.message || `Lỗi ${res.status}`, true); return; }
            data[kind] = d[KIND[kind].api] || [];
            dirty[kind] = false;
            if (kind === 'client' && mapId !== null && !data.client.some(r => r.id === mapId)) closeMap();
            render(kind);
            if (kind === 'client' && mapId !== null) renderMap();
            say(kind, '✔ Đã lưu.');
        } catch (err) {
            say(kind, `Không lưu được: ${err.message}`, true);
        }
    }

    // ----------------------------------------------------------------- bang map bien MS -> thanh ghi (Client)
    const clientById = id => data.client.find(r => r.id === id);

    async function loadVarNames() {
        try {
            // bien MS (cam bien) + bien MC (camera)
            const [a, b] = await Promise.all([
                fetch('/api/system/sensors/variables').then(r => (r.ok ? r.json() : { variables: [] })),
                fetch('/api/system/camera-variables').then(r => (r.ok ? r.json() : { variables: [] }))
            ]);
            varNames = [...(a.variables || []), ...(b.variables || [])].map(v => v.name);
        } catch { /* giu danh sach cu */ }
    }

    async function openMap(id) {
        mapId = id;
        $('mbMapState').textContent = '';
        await loadVarNames();
        render('client');
        renderMap();
        $('mbMapPanel').style.display = '';
        $('mbMapPanel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
    function closeMap() {
        mapId = null;
        $('mbMapPanel').style.display = 'none';
        $('mbMapBody').innerHTML = '';
        render('client');
    }

    function renderMapTitle() {
        const c = clientById(mapId); if (!c) return;
        $('mbMapTitle').textContent = `Biến MS / MC → thanh ghi — Client ${c.id} (${c.ip || 'chưa có IP'}:${c.port}, Unit ${c.unitId})`;
    }

    function renderMap() {
        const c = clientById(mapId); if (!c) { closeMap(); return; }
        renderMapTitle();
        c.mappings = c.mappings || [];
        const body = $('mbMapBody');
        if (!c.mappings.length) {
            body.innerHTML = '<tr><td colspan="3" class="hint small">Chưa gán biến nào — bấm "Thêm dòng" hoặc "Thêm tất cả biến".</td></tr>';
            return;
        }
        body.innerHTML = c.mappings.map((m, i) => {
            const names = varNames.includes(m.variable) || !m.variable ? varNames : [m.variable, ...varNames];
            const opts = ['<option value="">— chọn biến —</option>'].concat(names.map(n =>
                `<option value="${esc(n)}" ${n === m.variable ? 'selected' : ''}>${esc(n)}${varNames.includes(n) ? '' : ' (không còn)'}</option>`)).join('');
            return `<tr data-i="${i}"><td><select data-f="variable" style="min-width:200px">${opts}</select></td>` +
                `<td><input type="number" data-f="address" min="0" max="65535" step="1" value="${m.address}" /></td>` +
                `<td><button type="button" class="btn secondary btn-sm" data-mdel title="Bỏ dòng này">🗑️</button></td></tr>`;
        }).join('');
    }

    const nextAddress = c => (c.mappings.length ? Math.max(...c.mappings.map(m => +m.address || 0)) + 1 : 0);

    $('mbMapAdd').addEventListener('click', () => {
        const c = clientById(mapId); if (!c) return;
        if (c.mappings.length >= 500) { $('mbMapState').textContent = 'Tối đa 500 dòng.'; return; }
        c.mappings.push({ variable: '', address: Math.min(65535, nextAddress(c)) });
        renderMap(); render('client'); markDirty('client');
        const sel = $('mbMapBody').querySelector('tr:last-child select'); if (sel) sel.focus();
    });
    $('mbMapAll').addEventListener('click', async () => {
        const c = clientById(mapId); if (!c) return;
        await loadVarNames();
        const have = new Set(c.mappings.map(m => m.variable));
        const todo = varNames.filter(n => !have.has(n));
        if (!todo.length) { $('mbMapState').textContent = varNames.length ? 'Tất cả biến đã được gán.' : 'Chưa có biến nào (tạo ở Cảm biến › Dữ liệu hoặc Camera › Dữ liệu).'; return; }
        let addr = nextAddress(c);
        for (const n of todo) { if (c.mappings.length >= 500 || addr > 65535) break; c.mappings.push({ variable: n, address: addr++ }); }
        $('mbMapState').textContent = `Đã thêm ${todo.length} biến.`;
        renderMap(); render('client'); markDirty('client');
    });
    $('mbMapClose').addEventListener('click', closeMap);

    const mapBody = $('mbMapBody');
    const onMapEdit = e => {
        const f = e.target.dataset.f; if (!f) return;
        const tr = e.target.closest('tr[data-i]'); const c = clientById(mapId); if (!tr || !c) return;
        const m = c.mappings[+tr.dataset.i]; if (!m) return;
        if (f === 'variable') m.variable = e.target.value;
        else m.address = e.target.value === '' ? 0 : +e.target.value;
        markDirty('client');
    };
    mapBody.addEventListener('input', onMapEdit);
    mapBody.addEventListener('change', onMapEdit);
    mapBody.addEventListener('click', e => {
        if (!e.target.closest('[data-mdel]')) return;
        const tr = e.target.closest('tr[data-i]'); const c = clientById(mapId); if (!tr || !c) return;
        c.mappings.splice(+tr.dataset.i, 1);
        renderMap(); render('client'); markDirty('client');
    });

    // ----------------------------------------------------------------- trang thai Client (chi hoi khi dang xem tab Client)
    const SERVER_TEXT = { Disabled: '⚪ Tắt', Starting: '🟡 Đang mở cổng…', Listening: '🟢 Đang mở cổng', Error: '🔴 Lỗi' };
    const STATE_TEXT = {
        Disabled: '⚪ Tắt', Idle: '⚪ Chưa gán biến', Connecting: '🟡 Đang kết nối…', Connected: '🟢 Đã kết nối', Error: '🔴 Lỗi'
    };
    async function updateStatus() {
        try {
            const res = await fetch(`${API}/status`);
            if (!res.ok) return;
            const d = await res.json();
            (d.servers || []).forEach(s => {
                const td = document.querySelector(`td[data-sst="${s.id}"]`); if (!td) return;
                let t = SERVER_TEXT[s.state] || '—';
                if (s.state === 'Listening') t += ` · ${s.connections} kết nối` + (s.requests ? ` · ${s.requests} lệnh` : '');
                td.textContent = t;
                td.title = s.error || '';
                td.style.color = s.state === 'Error' ? '#ef4444' : '';
            });
            (d.clients || []).forEach(s => {
                const td = document.querySelector(`td[data-st="${s.id}"]`); if (!td) return;
                let t = STATE_TEXT[s.State] || STATE_TEXT[s.state] || '—';
                const state = s.state || s.State;
                if (state === 'Connected' && s.lastWriteUtc)
                    t += ` · ${s.registers} thanh ghi · ${new Date(s.lastWriteUtc).toLocaleTimeString()}` + (s.skipped ? ` · bỏ ${s.skipped} biến` : '');
                td.textContent = t;
                td.title = s.error || '';
                td.style.color = state === 'Error' ? '#ef4444' : '';
            });
        } catch { /* mat ket noi tam thoi */ }
    }
    function updateStatusPolling() {
        const need = !document.hidden;
        if (need && !statusTimer) { updateStatus(); statusTimer = setInterval(updateStatus, 2000); }
        else if (!need && statusTimer) { clearInterval(statusTimer); statusTimer = null; }
    }
    document.addEventListener('visibilitychange', updateStatusPolling);

    // ----------------------------------------------------------------- khoi tao
    async function load() {
        try {
            const res = await fetch(API);
            if (!res.ok) { say('server', 'Không đọc được khai báo.', true); return; }
            const d = await res.json();
            max = d.maxEntries || 32;
            data.server = d.servers || [];
            data.client = (d.clients || []).map(c => ({ ...c, mappings: c.mappings || [] }));
        } catch { say('server', 'Không đọc được khai báo.', true); }
        render('server'); render('client');
        updateStatusPolling();
    }

    $('mbSvAdd').addEventListener('click', () => add('server'));
    $('mbClAdd').addEventListener('click', () => add('client'));
    $('mbSvSave').addEventListener('click', () => save('server'));
    $('mbClSave').addEventListener('click', () => save('client'));
    bind('server'); bind('client');

    window.addEventListener('beforeunload', e => {
        if (dirty.server || dirty.client) { e.preventDefault(); e.returnValue = ''; }
    });

    load();
})();
