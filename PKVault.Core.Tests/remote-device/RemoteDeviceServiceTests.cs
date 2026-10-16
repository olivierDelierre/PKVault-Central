using System.Net;
using Moq;
using PKVault.Core;

public class RemoteDeviceServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TransferTestEnv env = new();
    private readonly FakeDeviceStore store = new();
    private readonly InMemoryRemoteSecretStore secrets = new();
    private readonly Mock<ISavesLoadersService> savesLoaders = new();
    private readonly Mock<ISessionService> session = new();
    private readonly StubHttpHandler handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"actions":[]}"""));
    private readonly TransferCoordinator coordinator;
    private readonly RemoteDeviceService service;
    private readonly SettingsDTO settings;

    public RemoteDeviceServiceTests()
    {
        Program.Initialize();

        store.ServerUrl = "https://nas.local:3000";
        session.Setup(s => s.HasEmptyActionList()).Returns(true);

        settings = new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.ANDROID, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        );
        var settingsService = new Mock<ISettingsService>();
        settingsService.Setup(x => x.GetSettings()).Returns(settings);

        SetDeviceSaves(TransferTestEnv.DevicePath);

        coordinator = env.NewCoordinator();
        var proxy = new RemoteProxy(new RemoteHttpConnection(new HttpClient(handler), new FakeConnectionInfo()), coordinator);

        service = new RemoteDeviceService(
            store, secrets, coordinator, env.Server, proxy, savesLoaders.Object, session.Object, settingsService.Object
        );
    }

    private static SaveInfosDTO CreateSaveInfos(string path)
        => SaveInfosDTO.FromSave(SaveWrapperTests.GetMockSave(path, [1]).Object, null, DateTime.UtcNow, []);

    private void SetDeviceSaves(params string[] paths)
    {
        var infos = paths.Select(CreateSaveInfos).ToList();
        // the mock saves all share the same id: only the last one is kept by id, so the id is made up
        savesLoaders.Setup(x => x.GetAllSaveInfos()).Returns(
            infos.Select((info, i) => (Id: (uint)(1000 + i), Info: info)).ToDictionary(p => p.Id, p => p.Info)
        );
    }

    private static DesktopFetchRequestInit Get() => new("GET", []);

    // --- status

    [Fact]
    public async Task Status_InLocalMode_ShouldNotTalkToTheServer()
    {
        var status = await service.GetStatus(Ct);

        Assert.Equal("LOCAL", status.Mode);
        Assert.False(status.Reachable);
        Assert.Null(status.ConnectionError);
        Assert.Empty(status.Transfers);
        Assert.Empty(env.Server.Calls);
    }

    [Fact]
    public async Task Status_InRemoteMode_ShouldTellIfTheServerIsReachable()
    {
        store.Mode = DeviceBankMode.REMOTE;

        var ok = await service.GetStatus(Ct);

        Assert.True(ok.Reachable);
        Assert.True(ok.Authorized);
        Assert.Null(ok.ConnectionError);

        env.Server.PingError = new RemoteUnreachableException("No route to host");
        var down = await service.GetStatus(Ct);

        Assert.False(down.Reachable);
        Assert.Contains("No route", down.ConnectionError);

        env.Server.PingError = new RemoteApiException(401, "Unauthorized");
        var refused = await service.GetStatus(Ct);

        Assert.True(refused.Reachable);
        Assert.False(refused.Authorized);
        Assert.Equal("UNAUTHORIZED", refused.ConnectionError);
    }

    [Fact]
    public async Task Status_ShouldNeverGiveTheTokenBack()
    {
        await service.SetConfig(new(null, "super-secret"), Ct);

        var status = await service.GetStatus(Ct);

        Assert.True(status.HasToken);
        Assert.DoesNotContain("super-secret", System.Text.Json.JsonSerializer.Serialize(status, RouteJsonContext.DefaultWithOptions.GetTypeInfo(typeof(DeviceRemoteStatusDTO))!));
    }

    [Fact]
    public async Task Status_ShouldListUnfinishedTransfers_AndFinishedOnesWithSomethingToKnow()
    {
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);

        var status = await service.GetStatus(Ct);

        var transfer = Assert.Single(status.Transfers);
        Assert.Equal("UPLOADED", transfer.State);
        Assert.Equal(FakeRemoteServer.SAVE_ID, transfer.SaveId);
        Assert.Equal("main.sav", transfer.FileName);

        // once delivered, it only shows while the local saves still have to be reloaded
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        env.Refresher.Result = false;
        await coordinator.EndCommit(true, Ct);

        var reloadPending = Assert.Single((await service.GetStatus(Ct)).Transfers);
        Assert.Equal("ACKED", reloadPending.State);
        Assert.True(reloadPending.LocalReloadPending);

        env.Refresher.Result = true;
        await coordinator.Resume(Ct);

        Assert.Empty((await service.GetStatus(Ct)).Transfers);
    }

    [Fact]
    public async Task Status_ShouldResumePausedTransfers_WhenTheServerIsBack()
    {
        store.Mode = DeviceBankMode.REMOTE;
        env.Server.FaultAt = (index, op) => op == "open" ? FaultKind.Before : FaultKind.None;
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        Assert.Equal(DeviceTransferState.UPLOADING, env.OnlyEntry.State);

        env.Server.FaultAt = null;
        await service.GetStatus(Ct);

        await service.BackgroundResume!;
        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
    }

    [Fact]
    public async Task Status_ShouldNotResume_WhenTheServerIsUnreachable_OrNothingCanMove()
    {
        store.Mode = DeviceBankMode.REMOTE;
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);    // UPLOADED: waits for the user
        env.Server.Calls.Clear();

        await service.GetStatus(Ct);

        Assert.Null(service.BackgroundResume);
        Assert.Equal(["ping"], env.Server.Calls);
    }

    // --- config

    [Fact]
    public async Task SetConfig_ShouldCleanTheAddress_AndKeepTheTokenSecret()
    {
        var status = await service.SetConfig(new("  https://home.example/pkvault/  ", "  tok  "), Ct);

        Assert.Equal("https://home.example/pkvault", status.ServerUrl);
        Assert.Equal("https://home.example/pkvault", store.ServerUrl);
        Assert.Equal("tok", secrets.Token);
        Assert.True(status.HasToken);
    }

    [Fact]
    public async Task SetConfig_ShouldLeaveUnchangedWhatIsNull_AndRemoveWhatIsEmpty()
    {
        await service.SetConfig(new(null, "tok"), Ct);
        Assert.Equal("https://nas.local:3000", store.ServerUrl);

        await service.SetConfig(new("http://other:3000", null), Ct);
        Assert.Equal("tok", secrets.Token);

        await service.SetConfig(new("", ""), Ct);
        Assert.Null(store.ServerUrl);
        Assert.Null(secrets.Token);
    }

    [Theory]
    [InlineData("nas.local")]
    [InlineData("ftp://nas.local")]
    [InlineData("not a url")]
    public async Task SetConfig_ShouldRefuseAnAddressWhichIsNotHttp(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetConfig(new(url, null), Ct));

        Assert.Equal("https://nas.local:3000", store.ServerUrl);
    }

    [Fact]
    public async Task SetConfig_ShouldRefuseChangingTheServer_WhileSavesAreSentToIt()
    {
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetConfig(new("https://other.example", null), Ct));
        Assert.Equal("https://nas.local:3000", store.ServerUrl);

        // same address and the token can still be edited
        await service.SetConfig(new("https://nas.local:3000/", "new-token"), Ct);
        Assert.Equal("new-token", secrets.Token);
    }

    [Fact]
    public async Task TestConnection_ShouldReturnTheServerVersion_OrTheError()
    {
        Assert.Equal("test", (await service.TestConnection(Ct)).Version);

        env.Server.PingError = new RemoteApiException(401, "Unauthorized");
        var ex = await Assert.ThrowsAsync<RemoteApiException>(() => service.TestConnection(Ct));
        Assert.Equal(401, ex.Status);
    }

    // --- mode

    [Fact]
    public async Task SetMode_Remote_ShouldNeedAServerThatAnswers()
    {
        store.ServerUrl = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMode(DeviceBankMode.REMOTE, Ct));

        store.ServerUrl = "https://nas.local:3000";
        env.Server.PingError = new RemoteUnreachableException("down");
        await Assert.ThrowsAsync<RemoteUnreachableException>(() => service.SetMode(DeviceBankMode.REMOTE, Ct));

        env.Server.PingError = new RemoteApiException(401, "Unauthorized");
        var ex = await Assert.ThrowsAsync<RemoteApiException>(() => service.SetMode(DeviceBankMode.REMOTE, Ct));
        Assert.Equal(401, ex.Status);

        Assert.Equal(DeviceBankMode.LOCAL, store.Mode);
    }

    [Fact]
    public async Task SetMode_Remote_ShouldWaitForLocalActionsToBeSavedOrDiscarded()
    {
        session.Setup(s => s.HasEmptyActionList()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMode(DeviceBankMode.REMOTE, Ct));

        Assert.Equal(DeviceBankMode.LOCAL, store.Mode);
    }

    [Fact]
    public async Task SetMode_ShouldSwitchBackAndForth()
    {
        var remote = await service.SetMode(DeviceBankMode.REMOTE, Ct);
        Assert.Equal("REMOTE", remote.Mode);
        Assert.Equal(DeviceBankMode.REMOTE, store.Mode);

        var local = await service.SetMode(DeviceBankMode.LOCAL, Ct);
        Assert.Equal("LOCAL", local.Mode);
    }

    [Fact]
    public async Task SetMode_Local_ShouldBeRefused_WhileASaveIsBeingDeliveredToTheDevice()
    {
        store.Mode = DeviceBankMode.REMOTE;
        env.Server.FaultAt = (_, op) => op == "download" ? FaultKind.Before : FaultKind.None;
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.COMMITTED_ON_SERVER, env.OnlyEntry.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMode(DeviceBankMode.LOCAL, Ct));
        Assert.Equal(DeviceBankMode.REMOTE, store.Mode);

        // once delivered, it is possible
        env.Server.FaultAt = null;
        await coordinator.Resume(Ct);
        await service.SetMode(DeviceBankMode.LOCAL, Ct);
        Assert.Equal(DeviceBankMode.LOCAL, store.Mode);
    }

    [Fact]
    public async Task SetMode_Local_ShouldBeAllowed_WhileASaveOnlyWaitsForEdits()
    {
        store.Mode = DeviceBankMode.REMOTE;
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);

        await service.SetMode(DeviceBankMode.LOCAL, Ct);

        Assert.Equal(DeviceBankMode.LOCAL, store.Mode);
    }

    // --- forwarding

    [Fact]
    public async Task TryForward_ShouldLeaveEverythingToTheLocalRouter_InLocalMode()
    {
        var response = await service.TryForward("https://0.0.0.0/api/storage/main/bank", Get(), Ct);

        Assert.Null(response);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryForward_InRemoteMode_ShouldSendToTheServer_ExceptTheDeviceRoutes()
    {
        store.Mode = DeviceBankMode.REMOTE;

        Assert.Null(await service.TryForward("https://0.0.0.0/api/device/status", Get(), Ct));
        Assert.Null(await service.TryForward("https://0.0.0.0/api/settings", Get(), Ct));
        Assert.Null(await service.TryForward("https://0.0.0.0/api/static-data/spritesheet/species", Get(), Ct));
        Assert.Empty(handler.Requests);

        var response = await service.TryForward("https://0.0.0.0/api/storage/main/bank", Get(), Ct);

        Assert.NotNull(response);
        Assert.Equal(200, response.status);
        Assert.Single(handler.Requests);
    }

    // --- device saves & transfers

    [Fact]
    public void GetDeviceSaves_ShouldListTheSavesOfTheDevice_WithTheirActiveTransfer()
    {
        var otherPath = Path.Combine(Directory.GetCurrentDirectory(), "saves", "other.sav");
        SetDeviceSaves(TransferTestEnv.DevicePath, otherPath);

        var saves = service.GetDeviceSaves();

        Assert.Equal(2, saves.Count);
        Assert.All(saves, s => Assert.Null(s.Transfer));
    }

    [Fact]
    public async Task GetDeviceSaves_ShouldAttachTheTransferInProgress_ButNotAFinishedOne()
    {
        store.Mode = DeviceBankMode.REMOTE;
        var infos = savesLoaders.Object.GetAllSaveInfos().Values.Single();
        await service.OpenOnServer(savesLoaders.Object.GetAllSaveInfos().Keys.Single(), Ct);

        var save = Assert.Single(service.GetDeviceSaves());
        Assert.Equal(infos.Path, save.Save.Path);
        Assert.Equal("UPLOADED", save.Transfer!.State);

        await service.CloseOnServer(save.Transfer.LocalId, Ct);

        Assert.Null(Assert.Single(service.GetDeviceSaves()).Transfer);
    }

    [Fact]
    public void GetDeviceSaves_ShouldHideSavesReceivedWhenActingAsAServer()
    {
        var received = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), "device-0002", 5), "main.sav");
        SetDeviceSaves(TransferTestEnv.DevicePath, received);

        var save = Assert.Single(service.GetDeviceSaves());

        Assert.Equal(TransferTestEnv.DevicePath, save.Save.Path);
    }

    [Fact]
    public async Task OpenOnServer_ShouldSendTheSaveFileOfTheDevice()
    {
        store.Mode = DeviceBankMode.REMOTE;

        var transfer = await service.OpenOnServer(1000, Ct);

        Assert.Equal("UPLOADED", transfer.State);
        Assert.Equal(FakeRemoteServer.SAVE_ID, transfer.SaveId);
        Assert.Equal(TransferTestEnv.OriginalBytes, env.Server.OpenSessions[FakeRemoteServer.SAVE_ID]);
    }

    [Fact]
    public async Task OpenOnServer_ShouldNeedRemoteMode_AndAKnownSave()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenOnServer(1000, Ct));

        store.Mode = DeviceBankMode.REMOTE;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenOnServer(4242, Ct));

        Assert.Empty(env.Server.Calls);
        Assert.Empty(env.Journal.LoadAll());
    }

    [Fact]
    public async Task OpenOnServer_ShouldRefuseASaveReceivedWhenActingAsAServer()
    {
        store.Mode = DeviceBankMode.REMOTE;
        var received = Path.Combine(RemoteSavePath.GetSaveDirectory(settings.GetRemoteSavesPath(), "device-0002", 5), "main.sav");
        SetDeviceSaves(received);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenOnServer(1000, Ct));
    }

    [Fact]
    public async Task OpenOnServer_ShouldTellWhenTheServerAlreadyHasTheSaveFromElsewhere()
    {
        store.Mode = DeviceBankMode.REMOTE;
        env.Server.SaveIdTakenByAnotherSource = true;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.OpenOnServer(1000, Ct));

        Assert.Equal(ConflictException.SAVE_ID_CONFLICT, ex.Code);
        Assert.Empty(env.Server.OpenSessions);
    }

    [Fact]
    public async Task OpenOnServer_WhenTheServerIsUnreachable_ShouldKeepTheTransferPaused()
    {
        store.Mode = DeviceBankMode.REMOTE;
        env.Server.FaultAt = (_, op) => op == "open" ? FaultKind.Before : FaultKind.None;

        var transfer = await service.OpenOnServer(1000, Ct);

        Assert.Equal("UPLOADING", transfer.State);
        Assert.NotNull(transfer.LastError);

        env.Server.FaultAt = null;
        var status = await service.Sync(Ct);

        Assert.Equal("UPLOADED", Assert.Single(status.Transfers).State);
    }

    [Fact]
    public async Task CloseOnServer_ShouldEndTheTransfer()
    {
        store.Mode = DeviceBankMode.REMOTE;
        var transfer = await service.OpenOnServer(1000, Ct);

        var closed = await service.CloseOnServer(transfer.LocalId, Ct);

        Assert.Equal("CLOSED", closed.State);
        Assert.Empty(env.Server.OpenSessions);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CloseOnServer("unknown", Ct));
    }

    [Fact]
    public async Task ResolveConflict_ShouldGoThroughTheEngine()
    {
        store.Mode = DeviceBankMode.REMOTE;
        var transfer = await service.OpenOnServer(1000, Ct);
        env.Device.Files[TransferTestEnv.DevicePath] = TransferTestEnv.PlayedBytes.ToArray();
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(true, Ct);

        var status = await service.GetStatus(Ct);
        var conflict = Assert.Single(status.Transfers);
        Assert.Equal("CONFLICT", conflict.State);
        Assert.Equal("DEVICE_SAVE_CHANGED", conflict.ConflictReason);

        var resolved = await service.ResolveConflict(transfer.LocalId, TransferConflictResolution.ABORT_ON_SERVER, Ct);

        Assert.Equal("CLOSED", resolved.State);
        Assert.Equal(TransferTestEnv.PlayedBytes, env.DeviceBytes);
        Assert.Empty(env.Server.Deliveries);
    }
}
