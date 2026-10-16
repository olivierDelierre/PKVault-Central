using System.IO.Abstractions;
using System.Text.Json.Nodes;
using PKVault.Core;

/// <summary>
/// The whole device side against a REAL server: real HTTP client, real proxy, real engine, real files.
/// It is skipped unless a server is given, so a normal test run does not need one.
///
///   PKVAULT_E2E_URL    address of a running server, ex: http://localhost:5000
///   PKVAULT_E2E_TOKEN  its PKVAULT_API_TOKEN, if it has one
///
/// The server must not already have the sample save (ID of "Pokemon - Version Saphir (France).srm"),
/// and its bank gets Pokémon moved into it: use a scratch data folder (see docs).
/// </summary>
public class RemoteBankEndToEndTests : IDisposable
{
    private const string LOCAL = "https://0.0.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string workDir = Path.Combine(Path.GetTempPath(), $"pkvault-e2e-{Guid.NewGuid():N}");
    private readonly string devicePath;
    private readonly string? serverUrl = Environment.GetEnvironmentVariable("PKVAULT_E2E_URL");

    // a device keeps its identifier: the server finds its deliveries by it, also after a restart
    private readonly string deviceId = "e2e" + Guid.NewGuid().ToString("N");

    public RemoteBankEndToEndTests()
    {
        Program.Initialize();

        devicePath = Path.Combine(workDir, "saves", "device.sav");
    }

    public void Dispose()
    {
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static string Hash(byte[] bytes) => RemoteSaveService.ComputeHash(bytes);

    private (RemoteProxy Proxy, TransferCoordinator Coordinator, TransferJournal Journal) CreateDevice()
    {
        var info = new FakeConnectionInfo
        {
            ServerUrl = serverUrl,
            Token = Environment.GetEnvironmentVariable("PKVAULT_E2E_TOKEN"),
        };
        var connection = new RemoteHttpConnection(new HttpClient(), info);
        var fileSystem = new FileSystem();
        var journal = new TransferJournal(fileSystem, Path.Combine(workDir, "journal"), TimeProvider.System);
        var coordinator = new TransferCoordinator(
            new HttpRemoteApiClient(connection), journal, new FileIOService(fileSystem),
            new InMemoryRemoteDeviceStore(Path.Combine(workDir, "journal"), deviceId),
            new FakeLocalSavesRefresher(), TimeProvider.System
        );
        return (new RemoteProxy(connection, coordinator), coordinator, journal);
    }

    private static async Task<JsonNode> GetJson(RemoteProxy proxy, string path)
    {
        var response = await proxy.Forward($"{LOCAL}{path}", new("GET", []), Ct);
        Assert.True(response.ok, $"GET {path} => {response.status} {response.headers.GetValueOrDefault("error-message")}");
        return JsonNode.Parse(response.body!)!;
    }

    /// <summary>Moves the first Pokémon of the save to the first bank, like a user would.</summary>
    private static async Task MoveFirstPkmToBank(RemoteProxy proxy, uint saveId)
    {
        var pkms = (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray();
        var banks = (await GetJson(proxy, "/api/storage/main/bank")).AsArray();
        var bankId = banks.First(b => b!["isDefault"]!.GetValue<bool>())!["id"]!.GetValue<string>();

        var body = new JsonObject
        {
            ["pkmIds"] = new JsonArray(pkms[0]!["id"]!.GetValue<string>()),
            ["sourceSaveId"] = saveId,
            ["bankId"] = bankId,
            ["attached"] = false,
        }.ToJsonString();

        var response = await proxy.Forward($"{LOCAL}/api/storage/move/pkm/bank", new("PUT", new() { ["Content-Type"] = "application/json" }, body), Ct);
        Assert.True(response.ok, $"move => {response.status} {response.headers.GetValueOrDefault("error-message")}");
    }

    private static async Task<DesktopFetchResponse> Commit(RemoteProxy proxy)
        => await proxy.Forward($"{LOCAL}/api/storage/action/save", new("POST", []), Ct);

    [Fact]
    public async Task OpenMoveCommit_ShouldDeliverTheSaveToTheDevice_TwiceInARow()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(serverUrl), "Set PKVAULT_E2E_URL to a running server to run this test");

        Directory.CreateDirectory(Path.GetDirectoryName(devicePath)!);
        File.Copy(Path.Combine(Program.InitialCurrentDirectory, "./assets/Pokemon - Version Saphir (France).srm"), devicePath);
        var original = await File.ReadAllBytesAsync(devicePath, Ct);

        var (proxy, coordinator, journal) = CreateDevice();

        // 1. send the save of the device
        var entry = await coordinator.StartUpload(devicePath, Ct);
        Assert.True(entry.State == DeviceTransferState.UPLOADED, $"{entry.State} {entry.LastError}");
        var saveId = entry.SaveId!.Value;
        Assert.Equal(Hash(original), entry.OriginalHash);

        var pkmsBefore = (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray().Count;
        Assert.True(pkmsBefore > 0);
        // the bank of the server can already have Pokémon (other runs)
        var bankBefore = (await GetJson(proxy, "/api/storage/main/pkm-version")).AsArray().Count;

        // 2. edit on the server, then commit: the modified save comes back right away
        await MoveFirstPkmToBank(proxy, saveId);
        var commit = await Commit(proxy);
        Assert.True(commit.ok, $"commit => {commit.status} {commit.headers.GetValueOrDefault("error-message")}");

        var delivered = journal.Get(entry.LocalId)!;
        Assert.True(delivered.State == DeviceTransferState.ACKED, $"{delivered.State} {delivered.LastError}");
        var firstResult = await File.ReadAllBytesAsync(devicePath, Ct);
        Assert.NotEqual(Hash(original), Hash(firstResult));
        Assert.Equal(delivered.NewHash, Hash(firstResult));

        // the bank of the server has the Pokémon
        Assert.Equal(bankBefore + 1, (await GetJson(proxy, "/api/storage/main/pkm-version")).AsArray().Count);

        // 3. again, from the new save of the device
        var second = await coordinator.StartUpload(devicePath, Ct);
        Assert.True(second.State == DeviceTransferState.UPLOADED, $"{second.State} {second.LastError}");
        Assert.NotEqual(entry.LocalId, second.LocalId);
        Assert.Equal(pkmsBefore - 1, (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray().Count);

        await MoveFirstPkmToBank(proxy, saveId);
        Assert.True((await Commit(proxy)).ok);

        Assert.Equal(DeviceTransferState.ACKED, journal.Get(second.LocalId)!.State);
        var secondResult = await File.ReadAllBytesAsync(devicePath, Ct);
        Assert.NotEqual(Hash(firstResult), Hash(secondResult));
        Assert.Equal(bankBefore + 2, (await GetJson(proxy, "/api/storage/main/pkm-version")).AsArray().Count);

        // nothing was left half done
        Assert.All(journal.LoadAll(), e => Assert.Equal(DeviceTransferState.ACKED, e.State));
        Assert.Empty(Directory.GetFiles(Path.Combine(workDir, "saves"), "*.tmp-*"));
    }

    [Fact]
    public async Task CommitWithTheAnswerLost_ShouldStillDeliverAtNextStart()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(serverUrl), "Set PKVAULT_E2E_URL to a running server to run this test");

        Directory.CreateDirectory(Path.GetDirectoryName(devicePath)!);
        File.Copy(Path.Combine(Program.InitialCurrentDirectory, "./assets/Pokemon - Version Saphir (France).srm"), devicePath);
        var original = await File.ReadAllBytesAsync(devicePath, Ct);

        var (proxy, coordinator, journal) = CreateDevice();
        var entry = await coordinator.StartUpload(devicePath, Ct);
        Assert.True(entry.State == DeviceTransferState.UPLOADED, $"{entry.State} {entry.LastError}");
        await MoveFirstPkmToBank(proxy, entry.SaveId!.Value);

        // the app dies right after the commit was sent: only BeginCommit ran on the device
        await coordinator.BeginCommit(Ct);
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl!.TrimEnd('/')}/api/storage/action/save");
        var token = Environment.GetEnvironmentVariable("PKVAULT_E2E_TOKEN");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new("Bearer", token);
        Assert.True((await http.SendAsync(request, Ct)).IsSuccessStatusCode);

        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, journal.Get(entry.LocalId)!.State);
        Assert.Equal(original, await File.ReadAllBytesAsync(devicePath, Ct));

        // next start
        var (_, restarted, restartedJournal) = CreateDevice();
        await restarted.Resume(Ct);

        var done = restartedJournal.Get(entry.LocalId)!;
        Assert.True(done.State == DeviceTransferState.ACKED, $"{done.State} {done.LastError}");
        Assert.Equal(done.NewHash, Hash(await File.ReadAllBytesAsync(devicePath, Ct)));
    }

    [Fact]
    public async Task CopyToBankThenCommit_ShouldKeepTheSaveOfTheDeviceAsItWas_AndAddToTheBank()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(serverUrl), "Set PKVAULT_E2E_URL to a running server to run this test");

        Directory.CreateDirectory(Path.GetDirectoryName(devicePath)!);
        File.Copy(Path.Combine(Program.InitialCurrentDirectory, "./assets/Pokemon - Version Saphir (France).srm"), devicePath);
        var original = await File.ReadAllBytesAsync(devicePath, Ct);

        var (proxy, coordinator, journal) = CreateDevice();
        var entry = await coordinator.StartUpload(devicePath, Ct);
        Assert.True(entry.State == DeviceTransferState.UPLOADED, $"{entry.State} {entry.LastError}");
        var saveId = entry.SaveId!.Value;

        var pkmsBefore = (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray().Count;
        var bankBefore = (await GetJson(proxy, "/api/storage/main/pkm-version")).AsArray().Count;

        // copy the last Pokémon which can go to the bank, like the button does (the other tests moved the first ones to the same bank)
        var pkms = (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray();
        var banks = (await GetJson(proxy, "/api/storage/main/bank")).AsArray();
        var bankId = banks.First(b => b!["isDefault"]!.GetValue<bool>())!["id"]!.GetValue<string>();
        var body = new JsonObject
        {
            ["pkmIds"] = new JsonArray(pkms.Last(p => p!["canMoveToMain"]!.GetValue<bool>())!["id"]!.GetValue<string>()),
            ["sourceSaveId"] = saveId,
            ["bankId"] = bankId,
        }.ToJsonString();
        var copy = await proxy.Forward($"{LOCAL}/api/storage/copy/pkm/bank", new("PUT", new() { ["Content-Type"] = "application/json" }, body), Ct);
        Assert.True(copy.ok, $"copy => {copy.status} {copy.headers.GetValueOrDefault("error-message")}");

        // the same Pokémon cannot be copied twice: refused, and the bank does not grow
        var again = await proxy.Forward($"{LOCAL}/api/storage/copy/pkm/bank", new("PUT", new() { ["Content-Type"] = "application/json" }, body), Ct);
        Assert.False(again.ok);
        Assert.Contains("already in the bank", again.headers.GetValueOrDefault("error-message") ?? again.body ?? "");

        var commit = await Commit(proxy);
        Assert.True(commit.ok, $"commit => {commit.status} {commit.headers.GetValueOrDefault("error-message")}");

        // the bank has the copy, the save of the server still has all its Pokémon
        Assert.Equal(bankBefore + 1, (await GetJson(proxy, "/api/storage/main/pkm-version")).AsArray().Count);
        Assert.Equal(pkmsBefore, (await GetJson(proxy, $"/api/storage/save/{saveId}/pkm")).AsArray().Count);

        // nothing to deliver: the save of the device is not written, byte for byte
        Assert.Equal(original, await File.ReadAllBytesAsync(devicePath, Ct));
        var after = journal.Get(entry.LocalId)!;
        Assert.True(after.State is DeviceTransferState.UPLOADED or DeviceTransferState.ACKED, $"{after.State} {after.LastError}");
        Assert.Null(after.NewHash);
        Assert.Empty(Directory.GetFiles(Path.Combine(workDir, "saves"), "*.tmp-*"));
    }
}
