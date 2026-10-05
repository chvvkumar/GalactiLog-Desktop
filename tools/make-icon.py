#!/usr/bin/env python3
"""Regenerate the app icon.

Reads the web logo, pads it to a transparent 512x512 square, and writes a
multi-size .ico for the native app. Run with no args from anywhere; the
default source is the sibling GalactiLog frontend clone.

Usage: python tools/make-icon.py [source.png]
"""
import sys
from pathlib import Path
from PIL import Image

repo_root = Path(__file__).resolve().parent.parent
default_source = repo_root.parent / "GalactiLog" / "frontend" / "public" / "logo-transparent.png"
source = Path(sys.argv[1]) if len(sys.argv) > 1 else default_source
dest = repo_root / "src" / "GalactiLog.App" / "Assets" / "GalactiLog.ico"

logo = Image.open(source).convert("RGBA")
canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
offset = ((512 - logo.width) // 2, (512 - logo.height) // 2)
canvas.paste(logo, offset, logo)

dest.parent.mkdir(parents=True, exist_ok=True)
canvas.save(dest, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
print(f"wrote {dest}")
