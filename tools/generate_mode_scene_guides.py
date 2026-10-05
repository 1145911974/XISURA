"""Generate deterministic RGBA motion guides for the nine approved scenes."""

from argparse import ArgumentParser
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter, ImageOps


SCENE_KEYS = (
    "office-orbital-focus",
    "office-constellation-flow",
    "game-facet-arena",
    "game-volumetric-cloud",
    "turbo-digital-fault-planes",
    "turbo-hex-pressure-cells",
    "custom-preset-1-graphite-strata",
    "custom-preset-2-liquid-jade",
    "custom-preset-3-purple-filaments",
)

_FAMILIES = {
    "office-orbital-focus": "flow",
    "office-constellation-flow": "flow-reverse",
    "game-facet-arena": "angular",
    "game-volumetric-cloud": "volume",
    "turbo-digital-fault-planes": "angular-reverse",
    "turbo-hex-pressure-cells": "angular",
    "custom-preset-1-graphite-strata": "flow-reverse",
    "custom-preset-2-liquid-jade": "volume",
    "custom-preset-3-purple-filaments": "flow",
}


def build_guide(
    scene_key: str,
    size: tuple[int, int] = (1672, 941),
    plate: Image.Image | None = None,
) -> Image.Image:
    try:
        family = _FAMILIES[scene_key]
    except KeyError as error:
        raise ValueError(f"Unknown scene key: {scene_key}") from error

    vertical = Image.linear_gradient("L").resize(size)
    horizontal = vertical.rotate(90, expand=True).resize(size)
    radial = Image.radial_gradient("L").resize(size)

    if family.startswith("angular"):
        red, green, blue, alpha = horizontal, vertical, ImageOps.invert(radial), radial
    elif family == "volume":
        red, green, blue, alpha = radial, ImageOps.invert(radial), vertical, radial
    else:
        red, green, blue, alpha = horizontal, ImageOps.invert(vertical), radial, horizontal

    if family.endswith("reverse"):
        red, green, alpha = ImageOps.invert(red), ImageOps.invert(green), ImageOps.invert(alpha)

    if plate is not None:
        material = plate.convert("RGB").resize(size, Image.Resampling.LANCZOS)
        plate_red, plate_green, plate_blue = material.split()
        mask = ImageOps.autocontrast(ImageChops.lighter(plate_red, ImageChops.lighter(plate_green, plate_blue)))
        mask = mask.filter(ImageFilter.GaussianBlur(max(1, min(size) // 240)))
        neutral = Image.new("L", size, 128)
        red = Image.composite(red, neutral, mask)
        green = Image.composite(green, neutral, mask)
        blue = mask

    return Image.merge("RGBA", (red, green, blue, alpha))


def main() -> None:
    parser = ArgumentParser()
    parser.add_argument("--scene-key", required=True, choices=SCENE_KEYS)
    parser.add_argument("--plate", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    args.output.parent.mkdir(parents=True, exist_ok=True)
    plate = Image.open(args.plate) if args.plate else None
    try:
        build_guide(args.scene_key, plate=plate).save(args.output, optimize=True)
    finally:
        if plate is not None:
            plate.close()


if __name__ == "__main__":
    main()
