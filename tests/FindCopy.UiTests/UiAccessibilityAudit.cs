using FindCopy.App;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// An opt-in audit, separate from correctness regressions. Findings deliberately
// return a failure exit code; unverified assistive-technology checks stay explicit.
internal static class UiAccessibilityAudit
{
    private sealed record Probe(string Name, string Outcome, string Detail);
    private static readonly List<Probe> Probes = new();
    private static string _output = "";

    internal static async Task<int> Run(string output)
    {
        _output = output;
        string root = Path.Combine(Path.GetTempPath(), "findcopy-accessibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = new MainWindow();
        Application.Current.MainWindow = main;
        main.Show();
        try
        {
            await Settle();
            var hwnd = new WindowInteropHelper(main).Handle;
            uint dpi = GetDpiForWindow(hwnd);
            var wpfDpi = VisualTreeHelper.GetDpi(main);
            Check("Actual desktop DPI", dpi > 0 && Math.Abs(dpi - wpfDpi.PixelsPerInchX) < 1,
                $"GetDpiForWindow={dpi}; WPF={wpfDpi.PixelsPerInchX}; monitors={GetSystemMetrics(80)}");
            Record("Real 125/150/200% monitor transitions", "UNVERIFIED",
                "The runner's actual DPI is recorded above. Raster exports and simulated WM_DPICHANGED messages do not validate monitor transitions; no unsupported DPI override or logoff is attempted.");

            foreach (string name in new[] { "FolderBox", "KeepRuleBox" }) CheckName(Control<Control>(main, name), name);
            foreach (string name in new[] { "SearchButton", "CancelButton", "RecoveryButton", "ExpandButton", "ExportButton" })
                CheckName(Control<Control>(main, name), name);
            var phase = Control<TextBlock>(main, "PhaseText");
            Check("Scan status live region", AutomationProperties.GetLiveSetting(phase) != AutomationLiveSetting.Off,
                $"PhaseText LiveSetting={AutomationProperties.GetLiveSetting(phase)}; status changes need a polite announcement mechanism.");
            var header = Descendants<TextBlock>(main).First(t => t.Text.StartsWith("Сравнение по содержимому"));
            double contrast = Contrast(((SolidColorBrush)header.Foreground).Color, ((SolidColorBrush)main.Background).Color);
            Check("Header supporting text contrast", contrast >= 4.5,
                $"ratio={contrast:F4}:1; normal-size text requires at least 4.5:1");

            await CheckKeyboard(main);
            await ScanFixture(main, root);
            foreach (var box in Descendants<CheckBox>(Control<TreeView>(main, "ResultTree")))
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(box);
                string accessibleName = peer?.GetName() ?? "";
                string path = (box.DataContext as FileVM)?.Path ?? "";
                Check("Result deletion checkbox identifies its file", path.Length > 0 && accessibleName.Contains(path, StringComparison.OrdinalIgnoreCase),
                    $"name={accessibleName}; path={path}; help={peer?.GetHelpText()}");
            }
            Check("Realized result deletion controls", Descendants<CheckBox>(Control<TreeView>(main, "ResultTree")).Any(),
                "An empty traversal cannot validate checkbox names.");
            Save(main, "main-long-paths.png");
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            Descendants<Expander>(main).Single().IsExpanded = true;
            await Settle();
            foreach (string name in new[] { "SearchButton", "DeleteButton", "RecoveryButton", "ExportButton" })
                CheckBounds(main, Control<FrameworkElement>(main, name), "Minimum-size expanded " + name);
            var tree = Control<TreeView>(main, "ResultTree");
            Check("Minimum-size expanded result viewport", tree.ActualHeight >= 40,
                $"result viewport height={tree.ActualHeight:F1} DIPs; advanced settings must not eliminate access to results");
            Save(main, "minimum-size-expanded.png");

            var settings = new SettingsWindow(new AppSettings()) { Owner = main };
            settings.Show();
            try
            {
                await Settle();
                foreach (string name in new[] { "ThresholdBox", "SampleBox", "BufferBox", "HddBox", "SsdBox", "NvmeBox", "NetBox", "UnknownBox" })
                    CheckName(Control<TextBox>(settings, name), name);
                Save(settings, "settings.png");
            }
            finally { settings.Close(); }
            var recovery = new RecoveryWindow(Path.Combine(root, "history")) { Owner = main };
            recovery.Show();
            try
            {
                await Settle();
                var status = Control<TextBlock>(recovery, "StatusText");
                Check("Recovery status live region", AutomationProperties.GetLiveSetting(status) != AutomationLiveSetting.Off,
                    $"StatusText LiveSetting={AutomationProperties.GetLiveSetting(status)}");
                Save(recovery, "recovery.png");
            }
            finally { recovery.Close(); }
            main.Width = 1100; main.Height = 760;
            Descendants<Expander>(main).Single().IsExpanded = false;
            await CheckHighContrast(main);
            await CheckNarrator(main);
        }
        catch (Exception ex) { Record("Audit execution", "ERROR", ex.ToString()); }
        finally
        {
            main.Close();
            // Only this uniquely created fixture directory is removed.
            try { Directory.Delete(root, true); }
            catch (Exception ex) { Record("Fixture cleanup", "ERROR", ex.Message); }
            File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(new
            {
                Os = RuntimeInformation.OSDescription,
                HostArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Probes,
                Passed = Probes.Count(p => p.Outcome == "PASS"),
                Findings = Probes.Count(p => p.Outcome == "FINDING"),
                Errors = Probes.Count(p => p.Outcome == "ERROR"),
                Unverified = Probes.Count(p => p.Outcome == "UNVERIFIED")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return Probes.Count(p => p.Outcome is "FINDING" or "ERROR");
    }

    private static async Task ScanFixture(MainWindow main, string root)
    {
        string directory = Path.Combine(root, new string('d', 150));
        Directory.CreateDirectory(directory);
        foreach (string suffix in new[] { "a", "b" })
            File.WriteAllText(Path.Combine(directory, "копия-" + new string('x', 100) + suffix + ".txt"), "bounded duplicate fixture");
        Control<TextBox>(main, "FolderBox").Text = root;
        Control<CheckBox>(main, "CacheBox").IsChecked = false;
        Control<Button>(main, "SearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var clock = Stopwatch.StartNew();
        var cts = typeof(MainWindow).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while (cts.GetValue(main) != null && clock.Elapsed < TimeSpan.FromSeconds(30)) await Settle();
        if (cts.GetValue(main) != null) throw new TimeoutException("Bounded accessibility fixture scan did not finish");
        var group = Control<TreeView>(main, "ResultTree").Items.Cast<GroupVM>().Single();
        group.IsExpanded = true;
        await Settle();
    }

    private static async Task CheckKeyboard(MainWindow main)
    {
        var hwnd = new WindowInteropHelper(main).Handle;
        main.Activate(); SetForegroundWindow(hwnd);
        await Settle();
        var folder = Control<TextBox>(main, "FolderBox");
        Keyboard.Focus(folder);
        await Settle();
        bool foreground = GetForegroundWindow() == hwnd;
        if (!foreground || !folder.IsKeyboardFocused)
        {
            Record("Physical keyboard input on hosted desktop", "UNVERIFIED",
                $"foreground={foreground}; folder focus={folder.IsKeyboardFocused}; SendInput cannot be attributed to the app without foreground ownership.");
            bool moved = folder.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            var next = Keyboard.FocusedElement as Button;
            Check("WPF forward focus traversal reaches browse button", moved && next?.Content?.ToString() == "Обзор…",
                $"MoveFocus={moved}; focused={Keyboard.FocusedElement?.GetType().Name}; content={next?.Content}; this is not physical keyboard input");
            if (next != null)
            {
                Check("Keyboard focus visual configured", next.FocusVisualStyle != null,
                    $"FocusVisualStyle={(next.FocusVisualStyle == null ? "null" : "present")}; actual keyboard focus rendering remains unverified");
                Save(main, "programmatic-focus.png");
            }
            Record("Complete keyboard traversal and focus visibility", "UNVERIFIED",
                "WPF focus traversal is exercised separately. Physical Tab/Space and every visible focus indicator require an interactive foreground desktop.");
            return;
        }
        Check("Keyboard desktop available", true, "The application owns the foreground and FolderBox keyboard focus.");
        Key(0x09); // Physical Tab via SendInput, not a synthesized WPF routed event.
        await Settle();
        var browse = Keyboard.FocusedElement as Button;
        Check("Tab reaches browse button", browse?.Content?.ToString() == "Обзор…",
            $"focused={Keyboard.FocusedElement?.GetType().Name}; content={browse?.Content}");
        if (browse != null)
        {
            Check("Keyboard focus visual configured", browse.FocusVisualStyle != null,
                $"FocusVisualStyle={(browse.FocusVisualStyle == null ? "null" : "present")}; screenshot retained for visual review");
            Save(main, "keyboard-focus.png");
        }
        Keyboard.Focus(Control<CheckBox>(main, "RecursiveBox"));
        await Settle();
        bool? before = Control<CheckBox>(main, "RecursiveBox").IsChecked;
        Key(0x20);
        await Settle();
        Check("Space toggles recursive checkbox", Control<CheckBox>(main, "RecursiveBox").IsChecked != before,
            $"before={before}; after={Control<CheckBox>(main, "RecursiveBox").IsChecked}");
        Control<CheckBox>(main, "RecursiveBox").IsChecked = true;
        Record("Complete keyboard traversal and focus visibility", "UNVERIFIED",
            "Tab and Space are exercised with real SendInput. Every menu, result action and visible focus indicator still needs a complete keyboard-only human review.");
    }

    private static async Task CheckHighContrast(MainWindow main)
    {
        Color originalBackground = ((SolidColorBrush)main.Background).Color;
        var original = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(0x0042, original.Size, ref original, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETHIGHCONTRAST");
        bool changed = false;
        try
        {
            var enabled = original; enabled.Flags |= 1; // HCF_HIGHCONTRASTON
            if (!SystemParametersInfo(0x0043, enabled.Size, ref enabled, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETHIGHCONTRAST");
            changed = true;
            await Task.Delay(500); await Settle();
            Check("Actual Windows high contrast enabled", SystemParameters.HighContrast,
                $"WPF HighContrast={SystemParameters.HighContrast}; system window={SystemColors.WindowColor}; system text={SystemColors.WindowTextColor}");
            if (SystemParameters.HighContrast)
            {
                var title = Descendants<TextBlock>(main).First(t => t.Text == "Поиск дубликатов файлов");
                Check("Application honors high-contrast palette",
                    ((SolidColorBrush)main.Background).Color == SystemColors.WindowColor &&
                    ((SolidColorBrush)title.Foreground).Color == SystemColors.WindowTextColor,
                    $"application window={((SolidColorBrush)main.Background).Color}; text={((SolidColorBrush)title.Foreground).Color}; fixed light colors remain in use");
                var resultTree = Control<TreeView>(main, "ResultTree");
                var fileName = Descendants<TextBlock>(resultTree).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>())
                    .First(r => r.Text.StartsWith("копия-"));
                Color fileForeground = ((SolidColorBrush)fileName.Foreground).Color;
                Color resultBackground = ((SolidColorBrush)resultTree.Background).Color;
                double fileContrast = Contrast(fileForeground, resultBackground);
                Check("High-contrast file name readability", fileContrast >= 4.5,
                    $"foreground={fileForeground}; result background={resultBackground}; ratio={fileContrast:F4}:1");
                var group = resultTree.Items.Cast<GroupVM>().Single();
                var groupItem = (TreeViewItem)resultTree.ItemContainerGenerator.ContainerFromItem(group);
                var fileItem = (TreeViewItem)groupItem.ItemContainerGenerator.ContainerFromItem(group.Files[0]);
                fileItem.IsSelected = true;
                await Settle();
                Color selectedBackground = fileItem.IsSelectionActive ? SystemColors.HighlightColor : SystemColors.InactiveSelectionHighlightBrush.Color;
                foreach (var run in Descendants<TextBlock>(fileItem).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>())
                    .Where(r => !string.IsNullOrWhiteSpace(r.Text)))
                {
                    double selectedContrast = Contrast(((SolidColorBrush)run.Foreground).Color, selectedBackground);
                    Check("High-contrast selected file text readability", selectedContrast >= 4.5,
                        $"active={fileItem.IsSelectionActive}; foreground={((SolidColorBrush)run.Foreground).Color}; background={selectedBackground}; ratio={selectedContrast:F4}:1");
                }
                Save(main, "actual-high-contrast.png");
            }
        }
        finally
        {
            if (changed)
            {
                if (!SystemParametersInfo(0x0043, original.Size, ref original, 2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Restore high contrast");
                await Task.Delay(500); await Settle();
                Check("Original high-contrast setting restored", SystemParameters.HighContrast == ((original.Flags & 1) != 0),
                    $"restored WPF HighContrast={SystemParameters.HighContrast}");
                Check("Application palette restored after high contrast", ((SolidColorBrush)main.Background).Color == originalBackground,
                    $"original={originalBackground}; restored={((SolidColorBrush)main.Background).Color}");
            }
        }
    }

    private static async Task CheckNarrator(MainWindow main)
    {
        var existing = Process.GetProcessesByName("Narrator");
        if (existing.Length > 0)
        {
            foreach (var process in existing) process.Dispose();
            Record("Narrator startup smoke", "UNVERIFIED", "An existing Narrator process is left untouched.");
        }
        else
        {
            Process? narrator = null;
            try
            {
                string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Narrator.exe");
                // Narrator uses UIAccess; ShellExecute invokes Windows' trusted
                // accessibility launch path without requesting runas elevation.
                narrator = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                await Task.Delay(2000);
                bool running = narrator != null && !narrator.HasExited;
                Record("Narrator startup smoke", running ? "PASS" : "UNVERIFIED",
                    running ? $"System Narrator process {narrator!.Id} stayed running; this does not prove speech or correct announcements."
                        : "Narrator launcher exited; a startup process alone cannot establish usable speech output.");
                Save(main, "narrator-startup.png");
            }
            catch (Exception ex) { Record("Narrator startup smoke", "UNVERIFIED", ex.Message); }
            finally
            {
                if (narrator != null)
                {
                    try { if (!narrator.HasExited) { narrator.Kill(); narrator.WaitForExit(3000); } }
                    finally { narrator.Dispose(); }
                }
            }
        }
        Record("Narrator speech and announcement usability", "UNVERIFIED",
            "The hosted runner provides no verified human listening session. UI Automation names and live-region configuration are checked separately; screen-reader usability is not declared passed.");
    }

    private static void CheckName(Control control, string name)
    {
        string value = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName() ?? "";
        Check("Accessible name: " + name, !string.IsNullOrWhiteSpace(value), $"UI Automation name={value}");
    }
    private static void CheckBounds(Window window, FrameworkElement element, string name)
    {
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Check(name, element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 &&
            bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
            $"bounds={bounds}; window={window.ActualWidth}x{window.ActualHeight}");
    }
    private static T Control<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Check(string name, bool pass, string detail) => Record(name, pass ? "PASS" : "FINDING", detail);
    private static void Record(string name, string outcome, string detail)
    {
        Probes.Add(new(name, outcome, detail));
        Console.WriteLine($"{outcome} {name}: {detail}");
    }
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double v = channel / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Save(Window window, string name)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(_output, name)); encoder.Save(output);
    }
    private static void Key(ushort virtualKey)
    {
        // INPUT's union must have MOUSEINPUT's full native size, including on ARM64.
        var input = new[] { new NativeInput { Type = 1, Key = virtualKey }, new NativeInput { Type = 1, Key = virtualKey, Flags = 2 } };
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<NativeInput>()) != input.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size; public uint Flags; public IntPtr DefaultScheme; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrast value, uint flags);
}
