"""Doc bien so o to Viet Nam - ban Python tuong duong PlateLocator/PlateTextReader/
TesseractPlateRecognitionService (C#). Phien ban da kiem chung tot nhat (xem tools/bench/):
85% tong the tren bo anh da dang, 100% tren bo bien 2 dong thuc te (co khung/tem/oc vit).

Dung: python tools/anpr_check.py anh.jpg
Yeu cau: pip install -r tools/requirements.txt  +  cai Tesseract OCR
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cv2, numpy as np, pytesseract
from detect_check import detect, CLASSES, TESS
from plate_locator import find_plate_candidates
from vn_plate_formatter import normalize_vn_plate

WL = "ABCDEFGHKLMNPRSTUVXYZ0123456789"


def to_binary(gray, adaptive, invert):
    """Chu = 255, nen = 0."""
    g = cv2.GaussianBlur(gray, (3, 3), 0)
    if not adaptive:
        b = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)[1]
    else:
        bs = max(15, (min(gray.shape) // 2) | 1)
        b = cv2.adaptiveThreshold(g, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, bs, 8)
    return cv2.bitwise_not(b) if invert else b


def char_boxes(binary):
    """Tach cac thanh phan lien thong thuoc VUNG CHU: loai khung vien theo DO DAC (fill ratio,
    khong chi kich thuoc), gom thanh TUNG DONG theo vi tri Y roi moi loc chieu cao THEO TRUNG VI
    CUA TUNG DONG RIENG (quan trong: bien 2 dong co the co 2 dong chieu cao chu khac nhau dang ke)."""
    H, W = binary.shape
    n, lab, st, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    cand = []
    for i in range(1, n):
        x, y, w, h, area = st[i]
        if area < 0.004 * H * H:
            continue
        fill = area / float(w * h)
        cand.append((i, x, y, w, h, fill))

    plausible = [c for c in cand if c[5] >= 0.25 and c[3] <= 4 * c[4]]
    if not plausible:
        return lab, [], []

    plausible.sort(key=lambda c: c[2] + c[4] / 2)
    lines = [[plausible[0]]]
    for c in plausible[1:]:
        cy = c[2] + c[4] / 2
        line_cy = np.mean([k[2] + k[4] / 2 for k in lines[-1]])
        line_h = np.mean([k[4] for k in lines[-1]])
        if abs(cy - line_cy) < 0.7 * max(line_h, 1):
            lines[-1].append(c)
        else:
            lines.append([c])

    if len(lines) > 2:
        lines.sort(key=len, reverse=True)
        lines = lines[:2]
        lines.sort(key=lambda ln: np.mean([c[2] for c in ln]))

    chars = []
    for line in lines:
        if len(line) < 2:
            continue
        med = np.median([c[4] for c in line])
        for c in line:
            if 0.5 * med <= c[4] <= 1.8 * med:
                chars.append(c)

    keep_ids = [c[0] for c in chars]
    boxes = [(c[1], c[2], c[3], c[4]) for c in chars]
    return lab, keep_ids, boxes


def clean_render(binary, lab, keep, chars):
    ids = set(keep)
    mask = np.isin(lab, list(ids)).astype(np.uint8) * 255
    ys, xs = np.where(mask > 0)
    x1, x2, y1, y2 = xs.min(), xs.max(), ys.min(), ys.max()
    return cv2.bitwise_not(mask[max(0, y1 - 4):y2 + 5, max(0, x1 - 4):x2 + 5])


def deskew(clean):
    mask = (clean < 128).astype(np.uint8) * 255
    h, w = mask.shape
    if h < 10 or w < 10:
        return clean
    small = cv2.resize(mask, None, fx=0.5, fy=0.5, interpolation=cv2.INTER_AREA) if w > 400 else mask
    best, best_a = -1.0, 0.0
    for a in np.arange(-10, 10.1, 1.0):
        M = cv2.getRotationMatrix2D((small.shape[1] / 2, small.shape[0] / 2), a, 1)
        r = cv2.warpAffine(small, M, (small.shape[1], small.shape[0]))
        v = float(np.var(r.sum(axis=1).astype(np.float64)))
        if v > best:
            best, best_a = v, a
    if abs(best_a) < 1.0:
        return clean
    pad = int(abs(np.sin(np.radians(best_a))) * w / 2) + 8
    big = cv2.copyMakeBorder(clean, pad, pad, 0, 0, cv2.BORDER_CONSTANT, value=255)
    M = cv2.getRotationMatrix2D((big.shape[1] / 2, big.shape[0] / 2), best_a, 1)
    rot = cv2.warpAffine(big, M, (big.shape[1], big.shape[0]), flags=cv2.INTER_CUBIC, borderValue=255)
    ys, xs = np.where(rot < 128)
    if len(ys) == 0:
        return clean
    return rot[max(0, ys.min() - 4):ys.max() + 5, max(0, xs.min() - 4):xs.max() + 5]


def ocr_line_with_linenum(img, two):
    """OCR + lay DO DAI TUNG DONG that tu line_num cua Tesseract (khong doan theo tong do dai)."""
    hh = img.shape[0]
    target = 140.0 if two else 80.0
    im = cv2.resize(img, None, fx=target / hh, fy=target / hh, interpolation=cv2.INTER_CUBIC)
    im = cv2.copyMakeBorder(im, 12, 12, 12, 12, cv2.BORDER_CONSTANT, value=255)
    psm = 6 if two else 7
    cfg = f'{TESS} --psm {psm} -c tessedit_char_whitelist={WL} -c load_system_dawg=0 -c load_freq_dawg=0'
    d = pytesseract.image_to_data(im, config=cfg, output_type=pytesseract.Output.DICT)
    items = [(t, float(c), ln) for t, c, ln in zip(d['text'], d['conf'], d['line_num']) if t.strip() and float(c) >= 0]
    if not items:
        return None, 0, None

    text = ''.join(t for t, _, _ in items)
    conf = sum(c for _, c, _ in items) / len(items)

    lines_seen = sorted(set(ln for _, _, ln in items))
    line_lengths = None
    if len(lines_seen) == 2:
        l1 = sum(len(t) for t, _, ln in items if ln == lines_seen[0])
        l2 = sum(len(t) for t, _, ln in items if ln == lines_seen[1])
        line_lengths = [l1, l2]
    return text, conf, line_lengths


def read_plate_votes(plate_bgr, votes):
    gray0 = cv2.cvtColor(plate_bgr, cv2.COLOR_BGR2GRAY)
    f = np.clip(110.0 / max(1, gray0.shape[0]), 1.0, 6.0)
    gray = cv2.resize(gray0, None, fx=f, fy=f, interpolation=cv2.INTER_CUBIC)
    if gray.std() < 45:   # anh it tuong phan (nguoc sang/toi/mo): can bang tuong phan cuc bo
        gray = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)

    for mode in ("otsu", "adaptive"):
        for inv in (False, True):
            b = to_binary(gray, mode == "adaptive", inv)
            lab, keep, chars = char_boxes(b)
            if not chars:
                continue
            img0 = deskew(clean_render(b, lab, keep, chars))
            two = img0.shape[1] / img0.shape[0] < 2.2
            for thin in (0, 1):
                im = img0 if not thin else cv2.dilate(img0, np.ones((3, 3), np.uint8))
                raw, conf, line_lengths = ocr_line_with_linenum(im, two)
                if not raw:
                    continue
                pretty, ok = normalize_vn_plate(raw, line_lengths if two else None)
                if not ok:
                    continue
                key = re.sub(r'[^A-Z0-9]', '', pretty)
                if key not in votes:
                    votes[key] = [0.0, pretty]
                votes[key][0] += 1.0 + conf / 100.0


def recognize(vehicle_crop, max_cands=5, early=3.2):
    """Tra ve (bien_so_dep, diem_phieu, khung_bien_so) hoac None."""
    votes = {}
    best = None
    for (x, y, w, h) in find_plate_candidates(vehicle_crop, max_c=max_cands):
        if w > 0.6 * vehicle_crop.shape[1]:
            continue
        crop = vehicle_crop[y:y + h, x:x + w]
        ph, pw = int(crop.shape[0] * 0.12), int(crop.shape[1] * 0.12)
        padded = cv2.copyMakeBorder(crop, ph, ph, pw, pw, cv2.BORDER_REPLICATE)
        read_plate_votes(padded, votes)
        read_plate_votes(crop, votes)
        if votes:
            top = max(votes, key=lambda k: votes[k][0])
            score, pretty = votes[top]
            if best is None or score > best[1]:
                best = (pretty, score, (x, y, w, h))
            if best[1] >= early:
                break
    return best


if __name__ == "__main__":
    img = cv2.imread(sys.argv[1])
    for i, b in enumerate(sorted(detect(img), key=lambda b: b[0]), 1):
        x1, y1, x2, y2 = [max(0, int(v)) for v in b[:4]]
        r = recognize(img[y1:y2, x1:x2])
        if r:
            print(f"#{i} {CLASSES[b[4]]:5s} -> {r[0]}   (diem phieu {r[1]:.1f})")
        else:
            print(f"#{i} {CLASSES[b[4]]:5s} -> Khong doc duoc")
