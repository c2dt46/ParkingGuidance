"""Phat hien o to bang YOLOv8 ONNX - ban Python tuong duong voi YoloVehicleDetectionService.cs.
Dung de kiem tra nhanh model tren 1 anh: python tools/detect_check.py anh.jpg
"""
import cv2, numpy as np, onnxruntime as ort, pytesseract, sys, re

import os
HERE = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.join(HERE, "..", "src", "ParkingGuidance.Web")
MODEL = os.path.join(WEB, "models", "yolov8n.onnx")
TESS = '--tessdata-dir "%s"' % os.path.abspath(os.path.join(WEB, "tessdata"))
CLASSES = {2: "car", 5: "bus", 7: "truck"}

sess = ort.InferenceSession(MODEL)
inp = sess.get_inputs()[0]
print("input:", inp.name, inp.shape, "| output:", sess.get_outputs()[0].shape)

def letterbox(img, size=640):
    h, w = img.shape[:2]
    s = min(size / w, size / h)
    nw, nh = int(w * s), int(h * s)
    px, py = (size - nw) / 2, (size - nh) / 2
    canvas = np.full((size, size, 3), 114, np.uint8)
    canvas[int(py):int(py) + nh, int(px):int(px) + nw] = cv2.resize(img, (nw, nh))
    blob = canvas[:, :, ::-1].transpose(2, 0, 1).astype(np.float32) / 255.0
    return blob[None], s, px, py

def detect(img, thr=0.25):
    blob, s, px, py = letterbox(img)
    out = sess.run(None, {inp.name: blob})[0][0]  # [84, 8400]
    # Model co the tra toa do chuan hoa 0-1 (thay vi pixel 0-640): tu nhan biet va doi ve pixel
    scale = 640.0 if out[2].max() <= 2.0 and out[3].max() <= 2.0 else 1.0
    boxes = []
    for i in range(out.shape[1]):
        cls_scores = out[4:, i]
        c = int(np.argmax(cls_scores)); sc = float(cls_scores[c])
        if sc < thr or c not in CLASSES: continue
        cx, cy, w, h = out[0:4, i] * scale
        boxes.append(((cx - w/2 - px)/s, (cy - h/2 - py)/s, (cx + w/2 - px)/s, (cy + h/2 - py)/s, c, sc))
    # NMS
    boxes.sort(key=lambda b: -b[5]); keep = []
    def iou(a, b):
        x1, y1, x2, y2 = max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])
        i = max(0, x2-x1) * max(0, y2-y1)
        return i / ((a[2]-a[0])*(a[3]-a[1]) + (b[2]-b[0])*(b[3]-b[1]) - i + 1e-9)
    for b in boxes:
        if all(not (b[4] == k[4] and iou(b, k) > 0.5) for k in keep): keep.append(b)
    return keep

if __name__ == "__main__":
    img = cv2.imread(sys.argv[1])
    print("image:", img.shape)
    for b in detect(img):
        print("  vehicle", CLASSES[b[4]], "conf=%.2f" % b[5], [int(v) for v in b[:4]])
