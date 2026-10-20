#!/usr/bin/env python3
"""
Huan luyen them (fine-tune) model nhan dien xe YOLO11 tu cac anh da gan khung o tab "Train xe" cua ung dung.

Duoc ung dung goi o MOT TIEN TRINH RIENG (uu tien thap), chi khi bam Train:
    python train_vehicle.py --samples <data/vehicle-train/samples> --out <data/vehicle-train/run> --status <train-status.json> \
        --base yolo11n.pt [--current <model dang chay .onnx/.pt>] [--epochs 30] [--imgsz 640] [--device cpu] [--batch 8] [--threads 0]

Quy trinh: dung bo du lieu YOLO tu index.json (CHI anh da duyet; chia train/val co dinh theo ma anh) -> do model hien tai tren tap val
-> fine-tune tu --base -> do model moi tren tap val -> xuat ONNX (best.onnx) + best.pt vao --out. Tien do ghi vao --status (JSON).

Giu nguyen 80 lop COCO cua model goc, chi gan nhan cho 4 lop xe (car=2, motorcycle=3, bus=5, truck=7), nen ONNX xuat ra cung dinh dang
[1, 84, 8400] nhu model goc va ung dung dung duoc ngay.

Yeu cau: pip install ultralytics   (giay phep AGPL-3.0 cua Ultralytics - xem README.md)
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
import time

# lop xe cua ung dung -> id lop COCO (giu nguyen dau 80 lop cua model goc)
COCO_IDS = {"car": 2, "motorcycle": 3, "bus": 5, "truck": 7}
COCO_NAMES = [
    "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light", "fire hydrant",
    "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
    "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball", "kite", "baseball bat",
    "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl",
    "banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
    "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave",
    "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
]
VAL_PERCENT = 20          # ~20% anh vao tap kiem tra (co dinh theo ma anh nen lan train sau khong bi lan)
MIN_VAL = 2


def write_status(path, **kw):
    """Ghi trang thai nguyen tu (ung dung doc de hien thi)."""
    data = {}
    try:
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
    except Exception:
        data = {}
    data.update(kw)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    os.replace(tmp, path)


def is_val(sample_id):
    return int(hashlib.sha1(sample_id.encode()).hexdigest()[:8], 16) % 100 < VAL_PERCENT


def build_dataset(samples_dir, out_dir):
    """Dung thu muc du lieu YOLO tu index.json. Tra ve (duong dan data.yaml, so lieu)."""
    with open(os.path.join(samples_dir, "index.json"), "r", encoding="utf-8") as f:
        index = json.load(f)
    reviewed = [e for e in index if e.get("reviewed")]
    if not reviewed:
        raise RuntimeError("Chua co anh nao da duyet.")

    ds = os.path.join(out_dir, "dataset")
    if os.path.isdir(ds):
        shutil.rmtree(ds)
    for split in ("train", "val"):
        os.makedirs(os.path.join(ds, "images", split))
        os.makedirs(os.path.join(ds, "labels", split))

    splits = {e["id"]: ("val" if is_val(e["id"]) else "train") for e in reviewed}
    if sum(1 for s in splits.values() if s == "val") < MIN_VAL and len(reviewed) >= 5:
        # qua it anh vao val: chuyen bot vai anh cuoi (theo ma anh) sang val de co so do dang tin
        for sid in sorted(splits)[:MIN_VAL]:
            splits[sid] = "val"
    if not any(s == "train" for s in splits.values()):
        for sid in list(splits)[:1]:
            splits[sid] = "train"

    counts = {"train": 0, "val": 0, "negatives": 0, "boxes": 0}
    for e in reviewed:
        sid, ext = e["id"], e.get("ext", ".jpg")
        src = os.path.join(samples_dir, sid + ext)
        if not os.path.exists(src):
            continue
        split = splits[sid]
        dst = os.path.join(ds, "images", split, sid + ext)
        try:
            os.link(src, dst)                 # nhanh, khong ton cho; khac o dia thi chep
        except OSError:
            shutil.copy2(src, dst)

        w, h = float(e.get("width") or 0), float(e.get("height") or 0)
        if w <= 0 or h <= 0:
            import cv2
            im = cv2.imread(src)
            h, w = im.shape[:2]
        lines = []
        for b in e.get("boxes", []):
            cid = COCO_IDS.get(b.get("cls"))
            if cid is None:
                continue
            x1, y1 = max(0.0, min(b["x1"], b["x2"])), max(0.0, min(b["y1"], b["y2"]))
            x2, y2 = min(w, max(b["x1"], b["x2"])), min(h, max(b["y1"], b["y2"]))
            if x2 - x1 < 2 or y2 - y1 < 2:
                continue
            lines.append(f"{cid} {(x1 + x2) / 2 / w:.6f} {(y1 + y2) / 2 / h:.6f} {(x2 - x1) / w:.6f} {(y2 - y1) / h:.6f}")
        with open(os.path.join(ds, "labels", split, sid + ".txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + ("\n" if lines else ""))       # file rong = anh khong co xe (mau am)
        counts[split] += 1
        counts["boxes"] += len(lines)
        if not lines:
            counts["negatives"] += 1

    yaml_path = os.path.join(ds, "data.yaml")
    with open(yaml_path, "w", encoding="utf-8") as f:
        f.write(f"path: {ds.replace(os.sep, '/')}\ntrain: images/train\nval: images/val\nnames:\n")
        for i, n in enumerate(COCO_NAMES):
            f.write(f"  {i}: {n}\n")
    return yaml_path, counts


def metrics_of(res):
    try:
        b = res.box
        return {"map50": float(b.map50), "map5095": float(b.map), "precision": float(b.mp), "recall": float(b.mr)}
    except Exception:
        return None


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--samples", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--status", required=True)
    ap.add_argument("--base", default="yolo11n.pt", help="trong so xuat phat (.pt)")
    ap.add_argument("--fallback-base", default="yolo11n.pt", help="trong so goc dung khi khong do duoc model hien tai")
    ap.add_argument("--current", default="", help="model dang chay (.onnx/.pt) de so sanh")
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--threads", type=int, default=0)
    return ap.parse_args(argv)


def main(argv=None):
    a = parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    started = time.strftime("%Y-%m-%d %H:%M:%S")
    write_status(a.status, state="preparing", message="Đang chuẩn bị dữ liệu…", epoch=0, epochs=a.epochs, startedAt=started)

    try:
        try:
            from ultralytics import YOLO
        except ImportError:
            raise RuntimeError("Chưa cài thư viện huấn luyện. Chạy: pip install ultralytics")

        if a.threads > 0:
            try:
                import torch
                torch.set_num_threads(a.threads)
            except Exception:
                pass

        data_yaml, counts = build_dataset(a.samples, a.out)
        write_status(a.status, images={"train": counts["train"], "val": counts["val"], "negatives": counts["negatives"], "boxes": counts["boxes"]})
        workers = max(1, min(4, a.threads if a.threads > 0 else 2))

        # 1) do model hien tai tren tap kiem tra (de biet model moi co tot hon that khong)
        write_status(a.status, state="evaluating-base", message="Đang đo model hiện tại trên tập kiểm tra…")
        base_metrics = None
        for cand in [c for c in (a.current, a.fallback_base) if c]:
            try:
                res = YOLO(cand).val(data=data_yaml, imgsz=a.imgsz, batch=a.batch, device=a.device, workers=workers, plots=False, verbose=False)
                base_metrics = metrics_of(res)
                if base_metrics:
                    break
            except Exception as ex:                      # thu tiep ung vien sau
                print(f"[train_vehicle] khong do duoc {cand}: {ex}", file=sys.stderr)
        write_status(a.status, metrics={"base": base_metrics})

        # 2) huan luyen
        write_status(a.status, state="training", message="Đang huấn luyện…")
        model = YOLO(a.base)

        def on_epoch_end(trainer):
            try:
                write_status(a.status, epoch=int(trainer.epoch) + 1, epochs=int(trainer.epochs),
                             message=f"Đang huấn luyện — epoch {int(trainer.epoch) + 1}/{int(trainer.epochs)}")
            except Exception:
                pass

        model.add_callback("on_fit_epoch_end", on_epoch_end)
        n_train = counts["train"]
        model.train(
            data=data_yaml, epochs=a.epochs, imgsz=a.imgsz, batch=a.batch, device=a.device, workers=workers,
            project=a.out, name="train", exist_ok=True, patience=10, plots=False, verbose=False,
            freeze=10 if n_train < 300 else 0,           # it anh: giu nguyen phan "mat" (backbone) de khoi quen
        )

        best_pt = os.path.join(a.out, "train", "weights", "best.pt")
        if not os.path.exists(best_pt):
            raise RuntimeError("Huấn luyện xong nhưng không thấy best.pt.")

        # 3) do model moi + xuat ONNX
        write_status(a.status, state="evaluating-new", message="Đang đo model mới trên tập kiểm tra…")
        best = YOLO(best_pt)
        new_metrics = metrics_of(best.val(data=data_yaml, imgsz=a.imgsz, batch=a.batch, device=a.device, workers=workers, plots=False, verbose=False))

        write_status(a.status, state="exporting", message="Đang xuất model ONNX…", metrics={"base": base_metrics, "new": new_metrics})
        onnx = best.export(format="onnx", imgsz=a.imgsz, opset=12, simplify=False, dynamic=False)
        shutil.copy2(str(onnx), os.path.join(a.out, "best.onnx"))
        shutil.copy2(best_pt, os.path.join(a.out, "best.pt"))

        write_status(a.status, state="done", message="Hoàn tất.", epoch=a.epochs, finishedAt=time.strftime("%Y-%m-%d %H:%M:%S"),
                     metrics={"base": base_metrics, "new": new_metrics}, onnx=True)
        return 0
    except Exception as ex:
        write_status(a.status, state="error", message=str(ex), error=str(ex), finishedAt=time.strftime("%Y-%m-%d %H:%M:%S"))
        print(f"[train_vehicle] loi: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
