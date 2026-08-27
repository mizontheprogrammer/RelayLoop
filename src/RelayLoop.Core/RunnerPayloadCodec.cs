using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RelayLoop.Core;

public sealed class RunnerPayloadException : IOException
{
    public RunnerPayloadException(string message)
        : base(message)
    {
    }

    public RunnerPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Appends a validated macro to a runner executable. The final fixed-size footer contains a magic
/// value, codec version, payload length, and SHA-256 digest, so a runner can safely locate and
/// authenticate its own payload without interpreting the executable image.
/// </summary>
public static class RunnerPayloadCodec
{
    public const int PayloadFormatVersion = 1;

    private const int HashLength = 32;
    private const int LengthFieldLength = sizeof(long);
    private const int VersionFieldLength = sizeof(int);
    private const int BufferSize = 64 * 1024;
    private static ReadOnlySpan<byte> FooterMagic => "RLOOP_PAYLOAD_V1"u8;
    private static int FooterLength => HashLength + LengthFieldLength + VersionFieldLength + FooterMagic.Length;

    public static async Task AppendPayloadAsync(
        Stream destination,
        MacroDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = MacroSerializer.SerializeToUtf8Bytes(document);
        var hash = SHA256.HashData(payload);
        var footer = new byte[FooterLength];
        hash.CopyTo(footer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            footer.AsSpan(HashLength, LengthFieldLength),
            payload.LongLength);
        BinaryPrimitives.WriteInt32LittleEndian(
            footer.AsSpan(HashLength + LengthFieldLength, VersionFieldLength),
            PayloadFormatVersion);
        FooterMagic.CopyTo(footer.AsSpan(HashLength + LengthFieldLength + VersionFieldLength));

        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
    }

    public static async Task AppendToExecutableAsync(
        string stubPath,
        string outputPath,
        MacroDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stubPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        // Serialize before creating a temp file, preserving any previous output on invalid input.
        MacroValidator.Validate(document, cancellationToken);
        var fullStubPath = Path.GetFullPath(stubPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The output must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var input = new FileStream(
                fullStubPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                await AppendPayloadAsync(output, document, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullOutputPath))
            {
                File.Replace(temporaryPath, fullOutputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullOutputPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public static async Task<MacroDocument> ReadFromExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        await using var stream = new FileStream(
            Path.GetFullPath(executablePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MacroDocument> ReadPayloadAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("Source stream must be readable and seekable.", nameof(source));
        }

        if (source.Length < FooterLength)
        {
            throw new RunnerPayloadException("Runner payload footer is missing.");
        }

        var footer = new byte[FooterLength];
        source.Seek(-FooterLength, SeekOrigin.End);
        await source.ReadExactlyAsync(footer, cancellationToken).ConfigureAwait(false);
        var metadata = ParseFooter(footer, source.Length);

        source.Position = metadata.PayloadOffset;
        var payload = new byte[metadata.PayloadLength];
        await source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        VerifyHash(payload, metadata.ExpectedHash);

        try
        {
            return MacroSerializer.Deserialize(payload);
        }
        catch (IOException exception) when (exception is MacroFormatException or MacroValidationException)
        {
            throw new RunnerPayloadException("Runner macro payload is invalid.", exception);
        }
    }

    public static bool TryReadFromExecutable(
        string executablePath,
        [NotNullWhen(true)] out MacroDocument? document,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            document = ReadFromExecutableAsync(executablePath).GetAwaiter().GetResult();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidDataException)
        {
            document = null;
            error = exception.Message;
            return false;
        }
    }

    public static bool TryReadPayload(
        Stream source,
        [NotNullWhen(true)] out MacroDocument? document,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            document = ReadPayloadAsync(source).GetAwaiter().GetResult();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidDataException)
        {
            document = null;
            error = exception.Message;
            return false;
        }
    }

    private static PayloadMetadata ParseFooter(ReadOnlySpan<byte> footer, long streamLength)
    {
        var magicOffset = HashLength + LengthFieldLength + VersionFieldLength;
        if (!footer[magicOffset..].SequenceEqual(FooterMagic))
        {
            throw new RunnerPayloadException("Runner payload signature is missing.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(
            footer.Slice(HashLength + LengthFieldLength, VersionFieldLength));
        if (version != PayloadFormatVersion)
        {
            throw new RunnerPayloadException($"Runner payload version {version} is not supported.");
        }

        var payloadLengthLong = BinaryPrimitives.ReadInt64LittleEndian(
            footer.Slice(HashLength, LengthFieldLength));
        if (payloadLengthLong is < 1 or > MacroValidator.MaxFileSizeBytes)
        {
            throw new RunnerPayloadException("Runner payload length is outside the allowed range.");
        }

        var payloadOffset = streamLength - FooterLength - payloadLengthLong;
        if (payloadOffset < 0)
        {
            throw new RunnerPayloadException("Runner payload length exceeds the file size.");
        }

        return new(
            payloadOffset,
            checked((int)payloadLengthLong),
            footer[..HashLength].ToArray());
    }

    private static void VerifyHash(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expectedHash)
    {
        var actualHash = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new RunnerPayloadException("Runner payload checksum does not match.");
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
        catch (IOException)
        {
            // Best effort: preserve the exception that caused the export to fail.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: preserve the exception that caused the export to fail.
        }
    }

    private sealed record PayloadMetadata(long PayloadOffset, int PayloadLength, byte[] ExpectedHash);
}
