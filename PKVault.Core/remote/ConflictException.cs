namespace PKVault.Core;

/// <summary>
/// Request valid but not applicable in the current state (HTTP 409).
/// <see cref="Code"/> is machine-readable (header "error-code"), the optional
/// <see cref="Data"/> carries a related identifier (header "error-data").
/// </summary>
public class ConflictException(string code, string message, string? data = null) : Exception(message)
{
    public const string PENDING_DELIVERY = "PENDING_DELIVERY";
    public const string SAVE_ID_CONFLICT = "SAVE_ID_CONFLICT";
    public const string HASH_MISMATCH = "HASH_MISMATCH";
    public const string ABORT_NOT_POSSIBLE = "ABORT_NOT_POSSIBLE";

    public string Code { get; } = code;
    public string? ErrorData { get; } = data;
}
