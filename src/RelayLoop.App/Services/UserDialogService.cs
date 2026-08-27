using Microsoft.Win32;
using System.Windows;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel
}

public interface IUserDialogService
{
    string? ChooseMacroToOpen(string? initialDirectory = null);
    string? ChooseMacroToSave(string suggestedName, string? initialDirectory = null);
    string? ChooseRunnerDestination(string suggestedName, string? initialDirectory = null);
    SaveChangesChoice ConfirmUnsavedChanges(string macroName);
    bool ConfirmRecovery(DateTime lastWriteTime);
    bool ConfirmLayoutMismatch(string warning);
    void ShowInformation(string title, string message);
    void ShowError(string title, string message);
}

public sealed class UserDialogService : IUserDialogService
{
    private const string MacroFilter = "RelayLoop macro (*.rloop)|*.rloop|All files (*.*)|*.*";

    public string? ChooseMacroToOpen(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open RelayLoop macro",
            Filter = MacroFilter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingDirectoryOrNull(initialDirectory)
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseMacroToSave(string suggestedName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save RelayLoop macro",
            Filter = MacroFilter,
            DefaultExt = MacroDocument.FileExtension,
            AddExtension = true,
            FileName = SanitizeFileName(suggestedName, "My macro") + MacroDocument.FileExtension,
            InitialDirectory = ExistingDirectoryOrNull(initialDirectory)
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseRunnerDestination(string suggestedName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export standalone RelayLoop runner",
            Filter = "Windows executable (*.exe)|*.exe",
            DefaultExt = ".exe",
            AddExtension = true,
            FileName = SanitizeFileName(suggestedName, "RelayLoop macro") + ".exe",
            InitialDirectory = ExistingDirectoryOrNull(initialDirectory)
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public SaveChangesChoice ConfirmUnsavedChanges(string macroName)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"Save changes to {macroName} before continuing?",
            "RelayLoop — unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        return result switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel
        };
    }

    public bool ConfirmLayoutMismatch(string warning) => MessageBox.Show(
        Application.Current.MainWindow,
        warning + Environment.NewLine + Environment.NewLine + "Playback coordinates may land on a different screen. Continue?",
        "RelayLoop — display layout changed",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRecovery(DateTime lastWriteTime) => MessageBox.Show(
        Application.Current.MainWindow,
        $"RelayLoop found a recoverable recording from {lastWriteTime:g}.\n\n" +
        "Choose Yes to load it, or No to discard this recovery copy.",
        "RelayLoop — recovery available",
        MessageBoxButton.YesNo,
        MessageBoxImage.Information,
        MessageBoxResult.Yes) == MessageBoxResult.Yes;

    public void ShowInformation(string title, string message) => MessageBox.Show(
        Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) => MessageBox.Show(
        Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static string? ExistingDirectoryOrNull(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : null;

    private static string SanitizeFileName(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
