// ---------------------------------------------------------------------------
// Trang Giam sat: theo doi tang dang chon + ket noi realtime (SignalR).
// - Khung "Su kien gan day" da chuyen sang He thong -> Camera -> tab "Lich su".
// - Chon trinh duyet da chuyen sang Giao dien & ngon ngu (Cai dat).
// - Ban do tang va the dem cho trong/co xe nam tren Layout Editor (doi tuong "Thong ke cho").
// ---------------------------------------------------------------------------
let dashboardFloor = 'B1';
window.addEventListener('floorchange', (e) => { dashboardFloor = e.detail; });

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/monitoring')
    .withAutomaticReconnect()
    .build();

// Co su kien nhan dien moi / camera mat ket noi hoac hoat dong lai -> cap nhat o do tren ban do
connection.on('ParkingEvent', () => { if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });
connection.on('SlotsChanged', () => { if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });
// Huy hieu "Realtime" da bo khoi giao dien; van tu ket noi lai va nap lai o do sau khi noi lai.
connection.onreconnected(() => { if (typeof refreshLayoutSlots === 'function') refreshLayoutSlots(); });

connection.start().catch(err => console.error('SignalR error:', err));
