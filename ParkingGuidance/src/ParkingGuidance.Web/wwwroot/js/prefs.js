// ---------------------------------------------------------------------------
// Tuy chon giao dien (nen sang/toi, ngon ngu, font, co chu, mau nhan).
// Hai pham vi ap dung:
//   - 'global' (mac dinh): dung chung cho MOI may, luu tren server (settings.json, API /api/system/ui-prefs).
//                          May nao o che do nay tu lay gia tri server moi khi mo/quay lai trang.
//   - 'local'            : chi rieng may nay, luu trong localStorage cua trinh duyet, bo qua gia tri server.
// Gia tri gan nhat luon duoc cache trong localStorage de ap dung NGAY khi mo trang (khong nhay mau).
// ---------------------------------------------------------------------------
(function () {
    const KEY = 'pg.prefs';
    const API = '/api/system/ui-prefs';
    const DEFAULTS = { theme: 'dark', lang: 'vi', font: 'segoe', size: 100, accent: '#3b82f6' };
    const VALUE_KEYS = Object.keys(DEFAULTS);
    const FONTS = {
        segoe:  '"Segoe UI", Roboto, sans-serif',
        roboto: 'Roboto, "Segoe UI", sans-serif',
        arial:  'Arial, Helvetica, sans-serif',
        verdana:'Verdana, Geneva, sans-serif',
        georgia:'Georgia, "Times New Roman", serif',
        mono:   'Consolas, "Courier New", monospace'
    };

    // Tu dien: chi can them khoa moi o day + data-i18n="khoa" tren the HTML. Tieng Viet la ngon ngu goc.
    const I18N = {
        en: {
            'nav.operate': 'OPERATIONS', 'nav.monitor': '🗂️ Monitoring', 'nav.search': '🔍 Find vehicle', 'nav.report': '📈 Reports',
            'nav.manage': 'PARKING MANAGEMENT', 'nav.catalog': '🗄️ Catalog', 'nav.map': '🗺️ Map', 'nav.import': '⬆️ Import', 'nav.kiosk': '🖥️ Kiosk & routes',
            'nav.settings': 'SETTINGS & SYSTEM', 'nav.user': '👤 User', 'nav.prefs': '🎨 Appearance & language',
            'nav.camera': '📷 Camera', 'nav.sensor': '📡 Sensors', 'nav.led': '💡 LED', 'nav.other': '🧩 External devices',
            'brand.sub': 'Parking guidance', 'prof.admin': 'Administrator',
            'pref.title': 'User — Appearance & language',
            'pref.hint': 'Changes apply immediately. Choose below whether they are for this computer only or for every computer.',
            'pref.scope': 'Apply to', 'pref.local': '🖥️ This computer only', 'pref.global': '🌐 All computers (saved on server)',
            'pref.scopeHintLocal': 'This computer uses its own settings; other computers are not affected.',
            'pref.scopeHintGlobal': 'Every computer in "All computers" mode loads these settings from the server when it opens or returns to a page.',
            'pref.theme': 'Background', 'pref.dark': '🌙 Dark', 'pref.light': '☀️ Light', 'pref.auto': '🖥️ Follow system',
            'pref.lang': 'Language', 'pref.font': 'Font', 'pref.size': 'Text size', 'pref.accent': 'Accent color',
            'pref.preview': 'Preview', 'pref.sample': 'Parking Guidance — AaBbCc 0123456789',
            'pref.reset': '↩️ Restore defaults', 'pref.note': 'Note: the language switch currently covers the menu and this page; other pages will follow as translations are added.',
            "led.addImage": "Add image",
            "led.addText": "Add text",
            "led.addNum": "Add number",
            "led.del": "Delete",
            "led.up": "Up",
            "led.down": "Down",
            "led.send": "Send to card",
            "led.props": "Properties",
            "led.propsTitle": "Display area properties",
            "led.propsHint": "Click an area on the LED panel (or a row in the table) to see its properties.",
            "led.tabCard": "LED card settings",
            "led.save": "Save",
            "led.addSrc": "Add source",
            "led.cardNo": "Card no.",
            "led.ip": "IP address",
            "led.status": "Status",
            "led.off": "Off",
            "led.on": "On",
            "led.w": "Width",
            "led.h": "Height",
            "led.cardType": "Card type",
            "led.color": "Color",
            "led.gray": "Gray level",
            "led.conn": "Connection",
            "led.cardList": "Card list",
            "led.screen": "Screen",
            "led.areas": "Areas",
            "led.type": "Type",
            "led.ox": "Origin X",
            "led.oy": "Origin Y",
            "led.hImg": "IMAGE AREA SETTINGS",
            "led.hTxt": "TEXT AREA SETTINGS",
            "led.hNum": "NUMBER AREA SETTINGS",
            "led.image": "Image",
            "led.pick": "Choose image",
            "led.effect": "Effect",
            "led.speed": "Movement speed",
            "led.delay": "Delay",
            "led.msg": "Message",
            "led.fsize": "Font size",
            "led.ftype": "Font type",
            "led.dMs": "MS data (sensors)",
            "led.dMc": "MC data (cameras)",
            "led.dTag": "Project tag",
            "led.numHint": "Displayed number = MS variable + MC variable + project tag selected (an empty entry counts as 0). Project tags are limited to Integer, Boolean and Real types.",
            "led.digits": "Digit count",
            "led.autoColor": "Auto color mode",
            "led.manual": "Manual",
            "led.auto": "Auto",
            "led.yel": "Yellow when &lt;=",
            "led.red": "Red when &lt;=",
            "led.full": "Show FULL at zero",
            "led.timeout": "LED send timeout (s)",
            "led.reg": "Register",
            "led.regName": "Label",
            "led.count": "Count",
            "led.curVal": "Current value",
            "led.free": "Free spaces",
            "led.occ": "Occupied spaces",
            "led.nameCol": "Name",
            "led.src": "Source",
            "led.val": "Value",
            "led.no": "No.",
            "led.optNo": "No",
            "led.optYes": "Yes",
            "nav.device": "Devices",
            "nav.cam": "Camera",
            "nav.sen": "Sensors",
            "nav.ledn": "LED",
            "led.timeoutHint": "If the card/SDK does not respond within this time, the send counts as failed (the bridge restarts itself and retries later). Takes effect immediately, no restart needed.",
            "led.none": "None",
            "led.t.Image": "Image",
            "led.t.Text": "Text",
            "led.t.Number": "Number",
            "led.empty": "No display areas yet",
            "nav.tasks": "TASKS",
            "nav.tags": "🏷️ Tags",
            "nav.script": "📜 Script",
            "tag.title": "Tasks — Tags",
            "tag.tabSystem": "System tags",
            "tag.tabProject": "Project tags",
            "tag.colNo": "No.",
            "tag.colName": "Name",
            "tag.colValue": "Value",
            "tag.colType": "Type",
            "tag.colDesc": "Description",
            "tag.colScope": "Scope",
            "tag.sysHint": "Built-in tags, read-only. They are updated by a separate background thread that does not touch cameras, sensors, LEDs or parking data, so they cannot disturb the main flow.",
            "tag.projHint": "Tags declared by you. Pick a row to edit or delete it. Names may use letters, digits and underscore, and must not duplicate a System tag.",
            "tag.add": "Add tag",
            "tag.edit": "Edit",
            "tag.delete": "Delete",
            "tag.save": "Save",
            "tag.cancel": "Cancel",
            "tag.formNew": "New tag",
            "tag.formEdit": "Edit tag",
            "tag.empty": "No project tags yet. Press “Add tag” to declare one.",
            "script.title": "Tasks — Script",
            "script.head": "Script",
            "script.hint": "This section is reserved for scripts that read and write tags (System tags and Project tags). The editor and the runtime engine are not available yet.",
            "tag.head": "Tags",
            "tag.stRun": "● System tags thread running",
            "tag.stStop": "● System tags thread not running",
            "tag.stOff": "System tags are disabled by configuration (Tags:SystemEnabled = false).",
            "tag.stTick": "tick",
            "tag.stSlow": "slow blink",
            "tag.stFast": "fast blink",
            "tag.stErr": "errors",
            "tag.confirmDel": "Delete tag",
            "tag.saved": "Saved",
            'pref.saved': '✅ Saved for all computers at ', 'pref.savedLocal': '✅ Saved on this computer at ', 'pref.fail': '⚠️ Could not save to server: '
        }
    };

    function readStore() { try { return JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { return {}; } }
    function writeStore(o) { try { localStorage.setItem(KEY, JSON.stringify(o)); } catch { } }
    function pickValues(o) { const r = {}; for (const k of VALUE_KEYS) r[k] = o[k] ?? DEFAULTS[k]; return r; }

    let state = Object.assign({ scope: 'global' }, DEFAULTS, readStore());
    if (state.scope !== 'local') state.scope = 'global';

    function apply() {
        const root = document.documentElement;
        const dark = state.theme === 'auto' ? !matchMedia('(prefers-color-scheme: light)').matches : state.theme !== 'light';
        root.dataset.theme = dark ? 'dark' : 'light';
        root.style.colorScheme = dark ? 'dark' : 'light';
        root.lang = state.lang;
        root.style.setProperty('--app-font', FONTS[state.font] || FONTS.segoe);
        root.style.setProperty('--accent', state.accent);
        root.style.zoom = (state.size / 100).toString();
    }
    function t(key, vi) { const d = I18N[state.lang]; return (d && d[key]) || vi; }
    function translate() {
        document.querySelectorAll('[data-i18n]').forEach(el => {
            if (el.dataset.vi === undefined) el.dataset.vi = el.textContent;   // nho ban tieng Viet goc
            el.textContent = t(el.dataset.i18n, el.dataset.vi);
        });
    }
    function refreshAll() { writeStore(state); apply(); translate(); document.dispatchEvent(new CustomEvent('pgprefs', { detail: api.get() })); }

    // ----- server -----
    async function fetchGlobal() {
        if (state.scope !== 'global') return;
        try {
            const res = await fetch(API, { cache: 'no-store' });
            if (!res.ok) return;
            const g = await res.json();
            const next = {};
            for (const k of VALUE_KEYS) next[k] = g[k] ?? DEFAULTS[k];
            if (VALUE_KEYS.some(k => next[k] !== state[k])) { Object.assign(state, next); refreshAll(); }
        } catch { /* mat ket noi: giu ban cache */ }
    }
    async function pushGlobal() {
        const res = await fetch(API, {
            method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pickValues(state))
        });
        if (!res.ok) throw new Error('HTTP ' + res.status);
    }
    let pushTimer = null;
    function schedulePush() {                       // gop cac thao tac keo thanh truot lien tiep thanh 1 lan luu
        return new Promise((resolve, reject) => {
            clearTimeout(pushTimer);
            pushTimer = setTimeout(() => pushGlobal().then(resolve, reject), 350);
        });
    }

    apply();
    document.addEventListener('DOMContentLoaded', () => { translate(); fetchGlobal(); });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) fetchGlobal(); });
    matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => { if (state.theme === 'auto') apply(); });

    const api = {
        FONTS, DEFAULTS, t,
        get: () => Object.assign({}, state),
        /** Doi gia tri. Neu dang o che do 'global' thi luu len server (tra ve Promise). */
        set(patch) {
            for (const k of VALUE_KEYS) if (k in patch) state[k] = patch[k];
            refreshAll();
            return state.scope === 'global' ? schedulePush() : Promise.resolve();
        },
        /** Chuyen pham vi 'local' <-> 'global'. Chuyen sang 'global' = day gia tri cua may nay len server cho moi may. */
        setScope(scope) {
            state.scope = scope === 'local' ? 'local' : 'global';
            refreshAll();
            return state.scope === 'global' ? pushGlobal() : Promise.resolve();
        },
        reset() { return api.set(DEFAULTS); }
    };
    window.PGPrefs = api;
})();
