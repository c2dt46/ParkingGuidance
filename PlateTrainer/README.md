# PlateTrainer v2 — huấn luyện nhận diện ký tự biển số cho ParkingGuidance

Ứng dụng web chạy trên máy bạn (Python + Flask), tạo ra **`char_cnn.onnx`** thay trực tiếp file
`src/ParkingGuidance.Web/Models/char_cnn.onnx` của dự án — **không cần sửa C#**.

## Vì sao khớp với dự án của bạn
`CnnPlateReader.cs` tách ký tự bằng thành phần liên thông, rồi đưa từng ký tự 28×28 (0..255, không chuẩn hoá) vào CNN 32 lớp
(`ABCDEFGHKLMNPRSTUVXYZ0123456789` + "nền"). PlateTrainer:
- **chuyển đúng thuật toán tách ký tự đó sang Python** (`anpr/segment.py`) nên ảnh huấn luyện giống hệt ảnh lúc chạy thật
  (đã đối chiếu: ảnh `case_51D-932.40.png` cắt biển → cùng model cũ cho `51D-932.40`);
- xuất ONNX đúng hợp đồng: vào `[N,1,28,28]` 0..255, ra `[N,32]` đã softmax (việc ÷255 nằm trong model);
- đo độ chính xác **cả biển** bằng đúng quy trình 4 kiểu nhị phân hoá + bỏ phiếu + `VietnamPlateFormatter`.
Nếu bạn đổi ngưỡng/bước trong CnnPlateReader.cs, sửa tương ứng `anpr/segment.py`.

## Cài đặt (Windows)
1. Cần Python 3.10+ (https://python.org, tick "Add to PATH").
2. Chạy `install.bat` (tạo .venv, cài thư viện; PyTorch bản CPU là đủ, có GPU NVIDIA thì cài bản CUDA theo pytorch.org).
3. Chạy `run.bat` → mở http://127.0.0.1:5055

## Quy trình khuyến nghị
0. **Xử lý thô** (tab đầu): thêm nhiều ảnh, quét chuột quanh biển số, gõ biển số, Enter → ảnh cắt (độ phân giải gốc) lưu vào `data/raw_done/<biển>.png`
   và tự sang ảnh kế. Xong bấm *Chuyển sang tab Dữ liệu* để tách ký tự (bước 2).
1. **Sinh dữ liệu**: phông biển số `Soxe2banh.TTF` đã có sẵn trong `fonts/` (chiếm ~85% mẫu; tick *chỉ dùng phông của bạn* để dùng 100%). Có thể thêm phông khác vào `fonts/`, bấm *Sinh dữ liệu* (≈400 ảnh/ký tự + 600 ảnh "nền").
2. **Dữ liệu → Nhập ảnh biển thật**: ảnh đã cắt sát biển, tên file = biển đúng (`80A-00538.jpg`). Biển tách đúng số ký tự thì gán nhãn
   tự động ("Chấp nhận tất cả mẫu đã khớp"), biển còn lại duyệt tay. Nên giữ 10–20% biển làm **tập kiểm thử** (không dùng để học).
   Ảnh thật quan trọng nhất: chúng được nhân ×4 khi huấn luyện, và độ chính xác "trên ảnh thật" quyết định bản tốt nhất.
3. **Huấn luyện** (mặc định 40 epoch, dừng sớm). Mặc định *học tiếp từ lần gần nhất* (fine-tune, LR ×0.3); model cha được đo trước và chỉ bị thay khi có bản tốt hơn. Muốn kết quả độc lập thì chọn *Học lại từ đầu*. Tập val chia cố định theo tên file nên không rò rỉ giữa các lần.
4. **Kiểm thử**: đọc từng biển hoặc đo cả thư mục, so sánh với model hiện tại của dự án. Ký tự đọc sai → chọn nhãn đúng → *Thêm* → huấn luyện lại.
5. **Cài vào dự án**: nhập đường dẫn thư mục ParkingGuidance, chọn bản huấn luyện; model cũ tự sao lưu `.bak-…`.
   Khởi động lại ParkingGuidance (build lại nếu chạy từ bin/ vì `Models\**` được chép khi build).

## Lưu dữ liệu thành thư mục & kế thừa giữa các lần train (v2)
- **Dữ liệu của bạn** (ký tự đã gán nhãn, ảnh biển, các lần train) nằm trong *kho dữ liệu*. Mặc định là thư mục `data/` cạnh ứng dụng.
  Ở tab *Dữ liệu → mục 4* bạn có thể **đặt kho cố định** ở nơi khác (vd `D:\PlateData`, lưu vào `config.json`) để đổi/cài lại ứng dụng mà dữ liệu vẫn còn.
- **Lưu ra thư mục**: sao chép bộ dữ liệu (ký tự thật, ảnh biển, tập kiểm thử, tuỳ chọn ảnh tổng hợp và các lần train) sang một thư mục bạn chọn. Không xoá gì ở kho.
  **Gộp từ thư mục**: nạp ngược lại, bỏ qua file đã có. Cấu trúc thư mục giống hệt kho nên đặt thẳng làm kho cũng được.
- Mỗi lần train ghi một **sổ ghi dữ liệu** (`runs/<lần>/manifest.json`): ký tự nào đã dùng, và (khi học tiếp) cả những gì kế thừa từ model cha.
  Ở tab *Huấn luyện* chọn **Dữ liệu dùng để train**: *Toàn bộ* · *Chỉ dữ liệu MỚI* (bỏ những gì lần tham chiếu đã học) · *Chỉ đúng dữ liệu của lần tham chiếu* (tái lập).
  Tab *Dữ liệu* hiển thị "Chưa train: N ký tự thật mới".
- Dùng *Chỉ dữ liệu MỚI* nên kèm *Tiếp tục từ* cùng lần đó; model có thể quên ký tự không có mẫu mới. Lần train từ bản cũ (không có sổ ghi) chỉ dùng được "Toàn bộ".

## Lưu ý
- Ảnh tổng hợp giúp phủ đủ 31 ký tự nhưng **không thay được ảnh camera thật**; muốn tốt cho bãi xe của bạn hãy bổ sung vài trăm biển thật.
- Lớp "nền" dạy model bỏ qua viền/ốc vít/mảnh vỡ — hãy nhập cả biển 2 dòng có khung, tem.
- Model dò biển (YOLOv3-tiny hiện tại) không đổi. Muốn huấn luyện model dò riêng: `train_detector.py` (cần `pip install ultralytics`).
- Dòng lệnh không dùng web: `python -m anpr.train --data data/chars --out runs/lan1`.

## Tự động học từ ParkingGuidance (`anpr.autotrain`)
Tab **Train** của ParkingGuidance (thiết bị → Camera → 🧠 Train; ảnh được thêm từ tab Mô phỏng bằng nút ➕ Thêm vào Train) gọi `python -m anpr.autotrain --corrections <thư mục mẫu> --current-model <char_cnn.onnx đang chạy> --status <status.json>`
(một tiến trình riêng, ưu tiên thấp, thoát khi xong). Các bước: nạp mẫu {ảnh biển, biển đúng, biển hệ thống đã đọc} → (lần đầu) sinh dữ liệu tổng hợp →
huấn luyện **học tiếp** từ lần trước, ký tự đã đọc sai (`hard_*`) được lấy mẫu nhiều hơn (`--hard-weight`) → so với model đang chạy → ghi kết quả vào `status.json`.
Việc cài model do ParkingGuidance làm (sao lưu + nạp lại ngay). Mẫu bị xoá ở ParkingGuidance thì ký tự tương ứng cũng bị xoá khỏi kho ở lần chạy sau.
Tham số hay dùng: `--epochs 25`, `--threads 2`, `--min-auto-install 10`; `--skip-train --candidate <onnx>` chỉ để chẩn đoán.
