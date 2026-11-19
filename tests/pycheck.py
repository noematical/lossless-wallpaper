"""Independent check with Pillow: python -I pycheck.py <command> ...

  diff <a> <b>             differing pixels and largest channel difference (8-bit RGB)
  bars <png> <x0> <x1>     verify columns [0,x0) and [x1,width) are pure black
  chunks <png>             list the PNG chunk types
"""
import struct
import sys

import numpy as np
from PIL import Image


def rgb(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int16)


cmd = sys.argv[1]
if cmd == "diff":
    a, b = rgb(sys.argv[2]), rgb(sys.argv[3])
    if a.shape != b.shape:
        print(f"size mismatch {a.shape} vs {b.shape}"); sys.exit(3)
    d = np.abs(a - b).max(axis=2)
    n, m = int((d > 0).sum()), int(d.max())
    print(f"{n} differing pixels, largest difference {m}")
    sys.exit(0 if n == 0 else 3)
elif cmd == "bars":
    a = rgb(sys.argv[2]); x0, x1 = int(sys.argv[3]), int(sys.argv[4])
    left, right = a[:, :x0], a[:, x1:]
    ok = (left == 0).all() and (right == 0).all()
    inner = a[:, x0:x1]
    print(f"{a.shape[1]}x{a.shape[0]}; bars black: {bool(ok)}; content mean {inner.mean():.1f}")
    sys.exit(0 if ok else 3)
elif cmd == "chunks":
    data = open(sys.argv[2], "rb").read()
    i, kinds = 8, []
    while i < len(data):
        n = struct.unpack(">I", data[i:i + 4])[0]
        k = data[i + 4:i + 8].decode("latin1")
        if not kinds or kinds[-1] != k:
            kinds.append(k)
        i += 12 + n
    print(" ".join(kinds))
