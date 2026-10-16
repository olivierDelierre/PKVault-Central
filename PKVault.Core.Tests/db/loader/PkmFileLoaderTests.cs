using Microsoft.EntityFrameworkCore;
using Moq;
using PKVault.Core;

public class PkmFileLoaderTests : IAsyncDisposable
{
    private readonly string dbPath;
    private readonly SessionDbContext db;
    private readonly Mock<IFileIOService> mockFileIO;
    private readonly PkmFileLoader pkmFileLoader;
    private int completedWrites;

    public PkmFileLoaderTests()
    {
        Program.Initialize();

        dbPath = $"db-PkmFileLoaderTests-{Guid.NewGuid()}.db";

        var sessionService = new Mock<ISessionServiceMinimal>();
        sessionService.Setup(s => s.SessionDbPath).Returns(dbPath);

        mockFileIO = new();
        // slow write: only completes if WriteToFiles actually awaits it
        mockFileIO.Setup(x => x.WriteBytes(It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns(async () =>
            {
                await Task.Delay(50);
                Interlocked.Increment(ref completedWrites);
            });

        db = new(sessionService.Object, new Mock<DbSeedingService>(mockFileIO.Object).Object);

        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetSettings()).Returns(new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        ));

        pkmFileLoader = new PkmFileLoader(mockFileIO.Object, sessionService.Object, mockSettings.Object, db);
    }

    public async ValueTask DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WriteToFiles_ShouldAwaitEveryWrite_BeforeReturning()
    {
        await db.Database.MigrateAsync();

        for (var i = 0; i < 5; i++)
        {
            db.PkmFiles.Add(new PkmFileEntity
            {
                Filepath = $"mock-storage/file-{i}.pk7",
                Data = [1, 2, 3],
                Error = null,
                Updated = true,
                Deleted = false,
            });
        }
        await db.SaveChangesAsync();

        await pkmFileLoader.WriteToFiles();

        Assert.Equal(5, completedWrites);
        mockFileIO.Verify(x => x.WriteBytes(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Exactly(5));
    }

    [Fact]
    public async Task WriteToFiles_ShouldPropagateWriteFailure()
    {
        await db.Database.MigrateAsync();

        mockFileIO.Setup(x => x.WriteBytes(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ThrowsAsync(new IOException("disk full"));

        db.PkmFiles.Add(new PkmFileEntity
        {
            Filepath = "mock-storage/file.pk7",
            Data = [1],
            Error = null,
            Updated = true,
            Deleted = false,
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<IOException>(() => pkmFileLoader.WriteToFiles());

        // row must keep its pending state so nothing is silently lost
        var row = await db.PkmFiles.SingleAsync();
        Assert.True(row.Updated);
        Assert.NotEmpty(row.Data);
    }
}
