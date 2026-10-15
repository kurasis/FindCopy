# Application icons

`AppIcon.svg` is the canonical flat vector artwork. Preserve its document-copy
identity when making quality corrections. Runtime WPF does not load SVG: the
generated `AppIcon.xaml` contains only vector geometry and supplies the header
and all three windows' `Icon` properties. It has a transparent 256-unit viewbox
so its padding remains consistent at every render scale.

The native `../app.ico` is embedded in the EXE for Explorer and shortcuts. It
contains 14 independent 32-bit RGBA frames: 16, 20, 24, 28, 32, 40, 48, 56, 64,
72, 80, 96, 128, and 256 pixels. Each frame is rendered from the source geometry;
small frames align details to physical pixels to preserve the three document
lines. No frame is enlarged from a smaller bitmap.

To regenerate the checked-in XAML and ICO, use Python 3 and ImageMagick 7:

```sh
python3 tools/build-icon-assets.py
python3 tests/test_icon_assets.py
```

ImageMagick is only an asset-maintenance tool. Normal builds, application
startup, and CI validation have no new imaging dependency. The generator uses
ImageMagick's own drawing primitives, so an external SVG delegate is unnecessary.

Windows regressions validate the resource frames, the three visible document
lines, rendered vector exports from 100% through 400%, native window HICONs,
and the icon group embedded in the actual published executable. Render exports
do not substitute for physical monitor/DPI-transition testing.
