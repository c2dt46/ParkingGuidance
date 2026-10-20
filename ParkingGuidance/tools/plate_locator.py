import cv2, numpy as np

# (kernel blackhat, kernel dong hinh thai hoc) - 3 bo tham so: biển dài, biển vuong, biển nho
PASSES = [((25, 7), (25, 7)), ((17, 9), (13, 15)), ((13, 5), (15, 5))]

def _iou(a, b):
    ax, ay, aw, ah = a; bx, by, bw, bh = b
    x1, y1, x2, y2 = max(ax, bx), max(ay, by), min(ax+aw, bx+bw), min(ay+ah, by+bh)
    i = max(0, x2-x1) * max(0, y2-y1)
    return i / float(aw*ah + bw*bh - i + 1e-9)

def find_plate_candidates(crop, max_c=8):
    H, W = crop.shape[:2]
    if H < 20 or W < 20: return []
    scale = 600.0 / W
    img = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC)
    gray = cv2.GaussianBlur(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), (5, 5), 0)
    h2, w2 = gray.shape
    out = []
    for bk, ck in PASSES:
        for polarity in ("dark", "light"):       # chu toi tren nen sang (blackhat) hoac chu sang tren nen toi (tophat)
            op = cv2.MORPH_BLACKHAT if polarity == "dark" else cv2.MORPH_TOPHAT
            hat = cv2.morphologyEx(gray, op, cv2.getStructuringElement(cv2.MORPH_RECT, bk))
            g = np.absolute(cv2.Sobel(hat, cv2.CV_32F, 1, 0, ksize=-1))
            mn, mx = g.min(), g.max()
            g = (255 * (g - mn) / (mx - mn + 1e-9)).astype("uint8")
            g = cv2.GaussianBlur(g, (5, 5), 0)
            g = cv2.morphologyEx(g, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, ck))
            t_otsu, _ = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
            cnts = []
            for tf in (1.0, 0.55):                      # nguong Otsu va nguong thap hon (khi co vat the gradient rat manh nhu luoi tan nhiet)
                thr = cv2.threshold(g, t_otsu * tf, 255, cv2.THRESH_BINARY)[1]
                thr = cv2.dilate(cv2.erode(thr, None, iterations=2), None, iterations=3)
                cs, _ = cv2.findContours(thr, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                cnts.extend(cs)
            for c in cnts:
                x, y, w, h = cv2.boundingRect(c)
                if w < 40 or h < 20 or w > w2 * 0.6 or h > h2 * 0.6: continue
                ar = w / float(h)
                if not (0.8 <= ar <= 6.5): continue
                fill = cv2.contourArea(c) / float(w * h)
                if fill < 0.35: continue
                pos = 1.0 + 0.6 * (y + h / 2) / h2
                cen = 1.0 - abs((x + w / 2) - w2 / 2) / w2
                score = np.sqrt(w * h) * fill * pos * (0.4 + cen)
                padx, pady = int(w * 0.06), int(h * 0.10)
                x0 = max(0, int((x - padx) / scale)); y0 = max(0, int((y - pady) / scale))
                x1 = min(W, int((x + w + padx) / scale)); y1 = min(H, int((y + h + pady) / scale))
                out.append(((x0, y0, x1 - x0, y1 - y0), score))
    out.sort(key=lambda t: -t[1])
    kept = []
    for r, s in out:
        if all(_iou(r, k) < 0.5 for k in kept):
            kept.append(r)
        if len(kept) >= max_c: break
    return kept
