# Đo độ chính xác ANPR bằng ảnh tổng hợp

Có 2 bộ sinh ảnh kiểm thử (biết trước đáp án đúng, dùng để đo khách quan thay vì đoán):

- `synth_plate_dataset.py` — đa dạng chung: 1/2 dòng, trắng/vàng/xanh, nghiêng, mờ, nhiễu.
- `synth_plate_dataset_2line_realistic.py` — **riêng biển 2 dòng thực tế**: có khung viền sát
  chữ, tem/quốc huy nhỏ giữa 2 dòng, ốc vít 4 góc — đúng những yếu tố từng làm pipeline thất bại
  gần hết với biển 2 dòng (xem lỗi đã sửa bên dưới).

## Chạy (từ thư mục gốc dự án, cần `pip install -r tools/requirements.txt` + cài Tesseract)
```bash
python tools/bench/run_ocr_benchmark.py 80 5000          # bo tong hop chung
python tools/bench/run_ocr_benchmark_2line.py 30          # rieng bien 2 dong thuc te
```

## Kết quả gần nhất

Bộ tổng hợp chung (80 ảnh, seed 5000-5079, chưa từng dùng để tinh chỉnh):
```
all           68/ 80 =  85.0%
two=False     41/ 53 =  77.4%   (biển 1 dòng)
two=True      27/ 27 = 100.0%   (biển 2 dòng, không khung/tem — xem thêm bộ "thực tế" bên dưới)
style=white   45/ 54 =  83.3%
style=yellow  11/ 13 =  84.6%
style=blue    12/ 13 =  92.3%
```

Bộ biển 2 dòng thực tế (30 ảnh, có khung/tem/ốc vít):
```
29/30 = 96.7%
```

## Lỗi đã tìm và sửa: biển 2 dòng thất bại gần hết (30% → 96.7%)
Khi thêm khung viền + tem + ốc vít vào ảnh test (giống biển thật hơn), độ chính xác biển 2 dòng
rớt từ ~96% xuống **30%**. Nguyên nhân: bước tách ký tự dùng 1 ngưỡng chiều cao tối thiểu CHUNG
cho cả ảnh (`0.2 × chiều cao ảnh`) để lọc nhiễu — nhưng dòng 2 của biển 2 dòng thường có chữ
**thấp hơn đáng kể** so với dòng 1 (khác tỉ lệ font), nên toàn bộ ký tự dòng 2 bị loại bỏ ngay
từ đầu, chỉ còn đọc được dòng 1.

Đã sửa bằng 2 thay đổi trong `PlateTextReader.cs` (và bản Python tương đương
`tools/anpr_check.py`):
1. Loại khung viền/nét trang trí bằng **độ đặc (fill ratio = diện tích / (rộng×cao))** thay vì
   chỉ dựa vào kích thước — nét viền mỏng có độ đặc ~5-10%, ký tự đặc luôn ≥ ~25%.
2. **Gom các thành phần còn lại thành từng DÒNG theo vị trí Y trước**, rồi mới lọc theo chiều
   cao — dùng chiều cao trung vị của **từng dòng riêng**, không dùng 1 ngưỡng chung cho cả ảnh.


## Ghi chú thay đổi (v18): CLAHE cho ảnh ít tương phản
`anpr_check.py` (và `PlateTextReader.cs`) áp CLAHE trước khi nhị phân hoá khi độ lệch chuẩn mức xám < 45.
Bộ 40 ảnh seed 5000: 32/40 → 33/40 (80% → 82,5%), không ảnh nào kém đi. Mẫu nhỏ, chỉ mang tính tham khảo.
Lưu ý: bộ ảnh tổng hợp KHÔNG phản ánh camera thật; lỗi lớn nhất còn lại ở biển 1 dòng là Tesseract làm rơi 2 ký tự cuối
(ảnh chữ sau tách ký tự vẫn sạch) — hướng xử lý chính là bỏ phiếu nhiều khung và model OCR ONNX (README mục 14).
