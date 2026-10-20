// ---------------------------------------------------------------------------
// TASKS > Tags
//   System tags : bang chi doc, gia tri cap nhat truc tiep (poll 500 ms khi tab dang mo va trang dang hien).
//   Project tags: nguoi dung khai bao theo bang (Name, Value, Type, Description, Scope) - them / sua / xoa.
// Trang nay chi goi /api/tasks/tags/*; khong cham toi camera/cam bien/LED/bai xe.
// ---------------------------------------------------------------------------
const $ = id => document.getElementById(id);
const API = '/api/tasks/tags';
const POLL_MS = 500;
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const tr = (k, vi) => (window.PGPrefs ? window.PGPrefs.t(k, vi) : vi);

async function api(url, options) {
    const res = await fetch(url, options);
    let data = null;
    try { data = await res.json(); } catch { /* khong co noi dung */ }
    if (!res.ok) throw new Error(data?.message || ('HTTP ' + res.status));
    return data;
}
const send = (url, method, body) => api(url, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

// ----- icon theo kieu: Boolean = bac thang (step), so = doc doc (ramp), String = Aa
const ICON_RAMP = '<svg class="tg-svg" viewBox="0 0 16 12" aria-hidden="true"><path d="M1 11 L5 11 L5 7 L9 7 L9 3 L15 3" fill="none" stroke="currentColor" stroke-width="1.6"/></svg>';
const ICON_STEP = '<svg class="tg-svg" viewBox="0 0 16 12" aria-hidden="true"><path d="M1 10 L6 10 L6 2 L15 2" fill="none" stroke="currentColor" stroke-width="1.6"/></svg>';
const iconFor = type => type === 'Boolean' ? ICON_STEP : type === 'String' ? '<span class="tg-aa">Aa</span>' : ICON_RAMP;

function valueHtml(type, value) {
    if (type === 'Boolean') return `<span class="tg-dot ${value === '1' ? 'on' : 'off'}"></span>${esc(value)}`;
    return esc(value);
}

let tab = 'system';
let sysTags = [];
let projTags = [];
let selName = null;
let editingName = null;      // null = dang them moi
let polling = false;

// ----------------------------------------------------------------- tab
function showTab(name) {
    tab = name;
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
    $('tab-system').style.display = name === 'system' ? '' : 'none';
    $('tab-project').style.display = name === 'project' ? '' : 'none';
    if (name === 'system') loadSystem(); else loadProject();
}

// ----------------------------------------------------------------- System tags
function renderSystem() {
    $('tgSysBody').innerHTML = sysTags.map(g => `
        <tr>
            <td>${g.no}</td>
            <td><span class="tg-ico">${iconFor(g.type)}</span>${esc(g.name)}</td>
            <td class="tg-val" data-sys="${esc(g.name)}" data-type="${esc(g.type)}">${valueHtml(g.type, g.value)}</td>
            <td>${esc(g.type)}</td>
            <td>${esc(g.description)}</td>
            <td>${esc(g.scope)}</td>
        </tr>`).join('');
}

function renderSysStatus(s) {
    const box = $('tgSysStatus');
    if (!s) { box.textContent = ''; return; }
    if (!s.enabled) {
        box.className = 'tg-status warn';
        box.textContent = tr('tag.stOff', 'System tags đang tắt theo cấu hình (Tags:SystemEnabled = false).');
        return;
    }
    const age = s.lastTickUtc ? (Date.now() - new Date(s.lastTickUtc).getTime()) / 1000 : Infinity;
    const ok = s.running && age < 5;
    box.className = 'tg-status ' + (ok ? 'ok' : 'warn');
    box.textContent = (ok ? tr('tag.stRun', '● Luồng System tags đang chạy') : tr('tag.stStop', '● Luồng System tags không chạy'))
        + ` · ${tr('tag.stTick', 'quét')} ${s.tickMs} ms`
        + ` · ${tr('tag.stSlow', 'nháy chậm')} ${s.blinkSlowMs} ms`
        + ` · ${tr('tag.stFast', 'nháy nhanh')} ${s.blinkFastMs} ms`
        + ` · ${tr('tag.stErr', 'lỗi')}: ${s.errorCount}`
        + (s.lastError ? ` (${s.lastError})` : '');
}

async function loadSystem() {
    try {
        const d = await api(API + '/system');
        sysTags = d.tags || [];
        renderSystem();
        renderSysStatus(d.status);
    } catch (e) {
        $('tgSysStatus').className = 'tg-status warn';
        $('tgSysStatus').textContent = '⚠️ ' + e.message;
    }
}

// ----------------------------------------------------------------- Project tags
function renderProject() {
    const body = $('tgProjBody');
    if (!projTags.length) {
        body.innerHTML = `<tr><td colspan="6" class="no-result">${esc(tr('tag.empty', 'Chưa có Project tag nào. Bấm “Thêm tag” để khai báo.'))}</td></tr>`;
    } else {
        body.innerHTML = projTags.map(g => `
            <tr data-name="${esc(g.name)}" class="${g.name === selName ? 'sel' : ''}">
                <td>${g.no}</td>
                <td><span class="tg-ico">${iconFor(g.type)}</span>${esc(g.name)}</td>
                <td class="tg-val" data-proj="${esc(g.name)}" data-type="${esc(g.type)}">${valueHtml(g.type, g.value)}</td>
                <td>${esc(g.type)}</td>
                <td>${esc(g.description)}</td>
                <td>${esc(g.scope)}</td>
            </tr>`).join('');
        body.querySelectorAll('tr[data-name]').forEach(row => {
            row.addEventListener('click', () => select(row.dataset.name));
            row.addEventListener('dblclick', () => { select(row.dataset.name); openForm(row.dataset.name); });
        });
    }
    $('tgEdit').disabled = $('tgDel').disabled = !projTags.some(g => g.name === selName);
    $('tgProjState').textContent = projTags.length ? `${projTags.length} tags` : '';
}

function select(name) {
    selName = name;
    renderProject();
}

async function loadProject() {
    try {
        projTags = await api(API + '/project');
        if (!projTags.some(g => g.name === selName)) selName = null;
        renderProject();
    } catch (e) {
        $('tgProjState').textContent = '⚠️ ' + e.message;
    }
}

// ----- form
function syncValueControl(keepValue) {
    const isBool = $('TgType').value === 'Boolean';
    $('TgValue').style.display = isBool ? 'none' : '';
    $('TgValueBool').style.display = isBool ? '' : 'none';
    if (isBool) {
        const v = $('TgValue').value.trim().toLowerCase();
        $('TgValueBool').value = (keepValue && (v === '1' || v === 'true')) ? '1' : '0';
    }
}
const formValue = () => $('TgType').value === 'Boolean' ? $('TgValueBool').value : $('TgValue').value;

function openForm(name) {
    editingName = name || null;
    const g = name ? projTags.find(x => x.name === name) : null;
    $('tgFormTitle').textContent = g ? tr('tag.formEdit', 'Sửa tag') + ': ' + g.name : tr('tag.formNew', 'Tag mới');
    $('TgName').value = g ? g.name : '';
    $('TgType').value = g ? g.type : 'Integer';
    $('TgValue').value = g ? g.value : '0';
    $('TgScope').value = g ? g.scope : 'Local';
    $('TgDesc').value = g ? g.description : '';
    $('tgFormState').textContent = '';
    $('tgForm').style.display = '';
    syncValueControl(true);
    $('TgName').focus();
}

function closeForm() {
    $('tgForm').style.display = 'none';
    editingName = null;
}

async function saveForm() {
    const body = {
        name: $('TgName').value.trim(),
        type: $('TgType').value,
        value: formValue(),
        description: $('TgDesc').value.trim(),
        scope: $('TgScope').value
    };
    const st = $('tgFormState');
    st.textContent = '…';
    try {
        const saved = editingName
            ? await send(`${API}/project/${encodeURIComponent(editingName)}`, 'PUT', body)
            : await send(`${API}/project`, 'POST', body);
        selName = saved.name;
        closeForm();
        await loadProject();
        $('tgProjState').textContent = '✅ ' + tr('tag.saved', 'Đã lưu') + ' ' + new Date().toLocaleTimeString();
    } catch (e) {
        st.textContent = '⚠️ ' + e.message;
    }
}

async function deleteSelected() {
    if (!selName) return;
    if (!confirm(tr('tag.confirmDel', 'Xoá tag') + ' "' + selName + '"?')) return;
    try {
        await api(`${API}/project/${encodeURIComponent(selName)}`, { method: 'DELETE' });
        selName = null;
        closeForm();
        await loadProject();
    } catch (e) {
        $('tgProjState').textContent = '⚠️ ' + e.message;
    }
}

// ----------------------------------------------------------------- cap nhat gia tri truc tiep
function applyLive(attr, values) {
    document.querySelectorAll(`td[${attr}]`).forEach(td => {
        const v = values[td.getAttribute(attr)];
        if (v === undefined) return;
        if (td.dataset.cur === v) return;
        td.dataset.cur = v;
        td.innerHTML = valueHtml(td.dataset.type, v);
    });
}

async function poll() {
    if (polling || document.hidden) return;
    polling = true;
    try {
        if (tab === 'system') {
            const d = await api(API + '/live?group=system');
            applyLive('data-sys', d.values || {});
            renderSysStatus(d.status);
        } else {
            const d = await api(API + '/live?group=project');
            applyLive('data-proj', d.values || {});
        }
    } catch { /* mat ket noi tam thoi: bo qua, lan sau thu lai */ }
    finally { polling = false; }
}

// ----------------------------------------------------------------- khoi dong
document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));
$('tgAdd').addEventListener('click', () => openForm(null));
$('tgEdit').addEventListener('click', () => selName && openForm(selName));
$('tgDel').addEventListener('click', deleteSelected);
$('tgSave').addEventListener('click', saveForm);
$('tgCancel').addEventListener('click', closeForm);
$('TgType').addEventListener('change', () => syncValueControl(true));
$('tgForm').addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.target.tagName !== 'BUTTON') { e.preventDefault(); saveForm(); }
    if (e.key === 'Escape') closeForm();
});

showTab('system');
setInterval(poll, POLL_MS);
