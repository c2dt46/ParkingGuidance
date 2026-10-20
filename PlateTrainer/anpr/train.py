"""Huấn luyện CNN phân loại ký tự biển số (32 lớp) và xuất char_cnn.onnx đúng 'hợp đồng' của CnnPlateReader.cs.

Hợp đồng ONNX (không cần sửa C#):
    đầu vào  : float32 [N,1,28,28], giá trị 0..255 (KHÔNG chuẩn hoá — việc chia 255 nằm TRONG model)
    đầu ra   : float32 [N,32] xác suất đã softmax; chỉ số 0..30 = "ABCDEFGHKLMNPRSTUVXYZ0123456789", 31 = nền/nhiễu

Chạy:  python -m anpr.train --data data/chars --out runs/lan1
Nhật ký từng epoch ghi vào <out>/log.jsonl (giao diện web đọc file này để vẽ biểu đồ).
"""
from __future__ import annotations
import argparse
import json
import os
import sys
import time
import zlib
import numpy as np

from .segment import CLASS_NAMES

N_CLASSES = 32


def log(out: str, **kw):
    kw["t"] = round(time.time(), 1)
    with open(os.path.join(out, "log.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps(kw, ensure_ascii=False) + "\n")
    print(kw, flush=True)


def build_model():
    import torch.nn as nn

    class Net(nn.Module):
        def __init__(self):
            super().__init__()

            def block(i, o):
                return nn.Sequential(nn.Conv2d(i, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True))
            self.features = nn.Sequential(block(1, 32), block(32, 32), nn.MaxPool2d(2),
                                          block(32, 64), block(64, 64), nn.MaxPool2d(2),
                                          block(64, 128), nn.MaxPool2d(2))           # 28 -> 14 -> 7 -> 3
            self.head = nn.Sequential(nn.Flatten(), nn.Dropout(0.4), nn.Linear(128 * 3 * 3, 256), nn.ReLU(inplace=True),
                                      nn.Dropout(0.3), nn.Linear(256, N_CLASSES))

        def forward(self, x):            # x: 0..255
            return self.head(self.features(x / 255.0))      # logits

    return Net()


def augment(x, strength: float):
    """x: [B,1,28,28] float 0..255 (GPU/CPU). Dịch/xoay/co giãn nhẹ, dày/mỏng nét, nhiễu, mờ nhẹ."""
    import torch
    import torch.nn.functional as F
    B = x.shape[0]
    dev = x.device
    ang = (torch.rand(B, device=dev) * 2 - 1) * 0.17 * strength
    sc = 1 + (torch.rand(B, device=dev) * 2 - 1) * 0.12 * strength
    sx = sc * (1 + (torch.rand(B, device=dev) * 2 - 1) * 0.12 * strength)
    sh = (torch.rand(B, device=dev) * 2 - 1) * 0.15 * strength
    tx = (torch.rand(B, device=dev) * 2 - 1) * 0.10 * strength
    ty = (torch.rand(B, device=dev) * 2 - 1) * 0.10 * strength
    cos, sin = torch.cos(ang), torch.sin(ang)
    theta = torch.zeros(B, 2, 3, device=dev)
    theta[:, 0, 0] = cos / sx
    theta[:, 0, 1] = (-sin + sh) / sx
    theta[:, 1, 0] = sin / sc
    theta[:, 1, 1] = cos / sc
    theta[:, 0, 2] = tx
    theta[:, 1, 2] = ty
    grid = F.affine_grid(theta, x.shape, align_corners=False)
    x = F.grid_sample(x, grid, mode="bilinear", padding_mode="zeros", align_corners=False)
    r = torch.rand(B, 1, 1, 1, device=dev)
    dil = F.max_pool2d(x, 3, 1, 1)
    ero = -F.max_pool2d(-x, 3, 1, 1)
    p = 0.25 * strength
    x = torch.where(r < p, dil, torch.where(r > 1 - p, ero, x))
    blur = F.avg_pool2d(F.pad(x, (1, 1, 1, 1), mode="replicate"), 3, 1)
    x = torch.where(torch.rand(B, 1, 1, 1, device=dev) < 0.2 * strength, blur, x)
    x = x + torch.randn_like(x) * 12 * strength * (torch.rand(B, 1, 1, 1, device=dev) < 0.5)
    return x.clamp(0, 255)


def split(names, real, val_real=0.2, val_syn=0.1):
    """Chia val CỐ ĐỊNH theo tên file (băm): một ảnh đã ở val thì luôn ở val ở mọi lần train,
    nên khi học tiếp từ model cũ không bị rò rỉ ảnh val vào train, dù bạn thêm/xoá dữ liệu."""
    h = np.array([zlib.crc32(n.encode("utf-8")) % 10000 / 10000.0 for n in names])
    val = h < np.where(real, val_real, val_syn)
    return ~val, val


def select_by_manifest(names, only_from=None, exclude_from=None):
    """Lọc dữ liệu theo "sổ ghi" (manifest.json) của một lần train trước.
    only_from    : chỉ giữ đúng các file lần đó đã dùng (tái lập).
    exclude_from : bỏ các file mà model của lần đó đã học (kể cả những gì nó kế thừa) → chỉ còn dữ liệu MỚI."""
    if only_from:
        keep = set(json.load(open(only_from, encoding="utf-8"))["files"])
        return np.array([n in keep for n in names], bool)
    if exclude_from:
        m = json.load(open(exclude_from, encoding="utf-8"))
        seen = set(m.get("lineage") or m["files"])
        return np.array([n not in seen for n in names], bool)
    return None


def sample_weights(names, real, real_weight: float, hard_weight: float):
    """Xác suất lấy mẫu: ảnh thật nhiều hơn ảnh tổng hợp (real_weight); ký tự mà hệ thống ĐÃ ĐỌC SAI (file hard_*) nhiều hơn nữa (hard_weight)."""
    hard = np.array([os.path.basename(n).startswith("hard_") for n in names], bool)
    w = np.where(real, float(real_weight), 1.0) * np.where(hard, float(hard_weight), 1.0)
    return w / w.sum()


def export_onnx(model, path: str):
    import torch
    import torch.nn as nn

    class Wrap(nn.Module):
        def __init__(self, m):
            super().__init__(); self.m = m
        def forward(self, x):
            return torch.softmax(self.m(x), dim=1)

    w = Wrap(model).eval().cpu()
    dummy = torch.zeros(2, 1, 28, 28)
    kw = dict(input_names=["input"], output_names=["probs"], dynamic_axes={"input": {0: "N"}, "probs": {0: "N"}}, opset_version=13)
    try:
        torch.onnx.export(w, dummy, path, dynamo=False, **kw)       # torch >= 2.5: ép dùng bộ xuất cổ điển (gọn, không cần onnxscript)
    except TypeError:
        torch.onnx.export(w, dummy, path, **kw)
    # kiểm chứng: onnxruntime cho kết quả trùng PyTorch
    import onnxruntime as ort
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    x = np.random.default_rng(1).integers(0, 256, (16, 1, 28, 28)).astype(np.float32)
    with torch.no_grad():
        ref = w(torch.from_numpy(x)).numpy()
    got = sess.run(None, {sess.get_inputs()[0].name: x})[0]
    return float(np.abs(ref - got).max())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/chars")
    ap.add_argument("--out", required=True)
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--batch", type=int, default=128)
    ap.add_argument("--lr", type=float, default=2e-3)
    ap.add_argument("--aug", type=float, default=1.0, help="0 = tắt tăng cường dữ liệu")
    ap.add_argument("--real-weight", type=int, default=4, help="ảnh thật được lặp lại N lần mỗi epoch so với ảnh tổng hợp")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--patience", type=int, default=10)
    ap.add_argument("--threads", type=int, default=0, help="số luồng CPU (0 = tự chọn; đặt thấp để không ảnh hưởng việc khác)")
    ap.add_argument("--hard-weight", type=int, default=3, help="ký tự bị đọc sai (hard_*) được lặp lại thêm N lần")
    ap.add_argument("--init", default="none", help="đường dẫn best.pt của lần train trước để học tiếp (none = học từ đầu)")
    ap.add_argument("--init-lr-scale", type=float, default=0.3, help="nhân learning rate khi học tiếp")
    ap.add_argument("--only-from", default="", help="manifest.json của lần train trước: chỉ dùng đúng dữ liệu của lần đó")
    ap.add_argument("--exclude-from", default="", help="manifest.json của lần train trước: bỏ dữ liệu model đã học, chỉ dùng dữ liệu mới")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    try:
        import torch
        import torch.nn.functional as F
    except ImportError:
        log(a.out, type="error", msg="Chưa cài PyTorch. Chạy: pip install torch (xem README).")
        sys.exit(2)
    torch.manual_seed(a.seed)
    np.random.seed(a.seed)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    if dev == "cpu":
        torch.set_num_threads(a.threads if a.threads > 0 else max(1, (os.cpu_count() or 4) - 1))

    # --data trỏ thẳng vào thư mục chars
    from .dataset import Data as D
    d = D.__new__(D); d.chars = a.data
    X, y, real, names = D.load_arrays(d)
    if len(X) == 0:
        log(a.out, type="error", msg="Chưa có dữ liệu. Hãy sinh dữ liệu tổng hợp hoặc nhập ảnh biển trước.")
        sys.exit(3)
    mask = select_by_manifest(names, a.only_from or None, a.exclude_from or None)
    data_mode = "only" if a.only_from else ("new" if a.exclude_from else "all")
    if mask is not None:
        idx = np.where(mask)[0]
        X, y, real = X[idx], y[idx], real[idx]
        names = [names[i] for i in idx]
        if len(X) == 0:
            log(a.out, type="error", msg="Không có dữ liệu nào thoả điều kiện đã chọn (không có dữ liệu mới nào chưa train).")
            sys.exit(3)
    counts = np.bincount(y, minlength=N_CLASSES)
    empty = [CLASS_NAMES[i] for i in range(N_CLASSES) if counts[i] == 0]
    tr, va = split(names, real)
    log(a.out, type="info", dev=dev, data_mode=data_mode, hard=int(sum(os.path.basename(n).startswith("hard_") for n in names)), samples=int(len(X)), real=int(real.sum()), synthetic=int((~real).sum()),
        train=int(tr.sum()), val=int(va.sum()), val_real=int((va & real).sum()), empty_classes=empty, args=vars(a))

    Xt = torch.from_numpy(X[tr]).float().unsqueeze(1).to(dev)
    yt = torch.from_numpy(y[tr]).to(dev)
    rt = torch.from_numpy(real[tr]).to(dev)
    Xv = torch.from_numpy(X[va]).float().unsqueeze(1).to(dev)
    yv = torch.from_numpy(y[va]).to(dev)
    rv = real[va]

    # lấy mẫu có trọng số: ảnh thật xuất hiện nhiều hơn
    names_tr = [names[i] for i in np.where(tr)[0]]
    w = sample_weights(names_tr, real[tr], a.real_weight, a.hard_weight)
    n_per_epoch = int(len(Xt) + (a.real_weight - 1) * real[tr].sum()) if a.real_weight > 1 else len(Xt)
    n_per_epoch = max(a.batch, min(n_per_epoch, 60000))

    model = build_model().to(dev)
    init_loaded = False
    if a.init and a.init != "none":
        try:
            model.load_state_dict(torch.load(a.init, map_location=dev))
            init_loaded = True
        except Exception as e:                       # kiến trúc đổi / file hỏng → học từ đầu
            log(a.out, type="info", msg=f"Không nạp được model cũ ({str(e)[:120]}); học lại từ đầu.")
    # sổ ghi dữ liệu của lần này: files = đã dùng trực tiếp; lineage = cộng cả những gì kế thừa từ model cha
    lineage = set(names)
    if init_loaded:
        try:
            pm = json.load(open(os.path.join(os.path.dirname(a.init), "manifest.json"), encoding="utf-8"))
            lineage |= set(pm.get("lineage") or pm["files"])
        except Exception:
            pass
    json.dump({"mode": data_mode, "files": names, "lineage": sorted(lineage), "created": time.strftime("%Y-%m-%d %H:%M:%S"),
               "parent": os.path.basename(os.path.dirname(a.init)) if init_loaded else None},
              open(os.path.join(a.out, "manifest.json"), "w", encoding="utf-8"))
    lr = a.lr * (a.init_lr_scale if init_loaded else 1.0)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    steps = max(1, n_per_epoch // a.batch)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=lr, total_steps=a.epochs * steps)
    best_key, best_ep, bad = -1.0, -1, 0
    best_path = os.path.join(a.out, "best.pt")
    use_real_val = (va & real).sum() >= 30
    t0 = time.time()

    def evaluate():
        model.eval()
        with torch.no_grad():
            lg = torch.cat([model(Xv[i:i + 1024]) for i in range(0, len(Xv), 1024)])
            loss = F.cross_entropy(lg, yv).item()
            pred = lg.argmax(1).cpu().numpy()
        yvn = yv.cpu().numpy()
        acc = float((pred == yvn).mean())
        acc_real = float((pred[rv] == yvn[rv]).mean()) if rv.any() else None
        return loss, acc, acc_real, pred, yvn

    start_val = None
    if init_loaded:                                   # điểm xuất phát = chính model cũ; chỉ thay khi có bản tốt hơn
        vl0, vacc0, vreal0, _, _ = evaluate()
        best_key = vreal0 if (use_real_val and vreal0 is not None) else vacc0
        best_ep = 0
        start_val = {"val_acc": round(vacc0, 4), "val_acc_real": None if vreal0 is None else round(vreal0, 4)}
        torch.save(model.state_dict(), best_path)
        log(a.out, type="info", msg="Học tiếp từ " + os.path.basename(os.path.dirname(a.init)), start=start_val, lr=round(lr, 6))

    for ep in range(1, a.epochs + 1):
        model.train()
        tl, n = 0.0, 0
        for _ in range(steps):
            idx = torch.from_numpy(np.random.choice(len(Xt), a.batch, p=w)).to(dev)
            xb, yb = Xt[idx], yt[idx]
            if a.aug > 0:
                xb = augment(xb, a.aug)
            loss = F.cross_entropy(model(xb), yb, label_smoothing=0.05)
            opt.zero_grad(set_to_none=True)
            loss.backward()
            opt.step()
            sched.step()
            tl += loss.item() * len(yb); n += len(yb)
        vl, vacc, vreal, pred, yvn = evaluate()
        key = vreal if (use_real_val and vreal is not None) else vacc
        improved = key > best_key + 1e-6
        if improved:
            best_key, best_ep, bad = key, ep, 0
            torch.save(model.state_dict(), best_path)
        else:
            bad += 1
        log(a.out, type="epoch", epoch=ep, epochs=a.epochs, train_loss=round(tl / n, 4), val_loss=round(vl, 4),
            val_acc=round(vacc, 4), val_acc_real=None if vreal is None else round(vreal, 4), best=improved,
            lr=round(sched.get_last_lr()[0], 6), sec=round(time.time() - t0, 1))
        if bad >= a.patience:
            log(a.out, type="info", msg=f"Dừng sớm: {a.patience} epoch không cải thiện.")
            break

    model.load_state_dict(torch.load(best_path, map_location=dev))
    model.to(dev)
    vl, vacc, vreal, pred, yvn = evaluate()
    per = {}
    conf: dict[str, int] = {}
    for c in range(N_CLASSES):
        m = yvn == c
        if m.any():
            per[CLASS_NAMES[c]] = round(float((pred[m] == c).mean()), 3)
    for t, p in zip(yvn, pred):
        if t != p:
            k = f"{CLASS_NAMES[t]}→{CLASS_NAMES[p]}"
            conf[k] = conf.get(k, 0) + 1
    onnx_path = os.path.join(a.out, "char_cnn.onnx")
    diff = export_onnx(model, onnx_path)
    report = {"val_acc": vacc, "val_acc_real": vreal, "best_epoch": best_ep, "per_class": per,
              "top_confusions": sorted(conf.items(), key=lambda kv: -kv[1])[:15], "onnx_max_diff_vs_torch": diff,
              "alphabet": "".join(CLASS_NAMES[:31]), "onnx": onnx_path,
              "parent": os.path.basename(os.path.dirname(a.init)) if init_loaded else None, "start_val": start_val}
    json.dump(report, open(os.path.join(a.out, "report.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    log(a.out, type="done", **{k: v for k, v in report.items() if k != "per_class"})


if __name__ == "__main__":
    main()
