using System.Text.Json;
using System.Security;
using RelayLoop.App.Models;

namespace RelayLoop.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SettingsService(string? baseDirectory = null)
    {
        var environmentDirectory = Environment.GetEnvironmentVariable("RELAYLOOP_DATA_DIR");
        BaseDirectory = baseDirectory ??
            (!string.IsNullOrWhiteSpace(environmentDirectory)
                ? Path.GetFullPath(environmentDirectory)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelayLoop"));
        SettingsPath = Path.Combine(BaseDirectory, "settings.json");
    }

    public string BaseDirectory { get; }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return Validate(settings ?? new AppSettings());
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
        catch (SecurityException)
        {
            return new AppSettings();
        }
        catch (NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Validate(settings);
        Directory.CreateDirectory(BaseDirectory);
        var temporaryPath = Path.Combine(BaseDirectory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            if (File.Exists(SettingsPath))
            {
                var backupPath = SettingsPath + ".bak";
                File.Replace(temporaryPath, SettingsPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cleanup is best effort and must not replace the save error or damage settings.
        }
    }

    private static AppSettings Validate(AppSettings settings)
    {
        settings.Version = AppSettings.CurrentVersion;
        settings.PlaybackSpeed = double.IsFinite(settings.PlaybackSpeed)
            ? Math.Clamp(settings.PlaybackSpeed, 0.25, 100.0)
            : 1.0;
        settings.RepeatCount = Math.Clamp(settings.RepeatCount, 1, 9999);
        if (settings.WindowLeft is double left && !double.IsFinite(left))
        {
            settings.WindowLeft = null;
        }

        if (settings.WindowTop is double top && !double.IsFinite(top))
        {
            settings.WindowTop = null;
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = ThemePreference.System;
        }

        settings.RecordHotkey = NormalizeHotkey(settings.RecordHotkey, 0x52);
        settings.PlayHotkey = NormalizeHotkey(settings.PlayHotkey, 0x50);
        settings.StopHotkey = NormalizeHotkey(settings.StopHotkey, 0x53);
        if (AreEquivalent(settings.RecordHotkey, settings.PlayHotkey) ||
            AreEquivalent(settings.RecordHotkey, settings.StopHotkey) ||
            AreEquivalent(settings.PlayHotkey, settings.StopHotkey))
        {
            settings.RecordHotkey = CreateDefaultHotkey(0x52);
            settings.PlayHotkey = CreateDefaultHotkey(0x50);
            settings.StopHotkey = CreateDefaultHotkey(0x53);
        }

        return settings;
    }

    private static HotkeySetting NormalizeHotkey(HotkeySetting? setting, uint defaultVirtualKey)
    {
        const uint modifierMask = 0x0001 | 0x0002 | 0x0004 | 0x0008;
        const uint allowedMask = modifierMask | 0x4000;
        if (setting is null ||
            setting.VirtualKey is 0 or > 0xFF ||
            (setting.Modifiers & modifierMask) == 0 ||
            (setting.Modifiers & ~allowedMask) != 0)
        {
            return CreateDefaultHotkey(defaultVirtualKey);
        }

        setting.Modifiers |= 0x4000;
        return setting;
    }

    private static HotkeySetting CreateDefaultHotkey(uint virtualKey) => new()
    {
        Modifiers = AppSettings.DefaultHotkeyModifiers,
        VirtualKey = virtualKey,
    };

    private static bool AreEquivalent(HotkeySetting left, HotkeySetting right) =>
        left.VirtualKey == right.VirtualKey && left.Modifiers == right.Modifiers;
}
