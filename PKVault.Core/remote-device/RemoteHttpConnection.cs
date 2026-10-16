using System.Text.Json;

namespace PKVault.Core;

public interface IRemoteConnectionInfo
{
    /// <summary>Base address of the server, ex: "https://nas.local:3000". Null when not configured.</summary>
    public string? ServerUrl { get; }

    /// <summary>Value of PKVAULT_API_TOKEN on the server, if it requires one.</summary>
    public string? Token { get; }
}

/// <summary>
/// Sends requests to the remote server: base address and token are read at each request
/// (they can change in the settings), network problems become <see cref="RemoteUnreachableException"/>.
/// </summary>
public class RemoteHttpConnection(HttpClient http, IRemoteConnectionInfo info)
{
    /// <summary>For JSON requests and downloads of a few hundred KB.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery)
    {
        var serverUrl = info.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new RemoteUnreachableException("Remote server is not configured");
        }

        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw new RemoteUnreachableException($"Remote server address is not valid: {serverUrl}");
        }

        var request = new HttpRequestMessage(method, new Uri(baseUri, pathAndQuery.TrimStart('/')));

        if (!string.IsNullOrEmpty(info.Token))
        {
            request.Headers.Authorization = new("Bearer", info.Token);
        }

        return request;
    }

    /// <exception cref="RemoteUnreachableException">Offline, timeout...</exception>
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? RequestTimeout);

        try
        {
            // the whole answer is read here, under the timeout
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteUnreachableException("The server did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteUnreachableException(ex.Message, ex);
        }
    }

    /// <summary>The server message, the "error-code" and "error-data" headers of an error answer.</summary>
    public static RemoteApiException ToException(HttpResponseMessage response)
    {
        string? message = null;
        if (response.Headers.TryGetValues("error-message", out var values))
        {
            // sent as a JSON string
            var raw = values.FirstOrDefault();
            try
            {
                message = raw == null ? null : JsonSerializer.Deserialize(raw, RouteJsonContext.Default.String);
            }
            catch (JsonException)
            {
                message = raw;
            }
        }

        message ??= $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

        return new RemoteApiException(
            (int)response.StatusCode,
            message,
            GetHeader(response, "error-code"),
            GetHeader(response, "error-data")
        );
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
