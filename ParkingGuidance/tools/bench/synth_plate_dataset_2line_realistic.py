"""Bien so 2 dong THUC TE HON synth_plate_dataset.py: co khung vien sat chu, tem/quoc huy nho
giua 2 dong, oc vit 4 goc - day la nhung yeu to tung lam pipeline ANPR that bai gan het voi bien
2 dong (30%) truoc khi sua loi trong PlateTextReader.cs (xem README muc 11).
"""
"""Bien so 2 dong thuc te hon: co khung vien sat chu, oc vit/bu-long o 4 goc, tem/quoc huy nho
giua 2 dong (rat pho bien tren bien 2 dong thuc te VN) - nhung yeu to nay co the pha vo buoc
tach ky tu (connected components) neu khong xu ly dung.
"""
import cv2, numpy as np, random
from PIL import Image, ImageDraw, ImageFont
from synth_plate_dataset import FONTS, PROVINCES, LETTERS, STYLES, rand_plate, vehicle_background, degrade

def render_plate_2line_realistic(plate, style, font_path, rng):
    bg, fg, border = STYLES[style]
    W, H = 220, 200
    S = 4
    img = Image.new("RGB", (W*S, H*S), bg)
    dr = ImageDraw.Draw(img)
    dr.rectangle([2*S, 2*S, (W-2)*S, (H-2)*S], outline=border, width=3*S)

    line1 = plate[:3]                      # vd "51K"
    line2 = f"{plate[3:6]}.{plate[6:]}"    # vd "152.17"

    def draw_line(text, y_frac, h_frac):
        size = int(H*S*h_frac*1.2)
        font = ImageFont.truetype(font_path, size)
        while dr.textlength(text, font=font) > (W-40)*S and size > 10:
            size -= 3; font = ImageFont.truetype(font_path, size)
        tw = dr.textlength(text, font=font)
        dr.text(((W*S-tw)/2, H*S*y_frac), text, font=font, fill=fg)

    draw_line(line1, 0.08, 0.34)
    draw_line(line2, 0.54, 0.38)

    # Tem/quoc huy nho giua 2 dong (hinh tron do+vang, rat pho bien tren bien 2 dong that)
    if rng.random() < 0.7:
        cx, cy, r = W*S//2, int(H*S*0.46), int(H*S*0.045)
        dr.ellipse([cx-r, cy-r, cx+r, cy+r], fill=(190, 30, 30))

    # Oc vit 4 goc (thuong thay tren bien so gan khung xe that)
    if rng.random() < 0.8:
        r = int(H*S*0.02)
        for cx, cy in [(12*S,12*S),(W*S-12*S,12*S),(12*S,H*S-12*S),(W*S-12*S,H*S-12*S)]:
            dr.ellipse([cx-r,cy-r,cx+r,cy+r], fill=(80,80,80))

    return np.array(img)[:, :, ::-1]

def make_sample_2line_hard(seed):
    rng = random.Random(seed); np.random.seed(seed)
    plate = rand_plate(rng)
    style = rng.choices(["white", "yellow", "blue"], [0.6, 0.25, 0.15])[0]
    pimg = render_plate_2line_realistic(plate, style, rng.choice(FONTS), rng)
    bg = vehicle_background(rng)

    wf = rng.uniform(0.12, 0.24)
    H, W = bg.shape[:2]
    pw = int(W*wf); ph = int(pimg.shape[0]*pw/pimg.shape[1])
    p = cv2.resize(pimg, (pw, ph), interpolation=cv2.INTER_AREA)
    ang = rng.uniform(-6, 6)
    M = cv2.getRotationMatrix2D((pw/2, ph/2), ang, 1.0)
    pad = 16
    canvas = cv2.copyMakeBorder(p, pad, pad, pad, pad, cv2.BORDER_REPLICATE)
    canvas = cv2.warpAffine(canvas, M, (canvas.shape[1], canvas.shape[0]), borderMode=cv2.BORDER_REPLICATE)
    x = int(W*rng.uniform(0.3, 0.55)); y = int(H*rng.uniform(0.62, 0.76))
    x = min(x, W-canvas.shape[1]-1); y = min(y, H-canvas.shape[0]-1)
    box = (x+pad, y+pad, pw, ph)
    bg[y:y+canvas.shape[0], x:x+canvas.shape[1]] = canvas
    img = degrade(bg, rng)
    return img, plate, dict(two=True, style=style, wf=round(wf,2), box=box)

if __name__ == "__main__":
    import sys
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 6
    for i in range(n):
        img, plate, meta = make_sample_2line_hard(i)
        cv2.imwrite(f"bench2/h{i:03d}_{plate}.png", img)
        print(i, plate, meta)
