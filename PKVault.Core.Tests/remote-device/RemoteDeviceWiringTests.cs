using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKVault.Core;

public class RemoteDeviceWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RemoteDeviceWiringTests()
    {
        Program.Initialize();
    }

    // --- local saves reload

    [Fact]
    public async Task LocalSavesRefresher_ShouldReloadTheSaves_LikeTheReloadButton()
    {
        var settings = new Mock<ISettingsService>();
        var saves = new Mock<ISavesLoadersService>();
        var session = new Mock<ISessionService>();
        session.Setup(s => s.HasEmptyActionList()).Returns(true);
        List<string> calls = [];
        settings.Setup(x => x.RefreshSettings(It.IsAny<DataUpdateFlags>())).Callback(() => calls.Add("settings"));
        saves.Setup(x => x.Clear()).Callback(() => calls.Add("clear"));
        session.Setup(x => x.StartNewSession(true, It.IsAny<DataUpdateFlags>())).Callback(() => calls.Add("session"));

        var done = await new LocalSavesRefresher(settings.Object, saves.Object, session.Object).TryRefresh();

        Assert.True(done);
        Assert.Equal(["settings", "clear", "session"], calls);
    }

    [Fact]
    public async Task LocalSavesRefresher_ShouldWait_WhenLocalActionsArePending()
    {
        var saves = new Mock<ISavesLoadersService>();
        var session = new Mock<ISessionService>();
        session.Setup(s => s.HasEmptyActionList()).Returns(false);

        var done = await new LocalSavesRefresher(new Mock<ISettingsService>().Object, saves.Object, session.Object).TryRefresh();

        // reloading would drop the actions the user has not saved
        Assert.False(done);
        saves.Verify(x => x.Clear(), Times.Never);
        session.Verify(x => x.StartNewSession(It.IsAny<bool>(), It.IsAny<DataUpdateFlags>()), Times.Never);
    }

    // --- registration

    private static ServiceCollection CreateServices(bool withRemoteDevice)
    {
        var services = new ServiceCollection();
        Program.ConfigureServices(services);

        if (withRemoteDevice)
        {
            services.AddSingleton<IRemoteDeviceStore>(new InMemoryRemoteDeviceStore(Path.Combine(Directory.GetCurrentDirectory(), "journal")));
            services.AddSingleton<IRemoteSecretStore, InMemoryRemoteSecretStore>();
            services.AddRemoteDevice();
        }

        return services;
    }

    [Fact]
    public void AddRemoteDevice_ShouldMakeEverythingResolvable_WithOneCoordinatorForTheWholeApp()
    {
        using var sp = CreateServices(withRemoteDevice: true).BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<RemoteDeviceService>());
        Assert.NotNull(sp.GetRequiredService<RemoteProxy>());
        Assert.IsType<HttpRemoteApiClient>(sp.GetRequiredService<IRemoteApiClient>());

        // transfers are advanced one step at a time: two coordinators would break that
        Assert.Same(sp.GetRequiredService<TransferCoordinator>(), sp.GetRequiredService<TransferCoordinator>());
        Assert.Same(sp.GetRequiredService<RemoteDeviceService>(), sp.GetRequiredService<RemoteDeviceService>());
    }

    [Fact]
    public void AddRemoteDevice_ShouldNotBeNeeded_ByTheOtherHosts()
    {
        using var sp = CreateServices(withRemoteDevice: false).BuildServiceProvider();

        Assert.Null(sp.GetService<RemoteDeviceService>());
        Assert.NotNull(sp.GetRequiredService<CoreRouter>());
    }

    // --- routes through the real router

    private static async Task<ICoreResponse> Dispatch(IServiceProvider sp, string method, string path, string query = "")
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreRouter>()
            .Dispatch(Task.CompletedTask, scope.ServiceProvider, method, path, query, new MemoryStream(Encoding.UTF8.GetBytes("")));
    }

    [Fact]
    public async Task DeviceRoutes_ShouldRefuseClearly_WhereThereIsNoRemoteBank()
    {
        using var sp = CreateServices(withRemoteDevice: false).BuildServiceProvider();

        var response = await Dispatch(sp, "GET", "/api/device/status");

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("only available in the Android app", response.Header["error-message"]);
    }

    private static ServiceProvider CreateServicesWithServer(FakeRemoteServer server)
    {
        var services = CreateServices(withRemoteDevice: true);
        // the HTTP client is replaced by a fake server
        services.AddSingleton<IRemoteApiClient>(server);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DeviceRoutes_ShouldAnswerStatus_ThroughTheRouter()
    {
        using var sp = CreateServicesWithServer(new FakeRemoteServer());

        var response = await Dispatch(sp, "GET", "/api/device/status");

        Assert.Equal(200, response.StatusCode);
        var status = Assert.IsType<DeviceRemoteStatusDTO>(Assert.IsType<CoreJSONResponse>(response).Data);
        Assert.Equal("LOCAL", status.Mode);
    }

    [Fact]
    public async Task ServerErrors_ShouldReachTheUI_WithTheirStatusAndCode()
    {
        var server = new FakeRemoteServer
        {
            PingError = new RemoteApiException(401, "Unauthorized: missing or invalid API token", "BAD_TOKEN", "some-data"),
        };
        using var sp = CreateServicesWithServer(server);

        var response = await Dispatch(sp, "POST", "/api/device/test");

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("BAD_TOKEN", response.Header["error-code"]);
        Assert.Equal("some-data", response.Header["error-data"]);
        Assert.Contains("missing or invalid API token", response.Header["error-message"]);
    }

    [Fact]
    public async Task UnreachableServer_ShouldReachTheUI_As503()
    {
        var server = new FakeRemoteServer { PingError = new RemoteUnreachableException("No route to host") };
        using var sp = CreateServicesWithServer(server);

        var response = await Dispatch(sp, "POST", "/api/device/test");

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("REMOTE_UNREACHABLE", response.Header["error-code"]);
    }

    [Fact]
    public async Task ModeRoute_ShouldParseTheEnum_AndSwitch()
    {
        using var sp = CreateServicesWithServer(new FakeRemoteServer());
        var store = sp.GetRequiredService<IRemoteDeviceStore>();
        store.ServerUrl = "https://nas.local:3000";

        var response = await Dispatch(sp, "PUT", "/api/device/mode", "?mode=remote");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(DeviceBankMode.REMOTE, store.Mode);
        Assert.Equal("REMOTE", Assert.IsType<DeviceRemoteStatusDTO>(Assert.IsType<CoreJSONResponse>(response).Data).Mode);
    }

    [Fact]
    public async Task ConfigRoute_ShouldReadTheBody_AndNeverReturnTheToken()
    {
        using var sp = CreateServicesWithServer(new FakeRemoteServer());
        using var scope = sp.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<CoreRouter>().Dispatch(
            Task.CompletedTask, scope.ServiceProvider, "PUT", "/api/device/config", "",
            new MemoryStream(Encoding.UTF8.GetBytes("""{"serverUrl":"https://nas.local:3000/","token":"s3cret"}"""))
        );

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("https://nas.local:3000", sp.GetRequiredService<IRemoteDeviceStore>().ServerUrl);
        Assert.Equal("s3cret", sp.GetRequiredService<IRemoteSecretStore>().Token);
        var status = Assert.IsType<DeviceRemoteStatusDTO>(Assert.IsType<CoreJSONResponse>(response).Data);
        Assert.True(status.HasToken);
    }
}
