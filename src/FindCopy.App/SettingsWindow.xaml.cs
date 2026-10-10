using System.Windows;
using System.Windows.Controls;

namespace FindCopy.App;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    internal Func<AppSettings, string?>? SaveSettings { get; set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        Settings = current;
        Show(current);
    }

    private void Show(AppSettings s)
    {
        ThresholdBox.Text = s.SmallFileThresholdKiB.ToString();
        SampleBox.Text = s.SampleSizeKiB.ToString();
        BufferBox.Text = s.StreamBufferKiB.ToString();
        HddBox.Text = s.ReadersHdd.ToString();
        SsdBox.Text = s.ReadersSsd.ToString();
        NvmeBox.Text = s.ReadersNvme.ToString();
        AutotuneBox.IsChecked = s.NvmeAutotune;
        NetBox.Text = s.ReadersNetwork.ToString();
        UnknownBox.Text = s.ReadersUnknown.ToString();
        FastEnumBox.IsChecked = s.FastEnumeration;
    }

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        ClearErrors();
        Show(new AppSettings());
    }

    private static bool Read(TextBox box, TextBlock error, int minimum, int maximum, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value >= minimum && value <= maximum) return true;
        AccessibilityStatus.Set(error, $"Введите целое число от {minimum} до {maximum}.");
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryApplySettings()) DialogResult = true;
    }

    private void ClearErrors()
    {
        foreach (var error in new[] { ThresholdError, SampleError, BufferError, HddError, SsdError, NvmeError, NetError, UnknownError, SaveError })
            error.Text = "";
    }

    internal bool TryApplySettings()
    {
        ClearErrors();
        if (!Read(ThresholdBox, ThresholdError, 64, 65536, out int th) || !Read(SampleBox, SampleError, 4, 1024, out int sample) ||
            !Read(BufferBox, BufferError, 64, 16384, out int buffer) || !Read(HddBox, HddError, 1, 16, out int hdd) ||
            !Read(SsdBox, SsdError, 1, 16, out int ssd) || !Read(NvmeBox, NvmeError, 1, 16, out int nvme) ||
            !Read(NetBox, NetError, 1, 16, out int net) || !Read(UnknownBox, UnknownError, 1, 16, out int unk)) return false;
        if (sample > th)
        {
            AccessibilityStatus.Set(SampleError, "Выборка не должна превышать порог маленького файла.");
            SampleBox.BringIntoView();
            SampleBox.Focus();
            SampleBox.SelectAll();
            return false;
        }
        var candidate = new AppSettings
        {
            SmallFileThresholdKiB = th, SampleSizeKiB = sample, StreamBufferKiB = buffer,
            ReadersHdd = hdd, ReadersSsd = ssd, ReadersNvme = nvme, NvmeAutotune = AutotuneBox.IsChecked == true,
            ReadersNetwork = net, ReadersUnknown = unk, FastEnumeration = FastEnumBox.IsChecked == true,
        };
        string? saveError = SaveSettings?.Invoke(candidate);
        if (saveError != null)
        {
            AccessibilityStatus.Set(SaveError, saveError);
            return false;
        }
        Settings = candidate;
        return true;
    }
}
