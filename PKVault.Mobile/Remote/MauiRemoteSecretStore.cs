using PKVault.Core;
using Serilog;

namespace PKVault.Mobile.Remote;

/// <summary>
/// The access token of the server, in the secure storage of the device.
/// </summary>
public class MauiRemoteSecretStore : IRemoteSecretStore
{
	private const string TokenKey = "pkvault-remote-token";

	// the secure storage is asynchronous, the token is read when a request is built
	private readonly Task<string?> loading;
	private string? token;
	private bool loaded;

	public MauiRemoteSecretStore()
	{
		loading = LoadAsync();
	}

	public string? Token
	{
		get
		{
			if (!loaded && loading.Wait(TimeSpan.FromSeconds(3)))
			{
				token = loading.Result;
				loaded = true;
			}
			return token;
		}
	}

	public async Task SetToken(string? value)
	{
		if (string.IsNullOrEmpty(value))
			SecureStorage.Default.Remove(TokenKey);
		else
			await SecureStorage.Default.SetAsync(TokenKey, value);

		token = string.IsNullOrEmpty(value) ? null : value;
		loaded = true;
	}

	private static async Task<string?> LoadAsync()
	{
		try
		{
			return await SecureStorage.Default.GetAsync(TokenKey);
		}
		catch (Exception ex)
		{
			// ex: the secure storage was reset by the system: the token has to be entered again
			Log.Warning(ex, "Cannot read the remote server token");
			return null;
		}
	}
}
