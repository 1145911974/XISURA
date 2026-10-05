import unittest
from pathlib import Path

from PIL import Image

from tools.generate_mode_scene_guides import SCENE_KEYS, build_guide
from tools.reconstruct_mode_plate import boundary_error, reconstruct


ASSET_ROOT = Path("src/Jiaolong.ControlCenter/Assets/ModeScenes")
PRE_FIX_BOUNDARY_ERRORS = {
    "office-orbital-focus": 4.1197,
    "office-constellation-flow": 3.7567,
    "game-facet-arena": 5.4050,
    "game-volumetric-cloud": 6.5288,
    "turbo-digital-fault-planes": 5.1382,
    "turbo-hex-pressure-cells": 4.2103,
    "custom-preset-1-graphite-strata": 10.0240,
    "custom-preset-2-liquid-jade": 11.5577,
    "custom-preset-3-purple-filaments": 6.7999,
}


class ModeSceneAssetTests(unittest.TestCase):
    def test_clean_candidate_reduces_artificial_mask_boundary_and_removes_ui(self):
        reference = Image.new("RGB", (128, 64))
        candidate = Image.new("RGB", reference.size)
        for x in range(reference.width):
            reference.paste((20 + x // 2,) * 3, (x, 0, x + 1, reference.height))
            candidate.paste((100 + x // 2,) * 3, (x, 0, x + 1, candidate.height))
        reference.paste("white", (4, 20, 20, 44))
        mask = Image.new("L", reference.size, 0)
        mask.paste(255, (32, 8, 96, 56))
        hard_composite = Image.composite(reference, candidate, mask)

        output = reconstruct(reference, candidate, mask)

        self.assertEqual(candidate.getpixel((64, 32)), output.getpixel((64, 32)))
        self.assertLess(boundary_error(output, mask), boundary_error(hard_composite, mask))
        self.assertLess(max(output.getpixel((5, 32))), 180)

    def test_reconstruct_uses_clean_candidate_without_reference_ui(self):
        reference = Image.new("RGB", (2, 1), (10, 20, 30))
        candidate = Image.new("RGB", (2, 1), (40, 50, 60))
        mask = Image.new("L", (2, 1), 0)
        mask.putpixel((0, 0), 255)

        output = reconstruct(reference, candidate, mask)

        self.assertEqual((40, 50, 60), output.getpixel((0, 0)))
        self.assertEqual((40, 50, 60), output.getpixel((1, 0)))

    def test_reconstruct_keeps_candidate_canvas_without_scaling_reference(self):
        reference = Image.new("RGB", (1, 1), (10, 20, 30))
        candidate = Image.new("RGB", (2, 1), (40, 50, 60))
        mask = Image.new("L", (2, 1), 255)

        output = reconstruct(reference, candidate, mask)

        self.assertEqual((2, 1), output.size)
        self.assertEqual((40, 50, 60), output.getpixel((0, 0)))
        self.assertEqual((40, 50, 60), output.getpixel((1, 0)))

    def test_reconstruct_uses_mask_as_runtime_canvas_size(self):
        reference = Image.new("RGB", (1, 1), (10, 20, 30))
        candidate = Image.new("RGB", (1, 1), (40, 50, 60))
        mask = Image.new("L", (2, 1), 0)

        output = reconstruct(reference, candidate, mask)

        self.assertEqual((2, 1), output.size)
        self.assertEqual((40, 50, 60), output.getpixel((1, 0)))

    def test_guides_are_rgba_and_non_neutral(self):
        for key in SCENE_KEYS:
            with self.subTest(scene=key):
                guide = build_guide(key)
                self.assertEqual("RGBA", guide.mode)
                self.assertEqual((1672, 941), guide.size)
                self.assertTrue(any(low != high for low, high in guide.getextrema()))

    def test_guides_move_bright_material_without_warping_the_dark_field(self):
        plate = Image.new("RGB", (64, 32), "black")
        plate.paste("white", (20, 8, 44, 24))

        guide = build_guide("office-orbital-focus", plate.size, plate)

        self.assertEqual((128, 128), guide.getpixel((2, 2))[:2])
        self.assertNotEqual((128, 128), guide.getpixel((40, 12))[:2])
        self.assertNotEqual((255, 255), guide.getchannel("A").getextrema())

    def test_unknown_scene_key_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "unknown-scene"):
            build_guide("unknown-scene", (4, 4))

    def test_all_bundles_exist_in_approved_order(self):
        for key in SCENE_KEYS:
            with self.subTest(scene=key):
                for name, mode in (("plate.png", "RGB"), ("guide.png", "RGBA")):
                    with Image.open(ASSET_ROOT / key.replace("-", "_") / name) as image:
                        self.assertEqual((1672, 941), image.size)
                        self.assertEqual(mode, image.mode)

    def test_all_repaired_plates_reduce_the_recorded_boundary_error(self):
        with Image.open("docs/acceptance/mode-scenes/background-visible-mask.png") as mask:
            for key, old_error in PRE_FIX_BOUNDARY_ERRORS.items():
                with self.subTest(scene=key), Image.open(ASSET_ROOT / key.replace("-", "_") / "plate.png") as plate:
                    self.assertLess(boundary_error(plate, mask), old_error)

if __name__ == "__main__":
    unittest.main()
