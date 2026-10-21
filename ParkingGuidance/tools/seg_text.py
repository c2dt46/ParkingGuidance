"""Tách ký tự biển số có DÙNG CHUỖI ĐÚNG đã biết (dùng khi gán nhãn tự động để train YOLO ký tự).

Khác auto_label cũ (chỉ nhận khi số thành phần == số ký tự): ở đây ép đủ N ký tự bằng
  - bóc khung viền theo hình chữ nhật xoay (chịu biển nghiêng, chữ dính khung),
  - cắt khối dính nhau theo cột ít mực gần vị trí chia đều,
  - gộp mảnh vỡ / bỏ nhiễu khi thừa,
  - thử cả 1 dòng và 2 dòng, 4 kiểu nhị phân hoá, chọn phương án ít "vá" nhất.
"""
import cv2
import numpy as np

MIN_H_FRAC = 0.12


def _binarize(gray, adaptive, invert):
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    if not adaptive:
        _, b = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    else:
        blk = max(15, (min(gray.shape) // 2) | 1)
        b = cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, blk, 8)
    return cv2.bitwise_not(b) if invert else b


def _peel_frames(binary, max_layers=3):
    """Xoá khung viền (và nền xe nối liền khung) bằng cách xoá dải quanh hình chữ nhật xoay của thành phần to, rỗng ruột."""
    b = binary.copy()
    H, W = b.shape
    for _ in range(max_layers):
        n, lab, st, _c = cv2.connectedComponentsWithStats(b, connectivity=8)
        best = None
        for i in range(1, n):
            x, y, w, h, a = (int(v) for v in st[i])
            if w > 0.55 * W and h > 0.55 * H and a / float(w * h) < 0.5:
                if best is None or w * h > best[1]:
                    best = (i, w * h)
        if best is None:
            break
        mask = (lab == best[0]).astype(np.uint8) * 255
        cnts, _h = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        c = max(cnts, key=cv2.contourArea)
        rect = cv2.minAreaRect(c)
        (rw, rh) = rect[1]
        if min(rw, rh) < 8:
            break
        poly = cv2.boxPoints(rect).astype(np.int32)
        filled = np.zeros_like(b)
        cv2.fillConvexPoly(filled, poly, 255)
        ring0_d = max(3, int(0.16 * min(rw, rh)))
        inner0 = cv2.erode(filled, cv2.getStructuringElement(cv2.MORPH_RECT, (2 * ring0_d + 1, 2 * ring0_d + 1)))
        ring0 = cv2.subtract(filled, inner0)
        area_in_ring = int(np.count_nonzero(cv2.bitwise_and(mask, ring0)))
        t = area_in_ring / max(1.0, 2.0 * (rw + rh))                 # bề dày khung ≈ diện tích / chu vi
        d = int(np.clip(np.ceil(1.5 * t) + 2, 3, 0.14 * min(rw, rh)))
        inner = cv2.erode(filled, cv2.getStructuringElement(cv2.MORPH_RECT, (2 * d + 1, 2 * d + 1)))
        ring = cv2.subtract(filled, inner)
        outside = cv2.bitwise_not(filled)
        kill = cv2.bitwise_or(ring, outside)
        b[(lab == best[0]) & (kill > 0)] = 0
        # đã xoá phần ngoài → lặp lại để bóc lớp khung kế tiếp nếu còn
    return b


def _strip_frame_open(binary):
    """Xoá nét MẢNH (khung viền, vạch nối) bằng phép mở hình tròn có đường kính ~5% chiều cao ảnh (nét chữ dày hơn nên còn),
    rồi dựng lại mép nét chữ bị bào bằng giãn có giới hạn trong vùng mực. Không phụ thuộc hình dạng khung nên chịu được biển nghiêng/hình thang."""
    H = binary.shape[0]
    d = max(3, int(round(0.05 * H)))
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (d, d))
    opened = cv2.morphologyEx(binary, cv2.MORPH_OPEN, k)
    rec = cv2.dilate(opened, k)
    return cv2.bitwise_and(rec, binary)


def _components(binary):
    H, W = binary.shape
    n, lab, st, _c = cv2.connectedComponentsWithStats(binary, connectivity=8)
    out = []
    for i in range(1, n):
        x, y, w, h, a = (int(v) for v in st[i])
        if h < MIN_H_FRAC * H or a < 0.003 * H * H:
            continue
        if w > 0.5 * W and h > 0.6 * H and a / float(w * h) < 0.45:
            continue
        if a / float(w * h) < 0.2:
            continue                                   # khung/nền nối liền: rỗng ruột
        m = (lab[y:y + h, x:x + w] == i).astype(np.uint8)
        out.append(dict(x=x, y=y, w=w, h=h, mask=m, area=a))
    return out


def _median(v):
    return float(np.median(v)) if len(v) else 0.0


def _ref_height(cs):
    hs = [c["h"] for c in cs]
    if len(hs) < 3:
        return _median(hs)
    best_h, best_n = 0, -1
    for h in hs:
        n = sum(1 for k in hs if 0.75 * h <= k <= 1.25 * h)
        if n > best_n or (n == best_n and h < best_h):
            best_n, best_h = n, h
    return _median([k for k in hs if 0.75 * best_h <= k <= 1.25 * best_h])


def _drop_noise(cs):
    if not cs:
        return cs
    ref = _ref_height(cs)
    med_a = _median([c["w"] * c["h"] for c in cs])
    keep = []
    for c in cs:
        if c["h"] < 0.5 * ref:
            continue
        if c["w"] < 0.15 * ref:
            continue                                  # mảnh mỏng sót lại của khung viền
        if c["h"] > 1.7 * ref and c["w"] < 0.35 * c["h"]:
            continue                                  # thanh dựng cao: viền/nhiễu
        keep.append(c)
    return keep


def _split_comp(c, k):
    """Cắt 1 khối thành k phần theo cột ít mực nhất gần các vị trí chia đều."""
    m = c["mask"]
    h, w = m.shape
    if k <= 1 or w < k * 2:
        return [c]
    ink = m.sum(axis=0).astype(float)
    ink = np.convolve(ink, np.ones(3) / 3.0, mode="same")
    cuts = []
    for j in range(1, k):
        centre = int(round(j * w / k))
        win = max(2, int(0.28 * w / k))
        lo, hi = max(1, centre - win), min(w - 1, centre + win + 1)
        seg = ink[lo:hi]
        if len(seg) == 0:
            return [c]
        cuts.append(lo + int(np.argmin(seg)))
    cuts = sorted(set(cuts))
    edges = [0] + cuts + [w]
    out = []
    for a, b in zip(edges[:-1], edges[1:]):
        if b - a < 2:
            continue
        sub = m[:, a:b]
        rows = np.where(sub.any(axis=1))[0]
        if len(rows) == 0:
            continue
        y0, y1 = int(rows[0]), int(rows[-1]) + 1
        out.append(dict(x=c["x"] + a, y=c["y"] + y0, w=b - a, h=y1 - y0, mask=sub[y0:y1].copy(), area=int(sub.sum())))
    return out


def _merge_pair(a, b):
    x0, y0 = min(a["x"], b["x"]), min(a["y"], b["y"])
    x1, y1 = max(a["x"] + a["w"], b["x"] + b["w"]), max(a["y"] + a["h"], b["y"] + b["h"])
    m = np.zeros((y1 - y0, x1 - x0), np.uint8)
    for c in (a, b):
        m[c["y"] - y0:c["y"] - y0 + c["h"], c["x"] - x0:c["x"] - x0 + c["w"]] |= c["mask"]
    return dict(x=x0, y=y0, w=x1 - x0, h=y1 - y0, mask=m, area=int(m.sum()))


def _fit_count(row, m, ref_h):
    """Ép 1 dòng về đúng m ký tự. Trả về (danh sách, số lần vá) hoặc None."""
    row = sorted(row, key=lambda c: c["x"])
    fixes = 0
    guard = 0
    while len(row) != m and guard < 40:
        guard += 1
        if len(row) < m:
            # cắt khối rộng nhất (so với chiều cao chữ)
            singles = [c["w"] for c in row if c["w"] <= 0.9 * ref_h]
            cw = _median(singles) if singles else 0.6 * ref_h
            cw = max(cw, 0.35 * ref_h)
            cand = max(row, key=lambda c: c["w"])
            k = int(np.clip(round(cand["w"] / cw), 2, m - len(row) + 1))
            if cand["w"] < 0.8 * cw * 1.0 and k < 2:
                return None
            parts = _split_comp(cand, k)
            if len(parts) < 2:
                return None
            row.remove(cand)
            row.extend(parts)
            row.sort(key=lambda c: c["x"])
            fixes += len(parts) - 1
        else:
            # thừa: bỏ mảnh nhỏ nhất nếu rất nhỏ, ngược lại gộp cặp gần nhất hợp lệ
            med_a = _median([c["area"] for c in row])
            small = min(row, key=lambda c: c["area"])
            if small["area"] < 0.3 * med_a:
                row.remove(small)
                fixes += 1
                continue
            best = None
            for i in range(len(row) - 1):
                a, b = row[i], row[i + 1]
                gap = b["x"] - (a["x"] + a["w"])
                if gap > 0.25 * ref_h:
                    continue
                mg = _merge_pair(a, b)
                if mg["h"] > 1.25 * ref_h or mg["w"] > 1.1 * ref_h:
                    continue
                if best is None or gap < best[0]:
                    best = (gap, i, mg)
            if best is None:
                return None
            _, i, mg = best
            row[i:i + 2] = [mg]
            fixes += 1
    if len(row) != m:
        return None
    return row, fixes


def _split_rows(cs, ref_h):
    """Chia 2 dòng bằng khoảng trống Y lớn nhất (đã hiệu chỉnh độ dốc). Mỗi dòng ≥ 1 khối (khối dính nhau sẽ được cắt sau)."""
    n = len(cs)
    if n < 2:
        return None
    cx = np.array([c["x"] + c["w"] / 2.0 for c in cs])
    cy = np.array([c["y"] + c["h"] / 2.0 for c in cs])
    mx = cx.mean()
    best = (-1.0, 0.0)
    slopes = [k * 0.01 for k in range(-14, 15)] if n >= 4 else [0.0]
    for s_ in slopes:
        ys = np.sort(cy - s_ * (cx - mx))
        g = float(np.max(np.diff(ys)))
        if g > best[0] + 1e-9:
            best = (g, s_)
    gap, s_ = best
    if gap < 0.5 * ref_h:
        return None
    adj = cy - s_ * (cx - mx)
    order = np.argsort(adj)
    d = np.diff(adj[order])
    cut = int(np.argmax(d)) + 1
    return [cs[i] for i in order[:cut]], [cs[i] for i in order[cut:]]


def _score_rows(rows, fixes, ref_h):
    """Càng thấp càng đáng tin: ít phải vá, chữ đều cao, bề rộng hợp lý, các tâm chữ thẳng hàng, bước chữ đều."""
    hs = [c["h"] for r in rows for c in r]
    sc = fixes * 1.0 + 3.0 * np.std(hs) / max(1.0, np.mean(hs))
    for r in rows:
        r = sorted(r, key=lambda c: c["x"])
        for c in r:
            a = c["w"] / float(c["h"])
            if a > 1.05 or a < 0.18:
                sc += 1.0
        if len(r) >= 3:
            cx = np.array([c["x"] + c["w"] / 2.0 for c in r]); cy = np.array([c["y"] + c["h"] / 2.0 for c in r])
            p = np.polyfit(cx, cy, 1)
            sc += 2.0 * float(np.sqrt(np.mean((cy - np.polyval(p, cx)) ** 2))) / max(1.0, ref_h)
            pitch = np.diff(cx)
            sc += 1.0 * float(np.std(pitch) / max(1.0, np.mean(pitch)))
    return sc


def _try_variant(binary, text, frame_mode):
    n = len(text)
    b = _peel_frames(binary) if frame_mode == "ring" else _strip_frame_open(binary)
    cs = _components(b)
    cs = _drop_noise(cs)
    if len(cs) < 2:
        return None
    ref = _ref_height(cs)
    best = None

    # giả thuyết 1 dòng
    r1 = _fit_count(list(cs), n, ref)
    if r1 is not None:
        row, fx = r1
        xs1, xs2 = min(c["x"] for c in row), max(c["x"] + c["w"] for c in row)
        ys1, ys2 = min(c["y"] for c in row), max(c["y"] + c["h"] for c in row)
        if (xs2 - xs1) / max(1.0, (ys2 - ys1)) >= 2.0:                   # 1 dòng phải dài
            best = (_score_rows([row], fx, ref), [row])

    # giả thuyết 2 dòng: dòng dưới 5 số (hoặc 4 số biển cũ)
    sp = _split_rows(cs, ref)
    if sp is not None:
        top, bot = sp
        for n2 in (5, 4):
            n1 = n - n2
            if n1 < 2 or n1 > 4:
                continue
            ra = _fit_count(top, n1, ref)
            rb = _fit_count(bot, n2, ref)
            if ra is None or rb is None:
                continue
            sc = _score_rows([ra[0], rb[0]], ra[1] + rb[1], ref)
            if best is None or sc < best[0]:
                best = (sc, [ra[0], rb[0]])

    if best is None:
        return None
    sc, rows = best
    # kiểm tra hợp lý: chiều cao trong từng dòng đồng đều, không chồng nhau quá nhiều
    for r in rows:
        med = _median([c["h"] for c in r])
        if any(c["h"] < 0.55 * med or c["h"] > 1.6 * med for c in r):
            return None
        r.sort(key=lambda c: c["x"])
        for a, b2 in zip(r[:-1], r[1:]):
            ov = (a["x"] + a["w"]) - b2["x"]
            if ov > 0.5 * min(a["w"], b2["w"]):
                return None
    return sc, rows


def segment_with_text(img_bgr, text, return_score=False):
    """Trả về [(x, y, w, h, chỉ số ký tự trong text)] theo toạ độ ảnh gốc, hoặc None nếu không tách đủ."""
    g0 = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    f = float(np.clip(130.0 / g0.shape[0], 1.0, 6.0))
    g = cv2.resize(g0, None, fx=f, fy=f, interpolation=cv2.INTER_CUBIC)
    if g.std() < 45:
        g = cv2.createCLAHE(2.0, (8, 8)).apply(g)
    best = None
    for adaptive in (False, True):
        for invert in (False, True):
            bin_ = _binarize(g, adaptive, invert)
            for mode in ("ring", "open"):
                res = _try_variant(bin_, text, mode)
                if res is not None and (best is None or res[0] < best[0]):
                    best = res
    if best is None:
        return (None, 9e9) if return_score else None
    score, rows = best
    out, idx = [], 0
    for r in rows:
        for c in r:
            out.append((c["x"] / f, c["y"] / f, c["w"] / f, c["h"] / f, idx))
            idx += 1
    return (out, score) if return_score else out
