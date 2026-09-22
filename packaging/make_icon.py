"""Build the native application's static robot icon without loading a UI shell."""

import argparse
from pathlib import Path


ICON_SIZES = [(16, 16), (32, 32), (64, 64)]


def create_icon_image():
    """Return the original 64-pixel robot-in-donut image."""
    from PIL import Image, ImageDraw

    image = Image.new('RGBA', (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.ellipse((4, 4, 60, 60), outline='#5f9de0', width=6)
    draw.rounded_rectangle((19, 23, 45, 43), radius=5, fill='#d7e4f5', outline='#4777ad', width=2)
    draw.line((32, 16, 32, 23), fill='#4777ad', width=2)
    draw.ellipse((29, 13, 35, 19), fill='#5f9de0')
    draw.ellipse((24, 29, 28, 33), fill='#25374a')
    draw.ellipse((36, 29, 40, 33), fill='#25374a')
    draw.line((26, 38, 38, 38), fill='#4777ad', width=2)
    return image


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', nargs='?', type=Path, default=Path('build/robot-ring.ico'))
    target = parser.parse_args(argv).output
    target.parent.mkdir(parents=True, exist_ok=True)
    with create_icon_image() as image:
        image.save(target, format='ICO', sizes=ICON_SIZES)


if __name__ == '__main__':
    main()
