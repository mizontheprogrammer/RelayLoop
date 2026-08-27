using System.Text.Json;
using System.Security;

namespace RelayLoop.App.Services;

public sealed class StructuredLogger
{
    private readonly object _gate = new();
    private readonly string _logPath;

    public StructuredLogger(string baseDirectory)
    {
        var logDirectory = Path.Combine(baseDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"relayloop-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    }

    public void Information(string eventName) => Write("information", eventName, null);

    public void Warning(string eventName) => Write("warning", eventName, null);

    public void Error(string eventName, Exception exception) =>
        Write("error", eventName, new { type = exception.GetType().Name, exception.HResult });

    private void Write(string level, string eventName, object? error)
    {
        // Input payloads, key codes, pointer coordinates, and typed content are deliberately never accepted here.
        var entry = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level,
            eventName,
            error
        });

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_logPath, entry + Environment.NewLine);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Logging is best effort and must never interrupt recording, playback, or cleanup.
            }
        }
    }
}
