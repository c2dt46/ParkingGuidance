// ---------------------------------------------------------------------------
// Chuyen tab (Danh sach / Cau hinh chung / Nhat ky)
// ---------------------------------------------------------------------------
document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
        document.querySelectorAll('.sys-tab-panel').forEach(p => p.classList.remove('active'));
        btn.classList.add('active');
        document.getElementById(`${btn.dataset.tab}Tab`).classList.add('active');
    });
});

// ---------------------------------------------------------------------------
// Danh muc hang camera
// ---------------------------------------------------------------------------
let brands = [];

async function loadBrands() {
    const res = await fetch('/api/system/cameras/brands');
    brands = await res.json();

    const fill = (select) => {
        select.innerHTML = brands.map(b => `<option value="${b.name}">${b.name}</option>`).join('');
    };
    fill(document.getElementById('brandSelect'));
    fill(document.getElementById('newCamBrand'));
}

// ---------------------------------------------------------------------------
// Do tim camera tren mang (chi khi bam "Quet lai")
// ---------------------------------------------------------------------------
const scanBtn = document.getElementById('scanBtn');
const scanStatus = document.getElementById('scanStatus');
const scanResultsBody = document.getElementById('scanResultsBody');

scanBtn.addEventListener('click', async () => {
    const brand = document.getElementById('brandSelect').value;
    scanBtn.disabled = true;
    scanBtn.textContent = '⏳ Đang quét...';
    scanStatus.textContent = `Đang dò tìm camera "${brand}" trên mạng LAN (tối đa ~3 giây)...`;
    scanResultsBody.innerHTML = '';

    try {
        const res = await fetch(`/api/system/cameras/discover?brand=${encodeURIComponent(brand)}&timeoutMs=3000`);
        const { cameras, diagnostics } = await res.json();
        renderScanResults(cameras);
        scanStatus.innerHTML = buildScanStatusMessage(cameras, diagnostics, brand);
    } catch (err) {
        scanStatus.textContent = 'Có lỗi khi quét mạng: ' + err.message;
    } finally {
        scanBtn.disabled = false;
        scanBtn.textContent = '🔄 Quét lại';
    }
});

function buildScanStatusMessage(cameras, diag, brand) {
    if (cameras.length > 0) {
        return `Tìm thấy ${cameras.length} camera.`;
    }

    // Khong tim thay gi - chi ro dang o buoc nao de de chan doan, thay vi chi noi chung chung
    if (!diag || diag.interfacesUsed.length === 0) {
        return '⚠️ Không gửi được gói dò tìm qua bất kỳ card mạng nào — kiểm tra kết nối mạng của máy chạy hệ thống.';
    }
    if (diag.rawPacketsReceived === 0) {
        return `⚠️ Đã gửi dò tìm qua ${diag.interfacesUsed.length} card mạng (${diag.interfacesUsed.join(', ')}) nhưng ` +
            `<strong>không nhận được phản hồi nào</strong> trong 3 giây. Nguyên nhân thường gặp: ` +
            `(1) máy chạy hệ thống không cùng mạng LAN/VLAN vật lý với camera — multicast không đi qua router khác lớp mạng; ` +
            `(2) tường lửa (Windows Firewall / tường lửa mạng) đang chặn UDP phản hồi gửi về; ` +
            `(3) camera tắt tính năng ONVIF/WS-Discovery trong cấu hình. Thử thêm thủ công theo IP nếu đã biết địa chỉ.`;
    }
    if (diag.camerasFoundBeforeBrandFilter > 0) {
        return `Nhận được phản hồi từ ${diag.camerasFoundBeforeBrandFilter} camera, nhưng không camera nào khớp hãng "${brand}" ` +
            `(hệ thống nhận diện hãng dựa trên thông tin camera tự khai báo, không phải lúc nào cũng đúng). ` +
            `Thử chọn "Khác / ONVIF chuẩn" để xem toàn bộ camera dò được.`;
    }
    return `Nhận được ${diag.rawPacketsReceived} phản hồi nhưng không đọc được thông tin camera hợp lệ từ đó.`;
}

function renderScanResults(list) {
    if (list.length === 0) {
        scanResultsBody.innerHTML = '<tr><td colspan="6" class="no-result">Không tìm thấy camera nào.</td></tr>';
        return;
    }

    scanResultsBody.innerHTML = list.map(c => `
        <tr>
            <td>${c.ipAddress}</td>
            <td>${c.port}</td>
            <td>${c.manufacturer || '<span class="hint small">Không rõ</span>'}</td>
            <td>${c.model || ''}</td>
            <td>${c.alreadyAdded ? '<span class="status-pill added">Đã có trong hệ thống</span>' : '<span class="status-pill new">Mới</span>'}</td>
            <td>${c.alreadyAdded ? '' : `<button class="btn secondary btn-sm" data-add-ip="${c.ipAddress}" data-add-port="${c.port}" data-add-brand="${c.manufacturer || ''}" data-add-model="${c.model || ''}" data-add-url="${c.onvifServiceUrl || ''}">➕ Thêm</button>`}</td>
        </tr>
    `).join('');

    scanResultsBody.querySelectorAll('[data-add-ip]').forEach(btn => {
        btn.addEventListener('click', () => addCamera({
            ipAddress: btn.dataset.addIp,
            port: parseInt(btn.dataset.addPort, 10),
            brand: btn.dataset.addBrand,
            model: btn.dataset.addModel,
            onvifServiceUrl: btn.dataset.addUrl
        }));
    });
}

// ---------------------------------------------------------------------------
// Them camera theo IP (nut rieng + tu ket qua quet)
// ---------------------------------------------------------------------------
const addCameraModal = document.getElementById('addCameraModal');

document.getElementById('addCameraBtn').addEventListener('click', () => {
    document.getElementById('newCamIp').value = '';
    document.getElementById('newCamPort').value = '80';
    document.getElementById('newCamModel').value = '';
    document.getElementById('addCameraError').textContent = '';
    addCameraModal.style.display = 'flex';
});
document.getElementById('cancelAddCameraBtn').addEventListener('click', () => {
    addCameraModal.style.display = 'none';
});
document.getElementById('confirmAddCameraBtn').addEventListener('click', () => {
    addCamera({
        ipAddress: document.getElementById('newCamIp').value.trim(),
        port: parseInt(document.getElementById('newCamPort').value, 10) || 80,
        brand: document.getElementById('newCamBrand').value,
        model: document.getElementById('newCamModel').value.trim() || null
    });
});

async function addCamera(payload) {
    const errBox = document.getElementById('addCameraError');
    if (!payload.ipAddress) {
        if (errBox) errBox.textContent = 'Vui lòng nhập địa chỉ IP.';
        return;
    }

    const res = await fetch('/api/system/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (res.status === 409) {
        const err = await res.json();
        if (errBox) errBox.textContent = err.message;
        else alert(err.message);
        return;
    }
    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.message || 'Không thêm được camera.');
        return;
    }

    addCameraModal.style.display = 'none';
    await loadRegistry();
}

// ---------------------------------------------------------------------------
// Danh sach camera da co trong he thong
// ---------------------------------------------------------------------------
let registryCameras = [];
let selectedCameraId = null;

async function loadRegistry() {
    const res = await fetch('/api/system/cameras');
    registryCameras = await res.json();
    renderRegistry();
}

function renderRegistry() {
    const body = document.getElementById('registryBody');
    if (registryCameras.length === 0) {
        body.innerHTML = '<tr><td colspan="6" class="no-result">Chưa có camera nào trong danh sách.</td></tr>';
        return;
    }

    body.innerHTML = registryCameras.map(c => `
        <tr data-id="${c.id}">
            <td>${c.ipAddress}:${c.port}</td>
            <td>${c.brand || ''}${c.model ? ' · ' + c.model : ''}</td>
            <td>${c.floor || '<span class="hint small">Chưa gán</span>'}</td>
            <td>${c.zone ? 'Zone ' + c.zone : '<span class="hint small">Chưa gán</span>'}</td>
            <td>${c.isCommitted ? '<span class="status-pill committed">✅ Đã vào bãi đỗ</span>' : '<span class="status-pill draft">Nháp</span>'}</td>
            <td><button class="btn secondary btn-sm" data-select-id="${c.id}">Chi tiết</button></td>
        </tr>
    `).join('');

    body.querySelectorAll('[data-select-id]').forEach(btn => {
        btn.addEventListener('click', () => selectCamera(btn.dataset.selectId));
    });
}

async function selectCamera(id) {
    selectedCameraId = id;
    const cam = registryCameras.find(c => c.id === id);
    if (!cam) return;

    const floorsRes = await fetch('/api/floors');
    const floors = await floorsRes.json();
    const floorOptions = ['<option value="">— Chưa gán —</option>']
        .concat(floors.map(f => `<option value="${f.id}" ${cam.floor === f.id ? 'selected' : ''}>${f.id} — ${f.name}</option>`))
        .join('');

    const panel = document.getElementById('cameraDetailPanel');
    panel.style.display = 'block';
    panel.innerHTML = `
        <h3>Camera ${cam.ipAddress}:${cam.port} ${cam.brand ? '· ' + cam.brand : ''}</h3>
        <div class="prop-grid-2">
            <div class="prop-field">
                <label>Tầng hầm</label>
                <select id="detailFloor">${floorOptions}</select>
            </div>
            <div class="prop-field">
                <label>Zone (khu vực lắp đặt trong tầng)</label>
                <input id="detailZone" type="text" inputmode="numeric" pattern="[0-9]*" placeholder="1, 2, 3..." value="${cam.zone ?? ''}" />
                <p class="hint small">Số nguyên dương. Mỗi tầng có các Zone riêng; số thứ tự các vị trí nhận diện không được trùng trong cùng 1 Zone.</p>
            </div>
            <div class="prop-field">
                <label>Trạng thái</label>
                <div>${cam.isCommitted ? '<span class="status-pill committed">✅ Đã vào hệ thống bãi đỗ</span>' : '<span class="status-pill draft">Nháp — chưa vào hệ thống</span>'}</div>
            </div>
        </div>
        <div class="prop-field">
            <label>Ghi chú</label>
            <textarea id="detailNotes" rows="2" placeholder="Vị trí lắp đặt, góc quay...">${cam.notes ?? ''}</textarea>
        </div>
        <div class="prop-field">
            <label>Nguồn video (URL RTSP / đường dẫn file video)</label>
            <input id="detailVideoSource" type="text" placeholder="rtsp://user:pass@${cam.ipAddress}:554/..." value="${cam.videoSource ?? ''}" />
            <p class="hint small">Cần điền để dịch vụ camera nền thực sự đọc được video — hệ thống dò IP qua ONVIF nhưng chưa tự lấy được URL luồng RTSP (khác nhau tuỳ hãng).</p>
        </div>
        <div class="prop-actions">
            <button id="saveDetailBtn" class="btn secondary">💾 Lưu thông tin</button>
            ${cam.isCommitted
                ? ''
                : '<button id="commitBtn" class="btn primary">➕ Thêm vào hệ thống bãi đỗ</button>'}
            <button id="deleteCameraBtn" class="btn secondary">🗑️ Xoá</button>
        </div>
        <p class="hint small">${cam.isCommitted ? '' : 'Cần chọn tầng hầm và nhập Zone trước khi thêm vào hệ thống bãi đỗ.'}</p>
    `;

    // Luu tang ham + Zone + ghi chu + nguon video. Tra ve false (va bao loi) neu may chu tu choi
    // (vd Zone khong hop le, co tang ma thieu Zone, so thu tu vi tri bi trung o Zone moi).
    async function saveCameraInfo() {
        const res = await fetch(`/api/system/cameras/${id}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                floor: document.getElementById('detailFloor').value || null,
                zone: document.getElementById('detailZone').value.trim() || null,
                notes: document.getElementById('detailNotes').value,
                videoSource: document.getElementById('detailVideoSource').value || null
            })
        });
        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            alert(err.message || 'Không lưu được thông tin camera.');
            return false;
        }
        return true;
    }

    document.getElementById('saveDetailBtn').addEventListener('click', async () => {
        if (!(await saveCameraInfo())) return;
        await loadRegistry();
        selectCamera(id);
    });

    const commitBtn = document.getElementById('commitBtn');
    if (commitBtn) {
        commitBtn.addEventListener('click', async () => {
            // Luu tang ham/Zone/ghi chu/nguon video truoc, roi moi commit
            if (!(await saveCameraInfo())) return;
            const res = await fetch(`/api/system/cameras/${id}/commit`, { method: 'POST' });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                alert(err.message || 'Không thêm được vào hệ thống bãi đỗ.');
                return;
            }
            await loadRegistry();
            selectCamera(id);
        });
    }

    document.getElementById('deleteCameraBtn').addEventListener('click', async () => {
        if (!confirm('Xoá camera này khỏi danh sách?')) return;
        await fetch(`/api/system/cameras/${id}`, { method: 'DELETE' });
        panel.style.display = 'none';
        selectedCameraId = null;
        await loadRegistry();
    });
}

// ---------------------------------------------------------------------------
// Khoi tao
// ---------------------------------------------------------------------------
loadBrands();
loadRegistry();
