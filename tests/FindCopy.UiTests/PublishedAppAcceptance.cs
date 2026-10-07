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
        using var done = new CancellationTokenSource();
        var dialogs = new Thread(() =>
        {
            while (!done.IsCancellationRequested)
            {
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint pid);
                    if (pid != process.Id) return true;
                    var title = new StringBuilder(256); GetWindowTextW(handle, title, title.Capacity);
                    if (title.ToString() == "Подтвердите удаление") SendMessageW(handle, 0x0111, new IntPtr(6), IntPtr.Zero);
                    if (title.ToString() == "Удаление") SendMessageW(handle, 0x0111, new IntPtr(1), IntPtr.Zero);
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
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new Exception("Missing published control: " + id);
            void Click(string id) => ((InvokePattern)Element(id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((ValuePattern)Element("FolderBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue(root);
            Click("SearchButton");
            Until(() => Element("SummaryText").Current.Name.Contains("Найдено групп: 1"));
            Click("SelectExtrasButton"); Until(() => Element("DeleteButton").Current.IsEnabled);
            Click("DeleteButton");
            Until(() => Element("SummaryText").Current.Name.Contains("Удалено файлов: 1"));
            if (Directory.EnumerateFiles(root).Count() != 1 || Element("SummaryText").Current.Name.Contains("замечаниями"))
                throw new Exception("Published recycling did not complete cleanly and preserve one copy");
            // The summary updates before the modal result dialog closes and controls are restored.
            Until(() => Element("SearchButton").Current.IsEnabled);
            ((WindowPattern)window!.GetCurrentPattern(WindowPattern.Pattern)).Close();
            if (!process.WaitForExit(10_000)) throw new Exception("Published application remained running after closing");
            if (process.ExitCode != 0) throw new Exception("Published application exited with code " + process.ExitCode);
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
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Published UI operation timed out");
            Thread.Sleep(20);
        }
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
