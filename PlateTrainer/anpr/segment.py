"""Tách ký tự biển số — bản Python chuyển ĐÚNG từng bước từ CnnPlateReader.cs của dự án ParkingGuidance.

Mục đích: dữ liệu huấn luyện (ảnh ký tự 28x28) được cắt bằng đúng thuật toán mà ứng dụng C# dùng khi chạy thật,
nên model học trên đúng kiểu ảnh nó sẽ gặp (cùng cách nhị phân hoá, cắt khối dính, đệm vuông, resize INTER_AREA).
Nếu bạn sửa CnnPlateReader.cs (ngưỡng, thứ tự bước...) thì sửa file này tương ứng.
"""
from __future__ import annotations
from dataclasses import dataclass, field
import cv2
import numpy as np

ALPHABET = "ABCDEFGHKLMNPRSTUVXYZ0123456789"   # chỉ số 31 = nền / nhiễu (giống C#)
BACKGROUND_CLASS = 31
CLASS_NAMES = list(ALPHABET) + ["_bg"]          # tên thư mục lớp, theo thứ tự chỉ số
LOW_CONTRAST_STDDEV = 45.0
GAP_RATIO = 0.6
MAX_SLOPE_STEPS = 12
MIN_SINGLE_ROW_ASPECT = 2.2


@dataclass
class Cand:
    x: int
    y: int
    w: int
    h: int
    mask: np.ndarray = field(repr=False)   # ký tự = 255, kích thước h x w


# ------------------------------------------------------------------ tiền xử lý
def prepare_gray(plate_bgr: np.ndarray) -> np.ndarray:
    gray0 = cv2.cvtColor(plate_bgr, cv2.COLOR_BGR2GRAY) if plate_bgr.ndim == 3 else plate_bgr
    f = min(max(110.0 / max(1, gray0.shape[0]), 1.0), 6.0)
    gray = cv2.resize(gray0, None, fx=f, fy=f, interpolation=cv2.INTER_CUBIC)
    if float(gray.std()) < LOW_CONTRAST_STDDEV:
        gray = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)
    return gray


def binarize(gray: np.ndarray, adaptive: bool, invert: bool) -> np.ndarray:
    """Chữ = 255, nền = 0."""
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    if not adaptive:
        _, b = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    else:
        block = max(15, (min(gray.shape[0], gray.shape[1]) // 2) | 1)
        b = cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, block, 8)
    if invert:
        b = cv2.bitwise_not(b)
    return b


def strip_frame(binary: np.ndarray) -> np.ndarray:
    H, W = binary.shape
    n, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    best = None
    for i in range(1, n):
        x, y, w, h, a = stats[i]
        if w > 0.55 * W and h > 0.6 * H and a / float(w * h) < 0.45:
            if best is None or w * h > best[2] * best[3]:
                best = (x, y, w, h)
    if best is None:
        return binary.copy()
    x, y, w, h = best
    m = max(3, int(0.06 * h))
    ix, iy, iw, ih = x + m, y + m, w - 2 * m, h - 2 * m
    result = np.zeros_like(binary)
    if iw > 0 and ih > 0:
        result[iy:iy + ih, ix:ix + iw] = binary[iy:iy + ih, ix:ix + iw]
    return result


# ------------------------------------------------------------------ thành phần liên thông
def components(binary: np.ndarray) -> list[Cand]:
    H, W = binary.shape
    n, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    pieces: list[Cand] = []
    for i in range(1, n):
        x, y, w, h, area = [int(v) for v in stats[i]]
        if area < 0.004 * H * H or h < 0.12 * H:
            continue
        if h > 0.9 * H and w > 0.8 * W:
            continue
        mask = ((labels[y:y + h, x:x + w] == i).astype(np.uint8)) * 255
        if w > 1.2 * h and h >= 0.3 * H:
            ink = (mask > 0).sum(axis=0) > 0.12 * h
            x0 = -1
            for cx in range(w + 1):
                on = cx < w and ink[cx]
                if on and x0 < 0:
                    x0 = cx
                if (not on) and x0 >= 0:
                    rows = np.where((mask[:, x0:cx] > 0).any(axis=1))[0]
                    if rows.size:
                        min_y, max_y = int(rows[0]), int(rows[-1])
                        sub = mask[min_y:max_y + 1, x0:cx].copy()
                        pieces.append(Cand(x + x0, y + min_y, cx - x0, max_y - min_y + 1, sub))
                    x0 = -1
        else:
            pieces.append(Cand(x, y, w, h, mask))
    keep = []
    for c in pieces:
        fill = np.count_nonzero(c.mask) / float(c.w * c.h)
        if c.h >= 0.12 * H and fill >= 0.25 and c.w <= 1.2 * c.h:
            keep.append(c)
    return keep


# ------------------------------------------------------------------ gom dòng
def _median(v):
    v = sorted(v)
    n = len(v)
    return v[n // 2] if n % 2 == 1 else (v[n // 2 - 1] + v[n // 2]) / 2.0


def _cx(c): return c.x + c.w / 2.0
def _cy(c): return c.y + c.h / 2.0


def select_char_like(cands: list[Cand]):
    if not cands:
        return [], 0.0
    hs = [float(c.h) for c in cands]
    if len(cands) < 3:
        return list(cands), _median(hs)
    best_h, best_n = 0.0, -1
    for h in hs:
        n = sum(1 for k in hs if 0.75 * h <= k <= 1.25 * h)
        if n > best_n or (n == best_n and h < best_h):
            best_n, best_h = n, h
    ref_h = _median([k for k in hs if 0.75 * best_h <= k <= 1.25 * best_h])
    med_area = _median([float(c.w * c.h) for c in cands])
    kept = [c for c in cands if not ((c.h > 1.6 * ref_h and c.w < 0.35 * c.h)
                                     or float(c.w * c.h) > 4.0 * med_area
                                     or c.h < 0.5 * ref_h)]
    return kept, (_median([float(c.h) for c in kept]) if kept else ref_h)


def group_rows(cands: list[Cand], ref_h: float):
    """Trả (rows, two_rows)."""
    n = len(cands)
    if n < 2:
        return [], False
    cx = [_cx(c) for c in cands]
    cy = [_cy(c) for c in cands]
    mx = sum(cx) / n
    best_gap, best_s = -1.0, 0.0
    if n >= 4:
        steps = [0]
        for k in range(1, MAX_SLOPE_STEPS + 1):
            steps += [k, -k]
        for k in steps:
            s = k * 0.01
            ys = sorted(cy[i] - s * (cx[i] - mx) for i in range(n))
            g = 0.0
            for i in range(1, n - 2):
                g = max(g, ys[i + 1] - ys[i])
            if g > best_gap + 1e-9:
                best_gap, best_s = g, s
    if best_gap >= GAP_RATIO * ref_h:
        adj = [cy[i] - best_s * (cx[i] - mx) for i in range(n)]
        idx = sorted(range(n), key=lambda i: adj[i])
        rows, cur = [], [idx[0]]
        for t in range(1, len(idx)):
            if adj[idx[t]] - adj[idx[t - 1]] >= GAP_RATIO * ref_h:
                rows.append(cur)
                cur = []
            cur.append(idx[t])
        rows.append(cur)
        rows = [r for r in rows if len(r) >= 2]
        if len(rows) > 2:
            rows = sorted(sorted(rows, key=len, reverse=True)[:2], key=lambda r: min(adj[i] for i in r))
        return [sorted([cands[i] for i in r], key=lambda c: c.x) for r in rows], True
    x1 = min(c.x for c in cands); x2 = max(c.x + c.w for c in cands)
    y1 = min(c.y for c in cands); y2 = max(c.y + c.h for c in cands)
    if (x2 - x1) / max(1, y2 - y1) < MIN_SINGLE_ROW_ASPECT:
        return [], False
    return [sorted(cands, key=lambda c: c.x)], False


def refine_rows(rows: list[list[Cand]], ref_h: float):
    if len(rows) != 2:
        return rows
    allc = [c for r in rows for c in r]
    assign = {id(c): k for k in range(2) for c in rows[k]}
    a = [0.0, 0.0]
    s = 0.0
    for _ in range(3):
        groups = [[c for c in allc if assign[id(c)] == 0], [c for c in allc if assign[id(c)] == 1]]
        if len(groups[0]) < 2 or len(groups[1]) < 2:
            return rows
        mxs = [sum(_cx(c) for c in g) / len(g) for g in groups]
        mys = [sum(_cy(c) for c in g) / len(g) for g in groups]
        num = den = 0.0
        for k in range(2):
            for c in groups[k]:
                num += (_cx(c) - mxs[k]) * (_cy(c) - mys[k])
                den += (_cx(c) - mxs[k]) ** 2
        s = min(max(num / den if den > 1e-6 else 0.0, -0.25), 0.25)
        a[0] = mys[0] - s * mxs[0]
        a[1] = mys[1] - s * mxs[1]
        if a[1] - a[0] < GAP_RATIO * ref_h:
            return rows
        for c in allc:
            assign[id(c)] = 1 if abs(_cy(c) - (a[1] + s * _cx(c))) < abs(_cy(c) - (a[0] + s * _cx(c))) else 0
    out = [[], []]
    for c in allc:
        k = assign[id(c)]
        if abs(_cy(c) - (a[k] + s * _cx(c))) <= 0.5 * ref_h:
            out[k].append(c)
    res = []
    for r in out:
        if len(r) < 2:
            return rows
        med = _median([float(c.h) for c in r])
        f = sorted([c for c in r if 0.6 * med <= c.h <= 1.5 * med], key=lambda c: c.x)
        if len(f) < 2:
            return rows
        res.append(f)
    return res


def group_lines(cands: list[Cand]) -> list[list[Cand]]:
    kept, ref_h = select_char_like(cands)
    rows, two = group_rows(kept, ref_h)
    if not rows:
        return rows
    return refine_rows(rows, ref_h) if two else rows


# ------------------------------------------------------------------ ảnh 28x28 đưa vào CNN
def to_28(c: Cand) -> np.ndarray:
    s = max(c.w, c.h)
    sq = np.zeros((s, s), np.uint8)
    ox, oy = (s - c.w) // 2, (s - c.h) // 2
    sq[oy:oy + c.h, ox:ox + c.w] = c.mask
    return cv2.resize(sq, (28, 28), interpolation=cv2.INTER_AREA)


def mask_to_28(mask: np.ndarray) -> np.ndarray:
    """Mặt nạ ký tự bất kỳ (255 = mực) → 28x28 đúng kiểu C# (cắt sát mực rồi đệm vuông)."""
    ys, xs = np.where(mask > 0)
    if ys.size == 0:
        return np.zeros((28, 28), np.uint8)
    sub = mask[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    return to_28(Cand(0, 0, sub.shape[1], sub.shape[0], sub))


VARIANTS = [(False, False), (False, True), (True, False), (True, True)]   # (adaptive, invert) — thứ tự như C#


def segment_variant(gray: np.ndarray, adaptive: bool, invert: bool):
    """Trả về list các dòng, mỗi dòng là list Cand (hoặc None nếu không tách được)."""
    binary = strip_frame(binarize(gray, adaptive, invert))
    lines = group_lines(components(binary))
    return lines or None


def segment_plate(plate_bgr: np.ndarray, adaptive=False, invert=False):
    gray = prepare_gray(plate_bgr)
    return gray, segment_variant(gray, adaptive, invert)
