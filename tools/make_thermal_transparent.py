from pathlib import Path

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tools" / "thermal-assets" / "source"
OUTPUT = ROOT / "src" / "Jiaolong.ControlCenter" / "Assets" / "Monitor"


def remove_uniform_background(source: str, destination: str) -> None:
    image = cv2.imdecode(np.fromfile(SOURCE / source, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise FileNotFoundError(source)

    h, w = image.shape[:2]
    edge = max(8, min(h, w) // 40)
    samples = np.concatenate(
        [
            image[:edge, :edge].reshape(-1, 3),
            image[:edge, -edge:].reshape(-1, 3),
            image[-edge:, :edge].reshape(-1, 3),
            image[-edge:, -edge:].reshape(-1, 3),
        ]
    ).astype(np.float32)
    background = np.median(samples, axis=0)
    distance = np.linalg.norm(image.astype(np.float32) - background, axis=2)
    alpha = np.clip((distance - 8.0) / 28.0, 0.0, 1.0)
    alpha = cv2.GaussianBlur(alpha, (0, 0), 0.8)
    alpha = np.where(alpha < 0.025, 0.0, alpha)

    rgba = cv2.cvtColor(image, cv2.COLOR_BGR2BGRA)
    rgba[:, :, 3] = np.rint(alpha * 255).astype(np.uint8)
    ok, encoded = cv2.imencode(".png", rgba)
    if not ok:
        raise OSError(destination)
    encoded.tofile(OUTPUT / destination)


remove_uniform_background("CpuThermalVisual.png", "CpuThermalVisualAlpha.png")
remove_uniform_background("GpuThermalVisual.png", "GpuThermalVisualAlpha.png")
