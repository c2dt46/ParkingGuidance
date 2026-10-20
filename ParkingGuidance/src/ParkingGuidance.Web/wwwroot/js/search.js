const input = document.getElementById('plateInput');

document.querySelectorAll('.key[data-key]').forEach(btn => {
    btn.addEventListener('click', () => { input.value += btn.dataset.key; });
});

document.querySelector('.key.back').addEventListener('click', () => {
    input.value = input.value.slice(0, -1);
});

document.querySelector('.key.clear').addEventListener('click', () => {
    input.value = '';
});

document.getElementById('clearBtn').addEventListener('click', () => {
    input.value = '';
    document.getElementById('results').innerHTML = '';
});

document.getElementById('searchBtn').addEventListener('click', async () => {
    const q = input.value.trim();
    if (q.length < 4) {
        alert('Nhập ít nhất 4 ký tự cuối của biển số xe');
        return;
    }

    const res = await fetch(`/api/search?q=${encodeURIComponent(q)}`);
    if (!res.ok) {
        alert('Có lỗi khi tìm kiếm.');
        return;
    }
    const data = await res.json();
    renderResults(data);
});

function renderResults(sessions) {
    const container = document.getElementById('results');
    container.innerHTML = '';

    if (sessions.length === 0) {
        container.innerHTML = '<p class="no-result">Không tìm thấy xe phù hợp.</p>';
        return;
    }

    sessions.forEach(s => {
        const status = s.isActive ? '🟢 Đang đỗ' : '⚪ Đã rời bãi';
        const card = document.createElement('div');
        card.className = 'result-card';
        card.innerHTML = `
            <div class="plate">${s.plateNumber}</div>
            <div class="slot">Vị trí: ${s.slotId}</div>
            <div class="status">${status}</div>
        `;
        container.appendChild(card);
    });
}
