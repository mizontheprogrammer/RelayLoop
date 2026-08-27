using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using RelayLoop.App.ViewModels;

namespace RelayLoop.App;

public partial class MainWindow : Window
{
    private bool _closingApproved;
    private bool _closeInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
            ApplyWindowDimensions(current);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel viewModel &&
            e.PropertyName is nameof(MainViewModel.IsExpanded) or
                              nameof(MainViewModel.WindowWidth) or
                              nameof(MainViewModel.WindowHeight) or
                              nameof(MainViewModel.WindowMinWidth))
        {
            ApplyWindowDimensions(viewModel);
        }
    }

    private void ApplyWindowDimensions(MainViewModel viewModel)
    {
        MinWidth = viewModel.WindowMinWidth;
        Width = viewModel.WindowWidth;
        Height = viewModel.WindowHeight;
    }

    private async void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        try
        {
            await ViewModel.InitializeAsync(handle).ConfigureAwait(true);
            ViewModel.TryGetWindowPosition(out var left, out var top);
            Left = left;
            Top = top;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "RelayLoop initialization", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSingleMacroFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !HasSingleMacroFile(e.Data))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await ViewModel.OpenPathAsync(paths[0]).ConfigureAwait(true);
    }

    private void OnLocationChanged(object? sender, EventArgs e) => ViewModel?.UpdateWindowPosition(Left, Top);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingApproved || ViewModel is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            if (await ViewModel.RequestCloseAsync().ConfigureAwait(true))
            {
                _closingApproved = true;
                _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Normal);
            }
        }
        catch (Exception exception)
        {
            ViewModel.EmergencyShutdown();
            MessageBox.Show(
                this,
                "RelayLoop encountered an error while closing and performed emergency cleanup. " +
                "If a key or mouse button still appears held, press and release it manually.\n\n" +
                exception.Message,
                "RelayLoop close warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _closingApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Normal);
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Z when Keyboard.Modifiers == ModifierKeys.Control => ViewModel.UndoCommand,
            Key.Y when Keyboard.Modifiers == ModifierKeys.Control => ViewModel.RedoCommand,
            Key.Delete when Keyboard.Modifiers == ModifierKeys.None => ViewModel.DeleteEventCommand,
            _ => null,
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private static bool HasSingleMacroFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
        {
            return false;
        }

        return string.Equals(Path.GetExtension(paths[0]), RelayLoop.Core.MacroDocument.FileExtension,
            StringComparison.OrdinalIgnoreCase);
    }
}
