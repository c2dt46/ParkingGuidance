#!/usr/bin/env python3
"""
Xuat model YOLO11 sang ONNX de ung dung dung (thay cho yolov8n.onnx).

    pip install ultralytics
    python tools/export_yolo11.py                  # yolo11n (nhanh nhat)
    python tools/export_yolo11.py yolo11s.pt       # chinh xac hon, cham hon

Ket qua: models/<ten>.onnx (cung dinh dang dau ra [1, 84, 8400] nhu YOLOv8). Neu dung model khac yolo11n, dat Detection:ModelPath
trong appsettings.json tro toi file moi. Ultralytics tu tai trong so .pt neu may co mang.
"""
import os
import shutil
import sys

weights = sys.argv[1] if len(sys.argv) > 1 else "yolo11n.pt"
imgsz = int(sys.argv[2]) if len(sys.argv) > 2 else 640

from ultralytics import YOLO

path = YOLO(weights).export(format="onnx", imgsz=imgsz, opset=12, simplify=False, dynamic=False)
dest_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "ParkingGuidance.Web", "models")
os.makedirs(dest_dir, exist_ok=True)
dest = os.path.join(dest_dir, os.path.splitext(os.path.basename(weights))[0] + ".onnx")
shutil.copy2(path, dest)
print("Da xuat:", os.path.normpath(dest))
