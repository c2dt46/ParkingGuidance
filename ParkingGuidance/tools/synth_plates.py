#!/usr/bin/env python3
"""Sinh anh bien so Viet Nam GIA LAP (1 dong / 2 dong, nhieu mau nen, nghieng, mo, nhieu, vit, vien khung) kem chuoi dung.
Dung de bo sung du lieu cho tools/train_plate_ocr.py khi mau that con it. Chay doc lap de xem thu:  python synth_plates.py --preview preview.png

Font: tu tim trong C:\\Windows\\Fonts (Arial Narrow Bold, Arial Bold, Calibri Bold ...) va mot so duong dan Linux; them font rieng bang --fonts <thu muc>.
Font cang giong font bien that (chu day, hep) thi model hoc cang sat thuc te: nen chep 1-2 file font bien so (neu co) vao thu muc --fonts.
"""
import argparse
import glob
import os
import random
import re
import string

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

# Ma tinh/thanh pho dang su dung tren bien so Viet Nam (dung de sinh bien hop le, khong sinh ma khong ton tai)
PROVINCES = ([11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 43, 47, 48, 49]
             + list(range(50, 91)) + [92, 93, 94, 95, 97, 98, 99])
LETTERS = "ABCDEFGHKLMNPRSTUVXYZ"      # 21 chu cai seri bien o to / xe may thuong gap
ALL_LETTERS = string.ascii_uppercase  # bien quan doi, cong an, ngoai giao... co the dung ca I, J, O, Q, W

_WIN_FONTS = ["ARIALNB.TTF", "arialbd.ttf", "calibrib.ttf", "segoeuib.ttf", "tahomabd.ttf", "verdanab.ttf", "trebucbd.ttf", "ARLRDBD.TTF"]
_LINUX_FONTS = ["/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed-Bold.ttf", "/usr/share/fonts/truetype/liberation/LiberationSansNarrow-Bold.ttf",
                "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]

# (nen, chu): bien trang, bien vang (xe kinh doanh), bien xanh (co quan), bien do (quan doi)
STYLES = {"white": ((238, 238, 235), (22, 22, 22)), "yellow": ((232, 190, 45), (25, 25, 25)),
          "blue": ((28, 72, 165), (245, 245, 245)), "red": ((190, 35, 35), (245, 245, 245))}
STYLE_WEIGHTS = [0.62, 0.20, 0.13, 0.05]


def find_fonts(extra_dir=None):
    found = []
    dirs = [os.environ.get("WINDIR", r"C:\Windows") + r"\Fonts"]
    if extra_dir:
        found += sorted(glob.glob(os.path.join(extra_dir, "*.ttf")) + glob.glob(os.path.join(extra_dir, "*.otf")))
    for d in dirs:
        for n in _WIN_FONTS:
            p = os.path.join(d, n)
            if os.path.exists(p):
                found.append(p)
    found += [p for p in _LINUX_FONTS if os.path.exists(p)]
    return found


def rand_special(rng):
    """Bien KHONG theo dang thuong (quan doi, cong an, ngoai giao, xe dac biet...). Chua co quy cach chinh xac nen sinh nhieu kieu ngau nhien."""
    L = lambda n: "".join(rng.choice(ALL_LETTERS) for _ in range(n))
    D = lambda n: "".join(rng.choice("0123456789") for _ in range(n))
    kind = rng.randrange(7)
    if kind == 0: return L(2) + D(4)                       # kieu AA1234
    if kind == 1: return L(2) + D(5)
    if kind == 2: return D(2) + L(2) + D(3)                # kieu 80NG123
    if kind == 3: return D(2) + L(2) + D(4)
    if kind == 4: return L(1) + D(rng.randint(4, 5))
    if kind == 5: return D(2) + L(1) + D(rng.randint(4, 5))
    return "".join(rng.choice(ALL_LETTERS + "0123456789") for _ in range(rng.randint(3, 9)))


def rand_text(rng):
    """Chuoi bien (khong dau - cham): 8 ky tu (NNLddddd) la chinh; co them 9 ky tu (series chu+so / 2 chu), bien cu 7 ky tu, va ~15% bien khong theo dang thuong."""
    if rng.random() < 0.15:
        return rand_special(rng)
    pv = "%02d" % rng.choice(PROVINCES)
    r = rng.random()
    if r < 0.66:   series = rng.choice(LETTERS)
    elif r < 0.86: series = rng.choice(LETTERS) + str(rng.randint(1, 9))
    elif r < 0.94: series = rng.choice(LETTERS) + rng.choice(LETTERS)
    else:          series = rng.choice(LETTERS)
    n = 4 if rng.random() < 0.05 else 5
    return pv + series + "".join(str(rng.randint(0, 9)) for _ in range(n))


def _lines(text, two, rng):
    """Chia thanh cac dong hien thi, them dau '-' va '.' nhu bien that (chi de VE; nhan chi gom chu va so)."""
    if len(text) < 6:
        return [text]
    n = 5 if len(text) >= 8 else 4
    head, serial = text[:-n], text[-n:]
    if not re.match(r"^\d{2}[A-Z][A-Z0-9]?\d{4,5}$", text):      # khong phai dang o to / xe may thuong: khong them dau - va cham
        return [head, serial] if two else [text]
    dot = (serial[:3] + "." + serial[3:]) if n == 5 else serial
    if two:
        return [(head[:2] + "-" + head[2:]) if rng.random() < 0.7 else head, dot]
    return [head[:2] + head[2:] + "-" + dot if rng.random() < 0.8 else head + dot]


def make_plate(seed, fonts, text=None):
    """Tra ve (anh BGR uint8, chuoi dung, la_bien_2_dong)."""
    rng = random.Random(seed)
    rs = np.random.RandomState(seed % (2 ** 31))
    text = text or rand_text(rng)
    two = rng.random() < 0.45 and len(text) >= 6
    bg, fg = STYLES[rng.choices(list(STYLES), STYLE_WEIGHTS)[0]]
    W, H = (rng.randint(200, 240), rng.randint(165, 200)) if two else (rng.randint(380, 520), rng.randint(95, 125))
    S = 3
    font_path = rng.choice(fonts) if fonts else None
    tight = rng.random() < 0.25
    rows = _lines(text, two, rng)
    ys = [0.10, 0.53] if two else [0.17]
    hh = [0.34, 0.40] if two else [0.66]

    base = Image.new("RGB", (W * S, H * S), bg)
    d = ImageDraw.Draw(base)
    if rng.random() < 0.9:                                           # vien khung
        d.rectangle([2 * S, 2 * S, (W - 2) * S, (H - 2) * S], outline=fg if bg != STYLES["blue"][0] else (240, 240, 240), width=int(rng.uniform(1.5, 4) * S))
    if rng.random() < 0.8:                                           # oc vit
        r_ = int(H * S * 0.02)
        for cx, cy in [(12 * S, 12 * S), (W * S - 12 * S, 12 * S), (12 * S, H * S - 12 * S), (W * S - 12 * S, H * S - 12 * S)]:
            d.ellipse([cx - r_, cy - r_, cx + r_, cy + r_], fill=(80, 80, 80))
    for line, y0, h in zip(rows, ys, hh):
        size = max(12, int(H * S * h * 1.2))
        font = ImageFont.truetype(font_path, size) if font_path else ImageFont.load_default(size)
        sp = (-0.06 if tight else 0.03) * size
        def width(sz):
            f = ImageFont.truetype(font_path, sz) if font_path else ImageFont.load_default(sz)
            return sum(f.getlength(c) for c in line) + sp * (len(line) - 1), f
        tot, font = width(size)
        while tot > (W - 34) * S and size > 14:
            size -= 3
            tot, font = width(size)
        x = (W * S - tot) / 2
        for c in line:
            d.text((x, H * S * y0), c, font=font, fill=fg)
            x += font.getlength(c) + sp

    ang, shear = rng.uniform(-8, 8), rng.uniform(-0.10, 0.10)
    M = cv2.getRotationMatrix2D((W * S / 2, H * S / 2), ang, 1.0)
    M[0, 1] += shear
    img = np.array(base)[:, :, ::-1].copy()
    img = cv2.warpAffine(img, M, (W * S, H * S), flags=cv2.INTER_LINEAR, borderValue=(60, 60, 60))
    img = cv2.resize(img, (W, H), interpolation=cv2.INTER_AREA)
    pad = rng.randint(-2, 8)                                         # cat long / thua vien nhu khung that
    if pad > 0:
        img = cv2.copyMakeBorder(img, pad, pad, pad, pad, cv2.BORDER_CONSTANT, value=(70, 70, 70))
    sc = rng.uniform(0.35, 0.95)                                     # do phan giai thap
    img = cv2.resize(img, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA)
    if rng.random() < 0.6:
        img = cv2.GaussianBlur(img, (3, 3), rng.uniform(0.4, 1.2))
    img = np.clip(img.astype(np.float32) + rs.normal(0, rng.uniform(2, 12), img.shape), 0, 255).astype(np.uint8)
    if rng.random() < 0.4:
        img = np.clip(img.astype(np.float32) * rng.uniform(0.55, 1.0) + rng.uniform(0, 40), 0, 255).astype(np.uint8)
    return img, text, two


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--preview", default="synth_preview.png")
    ap.add_argument("--fonts", default=None, help="thu muc chua font .ttf/.otf them")
    ap.add_argument("--n", type=int, default=12)
    a = ap.parse_args()
    fonts = find_fonts(a.fonts)
    print("Font tim thay:", len(fonts), [os.path.basename(f) for f in fonts])
    tiles = []
    for s in range(a.n):
        img, t, two = make_plate(s, fonts)
        v = cv2.resize(img, (300, int(300 * img.shape[0] / img.shape[1])))
        cv2.putText(v, t, (4, 14), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 0, 255), 1)
        tiles.append(v)
    H = max(t.shape[0] for t in tiles)
    tiles = [cv2.copyMakeBorder(t, 0, H - t.shape[0], 0, 0, cv2.BORDER_CONSTANT, value=(255, 255, 255)) for t in tiles]
    while len(tiles) % 4: tiles.append(np.full_like(tiles[0], 255))
    cv2.imwrite(a.preview, np.vstack([np.hstack(tiles[i:i + 4]) for i in range(0, len(tiles), 4)]))
    print("Da luu", a.preview)


if __name__ == "__main__":
    main()
