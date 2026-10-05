"""Build a seamless runtime plate from a reviewed background-only candidate."""

from argparse import ArgumentParser
from pathlib import Path

import numpy as np
from PIL import Image


def boundary_error(image: Image.Image, visible_mask: Image.Image, radius: int = 2) -> float:
    pixels = np.asarray(image.convert("RGB"), dtype=np.float32)
    visible = np.asarray(visible_mask.convert("L")) >= 128
    errors = []
    for axis in (0, 1):
        shifted_pixels = np.roll(pixels, -radius, axis=axis)
        shifted_visible = np.roll(visible, -radius, axis=axis)
        boundary = visible != shifted_visible
        edge = [slice(None)] * 2
        edge[axis] = slice(-radius, None)
        boundary[tuple(edge)] = False
        if boundary.any():
            errors.append(np.abs(pixels - shifted_pixels)[boundary].mean())
    return float(np.mean(errors)) if errors else 0.0


def reconstruct(reference: Image.Image, candidate: Image.Image, visible_mask: Image.Image) -> Image.Image:
    reference.verify()
    mask = visible_mask.convert("L")
    return candidate.convert("RGB").resize(mask.size, Image.Resampling.LANCZOS)


def main() -> None:
    parser = ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--mask", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    with Image.open(args.reference) as reference, Image.open(args.candidate) as candidate, Image.open(args.mask) as mask:
        output = reconstruct(reference, candidate, mask)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    output.save(args.output, optimize=True)


if __name__ == "__main__":
    main()
