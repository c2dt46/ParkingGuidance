"""PlateTrainer — ứng dụng huấn luyện CNN ký tự biển số cho dự án ParkingGuidance. Chạy: python app.py → http://127.0.0.1:5055"""
from __future__ import annotations
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import cv2
import numpy as np
from flask import Flask, jsonify, render_template, request, send_file, abort

from anpr import synth
from anpr.dataset import Data, png_b64, IMG_EXT
from anpr.reader import Reader, check_model_contract, label_from_filename, normalize_key
from anpr.segment import ALPHABET, CLASS_NAMES

ROOT = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(ROOT, "config.json")      # {"data_dir": "D:\\PlateData"} — kho dữ liệu đặt ngoài thư mục ứng dụng


def _read_config() -> dict:
    try:
        return json.load(open(CONFIG_PATH, encoding="utf-8"))
    except Exception:
        return {}


CUSTOM_DIR = (_read_config().get("data_dir") or "").strip().strip('"')
DATA_DIR = os.path.abspath(CUSTOM_DIR) if CUSTOM_DIR else os.path.join(ROOT, "data")
DATA = Data(DATA_DIR, fonts=os.path.join(ROOT, "fonts"))
RUNS = os.path.join(DATA_DIR, "runs") if CUSTOM_DIR else os.path.join(ROOT, "runs")
RAW = os.path.join(DATA_DIR, "raw_done")             # ảnh biển đã cắt tay ở tab "Xử lý thô", chờ nhập sang Dữ liệu
os.makedirs(os.path.join(RAW, "_imported"), exist_ok=True)
SETTINGS_PATH = os.path.join(DATA_DIR, "settings.json")
os.makedirs(RUNS, exist_ok=True)
os.makedirs(DATA.fonts, exist_ok=True)

app = Flask(__name__)
app.config["MAX_CONTENT_LENGTH"] = 512 * 1024 * 1024
_proc: dict = {"p": None, "run": None}
_synth_state = {"running": False, "progress": 0.0, "msg": ""}
_readers: dict[str, tuple[float, Reader]] = {}


def settings() -> dict:
    try:
        return json.load(open(SETTINGS_PATH, encoding="utf-8"))
    except Exception:
        return {"project_path": ""}


def project_model_path() -> str | None:
    pp = settings().get("project_path", "").strip().strip('"')
    if not pp:
        return None
    cand = [os.path.join(pp, "src", "ParkingGuidance.Web", "Models", "char_cnn.onnx"),
            os.path.join(pp, "ParkingGuidance", "src", "ParkingGuidance.Web", "Models", "char_cnn.onnx"),
            os.path.join(pp, "Models", "char_cnn.onnx")]
    for c in cand:
        if os.path.isdir(os.path.dirname(c)):
            return c
    return None


def model_file(key: str) -> str | None:
    if key == "project":
        p = project_model_path()
        return p if p and os.path.exists(p) else None
    if key.startswith("run:"):
        p = os.path.join(RUNS, os.path.basename(key[4:]), "char_cnn.onnx")
        return p if os.path.exists(p) else None
    return None


def reader_for(key: str) -> Reader:
    p = model_file(key)
    if not p:
        raise ValueError("Không tìm thấy model: " + key)
    mt = os.path.getmtime(p)
    if key not in _readers or _readers[key][0] != mt:
        _readers[key] = (mt, Reader(p))
    return _readers[key][1]


# ------------------------------------------------------------------ trang & trạng thái
@app.get("/")
def index():
    return render_template("index.html", alphabet=ALPHABET)


@app.get("/api/state")
def state():
    runs = []
    for r in sorted(os.listdir(RUNS), reverse=True):
        rep = os.path.join(RUNS, r, "report.json")
        if os.path.exists(rep):
            j = json.load(open(rep, encoding="utf-8"))
            runs.append({"name": r, "val_acc": j.get("val_acc"), "val_acc_real": j.get("val_acc_real"), "best_epoch": j.get("best_epoch"),
                         "parent": j.get("parent"), "has_manifest": os.path.exists(os.path.join(RUNS, r, "manifest.json"))})
    latest = next((r["name"] for r in runs if r["has_manifest"]), None)       # runs đang xếp mới → cũ
    untrained = None
    if latest:
        try:
            m = json.load(open(os.path.join(RUNS, latest, "manifest.json"), encoding="utf-8"))
            seen = set(m.get("lineage") or m["files"])
            untrained = sum(1 for c in CLASS_NAMES for f in os.listdir(os.path.join(DATA.chars, c))
                            if not f.startswith("syn_") and f"{c}/{f}" not in seen)
        except Exception:
            untrained = None
    fonts = synth.find_fonts(DATA.fonts)
    user_names = sorted(f for f in os.listdir(DATA.fonts) if f.lower().endswith((".ttf", ".otf")))
    pm = project_model_path()
    return jsonify(stats=DATA.stats(), runs=runs, settings=settings(), project_model=pm, project_model_exists=bool(pm and os.path.exists(pm)),
                   fonts_user=len(user_names), fonts_user_names=user_names, fonts_total=len(set(fonts)),
                   training=bool(_proc["p"] and _proc["p"].poll() is None), current_run=_proc["run"],
                   data_dir=DATA_DIR, custom_data_dir=bool(CUSTOM_DIR), runs_dir=RUNS, latest_run=latest, untrained_real=untrained)


@app.post("/api/settings")
def set_settings():
    s = settings()
    s.update({k: v for k, v in (request.json or {}).items() if k in ("project_path",)})
    json.dump(s, open(SETTINGS_PATH, "w", encoding="utf-8"), ensure_ascii=False)
    return state()


# ------------------------------------------------------------------ lưu / nạp bộ dữ liệu thành thư mục
def _clean_path(p: str) -> str:
    return os.path.abspath(os.path.expanduser((p or "").strip().strip('"')))


@app.post("/api/data/export")
def data_export():
    j = request.json or {}
    if not (j.get("dest") or "").strip():
        return jsonify(error="Hãy nhập thư mục đích."), 400
    dest = _clean_path(j["dest"])
    try:
        n = DATA.export_to(dest, bool(j.get("include_syn")), RUNS, bool(j.get("include_runs", True)))
    except Exception as e:
        return jsonify(error=str(e)), 400
    return jsonify(ok=True, dest=dest, counts=n)


@app.post("/api/data/merge")
def data_merge():
    j = request.json or {}
    if not (j.get("src") or "").strip():
        return jsonify(error="Hãy nhập thư mục nguồn."), 400
    try:
        n = DATA.merge_from(_clean_path(j["src"]), RUNS)
    except Exception as e:
        return jsonify(error=str(e)), 400
    return jsonify(ok=True, counts=n, stats=DATA.stats())


@app.post("/api/config")
def set_config():
    j = request.json or {}
    cfg = _read_config()
    if j.get("reset"):
        cfg.pop("data_dir", None)
    else:
        d = _clean_path(j.get("data_dir", ""))
        try:
            os.makedirs(d, exist_ok=True)
            probe = os.path.join(d, ".write_test")
            open(probe, "w").close(); os.remove(probe)
        except Exception as e:
            return jsonify(error=f"Không ghi được vào thư mục này: {e}"), 400
        cfg["data_dir"] = d
    json.dump(cfg, open(CONFIG_PATH, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return jsonify(ok=True, data_dir=cfg.get("data_dir"), note="Đóng ứng dụng và chạy lại (run.bat) để dùng kho dữ liệu mới. Dữ liệu cũ không tự chuyển: hãy dùng 'Lưu ra thư mục' trước, rồi đặt thư mục đó làm kho.")


# ------------------------------------------------------------------ xử lý thô (cắt biển + nhập tên)
import re


def _raw_files() -> list[str]:
    return sorted(f for f in os.listdir(RAW) if f.lower().endswith(".png") and os.path.isfile(os.path.join(RAW, f)))


@app.post("/api/raw/save")
def raw_save():
    f = request.files.get("file")
    label = re.sub(r"[^A-Z0-9.\-]", "", (request.form.get("label") or "").upper())
    core = re.sub(r"[^A-Z0-9]", "", label)
    if f is None:
        return jsonify(error="Thiếu ảnh"), 400
    if not (7 <= len(core) <= 9):
        return jsonify(error=f"Biển số '{label}' có {len(core)} ký tự chữ/số (cần 7–9, vd 51D-932.40)."), 400
    img = cv2.imdecode(np.frombuffer(f.read(), np.uint8), cv2.IMREAD_COLOR)
    if img is None or img.shape[0] < 6 or img.shape[1] < 10:
        return jsonify(error="Vùng cắt quá nhỏ hoặc không đọc được"), 400
    name, k = label, 1
    while os.path.exists(os.path.join(RAW, name + ".png")) or os.path.exists(os.path.join(RAW, "_imported", name + ".png")):
        k += 1
        name = f"{label}_{k}"
    cv2.imwrite(os.path.join(RAW, name + ".png"), img)
    return jsonify(ok=True, name=name + ".png", count=len(_raw_files()))


@app.get("/api/raw/list")
def raw_list():
    return jsonify(files=_raw_files())


@app.get("/raw/<name>")
def raw_img(name):
    p = os.path.join(RAW, os.path.basename(name))
    if not os.path.isfile(p):
        abort(404)
    return send_file(p, mimetype="image/png")


@app.post("/api/raw/delete")
def raw_delete():
    p = os.path.join(RAW, os.path.basename((request.json or {}).get("name", "")))
    if os.path.isfile(p):
        os.remove(p)
    return jsonify(files=_raw_files())


@app.post("/api/raw/import")
def raw_import():
    """Chuyển các ảnh đã xử lý thô sang tab Dữ liệu (tách ký tự → hàng đợi duyệt / tập kiểm thử)."""
    j = request.json or {}
    holdout = float(j.get("holdout", 0)) / 100.0
    suggest = None
    if j.get("suggest"):
        try:
            suggest = reader_for("project")
        except Exception:
            suggest = None
    rng = np.random.default_rng(11)
    out = []
    for fn in _raw_files():
        path = os.path.join(RAW, fn)
        r = DATA.import_plate(fn, open(path, "rb").read(), bool(rng.random() < holdout), suggest)
        out.append(r)
        if not r["status"].startswith(("không", "tên file")):     # lỗi thì giữ lại để bạn sửa
            shutil.move(path, os.path.join(RAW, "_imported", fn))
    return jsonify(results=out, remaining=len(_raw_files()), stats=DATA.stats())


# ------------------------------------------------------------------ dữ liệu
@app.post("/api/import")
def import_plates():
    holdout = float(request.form.get("holdout", 0)) / 100.0
    files = [f for f in request.files.getlist("files") if f.filename.lower().endswith(IMG_EXT)]
    rng = np.random.default_rng(7)
    suggest = None
    try:
        if request.form.get("suggest") == "1":
            suggest = reader_for("project")
    except Exception:
        suggest = None
    out = []
    for f in files:
        out.append(DATA.import_plate(f.filename, f.read(), bool(rng.random() < holdout), suggest))
    return jsonify(results=out, stats=DATA.stats())


@app.get("/api/pending")
def pending():
    return jsonify(items=DATA.list_pending(), total=DATA.stats()["pending"])


@app.get("/pending/<pid>.png")
def pending_img(pid):
    p = DATA.pending_png(os.path.basename(pid))
    if not os.path.exists(p):
        abort(404)
    return send_file(p, mimetype="image/png")


@app.post("/api/pending/resolve")
def pending_resolve():
    j = request.json
    for it in j.get("items", []):
        DATA.resolve(it["id"], it.get("label"))
    return jsonify(stats=DATA.stats())


@app.post("/api/pending/accept_matched")
def pending_accept():
    return jsonify(accepted=DATA.accept_matched(), stats=DATA.stats())


@app.post("/api/pending/clear")
def pending_clear():
    DATA.clear_pending()
    return jsonify(stats=DATA.stats())


@app.post("/api/add_char")
def add_char():
    j = request.json
    import base64
    img = cv2.imdecode(np.frombuffer(base64.b64decode(j["png"].split(",")[1]), np.uint8), cv2.IMREAD_GRAYSCALE)
    DATA.save_char(img, j["label"], "man")
    return jsonify(stats=DATA.stats())


@app.post("/api/delete_synthetic")
def delete_syn():
    n = 0
    for c in CLASS_NAMES:
        d = os.path.join(DATA.chars, c)
        for f in os.listdir(d):
            if f.startswith("syn_"):
                os.remove(os.path.join(d, f)); n += 1
    return jsonify(deleted=n, stats=DATA.stats())


@app.post("/api/fonts")
def upload_font():
    for f in request.files.getlist("files"):
        if f.filename.lower().endswith((".ttf", ".otf")):
            f.save(os.path.join(DATA.fonts, os.path.basename(f.filename)))
    return state()


@app.post("/api/synth")
def synth_start():
    if _synth_state["running"]:
        return jsonify(error="Đang sinh dữ liệu"), 409
    j = request.json or {}

    def work():
        _synth_state.update(running=True, progress=0.0, msg="")
        try:
            r = synth.generate(DATA.chars, DATA.fonts, int(j.get("per_class", 400)), int(j.get("bg_count", 600)),
                               int(j.get("seed", int(time.time()) % 100000)), float(j.get("strength", 1.0)),
                               progress=lambda p: _synth_state.update(progress=p), user_only=bool(j.get("user_only")))
            _synth_state["msg"] = f"Đã tạo {r['created']} ảnh (dùng {r['fonts']} phông)."
        except Exception as e:
            _synth_state["msg"] = "Lỗi: " + str(e)
        _synth_state["running"] = False
    threading.Thread(target=work, daemon=True).start()
    return jsonify(ok=True)


@app.get("/api/synth/status")
def synth_status():
    return jsonify(**_synth_state, stats=DATA.stats())


@app.get("/api/synth/preview")
def synth_preview():
    fonts = synth.find_fonts(DATA.fonts, request.args.get("user_only") == "1")
    if not fonts:
        return jsonify(error="Chưa có phông chữ"), 400
    import random
    rng = random.Random(int(time.time()))
    rows = []
    for ch in ALPHABET:
        s = synth.render_char_sample(ch, fonts, rng, 1.0)
        rows.append({"ch": ch, "img": png_b64(s if s is not None else np.zeros((28, 28), np.uint8), 3)})
    rows.append({"ch": "nền", "img": png_b64(synth.render_bg_sample(fonts, rng), 3)})
    return jsonify(items=rows)


# ------------------------------------------------------------------ huấn luyện
@app.post("/api/train")
def train_start():
    if _proc["p"] and _proc["p"].poll() is None:
        return jsonify(error="Đang huấn luyện"), 409
    j = request.json or {}
    # khởi tạo: "latest" (lần gần nhất có best.pt), "none" (học từ đầu) hoặc "run:<tên>"
    init, init_path = j.get("init", "latest"), "none"
    have = [r for r in sorted(os.listdir(RUNS)) if os.path.exists(os.path.join(RUNS, r, "best.pt"))]
    if init == "latest" and have:
        init_path = os.path.join(RUNS, have[-1], "best.pt")
    elif init.startswith("run:") and os.path.exists(os.path.join(RUNS, os.path.basename(init[4:]), "best.pt")):
        init_path = os.path.join(RUNS, os.path.basename(init[4:]), "best.pt")
    # dữ liệu dùng để train: all | new (bỏ dữ liệu model tham chiếu đã học) | only (đúng dữ liệu lần tham chiếu)
    mode, ref, data_args = j.get("data_mode", "all"), j.get("data_ref", "latest"), []
    if mode in ("new", "only"):
        ok_runs = [r for r in sorted(os.listdir(RUNS)) if os.path.exists(os.path.join(RUNS, r, "report.json"))
                   and os.path.exists(os.path.join(RUNS, r, "manifest.json"))]
        refname = (ok_runs[-1] if ok_runs else None) if ref == "latest" else os.path.basename(ref.replace("run:", ""))
        mp = os.path.join(RUNS, refname, "manifest.json") if refname else ""
        if not refname or not os.path.exists(mp):
            return jsonify(error="Chưa có lần train nào có 'sổ ghi dữ liệu' để làm mốc (lần train từ phiên bản cũ không có sổ). "
                                 "Hãy chọn 'Toàn bộ dữ liệu' cho lần này."), 400
        data_args = ["--exclude-from" if mode == "new" else "--only-from", mp]
    name = time.strftime("%Y%m%d-%H%M%S")
    out = os.path.join(RUNS, name)
    os.makedirs(out, exist_ok=True)
    cmd = [sys.executable, "-m", "anpr.train", "--data", DATA.chars, "--out", out, "--epochs", str(int(j.get("epochs", 40))),
           "--batch", str(int(j.get("batch", 128))), "--lr", str(float(j.get("lr", 0.002))), "--aug", str(float(j.get("aug", 1.0))),
           "--real-weight", str(int(j.get("real_weight", 4))), "--patience", str(int(j.get("patience", 10))),
           "--init", init_path, "--init-lr-scale", str(float(j.get("init_lr_scale", 0.3)))] + data_args
    logf = open(os.path.join(out, "stdout.txt"), "w", encoding="utf-8")
    _proc["p"] = subprocess.Popen(cmd, cwd=ROOT, stdout=logf, stderr=subprocess.STDOUT)
    _proc["run"] = name
    return jsonify(run=name)


@app.post("/api/train/stop")
def train_stop():
    p = _proc["p"]
    if p and p.poll() is None:
        p.terminate()
    return jsonify(ok=True)


@app.get("/api/train/status")
def train_status():
    name = request.args.get("run") or _proc["run"]
    events, tail = [], ""
    if name:
        lp = os.path.join(RUNS, os.path.basename(name), "log.jsonl")
        if os.path.exists(lp):
            for line in open(lp, encoding="utf-8"):
                try: events.append(json.loads(line))
                except Exception: pass
        sp = os.path.join(RUNS, os.path.basename(name), "stdout.txt")
        if os.path.exists(sp):
            tail = "".join(open(sp, encoding="utf-8", errors="replace").readlines()[-6:]) if not events or events[-1].get("type") != "done" else ""
    running = bool(_proc["p"] and _proc["p"].poll() is None)
    report = None
    rp = os.path.join(RUNS, os.path.basename(name or "x"), "report.json")
    if os.path.exists(rp):
        report = json.load(open(rp, encoding="utf-8"))
    crashed = (not running) and name == _proc["run"] and not any(e.get("type") in ("done", "error") for e in events) and _proc["p"] is not None
    return jsonify(run=name, running=running, events=events, report=report, crashed=crashed, tail=tail if (crashed or not events) else "")


# ------------------------------------------------------------------ kiểm thử
def annotate(res: dict) -> tuple[np.ndarray, dict | None]:
    """Vẽ khung + nhãn lên ảnh xám đã phóng của biến thể tốt nhất."""
    gray = res["gray"]
    best_key = normalize_key(res["plate"]) if res["plate"] else None
    chosen = None
    for v in res["variants"]:
        if v.get("ok") and (best_key is None or normalize_key(v.get("pretty", "")) == best_key):
            chosen = v; break
    if chosen is None:
        chosen = next((v for v in res["variants"] if v.get("ok")), None)
    vis = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
    if chosen:
        for ch in chosen["chars"]:
            x, y, w, h = ch["box"]
            col = (0, 160, 0) if ch["p"] >= 0.9 else ((0, 140, 255) if ch["p"] >= 0.6 else (0, 0, 220))
            cv2.rectangle(vis, (x, y), (x + w, y + h), col, 2)
            cv2.putText(vis, ch["ch"], (x, max(14, y - 4)), cv2.FONT_HERSHEY_SIMPLEX, 0.7, col, 2)
    return vis, chosen


@app.post("/api/test")
def test_image():
    f = request.files["file"]
    img = cv2.imdecode(np.frombuffer(f.read(), np.uint8), cv2.IMREAD_COLOR)
    if img is None:
        return jsonify(error="Không đọc được ảnh"), 400
    try:
        rd = reader_for(request.form.get("model", "project"))
    except Exception as e:
        return jsonify(error=str(e)), 400
    res = rd.read(img)
    vis, chosen = annotate(res)
    chars = []
    if chosen:
        for ch in chosen["chars"]:
            chars.append({"ch": ch["ch"], "p": round(ch["p"], 3), "top3": [[a, round(b, 3)] for a, b in ch["top3"]], "crop": png_b64(ch["crop"], 3), "png": png_b64(ch["crop"])})
    expected = label_from_filename(f.filename)
    return jsonify(plate=res["plate"], conf=round(res["conf"], 3), expected=expected, image=png_b64(vis), chars=chars,
                   correct=(normalize_key(res["plate"]) == expected) if res["plate"] and expected else None,
                   variants=[{k: v.get(k) for k in ("adaptive", "invert", "ok", "pretty", "valid", "lines")} for v in res["variants"]])


@app.post("/api/eval")
def evaluate():
    j = request.json
    folder = {"test": DATA.plates_test, "train": DATA.plates}.get(j.get("set", "test"), DATA.plates_test)
    keys = j.get("models") or ["project"]
    files = [f for f in sorted(os.listdir(folder)) if f.lower().endswith(IMG_EXT)]
    if not files:
        return jsonify(error="Thư mục ảnh biển trống — hãy nhập ảnh biển (tên file = biển đúng) trước."), 400
    out = {}
    for k in keys:
        try:
            rd = reader_for(k)
        except Exception as e:
            out[k] = {"error": str(e)}; continue
        ok, rows = 0, []
        for fn in files:
            img = cv2.imread(os.path.join(folder, fn))
            exp = label_from_filename(fn)
            r = rd.read(img)
            got = normalize_key(r["plate"]) if r["plate"] else ""
            good = got == exp
            ok += good
            rows.append({"file": fn, "expected": exp, "got": r["plate"] or "—", "ok": good})
        out[k] = {"acc": ok / len(files), "n": len(files), "rows": rows}
    return jsonify(results=out, n=len(files))


# ------------------------------------------------------------------ cài vào dự án
@app.post("/api/install")
def install():
    key = (request.json or {}).get("model", "")
    src = model_file(key)
    dst = project_model_path()
    if not src or key == "project":
        return jsonify(error="Chọn một bản huấn luyện (run) để cài."), 400
    if not dst:
        return jsonify(error="Chưa đặt đường dẫn dự án ParkingGuidance (mục Cài đặt)."), 400
    c = check_model_contract(src)
    if not c["ok"]:
        return jsonify(error="Model không đúng hợp đồng đầu vào/ra: " + json.dumps(c)), 400
    backup = None
    if os.path.exists(dst):
        backup = dst + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
        shutil.copy2(dst, backup)
    shutil.copy2(src, dst)
    return jsonify(ok=True, dst=dst, backup=backup, contract=c,
                   note="Khởi động lại ứng dụng ParkingGuidance để nạp model mới (nếu chạy từ bin/, build lại để chép Models).")


if __name__ == "__main__":
    port = int(os.environ.get("PORT", 5055))
    print(f"PlateTrainer: http://127.0.0.1:{port}")
    app.run(host="127.0.0.1", port=port, debug=False, threaded=True)
