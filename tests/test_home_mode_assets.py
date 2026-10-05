import importlib.util
import math
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageStat


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "home_mode_assets", ROOT / "tools" / "generate_home_mode_assets.py"
)
ASSETS = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(ASSETS)


class MonitorIconGeometryTests(unittest.TestCase):
    def test_monitor_icons_share_one_optical_size_and_keep_title_clearance(self) -> None:
        image = Image.new("RGBA", (1672, 941), (0, 0, 0, 0))
        ASSETS.draw_monitor_icons(image)

        regions = {
            "cpu": ((912, 157, 963, 211), 964),
            "gpu": ((1273, 157, 1327, 211), 1328),
            "storage": ((912, 433, 963, 488), 964),
            "fan": ((1273, 433, 1327, 488), 1328),
        }
        visible_sizes = []
        for name, (region, title_x) in regions.items():
            alpha = image.crop(region).getchannel("A")
            bounds = alpha.getbbox()
            self.assertIsNotNone(bounds, name)
            assert bounds is not None
            width = bounds[2] - bounds[0]
            height = bounds[3] - bounds[1]
            visible_sizes.append(max(width, height))
            right = region[0] + bounds[2]
            self.assertGreaterEqual(title_x - right, 12, name)
            self.assertLessEqual(max(width, height), 24, name)

        self.assertLessEqual(max(visible_sizes) - min(visible_sizes), 1)

        center = (1302, 461)
        samples = []
        alpha = image.getchannel("A")
        for degrees in range(360):
            radians = math.radians(degrees)
            x = round(center[0] + math.cos(radians) * 8)
            y = round(center[1] + math.sin(radians) * 8)
            samples.append(alpha.getpixel((x, y)) > 96)
        runs = sum(samples[index] and not samples[index - 1] for index in range(len(samples)))
        self.assertEqual(runs, 5)

    def test_fan_cleanup_does_not_smear_the_title_into_the_icon_gap(self) -> None:
        image = Image.open(ASSETS.SOURCE).convert("RGBA")
        ASSETS.draw_monitor_icons(image)

        gap = image.crop((1314, 451, 1323, 472)).convert("RGB")
        mean_luminance = sum(ImageStat.Stat(gap).mean) / 3
        self.assertLess(mean_luminance, 20)

    def test_checked_icon_variants_are_transparent_and_use_planar_foregrounds(self) -> None:
        original_output = ASSETS.OUTPUT
        with tempfile.TemporaryDirectory() as directory:
            ASSETS.OUTPUT = Path(directory)
            ASSETS.save_planar_icons()
            for name in ASSETS.QUICK_ICONS:
                for variant, expected_red in (("White", 240), ("Black", 5)):
                    icon = Image.open(ASSETS.OUTPUT / f"Quick{name}{variant}.png").convert("RGBA")
                    self.assertEqual((0, 0, 0, 0), icon.getpixel((0, 0)), f"{name} {variant}")
                    bounds = icon.getchannel("A").getbbox()
                    self.assertIsNotNone(bounds, f"{name} {variant}")
                    assert bounds is not None
                    sample = max(
                        (icon.getpixel((x, y)) for y in range(bounds[1], bounds[3]) for x in range(bounds[0], bounds[2])),
                        key=lambda pixel: pixel[3],
                    )
                    self.assertLessEqual(abs(sample[0] - expected_red), 20, f"{name} {variant}")
        ASSETS.OUTPUT = original_output

    def test_turbo_flash_is_a_transparent_bolt_without_a_crossbar(self) -> None:
        original_output = ASSETS.OUTPUT
        with tempfile.TemporaryDirectory() as directory:
            ASSETS.OUTPUT = Path(directory)
            ASSETS.save_planar_icons()
            icon = Image.open(ASSETS.OUTPUT / "ModeTurboWhite.png").convert("RGBA")
        ASSETS.OUTPUT = original_output

        alpha = icon.getchannel("A")
        bounds = alpha.getbbox()
        self.assertIsNotNone(bounds)
        assert bounds is not None
        widest_row = max(sum(alpha.getpixel((x, y)) > 32 for x in range(bounds[0], bounds[2])) for y in range(bounds[1], bounds[3]))
        self.assertLess(widest_row, 22)
        self.assertEqual((0, 0, 0, 0), icon.getpixel((0, 0)))


if __name__ == "__main__":
    unittest.main()
