# Parking Guidance — Hệ thống Giám sát & Nhận diện Xe (C# / ASP.NET Core)

Ứng dụng web mô phỏng hệ thống hướng dẫn đỗ xe thông minh trong ảnh bạn gửi:
- **Giám sát bãi xe** (`/`): bản đồ ô đỗ theo tầng, đếm số chỗ trống/có xe, nhật ký sự kiện — cập nhật **realtime** qua SignalR.
- **Tìm xe** (`/Search`): kiosk nhập ít nhất 4 ký tự cuối biển số bằng bàn phím số, trả về vị trí xe.

Lõi AI:
- **Nhận diện xe**: **YOLO11** (hoặc YOLOv8 — cùng định dạng ONNX) chạy qua **ONNX Runtime**; có thể **train thêm** ngay trong ứng dụng (tab 🧠 Train › 🚗 Train xe).
- **Đọc biển số (ANPR)**: dò vùng biển số bằng xử lý ảnh cổ điển (OpenCvSharp) rồi đọc chữ bằng **Tesseract OCR**.

> ⚠️ **Lưu ý quan trọng**: Toàn bộ code được viết trong môi trường không có .NET SDK / internet để build và test trực tiếp, nên **chưa được compile thử**. Cấu trúc và logic đã được viết cẩn thận theo đúng API của các thư viện, nhưng khi `dotnet build` lần đầu bạn có thể gặp vài lỗi cú pháp nhỏ cần chỉnh — đây là điều bình thường với 1 dự án có quy mô này. Hãy dán lỗi build lại cho mình nếu gặp, mình sẽ sửa ngay.

## 1. Yêu cầu

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Windows / Linux / macOS (xem ghi chú OpenCvSharp runtime bên dưới)

## 2. Cài đặt

```bash
cd ParkingGuidance
dotnet restore
```

### OpenCvSharp theo hệ điều hành

File `.csproj` mặc định dùng `OpenCvSharp4.runtime.win`. Nếu chạy trên Linux, sửa lại trong
`src/ParkingGuidance.Web/ParkingGuidance.Web.csproj`:

```xml
<!-- Thay dòng runtime.win bằng: -->
<PackageReference Include="OpenCvSharp4.runtime.ubuntu.22.04-x64" Version="4.9.0.20240103" />
```

(hoặc gói runtime phù hợp với bản phân phối Linux bạn dùng — xem NuGet.org "OpenCvSharp4.runtime").

## 3. Chuẩn bị model AI

### 3.1. Model nhận diện xe (YOLOv8 → ONNX)

Dự án cần file `models/yolov8n.onnx`. Nếu chưa có, có thể xuất từ model YOLOv8 pretrained
(dùng máy có Python + internet):

```bash
pip install ultralytics
python -c "from ultralytics import YOLO; YOLO('yolov8n.pt').export(format='onnx')"
```

Lệnh trên tải `yolov8n.pt` rồi xuất ra `yolov8n.onnx`. Copy file này vào
`src/ParkingGuidance.Web/models/yolov8n.onnx`.

Model pretrained trên bộ COCO đã có sẵn 4 lớp xe cần dùng (car, motorcycle, bus, truck) nên
**không cần huấn luyện lại** để chạy demo cơ bản.

### 3.2. Dữ liệu OCR (Tesseract)

Tải `eng.traineddata` tại kho chính thức:
https://github.com/tesseract-ocr/tessdata (hoặc bản `tessdata_fast` để nhẹ hơn)

Đặt vào `src/ParkingGuidance.Web/tessdata/eng.traineddata`.

> Ghi chú độ chính xác: Tesseract với model `eng` là mô hình OCR chữ in tiếng Anh nói chung,
> **không chuyên biệt cho biển số xe** — độ chính xác trên biển số Việt Nam sẽ ở mức trung
> bình, đặc biệt với ảnh góc nghiêng/mờ. Để tăng độ chính xác trong thực tế, cân nhắc:
> - Huấn luyện thêm 1 model YOLOv8 riêng cho lớp "license_plate" để khoanh vùng biển số
>   chính xác hơn thay cho `PlateLocator.cs` (đang dùng xử lý ảnh cổ điển).
> - Dùng bộ traineddata huấn luyện riêng cho font biển số xe, hoặc dịch vụ ANPR chuyên dụng.

### 3.3. (Tuỳ chọn) Video mẫu cho camera

Camera được khai báo ở trang **Hệ thống → Camera** (nguồn video: URL RTSP hoặc đường dẫn file).

> ✅ **Bản này đã đi kèm sẵn** `models/yolov8n.onnx` và `tessdata/eng.traineddata` trong thư mục
> `src/ParkingGuidance.Web/` — không cần tải/xuất thêm. Khi `dotnet build`, 2 thư mục này được
> tự copy sang `bin/Debug/net8.0/`. Các bước 3.1/3.2 bên trên chỉ cần khi muốn thay bằng model khác.

## 4. Chạy dự án

```bash
cd src/ParkingGuidance.Web
dotnet run
```

Mở trình duyệt tại URL được in ra (thường là `https://localhost:5001`), điều hướng:
- `/` — Giám sát bãi xe
- `/Search` — Tìm xe

## 5. Cấu trúc dự án

```
ParkingGuidance/
├── ParkingGuidance.sln
├── README.md
├── tools/                              # Script Python kiểm tra nhanh model/OCR trên 1 ảnh bất kỳ
└── src/ParkingGuidance.Web/
    ├── Program.cs                     # Khởi tạo app, đăng ký dịch vụ
    ├── appsettings.json                # Cấu hình model, camera, OCR
    ├── Models/                         # Vehicle, ParkingSlot, ParkingSession, CameraDevice...
    ├── Services/
    │   ├── Detection/                  # YOLOv8 qua ONNX Runtime (car/bus/truck)
    │   ├── Anpr/                       # Dò vùng biển số + OCR Tesseract + chuẩn hoá biển số VN
    │   ├── Parking/                    # Quản lý trạng thái bãi xe
    │   ├── Layout/                     # Quản lý đối tượng layout (nút ấn, ảnh, text, lô đỗ)
    │   ├── Floor/                      # Quản lý danh sách mặt bằng
    │   ├── Persistence/                # Lưu/đọc dữ liệu ra file JSON (data/)
    │   ├── CameraRegistry/             # Quản lý camera vật lý + dò tìm ONVIF WS-Discovery
    │   ├── Detection/                  #   ZoneUtils (vùng nhận diện), FrameChangeDetector (tối ưu tải)
    │   └── Camera/                     # BackgroundService đọc khung hình định kỳ
    ├── Hubs/MonitoringHub.cs           # SignalR — đẩy cập nhật realtime
    ├── Controllers/                    # API: /api/slots, /api/search, /api/system/cameras
    ├── Pages/                          # Giao diện: Index (Giám sát), Search, System/Cameras
    ├── wwwroot/css, wwwroot/js         # Giao diện dark theme + logic phía client
    │   └── uploads/floors/             # Ảnh nền mặt bằng đã tải lên (ảnh đèn/công tắc bit: uploads/layout/)
    ├── models/                         # yolov8n.onnx (đã có sẵn)
    ├── tessdata/                       # eng.traineddata (đã có sẵn)
    ├── data/                           # Dữ liệu đã lưu (tự tạo, xem mục 9)
    └── samples/                        # (tuỳ chọn) video mẫu cho camera
```

## 6. Cách hoạt động (tóm tắt luồng dữ liệu)

Pipeline nhận diện luôn tách rõ **2 bước** (áp dụng cả ở chức năng Chạy thử nhận diện lẫn dịch vụ
camera nền `CameraIngestionService`):

1. **Bước 1 — Có/không có xe**: `IVehicleDetectionService.Detect()` chạy YOLOv8 trên toàn bộ
   khung hình. Nếu không phát hiện xe nào, dừng lại ngay — **không** chạy OCR, tránh đọc
   nhầm biển số từ nền/vật thể không phải xe.
2. **Bước 2 — Đọc biển số**: chỉ với những xe đã phát hiện được ở bước 1, cắt riêng vùng ảnh
   chứa từng xe (đánh số thứ tự 1..n theo thứ tự trái → phải) rồi gọi
   `IPlateRecognitionService.Recognize()` để đọc biển số cho từng xe.

Luồng camera nền (`CameraIngestionService`) và luồng chạy thử trên giao diện (`test-detect`)
đều dùng chung logic 2 bước này, khác nhau ở chỗ
dịch vụ nền chỉ xử lý 1 xe có độ tin cậy cao nhất (để gán vào ô đỗ tương ứng với camera),
còn API demo trả về **toàn bộ** các xe phát hiện được kèm biển số từng xe.

Chi tiết luồng end-to-end với camera nền:

1. `CameraIngestionService` (chạy nền) đọc khung hình định kỳ từ các camera cấu hình trong
   `appsettings.json` → chạy Bước 1 + Bước 2 ở trên.
2. Kết quả được ghi nhận vào `ParkingService` (cập nhật trạng thái ô đỗ + phiên đỗ xe +
   sự kiện), sau đó phát broadcast qua `MonitoringHub` (SignalR).
3. Trang **Giám sát** (`dashboard.js`) lắng nghe sự kiện SignalR, tự động load lại bản đồ ô
   đỗ và danh sách sự kiện — không cần refresh trang.
4. Trang **Tìm xe** gọi `/api/search?q=...` để tra cứu theo biển số.

## 6b. Phạm vi nhận diện & cách đọc biển số

- **Loại xe nhận diện chọn được** ở Hệ thống › Camera › Cấu hình chung › 🚗 Nhận diện xe: ô tô (`car`), xe buýt (`bus`),
  xe tải (`truck`), xe máy (`motorcycle`) — mỗi loại Có/Không, có hiệu lực ngay cho Mô phỏng, Thử nhận diện và camera thật.
  Mặc định: ô tô/buýt/tải = Có, xe máy theo `Detection:IncludeMotorcycle` (mặc định Không). Tab Train xe luôn gợi ý đủ 4 loại.
- **Toạ độ khung tự nhận biết**: một số file `yolov8n.onnx` xuất ra toạ độ khung **chuẩn hoá 0–1**
  thay vì pixel 0–640 (đúng trường hợp file bạn đã tải). Nếu không xử lý, mọi khung bị co về ~0px
  nên "không thấy xe". `YoloVehicleDetectionService` giờ tự phát hiện và quy đổi cả 2 kiểu.
- **Đọc biển số (ANPR)** gồm 3 tầng:
  1. `PlateLocator` — định vị vùng biển số: Blackhat → gradient ngang → đóng hình thái học → Otsu →
     lọc contour theo tỉ lệ khung. Trả về **tối đa 4 ứng viên** (vùng đầu tiên có thể là lưới tản
     nhiệt xe... nên không tin tuyệt đối vào 1 vùng).
  2. `TesseractPlateRecognitionService` — với mỗi ứng viên thử 3 kiểu tiền xử lý (Otsu, Otsu đảo màu
     cho biển nền tối, ngưỡng thích ứng) rồi OCR 1 dòng, phóng đại thích ứng về ~64px cao.
  3. `VietnamPlateFormatter` — chuẩn hoá theo định dạng biển ô tô VN (`51L15217` → `51L-152.17`),
     sửa nhầm lẫn theo vị trí (chữ `S` ở vị trí số → `5`...). **Chỉ kết quả khớp định dạng** mới được
     nhận (hoặc Tesseract cực kỳ tự tin ≥ 85%) → không trả biển số "rác".
- Giao diện Chạy thử nhận diện vẽ **khung biển số màu xanh + nhãn biển số** trên ảnh như ảnh mẫu.

**Đo bằng bộ ảnh tổng hợp** (`tools/bench/`, xem `tools/bench/README.md` để biết chi tiết và tái
kiểm chứng):

| | Độ chính xác |
|---|---|
| Tổng thể (80 ảnh đa dạng) | **85.0%** (68/80) |
| Biển 2 dòng — thực tế (có khung/tem/ốc vít) | **96.7%** (29/30) |
| Biển 1 dòng | 77.4% |
| Biển vàng/xanh | 84.6% / 92.3% |
| Biển trắng | 83.3% |

**2 lỗi cấu trúc đã tìm và sửa (quan trọng):**

1. **Biển 2 dòng từng thất bại gần hết (30%)** — khi thêm khung viền/tem/ốc vít vào ảnh test cho
   giống biển thật, bước tách ký tự dùng 1 ngưỡng chiều cao tối thiểu CHUNG cho cả ảnh, trong khi
   dòng 2 thường có chữ thấp hơn dòng 1 đáng kể — toàn bộ ký tự dòng 2 bị loại ngay từ đầu. Đã sửa:
   loại khung viền theo ĐỘ ĐẶC (fill ratio) thay vì kích thước, gom theo TỪNG DÒNG trước rồi mới lọc
   chiều cao theo trung vị riêng từng dòng. Kết quả: 30% → 100% trên bộ ảnh phát hiện lỗi.

2. **Biển có seri dạng chữ+số (vd "C1", "Y9") hoặc 2 chữ (vd "AB") bị đọc sai âm thầm** — bộ chuẩn
   hoá cũ ép CẢ 2 ký tự seri phải là chữ cái khi biển dài 9 ký tự, hoặc ép theo đúng 1 kiểu cấu trúc
   duy nhất khi dài 8 ký tự — trong khi biển số VN có NHIỀU cấu trúc seri khác nhau (xem ảnh mẫu
   bạn gửi: "29-AB", "30-Y9", "15-C1"...), và độ dài tổng không đủ để phân biệt chúng. Đã sửa: dùng
   ĐỘ DÀI TỪNG DÒNG THẬT (lấy trực tiếp từ `line_num` của Tesseract, không đoán) để xác định đúng
   cấu trúc, và không còn ép kiểu ký tự thứ 2 của seri (chấp nhận cả chữ lẫn số tại vị trí đó).

Chi tiết đầy đủ + cách tái kiểm chứng trong `tools/bench/README.md`.

**Về ảnh thật độ phân giải thấp**: khi debug với ảnh bạn gửi (ảnh chụp màn hình 1 bài báo), vùng
biển số thực tế chỉ khoảng **36×68 pixel** — ở độ phân giải này, kể cả để Tesseract tự phân vùng
chữ (không qua bước tách ký tự thủ công nào) cũng không đọc được. Đây là giới hạn vật lý của ảnh
đầu vào (quá nhỏ), không phải lỗi thuật toán — camera ANPR thực tế cần đặt đủ gần/đủ zoom để biển
số chiếm tối thiểu ~100-150px chiều rộng trong khung hình.

So với thuật toán ban đầu (chỉ dò 1 vùng, đọc thẳng không tiền xử lý): độ chính xác trên bộ ảnh đa
dạng là ~30%. Các cải tiến chính: dò NHIỀU vùng ứng viên với nhiều bộ tham số (biển dài/vuông ×
chữ tối/sáng), tách riêng từng ký tự theo TỪNG DÒNG để loại nhiễu, tự chỉnh thẳng chữ bị nghiêng
(deskew), bỏ phiếu giữa nhiều lần đọc, chỉ nhận kết quả khớp định dạng biển số VN.

Mã C# là bản port đúng thuật toán này (đã đối chiếu số liệu qua bản Python tương đương) nhưng
**chưa được biên dịch trong môi trường soạn thảo** — nếu kết quả trên web khác với
`tools/anpr_check.py`/`tools/bench/`, hãy gửi lại ảnh + log lỗi build (nếu có).

**Tự kiểm tra 1 ảnh bất kỳ** (không cần chạy web):
```bash
pip install -r tools/requirements.txt      # + cài Tesseract OCR (https://github.com/UB-Mannheim/tesseract/wiki)
python tools/detect_check.py anh.jpg       # chỉ phát hiện xe
python tools/anpr_check.py anh.jpg         # phát hiện xe + đọc biển số
```
Muốn tăng độ chính xác hơn nữa: huấn luyện thêm model YOLO cho lớp `license_plate` để thay
`PlateLocator` — cách hiệu quả nhất với biển nhỏ/mờ/nghiêng.

## 7. Xử lý sự cố "Không nhận diện được xe"

Nếu chức năng Chạy thử nhận diện luôn báo **"Không phát hiện ô tô/xe nào trong ảnh"**, gần như chắc
chắn là do 1 trong các nguyên nhân sau (trang giờ đã tự kiểm tra và hiển thị cảnh báo ngay
trên giao diện khi tải trang, liệt kê chính xác các vị trí đã thử tìm model):

1. **File model không nằm trong thư mục `models/`** — nguyên nhân phổ biến nhất. Ứng dụng
   tìm model theo đường dẫn tương đối `models/yolov8n.onnx` (cấu hình trong
   `Detection:ModelPath`). Nếu bạn đặt file `yolov8n.onnx` **trực tiếp** vào
   `bin\Debug\net8.0` (không có thư mục con `models`), trước đây ứng dụng sẽ **không** tìm
   thấy. Từ bản cập nhật này, dịch vụ nhận diện tự động dò thêm **4 vị trí khả dĩ**:
   - Theo cấu hình, tính từ thư mục làm việc hiện tại
   - Theo cấu hình, tính từ thư mục chứa file thực thi (`bin/Debug/net8.0/...`)
   - **File `yolov8n.onnx` đặt thẳng vào `bin/Debug/net8.0`** (không cần thư mục con `models`)
   - File đặt thẳng vào thư mục làm việc hiện tại

   → Trường hợp bạn vừa gặp (đặt file thẳng vào `bin\Debug\net8.0`) giờ sẽ tự được nhận ra.
   Cách bền vững nhất về lâu dài: đặt file vào `src/ParkingGuidance.Web/models/yolov8n.onnx`
   (thư mục mã nguồn) để mỗi lần `dotnet build`/`dotnet run` tự copy ra output, không bị mất
   khi xoá thư mục `bin`.
2. Model có tồn tại nhưng **export sai định dạng** (không phải ONNX chuẩn YOLOv8, hoặc dùng
   opset không tương thích với `Microsoft.ML.OnnxRuntime`). Kiểm tra log console khi
   `dotnet run` — nếu nạp lỗi sẽ có dòng log rõ ràng kèm thông báo lỗi cụ thể.
3. **Thiếu gói runtime OpenCvSharp** đúng hệ điều hành (xem mục 2) khiến các thao tác xử lý
   ảnh (Mat, VideoCapture...) throw exception ngay khi gọi.
4. Ngưỡng tin cậy (`Detection:ConfidenceThreshold` trong `appsettings.json`, mặc định **0.25**)
   quá cao so với chất lượng ảnh test — thử hạ xuống 0.15-0.2 để kiểm tra.

Xem log khi khởi động: dòng "Da tai model nhan dien xe: ..." cho biết model đã nạp và đường dẫn thực sự tìm thấy.

**Không cần chờ model hoạt động mới test được giao diện**: dùng Layout Editor ở mục 8 bên
dưới để tự chỉnh tay trạng thái ô đỗ (đã đỗ/chưa đỗ/lỗi + biển số + thời điểm) và xem ngay
kết quả trên bản đồ — tách biệt hoàn toàn với pipeline AI.

## 8. Layout Editor — nhiều mặt bằng, ảnh nền, chế độ Build/Chạy

Trang **Giám sát** (`/`) nay hỗ trợ **nhiều mặt bằng** (B1, B2, B3...) và có khu vực
**"Bản đồ tuỳ chỉnh (Layout Editor)"** bên dưới bản đồ mặc định.

### Quản lý mặt bằng
- Thanh **"Mặt bằng"** ở đầu trang: chọn mặt bằng đang xem, **➕ Thêm mặt bằng** (nhập mã +
  tên, ví dụ `B2` / "Tầng hầm B2"), **✏️ Đổi tên**, **🗑️ Xoá mặt bằng** khỏi danh sách chọn (mặc định `B1`
  không xoá được). Mọi phần trên trang (bản đồ ô đỗ mặc định + Layout Editor) đều tự chuyển
  theo mặt bằng đang chọn.
- **Ảnh nền mặt bằng**: nút **"🖼️ Ảnh nền mặt bằng"** trong thanh công cụ Layout Editor cho
  phép chọn 1 ảnh từ máy tính (sơ đồ/bản vẽ mặt bằng thực tế) làm nền cho bản đồ của mặt bằng
  đang chọn — giúp đặt các đối tượng đúng vị trí thực tế. Ảnh được lưu tại
  `wwwroot/uploads/floors/`. Nút **"✕ Bỏ ảnh nền"** để gỡ.

### Thêm & chỉnh đối tượng
- 4 loại đối tượng: **Nút ấn**, **Hình ảnh**, **Text**, **Lô đỗ xe** — thêm bằng nút trên
  thanh công cụ, **kéo-thả** để sắp vị trí. Toạ độ lưu dạng **phần trăm (0-100%)** so với
  khung mặt bằng (không phải pixel tuyệt đối), nên vị trí luôn đúng tỉ lệ dù xem ở khung nhỏ
  (Build) hay phóng to toàn màn hình (Chạy).
- **Thanh công cụ dạng ribbon** (tab "Đồ họa", các nhóm có tên ở dưới: Chỉnh sửa · Hình vẽ · Thành phần · Bãi đỗ · Hiển thị vùng · Ảnh nền · Chế độ; nút lớn icon trên chữ dưới,
  nút nhỏ xếp cột). Nhóm Chỉnh sửa có **Thuộc tính** (mở/đóng hộp thoại, mở rỗng rồi nhấp từng đối tượng cũng được) và **Xoá đối tượng** (xoá đối tượng đang chọn).
- **Hộp thoại thuộc tính nổi** bố cục **ngang** (các trường chia nhiều cột, mặc định nằm giữa sát đáy màn hình; thay cho cột bên phải nên vùng thiết kế rộng hết chiều ngang; ô Zoom nằm cạnh tiêu đề): khi chưa mở, **nháy đúp** vào đối tượng để
  hiện thuộc tính; khi hộp thoại đang mở, chỉ cần **nhấp vào đối tượng khác** là thuộc tính của đối tượng đó thay thế. Kéo thanh tiêu đề để dời hộp thoại (vị trí được nhớ),
  đóng bằng ✕ hoặc Esc (thay đổi chưa Lưu được tự lưu). Nhãn ô trên bản đồ chỉ kèm Zone (`Z2·5`) khi tầng có từ 2 Zone trở lên; tầng chỉ có 1 Zone thì chỉ hiện số vị trí.
- Thuộc tính của đối tượng đang chọn gồm:
  - *Nút ấn*: chỉnh nhãn + hành động chuyển mặt bằng. *Text / Hình ảnh*: chỉnh nội dung/URL ảnh
    (hiển thị tĩnh, chưa gắn hành động).
  - *Lô đỗ xe*: hiển thị trạng thái sống lấy từ `IParkingService` (Chưa đỗ / Đã đỗ / Lỗi).
    "Đã đỗ" có thêm **biển số xe** + **thời điểm đỗ**; "Lỗi" có thêm **mô tả lỗi**. Có thể
    **chỉnh tay** để test giao diện khi model AI chưa chạy được (xem mục 7).
- **Thuộc tính chung của mọi đối tượng** (nhập số trực tiếp trong bảng thuộc tính, ngoài kéo-thả):
  vị trí (X, Y), kích thước (Rộng, Cao) theo %, **màu nền**, **loại đường viền** (liền nét / nét đứt /
  nét chấm / không viền), **màu viền**, **độ dày viền** (px). Để trống độ dày viền = giữ kiểu mặc
  định của loại đối tượng (ví dụ ô đỗ vẫn đổi màu theo trạng thái).
- **Nút ấn có hành động "chuyển đến mặt bằng"**: chọn mặt bằng đích trong bảng thuộc tính của nút.
  Ở chế độ **▶️ Chạy**, bấm nút sẽ chuyển sang mặt bằng đó (con trỏ đổi thành bàn tay). Để có thể
  quay lại, hãy đặt 1 nút "Về B1" trên mặt bằng đích trỏ ngược lại.
- Xoá đối tượng khỏi bản đồ (không xoá dữ liệu ô đỗ thật, chỉ gỡ khỏi layout).

### Chế độ Build / Chạy
- **🛠️ Build** (mặc định): hiển thị đầy đủ thanh công cụ, bảng thuộc tính, có thể kéo-thả và
  chỉnh sửa.
- **▶️ Chạy**: ẩn toàn bộ sidebar, thanh công cụ và bảng thuộc tính; bản đồ **phóng to toàn
  màn hình** (chỉ đọc, không kéo-thả/chỉnh sửa được) — phù hợp để trình chiếu lên màn hình
  lớn/TV trong bãi xe. Bấm **"🛠️ Thoát chế độ chạy"** (góc trên phải) để quay lại Build.

Dữ liệu mặt bằng/layout lưu in-memory qua `IFloorService`/`ILayoutService` (mất khi restart
server) — khi triển khai thật nên chuyển sang cơ sở dữ liệu cùng với `ParkingService`
(xem mục 9).

## 9. Lưu chương trình (không mất dữ liệu khi tắt/khởi động lại)

**Trước đây**: toàn bộ dữ liệu (mặt bằng, layout, ô đỗ, phiên đỗ xe...) chỉ nằm trong bộ nhớ —
tắt server (đóng cửa sổ `dotnet run`, restart máy...) là **mất sạch**, kể cả layout bạn vừa dựng.

**Từ bản này**: mọi thay đổi được **tự động lưu ra file JSON** trong thư mục dữ liệu ngay khi vừa
thao tác xong (thêm/xoá/sửa mặt bằng, layout, ô đỗ...) — không cần bấm nút "Lưu" nào cả. Khi khởi
động lại, chương trình tự đọc lại đúng dữ liệu đó.

```
<thư mục dữ liệu>/
├── floors.json              # danh sách mặt bằng (B1, B2...)
├── layout-elements.json     # nút ấn, hình ảnh, text, lô đỗ xe đã đặt trên từng mặt bằng
├── parking-state.json       # ô đỗ, camera, LED, phiên đỗ xe, sự kiện gần đây
├── camera-registry.json     # danh sách camera, vùng nhận diện, ngưỡng, chế độ, thiết lập giao diện từng camera
└── settings.json            # cài đặt nhận diện chung (tab Hệ thống → Camera → Cấu hình chung)
```

**Thư mục dữ liệu mặc định** là thư mục dữ liệu của người dùng, KHÔNG nằm cạnh file chạy:
Windows `%LOCALAPPDATA%\ParkingGuidance\data` (Linux/macOS: `~/.local/share/ParkingGuidance/data`).
Nhờ vậy cài đặt và danh sách camera **không mất** khi giải nén bản mới sang thư mục khác, clean/rebuild hay publish lại.
Đường dẫn thực tế được ghi ở log khi khởi động và hiển thị ở tab **Cấu hình chung**. Muốn đổi, đặt
`Persistence:DataPath` trong `appsettings.json` (đường dẫn tuyệt đối). Lần đầu chạy, nếu thư mục `data/` cũ cạnh file chạy
(`bin/Debug/net8.0/data/`) còn file thì chúng được **tự sao chép** sang thư mục mới (không ghi đè file đã có).

### Sao lưu / chuyển sang máy khác
Copy 2 thư mục sau (đè lên thư mục tương ứng ở máy đích):
```
<thư mục dữ liệu>/                           # xem ở tab Cấu hình chung
bin/Debug/net8.0/wwwroot/uploads/floors/     # anh nen mat bang da tai len
```

### Muốn xoá dữ liệu, làm lại từ đầu
Dừng server, xoá các file `.json` trong thư mục dữ liệu (xem trên), chạy
lại — chương trình sẽ tự tạo dữ liệu mặc định (mặt bằng B1 trống) như lần đầu.

### Giới hạn của cách lưu này
File JSON phù hợp cho quy mô demo/thử nghiệm (vài mặt bằng, vài trăm ô đỗ). Khi triển khai thật với
lượng dữ liệu lớn, nhiều người dùng cùng sửa layout, hoặc cần lịch sử/báo cáo lâu dài, nên thay
`IPersistenceService`/`ParkingService`/`LayoutService`/`FloorService` bằng **Entity Framework Core**
+ SQL Server/PostgreSQL (kiến trúc đã tách interface sẵn, chỉ cần viết class triển khai mới, không
phải sửa Controller/giao diện).

## 10. Hệ thống — Quản lý Camera (`/System/Cameras`)

Menu **"CÀI ĐẶT & HỆ THỐNG"** có nhóm con **"Hệ thống"** với 4 mục: **Camera** (đã làm), **Cảm
biến**, **LED**, **Thiết bị ngoài** (3 mục sau còn là nút tĩnh, chưa có tính năng).

Trang Camera có 3 tab: **Danh sách** (đầy đủ chức năng bên dưới), **Cấu hình chung** và **Nhật ký**
(2 tab sau còn là chỗ trống, chưa có tính năng).

### Danh mục hãng camera (combobox lọc)
Chọn theo 2 nhóm phổ biến cho hệ thống giám sát bãi xe/ANPR:
- **Phổ biến tại VN** (giá tốt, dễ mua): Hikvision, Dahua, KBVision, CP Plus, Avtech, TVT
- **Quốc tế/phân khúc cao** (ONVIF chuẩn tốt): Axis Communications, Bosch, Panasonic i-PRO,
  Hanwha Vision (Samsung), Uniview (UNV), Vivotek
- **Khác / ONVIF chuẩn**: không lọc theo hãng, hiện mọi camera dò được

### Luồng hoạt động
1. Chọn hãng trong combobox → bấm **"🔄 Quét lại"** (KHÔNG tự động quét) → hệ thống gửi gói dò tìm
   ONVIF WS-Discovery (UDP multicast) trên mạng LAN, liệt kê camera hãng đó tìm được (IP, cổng,
   model, và đã có trong hệ thống hay chưa).
2. Với mỗi camera vừa dò được: bấm **"➕ Thêm"** trên dòng đó, HOẶC dùng nút riêng
   **"➕ Thêm camera (theo IP)"** để nhập tay IP/cổng/hãng — cả 2 cách đều **kiểm tra trùng IP:Cổng**
   trước khi thêm (báo lỗi rõ ràng nếu đã tồn tại).
3. Camera vừa thêm xuất hiện trong bảng **"Camera trong hệ thống"** ở trạng thái **"Nháp"**. Bấm vào
   dòng đó để mở khung chi tiết: gán **tầng hầm**, ghi **chú thích**.
4. Bấm **"➕ Thêm vào hệ thống bãi đỗ"** (yêu cầu đã chọn tầng hầm) → camera chuyển sang trạng thái
   **"✅ Đã vào bãi đỗ"** — từ đây camera được coi là chính thức, dùng cho các thao tác về sau.

### Cách dò tìm hoạt động (ONVIF WS-Discovery) — đọc kỹ trước khi dùng thật
`OnvifDiscoveryService` gửi gói UDP multicast chuẩn WS-Discovery tới `239.255.255.250:3702` và lắng
nghe phản hồi trong ~3 giây — đây là giao thức chuẩn công nghiệp, hầu hết camera IP đời sau ~2012 hỗ
trợ, **không phụ thuộc hãng** (hãng chỉ dùng để lọc kết quả, không đổi cách dò).

### Đã sửa: dò không ra camera dù chắc chắn cùng mạng (lỗi đa card mạng)
Nếu máy chạy server có **nhiều card mạng** (card LAN thật + card ảo VPN/Hyper-V/VMware/Docker/
WSL...) — rất phổ biến trên máy Windows — hệ điều hành có thể gửi gói dò multicast qua **sai card**
(card ảo thay vì card LAN thật nối với camera), khiến dò không ra bất kỳ thiết bị nào dù camera
hoàn toàn bình thường. Đây là lỗi kinh điển của multicast UDP trong .NET, không phải lỗi camera.

Đã sửa: `OnvifDiscoveryService` giờ gửi gói dò qua **TỪNG card mạng đang hoạt động** (không chỉ 1
lần theo route mặc định), nên sẽ tìm ra camera dù nó nằm sau card mạng nào trong số các card của máy.

### Thông tin chẩn đoán khi quét không ra kết quả
Khi "Quét lại" không tìm thấy camera nào, dòng trạng thái giờ nói rõ **đang vướng ở bước nào** thay
vì chỉ báo chung chung "không tìm thấy":
- Không gửi được gói dò qua card mạng nào → kiểm tra kết nối mạng của máy chạy hệ thống.
- Gửi được nhưng **0 phản hồi** trong 3 giây → khả năng cao nhất: (1) máy chạy hệ thống không cùng
  mạng LAN/VLAN vật lý với camera, (2) tường lửa (Windows Firewall...) chặn UDP phản hồi gửi về,
  (3) camera tắt ONVIF/WS-Discovery trong cấu hình.
- Có phản hồi nhưng không khớp hãng đã chọn → thử lại với **"Khác / ONVIF chuẩn"** để xem toàn bộ.

⚠️ **Giới hạn còn lại**:
- Server chạy ứng dụng vẫn phải **cùng mạng LAN/VLAN vật lý** với camera — multicast không đi qua
  router/NAT khác lớp mạng, kể cả sau khi sửa lỗi đa card mạng ở trên.
- Code lắng nghe phản hồi kiểu **unicast** gửi thẳng về (đa số camera hoạt động vậy). Số ít thiết bị
  phản hồi qua multicast tới cổng 3702 cần cách lắng nghe khác (bind cổng 3702 + join multicast
  group) — **chưa triển khai** vì không có phần cứng thật để kiểm thử.
- Nhận diện hãng dựa vào chuỗi "Scopes" camera tự khai báo — không phải camera nào cũng khai đúng
  chuẩn; nếu không nhận ra hãng, chọn **"Khác / ONVIF chuẩn"** để xem toàn bộ camera dò được.
- **Phần dò mạng này không thể kiểm thử trong môi trường soạn thảo** (không có mạng/camera thật) —
  việc sửa lỗi đa card mạng dựa trên nguyên nhân phổ biến đã biết của multicast UDP trong .NET,
  không phải đã xác nhận bằng cách chạy thử. Nếu sau bản sửa này vẫn không dò ra, hãy gửi lại thông
  tin trong thông báo chẩn đoán (số card đã gửi, số phản hồi nhận được) để tiếp tục khoanh vùng.

Dữ liệu camera lưu tại `data/camera-registry.json` (tự động, xem mục 9).

## 11. Tab "Vị trí đỗ" — vùng nhận diện theo từng ô đỗ + tối ưu tải nhiều camera

Trong trang Camera, tab **"🎯 Vị trí đỗ"** giải quyết 2 việc:

### 0. Nguồn ảnh: Camera thật hoặc mô phỏng từ PC
2 nút chọn chế độ ở đầu khu vực vẽ vùng:
- **📁 Chọn từ PC** (mặc định): nút "Chọn ảnh mô phỏng" hoạt động như cũ — chọn 1 ảnh từ máy tính.
- **📷 Lấy từ Camera**: nút "Chọn ảnh mô phỏng" bị **vô hiệu hoá** (đúng yêu cầu: chỉ dùng được ở
  chế độ PC). Thay vào đó hiện: **"📸 Lấy ảnh ngay"** (chụp 1 lần ngay lập tức) và **"▶️ Bắt đầu tự
  động"** — tự lấy lại ảnh theo chu kỳ **thiết lập được** (ô nhập số giây, mặc định 10s). Đây KHÔNG
  phải xem video liên tục — chỉ lấy đúng 1 khung hình mỗi chu kỳ, nhẹ tải hơn nhiều so với stream
  video, đúng tinh thần "không cần liên tục". Yêu cầu camera đã khai báo "Nguồn video" ở tab Danh sách.

### 1. Vẽ vùng nhận diện (mỗi vùng = 1 ô đỗ cụ thể)
- Chọn 1 camera **đã "Thêm vào hệ thống bãi đỗ"** (tab Danh sách).
- **Chọn ảnh mô phỏng** từ máy tính (do chưa có camera thật) — dùng đúng cơ chế "chọn ảnh trên máy
  tính" đã có ở chức năng Chạy thử nhận diện.
- **➕ Vẽ vùng mới**: bấm nút, sau đó **nhấp chuột tại điểm đầu** của khu vực trên ảnh, **kéo tới
  điểm cuối** rồi thả chuột — hệ thống tự tạo vùng hình chữ nhật từ 2 điểm đó, sau đó nhập mã ô đỗ
  tương ứng (vd `B1-005`) — ô đỗ chưa tồn tại sẽ tự được tạo.
- Mỗi vùng còn hiện **2 hàng "Điểm đầu"/"Điểm cuối" (X/Y %) với number up/down** ngay bên dưới để
  tinh chỉnh lại toạ độ chính xác mà không cần kéo chuột lại — đúng theo yêu cầu "dùng chuột hoặc
  number up down".
- Có thể vẽ **nhiều vùng trên cùng 1 ảnh** (nhiều ô đỗ trong cùng khung hình camera).
- **💾 Lưu vùng nhận diện** để lưu lại (tự động lưu ra `data/camera-registry.json`).

### 2. Lõi AI dùng vùng để nhận diện (đã nối thật, kiểm chứng được thuật toán)
- **🔍 Chạy thử nhận diện**: chạy `YOLOv8` trên ảnh mô phỏng, với MỖI xe phát hiện được, tính
  **tỉ lệ diện tích xe nằm trong từng vùng** (lấy mẫu lưới điểm 10x10 + kiểm tra điểm trong đa giác
  — thuật toán ray-casting chuẩn). Xe chỉ được gán vào 1 ô đỗ khi tỉ lệ che phủ **≥ 50%** (phần lớn
  diện tích xe nằm trong vùng) — xe đi ngang qua hoặc chỉ ló 1 góc vào vùng sẽ **không** được tính.
  Ảnh kết quả vẽ khung xanh (khớp ô đỗ, kèm % che phủ + biển số) hoặc khung xám (không thuộc ô nào).
- Thuật toán tính tỉ lệ che phủ đã được **kiểm chứng bằng bộ test độc lập** (hình chữ nhật thẳng,
  tràn biên, và cả **vùng nghiêng mô phỏng góc camera**) trước khi viết bản C# — sai số dưới 3-6%
  tuỳ độ mịn lưới lấy mẫu, xem `ZoneUtils.cs`.
- Cùng logic này được dùng trong dịch vụ camera nền (`CameraIngestionService`) khi camera đã có
  "Nguồn video" (tab Danh sách) — thay vì kiểu cũ chỉ đoán 1 xe/1 ô mỗi khung hình, giờ xử lý
  **đúng nhiều ô đỗ cùng lúc** trong 1 khung hình.

### 3. Chế độ nhận diện: Tự động / Thủ công
Mỗi camera có 2 chế độ, chọn bằng 2 nút trong tab Vị trí đỗ:
- **🔁 Tự động**: chạy trong vòng lặp nền (`CameraIngestionService`), áp dụng tối ưu tải ở mục 4
  bên dưới.
- **✋ Thủ công**: KHÔNG chạy trong vòng lặp nền — camera này bị bỏ qua hoàn toàn, chỉ nhận diện khi
  bấm nút **"▶️ Nhận diện ngay"** (mở "Nguồn video" của camera, đọc đúng 1 khung hình hiện tại, chạy
  AI + khớp vùng, **ghi nhận thật** vào hệ thống — khác với "Chạy thử nhận diện" ở mục 2 chỉ để xem
  trước trên ảnh mô phỏng, không ghi nhận). Phù hợp khi muốn kiểm soát thời điểm chạy AI thay vì để
  chạy liên tục.

Cả 2 chế độ dùng chung 1 lõi xử lý (`ICameraFrameProcessor`) — không có sự khác biệt về độ chính
xác hay logic khớp vùng, chỉ khác ở việc CÓ chạy tự động theo vòng lặp hay không.

### 4. Tối ưu tải khi có nhiều camera (độ sai khác hình ảnh, thiết lập được)
Đúng như bạn lo ngại — chạy YOLOv8 + OCR cho MỌI khung hình của MỌI camera sẽ rất tốn CPU/GPU khi có
nhiều camera. `FrameChangeDetector` giải quyết việc này:
- Trước khi chạy AI, so khung hình hiện tại với khung hình gần nhất **đã xử lý** của cùng camera
  (thu nhỏ về 48×48 mức xám, tính sai khác tuyệt đối trung bình, quy về thang %).
- Chỉ chạy AI khi % sai khác **≥ ngưỡng thiết lập** — bãi xe phần lớn thời gian gần như tĩnh, nên
  đa số khung hình được bỏ qua an toàn, giảm đáng kể tải khi số camera tăng lên.
- **Ngưỡng có thể thiết lập**: ô "Ngưỡng thay đổi ảnh (%)" ngay trong tab Vị trí đỗ (lưu riêng theo
  từng camera), hoặc mặc định toàn hệ thống qua `Detection:DefaultMotionThresholdPercent` trong
  `appsettings.json` (mặc định 4%). Đặt `0` = tắt tối ưu, luôn chạy AI mỗi khung hình.

### 5. Tự động nhận diện khi ảnh mới thay đổi + timeout theo từng camera
- **Giao diện (tab Vị trí đỗ)**: ô chọn **"Tự động nhận diện khi ảnh mới thay đổi vượt ngưỡng"** (mặc định bật). Mỗi
  khi có ảnh MỚI — dù lấy từ **Camera** ("Lấy ảnh ngay"/tự động theo chu kỳ) hay chọn từ **PC** — giao diện gọi
  `POST api/system/cameras/{id}/test-detect?onlyIfChanged=true`. Server so ảnh với ảnh tham chiếu gần nhất của camera
  đó (cùng `FrameChangeDetector`, cùng ngưỡng riêng của camera hoặc `DefaultMotionThresholdPercent`); chỉ khi
  **% thay đổi ≥ ngưỡng** mới chạy YOLO + OCR, ngược lại trả `skipped: true` kèm % thay đổi. Ảnh đầu tiên luôn được nhận diện.
- **Vòng lặp nền**: xem mục 8 (mỗi camera chạy độc lập, AI xử lý qua hàng đợi).
- **Ảnh tham chiếu giữ nguyên khi chưa vượt ngưỡng**: ảnh mới thay đổi nhỏ so với ảnh tham chiếu sẽ bị bỏ qua và ảnh tham chiếu vẫn là ảnh cũ (thay đổi nhỏ không bị cộng dồn). Ảnh tham chiếu chỉ đổi sang ảnh mới **sau khi nhận diện thành công** (`CommitReference`); nếu nhận diện lỗi thì giữ tham chiếu cũ để thử lại ở khung kế tiếp.
- `snapshot` và `detect-now` cũng dùng timeout này (trả HTTP 504 kèm thông báo khi camera không phản hồi).

### 6. Kiểm tra lại đọc biển số (ANPR) — các lỗi đã sửa
- **Tìm xe không ra xe do camera nhận diện**: biển OCR được lưu dạng `51K-163.27` (có `-` và `.`) nhưng ô tìm kiếm so khớp
  chuỗi thô, nên gõ `6327` hay `51K16327` không tìm thấy. Nay `Search` so khớp trên chuỗi đã bỏ dấu ở cả 2 phía.
- **Mất/đứt phiên đỗ khi OCR đọc hụt hoặc sai 1 ký tự**: trước đây lần đọc không ra biển ghi đè thành `UNKNOWN` và đóng
  phiên; lần đọc sai 1 ký tự mở phiên mới. Nay: đọc hụt thì giữ biển đã biết; phiên `UNKNOWN` được bổ sung biển khi đọc được
  (không tạo phiên mới); biển lệch ≤1 ký tự (cùng độ dài) coi là cùng xe và chỉ đổi khi cách đọc mới tin cậy hơn
  (`ParkingSession.PlateConfidence`); biển khác hẳn mới tách phiên.
- **Kết quả không ổn định giữa các lần chạy**: khi 2 cách phân tách biển cùng số lần sửa (vd `30Y96850` có thể là
  `30Y-968.50` hoặc `30Y9-6850`), `List.Sort` không đảm bảo thứ tự ổn định. Nay dùng sắp xếp ổn định, ưu tiên biển 5 số.
- **Chưa kiểm chứng được trên ảnh thật**: bộ dò vùng biển (`PlateLocator`) trên ảnh mô phỏng của mình đôi khi chỉ trả về
  từng dòng của biển 2 dòng thay vì cả tấm biển; chưa có ảnh camera thật để xác định đây là lỗi thật hay do ảnh mô phỏng, nên
  chưa sửa thuật toán này. Nếu bạn gửi vài khung hình/ảnh crop biển thật, có thể chỉnh và đo lại.

### 7. Tăng tốc khởi động; bỏ trang Demo Camera AI và webcam
- Đã **xoá trang Demo Camera AI** (`/CameraLive`, `cameraLive.js`, `api/detection/*`) và **bỏ webcam**. `appsettings.json` mục `Cameras`
  giờ để trống (`[]`): trước đây 3 camera video mẫu (`samples/*.mp4`, không tồn tại) + webcam `"0"` được mở tuần tự ngay lúc khởi động, mỗi
  camera lỗi chờ tới 8 giây nên khởi động rất lâu và log đầy cảnh báo. Camera thật khai báo ở trang **Hệ thống → Camera**.
- Dịch vụ nền camera trả quyền điều khiển cho host ngay (`await Task.Yield()`), không còn chặn khởi động web.
- Model YOLO + Tesseract được nạp sẵn ở luồng nền (`AiWarmupService`), nên ảnh/khung hình đầu tiên không phải chờ nạp model.

### 8. Lưu cài đặt + hàng đợi AI chạy nền (không cần mở trang camera)
- **Lưu lại**: cài đặt chung (chu kỳ lấy ảnh, ngưỡng thay đổi ảnh mặc định, độ tin cậy YOLO, timeout, backoff, cảnh báo quá tải)
  nằm ở tab **Cấu hình chung**, lưu vào `settings.json`, có hiệu lực ngay (không cần khởi động lại); lần chạy đầu lấy mặc định từ
  `appsettings.json` mục `Detection`. Danh sách camera + vùng + ngưỡng + chế độ vẫn lưu ở `camera-registry.json`; thiết lập giao diện
  từng camera (nguồn ảnh Camera/PC, chu kỳ tự lấy ảnh, bật tự lấy ảnh, ô tự nhận diện) cũng được lưu và khôi phục khi chọn lại camera.
  **Thư mục dữ liệu mặc định đã đổi** sang thư mục người dùng (xem mục 9) — đây là chỗ khiến dữ liệu "mất" khi giải nén bản mới.
- **Mỗi camera đủ điều kiện luôn chạy ngầm, độc lập** (`CameraIngestionService` → 1 công nhân/camera): đã "Thêm vào hệ thống bãi đỗ" +
  có Nguồn video + chế độ Tự động + đã vẽ ít nhất 1 vùng gắn ô đỗ. Không cần vào trang camera. Công nhân tự mở kết nối (có timeout),
  lấy khung **mới nhất** (bỏ khung dồn trong bộ đệm RTSP), so với ảnh tham chiếu; chỉ khi vượt ngưỡng mới xếp hàng AI. Camera lỗi/treo
  chỉ tự nghỉ theo backoff riêng, không làm chậm camera khác. Danh sách được so khớp lại mỗi 2 giây (thêm/sửa/xoá có hiệu lực ngay).
- **Hàng đợi AI** (`AiJobScheduler`): 1 luồng AI riêng xử lý lần lượt từng việc nên việc nặng (YOLO + OCR) không chặn việc lấy ảnh và không
  quá tải CPU. Thứ tự: việc người dùng đang chờ (nút "Nhận diện ngay", "Chạy thử nhận diện") **ưu tiên trước**; các camera nền xếp theo
  thứ tự đến trước làm trước; mỗi camera chỉ có tối đa **1 việc đang chờ** — khung mới hơn thay khung cũ chưa kịp xử lý (giữ nguyên lượt
  xếp hàng), nên không dồn ảnh cũ và camera đông tải không chiếm lượt của camera khác. Quá `QueueWarnSeconds` sẽ ghi cảnh báo quá tải.
- Theo dõi: tab **Cấu hình chung** hiển thị việc đang chạy, số việc chờ, và trạng thái từng camera (làm mới 3 giây/lần);
  API: `GET api/system/queue`, `GET/PUT api/system/settings`, `PUT api/system/cameras/{id}/ui-settings`.
- Đã bỏ mục `Cameras` (camera video mẫu kiểu cũ) khỏi `appsettings.json` và mã nguồn.

### 9. Mỗi vùng nhận diện tối đa 1 xe
- **Quy tắc**: 1 vùng chỉ nhận 1 xe, 1 xe chỉ thuộc 1 vùng ⇒ số xe nhận được trên 1 ảnh **không bao giờ vượt quá số vùng**. Áp dụng ở cả vòng
  lặp nền, "Nhận diện ngay" và "Chạy thử nhận diện" (hàm `ZoneUtils.AssignVehiclesToZones`).
- **Cách chọn**: xe vẫn phải nằm trong vùng từ 50% diện tích khung xe trở lên (xe chỉ có một phần nhỏ trong vùng, như xe trắng ở xa trong ảnh mẫu,
  không bao giờ được xét). Trong các xe đủ điều kiện, xe có **diện tích nằm trong vùng lớn nhất** (độ phủ × diện tích khung xe) thắng; xe nhỏ/xa/bị
  che cùng lọt trong vùng (xe hồng ở ảnh mẫu) bị bỏ qua hoàn toàn và **không đọc biển số** (tiết kiệm OCR). Khi có nhiều vùng, chọn tham lam theo điểm
  cao nhất trước.
- **Chạy thử nhận diện** hiển thị xe bị loại với nhãn "ngoài ô đỗ" hoặc "vùng đã có xe khác" để biết vì sao.
- Lưu ý: xe rất lớn chỉ lướt qua mép vùng (độ phủ ≥ 50%) có thể thắng 1 xe nhỏ đang đỗ trong vùng; nếu gặp, thu hẹp vùng cho vừa 1 xe đỗ.

### 10. Vùng nhận diện = ô đỗ: tầng hầm → Zone → vị trí, số thứ tự không trùng, trạng thái, và thêm vào bản đồ tuỳ chỉnh
- **Mỗi camera thuộc 1 tầng hầm VÀ 1 Zone** (khu vực lắp đặt trong tầng) — chọn ở Hệ thống → Camera → tab Danh sách (chi tiết camera). **Zone là số tự nhiên
  nguyên dương** (`1`, `2`, `3`...), mỗi tầng có các Zone riêng (tầng B1 có Zone 1, tầng B2 cũng có Zone 1); nhiều camera được đặt chung 1 Zone. Có tầng thì
  bắt buộc có Zone (HTTP 400 nếu thiếu/sai định dạng) và camera phải có đủ tầng + Zone mới "Thêm vào hệ thống bãi đỗ" được.
- **Số thứ tự vị trí nhận diện phải là số tự nhiên nguyên dương** (`1`, `2`, `3`...). Chữ hoặc ký hiệu như `B1`, `A-2`, `1.5`, `-3`, `0` đều **không hợp lệ**
  (số có số 0 đứng đầu như `007` được chuẩn hoá thành `7`; tối đa 6 chữ số). Kiểm tra ở cả giao diện (báo ngay khi nhập/lưu) lẫn máy chủ (HTTP 400).
- **Không trùng trong 1 Zone**: giữa các vị trí của cùng camera và giữa các camera cùng tầng + cùng Zone (HTTP 409). Zone khác hoặc tầng khác **được dùng lại cùng số**
  (B1/Zone 1 có vị trí 1, B1/Zone 2 cũng có vị trí 1). Khi vẽ vị trí mới hệ thống gợi ý số nhỏ nhất còn trống của Zone. Khoá ô nội bộ là `{tầng}-{zone}-{số}`
  (vd `B1-2-5` = tầng B1, Zone 2, vị trí 5) — chỉ để các tầng/Zone không đè lên nhau. Trên bản đồ tuỳ chỉnh mỗi ô hiện nhãn `Z2·5` (Zone 2, vị trí 5).
- **Chuyển dữ liệu cũ tự động khi khởi động**: camera cũ đã gán tầng nhưng chưa có Zone được xếp vào **Zone 1** (dữ liệu cũ vốn không trùng số trong cả tầng nên xếp chung
  1 Zone vẫn không trùng); vị trí có số thứ tự kiểu cũ (`B1-001`...) → lấy dãy số ở cuối (`1`); trùng hoặc không có số thì cấp số nhỏ nhất còn trống của Zone.
  Ô đỗ, phiên đỗ, lịch sử, sự kiện và vùng đã đặt trên bản đồ tuỳ chỉnh được đổi sang khoá mới (vị trí `1` ở tầng B1 → `B1-1-1`), không mất dữ liệu.
  Đổi tầng **hoặc Zone** của camera đã vẽ vị trí: máy chủ chặn nếu số bị trùng ở Zone mới, ô ở nơi cũ về "Còn trống" (đối tượng đã đặt trên bản đồ tuỳ chỉnh
  vẫn mang khoá cũ — nếu cần hãy xoá và thêm lại bằng "Thêm vùng nhận diện").
- **Thuộc tính mỗi vùng/ô đỗ**: trạng thái bằng **màu** (đỏ = có xe đỗ, vàng = lỗi camera, xanh = còn trống) và bằng **chữ** (Đã đỗ / Lỗi / Còn trống),
  **biển số xe đang đỗ**, **thời gian xe đó vào**, **biển xe gần nhất đã ra khỏi vị trí** và **thời gian ra** (`ParkingSlot.LastExitPlate/LastExitTime`).
- **Xe ra khỏi vị trí**: khi vùng không còn xe, sau `VacancyConfirmFrames` lần xử lý liên tiếp (mặc định 3, chỉnh ở tab Cấu hình chung) thì xác nhận xe đã rời:
  đóng phiên đỗ, ô về "Còn trống", lưu biển + giờ ra. Trong lúc chờ xác nhận, camera được xử lý mỗi chu kỳ dù ảnh không đổi (vùng trống là ảnh tĩnh nên
  sẽ không vượt ngưỡng chuyển động). Xe khác biển đỗ vào ô cũ cũng ghi xe trước là "xe ra gần nhất". Nếu model AI chưa nạp được thì **không** kết luận xe đã rời.
- **Lỗi camera → màu vàng**: camera mở/đọc lỗi liên tiếp 2 lần thì các ô của camera đó chuyển "Lỗi" (giữ nguyên phiên đỗ đang mở); camera hoạt động lại thì
  ô tự về đúng trạng thái trước đó (Đã đỗ/Còn trống). Xoá vùng/xoá camera thì ô tương ứng được đưa về "Còn trống".
- **Bản đồ tuỳ chỉnh (Layout Editor)**: nút **➕ Vùng nhận diện** mở danh sách các vùng đã vẽ của camera trên tầng hiện tại (chọn nhiều, mỗi vùng chỉ đặt 1 lần),
  tự xếp lưới. Ở chế độ **Build** có thể **bật/ẩn từng thuộc tính** (màu trạng thái, chữ trạng thái, biển số đang đỗ, giờ vào, biển xe ra gần nhất, giờ ra):
  thanh "Hiển thị trên vùng nhận diện (cả tầng)" áp dụng cho tất cả vùng của tầng, hoặc chỉnh riêng từng vùng trong bảng thuộc tính. Các thiết lập này được lưu.
  Bản đồ cập nhật realtime (SignalR `ParkingEvent`/`SlotsChanged`) và tự làm mới 5 giây/lần, kể cả ở chế độ Chạy.
- **Dọn dữ liệu demo cũ**: bản này không còn tạo 7 ô demo `B1-001..007` + camera mẫu với biển số giả (chúng làm ô hiện "đã đỗ" sai). Dữ liệu demo cũ còn nguyên vẹn
  trong file đã lưu sẽ được xoá khi khởi động (ô đã bị sửa tay thì giữ lại). Ô đỗ giờ sinh ra từ các vùng nhận diện bạn vẽ.

### 11. Kết quả nhận diện ở giao diện cập nhật bản đồ
- Trước đây "Chạy thử nhận diện" và tự nhận diện ảnh từ PC chỉ **hiển thị** kết quả (vd vùng 1 tầng B1 đọc được `30G-567.89`) mà không ghi vào bãi đỗ, nên bản đồ
  tuỳ chỉnh vẫn "Còn trống". Nay có ô **"Ghi nhận kết quả vào bãi đỗ"** (mặc định bật, được lưu theo từng camera) ở tab Vị trí đỗ: khi bật, kết quả
  của ảnh từ PC / lấy tay từ camera được ghi vào ô đỗ → bản đồ và trang giám sát cập nhật ngay (SignalR). Vùng nào trong ảnh không còn xe thì xác nhận xe đã rời **ngay**
  (vì ảnh do người dùng chủ động đưa vào). Bỏ chọn ô này để chỉ chạy thử, không đổi dữ liệu. Camera chạy nền vẫn tự ghi nhận như cũ.

### 12. Trang Giám sát gọn hơn: thống kê chỗ đặt trên layout, khung Sự kiện ẩn/hiện
- **Đã bỏ** khung "Bản đồ tầng B1" (lưới ô đỗ đơn giản) và thẻ "Số chỗ trên tầng" ở đầu trang — bản đồ tuỳ chỉnh (Layout Editor) là nơi duy nhất hiển thị ô đỗ.
- **Đối tượng mới "Thống kê chỗ"** (nút **➕ Thống kê chỗ** ở thanh công cụ Layout Editor, kéo-thả/đổi cỡ/đổi màu như các đối tượng khác): hiển thị
  **số chỗ Trống**, **số chỗ Có xe** và **số Lỗi không lấy được ảnh** (kèm số camera đang mất ảnh) của tầng đang xem, cập nhật realtime. Trong bảng thuộc tính
  chọn bật/ẩn từng con số. Số liệu tính trên các **vùng nhận diện đang có** của camera trên tầng (ô của vùng đã xoá không được đếm); "Lỗi ảnh" = ô ở trạng thái Lỗi
  do camera mở/đọc lỗi liên tiếp (màu vàng). API: `GET api/layout/stats?floor=`. Có thể đặt nhiều đối tượng thống kê, kể cả ở nhiều tầng.
- **Khung "Sự kiện gần đây" ẩn/hiện**: nút **📋 Ẩn sự kiện / Hiện sự kiện** ở đầu trang (hoặc nút ✕ trong khung) — khi ẩn, bản đồ tuỳ chỉnh chiếm toàn bộ chiều rộng.
  Lựa chọn được nhớ trên trình duyệt (localStorage); khi đang ẩn, nút hiện số sự kiện mới chưa xem. Chế độ **Chạy** (toàn màn hình) luôn ẩn khung này.
- Danh sách sự kiện dễ đọc hơn: hiện "Xe vào / Xe ra", "Vị trí 1 · tầng B1", biển số và **địa chỉ IP camera** thay cho mã GUID.

### 13. Đối tượng mới "Đèn / Công tắc bit" trên Layout Editor (kiểu Bit Lamp / Toggle Switch)
Nút **Đèn / Công tắc bit** (nhóm **Điều khiển** trên thanh công cụ). Nháy đúp vào đối tượng để mở thuộc tính (4 tab):
- **General**: *Comment*; chọn **Bit Lamp** (chỉ hiển thị) hoặc **Toggle Switch** (bấm ở chế độ Chạy để ghi); **tag bit** lấy từ *System tags* kiểu Boolean
  (`BlinkSlow`, `BlinkFast` — chỉ đọc) và *Project tags* kiểu Boolean (đọc/ghi); tuỳ chọn *Đọc và ghi dùng tag khác nhau*; *Đảo tín hiệu*;
  *Chỉ ghi khi nhả nút*; **Kiểu công tắc**: Set ON, Set OFF, Toggle, Momentary (giữ = ON, nhả = OFF).
- **Shape**: ảnh cho **trạng thái 0** và **trạng thái 1** (tải lên PNG/JPG/GIF/WEBP/BMP ≤ 5 MB, hoặc dán URL). Không chọn ảnh thì vẽ hình mặc định (đèn tròn / công tắc gạt).
- **Label**: chữ hiển thị theo từng trạng thái, màu và cỡ chữ.
- **Vị trí**: vị trí/kích thước, màu nền/viền, xoay/độ mờ/lớp như các đối tượng khác.
Trạng thái cập nhật khoảng 5 lần/giây (cả ở Build lẫn Chạy). Công tắc chỉ ghi được vào **Project tag Boolean** (System tag chỉ đọc); ghi thất bại thì đối tượng viền đỏ ngắn.
Momentary tự nhả về OFF khi thả chuột, mất tiêu điểm, đổi tab, thoát chế độ Chạy (ESC) hoặc đóng trang. Các lệnh ghi được xếp hàng tuần tự nên lệnh nhả không bao giờ tới trước lệnh nhấn.
API: `GET api/tasks/tags/bits` (danh sách tag bit), `GET api/tasks/tags/bit-values?names=A,B`, `POST api/layout/upload-image` (ảnh lưu ở `wwwroot/uploads/layout/`);
ghi giá trị dùng sẵn `PUT api/tasks/tags/project/{name}/value`. Chưa có: tab Security và Macro (chưa có hệ thống phân quyền / macro).

### 14. Quy trình nhận diện biển số chính xác hơn (bỏ phiếu theo thời gian + model ONNX tuỳ chọn)
Lỗi gốc (xem phân tích): mỗi khung hình được đọc độc lập bằng Tesseract (hay sai 1–2 ký tự hoặc rơi 2 ký tự cuối với biển 1 dòng), khung ĐẦU TIÊN (xe còn đang chạy vào ô)
lại quyết định biển của cả phiên, và 1 lần đọc lệch ≥ 2 ký tự làm sinh "xe ra/xe vào" ảo. Đã sửa:

**Luôn bật (không cần model mới)**
- **Chỉ đọc biển khi xe đã đứng yên**: khung xe ở ô phải gần như không đổi (IoU ≥ 0,80) so với lần xử lý trước (`CameraFrameProcessor`).
- **Bỏ phiếu giữa nhiều khung** (`PlateConsensus.cs`, `PlateTracker`): biển nào được ≥ 2 lần đọc giống hệt thì thắng; nếu chưa có thì bỏ phiếu **từng vị trí ký tự**
  giữa ≥ 3 lần đọc lệch ≤ 2 ký tự (chỉ nhận nếu đúng cấu trúc biển VN). Biển của phiên chỉ đổi khi biển mới hơn ≥ ~1 lần đọc.
- **Chỉ tách phiên (xe khác) khi biển lạ lặp lại 2 lần liên tiếp**. Nếu biển đầu tiên (chỉ 1 lần đọc, phiên < 90 giây) bị đọc sai thì **sửa ngay trong cùng phiên**, không tạo phiên giả.
  Ảnh nạp tay/"Chạy thử nhận diện" (chỉ 1 lần) vẫn đổi xe ngay.
- **Biển đã chốt (≥ 3 lần đọc giống nhau) thì bỏ qua OCR**, chỉ kiểm tra lại mỗi 10 khung → ít tải hơn và không bị đọc sai làm đổi biển.
  Khi biển CHƯA chốt, camera được xử lý tiếp (tối đa 12 khung/phiên) dù ảnh không đổi, để gom đủ lần đọc.
- **"Đã xác thực"** = ≥ 2 lần đọc giống nhau (hoặc 1 lần tin cậy > 0,85), thay vì chỉ dựa độ tin cậy Tesseract (không chuẩn).
- Chấm điểm theo **mã tỉnh** (mã lạ → trọng số 0,4, không loại hẳn) và **CLAHE** cho ảnh biển ít tương phản (ngược sáng/ban đêm).

**Tuỳ chọn: model ONNX (chính xác hơn nhiều, KHÔNG kèm sẵn trong gói — cần tự chuẩn bị file)**
- `models/plate_detector.onnx`: YOLO 1 lớp "biển số" (đầu ra `[1, 4+số_lớp, N]` hoặc `[1, N, 6]`), chạy trên ảnh xe đã cắt. Thay `PlateLocator`; không thấy biển thì tự dùng lại `PlateLocator`.
- `models/plate_ocr.onnx`: model đọc nguyên dòng kiểu CRNN + CTC. Biển 2 dòng được tách đôi rồi đọc từng dòng. Khai báo bộ ký tự/chuẩn hoá ở mục `Anpr` trong `appsettings.json`
  (`OcrCharset` hoặc `OcrCharsetFile`, `OcrBlankLast`, `OcrNormalize`). Kích thước ảnh vào đọc từ model.
- Có file thì tự bật (xem log khi khởi động); lỗi khi chạy thì tự quay về Tesseract. Hai model này **chưa được kiểm thử với model thật** — hãy thử trên ảnh thực của bạn và chỉnh `OcrCharset`/`OcrBlankLast`/`OcrNormalize` cho khớp model.

**Đo lỗi ở bước nào**: đặt `Anpr:DebugDir` (vd `"debug-anpr"`) → lưu ảnh xe kèm khung biển đã chọn mỗi khi đọc sai/không đọc được/độ tin cậy thấp
(`Anpr:DebugAll: true` để lưu mọi lần). Tên file có biển đọc được và độ tin cậy; thư mục tự giữ ≤ ~600 ảnh.

**Còn chưa làm**: nắn phối cảnh biển nghiêng; giữ nguyên thông số camera/độ phân giải — biển cần rộng ≥ ~100–150 px trong ảnh gốc, nếu nhỏ hơn thì model nào cũng khó đọc.

### Giới hạn cần biết
- Cần điền **"Nguồn video"** (URL RTSP/file video) cho camera ở tab Danh sách thì dịch vụ nền mới thực
  sự đọc được video liên tục — dò ONVIF hiện chỉ tìm ra được địa chỉ IP, chưa tự lấy URL luồng RTSP
  (khác nhau tuỳ hãng, cần thêm bước gọi ONVIF Media Service).
- Nút "🔍 Chạy thử nhận diện" hoạt động độc lập, không phụ thuộc ngưỡng tối ưu tải (vì đó là thao
  tác thủ công 1 lần, không phải vòng lặp liên tục) — ngưỡng chỉ có tác dụng khi camera chạy nền.
- Toàn bộ phần này (vẽ vùng, tính tỉ lệ che phủ, tối ưu tải) đã kiểm chứng thuật toán qua Python độc
  lập nhưng **mã C# chưa được biên dịch/chạy thật** — nếu build lỗi, gửi lại log để mình sửa.

## 12. Hướng phát triển tiếp theo

- Nâng cấp từ file JSON (mục 9) lên **Entity Framework Core** + SQL Server/PostgreSQL khi cần lưu
  trữ quy mô lớn, nhiều người dùng sửa đồng thời, hoặc báo cáo/lịch sử lâu dài.
- Kiểm thử `OnvifDiscoveryService` (mục 10) với camera thật, xử lý thêm trường hợp phản hồi qua
  multicast.
- Tự động lấy URL luồng RTSP qua ONVIF Media Service thay vì phải nhập tay "Nguồn video" (mục 11).
- Tối ưu `FrameChangeDetector` hơn nữa: chỉ so khác trong vùng nhận diện đã vẽ thay vì toàn khung
  hình (giảm tải hơn nữa với camera góc rộng nhưng chỉ vài ô đỗ nhỏ trong khung hình).
- Xây dựng các mục còn để trống: LED (điều khiển thật), Thiết bị ngoài, tab Cấu hình
  chung/Nhật ký của Camera, trang Báo cáo, Danh mục, Import, Kiosk & đường đi, Người dùng.
- Thêm xác thực người dùng (đăng nhập quản trị viên) bằng ASP.NET Core Identity.
- Huấn luyện model YOLOv8 riêng cho lớp "license_plate" để tăng độ chính xác ANPR.
- Thêm tính năng **đếm xe qua vạch** (line-crossing) bằng tracking (ByteTrack/DeepSORT) để
  tránh đếm trùng khi xe di chuyển qua nhiều khung hình liên tiếp.
- Tích hợp bảng LED vật lý qua giao thức phù hợp (Modbus/RS485/HTTP tuỳ thiết bị thực tế),
  thay cho `LedBoard` mô phỏng hiện tại.
- Thêm trang **Báo cáo** (thống kê lượt vào/ra, tỷ lệ lấp đầy theo giờ) như trong ảnh gốc.

## 5. LED card Huidu (HDSDK.dll) — theo Form HDCard của PGS

Trang **Hệ thống → LED** (`/System/Leds`) dựng lại đúng cách làm việc của Form HDCard trong PGS:

| PGS (HDCard) | Trang LED |
|---|---|
| Card số 1..100, Địa chỉ IP, Enable/Disable, Rộng (bội 32), Cao (bội 16), Loại Card, Màu, Gray Level | Cùng các ô, cùng danh sách model/giới hạn/số khối (`HdCardCatalog`) |
| Bảng vùng (No, Type) với Add Image / Add text / Add Num / Delete / Up / Down, tối đa 20 | Giống hệt, "Quá 20 thông tin" khi vượt |
| Panel CÀI ĐẶT VÙNG NHỚ HÌNH ẢNH / CHỮ / SỐ, Origin X/Y, Width/Height | Giống hệt (cùng tên cột `Ttext`, `NlimitY`... trong dữ liệu; riêng nguồn số dùng `NMs`, `NMc`, `NTag`) |
| Vùng Number = **biến MS + biến MC + project tag**, màu Manual/Auto theo ngưỡng vàng/đỏ, Số lượng kí tự, Full | Giống hệt |
| Khung mô phỏng LED, bấm vào vùng để chọn, số cập nhật trực tiếp | Có (tỉ lệ 10 px/điểm LED khi vừa khung) |
| Sửa là tự lưu (Save_1_card / Save_All_card) | Tự lưu sau mỗi thay đổi |
| Nút **Gửi tới card** (ép gửi lại) | Giống hệt |
| Nút **Danh sách card** | Có, kèm trạng thái kết nối |
| Dữ liệu loại A/B/C | Không còn — thay bằng biến MS, biến MC và project tag (xem bên dưới) |

### Nguồn số của vùng Number: MS, MC, project tag
Mỗi vùng Number có 3 ô chọn, **số hiển thị = MS + MC + tag** (ô để trống tính là 0):
- **Dữ liệu MS**: một biến MS (tab *Dữ liệu* của trang Cảm biến), ví dụ `MS0` = số chỗ còn trống đếm từ cảm biến.
- **Dữ liệu MC**: một biến MC (tab *Dữ liệu* của trang Camera), ví dụ `MC1` = số vị trí đã có xe.
- **Project tag**: một project tag kiểu **Integer / Boolean / Real** (TASKS → Tags → Project Tags). Real được làm tròn tới số nguyên gần nhất; kiểu String không chọn được.

Giá trị luôn đọc trực tiếp từ biến/tag nên không có thanh ghi riêng để cấu hình. Số âm hiển thị là 0. Biến/tag đã bị xoá tính là 0 (trang vẫn hiện tên đã chọn kèm "không còn" để bạn biết cần chọn lại).
Các thanh ghi **A1..A100 / B1..B100 / C1..C100** và API `/api/system/leds/registers/*` đã bị bỏ; cấu hình cũ (`NAdata`, `NBdata`, `NCdata`) không còn được đọc, nên vùng Number cũ cần chọn lại nguồn.

### Cách gửi (giống SendtoDHCard.HDMain)
Chỉ gửi khi **dữ liệu** (số hiển thị của các vùng Number của card) khác lần đã gửi thành công; chữ/ảnh chỉ gửi lần đầu, khi bấm *Gửi tới card* hoặc đi kèm lần gửi khi số đổi. Đổi cấu hình card **không tự gửi** — trang hiện cảnh báo "Cấu hình đã đổi… bấm Gửi tới card" như quy trình ở PGS.
Luồng SDK: `Hd_CreateScreen` → `Hd_AddProgram` → mỗi vùng `Hd_AddArea` + `Hd_AddSimpleTextAreaItem`/`Hd_AddImageAreaItem` → `Hd_SendScreen(0, IP)` (khai báo chép từ `CSDKExport.cs`).

### Khác PGS (chỉ về hiệu năng, không đổi cách dùng)
- Chạy theo **sự kiện** (bãi xe đổi / lưu card) và đọc lại giá trị MS/MC/tag mỗi `Led:PollMs` (mặc định 1000 ms, vì các biến này không phát sự kiện khi đổi), gộp sự kiện dồn dập, thay vì quét 100 card liên tục. Chỉ đọc các biến/tag mà vùng Number đang dùng.
- Giãn cách tối thiểu mỗi card; card mất kết nối **thử lại lùi dần** (không chặn card khác).
- `HDSDK.dll` là DLL 32-bit nên chạy trong tiến trình phụ `src/ParkingGuidance.LedBridge` (x86): SDK treo/crash không làm sập web.
- Cấu hình `appsettings.json` mục `Led` (`Driver`: `Auto`|`Simulator`, `DebounceMs`, `MinIntervalMs`, `PollMs`, `MaxBackoffSeconds`, `ResyncMinutes` — mặc định 0 = tắt như PGS).

### Triển khai
1. `tools\build-led-bridge.cmd` → tạo `src/ParkingGuidance.Web/led-bridge/` (chưa có thì LED chạy **chế độ mô phỏng**).
2. Dữ liệu card lưu `data/led-cards.json`, thanh ghi `data/led-registers.json`, ảnh `data/led-images/`.

### Chưa chép / khác biệt cần biết
- Ngưỡng màu: nhãn trên form PGS ghi "<=" nhưng mã PGS so sánh **nhỏ hơn hẳn** (`giá trị < ngưỡng`); bản này giữ đúng hành vi của PGS.
- Ngoại lệ riêng dự án Hạ Long trong `SendtoDHCard` (card số 100 hiện SẴN SÀNG/ĐANG CHỜ theo B1..B4) **không** chép.
- Nút "Kết nối" dùng `Cmd_IsCardOnline` (PGS không gọi hàm này; ý nghĩa giá trị trả về được suy từ mã máy DLL: 0 = online).
- Font `.Vn...` là font cài trên máy (như PGS).


## Thiết bị › Cảm biến (MGT200 / Keytop)

Trang `/System/Sensors` làm lại 2 loại cảm biến của phần mềm PC (PGS). **MGT200 là mặc định.** Cài đặt lưu `data/sensors.json`, đổi xong có hiệu lực ngay.

| | MGT200 | Keytop |
|---|---|---|
| Cách nối | PC kết nối TCP tới converter (mặc định 192.168.0.7:8888), hỏi lần lượt từng ZCU được kích hoạt | PC mở server TCP (mặc định cổng 7777), các Keytop tự kết nối và gửi |
| Quy mô | tối đa 32 ZCU × 160 cảm biến | tối đa 85 Keytop × 60 cảm biến |
| Lệnh hỏi | `p` + ID 2 chữ số + `0011900000` + CR | — (thiết bị tự gửi) |
| Dữ liệu | khung 185 ký tự sau khi bỏ `Mnn` và `099`: 5 dòng × 32, `-` lỗi, `1` có xe, còn lại trống | các nhóm `(Xs)`: X = cảm biến 1–60 (`A`=1 … `|`=60), s = 0 trống / 1 có xe / 2 lỗi |
| Mất kết nối | converter lỗi 10 lần liên tiếp, hoặc 1 ZCU không trả lời 3 lần liên tiếp | 1 Keytop không gửi gì quá timeout (mặc định 50 giây, chỉnh được) |

Trang có: chọn loại cảm biến + cài đặt kết nối, tích chọn ZCU quét, giám sát từng ZCU/Keytop và từng cảm biến (cập nhật mỗi giây), ô "Thử khung dữ liệu" để kiểm tra giao thức.
Mã: `Services/Sensor/` (`SensorProtocol` giao thức, `SensorService` trạng thái + cài đặt, `SensorDriverService` tiến trình nền), `Controllers/SensorsApiController.cs`, `Pages/System/Sensors.cshtml`, `wwwroot/js/systemSensors.js`.
**Cài đặt cảm biến MGT200** (như tab "Cài đặt cảm biến" của PM200Setting): chọn ZCU (0–31), lệnh, Line (CN 1–5 hoặc tất cả), cảm biến (ID 0–31 hoặc tất cả), dữ liệu rồi bấm "Thực hiện".
Khung lệnh: `p` + ZCU(2) + Line(1) + ID(2) + mã lệnh(2) + dữ liệu(3) + `00` + CR. Mã lệnh: 1 Ground auto set · 2 Ground manual set (100–600 cm) · 3 Saved Ground Time Report · 4 Ground threshold set (0–200 cm) · 5 Saved Active Time Report · 6 Lamp Mode Set (0–9) · 7 Report Detect_Distance (đọc khoảng cách 32 cảm biến của 1 Line, trả về khung 139 ký tự; có "Đọc liên tục").
Ảnh minh họa vẽ lại bằng SVG/HTML: lệnh 1, 2, 4 hiện sơ đồ lắp đặt (xe + cảm biến treo trần, sáng đúng thông số của lệnh đang chọn: Ground auto set / manual set / threshold set); lệnh 6 hiện bảng màu đèn 0–9 (màu khi trống / khi đỗ; 5 và 6 không dùng) và tô sáng chế độ đang chọn.
Lệnh được tiến trình nền gửi xen giữa các lượt hỏi ZCU để không tranh kết nối với converter. Panel này chỉ hiện khi loại cảm biến là MGT200. ZCU đánh số từ 0 như PGS.

Lưu ý: hiện trang chỉ cài đặt và giám sát; chưa gắn cảm biến vào ô đỗ trên layout.

### Ghi chú giao diện (cập nhật)
- Nhóm menu **Hệ thống** đã đổi tên thành **Thiết bị** (Camera, Cảm biến, LED).
- Mọi nút bấm / ô nhập / danh sách chọn kế thừa font đã chọn (`button, input, select, textarea { font-family: inherit }` trong `site.css`).
- **Thuộc tính vùng hiển thị LED** nằm trong **hộp thoại nổi bố cục ngang** (giống Layout Editor, khung LED không còn bị đẩy xuống bởi bảng thuộc tính): **nháy đúp** vào vùng trên khung LED
  (hoặc dòng trong bảng) hoặc bấm nút **⚙️ Thuộc tính** để mở; khi đang mở, chọn vùng khác thì nội dung hộp thoại đổi theo vùng đó. Kéo thanh tiêu đề để dời (vị trí được nhớ),
  đóng bằng ✕ hoặc Esc. Chỉnh sửa trong hộp thoại vẫn ghi ngay và vẽ lại mô phỏng như trước.
- Trang **LED** đã Việt hoá toàn bộ nhãn tĩnh (Thêm hình ảnh, Thêm chữ, Thêm số, Xóa, Lên, Xuống, Gốc X/Y, Cỡ chữ, ...) và có bản English theo ngôn ngữ đã chọn.

### Ghi chú tab Camera › Vị trí đỗ (cập nhật)
- Khung ảnh tự co theo chiều cao màn hình (CSS `#zoneStage`, biến `--ar` = tỉ lệ ảnh) nên luôn nhìn thấy trọn ảnh; toạ độ vẽ vùng tính theo % của khung nên không bị lệch.
- **Danh sách vùng đã vẽ** nằm cạnh ảnh, mặc định **ẩn**; bấm **📋 Danh sách vùng (n)** để xổ ra / thu lại.
- Khung "Chế độ nhận diện" và "Tối ưu tải xử lý" gộp thành 1 hàng gọn phía trên ảnh. Chế độ **Tự động/Thủ công** được giữ lại vì nó khác ô "Tự động nhận diện khi ảnh mới thay đổi…": ô đó chỉ áp dụng cho ảnh xem trước khi đang mở trang, còn chế độ này lưu trên server và quyết định camera có chạy nền liên tục hay không (Thủ công = chỉ nhận diện khi bấm "Nhận diện ngay").

## TASKS › Tags (`/Tasks/Tags`) và Script (`/Tasks/Script`)

Menu **TASKS** có 2 mục: **🏷️ Tags** và **📜 Script**. Trang Tags có 2 tab:

### System tags (chỉ đọc)

Bảng tag có sẵn của hệ thống, giá trị cập nhật trực tiếp trên trang:

| Tag | Kiểu | Ý nghĩa |
|---|---|---|
| `Hour` / `Minute` / `Second` | Integer | giờ / phút / giây (giờ máy chủ) |
| `Day` / `Month` / `Year` | Integer | ngày / tháng / năm |
| `DayOfYear` | Integer | ngày trong năm, tính từ 0 (0–365) |
| `Weekday` | Integer | 0 = Chủ nhật … 6 = Thứ bảy |
| `Yesterday` / `Tomorrow` | Integer | số ngày (trong tháng) của hôm qua / ngày mai |
| `BlinkSlow` | Boolean | nháy chậm: đổi 0↔1 sau mỗi `Tags:BlinkSlowMs` (mặc định 1000 ms) |
| `BlinkFast` | Boolean | nháy nhanh: đổi 0↔1 sau mỗi `Tags:BlinkFastMs` (mặc định 250 ms) |

**Không ảnh hưởng luồng chính** nhờ:

- chạy trong luồng nền riêng `SystemTagWorker`, không dùng chung khoá/hàng đợi với camera, cảm biến, LED, bãi xe (`SystemTagService` không phụ thuộc các dịch vụ đó);
- giá trị được thay nguyên khối (không khoá khi đọc), nháy tính theo đồng hồ đơn điệu nên không lệch khi luồng bị trễ;
- mọi lỗi bị chặn tại chỗ (chỉ ghi vào trạng thái + log thưa), vòng lặp hỏng thì tự chạy lại sau 5 giây; không bao giờ ném lỗi làm dừng ứng dụng;
- tắt hẳn được bằng `"Tags": { "SystemEnabled": false }` trong `appsettings.json`;
- trang hiện trạng thái luồng (đang chạy, chu kỳ quét, số lỗi) ngay phía trên bảng.

### Project tags (người dùng khai báo)

Khai báo theo bảng **Name · Value · Type · Description · Scope** (nút Thêm / Sửa / Xoá, nháy đúp 1 dòng để sửa).

- Tên: chữ không dấu, số, `_`; bắt đầu bằng chữ hoặc `_`; tối đa 64 ký tự; không trùng nhau (không phân biệt hoa/thường) và không trùng System tag.
- Kiểu: `Integer`, `Boolean` (0/1), `Real` (chấp nhận cả `1,5` lẫn `1.5`), `String` (≤ 255 ký tự). Để trống Value = giá trị mặc định.
- Scope: `Local` hoặc `Global`.
- Lưu ở `data/project-tags.json` (cùng thư mục dữ liệu khác, xem mục 9).

API: `GET /api/tasks/tags/system`, `GET|POST /api/tasks/tags/project`, `PUT|DELETE /api/tasks/tags/project/{name}`, `PUT /api/tasks/tags/project/{name}/value`, `GET /api/tasks/tags/live?group=system|project`.

Mã: `Models/Tags.cs`, `Services/Tags/` (`SystemTagService`, `SystemTagWorker`, `ProjectTagService`), `Controllers/TagsApiController.cs`, `Pages/Tasks/Tags.cshtml`, `wwwroot/js/tasksTags.js`.

### Script

Mục menu và trang `/Tasks/Script` đã có chỗ đứng; trình soạn thảo và bộ chạy script chưa làm.

## Tab 🧠 Train (Thiết bị → Camera)
Quy trình dạy lại nhận diện biển số:
1. Tab **🧪 Mô phỏng**: ảnh nào không nhận diện được / đọc sai → dùng nhóm nút **🧠 Thêm vào Train** ở góc trái thanh công cụ: **➕ Train xe** (sang kho mẫu của Train xe), **➕ Train biển** (sang kho mẫu của Train biển số) — cả hai tự gợi ý khung chưa duyệt để bạn kiểm tra lại —, **➕ Train ký tự** (phím `T`: ảnh gốc + khung biển/biển đã đọc được chuyển sang *hàng chờ* Train ký tự) hoặc **➕ Train tất cả** (cả ba).
2. Tab **🧠 Train**: chọn ảnh trong hàng chờ → kéo chuột **cắt vùng biển** (dời/chỉnh khung, lăn chuột zoom, `Space` + kéo để dịch ảnh) → gõ **ký tự đúng** → `Enter` để lưu mẫu (xong ảnh thì tự sang ảnh kế). `🤖 Tự dò biển` chạy AI gợi ý khung; `📂 Thêm ảnh từ máy` nạp ảnh trực tiếp. `✂️ Cắt vùng biển` (phím `C`) bật chế độ kéo chuột cắt khung khi AI không dò ra biển.
   Khi hệ thống **không tách/nhận được một số ký tự**: bấm **🔤 Gắn từng ký tự** ở khung biển → khoanh từng ký tự bằng một ô và chọn ký tự đúng (gõ phím hoặc bàn phím ký tự); mô hình học thẳng từ các ô này (không phụ thuộc bước tự tách).
3. Bấm **🧠 Train** (nền, ưu tiên thấp). Model mới tốt hơn thì tự cài và nạp lại ngay; bản cũ được sao lưu `.bak-…`.

Dữ liệu lưu trong thư mục dữ liệu của ứng dụng: `simulation/inbox` (hàng chờ ảnh) và `simulation/corrections` (mẫu đã cắt + nhãn).
API: `/api/system/simulation/inbox*` (hàng chờ), `/corrections*` (mẫu), `/train*` (huấn luyện).

### Dò vùng biển (bước 1) — cải tiến
- Khung xe được nới 8% mỗi phía trước khi cắt để dò biển (khung xe hay cắt sát làm mất một phần biển).
- Lần dò thường (ngưỡng 0.5) không ra biển đọc được → **dò nới lỏng**: ngưỡng 0.3 trên cả ảnh + ô 2x2/3x3, rồi ảnh đã cân bằng sáng (CLAHE), cuối cùng ngưỡng 0.15. Tắt bằng `Anpr:RelaxedSearch = false` (nếu CPU yếu và nhiều camera).
- Dò được vùng biển nhưng chưa đọc được ký tự thì **vẫn trả khung biển** (tab Mô phỏng ghi rõ "🔶 dò được vùng biển nhưng chưa đọc được ký tự"; "🔴 không dò được vùng biển" là lỗi ở bước dò). Khung này được điền sẵn sang tab Train.
- Model dò xe không thấy xe nào: mô phỏng vẫn thử tìm biển trên cả khung hình.
- Sau khi train, bảng **chi tiết từng biển** cho biết model cũ/mới đọc ra gì và lỗi ở bước nào (tách ký tự / đọc ký tự).


---

## 🚗 Nhận diện xe: YOLO11 + Train xe

### Chuyển từ YOLOv8 sang YOLO11

YOLO11 và YOLOv8 xuất ONNX cùng một dạng đầu ra `[1, 84, 8400]`, nên **không cần đổi cách giải mã**; chỉ cần đổi file model. `Detection:ModelPath` mặc định giờ là `models/yolo11n.onnx`. Nếu file này **chưa có**, ứng dụng tự dùng `models/yolov8n.onnx` (đi kèm sẵn) để vẫn chạy.

```
pip install ultralytics
python tools/export_yolo11.py            # yolo11n (nhanh nhất)  → src/ParkingGuidance.Web/models/yolo11n.onnx
python tools/export_yolo11.py yolo11s.pt # chính xác hơn, chậm hơn (rồi đặt Detection:ModelPath = models/yolo11s.onnx)
```

Cải tiến bộ nhận diện (`YoloVehicleDetectionService`): kích thước ảnh vào lấy từ chính model; nhận biết đầu ra chuyển vị; lớp xe lấy từ **tên lớp ghi trong model** (model train thêm có đúng 80 lớp COCO nên vẫn là car=2, bus=5, truck=7; xe máy chỉ tính khi `Detection:IncludeMotorcycle = true`); tiền xử lý ảnh bằng phép toán OpenCV theo khối và đọc đầu ra bằng vùng nhớ liên tục (nhanh hơn nhiều so với lặp từng điểm ảnh / chỉ số tensor); `Detection:Threads` giới hạn số luồng ONNX Runtime (0 = mặc định); **nạp lại model khi đang chạy** không cần khởi động lại.

### Tab 🚗 Train xe (trang Camera)

Dạy thêm cho model nhận diện xe bằng chính ảnh camera của bạn (fine-tune YOLO11):

1. **Thêm ảnh** (tải lên; mặc định tự gán khung bằng model đang chạy để bạn chỉ phải sửa).
2. Mở từng ảnh, **chỉnh khung**: kéo chuột để vẽ khung mới, kéo khung để di chuyển, kéo góc để đổi kích thước, `Delete` để xoá, phím `1`–`4` đổi loại xe (ô tô / xe máy / xe buýt / xe tải), `Enter` = lưu & sang ảnh kế. Bấm **✓ Lưu & duyệt**. Ảnh thật sự không có xe: duyệt với 0 khung (mẫu âm, giúp bớt báo nhầm).
3. Từ **~100 ảnh đã duyệt** (tối thiểu 10) bấm **▶ Train**. Chỉ ảnh đã duyệt được dùng; ~20% ảnh (cố định theo mã ảnh) làm tập kiểm tra.
4. Train chạy ở **tiến trình Python riêng** (`tools/train_vehicle.py`, ưu tiên thấp, chỉ khi bấm Train) nên không ảnh hưởng nhận diện camera thật. Xong, ứng dụng so **mAP50-95** trên tập kiểm tra giữa model hiện tại và model mới: **không kém hơn thì tự cài** (sao lưu bản cũ, nạp lại ngay, không cần khởi động lại); kém hơn thì không tự cài, có nút **✔ Vẫn cài bản mới**. Lần Train sau mặc định **train tiếp** từ lần trước (tích *Bắt đầu lại từ model gốc* để làm lại).
5. **↩ Về model gốc** bỏ model đã train thêm (giữ lại bản đã train với đuôi `.removed-…`).

Dữ liệu ở `data/vehicle-train/` (`samples/` ảnh + `index.json`, `active/vehicle.onnx` model đã train, `train-status.json`). Cấu hình (`appsettings.json` → `VehicleTrainer`): `Script`, `Python` (mặc định tìm `tools/.venv` rồi `python`), `BaseWeights` (mặc định `yolo11n.pt`; có thể là đường dẫn file đã tải sẵn), `Epochs` (30), `ImgSize` (640), `Device` (`cpu`, hoặc `0` nếu có GPU NVIDIA + PyTorch CUDA), `Batch` (8), `Threads` (0).

**Lưu ý:** cần `pip install ultralytics` trong Python dùng để train (lần đầu cần mạng để tải trọng số `yolo11n.pt` trừ khi đã đặt sẵn `BaseWeights`). Train trên CPU chậm (vài chục phút trở lên tuỳ số ảnh); có GPU sẽ nhanh hơn nhiều. **Giấy phép:** Ultralytics YOLO11/YOLOv8 theo **AGPL-3.0** (hoặc giấy phép doanh nghiệp trả phí) — hãy kiểm tra điều kiện nếu dùng thương mại.


---

## 🧠 Tab lớn Train (trang Camera): Train biển số · Train xe · Train ký tự

Ba phần huấn luyện nằm chung một tab **🧠 Train**, mỗi phần là một tab con. Quy trình đọc biển: **🚗 tìm xe → 🔖 tìm biển trên xe → 🔤 đọc từng ký tự**; sai ở bước nào thì train bước đó.

| Tab con | Dạy gì | Dữ liệu | Model sau khi train |
|---|---|---|---|
| 🔖 **Train biển số** | Bộ dò **vị trí biển số** (YOLO11, 1 lớp) | `data/plate-train/` | `data/plate-train/active/plate.onnx` |
| 🚗 **Train xe** | Model nhận diện xe (YOLO11) | `data/vehicle-train/` | `data/vehicle-train/active/vehicle.onnx` |
| 🔤 **Train ký tự** | CNN đọc từng ký tự (cắt biển, gán ký tự) | `simulation/inbox`, `simulation/corrections` | `char_cnn.onnx` |

### 🔖 Train biển số

1. **Thêm ảnh** (cả khung hình camera hoặc ảnh xe đều được), nhất là ảnh bộ dò đang bỏ sót / khoanh nhầm biển (nghiêng, xa, ban đêm, ngược sáng, xe máy…). Mặc định tự gán khung bằng bộ dò hiện tại để bạn chỉ phải sửa.
2. Mở từng ảnh, **vẽ khung ôm sát biển** (biển 2 dòng: một khung chứa cả hai dòng). Kéo chuột để vẽ, kéo khung / góc để chỉnh, `Delete` xoá khung, `Enter` = lưu & sang ảnh kế. Ảnh không có biển: duyệt với 0 khung (mẫu âm).
3. Từ **~50 ảnh đã duyệt** (tối thiểu 10) bấm **▶ Train**. Chỉ ảnh đã duyệt được dùng; ~20% ảnh làm tập kiểm tra. Không lật ngang ảnh khi train (biển lật gương không còn là biển).
4. Train chạy ở **tiến trình Python riêng** (`tools/train_plate.py`, ưu tiên thấp). Xong thì so mAP50-95 với model biển ONNX đang chạy: không kém hơn thì **tự cài và nạp lại ngay**. Nếu chưa có model ONNX để so (bộ dò mặc định là Darknet) thì chỉ tự cài khi **mAP50 ≥ 60%**; không thì có nút **✔ Vẫn cài bản mới**.
5. Model đã train được **ưu tiên trước** bộ dò mặc định; không thấy biển thì vẫn rơi về bộ dò mặc định. **↩ Về bộ dò mặc định** bỏ model đã train (giữ bản `.removed-…`).

Cấu hình (`appsettings.json` → `PlateTrainer`): `Script`, `Python`, `BaseWeights` (mặc định `yolo11n.pt`), `Epochs` (40), `ImgSize` (640), `Device`, `Batch`, `Threads`. Cần `pip install ultralytics`.
API: `/api/system/plate-train/*` (info, samples, train/start|status|cancel|install|restore|clear).
Mã: `Services/PlateTrain/`, `Controllers/PlateTrainApiController.cs`, `Services/Anpr/IPlateDetectorModel.cs`, `wwwroot/js/plateTrain.js`; chuyển tab con: `wwwroot/js/trainHub.js`.


### 📤 Export / 📥 Import dữ liệu train

- **Export...** (nút ở tab lớn 🧠 Train): chọn **phần cần xuất** (🔤 Train ký tự · 🔖 Train biển số · 🚗 Train xe), tuỳ chọn **kèm model đã train**, chọn **thư mục đích**. Ứng dụng tạo thư mục con `ParkingGuidance-train-<ngày giờ>` trong thư mục đã chọn.
- **Import...** (nút ở tab 🧪 Mô phỏng): chọn thư mục gói train (thư mục `ParkingGuidance-train-…`; bộ duyệt thư mục đánh dấu 📦 các gói), xem trước số mục và số mục **mới**, chọn phần cần nhập rồi bấm Import.
- Thư mục nằm trên **máy chạy ứng dụng** (không phải máy của trình duyệt); hộp thoại có bộ duyệt thư mục + tạo thư mục mới.
- **Import là gộp, không xoá gì**: mục đã có (cùng mã) được giữ nguyên, chỉ thêm mục mới. Model chỉ bị thay khi bạn tick “Thay model đang chạy…” (bản cũ được sao lưu `.bak-…` và nạp lại ngay; nạp lỗi thì khôi phục).
- Nội dung gói: `char/corrections` + `char/inbox` (+ `char/model/char_cnn.onnx`), `plate/samples` (+ `plate/model/plate.onnx`), `vehicle/samples` (+ `vehicle/model/vehicle.onnx`), `parkingguidance-train.json`.
- API: `/api/system/train-transfer/*` (summary, browse, mkdir, inspect, export, import). Mã: `Services/TrainTransfer/TrainTransferService.cs`, `Controllers/TrainTransferApiController.cs`, `wwwroot/js/trainTransfer.js`.

## Đọc ký tự bằng YOLO11 (bộ dò ký tự `char_det.onnx`)

Ngoài đọc cổ điển (nhị phân hoá → tách thành phần liên thông → CNN `char_cnn.onnx`), ứng dụng có thể đọc ký tự bằng **model dò đối tượng YOLO11n** (31 lớp = 31 ký tự biển VN). Model nhìn thẳng ảnh biển và trả về vị trí + loại từng ký tự nên không phụ thuộc bước nhị phân hoá/tách ký tự (nơi dễ sai khi biển nghiêng, mờ, dính chữ hoặc dính khung viền).

- **Chạy:** nếu có file model, ứng dụng dùng nó TRƯỚC; đọc được thì dùng luôn, không đọc được thì tự rơi về cách cổ điển (CNN, rồi Tesseract). Không có file thì mọi thứ chạy như cũ.
- **File model (ưu tiên từ trên xuống):** `{DataDirectory}/char-det/char_det.onnx` (do `tools/train_chars.py --deploy` xuất) → `Anpr:CharDetModelPath` (mặc định `models/char_det.onnx`). Ứng dụng kiểm tra file mỗi 15 giây và tự nạp lại khi file đổi, không cần khởi động lại.
- **Dữ liệu train:** chính các mẫu ở tab Train (`{DataDirectory}/simulation/corrections`: ảnh biển + biển đúng). Khung từng ký tự lấy từ các ô bạn khoanh tay (nút **🔤 Gắn từng ký tự**); mẫu không khoanh tay thì tự gán nhãn bằng tách ký tự cổ điển và chỉ nhận khi số thành phần = số ký tự. **Biển khó (nghiêng, mờ, dính chữ) nên khoanh tay** để model học đúng các ca này.
- **Train:**
  ```bash
  pip install ultralytics
  python tools/train_chars.py --corrections <DataDirectory>/simulation/corrections --out <thư mục làm việc> \
         --deploy <DataDirectory>/char-det/char_det.onnx
  ```
  Cuối quá trình script in tỉ lệ **đọc đúng trọn biển** trên tập kiểm tra. Chỉ `--dataset-only` để dựng bộ dữ liệu YOLO và xem thống kê trước khi train.
- **Số liệu tham khảo:** cần tối thiểu 30 biển (mặc định); càng nhiều biển đa dạng (ngày/đêm, nghiêng, 1–2 dòng, từng camera) càng chính xác.

### Kết hợp YOLO và Tesseract
YOLO là bộ đọc **chính**; Tesseract chỉ làm bước **kiểm chứng** (Cấu hình chung → *Kết hợp Tesseract kiểm chứng*, mặc định bật). Tesseract vốn thiết kế cho văn bản, đứng một mình đọc biển rất kém (đo trên 2 biển mẫu: 0/48 lần đọc trọn vẹn, hay thêm ký tự thừa từ khung viền), nên không được đối xử ngang hàng với YOLO:
- YOLO **chắc** (một kết quả duy nhất, độ tin cậy trung bình ≥ 0,85, khung không nghi cụt) → nhận luôn, không chạy Tesseract.
- YOLO **chưa chắc** (tin cậy thấp, các lần đọc bất đồng, hoặc khung nghi cụt) → Tesseract đọc nhiều biến thể ảnh rồi bỏ phiếu; mỗi kết quả của Tesseract chỉ được cộng **tối đa 2,5 điểm**. Cùng kết quả với YOLO → tăng độ tin cậy; khác → chỉ có tác dụng khi YOLO chưa phân thắng bại. Điểm tối đa này không đủ để lật một kết quả YOLO đã đạt ngưỡng.
- YOLO **không đọc được** → rơi về CNN cổ điển, rồi Tesseract (như trước).
- Muốn biết kết hợp có đáng bật với dữ liệu của bạn: chạy `train_chars.py ... --compare-tesseract`; script in số biển "chỉ YOLO đúng / chỉ Tesseract đúng / cả hai đúng / cả hai sai" trên tập kiểm tra. "Chỉ Tesseract đúng" gần 0 → nên tắt kết hợp.

### Chọn bộ dò khung biển
Cấu hình chung có ô **Bộ dò khung biển**: 0 = tự động (gộp khung của YOLO11n đã train và Darknet), 1 = chỉ YOLO11n đã train (chưa có model thì dùng Darknet), 2 = chỉ Darknet. Dùng để so sánh hai bộ dò trên cùng một tập ảnh (bật `Anpr:DebugDir` + `DebugAll` để xem khung từng lần đọc).
