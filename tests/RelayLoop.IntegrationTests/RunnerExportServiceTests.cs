using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using RelayLoop.App.Services;
using RelayLoop.Core;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class RunnerExportServiceTests
{
    public const string PublishedStubVerificationVariable = "RELAYLOOP_VERIFY_PUBLISHED_RUNNER";

    [Fact]
    public async Task ExportAsync_AppendsReadablePayload_AndReplacesExistingDestination()
    {
        using TemporaryDirectory temporary = new();
        string stub = Path.Combine(temporary.Path, "stub.exe");
        string destination = Path.Combine(temporary.Path, "My workflow.exe");
        await File.WriteAllBytesAsync(stub, CreatePeLikeStub());
        await File.WriteAllTextAsync(destination, "old destination");

        MacroDocument document = new()
        {
            DisplayLayout = CreateDisplayLayout(),
            Events =
            [
                new MacroEvent
                {
                    Kind = MacroEventKind.KeyDown,
                    DelayMicroseconds = 12_500,
                    VirtualKey = 0x41,
                    ScanCode = 0x1E,
                },
                new MacroEvent
                {
                    Kind = MacroEventKind.KeyUp,
                    DelayMicroseconds = 2_000,
                    VirtualKey = 0x41,
                    ScanCode = 0x1E,
                },
            ],
        };

        RunnerExportResult result = await new RunnerExportService(stub)
            .ExportAsync(document, destination);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(destination, result.OutputPath);
        Assert.Null(result.ErrorMessage);
        MacroDocument roundTrip = await RunnerPayloadCodec.ReadFromExecutableAsync(destination);
        Assert.Equal(2, roundTrip.Events.Count);
        Assert.Equal(12_500, roundTrip.Events[0].DelayMicroseconds);
        Assert.Equal(0x41, roundTrip.Events[0].VirtualKey);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(temporary.Path),
            path => Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportAsync_MissingExplicitStub_ReturnsHelpfulError_AndPreservesDestination()
    {
        using TemporaryDirectory temporary = new();
        string missingStub = Path.Combine(temporary.Path, "missing-stub.exe");
        string destination = Path.Combine(temporary.Path, "existing.exe");
        byte[] original = [7, 8, 9, 10];
        await File.WriteAllBytesAsync(destination, original);

        RunnerExportResult result = await new RunnerExportService(missingStub)
            .ExportAsync(new MacroDocument(), destination);

        Assert.False(result.Success);
        Assert.Null(result.OutputPath);
        Assert.Contains("stub is missing", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ExportAsync_InvalidStub_ReturnsError_AndPreservesDestination()
    {
        using TemporaryDirectory temporary = new();
        string stub = Path.Combine(temporary.Path, "not-an-executable.exe");
        string destination = Path.Combine(temporary.Path, "existing.exe");
        byte[] original = [1, 2, 3];
        byte[] malformedPe = new byte[512];
        malformedPe[0] = (byte)'M';
        malformedPe[1] = (byte)'Z';
        await File.WriteAllBytesAsync(stub, malformedPe);
        await File.WriteAllBytesAsync(destination, original);

        RunnerExportResult result = await new RunnerExportService(stub)
            .ExportAsync(new MacroDocument(), destination);

        Assert.False(result.Success);
        Assert.Contains("PE header", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ExportAsync_NonX64Stub_ReturnsError_AndPreservesDestination()
    {
        using TemporaryDirectory temporary = new();
        string stub = Path.Combine(temporary.Path, "x86-stub.exe");
        string destination = Path.Combine(temporary.Path, "existing.exe");
        byte[] original = [31, 32, 33];
        await File.WriteAllBytesAsync(stub, CreatePeLikeStub(machine: 0x014C));
        await File.WriteAllBytesAsync(destination, original);

        RunnerExportResult result = await new RunnerExportService(stub)
            .ExportAsync(new MacroDocument(), destination);

        Assert.False(result.Success);
        Assert.Contains("x64", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ExportAsync_InvalidMacro_ReturnsError_AndPreservesDestination()
    {
        using TemporaryDirectory temporary = new();
        string stub = Path.Combine(temporary.Path, "stub.exe");
        string destination = Path.Combine(temporary.Path, "existing.exe");
        byte[] original = [11, 12, 13];
        await File.WriteAllBytesAsync(stub, CreatePeLikeStub());
        await File.WriteAllBytesAsync(destination, original);
        MacroDocument invalid = new()
        {
            Events = [new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0 }],
        };

        RunnerExportResult result = await new RunnerExportService(stub)
            .ExportAsync(invalid, destination);

        Assert.False(result.Success);
        Assert.Contains("cannot be exported", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ExportAsync_PreCanceledToken_DoesNotTouchDestination()
    {
        using TemporaryDirectory temporary = new();
        string stub = Path.Combine(temporary.Path, "stub.exe");
        string destination = Path.Combine(temporary.Path, "existing.exe");
        byte[] original = [21, 22, 23];
        await File.WriteAllBytesAsync(stub, CreatePeLikeStub());
        await File.WriteAllBytesAsync(destination, original);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        RunnerExportResult result = await new RunnerExportService(stub)
            .ExportAsync(new MacroDocument(), destination, cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("canceled", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task PublishedStub_ExportsReadableRunner_AndOpensVisibleConfirmation_WhenRequested()
    {
        string? publishedStub = Environment.GetEnvironmentVariable(PublishedStubVerificationVariable);
        if (string.IsNullOrWhiteSpace(publishedStub))
        {
            // Normal test runs do not depend on a previously published artifact. The release
            // verification pass sets the variable to exercise the actual packaged stub.
            return;
        }

        Assert.True(File.Exists(publishedStub), $"Published runner stub not found: {publishedStub}");
        using TemporaryDirectory temporary = new();
        string destination = Path.Combine(temporary.Path, "RelayLoop safe verification.exe");
        MacroDocument safeDocument = new()
        {
            DisplayLayout = CreateDisplayLayout(),
            Events =
            [
                new MacroEvent
                {
                    Kind = MacroEventKind.MouseMove,
                    Enabled = false,
                    X = -25,
                    Y = 25,
                },
            ],
        };

        RunnerExportResult export = await new RunnerExportService(publishedStub)
            .ExportAsync(safeDocument, destination);

        Assert.True(export.Success, export.ErrorMessage);
        MacroDocument embedded = await RunnerPayloadCodec.ReadFromExecutableAsync(destination);
        Assert.Single(embedded.Events);
        Assert.False(embedded.Events[0].Enabled);

        using Process process = Process.Start(new ProcessStartInfo(destination)
        {
            UseShellExecute = false,
            WorkingDirectory = temporary.Path,
        }) ?? throw new InvalidOperationException("The exported runner process could not be started.");

        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(20))
            {
                process.Refresh();
                if (process.MainWindowHandle != 0 &&
                    process.MainWindowTitle.Contains("RelayLoop Runner", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(50);
            }

            process.Refresh();
            Assert.False(process.HasExited);
            Assert.NotEqual(0, process.MainWindowHandle);
            Assert.Contains("RelayLoop Runner", process.MainWindowTitle, StringComparison.Ordinal);
            Assert.True(process.CloseMainWindow(), "The exported runner confirmation window did not accept a normal close request.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                _ = process.CloseMainWindow();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
    }

    private static byte[] CreatePeLikeStub(ushort machine = 0x8664)
    {
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        const int peOffset = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, sizeof(int)), peOffset);
        bytes[peOffset] = (byte)'P';
        bytes[peOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 4, sizeof(ushort)), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 20, sizeof(ushort)), 0x00F0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 24, sizeof(ushort)), 0x020B);
        return bytes;
    }

    private static DisplayLayout CreateDisplayLayout() => new()
    {
        VirtualLeft = 0,
        VirtualTop = 0,
        VirtualWidth = 1920,
        VirtualHeight = 1080,
        Monitors =
        [
            new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY1",
                Left = 0,
                Top = 0,
                Width = 1920,
                Height = 1080,
                DpiX = 96,
                DpiY = 96,
                IsPrimary = true,
            },
        ],
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RelayLoop.RunnerTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
