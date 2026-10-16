using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKVault.Core;

public class ActionServiceTests
{
    private readonly MockFileSystem mockFileSystem;
    private readonly IFileIOService fileIOService;
    private readonly Mock<ISettingsService> mockSettingsService = new();
    private readonly Mock<ISessionService> mockSessionService = new();

    public ActionServiceTests()
    {
        Program.Initialize();

        mockFileSystem = new(new Dictionary<string, MockFileData>(), Directory.GetCurrentDirectory());
        fileIOService = new FileIOService(mockFileSystem);
        fileIOService.Matcher.GetAllPaths = (rootDir, globs) => [.. mockFileSystem.AllFiles];
    }

    private ActionService GetService(DateTime now, bool throwOnSessionPersist = false, IDeviceSaveLock? saveLock = null, bool saveHasWrites = false)
    {
        var serviceCollection = new ServiceCollection();

        var mockTimeProvider = new Mock<TimeProvider>();
        mockTimeProvider.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(now));

        mockFileSystem.AddFile("mock-main.db", "mock-db");  // main db
        DataNormalizeAction.GetLegacyFilepaths("legacy")
            .ForEach(legacyPath => mockFileSystem.AddFile(legacyPath, "mock-legacy-data"));

        mockSessionService.Setup(x => x.MainDbPath).Returns(Path.Combine(PathUtils.GetExpectedAppDirectory(), "mock-main.db"));
        mockSessionService.Setup(x => x.MainDbRelativePath).Returns("mock-main.db");
        if (throwOnSessionPersist)
        {
            mockSessionService.Setup(x => x.PersistSession(It.IsAny<IServiceScope>())).ThrowsAsync(new Exception());
        }

        mockFileSystem.AddFile("mock-pkm-files/123", "mock-data");

        var mockPkmFileService = new Mock<IPkmFileLoader>();
        mockPkmFileService.Setup(x => x.GetEnabledFilepaths()).ReturnsAsync([
            "mock-pkm-files/123",
            "mock-pkm-files/456",
        ]);

        serviceCollection.AddSingleton(mockPkmFileService.Object);
        if (saveLock != null)
        {
            serviceCollection.AddSingleton(saveLock);
        }

        var sp = serviceCollection.BuildServiceProvider();

        Mock<ISavesLoadersService> savesLoadersService = new();

        var mockSavePath = MatcherUtil.NormalizePath(Path.Combine(PathUtils.GetExpectedAppDirectory(), "mock-save-path"));
        mockFileSystem.AddFile(mockSavePath, "mock-save-content");

        var saveWrapper = SaveWrapperTests.GetMockSave(mockSavePath, Encoding.ASCII.GetBytes("mock-save-content"));
        savesLoadersService.Setup(x => x.GetSaveById()).Returns(new Dictionary<uint, SaveWrapper>()
        {
            {saveWrapper.Object.Id, saveWrapper.Object}
        });

        var mockBoxes = new Mock<ISaveBoxLoader>();
        var mockPkms = new Mock<ISavePkmLoader>();
        mockPkms.Setup(x => x.HasWritten).Returns(saveHasWrites);
        var mockInventory = new Mock<ISaveInventoryLoader>();
        savesLoadersService.Setup(x => x.GetAllLoaders()).Returns([
            new SaveLoadersRecord(saveWrapper.Object, mockBoxes.Object, mockPkms.Object, mockInventory.Object, []),
        ]);

        return new(
            sp: sp,
            pkmUpdateService: new(),
            backupService: new(
                sp: sp,
                mockTimeProvider.Object,
                fileIOService,
                savesLoadersService.Object,
                mockSettingsService.Object,
                mockSessionService.Object
            ),
            settingsService: mockSettingsService.Object,
            sessionService: mockSessionService.Object,
            savesLoadersService: savesLoadersService.Object
        );
    }

    [Fact]
    public async Task Save_CreatesBackupFile()
    {
        ConfigureSettings("mock-bkp");

        var actionService = GetService(
            now: DateTime.Parse("2015-03-21 23:26:11")
        );

        mockSessionService.Setup(x => x.Actions).Returns([
            new(
                ActionFn: async (scope, flags) => new(DataActionType.DATA_NORMALIZE, []),
                new(DataActionType.DATA_NORMALIZE, [])
            )
        ]);

        var flags = await actionService.Save();

        Assert.True(mockFileSystem.FileExists(
                Path.Combine(PathUtils.GetExpectedAppDirectory(), "mock-bkp", "backup_before_save_2015-03-21T232611-000Z.zip")
            ),
            $"File is missing, list of current files:\n{string.Join('\n', mockFileSystem.AllFiles)}");
    }

    private void AddPendingAction()
    {
        mockSessionService.Setup(x => x.Actions).Returns([
            new(
                ActionFn: async (scope, flags) => new(DataActionType.DATA_NORMALIZE, []),
                new(DataActionType.DATA_NORMALIZE, [])
            )
        ]);
    }

    [Fact]
    public async Task Save_ShouldRefuse_WhenAWrittenSaveIsBeingEditedOnTheRemoteBank()
    {
        ConfigureSettings("mock-bkp");
        var saveLock = new Mock<IDeviceSaveLock>();
        saveLock.Setup(x => x.IsLocked(It.IsAny<string>())).Returns(true);
        var actionService = GetService(DateTime.Parse("2015-03-21 23:26:11"), saveLock: saveLock.Object, saveHasWrites: true);
        AddPendingAction();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(actionService.Save);

        Assert.Contains("mock-save-path", ex.Message);
        Assert.Contains("remote bank", ex.Message);
        // refused before anything was done: no backup, nothing persisted
        Assert.DoesNotContain(mockFileSystem.AllFiles, f => f.Contains("backup_before_save"));
        mockSessionService.Verify(x => x.PersistSession(It.IsAny<IServiceScope>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false)]    // locked but this save is not written
    [InlineData(false, true)]    // written but not locked
    public async Task Save_ShouldGoOn_WhenTheSaveWrittenIsNotTheLockedOne(bool locked, bool saveHasWrites)
    {
        ConfigureSettings("mock-bkp");
        var saveLock = new Mock<IDeviceSaveLock>();
        saveLock.Setup(x => x.IsLocked(It.IsAny<string>())).Returns(locked);
        var actionService = GetService(DateTime.Parse("2016-03-21 23:26:11"), saveLock: saveLock.Object, saveHasWrites: saveHasWrites);
        AddPendingAction();

        await actionService.Save();

        Assert.Contains(mockFileSystem.AllFiles, f => f.Contains("backup_before_save"));
    }

    [Fact]
    public async Task Save_RestoreBackupOnException()
    {
        ConfigureSettings("mock-bkp");

        var actionService = GetService(
            now: DateTime.Parse("2018-03-21 14:26:11"),
            throwOnSessionPersist: true
        );


        mockSessionService.Setup(x => x.Actions).Returns([
            new(
                ActionFn: async (scope, flags) => new(DataActionType.DATA_NORMALIZE, []),
                new(DataActionType.DATA_NORMALIZE, [])
            )
        ]);

        await Assert.ThrowsAnyAsync<Exception>(actionService.Save);

        Assert.True(mockFileSystem.FileExists(Path.Combine(PathUtils.GetExpectedAppDirectory(), "mock-bkp", "backup_before_save_2018-03-21T142611-000Z.zip")));

        Assert.True(mockFileSystem.FileExists("mock-main.db"));

        DataNormalizeAction.GetLegacyFilepaths("legacy")
            .ForEach(legacyPath => Assert.True(mockFileSystem.FileExists(legacyPath)));

        Assert.True(mockFileSystem.FileExists(Path.Combine(PathUtils.GetExpectedAppDirectory(), "mock-save-path")), string.Join('\n', mockFileSystem.AllFiles));
        Assert.True(mockFileSystem.FileExists(Path.Combine("mock-pkm-files", "123")));
    }

    private void ConfigureSettings(
        string backupPath
    )
    {
        mockSettingsService.Setup(x => x.GetSettings()).Returns(new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: backupPath,
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        ));
    }
}
