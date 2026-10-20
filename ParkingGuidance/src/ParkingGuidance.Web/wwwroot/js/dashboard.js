// ---------------------------------------------------------------------------
// Trang Giam sat: theo doi tang dang chon + ket noi realtime (SignalR).
// - Khung "Su kien gan day" da chuyen sang He thong -> Camera -> tab "Lich su".
// - Chon trinh duyet da chuyen sang Giao dien & ngon ngu (Cai dat).
// - Ban do tang va the dem cho trong/co xe nam tren Layout Editor (doi tuong "Thong ke cho").
// ---------------------------------------------------------------------------
let dashboardFloor = 'B1';
window.addEventListener('floorchange', (e) => { dashboardFloor = e.detail; });

const liveBadge = document.getElementById('liveBadge');
function setLive(state) {
    if (!liveBadge) return;
    liveBadge.classList.toggle('off', state !== 'on');
    liveBadge.textContent = state === 'on' ? '● Realtime' : (state === 'retry' ? '● Đang kết nối lại…' : '● Mất kết nối');
}

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/monitoring')
    .withAutomaticReconnect()
    .build();

// Co su kien nhan dien moi / camera mat ket noi hoac hoat dong lai -> cap nhat o do tren ban do
connection.on('ParkingEvent', () => { if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });
connection.on('SlotsChanged', () => { if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });
connection.onreconnecting(() => setLive('retry'));
connection.onreconnected(() => { setLive('on'); if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });
connection.onclose(() => setLive('off'));

setLive('retry');
connection.start().then(() => setLive('on')).catch(err => { console.error('SignalR error:', err); setLive('off'); });
