"""Tự động học từ các biển ĐÃ SỬA ở tab Mô phỏng của ParkingGuidance.

ParkingGuidance gọi lệnh này (một tiến trình riêng, ưu tiên thấp, chỉ chạy khi bấm Train, xong là thoát):

    python -m anpr.autotrain --corrections <thư mục mẫu> --current-model <char_cnn.onnx đang chạy> --status <status.json>

Các bước (ghi tiến độ vào status.json cho giao diện):
  1. import   : nạp từng mẫu {ảnh biển, biển ĐÚNG, biển hệ thống đã đọc}; tách ký tự bằng đúng thuật toán của C#. Ký tự mà hệ thống
                đọc sai được đánh dấu "hard_" để học kỹ hơn; mẫu bị xoá ở ParkingGuidance thì xoá luôn ký tự tương ứng.
  2. synth    : nếu kho chưa có dữ liệu tổng hợp thì sinh sẵn (phủ đủ 31 ký tự + lớp nền).
  3. train    : học tiếp từ lần train gần nhất (nếu có), giữ mọi dữ liệu cũ + mẫu mới ("bổ sung vào mô hình").
  4. evaluate : so model mới với model đang chạy: (a) đúng cả biển trên các biển bạn đã sửa/xác nhận, (b) đúng từng ký tự trên tập kiểm
                chứng chưa từng học. Chỉ khuyến nghị cài khi biển không kém đi và ký tự không tụt quá 2%.
  Việc CÀI model (sao lưu, chép đè, nạp lại không cần khởi động) do ParkingGuidance làm.
"""
from __future__ import annotations
import argparse
import json
import os
import re
import subprocess
import sys
import time
import traceback
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def resolve_paths() -> tuple[str, str]:
    """(thư mục dữ liệu, thư mục runs) — cùng quy tắc với app.py: config.json có data_dir thì dùng."""
    try:
        custom = (json.load(open(os.path.join(ROOT, "config.json"), encoding="utf-8")).get("data_dir") or "").strip()
    except Exception:
        custom = ""
    if custom:
        return os.path.abspath(custom), os.path.join(os.path.abspath(custom), "runs")
    return os.path.join(ROOT, "data"), os.path.join(ROOT, "runs")


class Status:
    def __init__(self, path: str | None):
        self.path = path
        self.d = {"stage": "import", "stageLabel": "Nhập mẫu đã sửa", "progress": 0, "message": "", "finished": False, "error": None,
                  "startedAt": time.strftime("%Y-%m-%d %H:%M:%S"), "import": None, "train": None, "result": None}

    def update(self, **kw):
        self.d.update(kw)
        if not self.path:
            return
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(self.d, f, ensure_ascii=False)
        os.replace(tmp, self.path)          # ghi nguyên tử: ParkingGuidance không bao giờ đọc phải file dở dang


def eval_char_acc(model_path: str, X: np.ndarray, y: np.ndarray) -> float | None:
    if len(X) == 0:
        return None
    import onnxruntime as ort
    sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    name = sess.get_inputs()[0].name
    correct = 0
    for i in range(0, len(X), 512):
        xb = X[i:i + 512][:, None].astype(np.float32)
        correct += int((sess.run(None, {name: xb})[0].argmax(1) == y[i:i + 512]).sum())
    return correct / len(X)


def eval_plate_acc(model_path: str, plates_dir: str) -> tuple[float | None, int]:
    import cv2
    from .reader import Reader, label_from_filename, normalize_key
    files = [f for f in sorted(os.listdir(plates_dir)) if f.lower().endswith((".png", ".jpg", ".jpeg", ".bmp"))]
    if not files:
        return None, 0
    rd = Reader(model_path)
    ok = 0
    for f in files:
        img = cv2.imread(os.path.join(plates_dir, f))
        if img is None:
            continue
        r = rd.read(img)
        ok += normalize_key(r["plate"] or "") == label_from_filename(f)
    return ok / len(files), len(files)


_SAMPLE_FILE = re.compile(r"^(?P<label>[A-Z0-9]+)_c(?P<tag>[0-9A-Za-z]+)\.png$")


def diagnose_plate(rd, img, label: str) -> dict:
    """Đọc 1 biển bằng model `rd` và cho biết LỖI Ở BƯỚC NÀO (để người dùng biết train có sửa được không):
      ok             = đọc đúng cả biển
      segmentation   = tách ra số ký tự khác số ký tự thật → lỗi ở bước TÁCH KÝ TỰ (xử lý ảnh), train model không sửa được
      classification = tách đủ ký tự nhưng model đọc sai một số ký tự → train thêm mẫu sẽ cải thiện
      format         = ký tự đúng nhưng bị chuẩn hoá/loại theo định dạng biển
    `diffs` liệt kê ký tự sai: vị trí, ký tự đúng, ký tự model đọc, xác suất."""
    from .reader import normalize_key
    r = rd.read(img)
    plate = r["plate"]
    out = {"plate": plate, "ok": normalize_key(plate or "") == label, "segCount": None, "stage": "ok", "diffs": []}
    best = None
    for v in r["variants"]:
        if not v.get("ok") or not v.get("chars"):
            continue
        chars = [c for c in v["chars"] if c["ch"] != "·"]
        rank = (abs(len(chars) - len(label)), not v.get("valid"), -v.get("conf", 0.0))
        if best is None or rank < best[0]:
            best = (rank, chars)
    if best is None:
        out["segCount"] = 0
        out["stage"] = "ok" if out["ok"] else "segmentation"
        return out
    chars = best[1]
    out["segCount"] = len(chars)
    if out["ok"]:
        return out
    if len(chars) != len(label):
        out["stage"] = "segmentation"
        return out
    for i, (c, e) in enumerate(zip(chars, label)):
        if c["ch"] != e:
            t3 = dict(c["top3"])
            out["diffs"].append({"pos": i + 1, "expected": e, "read": c["ch"], "pRead": round(float(c["p"]), 3),
                                 "pExpected": round(float(t3[e]), 3) if e in t3 else None})
    out["stage"] = "classification" if out["diffs"] else "format"
    return out


def sample_report(candidate: str, current: str, plates_dir: str, limit: int = 80) -> list:
    """Bảng từng biển do người dùng dạy (file `<biển>_c<tag>.png`): model cũ vs model mới đọc ra gì và lỗi ở bước nào."""
    import cv2
    from .reader import Reader
    files = [f for f in sorted(os.listdir(plates_dir)) if _SAMPLE_FILE.match(f)][:limit]
    if not files:
        return []
    rc = Reader(candidate)
    rcur = Reader(current) if current else None
    rows = []
    for f in files:
        m = _SAMPLE_FILE.match(f)
        img = cv2.imread(os.path.join(plates_dir, f))
        if img is None:
            continue
        label = m.group("label")
        new = diagnose_plate(rc, img, label)
        row = {"tag": m.group("tag"), "label": label, "candidate": new}
        if rcur is not None:
            row["current"] = diagnose_plate(rcur, img, label)
        rows.append(row)
    return rows


def latest_warm_start(runs_dir: str) -> str:
    if not os.path.isdir(runs_dir):
        return "none"
    cand = [r for r in sorted(os.listdir(runs_dir))
            if os.path.exists(os.path.join(runs_dir, r, "best.pt")) and os.path.exists(os.path.join(runs_dir, r, "report.json"))]
    return os.path.join(runs_dir, cand[-1], "best.pt") if cand else "none"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corrections", required=True, help="thư mục có index.json + <id>.png (ParkingGuidance ghi)")
    ap.add_argument("--current-model", default="", help="char_cnn.onnx đang chạy trong ParkingGuidance (để so sánh)")
    ap.add_argument("--status", default="", help="file JSON ghi tiến độ")
    ap.add_argument("--epochs", type=int, default=25)
    ap.add_argument("--threads", type=int, default=2)
    ap.add_argument("--per-class", type=int, default=300, help="số ảnh tổng hợp mỗi ký tự nếu kho chưa có")
    ap.add_argument("--min-auto-install", type=int, default=10,
                    help="số mẫu hợp lệ tối thiểu để khuyến nghị TỰ CÀI model mới (ít hơn thì chỉ cài thủ công — tránh học lệch từ vài biển)")
    ap.add_argument("--skip-train", action="store_true", help="(chẩn đoán/thử nghiệm) bỏ qua bước train, đánh giá --candidate")
    ap.add_argument("--candidate", default="", help="dùng kèm --skip-train: model ứng viên")
    a = ap.parse_args()

    st = Status(a.status or None)
    st.update()
    try:
        from .dataset import Data
        from .train import split
        from .reader import check_model_contract
        from . import synth

        data_root, runs_dir = resolve_paths()
        os.makedirs(runs_dir, exist_ok=True)
        data = Data(data_root, fonts=os.path.join(ROOT, "fonts"))

        # ---------------- 1. import ----------------
        index_path = os.path.join(a.corrections, "index.json")
        items = json.load(open(index_path, encoding="utf-8")) if os.path.exists(index_path) else []
        valid = {it["id"][:10] for it in items}
        removed = data.sync_corrections(valid)
        used = chars = hard = 0
        skipped = []
        for k, it in enumerate(items):
            png = os.path.join(a.corrections, it["id"] + ".png")
            if not os.path.exists(png):
                skipped.append({"key": it.get("key"), "reason": "thiếu file ảnh"}); continue
            boxes = None
            try:                                         # meta = JSON do tab Train gửi; "chars" = ô ký tự khoanh tay (nếu có)
                m = json.loads(it.get("meta") or "null")
                boxes = m.get("chars") if isinstance(m, dict) else None
            except ValueError:
                boxes = None
            r = data.import_correction(open(png, "rb").read(), it.get("correct", ""), it.get("recognized", ""), it["id"][:10], boxes)
            if r["used"]:
                used += 1; chars += r["chars"]; hard += r["hard"]
            else:
                skipped.append({"key": it.get("key"), "label": r["label"], "reason": r["status"]})
            st.update(progress=round(8 * (k + 1) / max(1, len(items))), message=f"Đã nạp {k + 1}/{len(items)} mẫu")
        st.update(**{"import": {"total": len(items), "used": used, "chars": chars, "hardChars": hard, "skipped": skipped,
                                "removedOld": removed}})
        if used == 0:
            raise RuntimeError("Không có mẫu nào dùng để học được: " +
                               ("; ".join(f"{s.get('label') or s.get('key')}: {s['reason']}" for s in skipped[:3]) or "danh sách trống") + ".")

        # ---------------- 2. synth ----------------
        syn_count = sum(f.startswith("syn_") for c in os.listdir(data.chars) for f in os.listdir(os.path.join(data.chars, c)))
        if syn_count == 0 and not a.skip_train:
            st.update(stage="synth", stageLabel="Sinh dữ liệu tổng hợp (lần đầu)", progress=10, message="Chưa có dữ liệu tổng hợp — sinh sẵn để phủ đủ ký tự")
            synth.generate(data.chars, data.fonts, per_class=a.per_class, bg_count=a.per_class * 2, seed=int(time.time()) % 100000)

        # ---------------- 3. train ----------------
        if a.skip_train:
            if not a.candidate or not os.path.exists(a.candidate):
                raise RuntimeError("--skip-train cần --candidate trỏ tới file model.")
            candidate, run_name = a.candidate, "(bỏ qua train)"
        else:
            run_name = time.strftime("%Y%m%d-%H%M%S")
            out = os.path.join(runs_dir, run_name)
            os.makedirs(out, exist_ok=True)
            init = latest_warm_start(runs_dir)
            cmd = [sys.executable, "-m", "anpr.train", "--data", data.chars, "--out", out, "--epochs", str(a.epochs),
                   "--threads", str(a.threads), "--patience", "8", "--real-weight", "4", "--hard-weight", "3",
                   "--init", init, "--init-lr-scale", "0.3"]
            st.update(stage="train", stageLabel="Huấn luyện" + (" (học tiếp từ lần trước)" if init != "none" else ""), progress=20,
                      message="Đang huấn luyện…")
            log_path = os.path.join(out, "log.jsonl")
            with open(os.path.join(out, "stdout.txt"), "w", encoding="utf-8") as so:
                proc = subprocess.Popen(cmd, cwd=ROOT, stdout=so, stderr=subprocess.STDOUT)
                seen = 0
                while proc.poll() is None:
                    time.sleep(1.0)
                    if os.path.exists(log_path):
                        lines = open(log_path, encoding="utf-8").read().splitlines()
                        for line in lines[seen:]:
                            try:
                                e = json.loads(line)
                            except Exception:
                                continue
                            if e.get("type") == "epoch":
                                st.update(progress=20 + round(70 * e["epoch"] / max(1, e["epochs"])),
                                          train={"epoch": e["epoch"], "epochs": e["epochs"], "val_acc": e["val_acc"],
                                                 "val_acc_real": e.get("val_acc_real")},
                                          message=f"Epoch {e['epoch']}/{e['epochs']} · val {e['val_acc'] * 100:.1f}%")
                        seen = len(lines)
            rc = proc.returncode
            err = None
            if os.path.exists(log_path):
                for line in open(log_path, encoding="utf-8").read().splitlines():
                    try:
                        e = json.loads(line)
                    except Exception:
                        continue
                    if e.get("type") == "error":
                        err = e.get("msg")
            candidate = os.path.join(out, "char_cnn.onnx")
            if rc != 0 or not os.path.exists(candidate):
                tail = ""
                try:
                    tail = open(os.path.join(out, "stdout.txt"), encoding="utf-8", errors="replace").read()[-400:]
                except Exception:
                    pass
                raise RuntimeError(err or f"Huấn luyện thất bại (mã {rc}). {tail}")

        # ---------------- 4. evaluate ----------------
        st.update(stage="evaluate", stageLabel="So sánh model mới với model đang chạy", progress=92, message="Đang đo độ chính xác…")
        contract = check_model_contract(candidate)
        if not contract["ok"]:
            raise RuntimeError("Model mới không đúng định dạng đầu vào/ra: " + json.dumps(contract))
        cur = a.current_model if a.current_model and os.path.exists(a.current_model) else ""
        plate_cand, n_plates = eval_plate_acc(candidate, data.plates)
        plate_cur = eval_plate_acc(cur, data.plates)[0] if cur else None
        X, y, real, names = data.load_arrays()
        _, va = split(names, real)
        char_cand = eval_char_acc(candidate, X[va], y[va])
        char_cur = eval_char_acc(cur, X[va], y[va]) if cur else None

        reasons = []
        ok_plate = plate_cur is None or plate_cand is None or plate_cand >= plate_cur
        ok_char = char_cur is None or char_cand is None or char_cand >= char_cur - 0.02
        if not ok_plate:
            reasons.append(f"đúng cả biển kém hơn model đang chạy ({plate_cand * 100:.0f}% so với {plate_cur * 100:.0f}%)")
        if not ok_char:
            reasons.append(f"đúng từng ký tự tụt quá 2% ({char_cand * 100:.1f}% so với {char_cur * 100:.1f}%)")
        if used < a.min_auto_install:
            reasons.append(f"mới có {used} mẫu hợp lệ (cần từ {a.min_auto_install} mẫu để tự cài; nếu tin model mới tốt bạn vẫn có thể cài thủ công)")
        recommend = bool(ok_plate and ok_char and used >= a.min_auto_install)
        try:
            samples_report = sample_report(candidate, cur, data.plates)
        except Exception:                                  # báo cáo chỉ để tham khảo: lỗi ở đây không được làm hỏng cả lần train
            samples_report = []
        result = {"candidate": os.path.abspath(candidate), "run": run_name, "plates": n_plates, "usedSamples": used,
                  "samples": samples_report,
                  "plateAccCurrent": plate_cur, "plateAccCandidate": plate_cand,
                  "charAccCurrent": char_cur, "charAccCandidate": char_cand, "valChars": int(va.sum()),
                  "recommendInstall": recommend,
                  "reason": "Model mới tốt hơn hoặc ngang model đang chạy." if recommend else "Không tự cài: " + "; ".join(reasons) + "."}
        st.update(stage="done", stageLabel="Hoàn tất", progress=100, message=result["reason"], finished=True, result=result)
        return 0
    except Exception as e:                                   # mọi lỗi đều ghi vào status để giao diện hiện rõ
        st.update(stage="error", stageLabel="Lỗi", finished=True, error=str(e) or e.__class__.__name__,
                  message=traceback.format_exc()[-600:])
        return 1


if __name__ == "__main__":
    sys.exit(main())
