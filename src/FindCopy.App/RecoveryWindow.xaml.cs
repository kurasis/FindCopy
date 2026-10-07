using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FindCopy.Core;

namespace FindCopy.App;

public partial class RecoveryWindow : Window
{
    private readonly WindowsRecoveryService _service;
    private bool _busy;
    public int FilesRestored { get; private set; }
    internal Action<string, bool>? NotificationSink { get; set; }
    internal Func<string, bool>? ConfirmationSink { get; set; }

    public RecoveryWindow(string? historyDirectory = null)
    {
        InitializeComponent();
        _service = new WindowsRecoveryService(historyDirectory);
    }

    private sealed record Row(RecoveryEntry Entry)
    {
        public string Date => Entry.DeletedUtc.ToLocalTime().ToString("g");
        public string State => Entry.State == RecoveryState.Restored ? "Восстановлен"
            : Entry.RestorePending ? "Завершить восстановление"
            : Entry.State == RecoveryState.Staged ? System.IO.File.Exists(Entry.StagedPath) ? "Подготовлен" : "Проверить корзину"
            : System.IO.File.Exists(Entry.RecyclePath) ? "В корзине" : "Недоступен";
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        RestoreButton.IsEnabled = !_busy && HistoryGrid.SelectedItem is Row { Entry.State: not RecoveryState.Restored };
        HistoryGrid.IsEnabled = RefreshButton.IsEnabled = CloseButton.IsEnabled = !_busy;
    }

    private void Refresh()
    {
        try
        {
            var entries = _service.Journal.Load(out var errors);
            HistoryGrid.ItemsSource = entries.Select(e => new Row(e)).ToList();
            StatusText.Text = errors.Count > 0 ? $"Не удалось прочитать записей истории: {errors.Count}. Файлы истории сохранены."
                : entries.Count == 0 ? "История удалений пуста." : $"Записей истории: {entries.Count}.";
        }
        catch (Exception ex) { StatusText.Text = "История недоступна: " + ex.Message; }
        UpdateButtons();
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_busy || HistoryGrid.SelectedItem is not Row row || row.Entry.State == RecoveryState.Restored) return;
        string message = "Восстановить файл по исходному пути?\n\n" + row.Entry.OriginalPath +
            "\n\nСуществующий файл не будет перезаписан.";
        bool confirm = ConfirmationSink?.Invoke(message) ?? MessageBox.Show(this, message,
            "Подтвердите восстановление", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirm) return;
        _busy = true; UpdateButtons(); StatusText.Text = "Восстановление…";
        try
        {
            var result = await Task.Run(() => _service.Restore(row.Entry.Id));
            if (result.Restored) FilesRestored++;
            Refresh();
            StatusText.Text = result.Restored ? "Файл восстановлен: " + result.Path : "Не восстановлен: " + result.Reason;
            if (result.Reason != null)
            {
                if (NotificationSink != null) NotificationSink(result.Reason, true);
                else MessageBox.Show(this, result.Reason, "Восстановление", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { StatusText.Text = "Восстановление не завершено: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }
}
