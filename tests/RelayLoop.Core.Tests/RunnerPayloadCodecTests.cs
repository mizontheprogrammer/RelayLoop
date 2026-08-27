using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class RunnerPayloadCodecTests
{
    [Fact]
    public async Task AppendAndReadPayload_RoundTripsAndPreservesExecutablePrefix()
    {
        using var stream = new MemoryStream();
        var executablePrefix = new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5 };
        await stream.WriteAsync(executablePrefix);
        var document = TestMacros.Create();

        await RunnerPayloadCodec.AppendPayloadAsync(stream, document);
        var completeBytes = stream.ToArray();
        stream.Position = 0;
        var restored = await RunnerPayloadCodec.ReadPayloadAsync(stream);

        Assert.Equal(executablePrefix, completeBytes[..executablePrefix.Length]);
        Assert.Equal(document.Events.Count, restored.Events.Count);
        Assert.Equal(document.Events[3].WheelDelta, restored.Events[3].WheelDelta);
    }

    [Fact]
    public async Task AppendToExecutableAsync_AtomicallyCreatesRunnableImageWithPayload()
    {
        using var directory = new TemporaryDirectory();
        var stub = Path.Combine(directory.Path, "stub.exe");
        var output = Path.Combine(directory.Path, "macro.exe");
        var prefix = new byte[] { 0x4D, 0x5A, 9, 8, 7 };
        await File.WriteAllBytesAsync(stub, prefix);

        await RunnerPayloadCodec.AppendToExecutableAsync(stub, output, TestMacros.WithDelays(123));

        Assert.Equal(prefix, (await File.ReadAllBytesAsync(output))[..prefix.Length]);
        var restored = await RunnerPayloadCodec.ReadFromExecutableAsync(output);
        Assert.Equal(123, restored.Events.Single().DelayMicroseconds);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ReadPayloadAsync_RejectsTamperingUsingChecksum()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(new byte[] { 0x4D, 0x5A });
        await RunnerPayloadCodec.AppendPayloadAsync(stream, TestMacros.WithDelays(100));
        var bytes = stream.ToArray();
        bytes[3] ^= 0x5A;
        using var tampered = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<RunnerPayloadException>(
            () => RunnerPayloadCodec.ReadPayloadAsync(tampered));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadPayload_ReturnsUsefulErrorForMissingPayload()
    {
        using var stream = new MemoryStream([0x4D, 0x5A]);

        var succeeded = RunnerPayloadCodec.TryReadPayload(stream, out var document, out var error);

        Assert.False(succeeded);
        Assert.Null(document);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task AppendToExecutableAsync_InvalidReplacementPreservesPriorOutput()
    {
        using var directory = new TemporaryDirectory();
        var stub = Path.Combine(directory.Path, "stub.exe");
        var output = Path.Combine(directory.Path, "macro.exe");
        await File.WriteAllBytesAsync(stub, [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(output, [1, 2, 3]);
        var invalid = TestMacros.WithDelays(-1);

        await Assert.ThrowsAsync<MacroValidationException>(
            () => RunnerPayloadCodec.AppendToExecutableAsync(stub, output, invalid));

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(output));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RelayLoop.PayloadTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
