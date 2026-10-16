namespace PKVault.Core;

/// <summary>
/// Client of the server "api/remote" (see <see cref="RemoteBankService"/>), used by the device
/// to deliver the changes made on the server back to its own save files.
/// </summary>
public interface IRemoteApiClient
{
    public Task<RemotePingDTO> Ping(CancellationToken ct = default);

    /// <summary>Uploads the device save. Idempotent for a given device & save.</summary>
    public Task<RemoteOpenSessionDTO> OpenSession(string deviceId, string fileName, byte[] saveBytes, CancellationToken ct = default);

    public Task<DataDTO> CloseSession(string deviceId, uint saveId, CancellationToken ct = default);

    /// <summary>Deliveries waiting for this device (READY only).</summary>
    public Task<List<RemoteTransferDTO>> GetTransfers(string deviceId, CancellationToken ct = default);

    public Task<RemoteDownload> DownloadTransfer(string transferId, CancellationToken ct = default);

    /// <summary>Idempotent.</summary>
    public Task<RemoteTransferDTO> Ack(string transferId, string newHash, CancellationToken ct = default);

    public Task<DataDTO> Abort(string transferId, CancellationToken ct = default);
}

public record RemoteDownload(byte[] Bytes, string OriginalHash, string NewHash);

/// <summary>
/// The server answered with an error status.
/// </summary>
/// <param name="Code">"error-code" header, ex: <see cref="ConflictException.PENDING_DELIVERY"/></param>
/// <param name="ErrorData">"error-data" header, ex: the pending transfer id</param>
public class RemoteApiException(int status, string message, string? code = null, string? errorData = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public string? ErrorData { get; } = errorData;
}

/// <summary>
/// The server could not be reached, or the answer did not come back (offline, timeout...).
/// The request may or may not have been handled by the server.
/// </summary>
public class RemoteUnreachableException(string message, Exception? inner = null) : Exception(message, inner);
