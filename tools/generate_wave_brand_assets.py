"""Import approved C runtime masters/exports without modifying fixed application assets.

The delivered SVG, PNG and ICO files are authoritative; no geometry is redrawn.
Use --source-root for the design tree, or validate the already imported runtime assets.
"""
import argparse
import shutil
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "src/Jiaolong.ControlCenter/Assets/Brand"
ASSETS = BRAND.parent
MODES = ("Office", "Gaming", "Turbo", "Custom1", "Custom2", "Custom3")
SIZES = (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)
GRADIENTS = ("jlw-upper-gradient", "jlw-lower-gradient", "upper-sheen", "lower-sheen",
             "jlw-rear-left-gradient", "jlw-rear-right-gradient", "jlw-front-gradient")


def validate_masters():
    for variant in ("Home", "System"):
        geometry = stop_layout = None
        for mode in MODES:
            master = ET.parse(BRAND / f"CModeIcons/JiaolongC{mode}-{variant}.svg").getroot()
            assert not any(node.tag.endswith("}image") for node in master.iter()), "Raster SVG is not a master"
            current_geometry = [(node.tag, sorted(node.attrib.items())) for node in master.iter()
                                if node.tag.rsplit("}", 1)[-1] in ("path", "use", "rect", "g")]
            current_layout = []
            for ident in GRADIENTS:
                gradient = next(node for node in master.iter() if node.get("id") == ident)
                current_layout.append((sorted(gradient.attrib.items()), [node.get("offset") for node in gradient]))
            if geometry is not None:
                assert current_geometry == geometry, "Approved geometry diverged"
                assert current_layout == stop_layout, "Approved gradient directions or stop positions diverged"
            geometry, stop_layout = current_geometry, current_layout
    for path in [BRAND / f"CModeIcons/JiaolongC{mode}.ico" for mode in MODES]:
        with Image.open(path) as image:
            assert image.ico.sizes() == {(size, size) for size in SIZES}, f"ICO sizes changed: {path}"


def import_assets(source_root):
    review = source_root / "design/logo-wave/material-review"
    modes = review / "c-mode-vectors"
    destination = BRAND / "CModeIcons"
    destination.mkdir(parents=True, exist_ok=True)
    for mode in MODES:
        name = f"JiaolongC{mode}"
        for variant in ("Home", "System"):
            shutil.copy2(modes / f"svg/{name}-{variant}.svg", destination / f"{name}-{variant}.svg")
        shutil.copy2(modes / f"icons/{name}.ico", destination / f"{name}.ico")
        for path in (modes / "icons").glob(f"{name}-*.png"):
            shutil.copy2(path, destination / path.name)
    for name in ("palette.json", "JiaolongCColorable-Home.svg", "JiaolongCColorable-System.svg"):
        shutil.copy2(modes / name, destination / name)


def generate_runtime_icons():
    # Remove transparent padding only; retain the approved shape, alpha edge and aspect ratio.
    for source in [BRAND / "JiaolongWaveApp.ico", *[BRAND / f"CModeIcons/JiaolongC{mode}.ico" for mode in MODES]]:
        with Image.open(source) as icon:
            image = icon.ico.getimage((256, 256)).convert("RGBA")
        image = image.crop(image.getbbox())
        side = max(image.size)
        canvas = Image.new("RGBA", (side, side))
        canvas.alpha_composite(image, ((side - image.width) // 2, (side - image.height) // 2))
        canvas.resize((256, 256), Image.Resampling.LANCZOS).save(
            source.with_name(source.stem + "-runtime.ico"), sizes=[(size, size) for size in SIZES])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path)
    args = parser.parse_args()
    if args.source_root:
        import_assets(args.source_root)
    validate_masters()
    generate_runtime_icons()
    print("Verified six C runtime materials and six 15-size ICOs; fixed application assets unchanged.")


if __name__ == "__main__":
    main()
