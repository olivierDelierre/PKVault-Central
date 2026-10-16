using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace PKVault.Core;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceTransferState>))]
public enum DeviceTransferState
{
    /// <summary>Device save not uploaded yet (or the upload may be incomplete).</summary>
    UPLOADING,
    /// <summary>Save opened on the server, waiting for edits & commit.</summary>
    UPLOADED,
    /// <summary>A commit was sent: the server may have recorded a delivery.</summary>
    COMMIT_REQUESTED,
    /// <summary>The delivery exists on the server, to download.</summary>
    COMMITTED_ON_SERVER,
    /// <summary>New save downloaded and verified, stored locally.</summary>
    DOWNLOADED,
    /// <summary>Writing the device save. It can be partially written in this state.</summary>
    WRITING,
    /// <summary>Device save written and verified, ack not sent yet.</summary>
    WRITTEN,
    /// <summary>Done.</summary>
    ACKED,
    /// <summary>Nothing to deliver anymore.</summary>
    CLOSED,
    /// <summary>Needs a choice from the user, nothing is written meanwhile.</summary>
    CONFLICT,
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceConflictReason>))]
public enum DeviceConflictReason
{
    /// <summary>The device save is neither the uploaded one nor the new one (game played since).</summary>
    DEVICE_SAVE_CHANGED,
    DEVICE_SAVE_MISSING,
    /// <summary>The new save could not be written (the previous content was put back).</summary>
    WRITE_FAILED,
    /// <summary>The server does not know the delivery anymore (ex: its backup was restored).</summary>
    DELIVERY_GONE,
}

public enum TransferBlob
{
    /// <summary>Exact bytes uploaded.</summary>
    Original,
    /// <summary>Bytes delivered by the server, hash verified.</summary>
    New,
    /// <summary>What was on the device just before writing the new save.</summary>
    DeviceBeforeWrite,
}

public class TransferJournalEntry
{
    public required string LocalId { get; init; }
    public required string DeviceSavePath { get; init; }
    public required string FileName { get; init; }
    public required DateTime CreatedAt { get; init; }

    public DeviceTransferState State { get; set; }
    public DateTime UpdatedAt { get; set; }

    public uint? SaveId { get; set; }
    public string OriginalHash { get; set; } = "";
    public string? TransferId { get; set; }
    public string? NewHash { get; set; }

    public DeviceConflictReason? ConflictReason { get; set; }
    public string? DeviceHashAtConflict { get; set; }

    /// <summary>Last error met while advancing, the state did not change. Cleared on progress.</summary>
    public string? LastError { get; set; }

    /// <summary>Something to know about a finished transfer, content is then never purged.</summary>
    public string? Warning { get; set; }

    /// <summary>The device save changed: the local save list has to be reloaded.</summary>
    public bool LocalReloadPending { get; set; }

    public bool IsTerminal => State is DeviceTransferState.ACKED or DeviceTransferState.CLOSED;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TransferJournalEntry))]
public partial class RemoteDeviceJsonContext : JsonSerializerContext;

/// <summary>
/// Durable record of what is going on between this device and the server: one JSON file per transfer
/// plus the staged contents. Files are replaced atomically, so a stop at any moment leaves either
/// the previous or the new version.
/// It lives in the app private storage: plain file system, not the (possibly SAF) <see cref="IFileIOService"/>.
/// </summary>
public class TransferJournal(IFileSystem fileSystem, string directory, TimeProvider timeProvider)
{
    public IReadOnlyList<TransferJournalEntry> LoadAll()
    {
        if (!fileSystem.Directory.Exists(directory))
        {
            return [];
        }

        List<TransferJournalEntry> entries = [];
        foreach (var path in fileSystem.Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize(
                    fileSystem.File.ReadAllBytes(path), RemoteDeviceJsonContext.Default.TransferJournalEntry
                );
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                // never delete it: it may be the only record of a delivery
                Log.Error(ex, $"Cannot read transfer journal entry {path}");
            }
        }

        return [.. entries.OrderBy(e => e.CreatedAt).ThenBy(e => e.LocalId)];
    }

    public TransferJournalEntry? Get(string localId)
        => LoadAll().FirstOrDefault(e => e.LocalId == localId);

    public async Task Save(TransferJournalEntry entry)
    {
        entry.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await WriteAtomic(
            Path.Combine(directory, $"{entry.LocalId}.json"),
            JsonSerializer.SerializeToUtf8Bytes(entry, RemoteDeviceJsonContext.Default.TransferJournalEntry)
        );
    }

    /// <summary>
    /// Removes a transfer and its contents. The entry goes last: if this stops halfway, it is
    /// still listed and removed again later, instead of leaving contents nobody knows about.
    /// </summary>
    public void Delete(string localId)
    {
        foreach (var blob in Enum.GetValues<TransferBlob>())
        {
            var path = GetBlobPath(localId, blob);
            if (fileSystem.File.Exists(path))
            {
                fileSystem.File.Delete(path);
            }
        }

        var entryPath = Path.Combine(directory, $"{localId}.json");
        if (fileSystem.File.Exists(entryPath))
        {
            fileSystem.File.Delete(entryPath);
        }
    }

    public Task WriteBlob(string localId, TransferBlob blob, byte[] bytes)
        => WriteAtomic(GetBlobPath(localId, blob), bytes);

    public bool HasBlob(string localId, TransferBlob blob)
        => fileSystem.File.Exists(GetBlobPath(localId, blob));

    public async Task<byte[]?> ReadBlob(string localId, TransferBlob blob)
    {
        var path = GetBlobPath(localId, blob);
        return fileSystem.File.Exists(path) ? await fileSystem.File.ReadAllBytesAsync(path) : null;
    }

    public string GetBlobPath(string localId, TransferBlob blob) => Path.Combine(directory, blob switch
    {
        TransferBlob.Original => $"{localId}.original.bin",
        TransferBlob.New => $"{localId}.new.bin",
        TransferBlob.DeviceBeforeWrite => $"{localId}.device-before-write.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(blob)),
    });

    private async Task WriteAtomic(string path, byte[] bytes)
    {
        fileSystem.Directory.CreateDirectory(directory);

        var tmpPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = fileSystem.File.Create(tmpPath))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(true);
            }

            fileSystem.File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (fileSystem.File.Exists(tmpPath))
                {
                    fileSystem.File.Delete(tmpPath);
                }
            }
            catch (Exception cleanupEx)
            {
                Log.Warning(cleanupEx, $"Cannot clean temp file {tmpPath}");
            }

            throw;
        }
    }
}
