using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace PKVault.Core;

/// <summary>
/// In remote mode, the UI keeps talking to its usual API: the requests are sent to the server instead
/// of the local router. The few routes about the device itself stay local.
/// </summary>
public class RemoteProxy(RemoteHttpConnection connection, TransferCoordinator coordinator)
{
    /// <summary>A commit moves Pokémon and can be slow on a big bank.</summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(1);

    private static readonly string[] LocalPrefixes = [
        // device specific: transfers, mode, device saves
        "/api/device",
        // the app settings (runtime, paths...) are the device ones, not the server ones
        "/api/settings",
        // static game data is embedded in the app
        "/api/static-data",
    ];

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Content-Encoding", "Transfer-Encoding", "Connection", "Keep-Alive", "Date", "Server",
    };

    /// <summary>True for routes which always use the local router, even in remote mode.</summary>
    public static bool IsLocalRoute(string path)
        => LocalPrefixes.Any(prefix =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    private static bool IsCommit(string method, string path)
        => method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.TrimEnd('/').Equals("/api/storage/action/save", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends the request to the server. Never throws for a network problem: it becomes a 503 answer.
    /// </summary>
    public async Task<DesktopFetchResponse> Forward(string url, DesktopFetchRequestInit init, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        var isCommit = IsCommit(init.method, uri.LocalPath);

        if (isCommit)
        {
            // from now on the server may have a delivery for us, even if the answer is lost
            await coordinator.BeginCommit(ct);
        }

        DesktopFetchResponse result;
        try
        {
            result = await Send(url, uri, init, isCommit ? CommitTimeout : RequestTimeout, ct);
        }
        catch (RemoteUnreachableException ex)
        {
            Log.Warning(ex, $"Remote request failed {init.method} {uri.LocalPath}");

            result = ErrorResponse(url, 503, "Service Unavailable", $"Remote server unreachable: {ex.Message}");
        }

        if (isCommit)
        {
            // gets the modified save back on the device as soon as the commit is done
            await coordinator.EndCommit(responseReceived: result.ok, ct);
        }

        return result;
    }

    private async Task<DesktopFetchResponse> Send(string url, Uri uri, DesktopFetchRequestInit init, TimeSpan timeout, CancellationToken ct)
    {
        using var request = connection.CreateRequest(new HttpMethod(init.method.ToUpperInvariant()), uri.PathAndQuery);

        if (!string.IsNullOrEmpty(init.body))
        {
            var contentType = init.headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                ?? "application/json";
            request.Content = new StringContent(init.body, System.Text.Encoding.UTF8, new MediaTypeHeaderValue(contentType.Split(';')[0]));
        }

        using var response = await connection.Send(request, timeout, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!DroppedResponseHeaders.Contains(name))
            {
                headers[name] = string.Join(", ", values);
            }
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !IsText(mediaType))
        {
            // the bridge with the web view only carries text
            return ErrorResponse(url, 415, "Unsupported Media Type", $"The remote answer is not text ({mediaType})");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!init.method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            body = RemoveServerSettings(body);
        }

        return new DesktopFetchResponse(
            url,
            status: (int)response.StatusCode,
            statusText: response.ReasonPhrase ?? "",
            ok: response.IsSuccessStatusCode,
            headers: headers,
            body: string.IsNullOrEmpty(body) ? null : body
        );
    }

    /// <summary>
    /// An update from the server can carry its own settings (runtime, paths, flags), which would replace
    /// the app ones in the UI cache and hide the settings of the device.
    /// </summary>
    public static string RemoveServerSettings(string body)
    {
        if (!body.StartsWith('{') || !body.Contains("\"settings\"", StringComparison.Ordinal))
        {
            return body;
        }

        try
        {
            if (JsonNode.Parse(body) is JsonObject json && json.Remove("settings"))
            {
                return json.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // not JSON after all
        }

        return body;
    }

    private static bool IsText(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static DesktopFetchResponse ErrorResponse(string url, int status, string statusText, string message) => new(
        url,
        status,
        statusText,
        ok: false,
        headers: new()
        {
            ["Content-Type"] = "text/plain",
            // same format as the router errors
            ["error-message"] = JsonSerializer.Serialize(message, RouteJsonContext.Default.String),
            ["error-code"] = status == 503 ? "REMOTE_UNREACHABLE" : "REMOTE_ERROR",
        }
    );
}
