using Serilog;

namespace PKVault.Core;

/// <summary>
/// Reloads the saves of the device once one has been rewritten by a delivery,
/// the same way the reload button of the UI does.
/// </summary>
public class LocalSavesRefresher(
    ISettingsService settingsService, ISavesLoadersService savesLoadersService, ISessionService sessionService
) : ILocalSavesRefresher
{
    public async Task<bool> TryRefresh()
    {
        if (!sessionService.HasEmptyActionList())
        {
            // pending local actions would be lost: tried again later
            Log.Information("Local saves reload postponed: local actions are pending");
            return false;
        }

        DataUpdateFlags flags = new();

        settingsService.RefreshSettings(flags);

        savesLoadersService.Clear();
        await sessionService.StartNewSession(checkInitialActions: true, flags);

        return true;
    }
}
