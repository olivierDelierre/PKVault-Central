using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKHeX.Core;
using PKVault.Core;

public class SavesLoadersServiceRemoteTests
{
    private readonly string cwd = Directory.GetCurrentDirectory();
    private readonly MockFileSystem mockFileSystem;
    private readonly FileIOService fileIOService;
    private readonly byte[] saveBytes;

    public SavesLoadersServiceRemoteTests()
    {
        Program.Initialize();

        mockFileSystem = new(new Dictionary<string, MockFileData>(), cwd);
        fileIOService = new FileIOService(mockFileSystem);
        fileIOService.Matcher.GetAllPaths = (rootDir, globs) => [.. mockFileSystem.AllFiles];

        saveBytes = File.ReadAllBytes(Path.Combine(Program.InitialCurrentDirectory, "./assets/Pokemon - Version Saphir (France).srm"));
    }

    /// <summary>
    /// The save ID must come from the service loading path (it can differ from a raw PKHeX load,
    /// ex: language revision), as it names the remote save folder.
    /// </summary>
    private async Task<uint> GetSaveId(SavesLoadersService service)
        => (await service.CheckSaveData(saveBytes, "any.srm", overwrite: true)).Id;

    private (SavesLoadersService Service, SettingsDTO Settings) CreateService(string[] saveGlobs)
    {
        var settings = new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: saveGlobs, PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        );
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetSettings()).Returns(settings);

        var service = new SavesLoadersService(
            new ServiceCollection().BuildServiceProvider(),
            fileIOService,
            mockSettings.Object,
            new Mock<IPkmConvertService>().Object,
            new StaticDataService(mockSettings.Object),
            new PKMConverterUtils()
        );

        return (service, settings);
    }

    private string AddRemoteSave(SettingsDTO settings, uint saveId, string deviceId = "device-1")
    {
        var path = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), deviceId, saveId), "main.srm");
        mockFileSystem.AddFile(path, new MockFileData(saveBytes));
        return path;
    }

    [Fact]
    public async Task Setup_ShouldLoadSaveFromRemoteFolder_EvenWithoutAnySaveGlob()
    {
        var (service, settings) = CreateService(saveGlobs: []);
        var saveId = await GetSaveId(service);
        AddRemoteSave(settings, saveId);

        await service.Setup(new DataUpdateFlags());

        var saves = service.GetSaveById();
        Assert.Single(saves);
        Assert.True(settings.IsRemoteSavePath(saves[saveId].Metadata.FilePath!));
    }

    [Fact]
    public async Task Setup_ShouldLoadRemoteSaveOnce_WhenBroadUserGlobAlsoMatchesIt()
    {
        var (service, settings) = CreateService(saveGlobs: ["./**"]);
        var saveId = await GetSaveId(service);
        AddRemoteSave(settings, saveId);

        await service.Setup(new DataUpdateFlags());

        Assert.Single(service.GetSaveById());
        Assert.Empty(service.GetLoadersRequired(saveId).Duplicates);
    }

    [Fact]
    public async Task WriteToFiles_ShouldNotWriteRemoteSaveToDisk_ButReturnItsBytes()
    {
        var (service, settings) = CreateService(saveGlobs: []);
        var saveId = await GetSaveId(service);
        var path = AddRemoteSave(settings, saveId);

        await service.Setup(new DataUpdateFlags());

        // mark the save as modified, like an action would
        var loaders = service.GetLoadersRequired(saveId);
        loaders.Pkms.HasWritten = true;

        var results = await service.WriteToFiles();

        var result = Assert.Single(results);
        Assert.True(result.IsRemote);
        Assert.Equal(saveId, result.SaveId);
        Assert.NotEmpty(result.Bytes);
        // file on disk is still the uploaded original: it is the reference for the delivery hash
        Assert.Equal(saveBytes, mockFileSystem.File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteToFiles_ShouldWriteLocalSaveToDisk_AndFlagItAsNotRemote()
    {
        var (service, _) = CreateService(saveGlobs: ["./saves/*.srm"]);
        var saveId = await GetSaveId(service);
        var path = Path.Combine(cwd, "saves", "local.srm");
        mockFileSystem.AddFile(path, new MockFileData(saveBytes));

        await service.Setup(new DataUpdateFlags());

        service.GetLoadersRequired(saveId).Pkms.HasWritten = true;

        var results = await service.WriteToFiles();

        var result = Assert.Single(results);
        Assert.False(result.IsRemote);
        Assert.Equal(result.Bytes, mockFileSystem.File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteToFiles_ShouldReturnNothing_WhenNoSaveWasModified()
    {
        var (service, settings) = CreateService(saveGlobs: []);
        AddRemoteSave(settings, await GetSaveId(service));

        await service.Setup(new DataUpdateFlags());

        Assert.Empty(await service.WriteToFiles());
    }
}
