using System.Net;
using System.Text;
using PKVault.Core;

public class RemoteProxyTests
{
    private const string LOCAL_URL = "https://0.0.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TransferTestEnv env = new();
    private readonly FakeConnectionInfo info = new();

    private RemoteProxy CreateProxy(StubHttpHandler handler)
        => new(new RemoteHttpConnection(new HttpClient(handler), info), env.NewCoordinator());

    private static DesktopFetchRequestInit Get() => new("GET", []);

    private static DesktopFetchRequestInit Post(string? body = null, string contentType = "application/json")
        => new("POST", new() { ["Content-Type"] = contentType }, body);

    // --- routing

    [Theory]
    [InlineData("/api/device", true)]
    [InlineData("/api/device/remote/status", true)]
    [InlineData("/API/Device/saves", true)]
    [InlineData("/api/settings", true)]
    [InlineData("/api/settings/refresh", true)]
    [InlineData("/api/static-data/spritesheet/species", true)]
    [InlineData("/api/storage/main/bank", false)]
    [InlineData("/api/save-infos", false)]
    [InlineData("/api/dex", false)]
    [InlineData("/api/warnings", false)]
    [InlineData("/api/backup", false)]
    [InlineData("/api/remote/ping", false)]
    // only a whole segment is local, not a longer name
    [InlineData("/api/devices", false)]
    [InlineData("/api/settings-other", false)]
    [InlineData("/api/static-datum", false)]
    public void IsLocalRoute_ShouldKeepOnlyDeviceSettingsAndStaticDataLocal(string path, bool expected)
    {
        Assert.Equal(expected, RemoteProxy.IsLocalRoute(path));
    }

    // --- forwarding

    [Fact]
    public async Task Forward_ShouldSendPathAndQueryToTheServer_WithTheToken()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""[{"id":"0"}]"""));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/main/bank?a=1&b=x%20y", Get(), Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://nas.local:3000/api/storage/main/bank?a=1&b=x%20y", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer secret-token", request.Authorization);

        Assert.True(response.ok);
        Assert.Equal(200, response.status);
        Assert.Equal("""[{"id":"0"}]""", response.body);
        Assert.Equal($"{LOCAL_URL}/api/storage/main/bank?a=1&b=x%20y", response.url);
    }

    [Fact]
    public async Task Forward_ShouldPassTheBodyAndContentType()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("{}"));
        var body = """{"name":"é à ü"}""";

        await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/move/pkm", new("PUT", new() { ["content-type"] = "application/json; charset=utf-8" }, body), Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal(body, request.Body);
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task Forward_ShouldKeepTheErrorStatusAndHeaders()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Error(
            HttpStatusCode.Conflict, "pending", ConflictException.PENDING_DELIVERY, "t1"
        ));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/move/pkm", Post("{}"), Ct);

        Assert.False(response.ok);
        Assert.Equal(409, response.status);
        Assert.Equal("\"pending\"", response.headers["error-message"]);
        Assert.Equal(ConflictException.PENDING_DELIVERY, response.headers["error-code"]);
        Assert.Equal("t1", response.headers["error-data"]);
    }

    [Fact]
    public async Task Forward_ShouldDropHeadersWhichDoNotMatchTheRebuiltAnswer()
    {
        var handler = StubHttpHandler.Of(_ =>
        {
            var response = StubHttpHandler.Json("""{"a":1}""");
            response.Headers.Add("Server", "Kestrel");
            response.Headers.TransferEncodingChunked = true;
            response.Headers.Add("X-Custom", "kept");
            return response;
        });

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/dex", Get(), Ct);

        Assert.DoesNotContain("Content-Length", response.headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding", response.headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server", response.headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("kept", response.headers["X-Custom"]);
        Assert.Contains("json", response.headers["Content-Type"]);
    }

    [Fact]
    public async Task Forward_ShouldAnswer503_InsteadOfThrowing_WhenTheServerIsUnreachable()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host")));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/main/bank", Get(), Ct);

        Assert.False(response.ok);
        Assert.Equal(503, response.status);
        Assert.Equal("REMOTE_UNREACHABLE", response.headers["error-code"]);
        // same format as the router errors: a JSON string
        Assert.StartsWith("\"Remote server unreachable", response.headers["error-message"]);
        Assert.Contains("No route to host", response.headers["error-message"]);
    }

    [Fact]
    public async Task Forward_ShouldAnswer503_WhenNoServerIsConfigured()
    {
        info.ServerUrl = null;
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("{}"));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/main/bank", Get(), Ct);

        Assert.Equal(503, response.status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forward_ShouldRefuseBinaryAnswers_WhichTheBridgeCannotCarry()
    {
        var handler = StubHttpHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/octet-stream") } },
        });

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/save-infos/1/download", Get(), Ct);

        Assert.Equal(415, response.status);
        Assert.False(response.ok);
    }

    [Fact]
    public async Task Forward_ShouldLeaveEmptyAnswersWithoutBody()
    {
        var handler = StubHttpHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/action", new("DELETE", []), Ct);

        Assert.True(response.ok);
        Assert.Equal(204, response.status);
        Assert.Null(response.body);
    }

    // --- server settings

    [Theory]
    [InlineData("""{"actions":[],"settings":{"runtimeSystem":"DOCKER"},"saveInfos":{}}""", """{"actions":[],"saveInfos":{}}""")]
    [InlineData("""{"actions":[]}""", """{"actions":[]}""")]
    [InlineData("""{"settings":{"x":1}}""", "{}")]
    // only the top level is the app settings
    [InlineData("""{"saves":[{"settings":1}]}""", """{"saves":[{"settings":1}]}""")]
    [InlineData("""[{"settings":1}]""", """[{"settings":1}]""")]
    [InlineData("""{ not json "settings" """, """{ not json "settings" """)]
    [InlineData("", "")]
    public void RemoveServerSettings_ShouldOnlyDropTheTopLevelSettings(string body, string expected)
    {
        Assert.Equal(expected, RemoteProxy.RemoveServerSettings(body));
    }

    [Fact]
    public async Task Forward_ShouldNotLetTheServerSettingsReachTheUI_OnUpdates()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"actions":[],"settings":{"runtimeSystem":"DOCKER"}}"""));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/storage/move/pkm", Post("{}"), Ct);

        Assert.Equal("""{"actions":[]}""", response.body);
    }

    [Fact]
    public async Task Forward_ShouldNotTouchGetAnswers()
    {
        var json = """{"settings":{"runtimeSystem":"DOCKER"}}""";
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json(json));

        var response = await CreateProxy(handler).Forward($"{LOCAL_URL}/api/warnings", Get(), Ct);

        Assert.Equal(json, response.body);
    }

    // --- commit

    private async Task<TransferJournalEntry> OpenSaveOnServer()
    {
        var entry = await env.NewCoordinator().StartUpload(TransferTestEnv.DevicePath, Ct);
        Assert.Equal(DeviceTransferState.UPLOADED, entry.State);
        return entry;
    }

    private const string COMMIT_URL = $"{LOCAL_URL}/api/storage/action/save";

    [Fact]
    public async Task Commit_ShouldBeMarkedBeforeItIsSent_AndDeliverTheSaveRightAfter()
    {
        await OpenSaveOnServer();
        DeviceTransferState? stateWhileSending = null;
        var handler = StubHttpHandler.Of(_ =>
        {
            stateWhileSending = env.OnlyEntry.State;
            env.Server.Commit(TransferTestEnv.NewBytes);
            return StubHttpHandler.Json("""{"actions":[]}""");
        });

        var response = await CreateProxy(handler).Forward(COMMIT_URL, Post(), Ct);

        // a delivery may exist from the moment the request leaves, even if the answer never returns
        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, stateWhileSending);
        Assert.Equal(200, response.status);
        Assert.Equal("""{"actions":[]}""", response.body);
        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
    }

    [Fact]
    public async Task Commit_WhoseAnswerIsLost_ShouldStillBeDeliveredLater()
    {
        await OpenSaveOnServer();
        var handler = new StubHttpHandler((_, _) =>
        {
            // the server handled the commit, the connection dropped before the answer
            env.Server.Commit(TransferTestEnv.NewBytes);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
        });

        // the delivery cannot be asked now either: the server is unreachable for the device engine too
        env.Server.FaultAt = (_, _) => FaultKind.Before;
        var response = await CreateProxy(handler).Forward(COMMIT_URL, Post(), Ct);

        Assert.Equal(503, response.status);
        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.OriginalBytes, env.DeviceBytes);

        // connection is back
        env.Server.FaultAt = null;
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
    }

    [Fact]
    public async Task Commit_ThatChangedNothingOnTheSave_ShouldGoBackToWaiting()
    {
        await OpenSaveOnServer();
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"actions":[]}"""));

        await CreateProxy(handler).Forward(COMMIT_URL, Post(), Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
    }

    [Fact]
    public async Task Commit_ThatFailedWithAnError_ShouldStayMaybeCommitted()
    {
        await OpenSaveOnServer();
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Error(HttpStatusCode.InternalServerError, "boom"));

        var response = await CreateProxy(handler).Forward(COMMIT_URL, Post(), Ct);

        Assert.Equal(500, response.status);
        // not assumed to have failed: the server may have recorded a delivery before failing
        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, env.OnlyEntry.State);
    }

    [Fact]
    public async Task OtherRequests_ShouldNotTouchTheTransfers()
    {
        await OpenSaveOnServer();
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"actions":[]}"""));
        var proxy = CreateProxy(handler);

        await proxy.Forward($"{LOCAL_URL}/api/storage/move/pkm", Post("{}"), Ct);
        await proxy.Forward($"{LOCAL_URL}/api/storage/action/save/other", Post("{}"), Ct);
        await proxy.Forward(COMMIT_URL, Get(), Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
    }

    [Fact]
    public async Task Commit_WithNoSaveOpenOnTheServer_ShouldJustBeForwarded()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"actions":[]}"""));

        var response = await CreateProxy(handler).Forward(COMMIT_URL, Post(), Ct);

        Assert.Equal(200, response.status);
        Assert.Empty(env.Journal.LoadAll());
        Assert.Empty(env.Server.Calls);
    }
}
