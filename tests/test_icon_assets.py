"""Validate checked-in icon formats and consistency without imaging dependencies."""
from pathlib import Path
import struct
import unittest
import xml.etree.ElementTree as ET
import zlib


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/FindCopy.App'
SIZES = {16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80, 96, 128, 256}
WPF = {'w': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}


class IconAssetTests(unittest.TestCase):
    def test_native_frames_cover_windows_sizes_and_use_full_color_alpha(self):
        data = (APP / 'app.ico').read_bytes()
        self.assertEqual(struct.unpack_from('<HHH', data), (0, 1, len(SIZES)))
        found, previous_end = set(), 6 + 16 * len(SIZES)
        for index in range(len(SIZES)):
            width, height, colors, reserved, planes, depth, length, offset = struct.unpack_from('<BBBBHHII', data, 6 + 16 * index)
            width, height = width or 256, height or 256
            self.assertEqual((height, colors, reserved, planes, depth, offset), (width, 0, 0, 1, 32, previous_end))
            self.assertNotIn(width, found)
            found.add(width)
            png = data[offset:offset + length]
            self.assertEqual(png[:8], b'\x89PNG\r\n\x1a\n')
            self.assertEqual(struct.unpack_from('>IIBB', png, 16), (width, height, 8, 6))
            position = 8
            while position < len(png):
                size = struct.unpack_from('>I', png, position)[0]
                body = png[position + 4:position + 8 + size]
                crc = struct.unpack_from('>I', png, position + 8 + size)[0]
                self.assertEqual(zlib.crc32(body), crc, 'Corrupt PNG chunk in native icon')
                position += 12 + size
            self.assertEqual(position, len(png))
            previous_end = offset + length
        self.assertEqual(found, SIZES)
        self.assertEqual(previous_end, len(data))

    def test_wpf_geometry_matches_the_canonical_vector(self):
        svg = ET.parse(APP / 'Assets/AppIcon.svg').getroot()
        xaml = ET.parse(APP / 'Assets/AppIcon.xaml').getroot()
        drawings = xaml.findall('.//w:GeometryDrawing', WPF)
        self.assertEqual(len(drawings), len(svg) + 1)
        self.assertEqual(drawings[0].attrib['Brush'], 'Transparent')
        for shape, drawing in zip(svg, drawings[1:]):
            self.assertEqual(drawing.attrib['Brush'], shape.attrib['fill'])
            geometry = drawing.find('.//w:RectangleGeometry', WPF)
            self.assertEqual(geometry.attrib['Rect'], ','.join(shape.attrib[k] for k in ('x', 'y', 'width', 'height')))
            self.assertEqual(geometry.attrib['RadiusX'], shape.attrib.get('rx', '0'))
            self.assertEqual(geometry.attrib['RadiusY'], shape.attrib.get('rx', '0'))
        self.assertFalse(xaml.findall('.//w:ImageDrawing', WPF), 'Raster images must not replace scalable geometry')

    def test_every_application_window_uses_the_shared_icon(self):
        for name in ('MainWindow', 'SettingsWindow', 'RecoveryWindow'):
            window = ET.parse(APP / (name + '.xaml')).getroot()
            self.assertEqual(window.attrib['Icon'], '{x:Static local:AppIcons.WindowIcon}')
        main = ET.parse(APP / 'MainWindow.xaml').getroot()
        self.assertTrue(all(image.attrib['Source'] == '{StaticResource AppIcon}' for image in main.findall('.//w:Image', WPF)))


if __name__ == '__main__':
    unittest.main()
