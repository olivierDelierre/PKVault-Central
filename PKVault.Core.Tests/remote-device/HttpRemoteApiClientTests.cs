using System.Net;
using PKVault.Core;

public class HttpRemoteApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeConnectionInfo info = new();

    private HttpRemoteApiClient CreateClient(StubHttpHandler handler, TimeSpan? timeout = null)
    {
        var connection = new RemoteHttpConnection(new HttpClient(handler), info);
        if (timeout != null) connection.RequestTimeout = timeout.Value;
        return new HttpRemoteApiClient(connection);
    }

    [Fact]
    public async Task Ping_ShouldSendTheToken_AndReadTheAnswer()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"version":"2.4.4","commitSeq":7}"""));

        var result = await CreateClient(handler).Ping(Ct);

        Assert.Equal("2.4.4", result.Version);
        Assert.Equal(7, result.CommitSeq);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://nas.local:3000/api/remote/ping", request.Uri.ToString());
        Assert.Equal("Bearer secret-token", request.Authorization);
    }

    [Fact]
    public async Task Requests_ShouldKeepAServerPathPrefix_BehindAReverseProxy()
    {
        info.ServerUrl = "https://home.example/pkvault/";
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"version":"x","commitSeq":0}"""));

        await CreateClient(handler).Ping(Ct);

        Assert.Equal("https://home.example/pkvault/api/remote/ping", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Requests_ShouldNotSendAnAuthorization_WhenNoTokenIsSet()
    {
        info.Token = null;
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"version":"x","commitSeq":0}"""));

        await CreateClient(handler).Ping(Ct);

        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task TokenAndAddress_ShouldBeReadAtEachRequest()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("""{"version":"x","commitSeq":0}"""));
        var client = CreateClient(handler);

        await client.Ping(Ct);
        info.Token = "other";
        info.ServerUrl = "http://other:3000";
        await client.Ping(Ct);

        Assert.Equal("Bearer other", handler.Requests[1].Authorization);
        Assert.Equal("http://other:3000/api/remote/ping", handler.Requests[1].Uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://nas/")]
    public async Task Requests_ShouldBeUnreachable_WhenServerIsNotConfiguredOrInvalid(string? url)
    {
        info.ServerUrl = url;
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<RemoteUnreachableException>(() => CreateClient(handler).Ping(Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenSession_ShouldUploadTheSaveAsMultipart_AndReadOnlyWhatItNeeds()
    {
        byte[] save = [1, 2, 3, 4, 5];
        byte[]? uploaded = null;
        string? uploadedName = null;
        var handler = new StubHttpHandler(async (request, _) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = Assert.Single(multipart);
            uploaded = await part.ReadAsByteArrayAsync();
            uploadedName = part.Headers.ContentDisposition?.FileName?.Trim('"');
            // the real answer also has a big "data" object
            return StubHttpHandler.Json("""{"saveId":1506469232,"originalHash":"ABC","data":{"saves":[],"x":{"y":1}}}""");
        });

        var result = await CreateClient(handler).OpenSession("device-0001", "my save.sav", save, Ct);

        Assert.Equal(1506469232u, result.SaveId);
        Assert.Equal("ABC", result.OriginalHash);
        Assert.Equal(save, uploaded);
        Assert.Equal("my save.sav", uploadedName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/remote/session/open", request.Uri.AbsolutePath);
        Assert.Equal("?deviceId=device-0001&fileName=my%20save.sav", request.Uri.Query);
    }

    [Fact]
    public async Task GetTransfers_ShouldReadDeliveries()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json(
            """[{"id":"t1","saveId":5,"fileName":"main.sav","originalHash":"OH","newHash":"NH","size":131072,"commitSeq":3,"createdAt":"2026-10-16T17:21:17.3629001","state":"READY"}]"""
        ));

        var result = await CreateClient(handler).GetTransfers("device-0001", Ct);

        var transfer = Assert.Single(result);
        Assert.Equal("t1", transfer.Id);
        Assert.Equal(5u, transfer.SaveId);
        Assert.Equal("OH", transfer.OriginalHash);
        Assert.Equal("NH", transfer.NewHash);
        Assert.Equal(3, transfer.CommitSeq);
        Assert.Equal("READY", transfer.State);
        Assert.Equal("?deviceId=device-0001", handler.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Download_ShouldReturnTheBytesWithTheirHashes()
    {
        var handler = StubHttpHandler.Of(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 8, 7]) };
            response.Content.Headers.ContentType = new("application/octet-stream");
            response.Headers.Add("X-Hash-Original", "OH");
            response.Headers.Add("X-Hash-New", "NH");
            return response;
        });

        var result = await CreateClient(handler).DownloadTransfer("t1", Ct);

        Assert.Equal([9, 8, 7], result.Bytes);
        Assert.Equal("OH", result.OriginalHash);
        Assert.Equal("NH", result.NewHash);
        Assert.Equal("/api/remote/transfers/t1/download", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Download_ShouldFail_WhenHashHeadersAreMissing()
    {
        var handler = StubHttpHandler.Of(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateClient(handler).DownloadTransfer("t1", Ct));
    }

    [Fact]
    public async Task Ack_ShouldSendTheHash()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json(
            """{"id":"t1","saveId":5,"fileName":"main.sav","originalHash":"OH","newHash":"NH","size":3,"commitSeq":3,"createdAt":"2026-10-16T17:21:17","state":"ACKED"}"""
        ));

        var result = await CreateClient(handler).Ack("t1", "NH", Ct);

        Assert.Equal("ACKED", result.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/remote/transfers/t1/ack", request.Uri.AbsolutePath);
        Assert.Equal("?newHash=NH", request.Uri.Query);
    }

    [Fact]
    public async Task CloseSession_AndAbort_ShouldCallTheRightRoutes()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Json("{}"));
        var client = CreateClient(handler);

        await client.CloseSession("device-0001", 42, Ct);
        await client.Abort("t1", Ct);

        Assert.Equal("/api/remote/session/close", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?deviceId=device-0001&saveId=42", handler.Requests[0].Uri.Query);
        Assert.Equal("/api/remote/transfers/t1/abort", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task ConflictAnswer_ShouldKeepTheCodeAndTheData()
    {
        var handler = StubHttpHandler.Of(_ => StubHttpHandler.Error(
            HttpStatusCode.Conflict, "Save 1 has a pending delivery", ConflictException.PENDING_DELIVERY, "transfer-1"
        ));

        var ex = await Assert.ThrowsAsync<RemoteApiException>(
            () => CreateClient(handler).OpenSession("device-0001", "main.sav", [1], Ct)
        );

        Assert.Equal(409, ex.Status);
        Assert.Equal(ConflictException.PENDING_DELIVERY, ex.Code);
        Assert.Equal("transfer-1", ex.ErrorData);
        // the JSON string quotes of the header are not part of the message
        Assert.Equal("Save 1 has a pending delivery", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorStatus_ShouldBecomeAnApiException_WithItsStatus(HttpStatusCode status)
    {
        var handler = StubHttpHandler.Of(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<RemoteApiException>(() => CreateClient(handler).Ping(Ct));

        Assert.Equal((int)status, ex.Status);
        Assert.Null(ex.Code);
    }

    [Fact]
    public async Task NetworkFailure_ShouldBecomeUnreachable()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host")));

        var ex = await Assert.ThrowsAsync<RemoteUnreachableException>(() => CreateClient(handler).Ping(Ct));

        Assert.Contains("No route", ex.Message);
    }

    [Fact]
    public async Task SlowServer_ShouldBecomeUnreachable_AfterTheTimeout()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            // like a real connection: stops when the request is cancelled
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return StubHttpHandler.Json("{}");
        });

        await Assert.ThrowsAsync<RemoteUnreachableException>(
            () => CreateClient(handler, TimeSpan.FromMilliseconds(100)).Ping(Ct)
        );
    }

    [Fact]
    public async Task CallerCancellation_ShouldNotBeTurnedIntoUnreachable()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return StubHttpHandler.Json("{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient(handler).Ping(cts.Token));
    }
}
