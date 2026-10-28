#!/usr/bin/env python3
"""Thống kê bộ đọc biển số nào sai ở đâu, từ log JSON-lines do ứng dụng ghi (data/anpr-log/reads-*.jsonl).

Cần file đáp án CSV, 2 cột:  tag,plate   (vd  img001.jpg,30A12345 ).
'tag' lấy từ TesseractPlateRecognitionService.LogTag.Value mà công cụ chạy thử đặt trước mỗi lần đọc.

Dùng:  python tools/eval_plate_reads.py --logs data/anpr-log --truth truth.csv
"""
import argparse, csv, glob, json, re, os
from collections import Counter, defaultdict
from difflib import SequenceMatcher

def key(s): return re.sub(r"[^A-Z0-9]", "", (s or "").upper().replace("Đ", "D"))

def diff(truth, read):
    """Danh sách lỗi: ('sub', đúng, đọc) | ('del', đúng) = rơi ký tự | ('ins', đọc) = thừa ký tự."""
    errs = []
    for op, i1, i2, j1, j2 in SequenceMatcher(None, truth, read, autojunk=False).get_opcodes():
        if op == "replace":
            for a, b in zip(truth[i1:i2], read[j1:j2]): errs.append(("sub", a, b))
            errs += [("del", a) for a in truth[i1 + (j2 - j1):i2]]
            errs += [("ins", b) for b in read[j1 + (i2 - i1):j2]]
        elif op == "delete": errs += [("del", a) for a in truth[i1:i2]]
        elif op == "insert": errs += [("ins", b) for b in read[j1:j2]]
    return errs

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--logs", default="data/anpr-log")
    ap.add_argument("--truth", required=True)
    a = ap.parse_args()

    truth = {}
    with open(a.truth, encoding="utf-8-sig", newline="") as f:
        for row in csv.reader(f):
            if len(row) >= 2 and row[0].strip().lower() != "tag": truth[row[0].strip()] = key(row[1])

    exact = Counter(); total = Counter(); subs = defaultdict(Counter); drops = defaultdict(Counter); extras = defaultdict(Counter)
    final_ok = final_n = 0
    wrong_final = []

    for path in sorted(glob.glob(os.path.join(a.logs, "reads-*.jsonl"))):
        for line in open(path, encoding="utf-8"):
            try: rec = json.loads(line)
            except ValueError: continue
            t = truth.get(rec.get("tag") or "")
            if not t: continue
            final_n += 1
            if key(rec.get("chosen")) == t: final_ok += 1
            else: wrong_final.append((rec["tag"], t, key(rec.get("chosen"))))
            for c in rec.get("candidates", []):
                for raw in c.get("raws", []):
                    src, _, text = raw.partition(":")
                    text = key(text.replace("(+)", ""))
                    total[src] += 1
                    if text == t: exact[src] += 1; continue
                    for e in diff(t, text):
                        if e[0] == "sub": subs[src][f"{e[1]}->{e[2]}"] += 1
                        elif e[0] == "del": drops[src][e[1]] += 1
                        else: extras[src][e[1]] += 1

    print(f"Kết quả CUỐI đúng: {final_ok}/{final_n}" + (f" ({100*final_ok/final_n:.0f}%)" if final_n else ""))
    for tag, t, got in wrong_final[:20]: print(f"   sai: {tag}: đúng {t} / ra {got or '(không đọc được)'}")
    for src in sorted(total):
        print(f"\n[{src}] đọc đúng {exact[src]}/{total[src]} lần đọc thô")
        print("   nhầm ký tự :", ", ".join(f"{k} x{v}" for k, v in subs[src].most_common(8)) or "-")
        print("   rơi ký tự  :", ", ".join(f"{k} x{v}" for k, v in drops[src].most_common(8)) or "-")
        print("   thừa ký tự :", ", ".join(f"{k} x{v}" for k, v in extras[src].most_common(8)) or "-")

if __name__ == "__main__":
    main()
