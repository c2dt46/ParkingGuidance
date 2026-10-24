#!/usr/bin/env python3
"""
Huan luyen MODEL DOC CHUOI BIEN SO (CRNN + CTC) -> xuat plate_ocr.onnx cho OnnxPlateReader.cs cua ung dung.

Khac voi dua tung ky tu (tach ky tu -> phan loai): model nhin ca dong chu va tra ve chuoi, KHONG can tach ky tu va KHONG can khung tung ky tu.
Chi can anh bien + chuoi dung (chinh la du lieu ban dang co o tab Train). Da tu xu ly bien 2 dong (tach doi, doc tung dong, y het OnnxPlateReader).

    python tools/train_plate_ocr.py --corrections <DataDirectory>/simulation/corrections --out <thu muc lam viec> \\
        [--deploy src/ParkingGuidance.Web/Models/plate_ocr.onnx] [--epochs 25] [--synth 8000] [--device cpu|cuda]

Kiem tra nhanh moi truong (khoang vai chuc giay, khong can du lieu that):   python tools/train_plate_ocr.py --smoke
Chi xem du lieu + anh sau tang cuong, khong train (khong can PyTorch):         python tools/train_plate_ocr.py --corrections ... --out ... --dry-run

Du lieu:
  - THAT: moi mau (<id>.png + "correct" trong index.json). Chia tap kiem tra THEO CHUOI BIEN (cung 1 bien chup nhieu lan khong nam ca 2 ben).
  - GIA LAP: synth_plates.py sinh them hang nghin bien (nhieu mau nen, nghieng, mo, nhieu). Mau that duoc lap lai nhieu lan va tang cuong moi lan.
    => Mau that cang nhieu va cang da dang (nhieu bien KHAC NHAU, nhieu dieu kien sang/toi) thi ket qua cang sat thuc te. Chi 1 bien chup 50 lan KHONG thay duoc 50 bien khac nhau.

Quy uoc PHAI khop OnnxPlateReader.cs / appsettings.json (muc Anpr): anh vao [1,1,48,256] xam, chuan hoa (x/255-0.5)/0.5, dong ngan hon 256 thi chen 0 ben phai;
dau ra [1,64,37] (blank = chi so 0, ky tu thu i = chi so i+1), OcrCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", OcrBlankLast = false.

Yeu cau: pip install torch opencv-python numpy pillow onnxruntime   (torch da co san neu ban da cai ultralytics cho train_chars.py)
"""
import argparse
import copy
import hashlib
import json
import math
import os
import random
import re
import shutil
import sys
import time

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from synth_plates import find_fonts, make_plate  # noqa: E402

CHARSET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"   # PHAI trung Anpr:OcrCharset (36 ky tu: moi chu A-Z va so 0-9 => hoc duoc ca bien quan doi, ngoai giao, dac biet)
H, W = 48, 256                                  # anh vao model (chieu cao co dinh, rong co dinh, chen 0 ben phai)
STRIDE = 4                                      # model giam chieu rong 4 lan -> T = W / 4 = 64 buoc


# --------------------------------------------------------------------------- tach dong + tien xu ly (y het OnnxPlateReader.cs)
def split_lines(img, text):
    """Anh bien -> danh sach (anh dong, chuoi dong). Ti le rong/cao < 2.4 => bien 2 dong: tach doi co chong 1/20 chieu cao (giong C#). Rong: [] khi khong hop le."""
    h, w = img.shape[:2]
    if h < 8 or w < 8:
        return []
    if w / float(h) < 2.4:
        n2 = 5 if len(text) >= 8 else (4 if len(text) >= 6 else max(1, len(text) // 2))
        n1 = len(text) - n2
        if n1 < 1 or n2 < 1:
            return []
        half, ov = h // 2, max(1, h // 20)
        top = img[0:min(h, half + ov), :]
        y2 = max(0, half - ov)
        return [(top, text[:n1]), (img[y2:h, :], text[n1:])]
    return [(img, text)]


def to_gray(img):
    return cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if img.ndim == 3 else img.copy()


def line_to_array(gray):
    """Anh xam dong chu -> mang float32 [H, W]: doi chieu cao ve 48 giu ti le, chuan hoa (x/255-0.5)/0.5, chen 0 ben phai."""
    h, w = gray.shape[:2]
    nw = max(1, min(W, int(round(H * w / float(h)))))
    r = cv2.resize(gray, (nw, H), interpolation=cv2.INTER_LINEAR)
    out = np.zeros((H, W), np.float32)
    out[:, :nw] = (r.astype(np.float32) / 255.0 - 0.5) / 0.5
    return out


# --------------------------------------------------------------------------- tang cuong anh
def augment(g, rs):
    """Mo phong khung cat lech, nghieng, mo, do phan giai thap, nhieu, nen sang toi khac nhau. Dau vao/ra: anh xam uint8."""
    h, w = g.shape
    l, r = int(rs.uniform(-0.05, 0.10) * w), int(rs.uniform(-0.05, 0.10) * w)
    t, b = int(rs.uniform(-0.05, 0.12) * h), int(rs.uniform(-0.05, 0.12) * h)
    y0, y1, x0, x1 = max(0, -t), h - max(0, -b), max(0, -l), w - max(0, -r)
    if y1 - y0 >= 8 and x1 - x0 >= 8:
        g = g[y0:y1, x0:x1]
    g = cv2.copyMakeBorder(g, max(0, t), max(0, b), max(0, l), max(0, r), cv2.BORDER_REPLICATE)

    h, w = g.shape
    g = cv2.resize(g, (max(8, int(w * rs.uniform(0.92, 1.08))), h))
    h, w = g.shape
    M = cv2.getRotationMatrix2D((w / 2, h / 2), rs.uniform(-3.5, 3.5), 1.0)
    M[0, 1] += rs.uniform(-0.12, 0.12)
    g = cv2.warpAffine(g, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    if rs.rand() < 0.3:
        src = np.float32([[0, 0], [w, 0], [w, h], [0, h]])
        dst = src + (rs.uniform(-0.04, 0.04, src.shape) * np.float32([w, h])).astype(np.float32)
        g = cv2.warpPerspective(g, cv2.getPerspectiveTransform(src, dst), (w, h), borderMode=cv2.BORDER_REPLICATE)

    a, bb = rs.uniform(0.5, 1.3), rs.uniform(-40, 40)
    g = np.clip(a * (g.astype(np.float32) - 128.0) + 128.0 + bb, 0, 255).astype(np.uint8)
    if rs.rand() < 0.3:
        gam = rs.uniform(0.7, 1.5)
        g = (255.0 * (g.astype(np.float32) / 255.0) ** gam).astype(np.uint8)
    if rs.rand() < 0.5:
        k = int(rs.choice([3, 5]))
        g = cv2.GaussianBlur(g, (k, k), rs.uniform(0.3, 1.5))
    if rs.rand() < 0.15:
        k = int(rs.randint(3, 10))
        g = cv2.filter2D(g, -1, np.full((1, k), 1.0 / k, np.float32))
    if rs.rand() < 0.4:
        f = rs.uniform(0.3, 0.8)
        gh, gw = g.shape
        small = cv2.resize(g, (max(4, int(gw * f)), max(4, int(gh * f))), interpolation=cv2.INTER_AREA)
        g = cv2.resize(small, (gw, gh), interpolation=cv2.INTER_LINEAR)
    sigma = rs.uniform(0, 12)
    if sigma > 0.5:
        g = np.clip(g.astype(np.float32) + rs.normal(0, sigma, g.shape), 0, 255).astype(np.uint8)
    if rs.rand() < 0.3:
        ok, enc = cv2.imencode(".jpg", g, [cv2.IMWRITE_JPEG_QUALITY, int(rs.randint(25, 80))])
        if ok:
            g = cv2.imdecode(enc, cv2.IMREAD_GRAYSCALE)
    if rs.rand() < 0.08:
        g = 255 - g
    return g


# --------------------------------------------------------------------------- du lieu
def is_val_label(text):
    return int(hashlib.sha1(text.encode()).hexdigest()[:8], 16) % 100 < 20


def load_real(corr_dir):
    """Doc <corr_dir>/index.json (+ <id>.png). Tra ve (danh sach bien [{text, lines:[(xam, chuoi)]}], so mau bo qua)."""
    path = os.path.join(corr_dir, "index.json")
    if not os.path.exists(path):
        raise SystemExit(f"Khong thay {path}")
    with open(path, "r", encoding="utf-8") as f:
        index = json.load(f)
    plates, skipped = [], 0
    for e in index:
        sid = e.get("id", "")
        text = re.sub(r"[^A-Z0-9]", "", (e.get("correct") or "").upper().replace("Đ", "D"))
        p = os.path.join(corr_dir, sid + ".png")
        if not sid or len(text) < 3 or any(c not in CHARSET for c in text) or not os.path.exists(p):
            skipped += 1
            continue
        img = cv2.imdecode(np.fromfile(p, dtype=np.uint8), cv2.IMREAD_COLOR)
        lines = split_lines(img, text) if img is not None else []
        if not lines:
            skipped += 1
            continue
        plates.append({"text": text, "lines": [(to_gray(li), lt) for li, lt in lines]})
    return plates, skipped


def split_real(plates):
    """Chia THEO CHUOI BIEN. Co it nhat 5 bien khac nhau thi moi de lai tap kiem tra that."""
    labels = sorted({p["text"] for p in plates})
    val_labels = {t for t in labels if is_val_label(t)}
    if not val_labels and len(labels) >= 5:
        val_labels = {min(labels, key=lambda t: hashlib.sha1(t.encode()).hexdigest())}
    if len(labels) < 5:
        val_labels = set()
    return [p for p in plates if p["text"] not in val_labels], [p for p in plates if p["text"] in val_labels]


def synth_plates(n, fonts, seed0):
    out = []
    for i in range(n):
        img, text, _ = make_plate(seed0 + i, fonts)
        lines = split_lines(img, text)
        if lines:
            out.append({"text": text, "lines": [(to_gray(li), lt) for li, lt in lines]})
    return out


def flat_lines(plates):
    return [(g, lt) for p in plates for g, lt in p["lines"]]


# --------------------------------------------------------------------------- model (chi dinh nghia khi co PyTorch)
try:
    import torch
    import torch.nn as nn
except ImportError:          # --dry-run khong can PyTorch
    torch = None

if torch is not None:
    def _cbr(i, o, k=3, p=1):
        return [nn.Conv2d(i, o, k, padding=p, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True)]

    class CRNN(nn.Module):
        """Anh [N,1,48,256] -> logit [N,64,C]. 2 lop MaxPool 2x2 giam rong 4 lan; 2 lop MaxPool (2,1) + conv (3,1) gom chieu cao ve 1; BiLSTM doc chuoi."""
        def __init__(self, n_cls):
            super().__init__()
            self.cnn = nn.Sequential(
                *_cbr(1, 32), nn.MaxPool2d(2, 2),                                   # 24 x 128
                *_cbr(32, 64), nn.MaxPool2d(2, 2),                                  # 12 x 64
                *_cbr(64, 128), *_cbr(128, 128), nn.MaxPool2d((2, 1), (2, 1)),      # 6 x 64
                *_cbr(128, 192), nn.MaxPool2d((2, 1), (2, 1)),                      # 3 x 64
                *_cbr(192, 192, k=(3, 1), p=0),                                      # 1 x 64
            )
            self.rnn = nn.LSTM(192, 128, num_layers=2, bidirectional=True, batch_first=True, dropout=0.2)
            self.fc = nn.Linear(256, n_cls)

        def forward(self, x):
            f = self.cnn(x).squeeze(2).permute(0, 2, 1)      # [N, T, 192]
            y, _ = self.rnn(f)
            return self.fc(y)                                  # [N, T, C]

    class LineDataset(torch.utils.data.Dataset):
        def __init__(self, items, aug_prob):
            self.items, self.aug_prob = items, aug_prob
        def __len__(self):
            return len(self.items)
        def __getitem__(self, i):
            g, label = self.items[i]
            if random.random() < self.aug_prob:
                g = augment(g, np.random)
            x = torch.from_numpy(line_to_array(g)).unsqueeze(0)
            y = torch.tensor([CHARSET.index(c) + 1 for c in label], dtype=torch.long)
            return x, y

    def collate(batch):
        xs = torch.stack([b[0] for b in batch])
        ys = torch.cat([b[1] for b in batch])
        lens = torch.tensor([len(b[1]) for b in batch], dtype=torch.long)
        return xs, ys, lens


# --------------------------------------------------------------------------- trang thai cho ung dung (--status)
INFO = {}
_STATUS = {"path": None, "data": {"stage": "prepare", "stageLabel": "Chuẩn bị", "progress": 0, "message": "", "finished": False,
                                    "error": None, "result": None, "startedAt": time.strftime("%Y-%m-%d %H:%M:%S")}}


def status(**kw):
    """Ghi file JSON trang thai (nguyen tu: ghi file tam roi doi ten) de tab Train trong ung dung hien tien trinh. Khong co --status thi bo qua."""
    _STATUS["data"].update(kw)
    path = _STATUS["path"]
    if not path:
        return
    try:
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_STATUS["data"], f, ensure_ascii=False)
        os.replace(tmp, path)
    except OSError:
        pass


def ctc_decode(logits):
    """Giai ma tham lam y het OnnxPlateReader.cs: argmax moi buoc, gop lap, bo blank (chi so 0). logits: [T, C]."""
    out, prev = [], -1
    for k in np.argmax(logits, axis=1):
        if k != 0 and k != prev:
            out.append(CHARSET[k - 1])
        prev = k
    return "".join(out)


def eval_plates(forward, plates):
    """forward(mang [n,1,H,W]) -> logit numpy [n,T,C]. Tra ve (ty le doc DUNG TRON bien, ty le dung tung dong, so bien)."""
    if not plates:
        return 0.0, 0.0, 0
    lines = [(pi, g, lt) for pi, p in enumerate(plates) for g, lt in p["lines"]]
    preds = []
    for s in range(0, len(lines), 64):
        chunk = lines[s:s + 64]
        x = np.stack([line_to_array(g) for _, g, _ in chunk])[:, None]
        lg = forward(x)
        preds += [ctc_decode(lg[i]) for i in range(len(chunk))]
    ok_line = sum(p == lt for p, (_, _, lt) in zip(preds, lines))
    per = {}
    for pr, (pi, _, _) in zip(preds, lines):
        per.setdefault(pi, []).append(pr)
    ok_plate = sum("".join(per.get(i, [])) == p["text"] for i, p in enumerate(plates))
    return ok_plate / len(plates), ok_line / len(lines), len(plates)


# --------------------------------------------------------------------------- huan luyen + xuat ONNX
def export_onnx(model, path):
    model.eval().cpu()
    dummy = torch.zeros(1, 1, H, W)
    kw = dict(input_names=["input"], output_names=["output"], dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}}, opset_version=14)
    try:
        torch.onnx.export(model, dummy, path, dynamo=False, **kw)      # torch moi: dung bo xuat ONNX kinh dien (on dinh voi LSTM)
    except TypeError:
        torch.onnx.export(model, dummy, path, **kw)                    # torch cu khong co tham so dynamo


def train(args, real_train, real_val, synth_train, synth_val):
    if args.threads > 0:
        torch.set_num_threads(args.threads)
    device = torch.device(args.device or ("cuda" if torch.cuda.is_available() else "cpu"))
    print(f"[train] thiet bi: {device}")
    real_lines, synth_lines = flat_lines(real_train), flat_lines(synth_train)
    repeat = args.real_repeat
    if repeat <= 0:       # tu can bang: mau that chiem khoang 40% moi epoch
        repeat = int(np.clip(round(0.4 * len(synth_lines) / max(1, len(real_lines))), 1, 60)) if real_lines else 0
    items = synth_lines + real_lines * repeat
    print(f"[train] moi epoch: {len(synth_lines)} dong gia lap + {len(real_lines)} dong that x{repeat} = {len(items)} dong")
    loader = torch.utils.data.DataLoader(LineDataset(items, aug_prob=0.85), batch_size=args.batch, shuffle=True,
                                         collate_fn=collate, num_workers=args.workers, drop_last=len(items) > args.batch)
    model = CRNN(len(CHARSET) + 1).to(device)
    print(f"[train] model {sum(p.numel() for p in model.parameters()) / 1e6:.2f}M tham so")
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=args.lr, total_steps=args.epochs * len(loader), pct_start=0.15)
    ctc = nn.CTCLoss(blank=0, zero_infinity=True)
    T = W // STRIDE

    def fwd_np(x):
        model.eval()
        with torch.no_grad():
            return model(torch.from_numpy(x).to(device)).cpu().numpy()

    best, best_state = -1.0, None
    for ep in range(1, args.epochs + 1):
        model.train()
        t0, tot, n = time.time(), 0.0, 0
        for xs, ys, lens in loader:
            xs = xs.to(device)
            logp = model(xs).log_softmax(2).permute(1, 0, 2)            # [T, N, C]
            loss = ctc(logp, ys.to(device), torch.full((xs.size(0),), T, dtype=torch.long), lens)
            opt.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt.step()
            sched.step()
            tot += loss.item() * xs.size(0)
            n += xs.size(0)
        sp, sl, sn = eval_plates(fwd_np, synth_val)
        rp, rl, rn = eval_plates(fwd_np, real_val)
        score = (0.5 * rp + 0.5 * sp) if rn >= 3 else sp
        mark = ""
        if score > best:
            best, mark = score, "  <- tot nhat"
            best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
            torch.save(best_state, os.path.join(args.out, "best.pt"))          # co the xuat ONNX som bang --export-from best.pt
        status(stage="train", stageLabel="Đang huấn luyện", progress=5 + int(90 * ep / args.epochs), epoch=ep, epochs=args.epochs,
               loss=round(tot / max(1, n), 4), synthAcc=round(sp, 4), realAcc=round(rp, 4) if rn else None, realPlates=rn,
               message=f"epoch {ep}/{args.epochs} — loss {tot / max(1, n):.2f}, giả lập {100 * sp:.0f}%" + (f", thật {100 * rp:.0f}% ({rn} biển)" if rn else ""))
        real_txt = f" | that {100 * rp:5.1f}% ({rn} bien)" if rn else " | that: chua co tap kiem tra"
        print(f"[epoch {ep:3d}/{args.epochs}] loss {tot / max(1, n):.3f} | gia lap {100 * sp:5.1f}%{real_txt} | {time.time() - t0:.0f}s{mark}", flush=True)
    model.load_state_dict(best_state)
    return model, fwd_np


def finish(args, model, fwd_np, synth_val, real_val):
    status(stage="export", stageLabel="Xuất model", progress=96, message="Đang xuất plate_ocr.onnx và kiểm tra lại…")
    onnx_path = os.path.join(args.out, "plate_ocr.onnx")
    export_onnx(copy.deepcopy(model), onnx_path)      # ban sao: export_onnx dua model ve CPU, khong lam hong fwd_np khi train bang GPU
    with open(os.path.join(args.out, "plate_ocr.charset.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(CHARSET) + "\n")
    print(f"[xuat] da tao: {os.path.abspath(onnx_path)}")

    sp = rp = 0.0
    sn = rn = 0
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        meta = sess.get_inputs()[0]
        print(f"[kiem tra ONNX] dau vao {meta.name} {meta.shape}, dau ra {sess.get_outputs()[0].shape}")
        run = lambda x: sess.run(None, {meta.name: x.astype(np.float32)})[0]
        sp, sl, sn = eval_plates(run, synth_val)
        rp, rl, rn = eval_plates(run, real_val)
        print(f"[kiem tra ONNX] doc DUNG TRON bien: gia lap {100 * sp:.1f}% ({sn} bien)" + (f", that {100 * rp:.1f}% ({rn} bien)" if rn else ""))
        x = np.stack([line_to_array(g) for g, _ in flat_lines(synth_val)[:8]])[:, None]
        diff = float(np.abs(run(x) - fwd_np(x)).max())
        print(f"[kiem tra ONNX] chenh lech so voi PyTorch: {diff:.2e}" + ("  (OK)" if diff < 1e-3 else "  <-- LON BAT THUONG, khong nen deploy"))
        if diff >= 1e-3:
            raise RuntimeError("Model ONNX xuất ra khác PyTorch quá nhiều — không dùng.")
    except ImportError:
        print("[kiem tra ONNX] bo qua (chua cai onnxruntime). Cai: pip install onnxruntime")

    # Khuyen nghi cai: chi khi doc DUNG TRON it nhat 80% tren it nhat 5 bien THAT (khac nhau) chua tung dung de train
    if rn >= 5 and rp >= 0.8:
        recommend, reason = True, f"Đọc đúng {100 * rp:.0f}% trên {rn} biển thật chưa từng học — đủ tin cậy."
    elif rn >= 5:
        recommend, reason = False, f"Chỉ đọc đúng {100 * rp:.0f}% trên {rn} biển thật chưa từng học (cần ≥ 80%). Hãy thêm nhiều biển KHÁC NHAU rồi train lại."
    else:
        recommend, reason = False, "Chưa có đủ biển thật để kiểm tra (cần ≥ 5 biển khác nhau ngoài tập học). Có thể cài thử rồi kiểm tra ở tab Mô phỏng."
    status(stage="done", stageLabel="Hoàn tất", progress=100, finished=True, error=None, message=reason,
           result={"candidate": os.path.abspath(onnx_path), "recommendInstall": recommend, "reason": reason,
                   "synthAcc": round(sp, 4), "realAcc": round(rp, 4) if rn else None, "realPlates": rn, "synthPlates": sn,
                   "uniquePlates": INFO.get("uniquePlates"), "samples": INFO.get("samples")})

    if args.deploy and not args.smoke:
        os.makedirs(os.path.dirname(os.path.abspath(args.deploy)), exist_ok=True)
        if os.path.exists(args.deploy):
            shutil.copy2(args.deploy, args.deploy + time.strftime(".bak-%Y%m%d-%H%M%S"))
        shutil.copy2(onnx_path, args.deploy)
        print(f"[deploy] da chep vao {args.deploy}. KHOI DONG LAI ung dung de nap model (bo doc OCR chi nap luc khoi dong).")
    elif not args.smoke:
        print("[deploy] CHUA chep vao ung dung. Chep file tren vao Models\\plate_ocr.onnx cua ung dung (hoac chay lai voi --deploy), roi khoi dong lai ung dung.")
    elif args.smoke:
        print("[smoke] Moi truong OK (xuat + chay duoc ONNX). Model nay moi train 2 epoch nen CHUA dung duoc — chay lai khong co --smoke.")


def default_corrections():
    base = os.environ.get("LOCALAPPDATA")
    if base:
        p = os.path.join(base, "ParkingGuidance", "data", "simulation", "corrections")
        if os.path.exists(os.path.join(p, "index.json")):
            return p
    return None


def _main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corrections", default=None, help="thu muc <DataDirectory>/simulation/corrections (index.json + <id>.png). Bo trong = tu tim %%LOCALAPPDATA%%/ParkingGuidance/data/simulation/corrections")
    ap.add_argument("--synth-only", action="store_true", help="chi dung du lieu gia lap (bo qua mau that)")
    ap.add_argument("--export-from", default=None, help="xuat ONNX tu checkpoint best.pt (khi muon dung train som, khong cho het cac epoch)")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "plate_ocr_run"), help="thu muc lam viec/ket qua (mac dinh: tools/plate_ocr_run, du chay tu dau)")
    ap.add_argument("--deploy", default="", help="neu co: chep plate_ocr.onnx vao day (vd src/ParkingGuidance.Web/Models/plate_ocr.onnx)")
    ap.add_argument("--fonts", default=None, help="thu muc chua them font .ttf cho du lieu gia lap")
    ap.add_argument("--epochs", type=int, default=25)
    ap.add_argument("--synth", type=int, default=8000, help="so bien gia lap sinh san (moi bien thanh 1-2 dong)")
    ap.add_argument("--real-repeat", type=int, default=0, help="so lan lap mau that moi epoch (0 = tu can bang)")
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--lr", type=float, default=2e-3)
    ap.add_argument("--workers", type=int, default=0)
    ap.add_argument("--threads", type=int, default=0, help="so luong CPU toi da cho PyTorch (0 = mac dinh)")
    ap.add_argument("--status", default=None, help="file JSON ghi tien trinh (ung dung dung de hien thi tab Train)")
    ap.add_argument("--device", default=None)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--smoke", action="store_true", help="chay nhanh 2 epoch voi du lieu gia lap nho de kiem tra moi truong + xuat ONNX")
    ap.add_argument("--dry-run", action="store_true", help="chi dung du lieu, in thong ke, luu anh xem thu (khong can PyTorch)")
    args = ap.parse_args()

    _STATUS["path"] = args.status
    status(stage="prepare", stageLabel="Chuẩn bị dữ liệu", progress=1, message="Đang đọc mẫu thật và sinh dữ liệu giả lập…")
    if args.smoke:
        args.synth, args.epochs, args.batch = 300, 2, 32
    random.seed(args.seed); np.random.seed(args.seed)
    os.makedirs(args.out, exist_ok=True)

    fonts = find_fonts(args.fonts)
    print(f"[du lieu] font cho du lieu gia lap: {len(fonts)} ({', '.join(os.path.basename(f) for f in fonts[:4])}...)" if fonts else
          "[du lieu] CANH BAO: khong tim thay font .ttf nao, dung font mac dinh cua Pillow -> kem giong bien that. Dung --fonts <thu muc font>")
    if args.export_from:
        model = CRNN(len(CHARSET) + 1)
        model.load_state_dict(torch.load(args.export_from, map_location="cpu"))
        model.eval()
        def fwd_np(x):
            with torch.no_grad():
                return model(torch.from_numpy(x)).numpy()
        finish(args, model, fwd_np, synth_plates(300, fonts, seed0=9000000), [])
        return
    real_train, real_val = [], []
    corr = None if (args.smoke or args.synth_only) else (args.corrections or default_corrections())
    if corr is None and not args.smoke:
        print("[du lieu] KHONG co du lieu that (khong tim thay corrections) -> chi train bang du lieu gia lap. Truyen --corrections <thu muc> de dung mau that.")
    if corr:
        print(f"[du lieu] doc mau that tu: {corr}")
        plates, skipped = load_real(corr)
        real_train, real_val = split_real(plates)
        uniq = len({p["text"] for p in plates})
        INFO.update(uniquePlates=uniq, samples=len(plates))
        print(f"[du lieu] mau that: {len(plates)} bien ({uniq} chuoi KHAC NHAU), bo qua {skipped}; train {len(real_train)}, kiem tra {len(real_val)}")
        if uniq < 30:
            print(f"[du lieu] LUU Y: chi {uniq} bien khac nhau — model se phu thuoc nhieu vao du lieu gia lap; hay thu them nhieu bien KHAC NHAU de on dinh hon.")
    t0 = time.time()
    synth_train = synth_plates(args.synth, fonts, seed0=100000)
    synth_val = synth_plates(60 if args.smoke else 300, fonts, seed0=9000000)
    print(f"[du lieu] gia lap: {len(synth_train)} bien train + {len(synth_val)} bien kiem tra ({time.time() - t0:.0f}s)")
    status(stage="prepare", progress=5, message="Đã chuẩn bị xong dữ liệu, bắt đầu huấn luyện…")

    if args.dry_run or torch is None:
        if torch is None and not args.dry_run:
            raise SystemExit("Chua cai PyTorch (pip install torch). Co the dung --dry-run de kiem tra du lieu ma khong can PyTorch.")
        rs = np.random.RandomState(0)
        items = (flat_lines(real_train) or flat_lines(synth_train))[:12] + flat_lines(synth_train)[:12]
        tiles = []
        for g, lt in items:
            a = augment(g, rs)
            t = (line_to_array(a) * 0.5 + 0.5) * 255
            t = cv2.cvtColor(np.clip(t, 0, 255).astype(np.uint8), cv2.COLOR_GRAY2BGR)
            cv2.putText(t, lt, (3, 12), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (0, 0, 255), 1)
            tiles.append(t)
        while len(tiles) % 3:
            tiles.append(np.full_like(tiles[0], 255))
        grid = np.vstack([np.hstack(tiles[i:i + 3]) for i in range(0, len(tiles), 3)])
        p = os.path.join(args.out, "dry_run_preview.png")
        cv2.imwrite(p, grid)
        print(f"[dry-run] da luu {p} (anh dong chu SAU tang cuong, dung nhu model se thay). Khong train.")
        return

    model, fwd_np = train(args, real_train, real_val, synth_train, synth_val)

    finish(args, model, fwd_np, synth_val, real_val)


def main():
    try:
        _main()
    except SystemExit as e:
        if e.code not in (None, 0):          # thoat co thong bao loi (vd: thieu PyTorch, khong thay thu muc corrections)
            status(stage="error", stageLabel="Lỗi", finished=True, error=str(e.code))
        raise
    except BaseException as e:              # ghi loi vao file trang thai de ung dung hien thi, roi thoat voi ma loi
        status(stage="error", stageLabel="Lỗi", finished=True, error=f"{type(e).__name__}: {e}")
        raise


if __name__ == "__main__":
    main()
