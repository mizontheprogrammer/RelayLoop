using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayLoop.App.Models;
using RelayLoop.Core;
using CorePlaybackOptions = RelayLoop.Core.PlaybackOptions;

namespace RelayLoop.App.Services;

/// <summary>Stores named profiles atomically beneath RelayLoop's local application-data folder.</summary>
public sealed class ProfileService
{
    public const int MaximumNameLength = 64;
    private const long MaximumProfileBytes = 128L * 1024 * 1024;
    private const string ProfileExtension = ".rlprofile";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public ProfileService(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ProfilesDirectory = Path.Combine(Path.GetFullPath(baseDirectory), "Profiles");
    }

    public string ProfilesDirectory { get; }

    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ProfilesDirectory))
        {
            return [];
        }

        List<string> names = [];
        foreach (var path in Directory.EnumerateFiles(ProfilesDirectory, "*" + ProfileExtension, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = await LoadPathAsync(path, expectedName: null, cancellationToken).ConfigureAwait(false);
                names.Add(profile.Name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException or InvalidDataException or MacroValidationException or ArgumentException)
            {
                // A damaged profile is isolated from valid profiles and can be replaced by saving it again.
            }
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetProfilePath(ValidateName(name))));
    }

    public async Task SaveAsync(MacroProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Name = ValidateName(profile.Name);
        ValidateProfile(profile, profile.Name);
        profile.Format = MacroProfile.FormatIdentifier;
        profile.Version = MacroProfile.CurrentVersion;
        profile.SavedUtc = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(ProfilesDirectory);
        var destination = GetProfilePath(profile.Name);
        var temporary = Path.Combine(ProfilesDirectory, $"profile.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public Task<MacroProfile> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        return LoadPathAsync(GetProfilePath(name), name, cancellationToken);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetProfilePath(ValidateName(name));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public static string ValidateName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumNameLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"Profile names must contain 1 to {MaximumNameLength} visible characters.", nameof(name));
        }

        return normalized;
    }

    private async Task<MacroProfile> LoadPathAsync(
        string path,
        string? expectedName,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new FileNotFoundException("The selected profile no longer exists.", path);
        }

        if (information.Length is <= 0 or > MaximumProfileBytes)
        {
            throw new InvalidDataException("The profile file has an invalid size.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var profile = await JsonSerializer.DeserializeAsync<MacroProfile>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The profile file is empty.");
        ValidateProfile(profile, expectedName);
        profile.Name = ValidateName(profile.Name);
        profile.Document = profile.Document.DeepClone();
        return profile;
    }

    private static void ValidateProfile(MacroProfile profile, string? expectedName)
    {
        if (!string.Equals(profile.Format, MacroProfile.FormatIdentifier, StringComparison.Ordinal) ||
            profile.Version != MacroProfile.CurrentVersion)
        {
            throw new InvalidDataException("The file is not a supported RelayLoop profile.");
        }

        var actualName = ValidateName(profile.Name);
        if (expectedName is not null && !string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The profile name does not match its storage key.");
        }

        if (profile.Document is null)
        {
            throw new InvalidDataException("The profile has no macro document.");
        }

        MacroValidator.Validate(profile.Document);
        _ = new CorePlaybackOptions(profile.PlaybackSpeed, profile.RepeatCount, profile.ContinuousPlayback);
    }

    private string GetProfilePath(string name)
    {
        var normalizedKey = name.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey));
        return Path.Combine(ProfilesDirectory, Convert.ToHexString(digest) + ProfileExtension);
    }

    private static void TryDelete(string path)
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
            // Cleanup is best effort and must not replace the original save error.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = MacroSerializer.JsonOptions;
        options.WriteIndented = true;
        return options;
    }
}
