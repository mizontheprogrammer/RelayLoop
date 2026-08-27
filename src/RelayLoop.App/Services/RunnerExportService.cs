using System.Buffers.Binary;
using System.IO;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

/// <summary>Creates a portable runner by appending a validated macro to the published runner stub.</summary>
public sealed class RunnerExportService
{
    public const string StubEnvironmentVariable = "RELAYLOOP_RUNNER_STUB";
    public const string RunnerFileName = "RelayLoop.Runner.exe";

    private readonly string? _runnerStubPath;

    public RunnerExportService(string? runnerStubPath = null)
    {
        _runnerStubPath = string.IsNullOrWhiteSpace(runnerStubPath)
            ? null
            : Path.GetFullPath(runnerStubPath);
    }

    /// <summary>
    /// Exports <paramref name="document"/> without exposing a partial destination file. Existing
    /// destinations are replaced only after the complete payload has been written and read back.
    /// </summary>
    public async Task<RunnerExportResult> ExportAsync(
        MacroDocument document,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return RunnerExportResult.Failed("Choose a destination for the exported runner.");
        }

        string destination;
        try
        {
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return RunnerExportResult.Failed($"The export destination is not valid. {exception.Message}");
        }

        if (!string.Equals(Path.GetExtension(destination), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return RunnerExportResult.Failed("Standalone runners must be saved with the .exe extension.");
        }

        string? stub = ResolveRunnerStubPath(_runnerStubPath);
        if (stub is null)
        {
            return RunnerExportResult.Failed(BuildMissingStubMessage());
        }

        if (string.Equals(stub, destination, StringComparison.OrdinalIgnoreCase))
        {
            return RunnerExportResult.Failed(
                "Choose a destination other than the runner stub; the published stub must remain unchanged for future exports.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return RunnerExportResult.Failed("The selected export folder does not exist.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateStubAsync(stub, cancellationToken).ConfigureAwait(false);

            // RunnerPayloadCodec serializes and validates the document before its footer is committed.
            await RunnerPayloadCodec.AppendToExecutableAsync(
                stub,
                temporaryPath,
                document,
                cancellationToken).ConfigureAwait(false);

            // Read-back catches truncation, a corrupt footer, and hash mismatches before the old
            // destination is touched.
            _ = await RunnerPayloadCodec.ReadFromExecutableAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            CommitAtomically(temporaryPath, destination);
            return RunnerExportResult.Succeeded(destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RunnerExportResult.Failed("Runner export was canceled.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return RunnerExportResult.Failed(
                $"RelayLoop cannot write the runner at that location. Choose a folder you can write to. {exception.Message}");
        }
        catch (MacroValidationException exception)
        {
            return RunnerExportResult.Failed($"The loaded macro cannot be exported. {exception.Message}");
        }
        catch (RunnerPayloadException exception)
        {
            return RunnerExportResult.Failed($"The exported runner failed validation. {exception.Message}");
        }
        catch (IOException exception)
        {
            return RunnerExportResult.Failed($"The runner could not be exported. {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return RunnerExportResult.Failed($"The loaded macro cannot be exported. {exception.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <summary>Returns the first existing configured or packaged runner stub.</summary>
    public static string? ResolveRunnerStubPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            try
            {
                string configuredPath = Path.GetFullPath(explicitPath);
                return File.Exists(configuredPath) ? configuredPath : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        foreach (string candidate in GetRunnerStubCandidates(explicitPath))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Continue to packaged defaults. ExportAsync returns a useful aggregate error if none exist.
            }
        }

        return null;
    }

    public static IReadOnlyList<string> GetRunnerStubCandidates(string? explicitPath = null)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        string? environmentPath = Environment.GetEnvironmentVariable(StubEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            candidates.Add(environmentPath);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "RunnerStub", RunnerFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, RunnerFileName));
        return candidates;
    }

    private static async Task ValidateStubAsync(string stubPath, CancellationToken cancellationToken)
    {
        const int DosHeaderLength = 64;
        const int PeOffsetField = 0x3C;
        const int PeHeaderLength = 24;
        const ushort Amd64Machine = 0x8664;
        const ushort Pe32PlusMagic = 0x020B;

        FileInfo info = new(stubPath);
        if (info.Length < DosHeaderLength)
        {
            throw new InvalidDataException("The packaged runner stub is empty or truncated.");
        }

        byte[] dosHeader = new byte[DosHeaderLength];
        await using FileStream stream = new(
            stubPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(dosHeader, cancellationToken).ConfigureAwait(false);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException(
                "The configured runner stub is not a valid Windows executable. Publish RelayLoop.Runner for win-x64 and try again.");
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.AsSpan(PeOffsetField, sizeof(int)));
        if (peOffset < DosHeaderLength || (long)peOffset + PeHeaderLength + sizeof(ushort) > info.Length)
        {
            throw new InvalidDataException("The configured runner stub has an invalid or truncated PE header.");
        }

        stream.Position = peOffset;
        byte[] peHeader = new byte[PeHeaderLength];
        await stream.ReadExactlyAsync(peHeader, cancellationToken).ConfigureAwait(false);
        if (peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
        {
            throw new InvalidDataException("The configured runner stub does not contain a valid PE signature.");
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.AsSpan(4, sizeof(ushort)));
        ushort optionalHeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.AsSpan(20, sizeof(ushort)));
        if (machine != Amd64Machine)
        {
            throw new InvalidDataException("The configured runner stub is not a Windows x64 executable.");
        }

        if (optionalHeaderLength < sizeof(ushort) || (long)peOffset + PeHeaderLength + optionalHeaderLength > info.Length)
        {
            throw new InvalidDataException("The configured runner stub has a truncated optional header.");
        }

        byte[] optionalMagic = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(optionalMagic, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt16LittleEndian(optionalMagic) != Pe32PlusMagic)
        {
            throw new InvalidDataException("The configured runner stub is not a 64-bit PE32+ executable.");
        }
    }

    private static void CommitAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static string BuildMissingStubMessage()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "RunnerStub", RunnerFileName);
        return
            $"The portable runner stub is missing. Expected '{expected}'. " +
            "Publish RelayLoop.Runner as a self-contained win-x64 single-file executable, then place " +
            $"{RunnerFileName} in the RunnerStub folder. Developers may also set {StubEnvironmentVariable}.";
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
        catch (IOException)
        {
            // A failed cleanup must not replace or damage an existing destination.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above; the randomly named file can be removed manually.
        }
    }
}

public sealed record RunnerExportResult(bool Success, string? OutputPath, string? ErrorMessage)
{
    internal static RunnerExportResult Succeeded(string outputPath) => new(true, outputPath, null);

    internal static RunnerExportResult Failed(string errorMessage) => new(false, null, errorMessage);
}
