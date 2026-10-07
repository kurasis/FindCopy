using System.Windows;
using System.Windows.Threading;

namespace FindCopy.App;

public partial class App : Application
{
    internal bool CreateStartupWindow { get; set; } = true;
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        if (CreateStartupWindow) new MainWindow().Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Непредвиденная ошибка:\n\n" + e.Exception.Message, "FindCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
