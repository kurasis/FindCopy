using System.Windows.Media.Imaging;

namespace FindCopy.App;

internal static class AppIcons
{
    // Keep the decoder's frame set: WPF selects the matching native size for
    // title bars/taskbar icons instead of enlarging the first 16-pixel frame.
    public static BitmapFrame WindowIcon { get; } = LoadWindowIcon();

    private static BitmapFrame LoadWindowIcon()
    {
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/FindCopy;component/app.ico"),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }
}
