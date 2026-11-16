#!/usr/bin/env python3
"""Generates the SabhaMujic app icon: a white music note (two beamed eighth notes) in an accent-coloured circle.

Writes src/SabhaMujic.Desktop/Assets/SabhaMujic.ico with 16, 32, 48 and 256 px images. Each size is drawn separately
at 8x and downsampled, so small sizes stay crisp. Requires Pillow (pip install pillow).

Usage (from the repository root):  python3 tools/icon/generate_icon.py [output.ico]
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = (16, 32, 48, 256)
SUPERSAMPLE = 8
ACCENT = (194, 65, 12, 255)  # #C2410C, the desktop theme's AccentColor
ACCENT_EDGE = (154, 52, 18, 255)  # #9A3412, a slightly darker rim
NOTE = (255, 255, 255, 255)


def render(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Circle with a thin darker rim (the rim is dropped at 16 px, where it would only blur the edge).
    margin = s * 0.02
    draw.ellipse((margin, margin, s - margin, s - margin), fill=ACCENT_EDGE)
    rim = 0 if size <= 16 else s * 0.035
    draw.ellipse((margin + rim, margin + rim, s - margin - rim, s - margin - rim), fill=ACCENT)

    # Two beamed eighth notes, in units of the canvas so every size has the same proportions.
    def u(v: float) -> float:
        return v * s

    stem_w = u(0.075 if size <= 16 else 0.06)
    head_rx, head_ry = u(0.115), u(0.085)
    left_x, right_x = u(0.45), u(0.74)  # right edges of the stems
    left_top, right_top = u(0.29), u(0.24)
    left_base, right_base = u(0.70), u(0.65)  # stem bottoms (centre of the note heads)
    beam_h = u(0.12 if size <= 16 else 0.10)

    # Stems.
    draw.rectangle((left_x - stem_w, left_top, left_x, left_base), fill=NOTE)
    draw.rectangle((right_x - stem_w, right_top, right_x, right_base), fill=NOTE)
    # Beam: a parallelogram from the top of the left stem to the top of the right stem.
    draw.polygon(
        [(left_x - stem_w, left_top), (right_x, right_top), (right_x, right_top + beam_h), (left_x - stem_w, left_top + beam_h)],
        fill=NOTE,
    )

    # Note heads: tilted ellipses, drawn on a separate layer and rotated, ending at the stems' right edge.
    for stem_x, base in ((left_x, left_base), (right_x, right_base)):
        layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        cx, cy = stem_x - head_rx * 0.9, base
        ImageDraw.Draw(layer).ellipse((cx - head_rx, cy - head_ry, cx + head_rx, cy + head_ry), fill=NOTE)
        layer = layer.rotate(25, resample=Image.BICUBIC, center=(cx, cy))
        image.alpha_composite(layer)

    return image.resize((size, size), Image.LANCZOS)


def main() -> int:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("src/SabhaMujic.Desktop/Assets/SabhaMujic.ico")
    out.parent.mkdir(parents=True, exist_ok=True)
    images = [render(size) for size in SIZES]
    largest = images[-1]
    largest.save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])

    with Image.open(out) as check:
        found = sorted(check.info.get("sizes", set()))
    expected = sorted((s, s) for s in SIZES)
    if found != expected:
        print(f"unexpected icon sizes {found}, expected {expected}", file=sys.stderr)
        return 1

    print(f"wrote {out} ({', '.join(str(s) for s in SIZES)} px)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
