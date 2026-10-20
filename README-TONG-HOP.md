# ParkingGuidance — gói đầy đủ

Gồm 2 phần dùng chung một thư mục:

| Thư mục | Là gì | Chạy thế nào |
|---|---|---|
| `ParkingGuidance/` | Dự án chính (ASP.NET 8): camera, nhận diện xe + biển số, bãi đỗ, LED, sensor. **Đã có tab 🧪 Mô phỏng** (Thiết bị → Camera) | Mở `ParkingGuidance/ParkingGuidance.sln` trong Visual Studio rồi build + chạy (hoặc `dotnet run` trong `src/ParkingGuidance.Web`) |
| `PlateTrainer/` | Công cụ huấn luyện lại CNN ký tự biển số (`char_cnn.onnx`), đã kèm phông biển số `Soxe2banh.TTF` | `PlateTrainer/install.bat` (một lần), rồi `PlateTrainer/run.bat` → http://127.0.0.1:5055 |

`PlateTrainer` đã được nối sẵn với dự án (`PlateTrainer/data/settings.json` trỏ `../ParkingGuidance`), nên nút **Cài vào dự án** ghi thẳng vào
`ParkingGuidance/src/ParkingGuidance.Web/Models/char_cnn.onnx` (tự sao lưu bản cũ thành `.bak-…`). Đổi vị trí thư mục thì chỉnh lại đường dẫn ở tab *Cài vào dự án*.

## Quy trình gợi ý (tự động, ngay trong ParkingGuidance)
1. Chạy ParkingGuidance → **Thiết bị → Camera → 🧪 Mô phỏng**, chọn thư mục ảnh xe: xem tất cả xe + biển số được nhận diện.
2. Biển nhỏ/xa: bật **✂️ Cắt/Zoom vùng**, quét chuột quanh biển để phóng to và nhận diện riêng vùng đó.
3. Biển đọc sai: gõ **Biển đúng** rồi Enter (đọc đúng thì bấm **✓ Đúng**). Làm vậy cho nhiều ảnh.
4. Bấm **🧠 Train** ở cuối trang: hệ thống tự dùng PlateTrainer để học lại từ các biển bạn đã cấp (đặc biệt ký tự đọc sai), so với model đang chạy, và nếu không kém (và có ≥ 10 mẫu) thì tự cài + nạp ngay, không cần khởi động lại. Bản cũ được sao lưu.
5. Chạy lại ảnh ở Mô phỏng để thấy model mới đọc ra sao.

Cách làm thủ công bằng giao diện riêng của PlateTrainer (Xử lý thô → Dữ liệu → Huấn luyện → Kiểm thử → Cài vào dự án) vẫn dùng được.

## Lưu ý
- Gói **không kèm `bin/`, `obj/`, `.vs/`** (thư mục sinh ra khi build, ~670 MB và là bản build CŨ chưa có tab Mô phỏng). Build lần đầu sẽ tự tạo lại.
  Model/tessdata và thư mục `led-bridge` vẫn đầy đủ.
- Tab Mô phỏng luôn nhường camera thật (hàng đợi ưu tiên thấp nhất) và tự giải phóng tài nguyên khi rời tab — chi tiết trong `ParkingGuidance/src/ParkingGuidance.Web/README.md`.
- Train chạy ở tiến trình Python riêng (ưu tiên thấp, ít luồng CPU, chỉ khi bấm Train) nên cần `PlateTrainer\\install.bat` đã chạy một lần (cài PyTorch). Cấu hình ở `appsettings.json` → `Trainer`.
- Chưa biên dịch C# và chưa chạy train PyTorch thật trong môi trường tạo gói này; nếu build/train báo lỗi, gửi lại thông báo lỗi để sửa.
