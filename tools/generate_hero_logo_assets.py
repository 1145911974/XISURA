import colorsys
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs/superpowers/specs/assets/2026-08-24-approved-silver-crystal-logo.png"
OUTPUT = ROOT / "src/Jiaolong.ControlCenter/Assets/Brand"
CANVAS = (1254, 1254)
CORE_BOX = (390, 560, 865, 965)
MODE_HUES = {
    "Office": 0.60,
    "Gaming": 0.083,
    "Turbo": 0.98,
    "Custom": 0.75,
}
CUSTOM_PROFILE_COLORS = {
    "Profile1": (0x8C, 0x94, 0x9F),
    "Profile2": (0x27, 0xD9, 0x80),
    "Profile3": (0x9A, 0x5C, 0xFF),
}


def is_blue(red: int, green: int, blue: int) -> bool:
    return blue >= 20 and blue - red >= 8 and blue - green >= 2


def _confidence_masks(
    source: Image.Image, core_box: tuple[int, int, int, int]
) -> tuple[Image.Image, Image.Image]:
    metal = Image.new("L", source.size)
    core = Image.new("L", source.size)
    source_pixels = source.load()
    metal_pixels = metal.load()
    core_pixels = core.load()
    left, top, right, bottom = core_box
    for y in range(source.height):
        for x in range(source.width):
            red, green, blue = source_pixels[x, y]
            if (
                left <= x < right
                and top <= y < bottom
                and blue >= 50
                and blue - red >= 20
                and blue - green >= 6
            ):
                core_pixels[x, y] = 255
            if max(red, green, blue) >= 40 and max(red, green, blue) - min(red, green, blue) <= 32:
                metal_pixels[x, y] = 255
    metal = metal.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    core = core.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    return metal, core


def build_foreground_mask(
    source: Image.Image, core_box: tuple[int, int, int, int] = CORE_BOX
) -> Image.Image:
    source = source.convert("RGB")
    metal, core = _confidence_masks(source, core_box)
    support = ImageChops.lighter(
        metal.filter(ImageFilter.MaxFilter(25)),
        core.filter(ImageFilter.MaxFilter(15)),
    )
    visible = source.convert("L").point(lambda value: 255 if value >= 24 else 0)
    foreground = ImageChops.multiply(support, visible)
    foreground = foreground.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5))
    foreground = foreground.filter(ImageFilter.GaussianBlur(0.65))
    return foreground.point(lambda value: 0 if value < 16 else value)


def _masked_pixel(pixel: tuple[int, int, int], alpha: int) -> tuple[int, int, int, int]:
    if alpha == 0:
        return (0, 0, 0, 0)
    if alpha < 255:
        return tuple(min(255, round(channel * 255 / alpha)) for channel in pixel) + (alpha,)
    return pixel + (255,)


def split_reference(
    image: Image.Image,
) -> tuple[Image.Image, Image.Image, Image.Image]:
    if image.size != CANVAS:
        raise ValueError(f"approved logo must be {CANVAS[0]}x{CANVAS[1]}, got {image.size}")
    source = image.convert("RGB")
    source_pixels = source.load()
    foreground = build_foreground_mask(source)
    metal_seed, core_seed = _confidence_masks(source, CORE_BOX)
    shell_surface = metal_seed.filter(ImageFilter.MaxFilter(25)).load()
    near_core = core_seed.filter(ImageFilter.MaxFilter(15)).load()
    foreground_pixels = foreground.load()
    silver = Image.new("RGBA", CANVAS)
    core = Image.new("RGBA", CANVAS)
    reflection = Image.new("RGBA", CANVAS)
    silver_pixels = silver.load()
    core_pixels = core.load()
    reflection_pixels = reflection.load()

    for y in range(CANVAS[1]):
        for x in range(CANVAS[0]):
            pixel = source_pixels[x, y]
            red, green, blue = pixel
            alpha = foreground_pixels[x, y]
            if near_core[x, y] and alpha:
                core_pixels[x, y] = _masked_pixel(pixel, alpha)
            elif shell_surface[x, y] and is_blue(red, green, blue) and alpha:
                reflection_pixels[x, y] = _masked_pixel(pixel, alpha)
            elif shell_surface[x, y] and alpha:
                silver_pixels[x, y] = _masked_pixel(pixel, alpha)

    return silver, core, reflection


def recolor_layer(image: Image.Image, target_hue: float) -> Image.Image:
    result = image.copy().convert("RGBA")
    pixels = result.load()
    for y in range(result.height):
        for x in range(result.width):
            red, green, blue, alpha = pixels[x, y]
            if not alpha:
                continue
            _, saturation, value = colorsys.rgb_to_hsv(red / 255, green / 255, blue / 255)
            mapped = colorsys.hsv_to_rgb(target_hue, saturation, value)
            pixels[x, y] = tuple(round(channel * 255) for channel in mapped) + (alpha,)
    return result


def neutralize_layer(image: Image.Image) -> Image.Image:
    result = image.copy().convert("RGBA")
    pixels = result.load()
    for y in range(result.height):
        for x in range(result.width):
            red, green, blue, alpha = pixels[x, y]
            value = max(red, green, blue)
            pixels[x, y] = (value, value, value, alpha)
    return result


def recolor_layer_to_color(
    image: Image.Image, target_color: tuple[int, int, int]
) -> Image.Image:
    target_hue, target_saturation, _ = colorsys.rgb_to_hsv(
        *(channel / 255 for channel in target_color)
    )
    result = image.copy().convert("RGBA")
    pixels = result.load()
    for y in range(result.height):
        for x in range(result.width):
            red, green, blue, alpha = pixels[x, y]
            if not alpha:
                continue
            _, source_saturation, value = colorsys.rgb_to_hsv(
                red / 255, green / 255, blue / 255
            )
            mapped = colorsys.hsv_to_rgb(
                target_hue, target_saturation * source_saturation, value
            )
            pixels[x, y] = tuple(round(channel * 255) for channel in mapped) + (
                alpha,
            )
    return result


def compose_full_logo(
    silver: Image.Image, reflection: Image.Image, core: Image.Image
) -> Image.Image:
    result = Image.new("RGBA", CANVAS)
    result.alpha_composite(silver)
    result.alpha_composite(reflection)
    result.alpha_composite(core)
    return result


def build(source: Path = SOURCE, output: Path = OUTPUT) -> None:
    output.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as source_image:
        silver, core, reflection = split_reference(source_image)
    silver.save(output / "HeroLogoSilver.png", optimize=True)
    neutralize_layer(reflection).save(output / "HeroLogoNeutralShell.png", optimize=True)

    for mode, hue in MODE_HUES.items():
        mode_core = core if mode == "Office" else recolor_layer(core, hue)
        mode_reflection = reflection if mode == "Office" else recolor_layer(reflection, hue)
        mode_core.save(output / f"HeroLogoCore{mode}.png", optimize=True)
        mode_reflection.save(output / f"HeroLogoReflection{mode}.png", optimize=True)
        if mode != "Custom":
            compose_full_logo(silver, mode_reflection, mode_core).save(
                output / f"HeroLogoFull{mode}.png", optimize=True
            )

    for profile, color in CUSTOM_PROFILE_COLORS.items():
        profile_core = recolor_layer_to_color(core, color)
        profile_reflection = recolor_layer_to_color(reflection, color)
        profile_core.save(output / f"HeroLogoCoreCustom{profile}.png", optimize=True)
        compose_full_logo(silver, profile_reflection, profile_core).save(
            output / f"HeroLogoFullCustom{profile}.png", optimize=True
        )

    approved = compose_full_logo(silver, reflection, core)
    approved.save(output / "HeroLogoApproved.png", optimize=True)


if __name__ == "__main__":
    build()
