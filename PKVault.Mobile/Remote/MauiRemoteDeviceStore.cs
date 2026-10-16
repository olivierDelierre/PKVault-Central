using PKVault.Core;

namespace PKVault.Mobile.Remote;

/// <summary>
/// Settings of the remote bank, in the app preferences.
/// </summary>
public class MauiRemoteDeviceStore : IRemoteDeviceStore
{
	private const string ModeKey = "pkvault-remote-mode";
	private const string ServerUrlKey = "pkvault-remote-url";
	private const string DeviceIdKey = "pkvault-remote-device-id";

	/// <summary>
	/// Created at first use, then kept: the server knows the device by it.
	/// 32 letters & digits, as expected by the server.
	/// </summary>
	public string DeviceId
	{
		get
		{
			var deviceId = Preferences.Default.Get(DeviceIdKey, string.Empty);
			if (string.IsNullOrEmpty(deviceId))
			{
				deviceId = Guid.NewGuid().ToString("N");
				Preferences.Default.Set(DeviceIdKey, deviceId);
			}
			return deviceId;
		}
	}

	public DeviceBankMode Mode
	{
		get => Enum.TryParse<DeviceBankMode>(Preferences.Default.Get(ModeKey, nameof(DeviceBankMode.LOCAL)), out var mode)
			? mode
			: DeviceBankMode.LOCAL;
		set => Preferences.Default.Set(ModeKey, value.ToString());
	}

	public string? ServerUrl
	{
		get
		{
			var serverUrl = Preferences.Default.Get(ServerUrlKey, string.Empty);
			return string.IsNullOrEmpty(serverUrl) ? null : serverUrl;
		}
		set
		{
			if (string.IsNullOrEmpty(value))
				Preferences.Default.Remove(ServerUrlKey);
			else
				Preferences.Default.Set(ServerUrlKey, value);
		}
	}

	/// <summary>
	/// Always the app private storage, never the folder chosen for the saves & the bank
	/// (it can be a shared folder reached through SAF).
	/// </summary>
	public string JournalDirectory => Path.Combine(FileSystem.AppDataDirectory, "remote-journal");
}
