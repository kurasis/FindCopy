using System.Windows;
using System.Windows.Controls;

namespace FindCopy.App;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
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

    private void OnDefaults(object sender, RoutedEventArgs e) => Show(new AppSettings());

    private static bool Read(TextBox box, string what, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value > 0) return true;
        MessageBox.Show($"Введите положительное целое число: {what}.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!Read(ThresholdBox, "порог маленького файла", out int th) || !Read(SampleBox, "размер выборки", out int sample) ||
            !Read(BufferBox, "буфер", out int buffer) || !Read(HddBox, "HDD", out int hdd) || !Read(SsdBox, "SSD", out int ssd) ||
            !Read(NvmeBox, "NVMe", out int nvme) || !Read(NetBox, "сетевой диск", out int net) || !Read(UnknownBox, "неизвестный носитель", out int unk))
            return;
        Settings = new AppSettings
        {
            SmallFileThresholdKiB = th, SampleSizeKiB = sample, StreamBufferKiB = buffer,
            ReadersHdd = hdd, ReadersSsd = ssd, ReadersNvme = nvme, NvmeAutotune = AutotuneBox.IsChecked == true,
            ReadersNetwork = net, ReadersUnknown = unk, FastEnumeration = FastEnumBox.IsChecked == true,
        }.Sanitized();
        DialogResult = true;
    }
}
