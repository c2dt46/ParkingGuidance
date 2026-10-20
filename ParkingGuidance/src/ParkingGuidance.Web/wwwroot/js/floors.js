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

    floorSelect.innerHTML = '';
    floors.forEach(f => {
        const opt = document.createElement('option');
        opt.value = f.id;
        opt.textContent = `${f.id} — ${f.name}`;
        floorSelect.appendChild(opt);
    });

    const targetId = (preferredId && floors.some(f => f.id === preferredId))
        ? preferredId
        : (floors[0]?.id ?? null);

    if (targetId) {
        floorSelect.value = targetId;
        setCurrentFloor(targetId, floors);
    }
}

function setCurrentFloor(id, floors) {
    window.currentFloor = id;
    window.currentFloorData = (floors || []).find(f => f.id === id) || null;
    window.dispatchEvent(new CustomEvent('floorchange', { detail: id }));
}

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
    if (window.currentFloor === 'B1') {
        alert('Không thể xoá mặt bằng mặc định B1.');
        return;
    }
    if (!confirm(`Xoá mặt bằng "${window.currentFloor}" khỏi danh sách chọn?`)) return;

    await fetch(`/api/floors/${encodeURIComponent(window.currentFloor)}`, { method: 'DELETE' });
    await loadFloors();
});

loadFloors();
