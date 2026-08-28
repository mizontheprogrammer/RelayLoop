using System.Text.Json.Serialization;
using RelayLoop.Core;

namespace RelayLoop.App.Models;

/// <summary>A named, locally stored macro plus the playback settings needed to run it.</summary>
public sealed class MacroProfile
{
    public const string FormatIdentifier = "RelayLoop.Profile";
    public const int CurrentVersion = 1;

    [JsonRequired]
    public string Format { get; set; } = FormatIdentifier;

    [JsonRequired]
    public int Version { get; set; } = CurrentVersion;

    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonRequired]
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonRequired]
    public MacroDocument Document { get; set; } = new();

    [JsonRequired]
    public double PlaybackSpeed { get; set; } = 1;

    [JsonRequired]
    public int RepeatCount { get; set; } = 1;

    [JsonRequired]
    public bool ContinuousPlayback { get; set; }

    [JsonRequired]
    public bool LockMouseDuringDirectionalHold { get; set; }
}
