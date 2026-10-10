using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;

namespace FindCopy.App;

public partial class App : Application
{
    internal bool CreateStartupWindow { get; set; } = true;
    private UiPalette? _palette;
    protected override void OnStartup(StartupEventArgs e)
    {
        _palette = new UiPalette(Resources);
        _palette.Apply(Resources);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        if (CreateStartupWindow) new MainWindow().Show();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(() => _palette?.Apply(Resources));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Непредвиденная ошибка:\n\n" + e.Exception.Message, "FindCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
