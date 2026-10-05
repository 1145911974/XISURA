import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "design/mockups/mechrevo-2026-screen-set/01-home-office.png"
OUTPUT = ROOT / "src/Jiaolong.ControlCenter/Assets/Reference"
FLUENT_FONT = ROOT / "tools/vendor/fluent-system-icons/FluentSystemIcons-Filled.ttf"

STRONG_COOLING_RECT = (505, 739, 663, 887, 20)
MODE_BAR_RECT = (901, 737, 1630, 885, 20)
ROUND_RECTS = (
    STRONG_COOLING_RECT,
    MODE_BAR_RECT,
    (492, 114, 689, 153, 10),
    (36, 668, 98, 727, 12), (112, 668, 174, 727, 12), (188, 668, 250, 727, 12),
    (36, 742, 98, 800, 12), (112, 742, 174, 800, 12), (188, 742, 250, 800, 12),
    (36, 814, 98, 873, 12), (112, 814, 174, 873, 12), (188, 814, 250, 873, 12),
)
PALETTE = {
    "Office": (22, 119, 255),
    "Gaming": (255, 138, 31),
    "Turbo": (255, 49, 65),
    "Custom": (154, 92, 255),
}
QUICK_ICONS = {
    "Wifi": (48, 679, 86, 713),
    "Bluetooth": (129, 679, 157, 713),
    "Touchpad": (200, 680, 238, 714),
    "Windows": (47, 751, 88, 789),
    "Osd": (123, 751, 163, 789),
    "Fn": (199, 751, 239, 789),
}
MODE_INDEX = {"Office": 0, "Gaming": 1, "Turbo": 2, "Custom": 3}
MODE_BAR_CELLS = (901, 1084, 1266, 1449, 1630)
OFFICE_MODE_CELL = (901, 737, 1084, 885)
PLANAR_ICON_COLOR = (240, 244, 248, 190)
ICON_CUTOUT = (0, 0, 0, 0)
MONITOR_ICON_SIZE = 24
MONITOR_ICON_VISIBLE_SIZE = 22
MONITOR_ICON_TEXT_GAP = 12
MONITOR_ICON_CENTERS = {
    "cpu": (940, 185),
    "gpu": (1302, 185),
    "storage": (940, 461),
    "fan": (1302, 461),
}
FLUENT_GLYPHS = {
    "Home": 62600,
    "Gauge": 62674,
    "Board": 61923,
    "Lightbulb": 62689,
    "Flowchart": 58950,
    "PulseSquare": 59881,
    "Settings": 63155,
    "Memory": 61680,
    "HardDrive": 983833,
    "Desktop": 62298,
    "Games": 62549,
    "Flash": 58918,
    "Hexagon": 983788,
    "Wifi": 63685,
    "Bluetooth": 61919,
    "Window": 63693,
}


def recolor_accent(image: Image.Image, color: tuple[int, int, int]) -> Image.Image:
    pixels = image.convert("RGB")
    source = pixels.load()
    for y in range(pixels.height):
        for x in range(pixels.width):
            red, green, blue = source[x, y]
            strongest_other = max(red, green)
            saturation = (blue - strongest_other) / max(1, blue)
            if blue > 8 and saturation > 0.10:
                strength = blue / 255
                neutral = min(red, green, blue) * 0.12
                source[x, y] = tuple(min(255, round(neutral + channel * strength)) for channel in color)
    return pixels


def neutralize_accent(
    image: Image.Image,
    box: tuple[int, int, int, int],
    color: tuple[int, int, int],
) -> None:
    """Remove only the Office selection tint; dimensions and content stay fixed."""
    pixels = image.load()
    left, top, right, bottom = box
    dominant = max(range(3), key=color.__getitem__)
    for y in range(top, bottom):
        for x in range(left, right):
            red, green, blue, alpha = pixels[x, y]
            channels = (red, green, blue)
            others = channels[:dominant] + channels[dominant + 1:]
            if channels[dominant] > 8 and all(channels[dominant] > value * 1.01 for value in others):
                neutral = round(red * 0.20 + green * 0.25 + blue * 0.10)
                pixels[x, y] = (neutral, neutral, neutral, alpha)


def round_control(image: Image.Image, rect: tuple[int, int, int, int, int]) -> None:
    left, top, right, bottom, radius = rect
    original = image.copy()
    pixels = image.load()
    source = original.load()
    for y in range(top, bottom):
        for x in range(left, right):
            dx = min(x - left, right - 1 - x)
            dy = min(y - top, bottom - 1 - y)
            if dx >= radius or dy >= radius:
                continue
            if (dx - radius) ** 2 + (dy - radius) ** 2 <= radius ** 2:
                continue
            corner_x = left + radius if x < left + radius else right - 1 - radius
            corner_y = top + radius if y < top + radius else bottom - 1 - radius
            vector_x, vector_y = x - corner_x, y - corner_y
            distance = max(1, (vector_x * vector_x + vector_y * vector_y) ** 0.5)
            sample_x = round(corner_x + vector_x * (radius + 2) / distance)
            sample_y = round(corner_y + vector_y * (radius + 2) / distance)
            sample_x = min(image.width - 1, max(0, sample_x))
            sample_y = min(image.height - 1, max(0, sample_y))
            pixels[x, y] = source[sample_x, sample_y]


def select_mode_cell(
    image: Image.Image,
    index: int,
    color: tuple[int, int, int],
    indicator_y: int = 866,
    show_indicator: bool = True,
) -> None:
    left, right = MODE_BAR_CELLS[index], MODE_BAR_CELLS[index + 1]
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    for y in range(738, 885):
        alpha = round(64 * (1 - abs((y - 811) / 74)))
        draw.line((left + 1, y, right - 1, y), fill=(*color, max(0, alpha)))
    draw.rectangle((left, 737, right, 885), outline=(*color, 230), width=1)
    if show_indicator:
        draw.rounded_rectangle(
            ((left + right) // 2 - 22, indicator_y, (left + right) // 2 + 22, indicator_y + 3),
            radius=2,
            fill=(*color, 255),
        )
    image.alpha_composite(overlay)


def _regular_polygon(center: tuple[int, int], radius: int, sides: int, rotation: float = 0) -> list[tuple[float, float]]:
    from math import cos, pi, sin

    center_x, center_y = center
    return [
        (
            center_x + radius * cos(rotation + index * 2 * pi / sides),
            center_y + radius * sin(rotation + index * 2 * pi / sides),
        )
        for index in range(sides)
    ]


def erase_existing_icon(image: Image.Image, box: tuple[int, int, int, int]) -> None:
    """Reconstruct the local background so bright glyphs and dark shadows both disappear."""
    left, top, right, bottom = box
    pixels = image.load()
    for y in range(top, bottom):
        before = pixels[max(0, left - 3), y]
        after = pixels[min(image.width - 1, right + 3), y]
        for x in range(left, right):
            blend = (x - left + 1) / (right - left + 1)
            pixels[x, y] = tuple(
                round(before[index] * (1 - blend) + after[index] * blend)
                for index in range(4)
            )


def erase_existing_icon_from_left(image: Image.Image, box: tuple[int, int, int, int]) -> None:
    """Clear an icon beside text without sampling the title's bright edge."""
    left, top, right, bottom = box
    pixels = image.load()
    sample_x = max(0, left - 3)
    for y in range(top, bottom):
        background = pixels[sample_x, y]
        for x in range(left, right):
            pixels[x, y] = background


def draw_fluent_icon(
    draw: ImageDraw.ImageDraw,
    name: str,
    center: tuple[int, int],
    size: int,
    color: tuple[int, int, int, int],
) -> None:
    """Draw one optically centered glyph from Microsoft's Fluent filled family."""
    if not FLUENT_FONT.exists():
        raise FileNotFoundError(f"Missing Fluent icon font: {FLUENT_FONT}")
    font = ImageFont.truetype(str(FLUENT_FONT), size)
    draw.text(center, chr(FLUENT_GLYPHS[name]), font=font, fill=color, anchor="mm")


def _normalize_icon_surface(icon: Image.Image) -> Image.Image:
    """Fit different source silhouettes into one 24 px optical box."""
    bounds = icon.getchannel("A").getbbox()
    if bounds is None:
        return Image.new("RGBA", (MONITOR_ICON_SIZE, MONITOR_ICON_SIZE), ICON_CUTOUT)
    crop = icon.crop(bounds)
    crop.thumbnail(
        (MONITOR_ICON_VISIBLE_SIZE, MONITOR_ICON_VISIBLE_SIZE),
        Image.Resampling.LANCZOS,
    )
    result = Image.new("RGBA", (MONITOR_ICON_SIZE, MONITOR_ICON_SIZE), ICON_CUTOUT)
    result.alpha_composite(
        crop,
        ((MONITOR_ICON_SIZE - crop.width) // 2, (MONITOR_ICON_SIZE - crop.height) // 2),
    )
    return result


def _paste_centered_icon(image: Image.Image, icon: Image.Image, center: tuple[int, int]) -> None:
    image.alpha_composite(
        icon,
        (center[0] - MONITOR_ICON_SIZE // 2, center[1] - MONITOR_ICON_SIZE // 2),
    )


def draw_normalized_fluent_icon(
    image: Image.Image,
    name: str,
    center: tuple[int, int],
    color: tuple[int, int, int, int],
) -> None:
    source = Image.new("RGBA", (64, 64), ICON_CUTOUT)
    draw_fluent_icon(ImageDraw.Draw(source), name, (32, 32), 48, color)
    _paste_centered_icon(image, _normalize_icon_surface(source), center)


def draw_gpu_icon(
    draw: ImageDraw.ImageDraw,
    center: tuple[int, int],
    color: tuple[int, int, int, int],
) -> None:
    """Conventional dual-fan graphics-card silhouette on the shared 32 px grid."""
    center_x, center_y = center
    draw.rounded_rectangle(
        (center_x - 16, center_y - 10, center_x + 12, center_y + 9),
        radius=4,
        fill=color,
    )
    draw.rectangle((center_x + 12, center_y - 6, center_x + 16, center_y + 6), fill=color)
    for fan_x in (center_x - 8, center_x + 4):
        draw.ellipse((fan_x - 5, center_y - 5, fan_x + 5, center_y + 5), fill=ICON_CUTOUT)
        draw.ellipse((fan_x - 2, center_y - 2, fan_x + 2, center_y + 2), fill=color)
    draw.rectangle((center_x - 11, center_y + 9, center_x + 6, center_y + 12), fill=color)


def draw_normalized_gpu_icon(
    image: Image.Image,
    center: tuple[int, int],
    color: tuple[int, int, int, int],
) -> None:
    source = Image.new("RGBA", (64, 64), ICON_CUTOUT)
    draw_gpu_icon(ImageDraw.Draw(source), (32, 32), color)
    _paste_centered_icon(image, _normalize_icon_surface(source), center)


def draw_monitor_fan_icon(
    image: Image.Image,
    center: tuple[int, int],
    color: tuple[int, int, int, int],
) -> None:
    """Draw the familiar five-blade silhouette used by gaming control centers."""
    scale = 4
    source = Image.new("RGBA", (96, 96), ICON_CUTOUT)
    draw = ImageDraw.Draw(source)
    source_center = 48

    def point(radius: float, degrees: float) -> tuple[int, int]:
        radians = math.radians(degrees)
        return (
            round(source_center + math.cos(radians) * radius * scale),
            round(source_center + math.sin(radians) * radius * scale),
        )

    for blade in range(5):
        angle = -90 + blade * 72
        draw.polygon(
            (
                point(2.4, angle - 10),
                point(5.0, angle - 30),
                point(9.0, angle - 25),
                point(10.5, angle + 1),
                point(8.5, angle + 20),
                point(4.8, angle + 23),
                point(2.4, angle + 10),
            ),
            fill=color,
        )
    hub = 2.7 * scale
    draw.ellipse(
        (source_center - hub, source_center - hub, source_center + hub, source_center + hub),
        fill=color,
    )
    _paste_centered_icon(image, _normalize_icon_surface(source), center)


def draw_monitor_icons(image: Image.Image) -> None:
    """Replace decorative marks with conventional, filled hardware glyphs."""
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    color = PLANAR_ICON_COLOR

    erase_existing_icon(image, (904, 105, 943, 141))
    for box in ((924, 168, 959, 202), (1282, 168, 1322, 202),
                (924, 443, 959, 478)):
        erase_existing_icon(image, box)
    erase_existing_icon_from_left(image, (1282, 443, 1323, 478))

    draw_fluent_icon(draw, "PulseSquare", (923, 123), 28, color)
    image.alpha_composite(overlay)
    draw_normalized_fluent_icon(image, "Memory", MONITOR_ICON_CENTERS["cpu"], color)
    draw_normalized_gpu_icon(image, MONITOR_ICON_CENTERS["gpu"], color)
    draw_normalized_fluent_icon(image, "HardDrive", MONITOR_ICON_CENTERS["storage"], color)
    draw_monitor_fan_icon(image, MONITOR_ICON_CENTERS["fan"], color)


def draw_mode_icons(image: Image.Image) -> None:
    """All performance modes share one semi-transparent white filled icon family."""
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    color = PLANAR_ICON_COLOR
    centers = ((992, 786), (1175, 786), (1357, 786), (1540, 786))
    for center_x, center_y in centers:
        erase_existing_icon(image, (center_x - 30, center_y - 30, center_x + 30, center_y + 26))
    erase_existing_icon(image, (1315, 811, 1400, 859))

    for center, glyph in ((centers[0], "Desktop"), (centers[1], "Games"), (centers[3], "Hexagon")):
        draw_fluent_icon(draw, glyph, center, 32, color)
    _draw_turbo_flash(draw, centers[2], 32, color)

    image.alpha_composite(overlay)


def _draw_fan(draw: ImageDraw.ImageDraw, center: tuple[int, int], radius: int, color: tuple[int, int, int, int]) -> None:
    """Draw a five-blade rotor without a backing tile."""
    center_x, center_y = center
    scale = radius / 10.5

    def point(distance: float, degrees: float) -> tuple[int, int]:
        radians = math.radians(degrees)
        return (round(center_x + math.cos(radians) * distance * scale), round(center_y + math.sin(radians) * distance * scale))

    for blade in range(5):
        angle = -90 + blade * 72
        draw.polygon(
            (point(2.4, angle - 10), point(5.0, angle - 30), point(9.0, angle - 25), point(10.5, angle + 1), point(8.5, angle + 20), point(4.8, angle + 23), point(2.4, angle + 10)),
            fill=color,
        )
    hub = max(3, round(2.7 * scale))
    draw.ellipse((center_x - hub, center_y - hub, center_x + hub, center_y + hub), fill=color)


def _draw_turbo_flash(
    draw: ImageDraw.ImageDraw,
    center: tuple[int, int],
    size: int,
    color: tuple[int, int, int, int],
) -> None:
    """A continuous lightning bolt; no horizontal crossbar or backing rectangle."""
    center_x, center_y = center
    scale = size / 48
    points = ((3, -22), (-10, 1), (-3, 1), (-7, 22), (11, -3), (4, -3))
    draw.polygon([(round(center_x + x * scale), round(center_y + y * scale)) for x, y in points], fill=color)


def _draw_quick_symbol(
    draw: ImageDraw.ImageDraw,
    name: str,
    center: tuple[int, int],
    color: tuple[int, int, int, int],
) -> None:
    center_x, center_y = center
    if name == "Wifi":
        draw_fluent_icon(draw, "Wifi", center, 29, color)
    elif name == "Bluetooth":
        draw_fluent_icon(draw, "Bluetooth", center, 29, color)
    elif name == "Touchpad":
        draw.rounded_rectangle((center_x - 15, center_y - 10, center_x + 15, center_y + 11), radius=4, fill=color)
        draw.rounded_rectangle((center_x - 11, center_y - 6, center_x + 11, center_y + 4), radius=2, fill=ICON_CUTOUT)
        draw.rectangle((center_x - 11, center_y + 7, center_x + 11, center_y + 8), fill=ICON_CUTOUT)
    elif name == "Windows":
        draw_fluent_icon(draw, "Window", center, 29, color)
    else:
        font_path = Path("C:/Windows/Fonts/arialbd.ttf")
        font = ImageFont.truetype(str(font_path), 12) if font_path.exists() else ImageFont.load_default()
        draw.text(center, "OSD" if name == "Osd" else "FN", font=font, fill=color, anchor="mm")


def draw_quick_icons(image: Image.Image) -> None:
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    centers = {
        "Wifi": (67, 697), "Bluetooth": (143, 697), "Touchpad": (219, 697),
        "Windows": (67, 771), "Osd": (143, 771), "Fn": (219, 771),
    }
    for name, box in QUICK_ICONS.items():
        erase_existing_icon(image, box)
        _draw_quick_symbol(draw, name, centers[name], PLANAR_ICON_COLOR)
    image.alpha_composite(overlay)


def draw_strong_cooling_icon(image: Image.Image) -> None:
    """The runtime owns both foreground states so translucent glass never ghosts."""
    erase_existing_icon(image, (561, 770, 606, 812))
    erase_existing_icon(image, (542, 818, 625, 852))


def draw_sidebar_icons(image: Image.Image) -> None:
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    centers = ((53, 141), (53, 209), (53, 277), (53, 345), (53, 413), (53, 482), (53, 552))
    for center_x, center_y in centers:
        erase_existing_icon(image, (center_x - 22, center_y - 22, center_x + 22, center_y + 22))

    sidebar_glyphs = ("Home", "Gauge", None, None, "Lightbulb", "Flowchart", "Settings")
    for center, glyph in zip(centers, sidebar_glyphs, strict=True):
        if center == centers[2]:
            draw_gpu_icon(draw, center, PLANAR_ICON_COLOR)
        elif glyph is None:
            _draw_fan(draw, center, 15, PLANAR_ICON_COLOR)
        else:
            draw_fluent_icon(draw, glyph, center, 29, PLANAR_ICON_COLOR)
    image.alpha_composite(overlay)


def save_planar_icons() -> None:
    black = (5, 8, 12, 255)
    for name in QUICK_ICONS:
        for variant, color in (("White", PLANAR_ICON_COLOR), ("Black", black)):
            icon = Image.new("RGBA", (36, 36), ICON_CUTOUT)
            _draw_quick_symbol(ImageDraw.Draw(icon), name, (18, 18), color)
            icon.save(OUTPUT / f"Quick{name}{variant}.png")
    strong = Image.new("RGBA", (40, 40), (0, 0, 0, 0))
    _draw_fan(ImageDraw.Draw(strong), (20, 20), 16, black)
    strong.save(OUTPUT / "StrongCoolingBlack.png")
    strong_white = Image.new("RGBA", (40, 40), (0, 0, 0, 0))
    _draw_fan(ImageDraw.Draw(strong_white), (20, 20), 16, PLANAR_ICON_COLOR)
    strong_white.save(OUTPUT / "StrongCoolingWhite.png")
    turbo_white = Image.new("RGBA", (48, 48), (0, 0, 0, 0))
    _draw_turbo_flash(ImageDraw.Draw(turbo_white), (24, 24), 48, PLANAR_ICON_COLOR)
    turbo_white.save(OUTPUT / "ModeTurboWhite.png")


def build() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    office = Image.open(SOURCE).convert("RGBA")
    for name, color in PALETTE.items():
        if name == "Office":
            mode = office.copy()
        else:
            base_surface = recolor_accent(office, color).convert("RGBA")
            neutralize_accent(base_surface, OFFICE_MODE_CELL, color)
            select_mode_cell(
                base_surface,
                MODE_INDEX[name],
                color,
                show_indicator=name != "Turbo",
            )
            mode = base_surface
        draw_monitor_icons(mode)
        draw_mode_icons(mode)
        draw_sidebar_icons(mode)
        draw_quick_icons(mode)
        draw_strong_cooling_icon(mode)
        for rect in ROUND_RECTS:
            round_control(mode, rect)
        mode.convert("RGB").save(OUTPUT / f"HomeMode{name}.png", optimize=True)
    save_planar_icons()


if __name__ == "__main__":
    build()
