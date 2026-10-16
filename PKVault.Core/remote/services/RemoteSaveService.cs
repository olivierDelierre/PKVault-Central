using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;

namespace PKVault.Core;

public interface IRemoteSaveService
{
    /// <summary>
    /// Time of the "backup_before_save" backup taken for the commit being persisted.
    /// Needed to restore the bank if the device asks to abort the delivery.
    /// </summary>
    public DateTime? BackupCreatedAt { get; set; }

    public Task<long> GetCommitSeq();

    /// <summary>
    /// Called by the session persist, before the session DB replaces the main one.
    /// Bumps the commit sequence and records a pending delivery for every written remote save.
    /// </summary>
    public Task RecordCommit(IReadOnlyList<SaveWriteResult> writtenSaves);

    /// <summary>
    /// Called after the session DB has been moved: delivered bytes now live in the DB,
    /// the uploaded copy of the save is not needed anymore.
    /// </summary>
    public void DeleteDeliveredFiles(IReadOnlyList<SaveWriteResult> writtenSaves);

    /// <summary>
    /// Startup recovery: if the process stopped between the DB move and the file deletion,
    /// remove the leftover uploaded saves that have a pending delivery.
    /// </summary>
    public void CleanupDeliveredFiles();
}

public class RemoteSaveService(
    SessionDbContext db, IMetaLoader metaLoader, IFileIOService fileIOService,
    ISettingsService settingsService, ISessionServiceMinimal sessionService, TimeProvider timeProvider
) : IRemoteSaveService
{
    public DateTime? BackupCreatedAt { get; set; }

    public static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public async Task<long> GetCommitSeq()
    {
        var entity = await metaLoader.GetEntity(MetaKey.COMMIT_SEQ);
        return entity != null && long.TryParse(entity.Value, out var seq) ? seq : 0;
    }

    public async Task RecordCommit(IReadOnlyList<SaveWriteResult> writtenSaves)
    {
        var seqEntity = await metaLoader.GetEntity(MetaKey.COMMIT_SEQ);
        var commitSeq = (seqEntity != null && long.TryParse(seqEntity.Value, out var current) ? current : 0) + 1;

        if (seqEntity == null)
        {
            await metaLoader.AddEntity(new() { Key = MetaKey.COMMIT_SEQ, Value = commitSeq.ToString() });
        }
        else
        {
            seqEntity.Value = commitSeq.ToString();
            await metaLoader.UpdateEntity(seqEntity);
        }

        var remoteRoot = settingsService.GetSettings().GetRemoteSavesPath();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var written in writtenSaves.Where(w => w.IsRemote))
        {
            if (!RemoteSavePath.TryParse(remoteRoot, written.Path, out var deviceId, out _))
            {
                throw new InvalidOperationException($"Remote save path not in expected format: {written.Path}");
            }

            var backupCreatedAt = BackupCreatedAt
                ?? throw new InvalidOperationException("Backup time is required to record a remote delivery");

            // the uploaded file is untouched on disk until the commit is done
            var originalBytes = await fileIOService.ReadBytes(written.Path);

            db.RemoteTransfers.Add(new()
            {
                Id = Guid.NewGuid().ToString(),
                DeviceId = deviceId,
                SaveId = written.SaveId,
                FileName = Path.GetFileName(written.Path),
                OriginalHash = ComputeHash(originalBytes),
                NewHash = ComputeHash(written.Bytes),
                NewBytes = written.Bytes,
                BackupCreatedAt = backupCreatedAt,
                CommitSeq = commitSeq,
                State = RemoteTransferState.READY,
                CreatedAt = now,
            });

            Log.Information($"Remote delivery recorded, save={written.SaveId}, device={deviceId}, commitSeq={commitSeq}");
        }

        await db.SaveChangesAsync();
    }

    public void DeleteDeliveredFiles(IReadOnlyList<SaveWriteResult> writtenSaves)
    {
        var remoteRoot = settingsService.GetSettings().GetRemoteSavesPath();

        foreach (var written in writtenSaves.Where(w => w.IsRemote))
        {
            try
            {
                if (RemoteSavePath.TryParse(remoteRoot, written.Path, out var deviceId, out var saveId))
                {
                    fileIOService.Delete(RemoteSavePath.GetSaveDirectory(remoteRoot, deviceId, saveId));
                }
            }
            catch (Exception ex)
            {
                // not blocking: cleaned at next startup
                Log.Warning(ex, $"Cannot delete delivered remote save, path={written.Path}");
            }
        }
    }

    public void CleanupDeliveredFiles()
    {
        try
        {
            var mainDbPath = sessionService.MainDbPath;
            if (!fileIOService.Exists(mainDbPath))
            {
                return;
            }

            List<(string DeviceId, uint SaveId)> pending = [];

            // direct read of the main DB: the session DB is not ready at this point
            using (var connection = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();

                using var tableCommand = connection.CreateCommand();
                tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='RemoteTransfers'";
                if (tableCommand.ExecuteScalar() == null)
                {
                    return;
                }

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DeviceId, SaveId FROM RemoteTransfers WHERE State = $state";
                command.Parameters.AddWithValue("$state", (int)RemoteTransferState.READY);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    pending.Add((reader.GetString(0), (uint)reader.GetInt64(1)));
                }
            }

            var remoteRoot = settingsService.GetSettings().GetRemoteSavesPath();
            foreach (var (deviceId, saveId) in pending)
            {
                if (fileIOService.Delete(RemoteSavePath.GetSaveDirectory(remoteRoot, deviceId, saveId)))
                {
                    Log.Information($"Removed leftover remote save with pending delivery, save={saveId}, device={deviceId}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Remote saves cleanup failed");
        }
    }
}
