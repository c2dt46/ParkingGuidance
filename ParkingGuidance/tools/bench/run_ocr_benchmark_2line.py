"""Do do chinh xac ANPR rieng cho bien 2 dong THUC TE (co khung/tem/oc vit) - benchmark dung de
phat hien va xac nhan sua loi "bien 2 dong khong nhan dien duoc" (xem README muc 11).

Chay (tu thu muc goc du an): python tools/bench/run_ocr_benchmark_2line.py 30
"""
import os, re, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))
import anpr_check
from synth_plate_dataset_2line_realistic import make_sample_2line_hard

n = int(sys.argv[1]) if len(sys.argv) > 1 else 30
ok = 0
fails = []
for i in range(1000, 1000 + n):
    img, plate, meta = make_sample_2line_hard(i)
    r = anpr_check.recognize(img)
    got = re.sub(r'[^A-Z0-9]', '', r[0].upper()) if r else None
    if got == plate:
        ok += 1
    else:
        fails.append((i, plate, got))

print(f"Bien 2 dong (co khung/tem/oc vit): {ok}/{n} = {100*ok/n:.1f}%")
for f in fails[:15]:
    print("  FAIL", f)
