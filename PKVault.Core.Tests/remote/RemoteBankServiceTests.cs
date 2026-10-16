using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PKVault.Core;

public class RemoteBankServiceTests : IAsyncDisposable
{
    private const string DEVICE = "device-0001";

    private readonly string sessionDbPath;
    private readonly string mainDbPath;
    private readonly SessionDbContext db;
    private readonly Mock<IFileIOService> mockFileIO;
    private readonly Mock<ISessionService> sessionService;
    private readonly Mock<ISavesLoadersService> savesLoadersService;
    private readonly SettingsDTO settings;
    private readonly RemoteBankService service;
    private DateTime now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    public RemoteBankServiceTests()
    {
        Program.Initialize();

        var id = Guid.NewGuid();
        sessionDbPath = Path.Combine(Directory.GetCurrentDirectory(), $"db-RemoteBankServiceTests-{id}.db");
        mainDbPath = Path.Combine(Directory.GetCurrentDirectory(), $"main-RemoteBankServiceTests-{id}.db");

        sessionService = new();
        sessionService.Setup(s => s.SessionDbPath).Returns(sessionDbPath);
        sessionService.Setup(s => s.MainDbPath).Returns(mainDbPath);
        sessionService.Setup(s => s.HasEmptyActionList()).Returns(true);

        mockFileIO = new();
        mockFileIO.Setup(x => x.Matcher).Returns(new MatcherUtil { GetAllPaths = (_, _) => [] });
        mockFileIO.Setup(x => x.Exists(mainDbPath)).Returns(true);

        db = new(sessionService.Object, new Mock<DbSeedingService>(mockFileIO.Object).Object);

        settings = new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "1.2.3", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        );
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetSettings()).Returns(settings);

        var timeProvider = new Mock<TimeProvider>();
        timeProvider.Setup(x => x.GetUtcNow()).Returns(() => new DateTimeOffset(now));

        savesLoadersService = new();
        savesLoadersService.Setup(x => x.GetAllSaveInfos()).Returns(new Dictionary<uint, SaveInfosDTO>());

        var remoteSaveService = new RemoteSaveService(
            db, new MetaLoader(db, sessionService.Object), mockFileIO.Object, mockSettings.Object, sessionService.Object, timeProvider.Object
        );
        var backupService = new BackupService(
            null!, timeProvider.Object, mockFileIO.Object, savesLoadersService.Object, mockSettings.Object, sessionService.Object
        );

        service = new RemoteBankService(
            db, remoteSaveService, savesLoadersService.Object, sessionService.Object,
            mockFileIO.Object, mockSettings.Object, backupService,
            null!,  // DataService: only used by flows that rebuild data after a save rescan
            timeProvider.Object
        );
    }

    public async ValueTask DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(mainDbPath)) File.Delete(mainDbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the schema, adds the rows, then makes the main DB a copy of the session DB.</summary>
    private async Task Seed(params RemoteTransferEntity[] transfers)
    {
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.RemoteTransfers.AddRange(transfers);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        SqliteConnection.ClearAllPools();
        File.Copy(sessionDbPath, mainDbPath, overwrite: true);
    }

    private RemoteTransferEntity Transfer(
        string? id = null, RemoteTransferState state = RemoteTransferState.READY, long commitSeq = 1,
        string deviceId = DEVICE, uint saveId = 111, byte[]? newBytes = null, DateTime? ackedAt = null
    ) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        DeviceId = deviceId,
        SaveId = saveId,
        FileName = "main.sav",
        OriginalHash = "ORIGINAL",
        NewHash = "NEWHASH",
        NewBytes = newBytes ?? [1, 2, 3],
        BackupCreatedAt = new DateTime(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc),
        CommitSeq = commitSeq,
        State = state,
        CreatedAt = now,
        AckedAt = ackedAt,
    };

    private async Task<(RemoteTransferState State, int Size)> ReadSessionRow(string id)
    {
        var row = await db.RemoteTransfers.AsNoTracking().SingleAsync(t => t.Id == id, TestContext.Current.CancellationToken);
        return (row.State, row.NewBytes.Length);
    }

    private async Task<(int State, long Size)> ReadMainRow(string id)
    {
        await using var connection = new SqliteConnection($"Data Source={mainDbPath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, length(NewBytes) FROM RemoteTransfers WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    // --- transfers listing & download

    [Fact]
    public async Task GetTransfers_ShouldReturnOnlyReadyOnesOfTheDevice_InCommitOrder()
    {
        await Seed(
            Transfer(id: "b", commitSeq: 3),
            Transfer(id: "a", commitSeq: 2),
            Transfer(id: "acked", state: RemoteTransferState.ACKED, commitSeq: 1),
            Transfer(id: "other-device", deviceId: "device-0002", commitSeq: 4)
        );

        var result = await service.GetTransfers(DEVICE);

        Assert.Equal(["a", "b"], result.Select(t => t.Id));
        Assert.All(result, t => Assert.Equal("READY", t.State));
        Assert.Equal(3, result[0].Size);
    }

    [Fact]
    public async Task GetTransfers_ShouldRejectInvalidDeviceId()
    {
        await Seed();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTransfers("../etc"));
    }

    [Fact]
    public async Task Download_ShouldReturnNewBytes_WithHashHeaders()
    {
        await Seed(Transfer(id: "t1", newBytes: [9, 8, 7]));

        var response = await service.Download("t1");

        using var ms = new MemoryStream();
        await response.File.Stream.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal([9, 8, 7], ms.ToArray());
        Assert.Equal("main.sav", response.File.FileName);
        Assert.Equal("NEWHASH", response.Header["X-Hash-New"]);
        Assert.Equal("ORIGINAL", response.Header["X-Hash-Original"]);
    }

    [Fact]
    public async Task Download_ShouldStillWork_AfterAck_UntilContentIsPurged()
    {
        await Seed(Transfer(id: "t1", state: RemoteTransferState.ACKED, ackedAt: now));

        Assert.Equal(200, (await service.Download("t1")).StatusCode);
    }

    [Fact]
    public async Task Download_ShouldNotFound_WhenUnknownAbortedOrPurged()
    {
        await Seed(
            Transfer(id: "aborted", state: RemoteTransferState.ABORTED),
            Transfer(id: "purged", state: RemoteTransferState.ACKED, newBytes: [])
        );

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Download("unknown"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Download("aborted"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Download("purged"));
    }

    // --- ack

    [Fact]
    public async Task Ack_ShouldRejectWrongHash_AndKeepTransferReady()
    {
        await Seed(Transfer(id: "t1"));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.Ack("t1", "WRONG"));

        Assert.Equal(ConflictException.HASH_MISMATCH, ex.Code);
        Assert.Equal(RemoteTransferState.READY, (await ReadSessionRow("t1")).State);
        Assert.Equal((int)RemoteTransferState.READY, (await ReadMainRow("t1")).State);
    }

    [Fact]
    public async Task Ack_ShouldMarkAcked_InMainDbAndSessionDb()
    {
        // the session DB is a working copy replaced by the main DB on restart:
        // an ack only made on the session DB would be lost
        await Seed(Transfer(id: "t1"));

        var result = await service.Ack("t1", "newhash");   // hash compare ignores case

        Assert.Equal("ACKED", result.State);
        Assert.Equal(RemoteTransferState.ACKED, (await ReadSessionRow("t1")).State);
        Assert.Equal((int)RemoteTransferState.ACKED, (await ReadMainRow("t1")).State);
    }

    [Fact]
    public async Task Ack_ShouldBeIdempotent()
    {
        await Seed(Transfer(id: "t1"));

        await service.Ack("t1", "NEWHASH");
        var second = await service.Ack("t1", "NEWHASH");

        Assert.Equal("ACKED", second.State);
    }

    [Fact]
    public async Task Ack_ShouldRefuseAbortedTransfer()
    {
        await Seed(Transfer(id: "t1", state: RemoteTransferState.ABORTED));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.Ack("t1", "NEWHASH"));

        Assert.Equal(ConflictException.ABORT_NOT_POSSIBLE, ex.Code);
    }

    [Fact]
    public async Task Ack_ShouldNotFound_WhenUnknownTransfer()
    {
        await Seed();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.Ack("unknown", "NEWHASH"));
    }

    [Fact]
    public async Task Ack_ShouldPurgeContentOfOldAckedTransfers_OnlyAfterRetention()
    {
        await Seed(
            Transfer(id: "to-ack"),
            Transfer(id: "old", state: RemoteTransferState.ACKED, ackedAt: now.AddDays(-40)),
            Transfer(id: "recent", state: RemoteTransferState.ACKED, ackedAt: now.AddDays(-5)),
            Transfer(id: "ready-old", commitSeq: 2)    // never acked: content must never be purged
        );

        await service.Ack("to-ack", "NEWHASH");

        Assert.Equal(0, (await ReadSessionRow("old")).Size);
        Assert.Equal(0L, (await ReadMainRow("old")).Size);
        Assert.Equal(3, (await ReadSessionRow("recent")).Size);
        Assert.Equal(3, (await ReadSessionRow("to-ack")).Size);
        Assert.Equal(3, (await ReadSessionRow("ready-old")).Size);
    }

    // --- abort (preconditions: the restore itself is covered by BackupService)

    [Fact]
    public async Task Abort_ShouldRefuse_WhenTransferNotPending()
    {
        await Seed(Transfer(id: "t1", state: RemoteTransferState.ACKED));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.Abort("t1"));

        Assert.Equal(ConflictException.ABORT_NOT_POSSIBLE, ex.Code);
    }

    [Fact]
    public async Task Abort_ShouldRefuse_WhenActionsArePending()
    {
        await Seed(Transfer(id: "t1"));
        sessionService.Setup(s => s.HasEmptyActionList()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Abort("t1"));
    }

    [Fact]
    public async Task Abort_ShouldRefuse_WhenOtherChangesWereCommittedSince()
    {
        // restoring would drop the later commits
        await Seed(Transfer(id: "t1", commitSeq: 1));
        db.Metas.Add(new MetaEntity { Key = MetaKey.COMMIT_SEQ, Value = "2" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.Abort("t1"));

        Assert.Equal(ConflictException.ABORT_NOT_POSSIBLE, ex.Code);
        Assert.Equal(RemoteTransferState.READY, (await ReadSessionRow("t1")).State);
    }

    [Fact]
    public async Task Abort_ShouldRefuse_WhenBackupBeforeTheCommitIsGone()
    {
        await Seed(Transfer(id: "t1", commitSeq: 1));
        db.Metas.Add(new MetaEntity { Key = MetaKey.COMMIT_SEQ, Value = "1" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.Abort("t1"));

        Assert.Equal(ConflictException.ABORT_NOT_POSSIBLE, ex.Code);
        Assert.Contains("backup", ex.Message);
    }

    // --- session open / close

    [Theory]
    [InlineData("short")]
    [InlineData("has space in it")]
    [InlineData("../../escape-path")]
    [InlineData("")]
    public async Task OpenSession_ShouldRejectInvalidDeviceId(string deviceId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.OpenSession(deviceId, "main.sav", new MemoryStream([1]))
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../main.sav")]
    [InlineData("sub/main.sav")]
    [InlineData("sub\\main.sav")]
    public async Task OpenSession_ShouldRejectInvalidFileName(string fileName)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.OpenSession(DEVICE, fileName, new MemoryStream([1]))
        );
    }

    [Fact]
    public async Task OpenSession_ShouldRefuse_WhenActionsArePending()
    {
        sessionService.Setup(s => s.HasEmptyActionList()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );
        savesLoadersService.Verify(x => x.UploadSaveWithoutCheck(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    private async Task<uint> SetupUploadedSave()
    {
        var mockSave = SaveWrapperTests.GetMockSave(Path.Combine(Directory.GetCurrentDirectory(), "x.sav"), [1]);
        savesLoadersService.Setup(x => x.CheckSaveData(It.IsAny<byte[]>(), It.IsAny<string>(), true))
            .ReturnsAsync(mockSave.Object);
        await Task.CompletedTask;
        return mockSave.Object.Id;
    }

    [Fact]
    public async Task OpenSession_ShouldRefuse_WhenDeliveryIsPending_AndGiveItsId()
    {
        var saveId = await SetupUploadedSave();
        await Seed(Transfer(id: "pending-1", saveId: saveId));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );

        Assert.Equal(ConflictException.PENDING_DELIVERY, ex.Code);
        Assert.Equal("pending-1", ex.ErrorData);
        savesLoadersService.Verify(x => x.UploadSaveWithoutCheck(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task OpenSession_ShouldAllowUpload_WhenPreviousDeliveryWasAcked_NotConflictingWithIt()
    {
        var saveId = await SetupUploadedSave();
        await Seed(Transfer(id: "done", saveId: saveId, state: RemoteTransferState.ACKED, ackedAt: now));

        // DataService is null in this test: reaching the end of the flow fails there,
        // after the upload happened, which is what is checked
        await Assert.ThrowsAnyAsync<NullReferenceException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );

        var expectedPath = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), DEVICE, saveId), "main.sav");
        savesLoadersService.Verify(x => x.UploadSaveWithoutCheck(expectedPath, It.IsAny<byte[]>()), Times.Once);
        mockFileIO.Verify(x => x.Delete(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), DEVICE, saveId)), Times.Once);
    }

    [Fact]
    public async Task OpenSession_ShouldRefuse_WhenSameSaveIdExistsFromAnotherSource()
    {
        var saveId = await SetupUploadedSave();
        await Seed();

        var serverSave = SaveWrapperTests.GetMockSave(Path.Combine(Directory.GetCurrentDirectory(), "saves", "server.sav"), [1]).Object;
        savesLoadersService.Setup(x => x.GetAllSaveInfos()).Returns(new Dictionary<uint, SaveInfosDTO>
        {
            [saveId] = SaveInfosDTO.FromSave(serverSave, null, DateTime.UtcNow, []),
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );

        Assert.Equal(ConflictException.SAVE_ID_CONFLICT, ex.Code);
        savesLoadersService.Verify(x => x.UploadSaveWithoutCheck(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task OpenSession_ShouldRefuse_WhenSameSaveIdIsUploadedByAnotherDevice()
    {
        var saveId = await SetupUploadedSave();
        await Seed();

        var otherDevicePath = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), "device-0002", saveId), "main.sav");
        var otherDeviceSave = SaveWrapperTests.GetMockSave(otherDevicePath, [1]).Object;
        savesLoadersService.Setup(x => x.GetAllSaveInfos()).Returns(new Dictionary<uint, SaveInfosDTO>
        {
            [saveId] = SaveInfosDTO.FromSave(otherDeviceSave, null, DateTime.UtcNow, []),
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );

        Assert.Equal(ConflictException.SAVE_ID_CONFLICT, ex.Code);
    }

    [Fact]
    public async Task OpenSession_ShouldRefuse_WhenServerHasDuplicateOfTheSaveId()
    {
        // the same device copy won the play-time tie-break, but a server file with the same ID exists too
        var saveId = await SetupUploadedSave();
        await Seed();

        var ownPath = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), DEVICE, saveId), "main.sav");
        var ownSave = SaveWrapperTests.GetMockSave(ownPath, [1]).Object;
        var serverDuplicate = SaveInfosDTO.FromSave(
            SaveWrapperTests.GetMockSave(Path.Combine(Directory.GetCurrentDirectory(), "saves", "server.sav"), [1]).Object,
            null, DateTime.UtcNow, []
        );
        savesLoadersService.Setup(x => x.GetAllSaveInfos()).Returns(new Dictionary<uint, SaveInfosDTO>
        {
            [saveId] = SaveInfosDTO.FromSave(ownSave, null, DateTime.UtcNow, [serverDuplicate]),
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.OpenSession(DEVICE, "main.sav", new MemoryStream([1]))
        );

        Assert.Equal(ConflictException.SAVE_ID_CONFLICT, ex.Code);
    }

    [Fact]
    public async Task CloseSession_ShouldRefuse_WhenActionsArePending()
    {
        sessionService.Setup(s => s.HasEmptyActionList()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseSession(DEVICE, 111));
        mockFileIO.Verify(x => x.Delete(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Ping_ShouldReturnVersionAndCommitSeq()
    {
        await Seed();
        db.Metas.Add(new MetaEntity { Key = MetaKey.COMMIT_SEQ, Value = "7" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await service.Ping();

        Assert.Equal("1.2.3", result.Version);
        Assert.Equal(7, result.CommitSeq);
    }
}
