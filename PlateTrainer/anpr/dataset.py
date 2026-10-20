"""Quản lý dữ liệu: thư mục lớp, nhập ảnh biển (tên file = biển đúng), hàng đợi duyệt nhãn, thống kê."""
from __future__ import annotations
import base64
import json
import os
import re
import shutil
import uuid
import cv2
import numpy as np
from .segment import ALPHABET, CLASS_NAMES, VARIANTS, mask_to_28, prepare_gray, segment_variant, to_28
from .reader import label_from_filename

IMG_EXT = (".jpg", ".jpeg", ".png", ".bmp", ".webp")


def _inside(child: str, parent: str) -> bool:
    try:
        return os.path.commonpath([os.path.abspath(child), os.path.abspath(parent)]) == os.path.abspath(parent)
    except ValueError:            # khác ổ đĩa
        return False


def _copy_new(src: str, dst: str, skip_prefix: str | None = None) -> tuple[int, int]:
    """Chép file chưa có (tên file là duy nhất nên trùng tên = cùng ảnh). Trả (đã chép, đã có sẵn)."""
    os.makedirs(dst, exist_ok=True)
    done = skipped = 0
    for f in os.listdir(src):
        sp = os.path.join(src, f)
        if not os.path.isfile(sp) or (skip_prefix and f.startswith(skip_prefix)):
            continue
        tp = os.path.join(dst, f)
        if os.path.exists(tp):
            skipped += 1
        else:
            shutil.copy2(sp, tp)
            done += 1
    return done, skipped


class Data:
    def __init__(self, root: str, fonts: str | None = None):
        self.root = root
        self.chars = os.path.join(root, "chars")
        self.plates = os.path.join(root, "plates")
        self.plates_test = os.path.join(root, "plates_test")
        self.pending = os.path.join(root, "pending")
        self.fonts = fonts or os.path.join(os.path.dirname(root.rstrip("/\\")) or ".", "fonts")
        for d in [self.chars, self.plates, self.plates_test, self.pending, self.fonts] + [os.path.join(self.chars, c) for c in CLASS_NAMES]:
            os.makedirs(d, exist_ok=True)

    # ---- thống kê
    def stats(self) -> dict:
        per = {}
        for c in CLASS_NAMES:
            fs = os.listdir(os.path.join(self.chars, c))
            per[c] = {"syn": sum(f.startswith("syn_") for f in fs), "real": sum(not f.startswith("syn_") for f in fs)}
        return {"per_class": per, "plates": len(os.listdir(self.plates)), "plates_test": len(os.listdir(self.plates_test)),
                "pending": len(self._index())}

    # ---- lưu / nạp bộ dữ liệu thành thư mục (cấu trúc giống hệt kho dữ liệu: chars/, plates/, plates_test/, runs/)
    def export_to(self, dest: str, include_syn=False, runs_dir: str | None = None, include_runs=True) -> dict:
        if _inside(dest, self.root):
            raise ValueError("Thư mục đích nằm trong (hoặc trùng) kho dữ liệu hiện tại — hãy chọn thư mục khác.")
        if include_runs and runs_dir and _inside(dest, runs_dir):
            raise ValueError("Thư mục đích nằm trong thư mục các lần train (runs) — hãy chọn thư mục khác.")
        os.makedirs(dest, exist_ok=True)
        n = {"real": 0, "syn": 0, "plates": 0, "plates_test": 0, "runs": 0}
        for c in CLASS_NAMES:
            dd = os.path.join(dest, "chars", c)
            _copy_new(os.path.join(self.chars, c), dd, None if include_syn else "syn_")
            for f in os.listdir(dd):
                n["syn" if f.startswith("syn_") else "real"] += 1       # đếm theo thực tế ở đích
        for key, src in (("plates", self.plates), ("plates_test", self.plates_test)):
            _copy_new(src, os.path.join(dest, key))
            n[key] = len(os.listdir(os.path.join(dest, key)))
        if include_runs and runs_dir and os.path.isdir(runs_dir):
            for r in sorted(os.listdir(runs_dir)):
                sp, tp = os.path.join(runs_dir, r), os.path.join(dest, "runs", r)
                if os.path.isdir(sp) and not os.path.exists(tp):
                    shutil.copytree(sp, tp)
                    n["runs"] += 1
        json.dump({"created": __import__("time").strftime("%Y-%m-%d %H:%M:%S"), "app": "PlateTrainer", "counts": n,
                   "note": "Cấu trúc giống kho dữ liệu: có thể đặt thư mục này làm kho dữ liệu hoặc gộp vào kho khác."},
                  open(os.path.join(dest, "dataset.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        return n

    def merge_from(self, src: str, runs_dir: str | None = None) -> dict:
        if not os.path.isdir(os.path.join(src, "chars")):
            raise ValueError("Thư mục nguồn không có thư mục con 'chars' — không phải bộ dữ liệu PlateTrainer.")
        if _inside(src, self.root) or _inside(self.root, src):
            raise ValueError("Thư mục nguồn trùng hoặc lồng với kho dữ liệu hiện tại.")
        n = {"chars_added": 0, "chars_existing": 0, "plates_added": 0, "runs_added": 0}
        for c in CLASS_NAMES:
            sd = os.path.join(src, "chars", c)
            if os.path.isdir(sd):
                d, k = _copy_new(sd, os.path.join(self.chars, c))
                n["chars_added"] += d; n["chars_existing"] += k
        for key, dst in (("plates", self.plates), ("plates_test", self.plates_test)):
            sd = os.path.join(src, key)
            if os.path.isdir(sd):
                d, _ = _copy_new(sd, dst)
                n["plates_added"] += d
        rs = os.path.join(src, "runs")
        if runs_dir and os.path.isdir(rs):
            os.makedirs(runs_dir, exist_ok=True)
            for r in sorted(os.listdir(rs)):
                if os.path.isdir(os.path.join(rs, r)) and not os.path.exists(os.path.join(runs_dir, r)):
                    shutil.copytree(os.path.join(rs, r), os.path.join(runs_dir, r))
                    n["runs_added"] += 1
        return n

    # ---- lưu ký tự
    def save_char(self, img28: np.ndarray, label: str, prefix: str = "real") -> str:
        name = "_bg" if label in ("_bg", "", "·") else label
        if name not in CLASS_NAMES:
            raise ValueError(f"Nhãn không hợp lệ: {label}")
        path = os.path.join(self.chars, name, f"{prefix}_{uuid.uuid4().hex[:10]}.png")
        cv2.imwrite(path, img28)
        return path

    # ---- hàng đợi duyệt
    def _index_path(self): return os.path.join(self.pending, "index.json")

    def _index(self) -> list[dict]:
        try:
            return json.load(open(self._index_path(), encoding="utf-8"))
        except Exception:
            return []

    def _save_index(self, items): json.dump(items, open(self._index_path(), "w", encoding="utf-8"), ensure_ascii=False)

    def list_pending(self, limit=400) -> list[dict]:
        return self._index()[:limit]

    def pending_png(self, pid: str) -> str: return os.path.join(self.pending, pid + ".png")

    def resolve(self, pid: str, label: str | None):
        """label=None → bỏ mẫu này; còn lại → lưu vào lớp."""
        items = self._index()
        keep = []
        for it in items:
            if it["id"] == pid:
                p = self.pending_png(pid)
                if label is not None and os.path.exists(p):
                    self.save_char(cv2.imread(p, 0), label, "real")
                if os.path.exists(p):
                    os.remove(p)
            else:
                keep.append(it)
        self._save_index(keep)

    def accept_matched(self) -> int:
        items = self._index()
        keep, n = [], 0
        for it in items:
            if it.get("matched") and it.get("guess"):
                p = self.pending_png(it["id"])
                if os.path.exists(p):
                    self.save_char(cv2.imread(p, 0), it["guess"], "real")
                    os.remove(p)
                n += 1
            else:
                keep.append(it)
        self._save_index(keep)
        return n

    def clear_pending(self):
        for it in self._index():
            p = self.pending_png(it["id"])
            if os.path.exists(p):
                os.remove(p)
        self._save_index([])

    # ---- nhập ảnh biển
    def import_plate(self, fname: str, data: bytes, to_test: bool, suggest=None) -> dict:
        label = label_from_filename(fname)
        arr = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
        res = {"file": fname, "label": label, "status": "", "chars": 0}
        if arr is None:
            res["status"] = "không đọc được ảnh"
            return res
        if not (7 <= len(label) <= 9):
            res["status"] = f"tên file không giống biển số (nhận '{label}')"
            return res
        base = fname.replace("\\", "/").split("/")[-1]
        dest = self.plates_test if to_test else self.plates
        cv2.imwrite(os.path.join(dest, os.path.splitext(base)[0] + ".png"), arr)
        if to_test:
            res["status"] = "đưa vào tập kiểm thử (không dùng để huấn luyện)"
            return res
        gray = prepare_gray(arr)
        best = None
        for adaptive, invert in VARIANTS:
            lines = segment_variant(gray, adaptive, invert)
            if not lines:
                continue
            flat = [c for ln in lines for c in ln]
            diff = abs(len(flat) - len(label))
            if best is None or diff < best[0]:
                best = (diff, flat)
            if diff == 0:
                break
        if best is None:
            res["status"] = "không tách được ký tự nào"
            return res
        diff, flat = best
        matched = diff == 0
        items = self._index()
        sugg = None
        if not matched and suggest is not None:
            try:
                sugg = suggest.classify([c for c in flat])[0]
            except Exception:
                sugg = None
        for i, c in enumerate(flat):
            pid = uuid.uuid4().hex[:12]
            cv2.imwrite(self.pending_png(pid), to_28(c))
            guess = label[i] if matched else (("" if sugg is None or sugg[i] == 31 else ALPHABET[sugg[i]]))
            items.append({"id": pid, "plate": label, "idx": i, "guess": guess, "matched": matched})
        self._save_index(items)
        res["chars"] = len(flat)
        res["status"] = "khớp số ký tự — có thể chấp nhận tự động" if matched else f"tách được {len(flat)} ký tự, biển có {len(label)} — cần duyệt tay"
        return res

    # ---- mẫu "sửa biển" gửi từ ParkingGuidance (tab Mô phỏng): ảnh biển + biển đúng + biển hệ thống đã đọc ----
    # Tên file ký tự có thẻ "_c<tag>_" (tag = 10 ký tự hex của khoá mẫu) để lần sau nạp lại mẫu đã sửa thì THAY chứ không nhân đôi,
    # và để xoá đúng ký tự của mẫu mà bạn đã xoá ở ParkingGuidance.
    _TAG_RE = re.compile(r"_c([0-9a-f]{10})[_.]")

    def remove_correction(self, tag: str) -> int:
        n = 0
        for c in CLASS_NAMES:
            d = os.path.join(self.chars, c)
            for f in os.listdir(d):
                if f"_c{tag}_" in f:
                    os.remove(os.path.join(d, f)); n += 1
        for f in os.listdir(self.plates):
            if f.endswith(f"_c{tag}.png"):
                os.remove(os.path.join(self.plates, f)); n += 1
        return n

    def sync_corrections(self, valid_tags: set[str]) -> int:
        """Xoá ký tự/ảnh biển của các mẫu đã bị xoá ở ParkingGuidance (tag không còn trong danh sách)."""
        gone = set()
        for folder in [os.path.join(self.chars, c) for c in CLASS_NAMES] + [self.plates]:
            for f in os.listdir(folder):
                m = self._TAG_RE.search(f)
                if m and m.group(1) not in valid_tags:
                    gone.add(m.group(1))
        return sum(self.remove_correction(t) for t in gone)

    @staticmethod
    def _char_from_box(gray: np.ndarray, f: float, box: dict) -> np.ndarray | None:
        """Ảnh 28x28 của 1 ký tự do người dùng khoanh tay (box theo pixel ảnh biển gốc; gray đã phóng f lần).
        Nhị phân hoá riêng trong khung (Otsu), lấy phía ít hơn làm mực, bỏ nhiễu nhỏ rồi cắt sát + đệm vuông như C#."""
        H, W = gray.shape
        x0 = max(0, int(round(box["x"] * f))); y0 = max(0, int(round(box["y"] * f)))
        x1 = min(W, int(round((box["x"] + box["w"]) * f))); y1 = min(H, int(round((box["y"] + box["h"]) * f)))
        if x1 - x0 < 4 or y1 - y0 < 6:
            return None
        roi = cv2.GaussianBlur(gray[y0:y1, x0:x1], (3, 3), 0)
        _, b = cv2.threshold(roi, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
        if (b > 0).mean() > 0.5:
            b = cv2.bitwise_not(b)                      # mực luôn là phía chiếm ít diện tích hơn
        n, labels, stats, _ = cv2.connectedComponentsWithStats(b, connectivity=8)
        if n <= 1:
            return None
        areas = stats[1:, cv2.CC_STAT_AREA]
        keep = [i + 1 for i, a in enumerate(areas) if a >= 0.15 * areas.max()]
        mask = np.isin(labels, keep).astype(np.uint8) * 255
        return mask_to_28(mask)

    def import_correction(self, png: bytes, label: str, recognized: str, tag: str, boxes: list | None = None) -> dict:
        """label = biển ĐÚNG (chỉ A-Z0-9). recognized = biển hệ thống đã đọc (để biết ký tự nào bị đọc sai → học kỹ hơn).
        boxes (tuỳ chọn) = các ô ký tự người dùng khoanh tay [{x,y,w,h,c}] theo pixel ảnh biển: có thì học THẲNG từ các ô,
        không phụ thuộc thuật toán tự tách (dùng khi tự tách sai/thiếu ký tự)."""
        label = re.sub(r"[^A-Z0-9]", "", (label or "").upper())
        recognized = re.sub(r"[^A-Z0-9]", "", (recognized or "").upper())
        res = {"tag": tag, "label": label, "used": False, "chars": 0, "hard": 0, "status": ""}
        arr = cv2.imdecode(np.frombuffer(png, np.uint8), cv2.IMREAD_COLOR)
        if arr is None:
            res["status"] = "không đọc được ảnh"; return res
        if not (7 <= len(label) <= 9) or any(ch not in ALPHABET for ch in label):
            res["status"] = f"biển '{label}' không hợp lệ (7–9 ký tự; biển VN không dùng I, J, O, Q, W)"; return res
        self.remove_correction(tag)
        cv2.imwrite(os.path.join(self.plates, f"{label}_c{tag}.png"), arr)      # để đo độ chính xác cả biển
        gray = prepare_gray(arr)
        if boxes:
            return self._import_boxes(arr, gray, label, recognized, tag, boxes, res)
        best = None
        for adaptive, invert in VARIANTS:
            lines = segment_variant(gray, adaptive, invert)
            if not lines:
                continue
            flat = [c for ln in lines for c in ln]
            diff = abs(len(flat) - len(label))
            if best is None or diff < best[0]:
                best = (diff, flat)
            if diff == 0:
                break
        if best is None or best[0] != 0:
            n = 0 if best is None else len(best[1])
            res["status"] = f"tách được {n} ký tự, biển có {len(label)} — không học được từ mẫu này (vẫn dùng để đo)"; return res
        flat = best[1]
        if recognized == label:
            hard = [False] * len(label)
        elif len(recognized) == len(label):
            hard = [recognized[i] != label[i] for i in range(len(label))]
        else:
            hard = [True] * len(label)                 # độ dài lệch → không gióng được từng ký tự: coi cả biển là khó
        for i, c in enumerate(flat):
            prefix = "hard" if hard[i] else "real"
            folder = os.path.join(self.chars, label[i])
            cv2.imwrite(os.path.join(folder, f"{prefix}_c{tag}_{i:02d}.png"), to_28(c))
        res.update(used=True, chars=len(flat), hard=int(sum(hard)), status="đã học")
        return res

    def _import_boxes(self, arr, gray, label, recognized, tag, boxes, res) -> dict:
        """Học từ các ô ký tự khoanh tay. Thứ tự ô = thứ tự đọc (dòng trên → dưới, trái → phải) nên khớp `label`."""
        try:
            bs = [b for b in boxes if isinstance(b, dict) and all(k in b for k in ("x", "y", "w", "h", "c"))]
            if len(bs) != len(label):
                res["status"] = f"khoanh {len(bs)} ô ký tự nhưng biển có {len(label)} — không học được từ mẫu này (vẫn dùng để đo)"; return res
            f = gray.shape[0] / float(arr.shape[0])
            chars = [str(b["c"]).upper() for b in bs]
            if sorted(chars) != sorted(label):
                res["status"] = "ký tự gắn ở các ô không khớp với biển đã nhập — không học được từ mẫu này (vẫn dùng để đo)"; return res
            imgs = []
            for b in bs:
                im = self._char_from_box(gray, f, b)
                if im is None:
                    res["status"] = f"ô ký tự '{b['c']}' quá nhỏ hoặc không có nét chữ — không học được từ mẫu này (vẫn dùng để đo)"; return res
                imgs.append(im)
        except (TypeError, ValueError, KeyError):
            res["status"] = "dữ liệu ô ký tự không hợp lệ — không học được từ mẫu này (vẫn dùng để đo)"; return res
        # gióng ký tự "khó": so từng vị trí theo thứ tự đọc (giống nhánh tự tách)
        order = sorted(range(len(bs)), key=lambda i: (round((bs[i]["y"] + bs[i]["h"] / 2.0) / max(1.0, float(np.median([b["h"] for b in bs])) * 0.6)), bs[i]["x"]))
        if recognized == label:
            hard = {i: False for i in order}
        elif len(recognized) == len(label):
            hard = {i: recognized[k] != label[k] for k, i in enumerate(order)}
        else:
            hard = {i: True for i in order}
        for k, i in enumerate(order):
            prefix = "hard" if hard[i] else "real"
            folder = os.path.join(self.chars, chars[i])
            cv2.imwrite(os.path.join(folder, f"{prefix}_c{tag}_{k:02d}.png"), imgs[i])
        res.update(used=True, chars=len(imgs), hard=int(sum(hard.values())), status="đã học (ô ký tự khoanh tay)")
        return res

    # ---- nạp cho huấn luyện
    def load_arrays(self):
        """Trả (ảnh, nhãn, là-ảnh-thật, tên "lớp/file") — tên dùng để chia train/val cố định giữa các lần train."""
        xs, ys, real, names = [], [], [], []
        for ci, c in enumerate(CLASS_NAMES):
            d = os.path.join(self.chars, c)
            for f in os.listdir(d):
                im = cv2.imread(os.path.join(d, f), 0)
                if im is None or im.shape != (28, 28):
                    continue
                xs.append(im); ys.append(ci); real.append(not f.startswith("syn_")); names.append(f"{c}/{f}")
        return np.array(xs, np.uint8), np.array(ys, np.int64), np.array(real, bool), names


def png_b64(img: np.ndarray, scale: int = 1) -> str:
    if scale != 1:
        img = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_NEAREST)
    return "data:image/png;base64," + base64.b64encode(cv2.imencode(".png", img)[1]).decode()
