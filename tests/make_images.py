"""Generate test images (and the exact pixels each one should become) into tests/data.

Needs Pillow and numpy. Usage: python -I make_images.py <out_dir> <width> <height>
Every expected_*.png is plain 8-bit RGB written by Pillow, so the app's output is checked
against a second, independent decoder and encoder.
"""
import struct
import sys
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
W, H = int(sys.argv[2]), int(sys.argv[3])
rng = np.random.default_rng(20261118)


def save_rgb(arr, name):
    Image.fromarray(arr.astype(np.uint8), "RGB").save(out / name)


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def write_png_raw(path, arr, bitdepth, colortype, extra_chunks=()):
    """Minimal PNG writer for cases Pillow cannot write (16-bit RGB, custom chunks)."""
    h, w = arr.shape[:2]
    if bitdepth == 16:
        rows = arr.astype(">u2").reshape(h, -1).view(np.uint8)
    else:
        rows = arr.astype(np.uint8).reshape(h, -1)
    raw = b"".join(b"\x00" + rows[y].tobytes() for y in range(h))
    data = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, bitdepth, colortype, 0, 0, 0))
    for c in extra_chunks:
        data += c
    data += png_chunk(b"IDAT", zlib.compress(raw, 6)) + png_chunk(b"IEND", b"")
    Path(path).write_bytes(data)


noise = rng.integers(0, 256, (H, W, 3), dtype=np.uint8)
save_rgb(noise, "noise.png"); save_rgb(noise, "expected_noise.png")

yy, xx = np.mgrid[0:H, 0:W]
checker = np.where(((xx + yy) % 2 == 0)[..., None], 255, 0).repeat(3, axis=2).astype(np.uint8)
save_rgb(checker, "checker.png"); save_rgb(checker, "expected_checker.png")

# 16-bit: v16 = v8 * 257 converts back to exactly v8.
write_png_raw(out / "noise16.png", noise.astype(np.uint16) * 257, 16, 2)
save_rgb(noise, "expected_noise16.png")

# RGBA fully opaque -> unchanged.
opaque = np.dstack([noise, np.full((H, W), 255, np.uint8)])
Image.fromarray(opaque, "RGBA").save(out / "rgba_opaque.png"); save_rgb(noise, "expected_rgba_opaque.png")

# RGBA partial alpha -> flattened on black with the app's rounding: (c*a + 127) // 255.
alpha = rng.integers(0, 256, (H, W), dtype=np.uint8)
Image.fromarray(np.dstack([noise, alpha]), "RGBA").save(out / "rgba_partial.png")
flat = (noise.astype(np.int32) * alpha[..., None].astype(np.int32) + 127) // 255
save_rgb(flat, "expected_rgba_partial.png")

# gAMA chunk (gamma 1.0): stored values must be kept as they are.
write_png_raw(out / "gamma.png", noise, 8, 2, [png_chunk(b"gAMA", struct.pack(">I", 100000))])
save_rgb(noise, "expected_gamma.png")

# Embedded ICC profile (Windows' own sRGB profile): ignored, values kept.
icc_path = Path(r"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm")
iccp = b"sRGB\x00\x00" + zlib.compress(icc_path.read_bytes())
write_png_raw(out / "icc.png", noise, 8, 2, [png_chunk(b"iCCP", iccp)])
save_rgb(noise, "expected_icc.png")

# 8-bit grey and palette images.
grey = noise[..., 0]
Image.fromarray(grey, "L").save(out / "grey.png"); save_rgb(np.dstack([grey] * 3), "expected_grey.png")
pal = Image.fromarray(noise, "RGB").quantize(colors=200, method=Image.Quantize.MEDIANCUT)
pal.save(out / "palette.png"); save_rgb(np.asarray(pal.convert("RGB")), "expected_palette.png")

# Off-size images for refusal / crop / fit.
Image.fromarray(rng.integers(0, 256, (2000, 2000, 3), dtype=np.uint8), "RGB").save(out / "square.png")
Image.fromarray(np.full((2000, 2000, 3), 200, np.uint8), "RGB").save(out / "square_grey200.png")

# JPEG input (information only: two JPEG decoders may differ slightly).
Image.fromarray(noise, "RGB").save(out / "photo.jpg", quality=92)

# Camera orientation tags 2-8. 4:4:4 quality-100 JPEGs decode identically in Windows and Pillow,
# so the expected image is Pillow's own upright version (ImageOps.exif_transpose).
from PIL import ImageOps
small = rng.integers(0, 256, (40, 64, 3), dtype=np.uint8)
for o in range(2, 9):
    im = Image.fromarray(small, "RGB")
    exif = Image.Exif(); exif[0x0112] = o
    im.save(out / f"orient{o}.jpg", quality=100, subsampling=0, exif=exif.tobytes())
    upright = ImageOps.exif_transpose(Image.open(out / f"orient{o}.jpg")).convert("RGB")
    upright.save(out / f"expected_orient{o}.png")

# CMYK JPEG: converting to RGB changes values, so it must not be labelled lossless.
Image.fromarray(small, "RGB").convert("CMYK").save(out / "cmyk.jpg", quality=95)

# Not an image, and an animated GIF.
(out / "not_an_image.png").write_text("hello, this is text")
frames = [Image.fromarray(np.full((64, 64, 3), v, np.uint8), "RGB") for v in (0, 128, 255)]
frames[0].save(out / "animated.gif", save_all=True, append_images=frames[1:], duration=100, loop=0)
print("ok")
