"""Đọc biển số bằng model ONNX + thuật toán tách ký tự/bỏ phiếu giống CnnPlateReader.cs (dùng để kiểm thử/so sánh model)."""
from __future__ import annotations
import re
import cv2
import numpy as np
import onnxruntime as ort
from .segment import ALPHABET, BACKGROUND_CLASS, VARIANTS, prepare_gray, segment_variant, to_28
from .formatter import normalize_vn_plate

KNOWN_PROVINCES = {
    11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
    43, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75,
    76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 88, 89, 90, 92, 93, 94, 95, 97, 98, 99}


def check_model_contract(path: str) -> dict:
    """Kiểm tra model có đúng 'hợp đồng' mà CnnPlateReader.cs cần: vào [N,1,28,28] 0..255, ra [N,32] xác suất."""
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    inp, out = sess.get_inputs()[0], sess.get_outputs()[0]
    rng = np.random.default_rng(0)
    x = rng.integers(0, 256, (3, 1, 28, 28)).astype(np.float32)
    y = sess.run(None, {inp.name: x})[0]
    ok_shape = y.ndim == 2 and y.shape == (3, 32)
    sums = y.sum(axis=1) if ok_shape else np.array([0])
    ok_prob = bool(ok_shape and np.allclose(sums, 1.0, atol=1e-3) and y.min() >= 0)
    return {"input": [str(d) for d in inp.shape], "output": [str(d) for d in out.shape],
            "ok_shape": bool(ok_shape), "ok_probabilities": ok_prob, "ok": bool(ok_shape and ok_prob)}


class Reader:
    def __init__(self, model_path: str):
        self.sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
        self.inp = self.sess.get_inputs()[0].name

    def classify(self, line) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        crops = np.stack([to_28(c) for c in line])
        x = crops[:, None].astype(np.float32)
        probs = self.sess.run(None, {self.inp: x})[0]
        return probs.argmax(1), probs.max(1), probs

    def read(self, plate_bgr: np.ndarray) -> dict:
        """Trả về {'plate', 'score', 'variants': [...]} — plate=None nếu không đọc được."""
        gray = prepare_gray(plate_bgr)
        votes: dict[str, dict] = {}
        variants = []
        for adaptive, invert in VARIANTS:
            v = {"adaptive": adaptive, "invert": invert, "ok": False}
            lines = segment_variant(gray, adaptive, invert)
            if not lines:
                variants.append(v)
                continue
            texts, confs, detail = [], [], []
            for ln in lines:
                ids, best, probs = self.classify(ln)
                s = ""
                for i, c in enumerate(ln):
                    top = np.argsort(-probs[i])[:3]
                    detail.append({"box": [c.x, c.y, c.w, c.h], "id": int(ids[i]),
                                   "ch": "·" if ids[i] == BACKGROUND_CLASS else ALPHABET[ids[i]], "p": float(best[i]),
                                   "top3": [("·" if t == BACKGROUND_CLASS else ALPHABET[t], float(probs[i][t])) for t in top],
                                   "crop": to_28(c)})
                    if ids[i] == BACKGROUND_CLASS:
                        continue
                    s += ALPHABET[ids[i]]
                    confs.append(float(best[i]))
                if not s:
                    texts = []
                    break
                texts.append(s)
            if not texts:
                variants.append(v)
                continue
            conf = float(np.mean(confs)) if confs else 0.0
            raw = "".join(texts)
            line_lengths = [len(texts[0]), len(texts[1])] if len(texts) == 2 else None
            pretty, valid = normalize_vn_plate(raw, line_lengths)
            v.update(ok=True, lines=texts, raw=raw, pretty=pretty, valid=bool(valid), conf=conf, chars=detail)
            variants.append(v)
            if not valid:
                continue
            key = re.sub(r"[^A-Z0-9]", "", pretty)
            e = votes.setdefault(key, {"pretty": pretty, "score": 0.0, "conf": 0.0})
            try:
                known = int(pretty[:2]) in KNOWN_PROVINCES
            except ValueError:
                known = False
            w = (1.0 + conf) * (1.0 if known else 0.4)
            if len(key) >= 8 and re.search(r"\d{5}$", key):
                w += 0.2
            e["score"] += w
            e["conf"] = max(e["conf"], conf)
        best = max(votes.values(), key=lambda e: e["score"]) if votes else None
        return {"plate": best["pretty"] if best else None, "score": best["score"] if best else 0.0,
                "conf": best["conf"] if best else 0.0, "variants": variants, "gray": gray}


def label_from_filename(name: str) -> str:
    """'80A-00538.jpg' -> '80A00538'; bỏ hậu tố như '_1', ' (2)'."""
    base = re.sub(r"\.[A-Za-z0-9]+$", "", name.replace("\\", "/").split("/")[-1])
    base = re.split(r"[_ (]", base, maxsplit=1)[0]
    return re.sub(r"[^A-Z0-9]", "", base.upper())


def normalize_key(s: str) -> str:
    return re.sub(r"[^A-Z0-9]", "", (s or "").upper())
