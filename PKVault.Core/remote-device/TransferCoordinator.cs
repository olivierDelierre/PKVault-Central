using System.Text.Json.Serialization;
using Serilog;

namespace PKVault.Core;

public interface ILocalSavesRefresher
{
    /// <summary>
    /// Reloads the device saves after one was rewritten.
    /// Returns false if it could not be done now (ex: pending local actions), it will be retried.
    /// </summary>
    public Task<bool> TryRefresh();
}

/// <summary>
/// Saves of the device which must not be written by anything but the transfer in progress.
/// </summary>
public interface IDeviceSaveLock
{
    public bool IsLocked(string savePath);
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferConflictResolution>))]
public enum TransferConflictResolution
{
    /// <summary>Cancel on the server: the bank goes back to before the commit, the device save is not touched.</summary>
    ABORT_ON_SERVER,
    /// <summary>Write the delivered save anyway. What was on the device is kept in the journal.</summary>
    OVERWRITE_DEVICE,
    /// <summary>Forget a delivery the server does not have anymore.</summary>
    DISCARD,
}

/// <summary>
/// Moves a save through its life between the device and the server, one step at a time, each step
/// being recorded in the <see cref="TransferJournal"/> before the next one starts, so it can resume from
/// wherever it stopped (app killed, network lost, response lost...).
///
/// Rules keeping Pokémon safe:
/// - the delivered save is stored & hash-verified locally before the device save is touched;
/// - the device save is only written if it still is the one that was uploaded (otherwise: conflict);
/// - what was on the device before writing is kept, and put back if the write cannot be completed;
/// - the server is told (ack) only after the written save has been read back and verified;
/// - nothing is deleted from the journal here.
/// </summary>
public class TransferCoordinator(
    IRemoteApiClient api, TransferJournal journal, IFileIOService fileIO,
    IRemoteDeviceStore store, ILocalSavesRefresher localRefresher, TimeProvider timeProvider
) : IDeviceSaveLock
{
    private const int WRITE_ATTEMPTS = 3;

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Finished transfers keep their contents this long, to recover from any late problem
    /// (the server keeps the delivered content as long, by default).
    /// </summary>
    public TimeSpan JournalRetention { get; set; } = TimeSpan.FromDays(30);

    public IReadOnlyList<TransferJournalEntry> GetEntries() => journal.LoadAll();

    /// <summary>
    /// A save which is being edited on the server, or exchanged with it, must not be written by the local bank:
    /// the server would deliver a save which erases that write.
    /// </summary>
    public bool IsLocked(string savePath)
        => journal.LoadAll().Any(e => !e.IsTerminal && e.DeviceSavePath == savePath);

    /// <summary>
    /// Uploads the device save so it can be edited on the server.
    /// Returns the existing transfer if this save already has one in progress.
    /// </summary>
    public async Task<TransferJournalEntry> StartUpload(string deviceSavePath, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var existing = journal.LoadAll().FirstOrDefault(e => e.DeviceSavePath == deviceSavePath && !e.IsTerminal);
            if (existing != null)
            {
                await Advance(existing, ct);
                return existing;
            }

            if (!fileIO.Exists(deviceSavePath))
            {
                throw new FileNotFoundException($"Device save not found: {deviceSavePath}");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var entry = new TransferJournalEntry
            {
                LocalId = Guid.NewGuid().ToString("N"),
                DeviceSavePath = deviceSavePath,
                FileName = Path.GetFileName(deviceSavePath),
                CreatedAt = now,
                State = DeviceTransferState.UPLOADING,
            };
            await journal.Save(entry);

            await Advance(entry, ct);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// To call right before a commit is sent to the server.
    /// From now on a delivery may exist, even if the answer never comes back.
    /// </summary>
    public async Task BeginCommit(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in journal.LoadAll().Where(e => e.State == DeviceTransferState.UPLOADED))
            {
                await Move(entry, DeviceTransferState.COMMIT_REQUESTED);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// To call after the commit request ended.
    /// </summary>
    /// <param name="responseReceived">
    /// True if the server answered successfully. A save without delivery then had nothing to deliver,
    /// and goes back to waiting for edits. If the answer is unknown, it stays "maybe committed".
    /// </param>
    public async Task EndCommit(bool responseReceived, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in journal.LoadAll().Where(e => e.State == DeviceTransferState.COMMIT_REQUESTED))
            {
                try
                {
                    if (await StepFindDelivery(entry, ct))
                    {
                        await Advance(entry, ct);
                    }
                    else if (responseReceived)
                    {
                        await Move(entry, DeviceTransferState.UPLOADED);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RecordError(entry, ex);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves every transfer forward as far as possible. Safe to call any time, as often as wanted:
    /// at app start, when the connection comes back, after a commit.
    /// </summary>
    public async Task Resume(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in journal.LoadAll())
            {
                if (!entry.IsTerminal)
                {
                    await Advance(entry, ct);
                }
                else if (entry.LocalReloadPending)
                {
                    await TryReloadLocal(entry);
                }
            }

            PurgeFinished();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes finished transfers (and their contents) older than <see cref="JournalRetention"/>.
    /// Never one with a warning, one waiting for a decision, or one which did not finish.
    /// </summary>
    public int PurgeFinished()
    {
        var limit = timeProvider.GetUtcNow().UtcDateTime - JournalRetention;
        var purged = 0;

        try
        {
            foreach (var entry in journal.LoadAll())
            {
                if (entry.IsTerminal && entry.Warning == null && !entry.LocalReloadPending && entry.UpdatedAt < limit)
                {
                    journal.Delete(entry.LocalId);
                    purged++;
                }
            }
        }
        catch (Exception ex)
        {
            // only a cleanup: tried again next time
            Log.Warning(ex, "Cannot purge old transfers");
        }

        return purged;
    }

    /// <summary>
    /// Stops working on a save without delivery (no commit, or a commit that changed nothing on it).
    /// </summary>
    public async Task CloseSession(string localId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var entry = GetRequired(localId);

            if (entry.State == DeviceTransferState.COMMIT_REQUESTED && await StepFindDelivery(entry, ct))
            {
                // a delivery exists: closing would forget it
                await Advance(entry, ct);
                return;
            }

            if (entry.State is not (DeviceTransferState.UPLOADED or DeviceTransferState.COMMIT_REQUESTED))
            {
                throw new InvalidOperationException($"Cannot close a transfer in state {entry.State}");
            }

            await api.CloseSession(store.DeviceId, entry.SaveId!.Value, ct);
            await Move(entry, DeviceTransferState.CLOSED);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResolveConflict(string localId, TransferConflictResolution resolution, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var entry = GetRequired(localId);
            if (entry.State != DeviceTransferState.CONFLICT || entry.ConflictReason == null)
            {
                throw new InvalidOperationException($"Transfer is not in conflict: {entry.State}");
            }

            var reason = entry.ConflictReason.Value;

            switch (resolution)
            {
                case TransferConflictResolution.ABORT_ON_SERVER:
                    if (reason == DeviceConflictReason.DELIVERY_GONE || entry.TransferId == null)
                    {
                        throw new InvalidOperationException("There is no delivery to cancel on the server");
                    }

                    // can be refused (ABORT_NOT_POSSIBLE): the transfer then stays in conflict
                    await api.Abort(entry.TransferId, ct);

                    try
                    {
                        await api.CloseSession(store.DeviceId, entry.SaveId!.Value, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warning(ex, "Cannot close remote session after abort, ignored");
                    }

                    await Move(entry, DeviceTransferState.CLOSED);
                    break;

                case TransferConflictResolution.OVERWRITE_DEVICE:
                    if (reason == DeviceConflictReason.DELIVERY_GONE)
                    {
                        throw new InvalidOperationException("There is no delivery to write");
                    }

                    if (reason == DeviceConflictReason.DEVICE_SAVE_CHANGED && fileIO.Exists(entry.DeviceSavePath))
                    {
                        // keep what the player did since the upload
                        await journal.WriteBlob(
                            entry.LocalId, TransferBlob.DeviceBeforeWrite, await fileIO.ReadBytes(entry.DeviceSavePath)
                        );
                    }

                    entry.ConflictReason = null;
                    entry.DeviceHashAtConflict = null;
                    await Move(entry, DeviceTransferState.WRITING);
                    await Advance(entry, ct);
                    break;

                case TransferConflictResolution.DISCARD:
                    if (reason != DeviceConflictReason.DELIVERY_GONE)
                    {
                        throw new InvalidOperationException("Only a delivery the server does not have anymore can be discarded");
                    }

                    await Move(entry, DeviceTransferState.CLOSED);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // --- steps

    private async Task Advance(TransferJournalEntry entry, CancellationToken ct)
    {
        while (!entry.IsTerminal)
        {
            bool progressed;
            try
            {
                progressed = await Step(entry, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // network lost, server error, disk error...: the state is unchanged and resumes later
                await RecordError(entry, ex);
                return;
            }

            if (!progressed)
            {
                return;
            }
        }
    }

    /// <returns>True if the state changed, false if there is nothing more to do for now.</returns>
    private async Task<bool> Step(TransferJournalEntry entry, CancellationToken ct) => entry.State switch
    {
        DeviceTransferState.UPLOADING => await StepUpload(entry, ct),
        DeviceTransferState.COMMIT_REQUESTED => await StepFindDelivery(entry, ct),
        DeviceTransferState.COMMITTED_ON_SERVER => await StepDownload(entry, ct),
        DeviceTransferState.DOWNLOADED => await StepCheckDevice(entry),
        DeviceTransferState.WRITING => await StepWrite(entry, ct),
        DeviceTransferState.WRITTEN => await StepAck(entry, ct),
        _ => false,  // UPLOADED waits for the user, CONFLICT for a choice
    };

    private async Task<bool> StepUpload(TransferJournalEntry entry, CancellationToken ct)
    {
        byte[] bytes;
        if (string.IsNullOrEmpty(entry.OriginalHash))
        {
            bytes = await fileIO.ReadBytes(entry.DeviceSavePath);

            // from here the hash identifies what was uploaded, and the exact bytes are kept:
            // a retry sends them again, even if the device file changed meanwhile
            await journal.WriteBlob(entry.LocalId, TransferBlob.Original, bytes);
            entry.OriginalHash = RemoteSaveService.ComputeHash(bytes);
            await journal.Save(entry);
        }
        else
        {
            bytes = await journal.ReadBlob(entry.LocalId, TransferBlob.Original)
                ?? throw new InvalidOperationException("Uploaded content is missing from the journal");
        }

        try
        {
            var opened = await api.OpenSession(store.DeviceId, entry.FileName, bytes, ct);

            if (!SameHash(opened.OriginalHash, entry.OriginalHash))
            {
                throw new InvalidDataException("The server received other content than the one sent");
            }

            entry.SaveId = opened.SaveId;
            await Move(entry, DeviceTransferState.UPLOADED);
            return true;
        }
        catch (RemoteApiException ex) when (ex.Code == ConflictException.PENDING_DELIVERY && ex.ErrorData != null)
        {
            // a delivery of this save is already waiting (ex: journal lost): finish it first
            var transfer = (await api.GetTransfers(store.DeviceId, ct)).FirstOrDefault(t => t.Id == ex.ErrorData)
                ?? throw new InvalidOperationException("Pending delivery not listed by the server");

            AdoptDelivery(entry, transfer);
            await Move(entry, DeviceTransferState.COMMITTED_ON_SERVER);
            return true;
        }
        catch (RemoteApiException ex) when (ex.Code == ConflictException.SAVE_ID_CONFLICT)
        {
            // the server has this save from another source: it will never accept it
            entry.LastError = ex.Code;
            await Move(entry, DeviceTransferState.CLOSED, keepLastError: true);
            return true;
        }
    }

    private async Task<bool> StepFindDelivery(TransferJournalEntry entry, CancellationToken ct)
    {
        if (entry.SaveId == null)
        {
            return false;
        }

        var transfer = (await api.GetTransfers(store.DeviceId, ct))
            .Where(t => t.SaveId == entry.SaveId && SameHash(t.OriginalHash, entry.OriginalHash))
            .OrderByDescending(t => t.CommitSeq)
            .FirstOrDefault();
        if (transfer == null)
        {
            return false;
        }

        AdoptDelivery(entry, transfer);
        await Move(entry, DeviceTransferState.COMMITTED_ON_SERVER);
        return true;
    }

    private async Task<bool> StepDownload(TransferJournalEntry entry, CancellationToken ct)
    {
        var stored = await journal.ReadBlob(entry.LocalId, TransferBlob.New);
        var alreadyStored = stored != null && entry.NewHash != null && SameHash(RemoteSaveService.ComputeHash(stored), entry.NewHash);

        if (!alreadyStored)
        {
            RemoteDownload download;
            try
            {
                download = await api.DownloadTransfer(entry.TransferId!, ct);
            }
            catch (RemoteApiException ex) when (ex.Status == 404)
            {
                await Conflict(entry, DeviceConflictReason.DELIVERY_GONE, ex.Message);
                return true;
            }

            // a corrupted transfer is never accepted, it is downloaded again later
            if (!SameHash(RemoteSaveService.ComputeHash(download.Bytes), download.NewHash))
            {
                throw new InvalidDataException("Downloaded content does not match its hash");
            }

            if (!SameHash(download.OriginalHash, entry.OriginalHash))
            {
                throw new InvalidDataException("The delivery is not for the uploaded save");
            }

            await journal.WriteBlob(entry.LocalId, TransferBlob.New, download.Bytes);
            entry.NewHash = download.NewHash;
        }

        await Move(entry, DeviceTransferState.DOWNLOADED);
        return true;
    }

    private async Task<bool> StepCheckDevice(TransferJournalEntry entry)
    {
        if (!fileIO.Exists(entry.DeviceSavePath))
        {
            await Conflict(entry, DeviceConflictReason.DEVICE_SAVE_MISSING);
            return true;
        }

        var deviceHash = RemoteSaveService.ComputeHash(await fileIO.ReadBytes(entry.DeviceSavePath));

        if (SameHash(deviceHash, entry.NewHash!))
        {
            // already applied (the app stopped right after writing)
            entry.LocalReloadPending = true;
            await Move(entry, DeviceTransferState.WRITTEN);
            return true;
        }

        if (SameHash(deviceHash, entry.OriginalHash))
        {
            await Move(entry, DeviceTransferState.WRITING);
            return true;
        }

        // the game was played since the upload: writing would erase that progress
        entry.DeviceHashAtConflict = deviceHash;
        await Conflict(entry, DeviceConflictReason.DEVICE_SAVE_CHANGED);
        return true;
    }

    private async Task<bool> StepWrite(TransferJournalEntry entry, CancellationToken ct)
    {
        var newBytes = await journal.ReadBlob(entry.LocalId, TransferBlob.New)
            ?? throw new InvalidOperationException("Delivered content is missing from the journal");

        // the previous run may have stopped after a complete write
        if (fileIO.Exists(entry.DeviceSavePath)
            && SameHash(RemoteSaveService.ComputeHash(await fileIO.ReadBytes(entry.DeviceSavePath)), entry.NewHash!))
        {
            entry.LocalReloadPending = true;
            await Move(entry, DeviceTransferState.WRITTEN);
            return true;
        }

        // kept once, before the first write: later the device file can be half written
        if (!journal.HasBlob(entry.LocalId, TransferBlob.DeviceBeforeWrite) && fileIO.Exists(entry.DeviceSavePath))
        {
            await journal.WriteBlob(entry.LocalId, TransferBlob.DeviceBeforeWrite, await fileIO.ReadBytes(entry.DeviceSavePath));
        }

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= WRITE_ATTEMPTS; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await fileIO.WriteBytes(entry.DeviceSavePath, newBytes);

                // the write is only trusted once read back
                var writtenHash = RemoteSaveService.ComputeHash(await fileIO.ReadBytes(entry.DeviceSavePath));
                if (SameHash(writtenHash, entry.NewHash!))
                {
                    entry.LocalReloadPending = true;
                    await Move(entry, DeviceTransferState.WRITTEN);
                    return true;
                }

                lastFailure = new InvalidDataException("Written save does not match when read back");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }

            Log.Warning(lastFailure, $"Writing device save failed, attempt {attempt}/{WRITE_ATTEMPTS}");
        }

        // give up for now: put back what was there, so the save is not left half written
        var before = await journal.ReadBlob(entry.LocalId, TransferBlob.DeviceBeforeWrite);
        if (before != null)
        {
            try
            {
                await fileIO.WriteBytes(entry.DeviceSavePath, before);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Cannot put back the device save, its previous content stays in the journal");
                entry.Warning = $"The device save could not be put back, previous content: {journal.GetBlobPath(entry.LocalId, TransferBlob.DeviceBeforeWrite)}";
            }
        }

        await Conflict(entry, DeviceConflictReason.WRITE_FAILED, lastFailure?.Message);
        return true;
    }

    private async Task<bool> StepAck(TransferJournalEntry entry, CancellationToken ct)
    {
        try
        {
            await api.Ack(entry.TransferId!, entry.NewHash!, ct);
        }
        catch (RemoteApiException ex) when (ex.Status == 404)
        {
            // the device has the new save but the server forgot the delivery
            // (ex: its backup was restored): nothing more to do, kept as a warning
            entry.Warning = "The server does not know this delivery anymore: its bank may not contain the moved Pokémon";
        }

        await Move(entry, DeviceTransferState.ACKED);
        await TryReloadLocal(entry);
        return true;
    }

    // --- helpers

    private async Task TryReloadLocal(TransferJournalEntry entry)
    {
        try
        {
            if (await localRefresher.TryRefresh())
            {
                entry.LocalReloadPending = false;
                await journal.Save(entry);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Local saves reload failed, will be retried");
        }
    }

    private static void AdoptDelivery(TransferJournalEntry entry, RemoteTransferDTO transfer)
    {
        entry.TransferId = transfer.Id;
        entry.SaveId = transfer.SaveId;
        entry.OriginalHash = transfer.OriginalHash;
        entry.NewHash = transfer.NewHash;
    }

    private async Task Move(TransferJournalEntry entry, DeviceTransferState state, bool keepLastError = false)
    {
        entry.State = state;
        if (!keepLastError)
        {
            entry.LastError = null;
        }
        await journal.Save(entry);
    }

    private async Task Conflict(TransferJournalEntry entry, DeviceConflictReason reason, string? message = null)
    {
        entry.ConflictReason = reason;
        entry.LastError = message;
        await Move(entry, DeviceTransferState.CONFLICT, keepLastError: true);

        Log.Warning($"Transfer {entry.LocalId} in conflict: {reason} {message}");
    }

    private async Task RecordError(TransferJournalEntry entry, Exception ex)
    {
        Log.Warning(ex, $"Transfer {entry.LocalId} paused in state {entry.State}");

        entry.LastError = ex is RemoteApiException api ? $"{api.Status} {api.Code} {api.Message}".Trim() : ex.Message;
        try
        {
            await journal.Save(entry);
        }
        catch (Exception saveEx)
        {
            Log.Error(saveEx, "Cannot save transfer error");
        }
    }

    private TransferJournalEntry GetRequired(string localId)
        => journal.Get(localId) ?? throw new KeyNotFoundException($"Transfer not found: {localId}");

    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
