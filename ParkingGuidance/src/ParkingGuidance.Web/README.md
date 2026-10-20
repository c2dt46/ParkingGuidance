
---

## Người dùng › Giao diện & ngôn ngữ

Trang `/User/Preferences` (menu **Người dùng → 🎨 Giao diện & ngôn ngữ**) cho chọn: màu nền (tối/sáng/theo hệ thống), ngôn ngữ (Việt/English), font chữ, cỡ chữ, màu nhấn.

Mục **Áp dụng cho**:

| Chế độ | Lưu ở đâu | Tác dụng |
|---|---|---|
| 🌐 **Mọi máy** (mặc định) | `settings.json` trên server (`Ui`), API `GET/PUT /api/system/ui-prefs` | Mọi máy ở chế độ này tự lấy giao diện từ server khi mở trang hoặc quay lại tab |
| 🖥️ **Chỉ máy này** | `localStorage` của trình duyệt (`pg.prefs`) | Máy này dùng giao diện riêng, không ảnh hưởng và không bị ảnh hưởng bởi máy khác |

Chuyển từ "Chỉ máy này" sang "Mọi máy" sẽ đẩy giao diện hiện tại của máy đó lên server cho tất cả. Ngôn ngữ mới áp dụng cho menu và trang này; thêm bản dịch bằng cách gắn `data-i18n="khoa"` và khai báo khoá trong `wwwroot/js/prefs.js` (`I18N`).

---

## Thiết bị › Camera › 🧪 Mô phỏng

Tab **Mô phỏng** (trang `/System/Cameras`) nhận diện thử một **thư mục ảnh** trên máy tính, từng ảnh một:

1. Bấm **📂 Chọn thư mục ảnh** (hoặc **🖼️ Chọn nhiều ảnh**). Ảnh jpg/png/bmp/webp được sắp xếp theo tên (số tự nhiên: `anh_2` trước `anh_10`), ảnh đầu tiên được nhận diện ngay.
2. Kết quả hiện ngay trên ảnh: khung xe (xanh = đọc được biển, cam = chưa đọc được), khung biển số (nét đứt xanh dương), nhãn `số thứ tự. biển số`; bên phải là bảng loại xe, màu, biển số và độ tin cậy.
3. Bấm **Tiếp theo ▶** (hoặc phím →) để nhận diện ảnh kế tiếp; **◀ Trước** (phím ←) xem lại ảnh đã nhận diện (không chạy lại AI); **🔁 Nhận diện lại** chạy lại ảnh hiện tại. Bấm vào một dòng trong danh sách để nhảy tới ảnh đó.
4. **Chuyển ảnh tự động**: nhập số giây ở ô *Chuyển ảnh sau (giây)* rồi bấm **▶ Chạy tự động** — mỗi ảnh nhận diện xong sẽ đếm ngược chừng đó giây rồi **tự sang ảnh kế tiếp**; bấm **⏸ Dừng tự động** để dừng. Tự dừng ở ảnh cuối, khi rời tab, và tạm dừng khi bạn đang nhập biển / cắt vùng hoặc trình duyệt bị ẩn. Số giây được nhớ trên trình duyệt; chế độ tự động luôn tắt khi mở lại tab.
5. **Tất cả xe** trong ảnh đều được nhận diện và đọc biển số — **không áp dụng vùng nhận diện**. Ô *Ngưỡng tin cậy xe* (bỏ trống = dùng cài đặt chung) chỉnh độ nhạy phát hiện xe.

**Không ảnh hưởng việc khác & giải phóng tài nguyên:**

- Phía máy chủ: ảnh mô phỏng xếp ở **hàng đợi ưu tiên thấp nhất** của `AiJobScheduler` (`RunLowPriorityAsync`) — chỉ chạy khi camera thật và các nút "Nhận diện ngay/Chạy thử" **không còn việc chờ**, nên không bao giờ chen trước nhận diện thật (kể cả khi bật tự chuyển ảnh). Việc mô phỏng không tính vào thống kê hàng đợi camera và không sinh cảnh báo "hàng đợi quá tải". Chờ quá 120 giây thì báo bận; tối đa 8 ảnh mô phỏng chờ cùng lúc.
- Chuyển ảnh nhanh / rời tab / đóng trang: việc **chưa chạy** bị gỡ khỏi hàng đợi ngay (không tốn CPU); việc đang chạy dở được chạy nốt (tối đa 1 ảnh).
- Máy chủ không giữ gì giữa các lần gọi (không cache, không dịch vụ nền); ảnh và `Mat` được giải phóng ngay sau mỗi lần nhận diện.
- Phía trình duyệt: **khi rời tab Mô phỏng** (hoặc ẩn/đóng trang) tab tự dừng đồng hồ tự chuyển, huỷ yêu cầu đang chờ, trả bộ nhớ ảnh + canvas, thu hồi URL tạm và gỡ lắng nghe phím mũi tên — không gửi thêm yêu cầu nào. Quay lại tab thì ảnh đang xem được nạp lại từ file (kết quả đã có được giữ, không chạy lại AI). Nút **🗑 Xoá danh sách** quên hẳn thư mục và kết quả.

Tab này **chỉ để xem kết quả**: không ghi vào bãi đỗ, bản đồ, lịch sử hay SignalR. API: `GET /api/system/simulation/status`, `POST /api/system/simulation/analyze` (form-data `image`, query `conf` tuỳ chọn) — xem `Controllers/SimulationApiController.cs`; giao diện: `wwwroot/js/cameraSimulation.js`.

### ✂️ Cắt/Zoom vùng, nhập biển đúng và 🧠 Train (tab Mô phỏng)

**✂️ Cắt/Zoom vùng** — bấm nút rồi quét chuột trên ảnh để cắt một vùng (vd biển số xa, nhỏ). Vùng hiện phóng to ở **cột bên phải** (dưới khung thông tin ảnh), khung "🔍 Vùng đã cắt":
- **🔍 Nhận diện xe/biển trong vùng**: chạy lại nhận diện riêng trên vùng (ảnh to hơn nên dễ đọc hơn). Không thấy xe nào thì coi cả vùng là biển.
- **🔖 Coi vùng này là biển số**: dùng khi vùng cắt vừa khít biển (bỏ bước dò xe, đọc biển trực tiếp).
- Vùng cắt lấy từ **ảnh gốc** (không mất độ phân giải). Đổi ảnh thì vùng cắt tự đóng.

**Biển đúng** — mỗi xe/vùng có ô *Biển đúng* **tự điền sẵn biển đã nhận diện** (viền nét đứt = mặc định, chưa lưu): biển sai thì chỉ cần sửa vài ký tự rồi Enter; biển đúng thì Enter hoặc bấm **✓ Đúng**. Chưa Enter/✓ thì **chưa lưu** — mặc định không tự thành mẫu học, để biển đọc sai không bị học nhầm. Mẫu (ảnh biển + biển đúng + biển hệ thống đọc) được **lưu trên máy chủ** (`{DataDirectory}/simulation/corrections`), còn nguyên khi tắt/mở lại ứng dụng. Biển hợp lệ: 7–9 ký tự chữ/số, không dùng I, J, O, Q, W. Xe chưa có khung biển thì dùng ✂️ cắt vùng biển rồi nhập. Đang nhập/cắt thì **tự chuyển ảnh tạm dừng**.

**🧠 Train** (khung cuối trang) — dạy lại model đọc ký tự từ các mẫu đã lưu:
1. So biển bạn cấp với biển hệ thống đã đọc: ký tự nào đọc sai được **học kỹ hơn**; mọi dữ liệu train cũ được giữ, mẫu mới **bổ sung** (học tiếp từ lần train trước nếu có).
2. Chạy bằng **PlateTrainer** ở **một tiến trình riêng**, ưu tiên thấp (BelowNormal), ít luồng CPU (`Trainer:Threads`, mặc định ¼ số nhân) — **không làm chậm nhận diện camera thật**; xong là thoát, không có dịch vụ nền nào khi không dùng. Bấm **⏹ Huỷ train** bất cứ lúc nào.
3. Xong, so model mới với model đang chạy (đúng cả biển trên các biển bạn đã cấp + đúng từng ký tự trên tập kiểm chứng chưa từng học). Nếu **không kém** và có **≥ 10 mẫu** thì **tự cài**: sao lưu bản cũ (`char_cnn.onnx.bak-…` ở cả thư mục chạy và `Models/`), chép model mới và **nạp lại ngay, không cần khởi động lại**. Ngược lại giữ nguyên model cũ và hiện nút **✔ Vẫn cài bản mới**.
4. Cần: thư mục `PlateTrainer` cạnh dự án (đã cài bằng `PlateTrainer\install.bat`). Cấu hình ở `appsettings.json` → `Trainer` (`Path`, `Python`, `Epochs`, `Threads`).

API: `GET/PUT/DELETE /api/system/simulation/corrections`, `POST …/corrections/clear`, `POST …/train`, `GET …/train/status`, `POST …/train/cancel`, `POST …/train/install`; `POST …/analyze?mode=region|plate`.

**Bố cục tab:** bên trái là ảnh; cột bên phải từ trên xuống: tóm tắt → thẻ xe (+ ô Biển đúng) → **khung thông tin ảnh** (số thứ tự, tên file, trạng thái, đếm ngược tự động và nút **◀ Lùi ảnh / Ảnh tiếp theo ▶**) → **vùng cắt/zoom**. Thanh công cụ phía trên vẫn có nút Trước/Tiếp theo.

---

## Thiết bị › Cảm biến: 3 tab

Trang `/System/Sensors` được chia thành 4 tab (cùng kiểu với trang Camera):

| Tab | Nội dung |
|---|---|
| ⚙️ **Cấu hình** | *Loại cảm biến* (MGT200 / Keytop) và cài đặt kết nối: IP, port, thời gian chờ, kích hoạt ZCU; nút **Lưu cài đặt** |
| 📡 **Giám sát** | *Giám sát* (kết nối, tổng Trống/Có xe/Lỗi, từng ZCU – từng cảm biến) và *Cài đặt cảm biến MGT200* (lệnh Ground auto/manual/threshold, Lamp mode, Report… — chỉ hiện với MGT200) |
| 📊 **Dữ liệu** | Biến **MS** — dữ liệu tổng hợp từ cảm biến (xem mục bên dưới) |
| 🔍 **Thử khung dữ liệu** | Dán 1 khung dữ liệu để xem chương trình hiểu thế nào; hiển thị loại cảm biến đang chọn ở tab Cấu hình |

Tab đang xem được nhớ khi tải lại trang. **Tiết kiệm tài nguyên:** chỉ tab *Giám sát* mới cập nhật trạng thái mỗi giây; khi chuyển sang tab khác (hoặc ẩn trình duyệt) việc hỏi trạng thái dừng hẳn, và nếu đang bật *Đọc liên tục* thì tự tắt để không gửi lệnh xuống phần cứng khi không ai xem. Quay lại tab Giám sát thì cập nhật ngay.

### 📊 Tab Dữ liệu — biến MS (dữ liệu liên quan tới cảm biến)

Dữ liệu của chương trình được chia theo loại: **MS** (cảm biến, làm ở đây), **MC** (camera), **Modbus TCP/IP client** (mỗi cổng mở một loại biến) và **Modbus TCP/IP server** — các loại còn lại sẽ thiết lập sau.

**Biến MS không còn là mỗi cảm biến một biến.** Một biến chỉ đếm **một trạng thái** trên **các cảm biến được chỉ định**:

| Đếm trạng thái | Tương ứng cảm biến |
|---|---|
| **Có chỗ đỗ** | Trống |
| **Không có chỗ đỗ** | Có xe |
| **Lỗi** | Lỗi |

*Giá trị của biến = số cảm biến (trong tập được chỉ định) đang ở trạng thái đó.* Chỉ cần tạo **1 biến** cho cả nhóm cảm biến; cần đếm riêng (theo khu, theo tầng…) hoặc đếm trạng thái khác thì **tạo thêm biến**.

- **Thêm biến**: bấm một lần là **thêm ngay** một biến, không cần đặt tên — tên tự đặt **MS0, MS1, MS2…** (chỉ số nhỏ nhất còn trống, bắt đầu từ 0). Xoá một biến ở giữa thì lần thêm sau **lấp lại chỗ trống** đó. Biến mới chưa có trạng thái đếm và chưa có cảm biến nào; chọn bằng **Sửa** (hoặc bấm đúp dòng). Tên không đổi được, không trùng System tag / Project tag. Tối đa 200 biến.
- **Mỗi biến chỉ có một chức năng đếm trạng thái duy nhất**: khi đã chọn trạng thái (Có chỗ đỗ / Không có chỗ đỗ / Lỗi) và lưu thì ô *Đếm trạng thái* bị **vô hiệu hoá**, không đổi được nữa (muốn đếm trạng thái khác: xoá biến, thêm lại). Biến chưa chọn trạng thái hiện *"—"* ở cột Giá trị.
- **Chọn cảm biến**: chọn ZCU ở hàng nút (MGT200: ZCU 0–31, mỗi ZCU 5 Line × 32 cảm biến; Keytop: mỗi Keytop 60 cảm biến), rồi **bấm hoặc kéo chuột** trên lưới để chọn / bỏ chọn nhiều ô; bấm một ô rồi **giữ Shift + bấm ô khác** (cùng Line) để chọn / bỏ chọn cả đoạn giữa hai ô (ví dụ ô 7 → Shift + ô 29 chọn 7…29); bấm nhãn **CN n** để chọn / bỏ cả Line; có nút *Chọn cả ZCU này*, *Chọn tất cả*, *Bỏ chọn…*. Nút ZCU có cảm biến được chọn thì viền sáng.
- **Cột Giá trị** cập nhật theo trạng thái cảm biến thật (mỗi giây, chỉ khi đang xem tab này). Kèm ghi chú: *trên N cảm biến*, số cảm biến *chưa có dữ liệu* (đã kết nối nhưng chưa báo) và số cảm biến *ngoài vùng quét* (ZCU chưa kích hoạt).
- **Quy ước đếm** giống tab Giám sát: ZCU đã kích hoạt nhưng **mất kết nối** thì mọi cảm biến của nó tính là **Lỗi**; cảm biến chưa báo lần nào không thuộc trạng thái nào; ZCU **chưa kích hoạt** không được quét nên không được đếm.
- Mỗi biến gắn với **loại cảm biến lúc tạo** (MGT200 hoặc Keytop). Đổi loại cảm biến ở tab Cấu hình thì biến thuộc loại kia hiện *"—"* (không tính được) cho tới khi dùng lại đúng loại; biến không bị xoá.
- Biến lưu ở `data/sensor-variables.json` (còn nguyên khi tắt/mở chương trình). Giá trị tính trực tiếp từ trạng thái cảm biến mỗi lần đọc — không có luồng nền, không giữ bản sao.

API: `GET /api/system/sensors/variables` (danh sách + giá trị), `GET …/variables/{tên}` (kèm phần chọn cảm biến), `POST …/variables` (tạo/sửa, `originalName` = đang sửa), `DELETE …/variables/{tên}`. Nơi dùng sau này (Tags, Modbus…) đọc qua `ISensorVariableService.TryGetValue(tên, out giá trị)`.

### 📊 Tab Dữ liệu của trang Camera — biến MC (dữ liệu liên quan tới camera)

Tab **📊 Dữ liệu** nằm giữa **⚙️ Cấu hình chung** và **🕘 Lịch sử** của trang Camera (`/System/Cameras`). Biến **MC** hoạt động giống biến MS nhưng đếm trên **vị trí đỗ** (vùng nhận diện khoanh trên ảnh camera ở tab 🎯 Vị trí đỗ):

- **Thêm biến**: bấm một lần là thêm ngay một biến, tên tự đặt **MC0, MC1, MC2…** (chỉ số nhỏ nhất còn trống; xoá giữa chừng thì lần thêm sau lấp lại chỗ trống). Biến mới chưa có trạng thái đếm và chưa có vị trí nào; chọn bằng **Sửa** (hoặc bấm đúp dòng). Tối đa 200 biến.
- **Mỗi biến chỉ đếm một trạng thái**: *Còn trống* (ô đỗ trống), *Đã đỗ* (có xe) hoặc *Lỗi* (ví dụ camera mất kết nối). Đã chọn trạng thái và lưu thì ô *Đếm trạng thái* bị vô hiệu hoá, không đổi được (muốn đổi: xoá biến, thêm lại).
- **Chọn vị trí (nhiều camera)**: lọc camera theo **Tầng** (liệt kê **mọi tầng hầm**: các mặt bằng đã khai báo và tầng ghi trên camera, tên hiển thị theo tên mặt bằng), **Zone**, ô **tìm theo IP / hãng** và tuỳ chọn *Chỉ camera đã chọn*; danh sách camera cuộn được, mỗi camera hiện số vị trí đã chọn (`n/tổng`); camera đã vào bãi đỗ nhưng **chưa vẽ vị trí đỗ nào** vẫn hiện (mờ, ghi "chưa có vị trí") để không bị thiếu. Bấm một camera để xem lưới các vị trí đỗ của nó; bấm, kéo chuột, hoặc **bấm một ô rồi giữ Shift + bấm ô khác** để chọn / bỏ cả đoạn. Nút **Chọn tất cả (đang lọc)** / **Bỏ chọn (đang lọc)** áp dụng cho các camera đang hiện (ví dụ lọc Tầng B1 + Zone 2 rồi chọn cả Zone), *Chọn cả camera này* cho riêng camera đang xem. Ô được chọn có màu theo trạng thái đang đếm (đỏ = Đã đỗ, xanh lá = Còn trống, vàng = Lỗi), chú thích màu cùng hàng với ô chọn trạng thái.
- **Xoá vị trí đỗ thì xoá luôn khỏi biến**: khi một vùng nhận diện bị xoá (lưu lại vùng ở tab 🎯 Vị trí đỗ), khi camera bị xoá, hoặc khi camera đổi tầng / Zone làm khoá vị trí thay đổi, các vị trí đó được tự động bỏ khỏi mọi biến MC và lưu lại ngay (kể cả dữ liệu còn sót từ trước khi khởi động chương trình).
- **Giá trị** tính trực tiếp từ trạng thái ô đỗ thật, cập nhật 1 giây/lần chỉ khi đang xem tab này. Vị trí mà vùng nhận diện đã bị xoá khỏi camera thì không được đếm (bảng báo số vị trí này). Biến lưu ở `data/camera-variables.json`.
- Biến MC cũng chọn được trong bảng **biến → thanh ghi** của Modbus Client (cùng với biến MS).

### 🧩 Thiết bị ngoài — Modbus TCP/IP (khai báo)

Menu **Cài đặt & hệ thống › Thiết bị › Thiết bị ngoài** (`/System/External`) có 2 tab:

- **Server**: khai báo các **cổng TCP chương trình sẽ mở** (mặc định 502); PLC / SCADA kết nối tới để đọc / ghi.
- **Client**: khai báo **địa chỉ IP + cổng của thiết bị** mà chương trình sẽ kết nối tới (kèm Unit ID, mặc định 1).

Bấm **Thêm** là thêm ngay một dòng (số thứ tự từ 0, xoá giữa chừng thì dòng mới lấp số nhỏ nhất còn trống), sửa trực tiếp trong bảng rồi bấm **Lưu**. Tối đa 32 dòng mỗi tab. Kiểm tra khi lưu: IP hợp lệ, cổng 1–65535, Unit ID 0–255, không trùng cổng giữa các Server đang bật (và không trùng cổng server Keytop khi đang dùng Keytop), không trùng IP + cổng + Unit ID giữa các Client đang bật. Lưu ở `data/modbus-config.json`.

**Client — gửi biến MS tới thiết bị.** Mỗi dòng Client có thêm **Chu kỳ gửi (ms)** (100 ms – 1 giờ, mặc định 1000) và nút **🔗 Biến** mở bảng **biến (MS / MC) → địa chỉ thanh ghi** của riêng thiết bị đó (nút *Thêm tất cả biến* điền nhanh các biến chưa gán, địa chỉ nối tiếp). Cứ mỗi chu kỳ chương trình ghi giá trị các biến đã gán vào **thanh ghi holding 16 bit không dấu** (giá trị cắt về 0–65535) bằng lệnh Modbus mã 16 (Write Multiple Registers); các địa chỉ liên tiếp được gộp thành một khối (tối đa 123 thanh ghi/lệnh). Địa chỉ tính từ 0 theo giao thức (0 = thanh ghi 40001). Biến chưa tính được (ví dụ khác loại cảm biến đang dùng) thì bỏ qua, không ghi đè. Cột **Trạng thái** (cập nhật 2 giây/lần, chỉ khi đang xem tab Client) cho biết đang kết nối / đã kết nối / lỗi (rê chuột để xem nội dung lỗi).

**Cách chạy (không làm chậm chương trình chính):** `ModbusClientService` là dịch vụ nền; mỗi Client đang bật là một tác vụ async riêng (timeout kết nối và I/O 3 giây, mất kết nối thì thử lại thưa dần tối đa 30 giây). Mỗi chu kỳ chỉ đọc giá trị các biến đã gán bằng **một** lần tính (`GetValues`), không đụng tới camera / AI. Bấm *Lưu* thì các tác vụ tự khởi động lại theo cấu hình mới, không cần khởi động lại chương trình. Phần ghi Modbus tự viết (không cần thư viện ngoài). 
**Server — mở cổng thật.** `ModbusServerService` mở các cổng đã bật (mỗi cổng một tác vụ async riêng, nghe mọi địa chỉ mạng) và trả lời lệnh **đọc thanh ghi (03, 04)** và **ghi thanh ghi (06, 16)** trên bảng 65536 thanh ghi riêng của từng cổng (hiện toàn 0, master ghi vào thì giữ lại; **dữ liệu chương trình đưa ra sẽ gán sau** qua `ModbusServerService.GetBank(id)`); lệnh khác trả mã lỗi 01. Tối đa 16 kết nối mỗi cổng, kết nối im lặng quá 5 phút bị đóng. Bấm *Lưu* là cổng được mở / đóng ngay (bảng thanh ghi giữ nguyên). Cổng bị chương trình khác chiếm hoặc thiếu quyền thì cột *Trạng thái* báo lỗi và tự thử mở lại sau 10 giây. Cần cho phép cổng trong tường lửa Windows; Modbus không có xác thực nên chỉ dùng trong mạng tin cậy.

### 🗑 Xoá kết quả train (tab Train)

Sau khi train xong, tab Train hiện bảng so sánh model cũ / mới và **chi tiết từng biển**. Nút **🙈 Ẩn bảng / 👁 Hiện bảng** (ngay trên bảng) chỉ ẩn / hiện bảng chi tiết từng biển, không xoá gì. Nút **🗑 Xoá thông tin này** (cũng ngay trên bảng) hoặc **🗑 Xoá kết quả train** (hàng nút Train) xoá kết quả lần train gần nhất (`POST api/.../train/clear`, xoá `train-status.json`). **Không** ảnh hưởng model đang dùng, các mẫu đã lưu hay các bản sao lưu model; không xoá được khi đang train. Sau khi xoá, lần train đó không còn nút *Vẫn cài bản mới*.

### 🙈 Ẩn / hiện bảng lớn (trang Camera)

Ba khung có nút **🙈 Ẩn bảng / 👁 Hiện bảng** ở tiêu đề: **Danh sách ảnh trong thư mục** (tab 🧪 Mô phỏng), **📥 Hàng chờ ảnh** và **📋 Mẫu đã lưu** (tab 🧠 Train). Ẩn chỉ là thu gọn (tiêu đề và số đếm vẫn hiện), không xoá dữ liệu; lựa chọn được nhớ theo trình duyệt. Muốn thêm cho khung khác: thêm `data-collapsible="<mã>"` vào `<section class="panel">` (script `panelCollapse.js`).

### 🔎 Tìm nhanh mẫu cần chỉnh sau khi train (tab Train)

- **Từ bảng kết quả train → tới đúng mẫu**: mỗi dòng của bảng *Chi tiết từng biển* có nút **🔎 Tìm mẫu** (hoặc bấm vào ảnh biển): mở khung *Mẫu đã lưu* nếu đang ẩn, bỏ các bộ lọc, tìm theo biển đó và **tô sáng** dòng mẫu (các mẫu cùng biển cũng hiện ra để so sánh). Nút **🖼 Mở ảnh để chỉnh** mở thẳng ảnh gốc trong khu chỉnh khung / ký tự phía trên (khi ảnh còn trong hàng chờ).
- **Chỉ biển còn sai**: ô tích ở dòng tóm tắt của bảng kết quả — ẩn các biển model mới đã đọc đúng, chỉ giữ các biển cần xử lý.
- **Ô tìm kiếm trong Mẫu đã lưu**: gõ biển (đúng hoặc hệ thống đọc ra; không cần gõ dấu `-`, `.`, không phân biệt hoa thường) hoặc một phần tên ảnh; có dòng "Hiện x/y mẫu".
- **Chỉ mẫu cần chỉnh theo kết quả train (n)**: ô lọc xuất hiện khi lần train gần nhất còn biển model mới đọc sai; các mẫu đó cũng có vạch cam bên trái dòng.
- Danh sách mẫu vẫn hiện tối đa 300 dòng một lần — dùng ô tìm kiếm để thu hẹp khi có nhiều mẫu.

### ⚡ Nhận diện nhanh khi ảnh không có xe (tab Mô phỏng)

Trước đây khi model dò xe không thấy xe nào, hệ thống vẫn tìm biển trên **cả khung hình** bằng toàn bộ các bước dự phòng (dò nới lỏng, tìm ứng viên bằng xử lý ảnh, rồi CNN + Tesseract trên từng vùng rác) nên rất lâu và còn giữ khoá đọc biển làm các lần đọc khác phải chờ. Nay:

- Không thấy xe → chỉ chạy **dò nhanh** (`RecognizeQuick`): chỉ dùng bộ dò biển số đã huấn luyện; **không có biển thì trả về ngay**; nếu có biển mới đọc ký tự. Không chạy lần dò nới lỏng và không thử vùng rác. (Nếu máy chưa cài bộ dò biển nào thì dùng xử lý ảnh với tối đa 2 ứng viên.)
- Mọi lần đọc đều có **trần thời gian**: `Anpr:MaxRecognizeMs` (mặc định 8000 ms, đọc bình thường / vùng đã cắt) và `Anpr:QuickMaxMs` (mặc định 2500 ms, dò nhanh). Hết hạn mức thì ngưng thử thêm ứng viên (luôn thử ít nhất 1). Đặt 0 để tắt giới hạn.
- Ảnh chụp sát biển (vùng đã cắt / chế độ "coi là biển") vẫn đọc đầy đủ như cũ.
