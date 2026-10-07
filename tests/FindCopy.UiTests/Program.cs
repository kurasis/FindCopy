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
using System.Windows.Automation;
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
        var actualDpi = VisualTreeHelper.GetDpi(window);
        Console.WriteLine($"DESKTOP_DPI: x={actualDpi.PixelsPerInchX}; y={actualDpi.PixelsPerInchY}");
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
                    while (deadline.Elapsed < TimeSpan.FromSeconds(12))
                    {
                        IntPtr dialog = FindWindowW("#32770", "Облачные файлы");
                        if (dialog != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(dialog, out uint process);
                            if (process == Environment.ProcessId)
                            {
                                string captured = "";
                                EnumChildWindows(dialog, (child, _) =>
                                {
                                    var text = new StringBuilder(4096); GetWindowTextW(child, text, text.Capacity);
                                    captured += text + " "; return true;
                                }, IntPtr.Zero);
                                // A native dialog's HWND can appear before its warning text is initialized.
                                if (captured.Contains("скачаны") && captured.Contains("место на диске"))
                                {
                                    dialogText = captured;
                                    SendMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(7) /* IDNO */, IntPtr.Zero);
                                    dismissed = true; return;
                                }
                                // Unblock the modal call on failure so the assertion can report it.
                                if (deadline.Elapsed >= TimeSpan.FromSeconds(10))
                                {
                                    dialogText = captured;
                                    SendMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(7) /* IDNO */, IntPtr.Zero);
                                    return;
                                }
                            }
                        }
                        Thread.Sleep(20);
                    }
                }) { IsBackground = true };
                dismiss.Start(); Control<CheckBox>(window, "CloudBox").IsChecked = true; dismiss.Join();
                Require(dismissed && Control<CheckBox>(window, "CloudBox").IsChecked == false &&
                    dialogText.Contains("скачаны") && dialogText.Contains("место на диске"),
                    $"cloud warning/decline flow; dismissed={dismissed}; checked={Control<CheckBox>(window, "CloudBox").IsChecked}; text={dialogText}");
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
            await Test("UI15 minimum-width actions remain visible in 100/125/150/200 percent render exports", async () =>
            {
                window.Width = window.MinWidth;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                foreach (string name in new[] { "RecoveryButton", "ExportButton", "ExpandButton" })
                {
                    var button = Control<Button>(window, name);
                    var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Require(bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                        name + " is clipped before scaled rendering");
                }
                foreach (int percent in new[] { 100, 125, 150, 200 })
                    Screenshot(window, $"render-{percent}.png", 96 * percent / 100.0);
                Console.WriteLine("RENDER_SCALE: exported 100/125/150/200 percent; does not change the desktop's actual DPI");
            });
            await Test("UI16 two thousand result groups support bulk selection and clearing", async () =>
            {
                const int groups = 2000;
                string directory = Path.Combine(root, "many-results"); Directory.CreateDirectory(directory);
                byte[] bytes = new byte[4096];
                for (int i = 0; i < groups; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, i);
                    string d = Path.Combine(directory, "group-" + i); Directory.CreateDirectory(d);
                    foreach (string name in new[] { "копия;a", "copy;b", "copy;c" }) File.WriteAllBytes(Path.Combine(d, name), bytes);
                }
                await Search(window, directory);
                Require(Groups(window).Count == groups && Control<TreeView>(window, "ResultTree").Items.Count == groups,
                    "large result list lost groups");
                var watch = Stopwatch.StartNew(); Click(window, "SelectExtrasButton");
                Require(Groups(window).All(g => g.SelectedCount == 2) && Control<Button>(window, "DeleteButton").IsEnabled,
                    "bulk selection lost keepers or omitted groups");
                Invoke(window, "ApplyDeletion", new List<DeleteOutcome> { new(Groups(window)[0].Files.First(f => f.IsChecked).Path,
                    false, "Fixture denial; original files retained", 0) });
                Require(Groups(window).Count == groups && Groups(window).All(g => g.SelectedCount == 2),
                    "large-list deletion refresh lost checked files or groups");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Click(window, "ClearSelectionButton");
                Require(Groups(window).All(g => g.SelectedCount == 0) && !Control<Button>(window, "DeleteButton").IsEnabled,
                    "bulk clear left hidden selections");
                Console.WriteLine($"RESULT_UI: groups={groups}; selection/clear={watch.Elapsed.TotalSeconds:F3}s");
                Screenshot(window, "many-results.png");
            });
            await Test("UI17 native CSV export preserves six thousand Unicode paths and group metadata", () =>
            {
                var result = Result(window);
                Require(result.Groups.Count == 2000, "export requires the large-result fixture");
                string output = Path.Combine(_artifacts, "native-export.csv"); File.Delete(output);
                SaveNativeCsv(window, output);
                var bytes = File.ReadAllBytes(output);
                Require(bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "CSV lacks UTF-8 BOM");
                using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(output, Encoding.UTF8);
                parser.SetDelimiters(";"); parser.HasFieldsEnclosedInQuotes = true;
                var header = parser.ReadFields();
                Require(header?.Length == 7 && header[1] == "Путь" && header[5] == "BLAKE3", "CSV header");
                var expected = result.Groups.SelectMany(g => g.Files.Select(f => (g, f)))
                    .ToDictionary(x => x.f.Path, StringComparer.Ordinal);
                int rows = 0;
                while (!parser.EndOfData)
                {
                    var row = parser.ReadFields()!;
                    Require(row.Length == 7, "CSV field count");
                    Require(expected.Remove(row[1], out var member), "missing, duplicate or incorrectly quoted CSV path");
                    Require(row[0] == member.g.GroupId.ToString() && row[2] == member.f.LogicalSize.ToString() &&
                        row[5] == member.g.Hash && row[6] == "", "CSV group/size/hash/alias columns");
                    Require(row[4] == (member.g.Verification == VerificationState.ExactMatch ? "EXACT_MATCH" : "HASH_MATCH"), "CSV verification state");
                    rows++;
                }
                Require(rows == 6000 && expected.Count == 0, "CSV omitted results");
                Console.WriteLine($"CSV_EXPORT: native dialog; rows={rows}; UTF-8 BOM; Unicode and semicolon paths preserved");
                return Task.CompletedTask;
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

    private static void SaveNativeCsv(MainWindow window, string output)
    {
        bool submitted = false; string diagnostic = "";
        var driver = new Thread(() =>
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                IntPtr dialog = FindWindowW("#32770", "Сохранить отчёт");
                if (dialog != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(dialog, out uint process);
                    if (process == Environment.ProcessId)
                    {
                        try
                        {
                            var element = AutomationElement.FromHandle(dialog);
                            var edit = element.FindFirst(TreeScope.Descendants, new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                                new PropertyCondition(AutomationElement.AutomationIdProperty, "1001")));
                            var save = element.FindFirst(TreeScope.Descendants, new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new OrCondition(
                                new PropertyCondition(AutomationElement.NameProperty, "Save"),
                                new PropertyCondition(AutomationElement.NameProperty, "Сохранить"))));
                            if (edit != null && save != null && save.Current.IsEnabled)
                            {
                                edit.SetFocus();
                                TypeNativeFilename(dialog, edit, output);
                                var value = (ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern);
                                var typing = Stopwatch.StartNew();
                                while (value.Current.Value != output && typing.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(20);
                                if (value.Current.Value != output) throw new Exception("Native filename keyboard input was not processed");
                                save.SetFocus();
                                diagnostic = $"filename={edit.Current.Name}; button={save.Current.Name}; value={((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value}";
                                Console.WriteLine("CSV_SAVE_CONTROL: " + diagnostic);
                                submitted = true;
                                ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                                return;
                            }
                            var controls = element.FindAll(TreeScope.Descendants, new OrCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                            diagnostic = string.Join("; ", controls.Cast<AutomationElement>().Select(c => c.Current.AutomationId + ":" + c.Current.Name));
                        }
                        catch (Exception ex) { diagnostic = ex.Message; }
                        if (submitted && File.Exists(output)) return;
                        if (deadline.Elapsed >= TimeSpan.FromSeconds(18))
                        {
                            PostMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(2) /* IDCANCEL */, IntPtr.Zero);
                            return;
                        }
                    }
                }
                Thread.Sleep(20);
            }
        }) { IsBackground = true };
        driver.Start(); Click(window, "ExportButton"); driver.Join();
        Require(submitted && File.Exists(output), $"Native CSV save failed: submitted={submitted}; exists={File.Exists(output)}; {diagnostic}");
    }

    private static void TypeNativeFilename(IntPtr dialog, AutomationElement edit, string filename)
    {
        IntPtr input = new(edit.Current.NativeWindowHandle);
        if (input == IntPtr.Zero)
        {
            IntPtr combo = GetDlgItem(dialog, 1148);
            if (combo != IntPtr.Zero) input = GetDlgItem(combo, 1001);
        }
        Require(input != IntPtr.Zero, "native filename edit has no HWND");
        GetWindowThreadProcessId(input, out uint process);
        Require(process == Environment.ProcessId, "native input belongs to another process");
        // Direct character messages work in the runner's unattended desktop;
        // UI Automation SetValue does not update IFileDialog's chosen name.
        SendMessageW(input, 0x00B1 /* EM_SETSEL */, IntPtr.Zero, new IntPtr(-1));
        foreach (char character in filename)
            SendMessageW(input, 0x0102 /* WM_CHAR */, new IntPtr(character), new IntPtr(1));
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
    private static void Screenshot(Window window, string name, double dpi = 96)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96),
            (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_artifacts, name)); encoder.Save(output);
    }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr value, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr value, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int control);
}
