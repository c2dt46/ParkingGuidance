"""Sinh anh 'vung xe' co bien so VN gia lap (nhieu kieu: 1/2 dong, trang/vang/xanh, nghieng, mo,
nhieu, phoi canh) de do do chinh xac cua pipeline ANPR ma khong can anh that.
"""
"""Sinh anh 'vung xe' co bien so VN gia lap voi nhieu dieu kien khac nhau de do do chinh xac ANPR."""
import cv2, numpy as np, random
from PIL import Image, ImageDraw, ImageFont

FONTS = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
    "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]
PROVINCES = ["29","30","31","33","43","50","51","52","59","60","61","72","79","86","88","92","14","15","17","36"]
LETTERS = "ABCDEFGHKLMNPSTUVXYZ"
STYLES = {  # (nen, chu, vien)
    "white":  ((238, 238, 235), (22, 22, 22), (40, 40, 40)),
    "yellow": ((232, 190, 45), (25, 25, 25), (40, 40, 40)),
    "blue":   ((28, 72, 165), (245, 245, 245), (240, 240, 240)),
}

def rand_plate(rng):
    prov = rng.choice(PROVINCES); let = rng.choice(LETTERS)
    d = "".join(str(rng.randint(0, 9)) for _ in range(5))
    return f"{prov}{let}{d}"   # dang lien: 51L15217

def pretty(p):  return f"{p[:3]}-{p[3:6]}.{p[6:]}"

def render_plate(plate, two_line, style, font_path):
    bg, fg, border = STYLES[style]
    W, H = (280, 200) if two_line else (470, 110)
    S = 3  # ve o do phan giai cao
    img = Image.new("RGB", (W * S, H * S), bg)
    dr = ImageDraw.Draw(img)
    dr.rounded_rectangle([3*S, 3*S, (W-3)*S, (H-3)*S], radius=8*S, outline=border, width=3*S)
    if two_line:
        lines = [plate[:3], f"{plate[3:6]}.{plate[6:]}"]
        heights = [0.36, 0.42]; ys = [0.12, 0.52]
    else:
        lines = [f"{plate[:3]}-{plate[3:6]}.{plate[6:]}"]; heights = [0.62]; ys = [0.19]
    for line, hh, yy in zip(lines, heights, ys):
        size = int(H * S * hh * 1.25)
        font = ImageFont.truetype(font_path, size)
        # thu nho de vua chieu rong
        while dr.textlength(line, font=font) > (W - 26) * S and size > 10:
            size -= 4; font = ImageFont.truetype(font_path, size)
        tw = dr.textlength(line, font=font)
        bbox = font.getbbox("8")
        dr.text(((W * S - tw) / 2, H * S * yy - bbox[1] * 0.3), line, font=font, fill=fg)
    return np.array(img)[:, :, ::-1]

def vehicle_background(rng, W=640, H=480):
    base = np.array(rng.choice([(40, 40, 190), (230, 230, 230), (35, 35, 35), (140, 140, 140), (160, 70, 30), (60, 90, 60)]), np.float32)
    grad = np.linspace(1.15, 0.7, H)[:, None, None]
    img = np.clip(base[None, None, :] * grad + np.random.randn(H, W, 3) * 6, 0, 255).astype(np.uint8)
    # gay nhieu: luoi tan nhiet (khoi toi co nhieu thanh ngang), den xe, can duoi
    gx0 = rng.randint(int(W*.2), int(W*.35)); gx1 = rng.randint(int(W*.65), int(W*.8))
    gy0 = rng.randint(int(H*.38), int(H*.5)); gy1 = gy0 + rng.randint(60, 90)
    cv2.rectangle(img, (gx0, gy0), (gx1, gy1), (30, 30, 30), -1)
    for y in range(gy0 + 6, gy1, 9):
        cv2.line(img, (gx0 + 4, y), (gx1 - 4, y), (170, 170, 170), 2)
    for cx in (rng.randint(int(W*.05), int(W*.15)), rng.randint(int(W*.85), int(W*.95))):
        cv2.ellipse(img, (cx, gy0 + 20), (45, 22), rng.randint(-15, 15), 0, 360, (225, 225, 210), -1)
    cv2.rectangle(img, (0, int(H*.9)), (W, H), (20, 20, 20), -1)
    return img

def add_plate(bg, plate_img, rng, width_frac):
    H, W = bg.shape[:2]
    pw = int(W * width_frac); ph = int(plate_img.shape[0] * pw / plate_img.shape[1])
    p = cv2.resize(plate_img, (pw, ph), interpolation=cv2.INTER_AREA)
    # xoay + meo phoi canh
    ang = rng.uniform(-6, 6)
    jit = lambda: rng.uniform(-0.07, 0.07)
    src = np.float32([[0, 0], [pw, 0], [pw, ph], [0, ph]])
    dst = np.float32([[pw*jit(), ph*jit()], [pw*(1+jit()), ph*jit()], [pw*(1+jit()), ph*(1+jit())], [pw*jit(), ph*(1+jit())]])
    M = cv2.getPerspectiveTransform(src, dst)
    R = cv2.getRotationMatrix2D((pw/2, ph/2), ang, 1.0)
    pad = 20
    canvas = cv2.copyMakeBorder(p, pad, pad, pad, pad, cv2.BORDER_REPLICATE)
    canvas = cv2.warpPerspective(canvas, M, (canvas.shape[1], canvas.shape[0]), borderMode=cv2.BORDER_REPLICATE)
    canvas = cv2.warpAffine(canvas, R, (canvas.shape[1], canvas.shape[0]), borderMode=cv2.BORDER_REPLICATE)
    x = int(W * rng.uniform(0.28, 0.6)); y = int(H * rng.uniform(0.6, 0.78))
    x = min(x, W - canvas.shape[1] - 1); y = min(y, H - canvas.shape[0] - 1)
    bg[y:y+canvas.shape[0], x:x+canvas.shape[1]] = canvas
    return bg, (x + pad, y + pad, pw, ph)

def degrade(img, rng):
    tint = np.array([rng.uniform(.85, 1.15), rng.uniform(.85, 1.15), rng.uniform(.85, 1.15)], np.float32)
    img = np.clip(img.astype(np.float32) * tint * rng.uniform(0.7, 1.2) + rng.uniform(-20, 20), 0, 255)
    s = rng.uniform(0, 1.4)
    if s > 0.3: img = cv2.GaussianBlur(img, (0, 0), s)
    img = np.clip(img + np.random.randn(*img.shape) * rng.uniform(0, 7), 0, 255).astype(np.uint8)
    ok, enc = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, rng.randint(55, 95)])
    return cv2.imdecode(enc, 1)

def make_sample(seed):
    rng = random.Random(seed); np.random.seed(seed)
    plate = rand_plate(rng)
    two = rng.random() < 0.35
    style = rng.choices(["white", "yellow", "blue"], [0.6, 0.25, 0.15])[0]
    pimg = render_plate(plate, two, style, rng.choice(FONTS))
    bg = vehicle_background(rng)
    wf = rng.uniform(0.15, 0.34) if not two else rng.uniform(0.11, 0.22)
    bg, box = add_plate(bg, pimg, rng, wf)
    img = degrade(bg, rng)
    return img, plate, dict(two=two, style=style, wf=round(wf, 2), box=box)

if __name__ == "__main__":
    import sys
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 8
    for i in range(n):
        img, plate, meta = make_sample(i)
        cv2.imwrite(f"bench/s{i:03d}_{plate}.png", img)
        print(i, plate, meta)
