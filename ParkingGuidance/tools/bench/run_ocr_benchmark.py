import cv2, re, sys, time, importlib, collections
from synth_plate_dataset import make_sample

def run(recognize, n=150, start=1000, verbose=False):
    stats = collections.defaultdict(lambda: [0, 0])
    fails = []
    t0 = time.time()
    for i in range(start, start + n):
        img, plate, meta = make_sample(i)
        res = recognize(img)
        got = None
        if res:
            got = re.sub(r'[^A-Z0-9]', '', res[0].upper()) if res else None
        ok = (got == plate)
        for key in ("all", "two=%s" % meta["two"], "style=%s" % meta["style"],
                    "small" if meta["wf"] < 0.2 else "large"):
            stats[key][0] += ok; stats[key][1] += 1
        if not ok: fails.append((i, plate, got, meta))
    dt = time.time() - t0
    print(f"--- {n} mau, {dt:.1f}s ({dt/n*1000:.0f} ms/anh) ---")
    for k in sorted(stats): print(f"  {k:14s} {stats[k][0]:3d}/{stats[k][1]:3d} = {100*stats[k][0]/stats[k][1]:5.1f}%")
    if verbose:
        for f in fails[:25]: print("   FAIL", f)
    return fails

if __name__ == "__main__":
    import os
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
    import anpr_check
    def rec(img):
        return anpr_check.recognize(img)
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 80
    start = int(sys.argv[2]) if len(sys.argv) > 2 else 5000  # >=5000: anh MOI, khac voi anh dung de tinh chinh thuat toan
    run(rec, n=n, start=start, verbose=True)
