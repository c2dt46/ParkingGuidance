#!/usr/bin/env python3
"""
Huan luyen BO DO KY TU BIEN SO (YOLO11, 31 lop = 31 ky tu bien Viet Nam) tu cac mau "sua bien" o tab Train cua ung dung.

Thay cho cach doc co dien (nhi phan hoa -> tach thanh phan lien thong -> CNN phan loai): model YOLO nhin thang anh bien va tra ve vi tri + loai
tung ky tu, nen khong phu thuoc buoc tach ky tu (cho sai nhieu nhat khi bien nghieng, mo, chu dinh nhau hoac dinh khung vien).

    python tools/train_chars.py --corrections <DataDirectory>/simulation/corrections --out <thu muc lam viec> \
        [--deploy <DataDirectory>/char-det/char_det.onnx] [--base tools/yolo11n.pt] [--epochs 100] [--imgsz 320] [--device cpu] [--batch 16]

Du lieu: moi mau = anh bien (<id>.png) + chuoi dung (correct) trong index.json. Khung tung ky tu lay tu:
  1) cac o ky tu NGUOI DUNG khoanh tay (nut "Gan tung ky tu" o tab Train — nam trong truong meta.chars) — uu tien;
  2) neu khong co: tach ky tu co dien va CHI nhan khi so thanh phan == so ky tu cua chuoi dung (khop tuyet doi, gan nhan theo thu tu doc).
Nhung bien kho (nghieng, mo, dinh chu) thuong khong tu tach dung -> hay khoanh tay (nut tren) de model hoc dung cac ca nay.

Ket thuc: do ty le doc DUNG TRON bien tren tap kiem tra (cung quy tac xep dong cua YoloCharReader.cs), xuat ONNX va (neu co --deploy) chep vao
thu muc du lieu cua ung dung — ung dung tu nap lai trong vai chuc giay, khong can khoi dong lai.

--compare-tesseract: sau khi train, doc lai tap kiem tra bang Tesseract (bo phieu nhieu bien the anh) va so voi YOLO: ca hai cung dung / chi mot ben dung /
cung sai, khi hai ben dong y thi dung bao nhieu %. Dung de quyet dinh co nen bat "Ket hop Tesseract kiem chung" (Cau hinh chung) hay khong.
Can them: pip install pytesseract va cai chuong trinh Tesseract (--tesseract-cmd tro toi tesseract.exe neu khong nam trong PATH).

Yeu cau: pip install ultralytics   (giay phep AGPL-3.0 cua Ultralytics)
Khong lat ngang anh (fliplr = 0) vi ky tu bi lat guong khong con la ky tu do.
"""
import argparse
import collections
import hashlib
import json
import os
import re
import shutil
import sys

import cv2
import numpy as np

ALPHABET = "ABCDEFGHKLMNPRSTUVXYZ0123456789"      # PHAI trung bang ky tu trong YoloCharReader.cs / CnnPlateReader.cs
VAL_PERCENT = 20
LOW_CONTRAST_STD = 45.0


# --------------------------------------------------------------------------------------------- xep dong (giong YoloCharReader.GroupRows)
def group_rows(items):
    """items: list (cx, cy, h, payload). Tra ve list dong, moi dong sap theo cx. Tach 2 dong khi buoc nhay tam-Y lon nhat > 0.7 * chieu cao chu trung vi
    va moi dong >= 2 ky tu (bien 1 dong nghieng chi co cac buoc nhay Y nho nen khong bi tach nham)."""
    if not items:
        return []
    by_y = sorted(items, key=lambda t: t[1])
    med_h = float(np.median([t[2] for t in by_y]))
    split, best_gap = -1, 0.0
    for i in range(len(by_y) - 1):
        gap = by_y[i + 1][1] - by_y[i][1]
        if gap > best_gap:
            best_gap, split = gap, i
    if split >= 1 and len(by_y) - (split + 1) >= 2 and best_gap > 0.7 * med_h:
        return [sorted(by_y[:split + 1], key=lambda t: t[0]), sorted(by_y[split + 1:], key=lambda t: t[0])]
    return [sorted(by_y, key=lambda t: t[0])]


# --------------------------------------------------------------------------------------------- tach ky tu co dien (de gan nhan tu dong)
def _binarize(gray, adaptive, invert):
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    if not adaptive:
        _, b = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    else:
        blk = max(15, (min(gray.shape) // 2) | 1)
        b = cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, blk, 8)
    return cv2.bitwise_not(b) if invert else b


def _components(binary):
    h_img, w_img = binary.shape
    n, _, st, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    out = []
    for i in range(1, n):
        x, y, w, h, a = (int(v) for v in st[i])
        if a < 0.004 * h_img * h_img or h < 0.12 * h_img:
            continue
        if h > 0.9 * h_img and w > 0.8 * w_img:
            continue
        if a / float(w * h) < 0.25 or w > 1.2 * h:
            continue
        if x <= 0 or y <= 0 or x + w >= w_img or y + h >= h_img:      # khung vien / nhieu cham mep
            continue
        out.append((x, y, w, h))
    return out


def auto_label(img_bgr, text):
    """Tach ky tu co dien; tra ve [(x, y, w, h, cls)] theo toa do anh goc neu so thanh phan == len(text), nguoc lai None."""
    gray0 = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    f = min(max(110.0 / gray0.shape[0], 1.0), 6.0)
    gray = cv2.resize(gray0, None, fx=f, fy=f, interpolation=cv2.INTER_CUBIC)
    if gray.std() < LOW_CONTRAST_STD:
        gray = cv2.createCLAHE(2.0, (8, 8)).apply(gray)
    for adaptive in (False, True):
        for invert in (False, True):
            comps = _components(_binarize(gray, adaptive, invert))
            if len(comps) != len(text):
                continue
            hs = [c[3] for c in comps]
            med = float(np.median(hs))
            if any(h < 0.6 * med or h > 1.6 * med for h in hs):
                continue
            rows = group_rows([(x + w / 2.0, y + h / 2.0, h, (x, y, w, h)) for x, y, w, h in comps])
            ordered = [t[3] for row in rows for t in row]
            return [(x / f, y / f, w / f, h / f, ALPHABET.index(ch)) for (x, y, w, h), ch in zip(ordered, text)]
    return None


# --------------------------------------------------------------------------------------------- dung bo du lieu YOLO
def is_val(sample_id):
    return int(hashlib.sha1(sample_id.encode()).hexdigest()[:8], 16) % 100 < VAL_PERCENT


def meta_boxes(entry, img_w, img_h):
    """Cac o ky tu khoanh tay trong meta.chars -> [(x, y, w, h, cls)] theo toa do anh bien; None neu khong co/khong hop le."""
    try:
        meta = json.loads(entry.get("meta") or "null")
    except Exception:
        return None
    if not isinstance(meta, dict) or not isinstance(meta.get("chars"), list) or not meta["chars"]:
        return None
    mw = float(meta.get("w") or 0)
    scale = img_w / mw if mw > 0 else 1.0
    out = []
    for c in meta["chars"]:
        ch = str(c.get("c", "")).upper()
        if len(ch) != 1 or ch not in ALPHABET:
            return None
        x, y, w, h = (float(c[k]) * scale for k in ("x", "y", "w", "h"))
        x, y = max(0.0, x), max(0.0, y)
        w, h = min(w, img_w - x), min(h, img_h - y)
        if w < 1 or h < 1:
            return None
        out.append((x, y, w, h, ALPHABET.index(ch)))
    return out


def build_dataset(corr_dir, out_dir, min_plates=1):
    """Dung <out>/dataset (YOLO). Tra ve (duong dan data.yaml, thong ke, danh sach mau kiem tra [(duong dan anh, chuoi dung)])."""
    index_path = os.path.join(corr_dir, "index.json")
    if not os.path.exists(index_path):
        raise RuntimeError(f"Khong thay {index_path}")
    with open(index_path, "r", encoding="utf-8") as f:
        index = json.load(f)

    ds = os.path.join(out_dir, "dataset")
    if os.path.isdir(ds):
        shutil.rmtree(ds)
    for sp in ("train", "val"):
        os.makedirs(os.path.join(ds, "images", sp))
        os.makedirs(os.path.join(ds, "labels", sp))

    stats = {"entries": len(index), "hand": 0, "auto": 0, "skipped": 0, "train": 0, "val": 0, "chars": 0}
    val_items = []
    for e in index:
        sid, text = e.get("id", ""), (e.get("correct") or "").upper()
        path = os.path.join(corr_dir, sid + ".png")
        if not sid or not os.path.exists(path) or not text or any(ch not in ALPHABET for ch in text):
            stats["skipped"] += 1
            continue
        img = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
        if img is None:
            stats["skipped"] += 1
            continue
        h, w = img.shape[:2]

        boxes = meta_boxes(e, w, h)
        if boxes is not None:
            stats["hand"] += 1
        else:
            boxes = auto_label(img, text)
            if boxes is None:
                stats["skipped"] += 1           # khong tach dung -> can khoanh tay moi dung duoc
                continue
            stats["auto"] += 1

        sp = "val" if is_val(sid) else "train"
        cv2.imwrite(os.path.join(ds, "images", sp, sid + ".png"), img)
        with open(os.path.join(ds, "labels", sp, sid + ".txt"), "w", encoding="utf-8") as lf:
            for x, y, bw, bh, cls in boxes:
                lf.write(f"{cls} {(x + bw / 2) / w:.6f} {(y + bh / 2) / h:.6f} {bw / w:.6f} {bh / h:.6f}\n")
        stats[sp] += 1
        stats["chars"] += len(boxes)
        if sp == "val":
            val_items.append((os.path.join(ds, "images", sp, sid + ".png"), text))

    if stats["train"] + stats["val"] < min_plates:
        raise RuntimeError(f"Chi co {stats['train'] + stats['val']} bien dung duoc de train (can it nhat {min_plates}). "
                           f"Hay khoanh tay ky tu o tab Train cho cac bien kho.")
    if stats["val"] == 0 and stats["train"] > 1:        # it mau: chuyen 1 mau sang tap kiem tra de co so do
        name = sorted(os.listdir(os.path.join(ds, "images", "train")))[0]
        stem = os.path.splitext(name)[0]
        for kind, ext in (("images", ".png"), ("labels", ".txt")):
            shutil.move(os.path.join(ds, kind, "train", stem + ext), os.path.join(ds, kind, "val", stem + ext))
        stats["train"] -= 1
        stats["val"] = 1
        text = next((e.get("correct", "").upper() for e in index if e.get("id") == stem), "")
        val_items.append((os.path.join(ds, "images", "val", stem + ".png"), text))

    yaml_path = os.path.join(ds, "data.yaml")
    with open(yaml_path, "w", encoding="utf-8") as f:
        f.write(f"path: {ds.replace(os.sep, '/')}\ntrain: images/train\nval: images/val\nnames:\n")
        for i, ch in enumerate(ALPHABET):
            f.write(f"  {i}: {ch}\n")
    return yaml_path, stats, val_items


# --------------------------------------------------------------------------------------------- danh gia + huan luyen
def dets_to_text(dets):
    """dets: list (x1, y1, x2, y2, score, cls) -> chuoi (xep dong giong YoloCharReader)."""
    items = [((x1 + x2) / 2.0, (y1 + y2) / 2.0, y2 - y1, cls) for x1, y1, x2, y2, _, cls in dets]
    rows = group_rows(items)
    return "".join(ALPHABET[t[3]] for row in rows for t in row)


def yolo_read(model, path, imgsz, device):
    """Doc 1 anh bien bang model YOLO -> (chuoi, do tin cay trung binh)."""
    r = model.predict(path, imgsz=imgsz, conf=0.25, iou=0.5, agnostic_nms=True, device=device, verbose=False)[0]
    dets = [tuple(map(float, b.xyxy[0].tolist())) + (float(b.conf[0]), int(b.cls[0])) for b in r.boxes]
    conf = sum(d[4] for d in dets) / len(dets) if dets else 0.0
    return dets_to_text(dets), conf


def exact_match(model, val_items, imgsz, device):
    if not val_items:
        return None
    ok = sum(yolo_read(model, path, imgsz, device)[0] == text for path, text in val_items)
    return ok / len(val_items)


# --------------------------------------------------------------------------------------------- so sanh voi Tesseract
TESS_WHITELIST = "0123456789ABCDEFGHKLMNPRSTUVXYZ"
PLATE_RE = re.compile(r"\d{2}[A-Z][A-Z0-9]?\d{4,5}")      # dinh dang bien VN (khong tinh dau . -)


def tesseract_vote(img_bgr, pytesseract):
    """Tesseract doc nhieu bien the anh (3 co chieu cao x 4 kieu nhi phan) roi BO PHIEU cac chuoi hop dinh dang bien VN.
    Tra ve (Counter, tong so lan doc). Bien 2 dong dung PSM 6, 1 dong dung PSM 7."""
    h, w = img_bgr.shape[:2]
    psm = 6 if w / float(h) < 2.2 else 7
    gray0 = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    tally, total = collections.Counter(), 0
    for target in (60, 100, 160):
        gray = cv2.resize(gray0, None, fx=target / h, fy=target / h, interpolation=cv2.INTER_CUBIC)
        blur = cv2.GaussianBlur(gray, (3, 3), 0)
        variants = [
            gray,
            cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)[1],
            255 - cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)[1],
            cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 31, 8),
        ]
        for v in variants:
            v = cv2.copyMakeBorder(v, 20, 20, 30, 30, cv2.BORDER_CONSTANT, value=int(np.median(v)))
            raw = pytesseract.image_to_string(v, lang="eng", config=f"--psm {psm} -c tessedit_char_whitelist={TESS_WHITELIST}")
            text = "".join(ch for ch in raw if ch in TESS_WHITELIST)
            total += 1
            found = [m.group(0) for m in PLATE_RE.finditer(text)]
            if found:
                tally[max(found, key=len)] += 1
    return tally, total


def compare_with_tesseract(model, val_items, imgsz, device, tess_cmd):
    try:
        import pytesseract
        if tess_cmd:
            pytesseract.pytesseract.tesseract_cmd = tess_cmd
        pytesseract.get_tesseract_version()
    except Exception as ex:                                  # chua cai pytesseract / chuong trinh Tesseract
        print(f"[so sanh Tesseract] bo qua: {ex}")
        return
    st = collections.Counter()
    for path, text in val_items:
        img = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
        y, _ = yolo_read(model, path, imgsz, device)
        tally, total = tesseract_vote(img, pytesseract)
        t = tally.most_common(1)[0][0] if tally else ""
        yok, tok = y == text, t == text
        st["n"] += 1
        st["both"] += yok and tok
        st["yolo_only"] += yok and not tok
        st["tess_only"] += tok and not yok
        st["neither"] += not yok and not tok
        if y and t and y == t:
            st["agree"] += 1
            st["agree_right"] += yok
        elif y and t:
            st["disagree"] += 1
    n = max(1, st["n"])
    print(f"[so sanh Tesseract] {st['n']} bien val | YOLO dung {st['both'] + st['yolo_only']} ({(st['both'] + st['yolo_only']) * 100 // n}%), "
          f"Tesseract (da so phieu) dung {st['both'] + st['tess_only']} ({(st['both'] + st['tess_only']) * 100 // n}%)")
    print(f"    ca hai dung {st['both']} | chi YOLO dung {st['yolo_only']} | chi Tesseract dung {st['tess_only']} | ca hai sai {st['neither']}")
    if st["agree"]:
        print(f"    khi hai ben DONG Y ({st['agree']} bien): dung {st['agree_right'] * 100 // st['agree']}%")
    print("    -> 'chi Tesseract dung' cang lon thi ket hop cang co loi; gan 0 thi nen tat 'Ket hop Tesseract' va chi dung YOLO.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corrections", required=True, help="thu muc <DataDirectory>/simulation/corrections (index.json + <id>.png)")
    ap.add_argument("--out", required=True, help="thu muc lam viec (dataset + ket qua train)")
    ap.add_argument("--deploy", default="", help="neu co: chep char_det.onnx vao day (vd <DataDirectory>/char-det/char_det.onnx)")
    ap.add_argument("--base", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "yolo11n.pt"))
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--imgsz", type=int, default=320)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--min-plates", type=int, default=30, help="so bien toi thieu de train (mac dinh 30)")
    ap.add_argument("--dataset-only", action="store_true", help="chi dung bo du lieu YOLO roi dung (khong train)")
    ap.add_argument("--compare-tesseract", action="store_true", help="so sanh YOLO voi Tesseract tren tap val sau khi train")
    ap.add_argument("--tesseract-cmd", default="", help="duong dan tesseract.exe neu khong nam trong PATH")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    yaml_path, st, val_items = build_dataset(args.corrections, args.out, 1 if args.dataset_only else args.min_plates)
    print(f"[du lieu] {st['entries']} mau: khoanh tay {st['hand']}, tu gan nhan {st['auto']}, bo qua {st['skipped']} "
          f"(khong tach dung — hay khoanh tay) | train {st['train']}, val {st['val']}, tong {st['chars']} ky tu")
    if args.dataset_only:
        print(f"[xong] bo du lieu o {os.path.dirname(yaml_path)}")
        return

    from ultralytics import YOLO       # import tre de --dataset-only chay duoc khi chua cai ultralytics

    model = YOLO(args.base)
    model.train(
        data=yaml_path, epochs=args.epochs, imgsz=args.imgsz, batch=args.batch, device=args.device,
        workers=args.threads if args.threads > 0 else 2, project=args.out, name="run", exist_ok=True, patience=25,
        fliplr=0.0, flipud=0.0, mosaic=0.3, close_mosaic=10, degrees=6.0, shear=4.0, perspective=0.0008,
        translate=0.08, scale=0.35, hsv_s=0.4, hsv_v=0.4, erasing=0.0, plots=False, verbose=False,
    )
    best_pt = os.path.join(args.out, "run", "weights", "best.pt")
    if not os.path.exists(best_pt):
        sys.exit("Khong thay best.pt sau khi train.")

    best = YOLO(best_pt)
    acc = exact_match(best, val_items, args.imgsz, args.device)
    if acc is not None:
        print(f"[kiem tra] doc DUNG TRON {acc * 100:.1f}% tren {len(val_items)} bien cua tap val")

    if args.compare_tesseract:
        compare_with_tesseract(best, val_items, args.imgsz, args.device, args.tesseract_cmd)

    onnx_path = best.export(format="onnx", imgsz=args.imgsz, opset=12, simplify=False, dynamic=False)
    final = os.path.join(args.out, "char_det.onnx")
    shutil.copyfile(onnx_path, final)
    print(f"[xuat] {final}")
    if args.deploy:
        os.makedirs(os.path.dirname(os.path.abspath(args.deploy)), exist_ok=True)
        tmp = args.deploy + ".tmp"
        shutil.copyfile(final, tmp)
        os.replace(tmp, args.deploy)       # thay nguyen tu: ung dung khong bao gio doc phai file dang ghi do
        print(f"[trien khai] {args.deploy} — ung dung tu nap lai trong ~15 giay")


if __name__ == "__main__":
    main()
