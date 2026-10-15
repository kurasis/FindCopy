"""Compile the canonical rectangle-only SVG into WPF geometry and native ICO sizes.

Requires ImageMagick 7 (`magick`) only when rebuilding the checked-in assets.
Application builds and tests do not depend on ImageMagick or Python imaging packages.
"""
from pathlib import Path
import math
import struct
import subprocess
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src/FindCopy.App"
SIZES = (16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80, 96, 128, 256)


def main():
    source = ET.parse(APP / "Assets/AppIcon.svg").getroot()
    assert source.attrib["viewBox"] == "0 0 256 256"
    shapes = list(source)
    assert shapes and all(shape.tag == "{http://www.w3.org/2000/svg}rect" for shape in shapes)
    drawing = [
        '<!-- Generated from AppIcon.svg by tools/build-icon-assets.py. -->',
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        '  <DrawingImage x:Key="AppIcon">',
        '    <DrawingImage.Drawing>',
        '      <DrawingGroup>',
        '        <GeometryDrawing Brush="Transparent">',
        '          <GeometryDrawing.Geometry><RectangleGeometry Rect="0,0,256,256"/></GeometryDrawing.Geometry>',
        '        </GeometryDrawing>',
    ]
    for shape in shapes:
        a = shape.attrib
        rect = ','.join(a[key] for key in ('x', 'y', 'width', 'height'))
        radius = a.get('rx', '0')
        drawing.extend([
            f'        <GeometryDrawing Brush="{a["fill"]}">',
            f'          <GeometryDrawing.Geometry><RectangleGeometry Rect="{rect}" RadiusX="{radius}" RadiusY="{radius}"/></GeometryDrawing.Geometry>',
            '        </GeometryDrawing>',
        ])
    drawing.extend(['      </DrawingGroup>', '    </DrawingImage.Drawing>', '  </DrawingImage>', '</ResourceDictionary>', ''])
    (APP / 'Assets/AppIcon.xaml').write_text('\n'.join(drawing), encoding='utf-8')

    frames = []
    for size in SIZES:
        # Draw each size from geometry, never from another ICO frame. Pixel-align
        # small details so the three document lines remain legible at 16/20 px.
        commands = ['scale 8,8', 'stroke none']
        for shape in shapes:
            a = {'fill': shape.attrib['fill']}
            for key in ('x', 'y', 'width', 'height', 'rx'):
                value = float(shape.attrib.get(key, '0')) * size / 256
                if size < 64:
                    value = math.floor(value + 0.5)
                    if key in ('width', 'height'): value = max(1, value)
                a[key] = str(value)
            x, y, width, height, radius = (float(a[key]) for key in ('x', 'y', 'width', 'height', 'rx'))
            primitive = (f'roundrectangle {x},{y} {x + width},{y + height} {radius},{radius}' if radius > 0
                         else f'rectangle {x},{y} {x + width},{y + height}')
            commands.extend([f'fill "{a["fill"]}"', primitive])
        png = subprocess.check_output([
            'magick', '-size', f'{size * 8}x{size * 8}', 'canvas:none', '-draw', '\n'.join(commands),
            '-filter', 'Box', '-resize', f'{size}x{size}',
            '-strip', 'PNG32:-',
        ])
        assert png[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack_from('>II', png, 16) == (size, size)
        frames.append((size, png))
    offset = 6 + 16 * len(frames)
    directory = bytearray(struct.pack('<HHH', 0, 1, len(frames)))
    for size, png in frames:
        directory.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(png), offset))
        offset += len(png)
    (APP / 'app.ico').write_bytes(directory + b''.join(png for _, png in frames))
    print('Generated WPF vector resource and ICO frames:', ', '.join(map(str, SIZES)))


if __name__ == '__main__':
    main()
