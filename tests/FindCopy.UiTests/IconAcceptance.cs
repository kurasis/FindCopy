using FindCopy.App;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class IconAcceptance
{
    private static readonly int[] Sizes = [16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80, 96, 128, 256];

    internal static void Run(MainWindow window, string output)
    {
        var image = (Image)window.FindName("BrandIcon");
        Require(image.Source is DrawingImage, "Header must use vector geometry, not the first small ICO frame");
        var logo = (DrawingImage)image.Source;
        Require(logo.Drawing is DrawingGroup group && group.Children.Count == 7 &&
            group.Children.All(child => child is GeometryDrawing), "Brand icon contains a raster drawing");
        Require(logo.Drawing.Bounds == new Rect(0, 0, 256, 256), "Vector viewbox lost transparent padding");
        foreach (int percent in new[] { 100, 125, 150, 175, 200, 250, 300, 400 })
        {
            double dpi = 96 * percent / 100.0;
            int pixels = 32 * percent / 100;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(logo, new Rect(0, 0, 32, 32));
            var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] data = new byte[pixels * pixels * 4]; bitmap.CopyPixels(data, pixels * 4, 0);
            Require(data[3] == 0 && data.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 255),
                "Scaled icon lost alpha transparency or opaque document geometry");
            CheckDocumentLines(data, pixels);
            Save(bitmap, Path.Combine(output, $"icon-vector-{percent}.png"));
        }
        var uri = new Uri("pack://application:,,,/FindCopy;component/app.ico");
        var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Require(decoder.Frames.Select(f => f.PixelWidth).Order().SequenceEqual(Sizes), "Native ICO frame coverage is incomplete");
        foreach (var frame in decoder.Frames)
        {
            Require(frame.PixelWidth == frame.PixelHeight, "Non-square ICO frame");
            var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            byte[] data = new byte[rgba.PixelWidth * rgba.PixelHeight * 4]; rgba.CopyPixels(data, rgba.PixelWidth * 4, 0);
            Require(data[3] == 0 && data.Where((_, i) => i % 4 == 3).Any(a => a == 255), "ICO frame alpha is invalid");
            CheckDocumentLines(data, rgba.PixelWidth);
            Save(frame, Path.Combine(output, $"icon-native-{frame.PixelWidth}.png"));
        }
        CheckWindow(window);
        Console.WriteLine("ICON_RENDER: vector exports at 100/125/150/175/200/250/300/400%; actual desktop DPI unchanged.");
    }

    internal static void CheckWindow(Window window)
    {
        Require(window.Icon is DrawingImage, "Window uses a generic or raster application icon: " + window.Title);
        var hwnd = new WindowInteropHelper(window).Handle;
        foreach (int kind in new[] { 0, 1 }) // WM_GETICON: ICON_SMALL and ICON_BIG.
        {
            IntPtr icon = SendMessageW(hwnd, 0x007f, new IntPtr(kind), IntPtr.Zero);
            Require(icon != IntPtr.Zero, "Window native icon is missing");
            Require(GetIconInfo(icon, out var info), "Window native icon cannot be inspected");
            try
            {
                Require(GetObjectW(info.Color, Marshal.SizeOf<NativeBitmap>(), out var bitmap) != 0 &&
                    bitmap.Width >= (kind == 0 ? 16 : 32) && bitmap.Width == bitmap.Height && bitmap.BitsPixel == 32,
                    "Window native icon is undersized or lacks full-color alpha");
                Console.WriteLine($"WINDOW_ICON: {window.GetType().Name}; kind={kind}; {bitmap.Width}x{bitmap.Height}; {bitmap.BitsPixel}bpp");
            }
            finally
            {
                // GetIconInfo owns these bitmap copies; the window retains its HICON.
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            }
        }
    }

    internal static void CheckPublishedExecutable(string path)
    {
        // Read PE resources without executing a DLL entry point or loading code.
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000002); // LOAD_LIBRARY_AS_DATAFILE
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var names = new List<IntPtr>();
            EnumResourceName callback = (_, _, name, _) => { names.Add(name); return true; };
            Require(EnumResourceNamesW(module, new IntPtr(14), callback, IntPtr.Zero) && names.Count == 1,
                "Published EXE must expose one unambiguous application icon group");
            IntPtr resource = FindResourceW(module, names[0], new IntPtr(14));
            uint length = SizeofResource(module, resource);
            Require(length == 6 + 14 * Sizes.Length, "Published EXE icon group is incomplete");
            byte[] data = new byte[length];
            Marshal.Copy(LockResource(LoadResource(module, resource)), data, 0, data.Length);
            Require(BitConverter.ToUInt16(data, 2) == 1 && BitConverter.ToUInt16(data, 4) == Sizes.Length,
                "Published EXE icon resource type or count is wrong");
            var found = new List<int>();
            for (int offset = 6; offset < data.Length; offset += 14)
            {
                int width = data[offset] == 0 ? 256 : data[offset];
                int height = data[offset + 1] == 0 ? 256 : data[offset + 1];
                Require(width == height && BitConverter.ToUInt16(data, offset + 6) == 32, "Invalid published icon depth/dimensions");
                found.Add(width);
            }
            Require(found.Order().SequenceEqual(Sizes), "Published EXE discarded native icon sizes");
            Console.WriteLine("PUBLISHED_ICON: 14 native 32-bit sizes, 16 through 256 pixels");
        }
        finally { FreeLibrary(module); }
    }

    private static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    private static void CheckDocumentLines(byte[] pixels, int size)
    {
        int segments = 0, column = (int)(size * 0.6); bool previous = false;
        for (int row = 0; row < size; row++)
        {
            int offset = (row * size + column) * 4;
            bool white = pixels[offset] > 240 && pixels[offset + 1] > 240 && pixels[offset + 2] > 240 && pixels[offset + 3] > 240;
            if (white && !previous) segments++;
            previous = white;
        }
        Require(segments == 3, $"The three document lines are not legible at {size}px: {segments} segments");
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool Icon;
        public uint HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }
    private delegate bool EnumResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIconInfo(IntPtr icon, out NativeIconInfo info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetObjectW(IntPtr bitmap, int size, out NativeBitmap data);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResourceName callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
}
