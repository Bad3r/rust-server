#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.14"
# dependencies = ["pillow==13.0.0"]
# ///
"""Render the farm's readable storage and market labels, entrance guide, and market sign, and their C# image table."""

import argparse
import base64
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

SIGNS = {
    "farm-feed": ["FEED", "ANIMAL SUPPLIES"],
    "farm-clones": ["CLONES", "PLANT STOCK"],
    "farm-compost": ["MANURE", "FOR COMPOST"],
    "farm-wool": ["WOOL", "SHEARING STORE"],
    "farm-cold": ["COLD STORE", "POWERED FRIDGES"],
    "farm-milk": ["MILK", "DAIRY STORE"],
    "farm-eggs": ["EGGS", "COOP PRODUCE"],
    "farm-meat": ["MEAT", "PRODUCE STORE"],
    "farm-guide": [
        "PANDORA FARM", "Type /farm for live status", "Barn: labeled storage, shared by all",
        "Market by the entrance: free to take", "Pen: west   |   Garden: north", "House & coops: south",
    ],
    "farm-market-dairy": ["MILK & EGGS", "MARKET: FREE"],
    "farm-market-wool": ["WOOL & CLOTH", "MARKET: FREE"],
    "farm-market-crops": ["CROPS", "MARKET: FREE"],
    "farm-market-meat": ["MEAT", "MARKET: FREE"],
    "farm-market": ["FARM MARKET", "Free produce: take what you need", "Stocked from the farm's surplus"],
}
# Signs on 512 x 256 boards (sign.medium.wood, sign.large.wood): (title size, line size, gap after the title)
LARGE = {"farm-guide": (30, 19, 42), "farm-market": (46, 24, 66)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="Directory for PNGs and the C# table")
    parser.add_argument("--font", type=Path, help="TrueType bold font; defaults to DejaVu Sans via fontconfig")
    args = parser.parse_args()
    font = args.font or Path(subprocess.check_output(
        ["fc-match", "-f", "%{file}", "DejaVu Sans:style=Bold"], text=True))
    args.out.mkdir(parents=True, exist_ok=True)
    table = ["        private static readonly Dictionary<string, string> ExperienceSignImages =",
             "            new Dictionary<string, string>", "            {"]
    for name, lines in SIGNS.items():
        width, height = (512, 256) if name in LARGE else (256, 128)
        title, line, gap = LARGE.get(name, (32, 16, 34))
        image = Image.new("RGB", (width, height), (218, 194, 151))
        draw = ImageDraw.Draw(image)
        draw.rectangle((5, 5, width - 6, height - 6), outline=(71, 53, 31), width=3)
        top = {"farm-guide": 15, "farm-market": 63}.get(name, 22)
        for i, text in enumerate(lines):
            size = title if i == 0 else line
            face = ImageFont.truetype(str(font), size)
            while draw.textlength(text, font=face) > width - 26:
                size -= 1
                face = ImageFont.truetype(str(font), size)
            draw.text((width // 2, top), text, fill=(47, 36, 24), font=face, anchor="mt")
            top += gap if i == 0 else 34 if name not in LARGE or name == "farm-guide" else 40
        target = args.out / f"{name}.png"
        image.save(target, optimize=True)
        data = target.read_bytes()
        # Rust's IsValidPNG accepts RGB/RGBA; indexed PNGs are rejected.
        if data[24:26] != bytes((8, 2)):
            raise ValueError(f"{target}: expected 8-bit RGB PNG")
        encoded = base64.b64encode(data).decode("ascii")
        table.append(f'                ["{name}"] =')
        chunks = [encoded[i:i + 96] for i in range(0, len(encoded), 96)]
        table.extend('                    "' + chunk + '"' + (" +" if i < len(chunks) - 1 else ",")
                     for i, chunk in enumerate(chunks))
        print(f"{target.name}: {width}x{height}, {len(data)} bytes")
    table.append("            };")
    (args.out / "ExperienceSignImages.cs.txt").write_text("\n".join(table) + "\n", encoding="ascii")


if __name__ == "__main__":
    main()
