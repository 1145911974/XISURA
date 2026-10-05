import math
import random
from pathlib import Path

from PIL import Image, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/Jiaolong.ControlCenter/Assets/StrongCooling"
OUTPUT = ASSETS / "Frames"
LAYER_OPACITY = {
    "StrongCoolingSource.png": 0.26,
    "StrongCoolingVeins.png": 0.20,
    "StrongCoolingGrain.png": 0.30,
}
THRESHOLDS = (0.09, 0.16, 0.24, 0.33, 0.43, 0.54, 0.66, 1.0)


def scaled_alpha(image: Image.Image, opacity: float) -> Image.Image:
    result = image.convert("RGBA")
    result.putalpha(result.getchannel("A").point(lambda value: round(value * opacity)))
    return result


def smooth_noise(size: tuple[int, int]) -> Image.Image:
    rng = random.Random(0xC01D)
    coarse = Image.new("L", (48, 48))
    coarse.putdata([rng.randrange(256) for _ in range(48 * 48)])
    return coarse.resize(size, Image.Resampling.BICUBIC).filter(ImageFilter.GaussianBlur(7))


def build() -> None:
    layers = [scaled_alpha(Image.open(ASSETS / name), opacity) for name, opacity in LAYER_OPACITY.items()]
    final = Image.new("RGBA", layers[0].size)
    for layer in layers:
        final.alpha_composite(layer)

    width, height = final.size
    noise = smooth_noise(final.size).load()
    source = final.load()
    bands = [Image.new("RGBA", final.size) for _ in THRESHOLDS]
    pixels = [band.load() for band in bands]
    diagonal = math.sqrt(2)
    for y in range(height):
        dy = (height - 1 - y) / (height - 1)
        for x in range(width):
            pixel = source[x, y]
            if not pixel[3]:
                continue
            dx = (width - 1 - x) / (width - 1)
            score = math.sqrt(dx * dx + dy * dy) / diagonal
            score += ((noise[x, y] / 255) - 0.5) * 0.075
            index = next((i for i, threshold in enumerate(THRESHOLDS) if score <= threshold), len(THRESHOLDS) - 1)
            pixels[index][x, y] = pixel

    OUTPUT.mkdir(parents=True, exist_ok=True)
    for index, band in enumerate(bands, 1):
        band.save(OUTPUT / f"Frame{index:02}.png", optimize=True)


if __name__ == "__main__":
    build()
