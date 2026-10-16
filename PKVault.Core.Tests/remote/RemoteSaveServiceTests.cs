using Microsoft.EntityFrameworkCore;
using Moq;
using PKVault.Core;

public class RemoteSaveServiceTests : IAsyncDisposable
{
    private readonly string dbPath;
    private readonly SessionDbContext db;
    private readonly Mock<IFileIOService> mockFileIO;
    private readonly Mock<ISessionServiceMinimal> sessionService;
    private readonly SettingsDTO settings;
    private readonly RemoteSaveService service;

    public RemoteSaveServiceTests()
    {
        Program.Initialize();

        dbPath = Path.Combine(Directory.GetCurrentDirectory(), $"db-RemoteSaveServiceTests-{Guid.NewGuid()}.db");

        sessionService = new();
        sessionService.Setup(s => s.SessionDbPath).Returns(dbPath);
        sessionService.Setup(s => s.MainDbPath).Returns(dbPath);

        mockFileIO = new();
        db = new(sessionService.Object, new Mock<DbSeedingService>(mockFileIO.Object).Object);

        settings = new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        );
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetSettings()).Returns(settings);

        var metaLoader = new MetaLoader(db, sessionService.Object);

        service = new RemoteSaveService(db, metaLoader, mockFileIO.Object, mockSettings.Object, sessionService.Object, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        GC.SuppressFinalize(this);
    }

    private async Task Migrate() => await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

    private string RemoteSavePathOf(string deviceId, uint saveId, string fileName = "main.sav")
        => Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), deviceId, saveId), fileName);

    [Fact]
    public async Task Migrations_ShouldMatchModel_NoPendingChanges()
    {
        // guards the hand-written AddRemoteTransfer migration & snapshot against model drift
        await Migrate();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task RecordCommit_ShouldCreateReadyTransfer_ForRemoteSave_WithHashes()
    {
        await Migrate();

        var path = RemoteSavePathOf("device-1", 123);
        byte[] originalBytes = [1, 2, 3];
        byte[] newBytes = [4, 5, 6, 7];
        mockFileIO.Setup(x => x.ReadBytes(path)).ReturnsAsync(originalBytes);

        var backupTime = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        service.BackupCreatedAt = backupTime;

        await service.RecordCommit([new SaveWriteResult(123, path, newBytes, IsRemote: true)]);

        var transfer = await db.RemoteTransfers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("device-1", transfer.DeviceId);
        Assert.Equal(123u, transfer.SaveId);
        Assert.Equal("main.sav", transfer.FileName);
        Assert.Equal(RemoteTransferState.READY, transfer.State);
        Assert.Equal(RemoteSaveService.ComputeHash(originalBytes), transfer.OriginalHash);
        Assert.Equal(RemoteSaveService.ComputeHash(newBytes), transfer.NewHash);
        Assert.NotEqual(transfer.OriginalHash, transfer.NewHash);
        Assert.Equal(newBytes, transfer.NewBytes);
        Assert.Equal(backupTime, transfer.BackupCreatedAt);
        Assert.Equal(1, transfer.CommitSeq);
    }

    [Fact]
    public async Task RecordCommit_ShouldIgnoreLocalSaves_ButStillBumpCommitSeq()
    {
        await Migrate();

        await service.RecordCommit([new SaveWriteResult(1, "/some/local/main.sav", [1], IsRemote: false)]);
        Assert.Empty(await db.RemoteTransfers.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await service.GetCommitSeq());

        await service.RecordCommit([]);
        Assert.Equal(2, await service.GetCommitSeq());
    }

    [Fact]
    public async Task RecordCommit_ShouldThrow_WhenBackupTimeMissing_AndRecordNothing()
    {
        await Migrate();

        var path = RemoteSavePathOf("device-1", 123);
        mockFileIO.Setup(x => x.ReadBytes(path)).ReturnsAsync([1]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecordCommit([new SaveWriteResult(123, path, [2], IsRemote: true)])
        );

        Assert.Empty(await db.RemoteTransfers.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordCommit_ShouldThrow_WhenRemotePathHasUnexpectedLayout()
    {
        await Migrate();

        service.BackupCreatedAt = DateTime.UtcNow;
        var path = Path.Combine(settings.GetRemoteSavesPath(), "stray.sav");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecordCommit([new SaveWriteResult(123, path, [2], IsRemote: true)])
        );
    }

    [Fact]
    public void DeleteDeliveredFiles_ShouldDeleteRemoteSaveFolder_OnlyForRemoteSaves()
    {
        var remotePath = RemoteSavePathOf("device-1", 123);

        service.DeleteDeliveredFiles([
            new SaveWriteResult(123, remotePath, [1], IsRemote: true),
            new SaveWriteResult(5, "/some/local/main.sav", [1], IsRemote: false),
        ]);

        mockFileIO.Verify(x => x.Delete(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), "device-1", 123)), Times.Once);
        mockFileIO.Verify(x => x.Delete(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void DeleteDeliveredFiles_ShouldNotThrow_WhenDeletionFails()
    {
        mockFileIO.Setup(x => x.Delete(It.IsAny<string>())).Throws(new IOException("locked"));

        service.DeleteDeliveredFiles([new SaveWriteResult(123, RemoteSavePathOf("device-1", 123), [1], IsRemote: true)]);
    }

    [Fact]
    public async Task CleanupDeliveredFiles_ShouldOnlyDeleteFolders_OfReadyTransfers()
    {
        await Migrate();

        RemoteTransferEntity Create(string deviceId, uint saveId, RemoteTransferState state) => new()
        {
            Id = Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            SaveId = saveId,
            FileName = "main.sav",
            OriginalHash = "a",
            NewHash = "b",
            NewBytes = [1],
            BackupCreatedAt = DateTime.UtcNow,
            CommitSeq = 1,
            State = state,
            CreatedAt = DateTime.UtcNow,
        };

        db.RemoteTransfers.AddRange(
            Create("device-ready", 1, RemoteTransferState.READY),
            // a new upload may legitimately exist after the delivery was acked or aborted
            Create("device-acked", 2, RemoteTransferState.ACKED),
            Create("device-aborted", 3, RemoteTransferState.ABORTED)
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        mockFileIO.Setup(x => x.Exists(dbPath)).Returns(true);
        mockFileIO.Setup(x => x.Delete(It.IsAny<string>())).Returns(true);

        service.CleanupDeliveredFiles();

        mockFileIO.Verify(x => x.Delete(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), "device-ready", 1)), Times.Once);
        mockFileIO.Verify(x => x.Delete(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void CleanupDeliveredFiles_ShouldDoNothing_WhenNoMainDb()
    {
        mockFileIO.Setup(x => x.Exists(dbPath)).Returns(false);

        service.CleanupDeliveredFiles();

        mockFileIO.Verify(x => x.Delete(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CleanupDeliveredFiles_ShouldNotThrow_WhenMainDbHasNoRemoteTable()
    {
        // main DB from a previous app version
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Metas (Key INTEGER PRIMARY KEY, Value TEXT)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        mockFileIO.Setup(x => x.Exists(dbPath)).Returns(true);

        service.CleanupDeliveredFiles();

        mockFileIO.Verify(x => x.Delete(It.IsAny<string>()), Times.Never);
    }
}
