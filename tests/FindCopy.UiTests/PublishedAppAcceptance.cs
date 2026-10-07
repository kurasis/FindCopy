using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class PublishedAppAcceptance
{
    public static void Run(string executable, string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keeper"), "Published desktop acceptance bytes");
        File.WriteAllText(Path.Combine(root, "extra"), "Published desktop acceptance bytes");
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
        int processId = process.Id;
        using var done = new CancellationTokenSource();
        var dialogs = new Thread(() =>
        {
            var reported = new HashSet<IntPtr>();
            while (!done.IsCancellationRequested)
            {
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint pid);
                    if (pid != processId) return true;
                    var title = new StringBuilder(256); GetWindowTextW(handle, title, title.Capacity);
                    // Posting avoids blocking the responder while a second modal dialog opens.
                    IntPtr button = IntPtr.Zero;
                    if (title.ToString() == "Подтвердите удаление") button = GetDlgItem(handle, 6);
                    if (title.ToString() == "Удаление")
                    {
                        var buttons = new List<IntPtr>();
                        EnumChildWindows(handle, (child, _) =>
                        {
                            var cls = new StringBuilder(64); GetClassNameW(child, cls, cls.Capacity);
                            if (cls.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase)) buttons.Add(child);
                            return true;
                        }, IntPtr.Zero);
                        // An information box's only button can have IDCANCEL rather than IDOK on Windows.
                        // Refuse multi-button dialogs here: no automatic permanent-delete fallback.
                        if (buttons.Count == 1) button = buttons[0];
                    }
                    if (button != IntPtr.Zero)
                    {
                        int id = GetDlgCtrlID(button);
                        bool posted = PostMessageW(handle, 0x0111, new IntPtr(id), button);
                        if (reported.Add(handle)) Console.WriteLine($"PUBLISHED_DIALOG: {title}, button={id}, posted={posted}");
                    }
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(20);
            }
        }) { IsBackground = true };
        dialogs.Start();
        try
        {
            AutomationElement? window = null;
            Until(() =>
            {
                if (process.HasExited) throw new IOException("Published desktop exited during startup: " + process.ExitCode);
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero) return false;
                window = AutomationElement.FromHandle(process.MainWindowHandle);
                return window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FolderBox")) != null;
            });
            AutomationElement Element(string id) => window!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new ControlNotReadyException(id);
            void Click(string id) => ((InvokePattern)Element(id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((ValuePattern)Element("FolderBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue(root);
            Click("SearchButton");
            Until(() => Element("SummaryText").Current.Name.Contains("Найдено групп: 1"));
            Click("SelectExtrasButton"); Until(() => Element("DeleteButton").Current.IsEnabled);
            Click("DeleteButton");
            Until(() => Directory.EnumerateFiles(root).Count() == 1 && Element("SearchButton").Current.IsEnabled);
            Until(() => Element("SummaryText").Current.Name.Contains("Удалено файлов: 1"));
            if (Directory.EnumerateFiles(root).Count() != 1 || Element("SummaryText").Current.Name.Contains("замечаниями"))
                throw new Exception("Published recycling did not complete cleanly and preserve one copy");
            // The summary updates before the modal result dialog closes and controls are restored.
            Until(() => Element("SearchButton").Current.IsEnabled);
            ((WindowPattern)window!.GetCurrentPattern(WindowPattern.Pattern)).Close();
            if (!process.WaitForExit(10_000)) throw new Exception("Published application remained running after closing");
            if (process.ExitCode != 0) throw new Exception("Published application exited with code " + process.ExitCode);
        }
        catch
        {
            Console.WriteLine($"PUBLISHED_FAILURE: exited={process.HasExited}, fixtureFiles={Directory.EnumerateFiles(root).Count()}");
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint pid);
                if (pid == processId)
                {
                    var text = new StringBuilder(2048); GetWindowTextW(handle, text, text.Capacity);
                    Console.WriteLine($"PUBLISHED_WINDOW: {text}, enabled={IsWindowEnabled(handle)}");
                    EnumChildWindows(handle, (child, _) =>
                    {
                        var caption = new StringBuilder(2048); GetWindowTextW(child, caption, caption.Capacity);
                        if (caption.Length > 0) Console.WriteLine("PUBLISHED_CHILD: " + caption);
                        return true;
                    }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            throw;
        }
        finally
        {
            done.Cancel(); dialogs.Join(1000);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { if (condition()) return; }
            catch (ElementNotAvailableException) { /* Providers can be temporarily unavailable during modal transitions. */ }
            catch (ControlNotReadyException) { }
            if (clock.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Published UI operation timed out");
            Thread.Sleep(20);
        }
    }
    private sealed class ControlNotReadyException(string id) : Exception("Missing published control: " + id);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
