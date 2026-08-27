using System.Windows;
using System.Windows.Threading;

namespace RelayLoop.Runner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            window.HandleFatalError(e.Exception);
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            $"RelayLoop Runner could not start.\n\n{e.Exception.Message}",
            "RelayLoop Runner",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
