namespace PKVault.Core;

/// <summary>A save moving between the device and the server.</summary>
/// <param name="State">See <see cref="DeviceTransferState"/></param>
/// <param name="ConflictReason">See <see cref="DeviceConflictReason"/>, only in conflict</param>
/// <param name="LastError">The transfer is paused on this problem and resumes by itself</param>
public record DeviceTransferDTO(
    string LocalId,
    uint? SaveId,
    string FileName,
    string State,
    string? ConflictReason,
    string? LastError,
    string? Warning,
    bool LocalReloadPending,
    DateTime UpdatedAt
);

/// <summary>A save file of the device, with its transfer if any.</summary>
public record DeviceSaveDTO(SaveInfosDTO Save, DeviceTransferDTO? Transfer);

/// <param name="Mode">LOCAL or REMOTE</param>
/// <param name="HasToken">The token itself is never sent back</param>
/// <param name="Reachable">Only checked in REMOTE mode</param>
/// <param name="Authorized">False if the server refused the token</param>
/// <param name="ConnectionError">Why the server cannot be used, if so</param>
/// <param name="Transfers">Transfers not finished, or finished with something to know</param>
public record DeviceRemoteStatusDTO(
    string Mode,
    string? ServerUrl,
    bool HasToken,
    bool Reachable,
    bool Authorized,
    string? ConnectionError,
    List<DeviceTransferDTO> Transfers
);

/// <param name="ServerUrl">Null: unchanged. Empty: removed.</param>
/// <param name="Token">Null: unchanged. Empty: removed.</param>
public record DeviceRemoteConfigInput(string? ServerUrl, string? Token);
