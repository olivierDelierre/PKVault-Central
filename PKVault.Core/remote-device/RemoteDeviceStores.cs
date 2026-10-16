using System.Text.Json.Serialization;

namespace PKVault.Core;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceBankMode>))]
public enum DeviceBankMode
{
    /// <summary>The bank is the one stored on this device.</summary>
    LOCAL,
    /// <summary>The bank is the one of the server, saves of the device are sent to it to be edited.</summary>
    REMOTE,
}

/// <summary>
/// Settings of the remote bank which live on the device (the Android app keeps them in its preferences).
/// </summary>
public interface IRemoteDeviceStore
{
    /// <summary>Stable identifier of this installation, sent to the server.</summary>
    public string DeviceId { get; }

    public DeviceBankMode Mode { get; set; }

    /// <summary>Address of the server, ex: "https://nas.local:3000".</summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Where the transfers journal is kept. It must be in the app private storage.
    /// </summary>
    public string JournalDirectory { get; }
}

/// <summary>
/// The access token is a secret: the Android app keeps it in its secure storage and never gives it back to the UI.
/// </summary>
public interface IRemoteSecretStore
{
    public string? Token { get; }

    public Task SetToken(string? token);
}

/// <summary>Address & token currently set, as seen by <see cref="RemoteHttpConnection"/>.</summary>
public class RemoteConnectionInfo(IRemoteDeviceStore store, IRemoteSecretStore secrets) : IRemoteConnectionInfo
{
    public string? ServerUrl => store.ServerUrl;
    public string? Token => secrets.Token;
}

public class InMemoryRemoteDeviceStore(string journalDirectory, string deviceId = "device-in-memory") : IRemoteDeviceStore
{
    public string DeviceId { get; } = deviceId;
    public DeviceBankMode Mode { get; set; } = DeviceBankMode.LOCAL;
    public string? ServerUrl { get; set; }
    public string JournalDirectory { get; } = journalDirectory;
}

public class InMemoryRemoteSecretStore : IRemoteSecretStore
{
    public string? Token { get; private set; }

    public Task SetToken(string? token)
    {
        Token = token;
        return Task.CompletedTask;
    }
}
