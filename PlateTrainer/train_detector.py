"""(Tuỳ chọn) Huấn luyện model dò biển số YOLOv8 1 lớp → plate_detector.onnx cho Anpr:DetectorModelPath.

Cần: pip install ultralytics. Dữ liệu theo định dạng YOLO (images/ + labels/, mỗi dòng "0 cx cy w h" chuẩn hoá 0..1),
vẽ khung bằng LabelImg/CVAT/Roboflow. Ảnh nên là ẢNH XE ĐÃ CẮT (giống đầu vào OnnxPlateDetector) lấy từ camera thật.

    python train_detector.py --images D:\\bs\\images --labels D:\\bs\\labels --epochs 80
"""
import argparse, os, random, shutil

ap = argparse.ArgumentParser()
ap.add_argument("--images", required=True); ap.add_argument("--labels", required=True)
ap.add_argument("--epochs", type=int, default=80); ap.add_argument("--imgsz", type=int, default=640)
ap.add_argument("--out", default="runs/detector")
a = ap.parse_args()

work = os.path.join(a.out, "dataset")
imgs = [f for f in os.listdir(a.images) if f.lower().endswith((".jpg", ".jpeg", ".png", ".bmp"))]
random.Random(0).shuffle(imgs)
nval = max(1, int(len(imgs) * 0.15))
for split, names in (("val", imgs[:nval]), ("train", imgs[nval:])):
    for sub in ("images", "labels"):
        os.makedirs(os.path.join(work, sub, split), exist_ok=True)
    for f in names:
        shutil.copy(os.path.join(a.images, f), os.path.join(work, "images", split, f))
        lab = os.path.splitext(f)[0] + ".txt"
        if os.path.exists(os.path.join(a.labels, lab)):
            shutil.copy(os.path.join(a.labels, lab), os.path.join(work, "labels", split, lab))
yaml = os.path.join(work, "data.yaml")
open(yaml, "w").write(f"path: {os.path.abspath(work)}\ntrain: images/train\nval: images/val\nnames:\n  0: license_plate\n")

from ultralytics import YOLO
model = YOLO("yolov8n.pt")
model.train(data=yaml, epochs=a.epochs, imgsz=a.imgsz, project=os.path.abspath(a.out), name="train", exist_ok=True)
onnx = model.export(format="onnx", imgsz=a.imgsz, opset=12, simplify=True)
print("\nXong:", onnx, "\nChép thành ParkingGuidance.Web/Models/plate_detector.onnx (đúng tên Anpr:DetectorModelPath).")
