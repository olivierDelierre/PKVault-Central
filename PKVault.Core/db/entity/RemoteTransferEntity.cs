namespace PKVault.Core;

public enum RemoteTransferState
{
    /// <summary>Save committed on server, waiting for the device to download & ack it.</summary>
    READY,
    /// <summary>Device confirmed it wrote the new save.</summary>
    ACKED,
    /// <summary>Delivery cancelled, bank restored to the state before commit.</summary>
    ABORTED,
}

/// <summary>
/// Outbox row: a save modified on server for a remote device, waiting to be delivered.
/// It lives in the bank DB so that the bank changes and the pending delivery are
/// committed together by the session DB move, or not at all.
/// </summary>
public class RemoteTransferEntity
{
    public required string Id { get; init; }    // PK, GUID
    public required string DeviceId { get; init; }
    public required uint SaveId { get; init; }
    public required string FileName { get; init; }
    public required string OriginalHash { get; init; }
    public required string NewHash { get; init; }
    public required byte[] NewBytes { get; set; }
    public required DateTime BackupCreatedAt { get; init; }
    public required long CommitSeq { get; init; }
    public required RemoteTransferState State { get; set; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? AckedAt { get; set; }
}
