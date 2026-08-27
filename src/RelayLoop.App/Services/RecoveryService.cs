using RelayLoop.Core;

namespace RelayLoop.App.Services;

public sealed class RecoveryService
{
    public RecoveryService(string baseDirectory)
    {
        var recoveryDirectory = Path.Combine(baseDirectory, "Recovery");
        RecoveryPath = Path.Combine(recoveryDirectory, "last-recording.rloop.recovery");
    }

    public string RecoveryPath { get; }

    public bool Exists => File.Exists(RecoveryPath);

    public DateTime? LastWriteTime => Exists ? File.GetLastWriteTime(RecoveryPath) : null;

    public Task SaveAsync(MacroDocument document, CancellationToken cancellationToken = default) =>
        MacroSerializer.SaveAsync(RecoveryPath, document, cancellationToken);

    public Task<MacroDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        MacroSerializer.LoadAsync(RecoveryPath, cancellationToken);

    public void Clear()
    {
        try
        {
            if (File.Exists(RecoveryPath))
            {
                File.Delete(RecoveryPath);
            }
        }
        catch (IOException)
        {
            // Recovery cleanup is best effort and must never block a safe shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // Recovery cleanup is best effort and must never block a safe shutdown.
        }
    }
}
