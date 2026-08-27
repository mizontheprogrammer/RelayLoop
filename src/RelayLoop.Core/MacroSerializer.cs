using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLoop.Core;

public sealed class MacroFormatException : IOException
{
    public MacroFormatException(string message)
        : base(message)
    {
    }

    public MacroFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads and atomically writes the versioned RelayLoop JSON format.</summary>
public static class MacroSerializer
{
    private const int BufferSize = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static JsonSerializerOptions JsonOptions => new(SerializerOptions);

    public static string Serialize(MacroDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MacroValidator.Validate(document);
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MacroValidator.MaxFileSizeBytes)
        {
            throw new MacroValidationException(
                [new("$", $"Serialized macro exceeds {MacroValidator.MaxFileSizeBytes} bytes.")]);
        }

        return json;
    }

    public static byte[] SerializeToUtf8Bytes(MacroDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MacroValidator.Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        if (bytes.LongLength > MacroValidator.MaxFileSizeBytes)
        {
            throw new MacroValidationException(
                [new("$", $"Serialized macro exceeds {MacroValidator.MaxFileSizeBytes} bytes.")]);
        }

        return bytes;
    }

    public static MacroDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MacroValidator.MaxFileSizeBytes)
        {
            throw new MacroFormatException($"Macro data exceeds the {MacroValidator.MaxFileSizeBytes}-byte limit.");
        }

        return Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static MacroDocument Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MacroValidator.MaxFileSizeBytes)
        {
            throw new MacroFormatException($"Macro data exceeds the {MacroValidator.MaxFileSizeBytes}-byte limit.");
        }

        try
        {
            EnsureCollectionCountsAreBounded(utf8Json);
            var document = JsonSerializer.Deserialize<MacroDocument>(utf8Json, SerializerOptions)
                ?? throw new MacroFormatException("Macro data contains a null document.");
            MacroValidator.Validate(document);
            return document;
        }
        catch (MacroFormatException)
        {
            throw;
        }
        catch (MacroValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new MacroFormatException("Macro data is not valid RelayLoop JSON.", exception);
        }
    }

    public static MacroDocument Clone(MacroDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MacroValidator.Validate(document);
        return document.DeepClone();
    }

    public static MacroDocument Load(string path) => LoadAsync(path).GetAwaiter().GetResult();

    public static async Task<MacroDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > MacroValidator.MaxFileSizeBytes)
        {
            throw new MacroFormatException($"Macro file exceeds the {MacroValidator.MaxFileSizeBytes}-byte limit.");
        }

        var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        return Deserialize(bytes);
    }

    public static void Save(string path, MacroDocument document) =>
        SaveAsync(path, document).GetAwaiter().GetResult();

    public static Task SaveAsync(
        MacroDocument document,
        string path,
        CancellationToken cancellationToken = default) => SaveAsync(path, document, cancellationToken);

    public static async Task SaveAsync(
        string path,
        MacroDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        // Validation and serialization happen before touching the destination.
        var bytes = SerializeToUtf8Bytes(document);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var initialCapacity = stream.CanSeek
            ? (int)Math.Min(stream.Length, MacroValidator.MaxFileSizeBytes)
            : 0;
        using var destination = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long totalRead = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > MacroValidator.MaxFileSizeBytes)
                {
                    throw new MacroFormatException($"Macro data exceeds the {MacroValidator.MaxFileSizeBytes}-byte limit.");
                }

                destination.Write(buffer, 0, read);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureCollectionCountsAreBounded(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = SerializerOptions.MaxDepth,
            });

        var awaitingEventsValue = false;
        var awaitingMonitorsValue = false;
        var eventsArrayDepth = -1;
        var monitorsArrayDepth = -1;
        var eventCount = 0;
        var monitorCount = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == 1 &&
                reader.ValueTextEquals("events"u8))
            {
                awaitingEventsValue = true;
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == 2 &&
                reader.ValueTextEquals("monitors"u8))
            {
                awaitingMonitorsValue = true;
                continue;
            }

            if (awaitingEventsValue)
            {
                awaitingEventsValue = false;
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    eventsArrayDepth = reader.CurrentDepth;
                }
            }

            if (awaitingMonitorsValue)
            {
                awaitingMonitorsValue = false;
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    monitorsArrayDepth = reader.CurrentDepth;
                }
            }

            if (eventsArrayDepth >= 0 && IsDirectArrayElement(reader, eventsArrayDepth))
            {
                eventCount++;
                if (eventCount > MacroValidator.MaxEventCount)
                {
                    throw new MacroFormatException($"Macro contains more than {MacroValidator.MaxEventCount:N0} events.");
                }
            }

            if (monitorsArrayDepth >= 0 && IsDirectArrayElement(reader, monitorsArrayDepth))
            {
                monitorCount++;
                if (monitorCount > MacroValidator.MaxMonitorCount)
                {
                    throw new MacroFormatException($"Macro contains more than {MacroValidator.MaxMonitorCount:N0} monitors.");
                }
            }

            if (eventsArrayDepth >= 0 &&
                reader.TokenType == JsonTokenType.EndArray &&
                reader.CurrentDepth == eventsArrayDepth)
            {
                eventsArrayDepth = -1;
            }

            if (monitorsArrayDepth >= 0 &&
                reader.TokenType == JsonTokenType.EndArray &&
                reader.CurrentDepth == monitorsArrayDepth)
            {
                monitorsArrayDepth = -1;
            }
        }
    }

    private static bool IsDirectArrayElement(Utf8JsonReader reader, int arrayDepth) =>
        reader.CurrentDepth == arrayDepth + 1 && reader.TokenType is
            JsonTokenType.StartObject or
            JsonTokenType.StartArray or
            JsonTokenType.String or
            JsonTokenType.Number or
            JsonTokenType.True or
            JsonTokenType.False or
            JsonTokenType.Null;

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
            // A failed best-effort cleanup must not hide the original save failure.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed best-effort cleanup must not hide the original save failure.
        }
    }
}
