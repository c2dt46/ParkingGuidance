"""Chuan hoa bien so VN theo kieu MOI: dung SO KY TU TUNG DONG (neu la bien 2 dong) de xac dinh
chinh xac cau truc seri (1 chu, 2 chu, hoac chu+so nhu "C1","Y9") thay vi doan theo tong do dai.
"""
import re

TO_DIGIT = {'O':'0','Q':'0','D':'0','I':'1','L':'1','Z':'2','S':'5','B':'8','G':'6','T':'7'}
TO_LETTER = {'0':'D','1':'L','5':'S','8':'B','2':'Z','6':'G','7':'T','4':'A'}
ALLOW_LEGACY_4_DIGIT = False

def _force(ch, mapping, target_set):
    """Neu ch da dung kieu (vd da la chu so khi can chu so) -> giu nguyen, 0 sua.
    Neu khong, thu doi qua mapping; khong doi duoc -> that bai (None)."""
    if ch in target_set:
        return ch, 0
    if ch in mapping:
        return mapping[ch], 1
    return None, None

DIGITS = set("0123456789")
LETTERS = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ")

def _build(chars, digit_positions, letter_positions, flexible_positions):
    """Ap dung ep kieu cho tung vi tri, tra ve (chuoi_da_sua, so_lan_sua) hoac None neu that bai."""
    out = list(chars)
    n_fix = 0
    for i in digit_positions:
        c, f = _force(out[i], TO_DIGIT, DIGITS)
        if c is None: return None
        out[i] = c; n_fix += f
    for i in letter_positions:
        c, f = _force(out[i], TO_LETTER, LETTERS)
        if c is None: return None
        out[i] = c; n_fix += f
    for i in flexible_positions:
        # Vi tri linh hoat (chu THU 2 cua seri): KHONG ep kieu - giu nguyen OCR doc duoc (co the
        # la chu hoac so, ca 2 deu hop le tren bien that, vd seri "AB" hoac seri "C1").
        if out[i] not in DIGITS and out[i] not in LETTERS:
            return None  # ky tu la rac (khong phai chu/so) -> loai
    return ''.join(out), n_fix


def normalize_vn_plate(raw, line_lengths=None):
    """raw: chuoi da ghep tu OCR (chi A-Z0-9). line_lengths: [do_dai_dong1, do_dai_dong2] neu biet
    chinh xac so ky tu tung dong (bien 2 dong) - dung de xac dinh DUNG cau truc, khong doan mo.
    Tra ve (bien_so_dep, hop_le)."""
    s = re.sub(r'[^A-Z0-9]', '', raw.upper())
    chars = list(s)
    candidates = []  # (pretty, n_fix)

    if line_lengths and len(line_lengths) == 2 and sum(line_lengths) == len(s):
        l1, l2 = line_lengths
        series_len = l1 - 2   # dong 1 = tinh (2) + seri (series_len)
        if series_len in (1, 2) and l2 in (4, 5) and len(s) >= 2:
            digit_pos = list(range(2)) + list(range(2 + series_len, len(s)))
            letter_pos = [2]
            flex_pos = [3] if series_len == 2 else []
            built = _build(chars, digit_pos, letter_pos, flex_pos)
            if built:
                fixed, n_fix = built
                head = fixed[:2 + series_len]
                tail = fixed[2 + series_len:]
                pretty = f"{head}-{tail[:3]}.{tail[3:]}" if l2 == 5 else f"{head}-{tail}"
                candidates.append((pretty, n_fix))
    else:
        # Khong biet so dong (bien 1 dong) -> thu ca 2 cau truc kha di cho moi do dai, chon it sua nhat
        n = len(s)
        shapes = []
        if n == 7 and ALLOW_LEGACY_4_DIGIT:
            shapes.append((1, 4, []))
        if n == 8:
            shapes.append((1, 5, []))              # NN L NNNNN
            shapes.append((2, 4, [3]))              # NN L? NNNN (vi tri 3 linh hoat)
        if n == 9:
            shapes.append((2, 5, [3]))              # NN L? NNNNN (vi tri 3 linh hoat)

        for series_len, serial_len, flex in shapes:
            digit_pos = list(range(2)) + list(range(2 + series_len, n))
            letter_pos = [2]
            built = _build(chars, digit_pos, letter_pos, flex)
            if built:
                fixed, n_fix = built
                head = fixed[:2 + series_len]
                tail = fixed[2 + series_len:]
                pretty = f"{head}-{tail[:3]}.{tail[3:]}" if serial_len == 5 else f"{head}-{tail}"
                candidates.append((pretty, n_fix))

    if not candidates:
        return raw, False

    candidates.sort(key=lambda c: c[1])  # it sua nhat truoc
    return candidates[0][0], True
