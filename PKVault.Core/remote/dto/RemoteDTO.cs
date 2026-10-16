namespace PKVault.Core;

public record RemotePingDTO(string Version, long CommitSeq);

public record RemoteOpenSessionDTO(uint SaveId, string OriginalHash, DataDTO Data);

/// <summary>A save modified on server, waiting to be written on the device.</summary>
/// <param name="State">READY, ACKED or ABORTED</param>
public record RemoteTransferDTO(
    string Id,
    uint SaveId,
    string FileName,
    string OriginalHash,
    string NewHash,
    long Size,
    long CommitSeq,
    DateTime CreatedAt,
    string State
);
