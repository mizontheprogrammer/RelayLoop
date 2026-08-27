using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class MacroSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesEverySupportedField()
    {
        var original = TestMacros.Create();

        var json = MacroSerializer.Serialize(original);
        var restored = MacroSerializer.Deserialize(json);

        Assert.Contains("\"format\": \"RelayLoop.Macro\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"mouseMove\"", json, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isEnabled", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(-1920, restored.DisplayLayout!.VirtualLeft);
        Assert.Equal(2, restored.DisplayLayout.Monitors.Count);
        Assert.Equal((uint)120, restored.DisplayLayout.Monitors[0].DpiX);
        Assert.Equal(original.Events.Count, restored.Events.Count);
        Assert.Equal(MouseButton.X1, restored.Events[1].Button);
        Assert.True(restored.Events[3].IsHorizontalWheel);
        Assert.False(restored.Events[5].Enabled);
    }

    [Fact]
    public void DeepClone_DoesNotShareNestedMutableObjects()
    {
        var original = TestMacros.Create();

        var clone = MacroSerializer.Clone(original);
        clone.Events[0].X = 99;
        clone.DisplayLayout!.Monitors[0].DeviceName = "changed";

        Assert.Equal(-1750, original.Events[0].X);
        Assert.Equal(@"\\.\DISPLAY1", original.DisplayLayout!.Monitors[0].DeviceName);
    }

    [Fact]
    public async Task SaveAndLoadAsync_AtomicallyReplacesExistingDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.rloop");
        var original = TestMacros.WithDelays(1);
        var replacement = TestMacros.WithDelays(99, 100);

        await MacroSerializer.SaveAsync(path, original);
        await MacroSerializer.SaveAsync(replacement, path);
        var restored = await MacroSerializer.LoadAsync(path);

        Assert.Equal([99L, 100L], restored.Events.Select(item => item.DelayMicroseconds));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_InvalidReplacementPreservesPreviousFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.rloop");
        await MacroSerializer.SaveAsync(path, TestMacros.WithDelays(10));
        var priorBytes = await File.ReadAllBytesAsync(path);
        var invalid = TestMacros.WithDelays(-1);

        await Assert.ThrowsAsync<MacroValidationException>(() => MacroSerializer.SaveAsync(path, invalid));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_PreCanceledOperationPreservesPreviousFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.rloop");
        await MacroSerializer.SaveAsync(path, TestMacros.WithDelays(10));
        var priorBytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MacroSerializer.SaveAsync(path, TestMacros.WithDelays(20), cancellation.Token));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Deserialize_RejectsMalformedOrWrongRootJson(string json)
    {
        Assert.ThrowsAny<IOException>(() => MacroSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsUnknownProperties()
    {
        var valid = MacroSerializer.Serialize(TestMacros.WithDelays(1));
        var withUnknownProperty = "{\"unexpected\":true," + valid[1..];

        Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(withUnknownProperty));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("createdUtc")]
    [InlineData("displayLayout")]
    [InlineData("events")]
    public void Deserialize_RejectsMissingRequiredRootProperties(string propertyName)
    {
        var root = JsonNode.Parse(MacroSerializer.Serialize(TestMacros.Create()))!.AsObject();
        Assert.True(root.Remove(propertyName));

        Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(root.ToJsonString()));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("delayMicroseconds")]
    [InlineData("enabled")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("button")]
    [InlineData("wheelDelta")]
    [InlineData("isHorizontalWheel")]
    [InlineData("virtualKey")]
    [InlineData("scanCode")]
    [InlineData("isExtendedKey")]
    public void Deserialize_RejectsMissingRequiredEventProperties(string propertyName)
    {
        var root = JsonNode.Parse(MacroSerializer.Serialize(TestMacros.Create()))!.AsObject();
        var firstEvent = root["events"]!.AsArray()[0]!.AsObject();
        Assert.True(firstEvent.Remove(propertyName));

        Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(root.ToJsonString()));
    }

    [Theory]
    [InlineData("virtualLeft")]
    [InlineData("virtualTop")]
    [InlineData("virtualWidth")]
    [InlineData("virtualHeight")]
    [InlineData("monitors")]
    public void Deserialize_RejectsMissingRequiredDisplayProperties(string propertyName)
    {
        var root = JsonNode.Parse(MacroSerializer.Serialize(TestMacros.Create()))!.AsObject();
        var displayLayout = root["displayLayout"]!.AsObject();
        Assert.True(displayLayout.Remove(propertyName));

        Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(root.ToJsonString()));
    }

    [Theory]
    [InlineData("deviceName")]
    [InlineData("left")]
    [InlineData("top")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("dpiX")]
    [InlineData("dpiY")]
    [InlineData("isPrimary")]
    public void Deserialize_RejectsMissingRequiredMonitorProperties(string propertyName)
    {
        var root = JsonNode.Parse(MacroSerializer.Serialize(TestMacros.Create()))!.AsObject();
        var firstMonitor = root["displayLayout"]!["monitors"]!.AsArray()[0]!.AsObject();
        Assert.True(firstMonitor.Remove(propertyName));

        Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void Deserialize_CountsNullEventEntriesBeforeMaterializingTheList()
    {
        var json = CreateNullArrayDocument("events", MacroValidator.MaxEventCount + 1);

        var exception = Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(json));

        Assert.Contains("events", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_CountsNullMonitorEntriesBeforeMaterializingTheList()
    {
        var json = $"{{\"displayLayout\":{{\"monitors\":[{string.Join(',', Enumerable.Repeat("null", MacroValidator.MaxMonitorCount + 1))}]}}}}";

        var exception = Assert.Throws<MacroFormatException>(() => MacroSerializer.Deserialize(json));

        Assert.Contains("monitors", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedVersion()
    {
        var json = MacroSerializer.Serialize(TestMacros.WithDelays(1));
        using var parsed = JsonDocument.Parse(json);
        var unsupported = json.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<MacroValidationException>(() => MacroSerializer.Deserialize(unsupported));
        Assert.Contains(exception.Issues, issue => issue.Path == "$.version");
    }

    [Fact]
    public async Task LoadAsync_RejectsFileLargerThanHardLimitWithoutReadingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.rloop");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(MacroValidator.MaxFileSizeBytes + 1);
        }

        await Assert.ThrowsAsync<MacroFormatException>(() => MacroSerializer.LoadAsync(path));
    }

    private static string CreateNullArrayDocument(string propertyName, int count)
    {
        var builder = new StringBuilder(capacity: checked((count * 5) + 32));
        builder.Append("{\"").Append(propertyName).Append("\":[");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }

            builder.Append("null");
        }

        return builder.Append("]}").ToString();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RelayLoop.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
