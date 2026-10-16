using System.Globalization;
using System.Net.Mime;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace PKVault.Core;

/// <summary>
/// Operations used by a remote device (ex: Android app) to work on this bank with a save it owns:
/// the device uploads its save, edits happen on the server with the usual actions,
/// the commit stores the modified save as a pending delivery, the device downloads it,
/// writes it, then acknowledges.
/// </summary>
public partial class RemoteBankService(
    SessionDbContext db, IRemoteSaveService remoteSaveService,
    ISavesLoadersService savesLoadersService, ISessionService sessionService,
    IFileIOService fileIOService, ISettingsService settingsService,
    BackupService backupService, DataService dataService, TimeProvider timeProvider
)
{
    private const int MAX_SAVE_SIZE = 10 * 1024 * 1024;
    private const string DATE_FORMAT = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex DeviceIdRegex();

    public async Task<RemotePingDTO> Ping()
    {
        await sessionService.EnsureSessionCreated(db.ContextId.InstanceId);

        return new RemotePingDTO(settingsService.GetSettings().Version, await remoteSaveService.GetCommitSeq());
    }

    public async Task<RemoteOpenSessionDTO> OpenSession(string deviceId, string fileName, Stream fileStream)
    {
        ValidateDeviceId(deviceId);
        ValidateFileName(fileName);

        if (!sessionService.HasEmptyActionList())
        {
            throw new InvalidOperationException("Empty action list is required");
        }

        await sessionService.EnsureSessionCreated(db.ContextId.InstanceId);

        if (fileStream.Length > MAX_SAVE_SIZE)
        {
            throw new ArgumentException($"Save file should be <{MAX_SAVE_SIZE / 1024 / 1024}MB");
        }

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        // the save ID used for the folder name must come from this loading path,
        // it can differ from a raw PKHeX load
        var save = await savesLoadersService.CheckSaveData(bytes, fileName, overwrite: true);
        var saveId = save.Id;

        // a delivery waiting for the device must be resumed first, never overridden
        var pending = await db.RemoteTransfers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.SaveId == saveId && t.State == RemoteTransferState.READY);
        if (pending != null)
        {
            throw new ConflictException(
                ConflictException.PENDING_DELIVERY,
                $"Save {saveId} has a pending delivery, it has to be downloaded and acknowledged first",
                pending.Id
            );
        }

        var remoteRoot = settingsService.GetSettings().GetRemoteSavesPath();
        var saveDirectory = RemoteSavePath.GetSaveDirectory(remoteRoot, deviceId, saveId);

        // same save ID from another source (server save, another device) would be ambiguous:
        // the play-time tie-break could make the server edit the wrong file
        var otherSources = savesLoadersService.GetAllSaveInfos().Values
            .SelectMany(infos => (SaveInfosDTO[])[infos, .. infos.Duplicates])
            .Where(infos => infos.Id == saveId)
            .Where(infos => !(RemoteSavePath.TryParse(remoteRoot, infos.Path, out var d, out var s) && d == deviceId && s == saveId))
            .ToList();
        if (otherSources.Count > 0)
        {
            throw new ConflictException(
                ConflictException.SAVE_ID_CONFLICT,
                $"Save {saveId} already exists on server from another source"
            );
        }

        // one file per device & save, re-upload is idempotent
        fileIOService.Delete(saveDirectory);
        await savesLoadersService.UploadSaveWithoutCheck(Path.Combine(saveDirectory, fileName), bytes);

        DataUpdateFlags flags = new();
        MarkSaveFlags(flags, saveId);
        await savesLoadersService.Setup(flags);

        Log.Information($"Remote save opened, save={saveId}, device={deviceId}");

        return new RemoteOpenSessionDTO(saveId, RemoteSaveService.ComputeHash(bytes), await dataService.CreateDataFromUpdateFlags(flags));
    }

    public async Task<DataDTO> CloseSession(string deviceId, uint saveId)
    {
        ValidateDeviceId(deviceId);

        if (!sessionService.HasEmptyActionList())
        {
            throw new InvalidOperationException("Empty action list is required");
        }

        DataUpdateFlags flags = new();

        var remoteRoot = settingsService.GetSettings().GetRemoteSavesPath();
        if (fileIOService.Delete(RemoteSavePath.GetSaveDirectory(remoteRoot, deviceId, saveId)))
        {
            MarkSaveFlags(flags, saveId);
            await savesLoadersService.Setup(flags);

            Log.Information($"Remote save closed, save={saveId}, device={deviceId}");
        }

        return await dataService.CreateDataFromUpdateFlags(flags);
    }

    public async Task<List<RemoteTransferDTO>> GetTransfers(string deviceId)
    {
        ValidateDeviceId(deviceId);

        await sessionService.EnsureSessionCreated(db.ContextId.InstanceId);

        var transfers = await db.RemoteTransfers.AsNoTracking()
            .Where(t => t.DeviceId == deviceId && t.State == RemoteTransferState.READY)
            .ToListAsync();

        return [.. transfers.OrderBy(t => t.CommitSeq).Select(ToDTO)];
    }

    public async Task<CoreFileResponse> Download(string id)
    {
        var transfer = await GetTransferRequired(id);

        if (transfer.State == RemoteTransferState.ABORTED || transfer.NewBytes.Length == 0)
        {
            throw new KeyNotFoundException($"Delivery content not available for transfer {id}");
        }

        return new(
            StatusCode: 200,
            ContentType: MediaTypeNames.Application.Octet,
            File: new(
                Stream: new MemoryStream(transfer.NewBytes),
                ContentType: MediaTypeNames.Application.Octet,
                FileName: transfer.FileName
            ),
            Header: new()
            {
                ["X-Hash-Original"] = transfer.OriginalHash,
                ["X-Hash-New"] = transfer.NewHash,
                ["access-control-expose-headers"] = "X-Hash-Original, X-Hash-New",
            }
        );
    }

    /// <summary>
    /// The device confirms it has written the new save. Idempotent.
    /// </summary>
    public async Task<RemoteTransferDTO> Ack(string id, string newHash)
    {
        var transfer = await GetTransferRequired(id);

        if (!string.Equals(transfer.NewHash, newHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(ConflictException.HASH_MISMATCH, $"Hash does not match transfer {id}");
        }

        if (transfer.State == RemoteTransferState.ABORTED)
        {
            throw new ConflictException(ConflictException.ABORT_NOT_POSSIBLE, $"Transfer {id} was aborted");
        }

        if (transfer.State == RemoteTransferState.READY)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            await ExecuteOnBothDbs(
                "UPDATE RemoteTransfers SET State = $state, AckedAt = $ackedAt WHERE Id = $id",
                ("$state", (int)RemoteTransferState.ACKED),
                ("$ackedAt", now.ToString(DATE_FORMAT, CultureInfo.InvariantCulture)),
                ("$id", transfer.Id)
            );

            Log.Information($"Remote delivery acknowledged, transfer={transfer.Id}, save={transfer.SaveId}");
        }

        await PurgeOldDeliveredContent();

        return ToDTO(await GetTransferRequired(id));
    }

    /// <summary>
    /// The device cannot write the new save (ex: the game was played since the upload):
    /// restore the bank as it was before the commit. Only possible if nothing was committed since.
    /// </summary>
    public async Task<DataDTO> Abort(string id)
    {
        var transfer = await GetTransferRequired(id);

        if (transfer.State != RemoteTransferState.READY)
        {
            throw new ConflictException(ConflictException.ABORT_NOT_POSSIBLE, $"Transfer {id} is not pending");
        }

        if (!sessionService.HasEmptyActionList())
        {
            throw new InvalidOperationException("Empty action list is required");
        }

        if (transfer.CommitSeq != await remoteSaveService.GetCommitSeq())
        {
            throw new ConflictException(ConflictException.ABORT_NOT_POSSIBLE, "Other changes were saved since this delivery");
        }

        var backupKey = BackupService.SerializeDateTime(transfer.BackupCreatedAt);
        if (!backupService.GetBackupList().Any(b => BackupService.SerializeDateTime(b.CreatedAt) == backupKey))
        {
            throw new ConflictException(ConflictException.ABORT_NOT_POSSIBLE, "The backup before this delivery does not exist anymore");
        }

        DataUpdateFlags flags = new();

        // restoring the DB removes the delivery row along with the commit
        await backupService.RestoreBackup(transfer.BackupCreatedAt, withSafeBackup: true, flags);

        Log.Information($"Remote delivery aborted, transfer={id}, save={transfer.SaveId}");

        return await dataService.CreateDataFromUpdateFlags(flags);
    }

    private async Task<RemoteTransferEntity> GetTransferRequired(string id)
    {
        await sessionService.EnsureSessionCreated(db.ContextId.InstanceId);

        var transfer = await db.RemoteTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        return transfer ?? throw new KeyNotFoundException($"Transfer not found: {id}");
    }

    /// <summary>
    /// Delivered content is not needed anymore after the device ack + the retention delay.
    /// </summary>
    private async Task PurgeOldDeliveredContent()
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-(EnvUtil.TRANSFER_RETENTION_DAYS ?? 30));

        await ExecuteOnBothDbs(
            "UPDATE RemoteTransfers SET NewBytes = x'' WHERE State = $state AND AckedAt < $cutoff AND length(NewBytes) > 0",
            ("$state", (int)RemoteTransferState.ACKED),
            ("$cutoff", cutoff.ToString(DATE_FORMAT, CultureInfo.InvariantCulture))
        );
    }

    /// <summary>
    /// The session DB is only a working copy of the main DB, replaced by it when the session restarts:
    /// a change not made on the main DB would be lost, and it must not be overridden at next commit.
    /// </summary>
    private async Task ExecuteOnBothDbs(string sql, params (string Name, object Value)[] parameters)
    {
        var mainDbPath = sessionService.MainDbPath;
        if (fileIOService.Exists(mainDbPath))
        {
            using var connection = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.ExecuteSqlRawAsync(sql, parameters.Select(p => new SqliteParameter(p.Name, p.Value)).ToArray());
    }

    private static RemoteTransferDTO ToDTO(RemoteTransferEntity t) => new(
        t.Id, t.SaveId, t.FileName, t.OriginalHash, t.NewHash, t.NewBytes.Length, t.CommitSeq, t.CreatedAt, t.State.ToString()
    );

    private static void MarkSaveFlags(DataUpdateFlags flags, uint saveId)
    {
        flags.SaveInfos = true;
        flags.Saves.UseSave(saveId).SavePkms.All = true;
        flags.Saves.UseSave(saveId).SaveBoxes = true;
        flags.Dex.All = true;
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !DeviceIdRegex().IsMatch(deviceId))
        {
            throw new ArgumentException("Device ID should be 8-64 characters among letters, digits, '-' and '_'");
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 128
            || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) != -1
            || fileName.IndexOfAny(['/', '\\']) != -1)
        {
            throw new ArgumentException($"Filename invalid: {fileName}");
        }
    }
}
