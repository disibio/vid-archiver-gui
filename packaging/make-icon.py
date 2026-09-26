"""Draws the app icon and writes it in every format the builds need.

Usage: python packaging/make-icon.py   (needs Pillow)
  src/VidArchiverGui.App/Assets/icon.ico    Windows exe + window icon
  src/VidArchiverGui.App/Assets/icon.png    Linux menu entry (512 px)
  packaging/macos/AppIcon.icns              macOS app bundle
  packaging/msix/Assets/*.png               Microsoft Store (MSIX) tiles and logos
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SIZE = 1024
SCALE = 4  # draw large, then downsample for smooth edges


def draw() -> Image.Image:
    s = SIZE * SCALE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Background: rounded square with a vertical yellow-to-orange gradient.
    top, bottom = (255, 200, 60), (242, 120, 24)
    gradient = Image.new("RGBA", (1, s))
    for y in range(s):
        t = y / (s - 1)
        gradient.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    gradient = gradient.resize((s, s))
    mask = Image.new("L", (s, s), 0)
    margin = s * 0.06
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, s - margin, s - margin), radius=s * 0.2, fill=255)
    img.paste(gradient, (0, 0), mask)

    d = ImageDraw.Draw(img)
    u = s / 100  # 1 % of the canvas

    # Folder: tab, then body.
    folder = (255, 255, 255, 255)
    d.rounded_rectangle((20 * u, 26 * u, 46 * u, 40 * u), radius=4 * u, fill=folder, corners=(True, True, False, False))
    d.polygon([(40 * u, 26 * u), (46 * u, 26 * u), (51 * u, 33 * u), (40 * u, 33 * u)], fill=folder)
    d.rounded_rectangle((20 * u, 32 * u, 80 * u, 76 * u), radius=6 * u, fill=folder)

    # A strip of film with a download arrow below it, dropping into the folder.
    ink = (196, 84, 12, 255)
    frame = (255, 176, 90, 255)
    cx = 50 * u
    top, bottom = 34 * u, 50 * u
    d.rounded_rectangle((cx - 13 * u, top, cx + 13 * u, bottom), radius=1.5 * u, fill=ink)
    for k in range(6):  # sprocket holes along both edges
        x = cx - 11.85 * u + k * 4.3 * u
        d.rectangle((x, top + 1.3 * u, x + 2.2 * u, top + 3.3 * u), fill=folder)
        d.rectangle((x, bottom - 3.3 * u, x + 2.2 * u, bottom - 1.3 * u), fill=folder)
    for x in (cx - 11.5 * u, cx + 0.75 * u):  # two frames
        d.rectangle((x, top + 4.8 * u, x + 10.75 * u, bottom - 4.8 * u), fill=frame)
    d.rectangle((cx - 4 * u, 53 * u, cx + 4 * u, 65 * u), fill=ink)
    d.polygon([(cx - 10 * u, 63.5 * u), (cx + 10 * u, 63.5 * u), (cx, 73.5 * u)], fill=ink)

    return img.resize((SIZE, SIZE), Image.LANCZOS)


def main() -> None:
    icon = draw()
    assets = ROOT / "src" / "VidArchiverGui.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    icon.save(assets / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    icon.resize((512, 512), Image.LANCZOS).save(assets / "icon.png", optimize=True)
    macos = ROOT / "packaging" / "macos"
    macos.mkdir(parents=True, exist_ok=True)
    icon.save(macos / "AppIcon.icns")
    write_msix_assets(icon, ROOT / "packaging" / "msix" / "Assets")


def write_msix_assets(icon: Image.Image, out: Path) -> None:
    """Tiles and logos referenced by packaging/msix/AppxManifest.xml; makepri picks the right size at runtime."""
    out.mkdir(parents=True, exist_ok=True)

    def tile(w: int, h: int, fill: float) -> Image.Image:
        """The icon centred on a transparent w x h canvas, taking up `fill` of the shorter side."""
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        side = round(min(w, h) * fill)
        canvas.paste(icon.resize((side, side), Image.LANCZOS), ((w - side) // 2, (h - side) // 2))
        return canvas

    for name, w, h, fill in (
        ("Square44x44Logo", 44, 44, 1.0),
        ("Square71x71Logo", 71, 71, 0.8),
        ("Square150x150Logo", 150, 150, 0.66),
        ("Wide310x150Logo", 310, 150, 0.66),
        ("Square310x310Logo", 310, 310, 0.66),
        ("StoreLogo", 50, 50, 1.0),
    ):
        for scale in (100, 125, 150, 200, 400):
            tile(w * scale // 100, h * scale // 100, fill).save(out / f"{name}.scale-{scale}.png", optimize=True)
    # Taskbar / Start list icons at exact pixel sizes; "altform-unplated" shows them without the accent backplate.
    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        small = tile(size, size, 1.0)
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            small.save(out / f"Square44x44Logo.targetsize-{size}{suffix}.png", optimize=True)


if __name__ == "__main__":
    main()
