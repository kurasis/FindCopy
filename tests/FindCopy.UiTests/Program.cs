using FindCopy.App;
using FindCopy.Core;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _passed, _failed;
    private static string _artifacts = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _artifacts = Path.GetFullPath(args.Length == 0 ? "ui-test-artifacts" : args[0]);
        Directory.CreateDirectory(_artifacts);
        Application.ResourceAssembly = typeof(App).Assembly;
        var app = new App(); app.InitializeComponent();
        app.StartupUri = null; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Run(); }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL UI runner: " + ex); }
            finally { Console.WriteLine($"{_passed} passed, {_failed} failed"); app.Shutdown(_failed == 0 ? 0 : 1); }
        }, DispatcherPriority.ApplicationIdle);
        return app.Run();
    }

    private static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "findcopy-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new ProbeWindow(); Application.Current.MainWindow = window; window.Show();
        try
        {
            string pair = Path.Combine(root, "pair"); Directory.CreateDirectory(pair);
            foreach (string name in new[] { "a", "b" })
            { using var stream = new FileStream(Path.Combine(pair, name), FileMode.CreateNew, FileAccess.Write); stream.SetLength(64L << 20); }
            File.WriteAllText(Path.Combine(pair, "empty-a"), ""); File.WriteAllText(Path.Combine(pair, "empty-b"), "");
            await Test("UI1 real search binds duplicate and empty-file results", async () =>
            {
                await Search(window, pair);
                Require(Result(window).Groups.Count == 1 && Control<ListBox>(window, "ZeroList").Items.Count == 2, "result bindings");
                Require(Control<Button>(window, "SearchButton").IsEnabled && !Control<Button>(window, "CancelButton").IsEnabled, "terminal controls");
                Require(Control<TextBlock>(window, "SummaryText").Text.Contains("Найдено групп"), "Russian summary");
                Screenshot(window, "duplicates.png");
            });
            await Test("UI2 selection controls retain one copy and clear selection", () =>
            {
                Click(window, "SelectExtrasButton");
                Require(Groups(window).Single().SelectedCount == 1 && Control<Button>(window, "DeleteButton").IsEnabled, "extra selection");
                Click(window, "ClearSelectionButton");
                Require(Groups(window).Single().SelectedCount == 0 && !Control<Button>(window, "DeleteButton").IsEnabled, "selection reset");
                return Task.CompletedTask;
            });
            await Test("UI3 checking every copy is rejected by the bound group model", () =>
            {
                int refused = 0;
                var model = new GroupVM(Result(window).Groups.Single(), true, refused: () => refused++);
                foreach (var file in model.Files) file.IsChecked = true;
                Require(model.SelectedCount == model.Files.Count - 1 && refused == 1, "keeper selection guard");
                return Task.CompletedTask;
            });
            await Test("UI4 empty-file visibility follows the checkbox", () =>
            {
                Control<CheckBox>(window, "ShowZeroBox").IsChecked = true;
                Require(Control<TabItem>(window, "ZeroTab").Visibility == Visibility.Visible, "empty tab not shown");
                Control<CheckBox>(window, "ShowZeroBox").IsChecked = false;
                Require(Control<TabItem>(window, "ZeroTab").Visibility == Visibility.Collapsed, "empty tab not hidden");
                return Task.CompletedTask;
            });
            await Test("UI5 staged recovery warnings remain visible after a removed source", () =>
            {
                string path = Result(window).Groups.Single().Files[1].Path;
                string recovery = Path.Combine(root, "private-recovery", "b");
                Invoke(window, "ApplyDeletion", new List<DeleteOutcome> { new(path, true, "Проверенный файл сохранён в " + recovery, 0) });
                var issues = (IEnumerable<IssueVM>)Control<DataGrid>(window, "IssuesGrid").ItemsSource;
                Require(issues.Any(x => x.Message?.Contains(recovery) == true) && window.Warning &&
                    window.Notification.Contains("восстановления") && Control<TextBlock>(window, "SummaryText").Text.Contains("замечаниями"), "recovery warning hidden");
                Screenshot(window, "recovery-warning.png");
                return Task.CompletedTask;
            });
            await Test("UI6 a failed deletion preserves group membership and selection", async () =>
            {
                await Search(window, pair); Click(window, "SelectExtrasButton");
                string path = Groups(window).Single().Files.Single(f => f.IsChecked).Path;
                Invoke(window, "ApplyDeletion", new List<DeleteOutcome> { new(path, false, "Sharing violation", 0) });
                Require(Groups(window).Single().SelectedCount == 1 && Result(window).Groups.Single().Files.Count == 2 && window.Warning, "failed deletion projection");
            });
            await Test("UI7 native policy exclusions qualify no-duplicate wording", async () =>
            {
                string d = Path.Combine(root, "excluded"); Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "a"), "policy pair"); File.WriteAllText(Path.Combine(d, "b"), "policy pair");
                File.SetAttributes(Path.Combine(d, "b"), FileAttributes.System);
                await Search(window, d);
                Require(Result(window).HasUncheckedFiles && Control<TextBlock>(window, "SummaryText").Text.Contains("успешно проверенных"), "unqualified no-duplicate wording");
                Screenshot(window, "policy-exclusion.png");
            });
            await Test("UI8 cancelling a real scan restores controls and hides partial results", async () =>
            {
                Control<TextBox>(window, "FolderBox").Text = pair;
                Click(window, "SearchButton"); Click(window, "CancelButton");
                await Wait(() => Field<CancellationTokenSource?>(window, "_cts") == null);
                Require(Control<Button>(window, "SearchButton").IsEnabled && !Control<Button>(window, "CancelButton").IsEnabled &&
                    Control<TextBlock>(window, "SummaryText").Text.Contains("остановлен пользователем"), "cancel state");
            });
            await Test("UI9 sanitized performance settings form valid scanner options", () =>
            {
                var settings = new AppSettings { SmallFileThresholdKiB = -1, SampleSizeKiB = int.MaxValue,
                    StreamBufferKiB = int.MaxValue, ReadersHdd = -1, ReadersSsd = 0, ReadersNvme = 1000, ReadersNetwork = 0 };
                settings.Sanitized().ToTuning().Validate();
                var dialog = new SettingsWindow(settings); dialog.Show(); dialog.Close();
                return Task.CompletedTask;
            });
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private static async Task Search(ProbeWindow window, string root)
    {
        Control<TextBox>(window, "FolderBox").Text = root;
        Control<CheckBox>(window, "CacheBox").IsChecked = false;
        Click(window, "SearchButton");
        await Wait(() => Field<CancellationTokenSource?>(window, "_cts") == null);
        Require(Field<ScanResult?>(window, "_result") != null, "search produced no result");
    }
    private static async Task Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        { if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("UI operation did not finish"); await Task.Delay(20); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Test(string name, Func<Task> body)
    {
        try { await body(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static T Control<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static ScanResult Result(MainWindow window) => Field<ScanResult>(window, "_result");
    private static List<GroupVM> Groups(MainWindow window) => Field<List<GroupVM>>(window, "_groups");
    private static void Invoke(MainWindow window, string method, object argument) => typeof(MainWindow).GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new[] { argument });
    private static void Screenshot(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_artifacts, name)); encoder.Save(output);
    }
    private sealed class ProbeWindow : MainWindow
    {
        public string Notification = ""; public bool Warning;
        protected override void ShowNotification(string message, string title, bool warning) { Notification = message; Warning = warning; }
    }
}
