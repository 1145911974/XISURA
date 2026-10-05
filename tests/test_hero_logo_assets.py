import importlib.util
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "generate_hero_logo_assets.py"


def load_assets():
    spec = importlib.util.spec_from_file_location("hero_logo_assets", SCRIPT)
    assert spec is not None and spec.loader is not None
    assets = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(assets)
    return assets


def load_rgba(path: Path) -> Image.Image:
    with Image.open(path) as image:
        return image.convert("RGBA")


def load_alpha(path: Path) -> Image.Image:
    with Image.open(path) as image:
        return image.getchannel("A").copy()


def neutral_metal_seed(source: Image.Image, core_box: tuple[int, int, int, int]) -> Image.Image:
    mask = Image.new("L", source.size)
    pixels = mask.load()
    for y in range(source.height):
        for x in range(source.width):
            red, green, blue = source.getpixel((x, y))
            pixels[x, y] = 255 if (
                max(red, green, blue) >= 40
                and max(red, green, blue) - min(red, green, blue) <= 32
            ) else 0
    return mask.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))


def neutral_metal_surface(source: Image.Image, core_box: tuple[int, int, int, int]) -> Image.Image:
    return neutral_metal_seed(source, core_box).filter(ImageFilter.MaxFilter(25))


class HeroLogoAssetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SCRIPT.exists():
            raise AssertionError("hero logo generator is missing")
        cls.assets = load_assets()
        cls.directory = tempfile.TemporaryDirectory()
        cls.output = Path(cls.directory.name)
        cls.assets.build(output=cls.output)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.directory.cleanup()

    def test_every_layer_uses_the_exact_approved_canvas(self) -> None:
        paths = list(self.output.glob("HeroLogo*.png"))
        self.assertEqual(10, len(paths))
        for path in paths:
            with Image.open(path) as image:
                self.assertEqual((1254, 1254), image.size, path.name)

    def test_mode_variants_keep_identical_alpha_masks(self) -> None:
        for family in ("Core", "Reflection"):
            office = load_alpha(self.output / f"HeroLogo{family}Office.png")
            for mode in ("Gaming", "Turbo", "Custom"):
                actual = load_alpha(self.output / f"HeroLogo{family}{mode}.png")
                self.assertIsNone(ImageChops.difference(office, actual).getbbox(), f"{family} {mode}")

    def test_background_flow_outside_shell_and_core_is_transparent(self) -> None:
        with Image.open(self.assets.SOURCE) as source_file:
            source = source_file.convert("RGB")
        allowed = neutral_metal_surface(source, self.assets.CORE_BOX)
        core_box = Image.new("L", source.size)
        left, top, right, bottom = self.assets.CORE_BOX
        ImageDraw.Draw(core_box).rectangle((left - 8, top - 8, right + 8, bottom + 8), fill=255)
        allowed = ImageChops.lighter(allowed, core_box)

        actual = Image.new("L", source.size)
        for name in ("Silver", "ReflectionOffice", "CoreOffice"):
            actual = ImageChops.lighter(actual, load_alpha(self.output / f"HeroLogo{name}.png"))
        forbidden = ImageChops.subtract(actual.point(lambda value: 255 if value else 0), allowed)
        self.assertIsNone(forbidden.getbbox())

    def test_reflection_stays_near_neutral_metal_in_the_reference(self) -> None:
        with Image.open(self.assets.SOURCE) as source_file:
            source = source_file.convert("RGB")
        allowed = neutral_metal_surface(source, self.assets.CORE_BOX)
        reflection = load_alpha(self.output / "HeroLogoReflectionOffice.png").point(
            lambda value: 255 if value else 0
        )
        self.assertIsNone(ImageChops.subtract(reflection, allowed).getbbox())

    def test_split_keeps_every_neutral_shell_and_blue_core_seed(self) -> None:
        with Image.open(self.assets.SOURCE) as source_file:
            source = source_file.convert("RGB")
        required = neutral_metal_seed(source, self.assets.CORE_BOX)
        pixels = required.load()
        left, top, right, bottom = self.assets.CORE_BOX
        for y in range(source.height):
            for x in range(source.width):
                red, green, blue = source.getpixel((x, y))
                blue_core = left <= x < right and top <= y < bottom and self.assets.is_blue(red, green, blue)
                if blue_core:
                    pixels[x, y] = 255

        actual = Image.new("L", source.size)
        for name in ("Silver", "ReflectionOffice", "CoreOffice"):
            actual = ImageChops.lighter(
                actual,
                load_alpha(self.output / f"HeroLogo{name}.png"),
            )
        actual = actual.point(lambda value: 255 if value else 0)
        self.assertIsNone(ImageChops.subtract(required, actual).getbbox())
        self.assertGreater(
            load_alpha(self.output / "HeroLogoCoreOffice.png").getpixel((628, 796)),
            0,
        )

    def test_core_region_does_not_cut_through_bright_silver(self) -> None:
        with Image.open(self.assets.SOURCE) as source_file:
            source = source_file.convert("RGB")
        actual = Image.new("L", source.size)
        for name in ("Silver", "ReflectionOffice", "CoreOffice"):
            actual = ImageChops.lighter(actual, load_alpha(self.output / f"HeroLogo{name}.png"))
        actual_pixels = actual.load()
        missing = Image.new("L", source.size)
        missing_pixels = missing.load()
        left, top, right, bottom = self.assets.CORE_BOX
        for y in range(top, bottom):
            for x in range(left, right):
                red, green, blue = source.getpixel((x, y))
                bright_silver = min(red, green, blue) >= 80 and max(red, green, blue) - min(red, green, blue) <= 40
                if bright_silver and not actual_pixels[x, y]:
                    missing_pixels[x, y] = 255
        self.assertIsNone(missing.getbbox())

    def test_office_layers_reconstruct_visible_logo_within_three_channels(self) -> None:
        with Image.open(self.assets.SOURCE) as expected_file:
            expected = expected_file.convert("RGB")
        actual = Image.new("RGBA", expected.size, (0, 0, 0, 255))
        for name in ("Silver", "ReflectionOffice", "CoreOffice"):
            actual.alpha_composite(load_rgba(self.output / f"HeroLogo{name}.png"))
        actual = actual.convert("RGB")

        mask = load_alpha(self.output / "HeroLogoSilver.png")
        mask = ImageChops.lighter(
            mask,
            load_alpha(self.output / "HeroLogoReflectionOffice.png"),
        )
        mask = ImageChops.lighter(
            mask,
            load_alpha(self.output / "HeroLogoCoreOffice.png"),
        )
        expected_bytes = expected.tobytes()
        actual_bytes = actual.tobytes()
        mask_bytes = mask.tobytes()
        for pixel in range(expected.width * expected.height):
            visible = mask_bytes[pixel]
            if visible:
                offset = pixel * 3
                self.assertLessEqual(
                    max(
                        abs(expected_bytes[offset + channel] - actual_bytes[offset + channel])
                        for channel in range(3)
                    ),
                    3,
                )


if __name__ == "__main__":
    unittest.main()
