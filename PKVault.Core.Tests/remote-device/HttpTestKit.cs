using System.Net;
using System.Text;
using PKVault.Core;

/// <summary>Records the requests and answers with the given function.</summary>
public class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public record Recorded(string Method, Uri Uri, string? Authorization, string? Body, string? ContentType, HttpContent? Content);

    public List<Recorded> Requests { get; } = [];

    /// <summary>Answers right away.</summary>
    public static StubHttpHandler Of(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new((request, _) => Task.FromResult(respond(request)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // the content is read here: it is disposed with the request
        var body = request.Content is { } content && content is not MultipartFormDataContent
            ? await content.ReadAsStringAsync(cancellationToken)
            : null;
        Requests.Add(new(
            request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body,
            request.Content?.Headers.ContentType?.MediaType, request.Content
        ));

        return await respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string message, string? code = null, string? data = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
        // like the router: the message is a JSON string
        response.Headers.Add("error-message", System.Text.Json.JsonSerializer.Serialize(message));
        if (code != null) response.Headers.Add("error-code", code);
        if (data != null) response.Headers.Add("error-data", data);
        return response;
    }
}

public class FakeConnectionInfo : IRemoteConnectionInfo
{
    public string? ServerUrl { get; set; } = "https://nas.local:3000";
    public string? Token { get; set; } = "secret-token";
}
