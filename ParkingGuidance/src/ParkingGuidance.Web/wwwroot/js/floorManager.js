// (Doi ten tu floors.js -> floorManager.js de trinh duyet/ban build cu khong con dung nham file cu.)
// Quản lý mặt bằng dùng chung cho cả bản đồ ô đỗ mặc định (dashboard.js) và
// Layout Editor (layout.js). Cả 2 file đó lắng nghe sự kiện 'floorchange' trên window.

window.currentFloor = null;
window.currentFloorData = null;

const floorSelect = document.getElementById('floorSelect');
const addFloorBtn = document.getElementById('addFloorBtn');
const deleteFloorBtn = document.getElementById('deleteFloorBtn');

async function fetchFloors() {
    const res = await fetch('/api/floors');
    return res.json();
}

async function loadFloors(preferredId) {
    const floors = await fetchFloors();

    window.dispatchEvent(new CustomEvent('floorlistchange', { detail: floors }));   // menu trái cập nhật mục con của "Giám sát"

    floorSelect.innerHTML = '';
    floors.forEach(f => {
        const opt = document.createElement('option');
        opt.value = f.id;
        opt.textContent = `${f.isStartup ? '★ ' : ''}${f.id} — ${f.name}`;
        floorSelect.appendChild(opt);
    });

    // Không chỉ định mặt bằng (mở trang lần đầu / vừa xoá mặt bằng đang chọn) -> mặt bằng khởi động, rồi mới tới mặt bằng đầu danh sách
    const targetId = (preferredId && floors.some(f => f.id === preferredId))
        ? preferredId
        : (floors.find(f => f.isStartup)?.id ?? floors[0]?.id ?? null);

    if (targetId) {
        floorSelect.value = targetId;
        setCurrentFloor(targetId, floors);
    }
}

function setCurrentFloor(id, floors) {
    window.currentFloor = id;
    window.currentFloorData = (floors || []).find(f => f.id === id) || null;
    syncStartupCheckbox();
    window.dispatchEvent(new CustomEvent('floorchange', { detail: id }));
}

// Ô "Mặt bằng khởi động đầu tiên": tích khi mặt bằng đang chọn chính là mặt bằng khởi động.
// Đang tích thì khoá (luôn phải có đúng 1 mặt bằng khởi động) — muốn đổi thì chọn mặt bằng khác rồi tích vào đó.
const startupChk = document.getElementById('startupFloorChk');

function syncStartupCheckbox() {
    if (!startupChk) return;
    const isStartup = !!window.currentFloorData?.isStartup;
    startupChk.checked = isStartup;
    startupChk.disabled = !window.currentFloorData || isStartup;
    startupChk.title = isStartup
        ? 'Đây đang là mặt bằng khởi động. Chọn mặt bằng khác rồi tích vào đó để chuyển.'
        : '';
}

startupChk?.addEventListener('change', async () => {
    if (!window.currentFloor || !startupChk.checked) { syncStartupCheckbox(); return; }
    const res = await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}/startup`, { method: 'PUT' });
    if (!res.ok) {
        alert('Không đặt được mặt bằng khởi động.');
        syncStartupCheckbox();
        return;
    }
    await loadFloors(window.currentFloor);   // nạp lại để dấu ★ chuyển sang mặt bằng này
});

floorSelect.addEventListener('change', async () => {
    const floors = await fetchFloors();
    setCurrentFloor(floorSelect.value, floors);
});

addFloorBtn.addEventListener('click', async () => {
    const id = prompt('Mã mặt bằng mới (ví dụ: B2):');
    if (!id || !id.trim()) return;
    const name = prompt('Tên hiển thị (ví dụ: Tầng hầm B2):', id.trim()) || id.trim();

    const res = await fetch('/api/floors', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: id.trim(), name })
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không thêm được mặt bằng.');
        return;
    }

    await loadFloors(id.trim());
});

document.getElementById('renameFloorBtn').addEventListener('click', async () => {
    if (!window.currentFloor) return;
    const current = window.currentFloorData?.name || window.currentFloor;
    const name = prompt(`Tên hiển thị mới cho mặt bằng ${window.currentFloor}:`, current);
    if (!name || !name.trim()) return;

    await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: window.currentFloor, name: name.trim() })
    });
    await loadFloors(window.currentFloor);
});

deleteFloorBtn.addEventListener('click', async () => {
    if (!window.currentFloor) return;
    // Được phép xoá mọi mặt bằng (kể cả B1). Khi xoá mặt bằng cuối cùng, server tự tạo "Mặt bằng mặc định".
    const isLast = floorSelect.options.length <= 1;
    const warn = 'Toàn bộ dữ liệu của mặt bằng này sẽ bị xoá vĩnh viễn: đối tượng trên bản đồ, ô đỗ và lịch sử đỗ xe, '
        + 'vị trí nhận diện của camera (camera được giữ lại nhưng bị gỡ khỏi mặt bằng), ảnh nền. Không thể hoàn tác.';
    const msg = isLast
        ? `Xoá mặt bằng "${window.currentFloor}"?\n\n${warn}\n\nĐây là mặt bằng cuối cùng nên hệ thống sẽ tự tạo lại "Mặt bằng mặc định".`
        : `Xoá mặt bằng "${window.currentFloor}"?\n\n${warn}`;
    if (!confirm(msg)) return;

    const res = await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}`, { method: 'DELETE' });
    if (!res.ok) {
        alert('Không xoá được mặt bằng.');
        return;
    }
    await loadFloors();
});

// Vào từ mục con của menu trái: /?floor=MÃ -> mở đúng mặt bằng đó (không có thì dùng mặt bằng khởi động)
const urlFloor = new URLSearchParams(location.search).get('floor');
if (urlFloor) history.replaceState(null, '', location.pathname);   // bỏ ?floor khỏi thanh địa chỉ để tải lại không bị ghim
loadFloors(urlFloor || undefined);
