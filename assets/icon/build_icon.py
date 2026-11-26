"""Builds hopkey.ico (and preview PNGs) from the geometry in hopkey.svg / hopkey-small.svg.

Pillow only (no SVG rasterizer needed): each design is drawn at 1024px and downscaled.
Run:  python assets/icon/build_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
OUT_ICO = HERE.parent.parent / "src" / "Hopkey.App" / "Assets" / "hopkey.ico"
R = 1024
K = R / 256  # svg unit -> pixels


def hexrgb(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


def rrect(d, x, y, w, h, rx, fill):
    d.rounded_rectangle([x * K, y * K, (x + w) * K, (y + h) * K], radius=rx * K, fill=fill)


def ellipse(d, cx, cy, rx, ry, fill):
    d.ellipse([(cx - rx) * K, (cy - ry) * K, (cx + rx) * K, (cy + ry) * K], fill=fill)


def stroke(d, pts, width, fill):
    """Polyline with round caps and joins."""
    px = [(x * K, y * K) for x, y in pts]
    d.line(px, fill=fill, width=round(width * K), joint="curve")
    r = width * K / 2
    for x, y in (px[0], px[-1]):
        d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def quad(p0, p1, p2, n=48):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]


def mask_rounded(img, rx):
    m = Image.new("L", img.size, 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, R - 1, R - 1], radius=rx * K, fill=255)
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.paste(img, (0, 0), m)
    return out


def draw(small: bool) -> Image.Image:
    accent = hexrgb("#4F46E5")
    # background
    if small:
        bg = Image.new("RGBA", (R, R), accent)
    else:
        top, bot = hexrgb("#6366F1"), hexrgb("#4338CA")
        bg = Image.new("RGBA", (R, R))
        gd = ImageDraw.Draw(bg)
        for y in range(R):
            t = y / (R - 1)
            gd.line([(0, y), (R, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bot)))
    img = mask_rounded(bg, 56)

    # shadows
    sh = Image.new("RGBA", (R, R), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sh)
    if small:
        ellipse(sd, 128, 222, 70, 14, hexrgb("#312E81"))
    else:
        ellipse(sd, 128, 212, 56, 10, hexrgb("#312E81", 191))
        ellipse(sd, 128, 230, 32, 6, hexrgb("#312E81", 102))
    img = Image.alpha_composite(img, sh)

    # keycap layer, then rotate -9deg (svg) == +9deg counter-clockwise (PIL)
    kc = Image.new("RGBA", (R, R), (0, 0, 0, 0))
    kd = ImageDraw.Draw(kc)
    if small:
        rrect(kd, 40, 34, 176, 160, 36, hexrgb("#A5B4FC"))
        rrect(kd, 54, 38, 148, 128, 30, hexrgb("#FFFFFF"))
        stroke(kd, quad((86, 128), (128, 66), (170, 128)), 24, accent)
        stroke(kd, [(148, 124), (170, 132), (178, 106)], 24, accent)
        center = (128, 112)
    else:
        rrect(kd, 58, 52, 140, 132, 30, hexrgb("#A5B4FC"))
        rrect(kd, 70, 56, 116, 104, 24, hexrgb("#FFFFFF"))
        stroke(kd, quad((94, 132), (128, 84), (162, 132)), 14, accent)
        stroke(kd, [(146, 128), (162, 134), (168, 116)], 14, accent)
        center = (128, 120)
    kc = kc.rotate(9, resample=Image.BICUBIC, center=(center[0] * K, center[1] * K))
    return Image.alpha_composite(img, kc)


def main():
    big, small = draw(False), draw(True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [(small if s <= 24 else big).resize((s, s), Image.LANCZOS) for s in sizes]
    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT_ICO, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    big.resize((512, 512), Image.LANCZOS).save(HERE / "hopkey-512.png")
    print(f"wrote {OUT_ICO}")


if __name__ == "__main__":
    main()
