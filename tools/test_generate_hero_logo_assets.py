import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from tools import generate_hero_logo_assets as generator


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/Jiaolong.ControlCenter/Assets/Brand"


class HeroLogoAssetTests(unittest.TestCase):
    def mask(self, source: Image.Image, core_box: tuple[int, int, int, int]) -> Image.Image:
        self.assertTrue(hasattr(generator, "build_foreground_mask"))
        return generator.build_foreground_mask(source, core_box)

    def test_mask_closes_dark_hairline_inside_bright_metal(self) -> None:
        source = Image.new("RGB", (48, 48), "black")
        draw = ImageDraw.Draw(source)
        draw.rectangle((10, 10, 37, 37), fill=(205, 205, 205))
        draw.rectangle((23, 10, 24, 37), fill=(8, 8, 8))

        mask = self.mask(source, (12, 12, 36, 36))

        self.assertGreaterEqual(mask.getpixel((23, 24)), 240)
        self.assertEqual(0, mask.getpixel((2, 2)))

    def test_mask_rejects_thin_blue_background_trace(self) -> None:
        source = Image.new("RGB", (48, 48), "black")
        draw = ImageDraw.Draw(source)
        draw.rectangle((16, 17, 31, 30), fill=(15, 90, 230))
        draw.line((1, 4, 46, 4), fill=(0, 55, 180), width=1)

        mask = self.mask(source, (8, 8, 40, 40))

        self.assertGreaterEqual(mask.getpixel((24, 24)), 240)
        self.assertEqual(0, mask.getpixel((24, 4)))

    def test_runtime_assets_have_no_low_alpha_canvas_residue(self) -> None:
        paths = sorted(ASSETS.glob("HeroLogoFull*.png"))
        self.assertEqual(6, len(paths))
        for path in paths:
            alpha = Image.open(path).convert("RGBA").getchannel("A")
            low_alpha = sum(1 for value in alpha.get_flattened_data() if 0 < value < 16)
            self.assertLess(low_alpha, 5_000, path.name)


if __name__ == "__main__":
    unittest.main()
