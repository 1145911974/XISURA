"""Keep the shared filled silhouettes legible on the tray's light surface."""
from pathlib import Path
from PIL import Image

assets = Path(__file__).resolve().parents[1] / "src/Jiaolong.ControlCenter/Assets/QuickMenu"
for source in assets.glob("Quick*.png"):
    if source.stem.endswith("Light"):
        continue
    original = Image.open(source).convert("RGBA")
    tinted = Image.new("RGBA", original.size, (89, 107, 132))
    tinted.putalpha(original.getchannel("A"))
    tinted.save(source.with_stem(source.stem + "Light"))
