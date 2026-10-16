using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PKVault.Core;

/// <summary>
/// <see cref="IRemoteApiClient"/> over HTTP, to the server "api/remote".
/// </summary>
public class HttpRemoteApiClient(RemoteHttpConnection connection) : IRemoteApiClient
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(2);

    public async Task<RemotePingDTO> Ping(CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(HttpMethod.Get, "api/remote/ping");
        return await SendForJson<RemotePingDTO>(request, ct: ct);
    }

    public async Task<RemoteOpenSessionResult> OpenSession(string deviceId, string fileName, byte[] saveBytes, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(
            HttpMethod.Post,
            $"api/remote/session/open?deviceId={Uri.EscapeDataString(deviceId)}&fileName={Uri.EscapeDataString(fileName)}"
        );

        var file = new ByteArrayContent(saveBytes);
        file.Headers.ContentType = new("application/octet-stream");
        request.Content = new MultipartFormDataContent { { file, "saveFile", fileName } };

        using var response = await connection.Send(request, UploadTimeout, ct);
        await ThrowIfError(response);

        // the answer also holds all the data to display: only what the device needs is read
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        return new RemoteOpenSessionResult(
            root.GetProperty("saveId").GetUInt32(),
            root.GetProperty("originalHash").GetString() ?? throw new InvalidDataException("Missing originalHash")
        );
    }

    public async Task CloseSession(string deviceId, uint saveId, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(
            HttpMethod.Post,
            $"api/remote/session/close?deviceId={Uri.EscapeDataString(deviceId)}&saveId={saveId}"
        );
        await SendForNothing(request, ct);
    }

    public async Task<List<RemoteTransferDTO>> GetTransfers(string deviceId, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(HttpMethod.Get, $"api/remote/transfers?deviceId={Uri.EscapeDataString(deviceId)}");
        return await SendForJson<List<RemoteTransferDTO>>(request, ct: ct);
    }

    public async Task<RemoteDownload> DownloadTransfer(string transferId, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(HttpMethod.Get, $"api/remote/transfers/{Uri.EscapeDataString(transferId)}/download");

        using var response = await connection.Send(request, UploadTimeout, ct);
        await ThrowIfError(response);

        return new RemoteDownload(
            await response.Content.ReadAsByteArrayAsync(ct),
            GetRequiredHeader(response, "X-Hash-Original"),
            GetRequiredHeader(response, "X-Hash-New")
        );
    }

    public async Task<RemoteTransferDTO> Ack(string transferId, string newHash, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(
            HttpMethod.Post,
            $"api/remote/transfers/{Uri.EscapeDataString(transferId)}/ack?newHash={Uri.EscapeDataString(newHash)}"
        );
        return await SendForJson<RemoteTransferDTO>(request, ct: ct);
    }

    public async Task Abort(string transferId, CancellationToken ct = default)
    {
        using var request = connection.CreateRequest(HttpMethod.Post, $"api/remote/transfers/{Uri.EscapeDataString(transferId)}/abort");
        // a restore of the bank takes a while
        await SendForNothing(request, ct, UploadTimeout);
    }

    private async Task<T> SendForJson<T>(HttpRequestMessage request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var response = await connection.Send(request, timeout, ct);
        await ThrowIfError(response);

        // same naming rules as the server
        var typeInfo = (JsonTypeInfo<T>)RouteJsonContext.DefaultWithOptions.GetTypeInfo(typeof(T))!;
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), typeInfo)
            ?? throw new InvalidDataException($"Empty answer from {request.RequestUri}");
    }

    private async Task SendForNothing(HttpRequestMessage request, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var response = await connection.Send(request, timeout, ct);
        await ThrowIfError(response);
    }

    private static Task ThrowIfError(HttpResponseMessage response)
        => response.IsSuccessStatusCode ? Task.CompletedTask : Task.FromException(RemoteHttpConnection.ToException(response));

    private static string GetRequiredHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Missing header {name}");
}
