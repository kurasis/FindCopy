using FindCopy.App;
using FindCopy.Core;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _passed, _failed;
    private static string _artifacts = "";
    private static string _notification = "";
    private static bool _warning;
    private static string? _published;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"RUNTIME: OS={RuntimeInformation.OSDescription}; OS architecture={RuntimeInformation.OSArchitecture}; process={RuntimeInformation.ProcessArchitecture}");
        string? expectedArchitecture = Environment.GetEnvironmentVariable("FINDCOPY_EXPECTED_ARCH");
        if (!string.IsNullOrEmpty(expectedArchitecture) && expectedArchitecture != RuntimeInformation.ProcessArchitecture.ToString())
            throw new InvalidOperationException("Unexpected UI process architecture: " + RuntimeInformation.ProcessArchitecture);
        if (args.Contains("--require-standard-user"))
        {
            var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Acceptance requires a non-administrator token");
            Console.WriteLine("STANDARD_USER: " + identity.Name + ", administrator=false");
        }
        int publishedIndex = Array.IndexOf(args, "--published-exe");
        if (publishedIndex >= 0) _published = Path.GetFullPath(args[publishedIndex + 1]);
        _artifacts = Path.GetFullPath(args.Length == 0 ? "ui-test-artifacts" : args[0]);
        Directory.CreateDirectory(_artifacts);
        if (args.Contains("--require-standard-user"))
        {
            // Credentialed launches inherit the administrator's TEMP; use this account's writable fixture area.
            string temporary = Path.Combine(_artifacts, "temp"); Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
        }
        var app = new App { CreateStartupWindow = false }; app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
        var window = new MainWindow { NotificationSink = (message, _, warning) => { _notification = message; _warning = warning; } };
        Application.Current.MainWindow = window; window.Show();
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
                Require(issues.Any(x => x.Message?.Contains(recovery) == true) && _warning &&
                    _notification.Contains("восстановления") && Control<TextBlock>(window, "SummaryText").Text.Contains("замечаниями"), "recovery warning hidden");
                Screenshot(window, "recovery-warning.png");
                return Task.CompletedTask;
            });
            await Test("UI6 a failed deletion preserves group membership and selection", async () =>
            {
                await Search(window, pair); Click(window, "SelectExtrasButton");
                string path = Groups(window).Single().Files.Single(f => f.IsChecked).Path;
                Invoke(window, "ApplyDeletion", new List<DeleteOutcome> { new(path, false, "Sharing violation", 0) });
                Require(Groups(window).Single().SelectedCount == 1 && Result(window).Groups.Single().Files.Count == 2 && _warning, "failed deletion projection");
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
            await Test("UI10 declining native cloud consent keeps online-only reads disabled", () =>
            {
                string dialogText = ""; bool dismissed = false;
                var dismiss = new Thread(() =>
                {
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        IntPtr dialog = FindWindowW("#32770", "Облачные файлы");
                        if (dialog != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(dialog, out uint process);
                            if (process == Environment.ProcessId)
                            {
                                EnumChildWindows(dialog, (child, _) =>
                                {
                                    var text = new StringBuilder(4096); GetWindowTextW(child, text, text.Capacity);
                                    dialogText += text + " "; return true;
                                }, IntPtr.Zero);
                                SendMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(7) /* IDNO */, IntPtr.Zero);
                                dismissed = true; return;
                            }
                        }
                        Thread.Sleep(20);
                    }
                }) { IsBackground = true };
                dismiss.Start(); Control<CheckBox>(window, "CloudBox").IsChecked = true; dismiss.Join();
                Require(dismissed && Control<CheckBox>(window, "CloudBox").IsChecked == false &&
                    dialogText.Contains("скачаны") && dialogText.Contains("место на диске"), "cloud warning/decline flow");
                return Task.CompletedTask;
            });
            await Test("UI11 deletion controls remain visible at minimum window width", async () =>
            {
                window.Width = window.MinWidth;
                await Search(window, pair); Click(window, "SelectExtrasButton"); window.UpdateLayout();
                foreach (string name in new[] { "KeepRuleBox", "SelectExtrasButton", "ClearSelectionButton", "PermanentBox", "DeleteButton" })
                {
                    var control = Control<FrameworkElement>(window, name);
                    var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                    Require(bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                        name + " is clipped at minimum width");
                }
                Screenshot(window, "minimum-width.png");
            });
            await Test("UI13 recovery history restores an actual recycled file and disables repeats", async () =>
            {
                string directory = Path.Combine(root, "ui-recovery"); Directory.CreateDirectory(directory);
                string original = Path.Combine(directory, "восстановить.txt"); File.WriteAllText(original, "UI restore bytes");
                string history = Path.Combine(root, "ui-history");
                var backend = new WindowsDeletionBackend(history); string staged;
                using (var h = backend.OpenCandidate(original))
                    Require(backend.StageForRecycle(h, original, out staged, out var error), "UI stage: " + error);
                Require(backend.MoveToRecycleBin(staged, out var recycleError) && recycleError == null, "UI recycle: " + recycleError);
                var recovery = new RecoveryWindow(history) { Owner = window, ConfirmationSink = _ => true,
                    NotificationSink = (message, _) => throw new Exception(message) };
                recovery.Show();
                try
                {
                    var grid = Control<DataGrid>(recovery, "HistoryGrid");
                    Require(grid.Items.Count == 1, "history binding"); grid.SelectedIndex = 0;
                    Click(recovery, "RestoreButton");
                    await Wait(() => recovery.FilesRestored == 1 && Control<Button>(recovery, "RefreshButton").IsEnabled);
                    Require(File.ReadAllText(original) == "UI restore bytes" &&
                        Control<TextBlock>(recovery, "StatusText").Text.Contains("Файл восстановлен"), "UI restore result");
                    grid.SelectedIndex = 0;
                    Require(!Control<Button>(recovery, "RestoreButton").IsEnabled, "repeat restore enabled");
                    Screenshot(recovery, "restored-history.png");
                }
                finally { recovery.Close(); }
            });
            await Test("UI14 pending restore cleanup remains available and completes through the history window", async () =>
            {
                string directory = Path.Combine(root, "ui-pending-recovery"); Directory.CreateDirectory(directory);
                string original = Path.Combine(directory, "returned.txt"); File.WriteAllText(original, "UI pending bytes");
                string history = Path.Combine(root, "ui-pending-history");
                var backend = new WindowsDeletionBackend(history);
                using (var h = backend.OpenCandidate(original))
                    Require(backend.StageForRecycle(h, original, out _, out var error), "UI stage: " + error);
                var journal = new RecoveryJournal(history); var entry = journal.Load(out _).Single();
                File.WriteAllText(Path.Combine(history, entry.Id.ToString("N") + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(entry with { RestorePending = true },
                        new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
                File.Move(entry.StagedPath, original);
                var recovery = new RecoveryWindow(history) { Owner = window, ConfirmationSink = _ => true,
                    NotificationSink = (message, _) => throw new Exception(message) };
                recovery.Show();
                try
                {
                    var grid = Control<DataGrid>(recovery, "HistoryGrid");
                    Require(grid.Items.Count == 1 && (string)grid.Items[0].GetType().GetProperty("State")!.GetValue(grid.Items[0])! ==
                        "Завершить восстановление", "pending cleanup status missing");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); recovery.UpdateLayout();
                    Require(grid.Columns[1].ActualWidth >= 250 && grid.Columns[2].ActualWidth >= 200,
                        "original path or pending status column is too narrow");
                    recovery.Width = recovery.MinWidth;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); recovery.UpdateLayout();
                    Require(grid.Columns[1].ActualWidth >= 250 && grid.Columns[2].ActualWidth >= 200,
                        "recovery columns collapse at minimum window width");
                    grid.SelectedIndex = 0;
                    Require(Control<Button>(recovery, "RestoreButton").IsEnabled, "pending cleanup retry disabled");
                    Screenshot(recovery, "pending-history.png"); Click(recovery, "RestoreButton");
                    await Wait(() => recovery.FilesRestored == 1 && Control<Button>(recovery, "RefreshButton").IsEnabled);
                    Require(File.ReadAllText(original) == "UI pending bytes" && journal.Load(out _).Single() is
                        { State: RecoveryState.Restored, RestorePending: false }, "pending cleanup did not complete");
                    grid.SelectedIndex = 0;
                    Require(!Control<Button>(recovery, "RestoreButton").IsEnabled, "completed cleanup retry enabled");
                }
                finally { recovery.Close(); }
            });
            if (_published != null)
                await Test("UI12 published executable scans, recycles, and restores a selected copy", async () =>
                {
                    await Task.Run(() => PublishedAppAcceptance.Run(_published, Path.Combine(root, "published-pair")));
                });
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private static async Task Search(MainWindow window, string root)
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
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr value, IntPtr parameter);
}
