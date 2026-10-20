"""Sinh ảnh ký tự tổng hợp cho 32 lớp (31 ký tự + "nền").

Không vẽ ký tự trắng-trên-đen sạch sẽ (model sẽ chỉ giỏi trên ảnh sạch). Mỗi mẫu được vẽ lên nền biển (trắng/vàng/xanh/đỏ),
làm xấu giống camera thật (nghiêng, phối cảnh, giảm độ phân giải rồi phóng lại, mờ, nhiễu, JPEG), rồi cho đi qua
ĐÚNG phần tiền xử lý của ứng dụng (nhị phân hoá Otsu/adaptive, cắt sát mực, đệm vuông, resize 28x28 INTER_AREA).

Phông chữ: đặt file .ttf/.otf giống phông biển số VN vào thư mục fonts/ (càng giống thật càng tốt). Nếu không có,
dùng phông đậm có sẵn trong hệ thống — chạy được nhưng kém giống biển thật hơn.
"""
from __future__ import annotations
import glob
import os
import random
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from .segment import ALPHABET, binarize, mask_to_28

SYSTEM_FONT_HINTS = [
    "C:/Windows/Fonts/arialbd.ttf", "C:/Windows/Fonts/arialnb.ttf", "C:/Windows/Fonts/consolab.ttf",
    "C:/Windows/Fonts/calibrib.ttf", "C:/Windows/Fonts/tahomabd.ttf", "C:/Windows/Fonts/verdanab.ttf",
    "C:/Windows/Fonts/impact.ttf", "C:/Windows/Fonts/segoeuib.ttf",
    "/Library/Fonts/Arial Bold.ttf", "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed-Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSansNarrow-Bold.ttf",
    "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
]
# (nền BGR, chữ BGR)
STYLES = {
    "white": ((236, 238, 238), (24, 24, 24)),
    "yellow": ((45, 190, 232), (28, 28, 28)),
    "blue": ((165, 72, 28), (245, 245, 245)),
    "red": ((40, 40, 190), (245, 245, 245)),
}
STYLE_WEIGHTS = [("white", 0.62), ("yellow", 0.18), ("blue", 0.14), ("red", 0.06)]


def find_fonts(fonts_dir: str, user_only: bool = False) -> list[str]:
    """Danh sách phông để chọn ngẫu nhiên (phông lặp lại nhiều lần = được chọn nhiều hơn).

    Có phông biển số của bạn trong fonts/ (vd Soxe2banh.TTF): phông đó chiếm ~85% mẫu, phông hệ thống chỉ ~15% để tăng
    đa dạng (biển nước ngoài/dập tay, chữ in khác). user_only=True: dùng 100% phông của bạn."""
    found = []
    for ext in ("*.ttf", "*.otf", "*.TTF", "*.OTF"):
        found += glob.glob(os.path.join(fonts_dir, ext))
    user = sorted(set(os.path.normcase(os.path.abspath(f)) for f in found))
    system = [p for p in SYSTEM_FONT_HINTS if os.path.exists(p)]
    if not user:
        return system
    if user_only or not system:
        return user
    k = max(1, round(5.7 * len(system) / len(user)))
    return user * k + system


def _pick_style(rng: random.Random) -> str:
    r, acc = rng.random(), 0.0
    for name, w in STYLE_WEIGHTS:
        acc += w
        if r <= acc:
            return name
    return "white"


def _draw_char(ch: str, font_path: str, bg, fg, rng: random.Random, size=120) -> np.ndarray:
    """Vẽ 1 ký tự ở độ phân giải cao lên nền biển (ảnh BGR, kích thước 2.2x)."""
    font = ImageFont.truetype(font_path, size)
    S = int(size * 2.2)
    img = Image.new("RGB", (S, S), (bg[2], bg[1], bg[0]))
    dr = ImageDraw.Draw(img)
    l, t, r, b = dr.textbbox((0, 0), ch, font=font)
    dr.text(((S - (r - l)) / 2 - l, (S - (b - t)) / 2 - t), ch, font=font, fill=(fg[2], fg[1], fg[0]))
    return np.array(img)[:, :, ::-1].copy()


def _tight_bbox(img_bgr: np.ndarray, bg) -> tuple[int, int, int, int]:
    diff = np.abs(img_bgr.astype(int) - np.array(bg)[None, None, :]).sum(axis=2) > 120
    ys, xs = np.where(diff)
    if ys.size == 0:
        return 0, 0, img_bgr.shape[1], img_bgr.shape[0]
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def _warp(img: np.ndarray, rng: random.Random, strength: float) -> np.ndarray:
    h, w = img.shape[:2]
    ang = rng.uniform(-9, 9) * strength
    shear = rng.uniform(-0.18, 0.18) * strength
    sx = rng.uniform(0.8, 1.15)
    sy = rng.uniform(0.9, 1.1)
    M = cv2.getRotationMatrix2D((w / 2, h / 2), ang, 1.0)
    M = np.vstack([M, [0, 0, 1]])
    S = np.array([[sx, shear, (1 - sx) * w / 2 - shear * h / 2], [0, sy, (1 - sy) * h / 2], [0, 0, 1]])
    P = np.eye(3)
    P[2, 0] = rng.uniform(-6e-4, 6e-4) * strength
    P[2, 1] = rng.uniform(-6e-4, 6e-4) * strength
    out = cv2.warpPerspective(img, P @ S @ M, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    return out


def _degrade(img: np.ndarray, rng: random.Random, strength: float) -> np.ndarray:
    """Giảm độ phân giải rồi phóng lại + mờ + nhiễu + đổi sáng + JPEG (kiểu ảnh camera biển nhỏ/xa)."""
    h, w = img.shape[:2]
    q = rng.uniform(0.28, 1.0) if rng.random() < 0.8 else rng.uniform(0.15, 0.3)
    q = 1.0 - (1.0 - q) * strength
    small = cv2.resize(img, (max(4, int(w * q)), max(4, int(h * q))), interpolation=cv2.INTER_AREA)
    img = cv2.resize(small, (w, h), interpolation=cv2.INTER_CUBIC)
    if rng.random() < 0.6:
        k = rng.choice([3, 5, 7])
        img = cv2.GaussianBlur(img, (k, k), rng.uniform(0.3, 2.0) * strength + 0.01)
    if rng.random() < 0.35:                      # mờ chuyển động
        k = rng.choice([5, 9, 13])
        ker = np.zeros((k, k), np.float32)
        ker[k // 2, :] = 1.0 / k
        M = cv2.getRotationMatrix2D((k / 2 - 0.5, k / 2 - 0.5), rng.uniform(0, 180), 1)
        ker = cv2.warpAffine(ker, M, (k, k))
        ker /= max(ker.sum(), 1e-6)
        img = cv2.filter2D(img, -1, ker)
    f = img.astype(np.float32)
    f = f * rng.uniform(0.55, 1.2) + rng.uniform(-45, 45)                 # tương phản / độ sáng
    if rng.random() < 0.4:                                                # chiếu sáng không đều
        gx = np.linspace(rng.uniform(-45, 25), rng.uniform(-45, 25), w)[None, :, None]
        f = f + gx
    f += np.random.default_rng(rng.randrange(1 << 30)).normal(0, rng.uniform(0, 14) * strength, f.shape)
    img = np.clip(f, 0, 255).astype(np.uint8)
    if rng.random() < 0.5:
        _, enc = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, rng.randint(25, 85)])
        img = cv2.imdecode(enc, cv2.IMREAD_COLOR)
    return img


def _clean_mask(binary: np.ndarray) -> np.ndarray | None:
    """Giống bước 'thành phần liên thông' của C#: lấy mực của ký tự (bỏ mảnh vụn quá nhỏ)."""
    n, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    if n <= 1:
        return None
    H = binary.shape[0]
    keep = [i for i in range(1, n) if stats[i, 4] >= 0.004 * H * H]
    if not keep:
        return None
    big = max(keep, key=lambda i: stats[i, 4])
    x, y, w, h = stats[big, :4]
    out = np.zeros_like(binary)
    for i in keep:                                 # giữ cả dấu/nét rời sát ký tự chính
        xi, yi, wi, hi = stats[i, :4]
        if abs((xi + wi / 2) - (x + w / 2)) <= max(w, h) and abs((yi + hi / 2) - (y + h / 2)) <= max(w, h):
            out[labels == i] = 255
    return out


def _is_boxed(mask: np.ndarray) -> bool:
    """Mặt nạ là khung chữ nhật (nền biển có lỗ hình ký tự — Otsu nhầm phân cực): cả 4 cạnh của vùng cắt sát mực đều đầy mực."""
    ys, xs = np.where(mask > 0)
    if ys.size == 0:
        return False
    sub = mask[ys.min():ys.max() + 1, xs.min():xs.max() + 1] > 0
    if sub.shape[0] < 6 or sub.shape[1] < 4:
        return False
    edges = (sub[0, :], sub[-1, :], sub[:, 0], sub[:, -1])
    return all(float(e.mean()) >= 0.6 for e in edges)


def render_char_sample(ch: str, fonts: list[str], rng: random.Random, strength: float = 1.0) -> np.ndarray | None:
    style = _pick_style(rng)
    bg, fg = STYLES[style]
    bg = tuple(int(np.clip(c + rng.randint(-14, 14), 0, 255)) for c in bg)
    big = _draw_char(ch, rng.choice(fonts), bg, fg, rng)
    big = _warp(big, rng, strength)
    x0, y0, x1, y1 = _tight_bbox(big, bg)
    pad = rng.randint(4, 26)
    big = big[max(0, y0 - pad):y1 + pad, max(0, x0 - pad):x1 + pad]
    # chiều cao ký tự sau khi ứng dụng phóng biển lên cao 110px: ~38-70px
    target = rng.randint(34, 72)
    s = target / max(1, (y1 - y0))
    img = cv2.resize(big, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    img = _degrade(img, rng, strength)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    adaptive = rng.random() < 0.3
    dark_on_light = style in ("white", "yellow")
    binary = binarize(gray, adaptive, invert=dark_on_light)         # chữ = 255 (phân cực đúng: chữ tối trên nền sáng thì đảo)
    if not adaptive and binary.mean() > 200:                        # Otsu hỏng
        return None
    mask = _clean_mask(binary)
    if mask is None or _is_boxed(mask):
        return None
    s = mask_to_28(mask)
    corners = sum(int(s[y, x] > 0) for y, x in ((0, 0), (0, 27), (27, 0), (27, 27)))
    if np.count_nonzero(s) > 0.8 * 784 or corners >= 3:   # nhị phân hoá hỏng (khối đặc / khung bao quanh) -> bỏ
        return None
    sides = sum(int(np.count_nonzero(e) >= 0.6 * 28) for e in (s[0, :], s[-1, :], s[:, 0], s[:, -1]))
    if sides >= 3:                                          # viền chữ nhật bao quanh ký tự (Otsu nhầm nền) -> bỏ
        return None
    return s


# ------------------------------------------------------------------ lớp "nền"
def _bg_blob(rng: random.Random, fonts: list[str]) -> np.ndarray | None:
    S = rng.randint(40, 90)
    m = np.zeros((S, S), np.uint8)
    kind = rng.choice(["speck", "bar", "ring", "dot", "half", "frame", "hole", "stroke", "L", "stamp"])
    if kind == "speck":
        for _ in range(rng.randint(3, 14)):
            cv2.circle(m, (rng.randrange(S), rng.randrange(S)), rng.randint(1, 5), 255, -1)
    elif kind == "bar":
        w = rng.randint(2, 10)
        if rng.random() < 0.5:
            cv2.rectangle(m, (rng.randint(0, S - w), 0), (rng.randint(0, S - w) + w, rng.randint(S // 2, S)), 255, -1)
        else:
            cv2.rectangle(m, (0, rng.randint(0, S - w)), (rng.randint(S // 2, S), rng.randint(0, S - w) + w), 255, -1)
    elif kind == "ring":                                  # ốc vít / quốc huy nhỏ
        c = (S // 2 + rng.randint(-8, 8), S // 2 + rng.randint(-8, 8))
        r = rng.randint(4, S // 2 - 2)
        cv2.circle(m, c, r, 255, rng.choice([-1, 2, 3]))
    elif kind == "dot":
        cv2.circle(m, (S // 2, S // 2), rng.randint(2, 7), 255, -1)
    elif kind == "half":                                   # nửa ký tự
        ch = rng.choice(ALPHABET)
        sample = render_char_sample(ch, fonts, rng, 1.0)
        if sample is None:
            return None
        sample = cv2.resize(sample, (S, S))
        cut = rng.randint(S // 3, 2 * S // 3)
        part = rng.choice(["t", "b", "l", "r"])
        if part == "t": sample[cut:, :] = 0
        elif part == "b": sample[:cut, :] = 0
        elif part == "l": sample[:, cut:] = 0
        else: sample[:, :cut] = 0
        m = sample
    elif kind == "frame":                                  # mảnh viền biển
        t = rng.randint(2, 6)
        cv2.rectangle(m, (rng.randint(0, 6), rng.randint(0, 6)), (S - rng.randint(1, 6), S - rng.randint(1, 6)), 255, t)
        m[:, rng.randint(S // 2, S):] = 0
    elif kind == "hole":                                   # nền biển có lỗ hình ký tự (sai phân cực)
        ch = rng.choice(ALPHABET)
        sample = render_char_sample(ch, fonts, rng, 1.0)
        if sample is None:
            return None
        m = cv2.bitwise_not(sample)
    elif kind == "stroke":
        pts = np.array([[rng.randrange(S), rng.randrange(S)] for _ in range(rng.randint(2, 4))], np.int32)
        cv2.polylines(m, [pts], False, 255, rng.randint(1, 4))
    elif kind == "L":
        t = rng.randint(2, 7)
        cv2.line(m, (rng.randint(0, 10), 2), (rng.randint(0, 10), S - 3), 255, t)
        cv2.line(m, (rng.randint(0, 10), S - 3), (S - 3, S - 3), 255, t)
    else:                                                  # tem / hình tròn có chữ
        cv2.circle(m, (S // 2, S // 2), S // 2 - 3, 255, -1)
        for _ in range(rng.randint(2, 6)):
            cv2.circle(m, (rng.randrange(S // 4, 3 * S // 4), rng.randrange(S // 4, 3 * S // 4)), rng.randint(2, 6), 0, -1)
    if rng.random() < 0.5:
        m = cv2.GaussianBlur(m, (3, 3), 0)
        _, m = cv2.threshold(m, 100, 255, cv2.THRESH_BINARY)
    if np.count_nonzero(m) < 6:
        return None
    return mask_to_28(m)


def render_bg_sample(fonts: list[str], rng: random.Random) -> np.ndarray:
    for _ in range(10):
        s = _bg_blob(rng, fonts)
        if s is not None:
            return s
    return np.zeros((28, 28), np.uint8)


def generate(out_dir: str, fonts_dir: str, per_class: int = 400, bg_count: int = 600, seed: int = 1,
             strength: float = 1.0, progress=None, user_only: bool = False) -> dict:
    """Ghi PNG 28x28 vào out_dir/<lớp>/syn_*.png. Trả về thống kê."""
    from .segment import CLASS_NAMES
    fonts = find_fonts(fonts_dir, user_only)
    if not fonts:
        raise RuntimeError("Không tìm thấy phông chữ nào. Hãy đặt file .ttf vào thư mục fonts/.")
    rng = random.Random(seed)
    np.random.seed(seed)
    total = per_class * len(ALPHABET) + bg_count
    done, made, failed = 0, 0, 0
    for name in CLASS_NAMES:
        os.makedirs(os.path.join(out_dir, name), exist_ok=True)
    for ci, ch in enumerate(ALPHABET):
        folder = os.path.join(out_dir, ch)
        k = 0
        tries = 0
        while k < per_class and tries < per_class * 4:
            tries += 1
            s = render_char_sample(ch, fonts, rng, strength)
            if s is None:
                failed += 1
                continue
            cv2.imwrite(os.path.join(folder, f"syn_{seed}_{k:05d}.png"), s)
            k += 1
            made += 1
        done += per_class
        if progress:
            progress(done / total)
    folder = os.path.join(out_dir, "_bg")
    for k in range(bg_count):
        cv2.imwrite(os.path.join(folder, f"syn_{seed}_{k:05d}.png"), render_bg_sample(fonts, rng))
        made += 1
    if progress:
        progress(1.0)
    return {"fonts": len(set(fonts)), "created": made, "failed": failed}
