using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RelayLoop.App.ViewModels;

namespace RelayLoop.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\RelayLoop.SingleInstance.v1";
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            MessageBox.Show(
                "RelayLoop is already running in this Windows session.",
                "RelayLoop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;

        _viewModel = new MainViewModel();
        MainWindow = new MainWindow
        {
            DataContext = _viewModel,
        };
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        SessionEnding -= OnSessionEnding;
        _viewModel?.EmergencyShutdown();

        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex can already be released after an abnormal dispatcher shutdown.
            }
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) =>
        _viewModel?.EmergencyShutdown();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _viewModel?.EmergencyShutdown();
        try
        {
            MessageBox.Show(
                "RelayLoop encountered an unexpected error and stopped all recording and playback. " +
                "It attempted to release every input held by playback before shutdown. If a key or " +
                "mouse button still appears held, press and release it manually.",
                "RelayLoop stopped safely",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Do not mask the original unhandled exception during final shutdown.
        }

        e.Handled = false;
    }
}
