using Serilog;

namespace PKVault.Core;

/// <summary>
/// What the UI can do about the remote bank, from the device: set the server, switch between the local
/// and the remote bank, send a save of the device to the server, follow and settle the deliveries.
/// </summary>
public class RemoteDeviceService(
    IRemoteDeviceStore store, IRemoteSecretStore secrets, TransferCoordinator coordinator, IRemoteApiClient api,
    RemoteProxy proxy, ISavesLoadersService savesLoadersService, ISessionService sessionService, ISettingsService settingsService
)
{
    /// <summary>The status is polled by the UI: it must stay quick when the server does not answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private int backgroundResumeRunning;

    /// <summary>For tests: the resume started by the last status request, if any.</summary>
    public Task? BackgroundResume { get; private set; }

    public bool IsRemoteMode => store.Mode == DeviceBankMode.REMOTE;

    /// <summary>
    /// In remote mode, answers the request with the server. Null when it has to be handled locally.
    /// </summary>
    public async Task<DesktopFetchResponse?> TryForward(string url, DesktopFetchRequestInit init, CancellationToken ct = default)
    {
        if (!IsRemoteMode || RemoteProxy.IsLocalRoute(new Uri(url).LocalPath))
        {
            return null;
        }

        return await proxy.Forward(url, init, ct);
    }

    public async Task<DeviceRemoteStatusDTO> GetStatus(CancellationToken ct = default)
    {
        var entries = coordinator.GetEntries();

        var reachable = false;
        var authorized = true;
        string? error = null;
        if (IsRemoteMode)
        {
            (reachable, authorized, error) = await Probe(ct);

            if (reachable && authorized && entries.Any(NeedsWork))
            {
                // the connection is back: unfinished transfers go on without waiting for the user
                StartBackgroundResume();
            }
        }

        return new DeviceRemoteStatusDTO(
            Mode: store.Mode.ToString(),
            ServerUrl: store.ServerUrl,
            HasToken: !string.IsNullOrEmpty(secrets.Token),
            Reachable: reachable,
            Authorized: authorized,
            ConnectionError: error,
            Transfers: [.. entries.Where(IsWorthShowing).Select(ToDTO)]
        );
    }

    public async Task<DeviceRemoteStatusDTO> SetConfig(DeviceRemoteConfigInput input, CancellationToken ct = default)
    {
        if (input.ServerUrl != null)
        {
            var serverUrl = input.ServerUrl.Trim().TrimEnd('/');

            if (serverUrl.Length > 0
                && (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            {
                throw new ArgumentException("Server address should be a http:// or https:// address");
            }

            var newUrl = serverUrl.Length == 0 ? null : serverUrl;
            if (newUrl != store.ServerUrl && coordinator.GetEntries().Any(e => !e.IsTerminal))
            {
                // the deliveries wait on the server which got the saves
                throw new InvalidOperationException("The server cannot be changed while saves are being sent to it");
            }

            store.ServerUrl = newUrl;
        }

        if (input.Token != null)
        {
            await secrets.SetToken(input.Token.Trim().Length == 0 ? null : input.Token.Trim());
        }

        return await GetStatus(ct);
    }

    /// <summary>Checks the saved address & token.</summary>
    public async Task<RemotePingDTO> TestConnection(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout * 2);

        try
        {
            return await api.Ping(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteUnreachableException("The server did not answer in time");
        }
    }

    public async Task<DeviceRemoteStatusDTO> SetMode(DeviceBankMode mode, CancellationToken ct = default)
    {
        if (mode == store.Mode)
        {
            return await GetStatus(ct);
        }

        if (mode == DeviceBankMode.REMOTE)
        {
            if (string.IsNullOrWhiteSpace(store.ServerUrl))
            {
                throw new InvalidOperationException("The remote server is not set");
            }

            if (!sessionService.HasEmptyActionList())
            {
                throw new InvalidOperationException("Empty action list is required");
            }

            var (reachable, authorized, error) = await Probe(ct);
            if (!reachable)
            {
                throw new RemoteUnreachableException(error ?? "The server cannot be reached");
            }

            if (!authorized)
            {
                throw new RemoteApiException(401, "The server refused the access token");
            }
        }
        else if (coordinator.GetEntries().Any(e => IsDelivering(e.State)))
        {
            throw new InvalidOperationException("A save is being delivered to this device, it has to be finished first");
        }

        store.Mode = mode;
        Log.Information($"Bank mode set to {mode}");

        return await GetStatus(ct);
    }

    /// <summary>The save files of the device, with the transfer they are in.</summary>
    public List<DeviceSaveDTO> GetDeviceSaves()
    {
        var settings = settingsService.GetSettings();
        var entries = coordinator.GetEntries();

        return [.. savesLoadersService.GetAllSaveInfos().Values
            // saves received by this app acting as a server are not the ones of the device
            .Where(infos => !settings.IsUnderRemoteSavesPath(infos.Path))
            .Select(infos => new DeviceSaveDTO(
                infos,
                entries.Where(e => e.DeviceSavePath == infos.Path && !e.IsTerminal).Select(ToDTO).FirstOrDefault()
            ))];
    }

    /// <summary>Sends a save of the device to the server, to be edited there.</summary>
    public async Task<DeviceTransferDTO> OpenOnServer(uint saveId, CancellationToken ct = default)
    {
        if (!IsRemoteMode)
        {
            throw new InvalidOperationException("Remote bank mode is required");
        }

        var infos = savesLoadersService.GetAllSaveInfos();
        if (!infos.TryGetValue(saveId, out var save))
        {
            throw new KeyNotFoundException($"Save not found: {saveId}");
        }

        if (settingsService.GetSettings().IsUnderRemoteSavesPath(save.Path))
        {
            throw new InvalidOperationException("This save is not a save of the device");
        }

        var entry = await coordinator.StartUpload(save.Path, ct);

        if (entry.State == DeviceTransferState.CLOSED && entry.LastError == ConflictException.SAVE_ID_CONFLICT)
        {
            throw new ConflictException(
                ConflictException.SAVE_ID_CONFLICT,
                $"The server already has the save {saveId} from another source (its own saves or another device)"
            );
        }

        return ToDTO(entry);
    }

    /// <summary>Stops working on a save of the device on the server, with nothing to deliver.</summary>
    public async Task<DeviceTransferDTO> CloseOnServer(string localId, CancellationToken ct = default)
    {
        await coordinator.CloseSession(localId, ct);
        return ToDTO(GetEntry(localId));
    }

    public async Task<DeviceTransferDTO> ResolveConflict(string localId, TransferConflictResolution resolution, CancellationToken ct = default)
    {
        await coordinator.ResolveConflict(localId, resolution, ct);
        return ToDTO(GetEntry(localId));
    }

    /// <summary>Goes on with the unfinished transfers now.</summary>
    public async Task<DeviceRemoteStatusDTO> Sync(CancellationToken ct = default)
    {
        await coordinator.Resume(ct);
        return await GetStatus(ct);
    }

    // --- helpers

    private async Task<(bool Reachable, bool Authorized, string? Error)> Probe(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            await api.Ping(cts.Token);
            return (true, true, null);
        }
        catch (RemoteApiException ex) when (ex.Status == 401)
        {
            return (true, false, "UNAUTHORIZED");
        }
        catch (RemoteApiException ex)
        {
            return (true, true, $"{ex.Status} {ex.Message}".Trim());
        }
        catch (RemoteUnreachableException ex)
        {
            return (false, true, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, true, "The server did not answer in time");
        }
    }

    private void StartBackgroundResume()
    {
        if (Interlocked.CompareExchange(ref backgroundResumeRunning, 1, 0) != 0)
        {
            return;
        }

        BackgroundResume = Task.Run(async () =>
        {
            try
            {
                await coordinator.Resume();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background resume of remote transfers failed");
            }
            finally
            {
                Interlocked.Exchange(ref backgroundResumeRunning, 0);
            }
        });
    }

    private TransferJournalEntry GetEntry(string localId)
        => coordinator.GetEntries().FirstOrDefault(e => e.LocalId == localId)
            ?? throw new KeyNotFoundException($"Transfer not found: {localId}");

    /// <summary>The transfer can move on by itself (as opposed to waiting for the user).</summary>
    private static bool NeedsWork(TransferJournalEntry entry)
        => entry.IsTerminal
            ? entry.LocalReloadPending
            : entry.State is not (DeviceTransferState.UPLOADED or DeviceTransferState.CONFLICT);

    /// <summary>The new save is on its way to the device save.</summary>
    private static bool IsDelivering(DeviceTransferState state)
        => state is DeviceTransferState.COMMIT_REQUESTED
            or DeviceTransferState.COMMITTED_ON_SERVER
            or DeviceTransferState.DOWNLOADED
            or DeviceTransferState.WRITING
            or DeviceTransferState.WRITTEN;

    private static bool IsWorthShowing(TransferJournalEntry entry)
        => !entry.IsTerminal || entry.Warning != null || entry.LocalReloadPending;

    private static DeviceTransferDTO ToDTO(TransferJournalEntry entry) => new(
        entry.LocalId,
        entry.SaveId,
        entry.FileName,
        entry.State.ToString(),
        entry.ConflictReason?.ToString(),
        entry.LastError,
        entry.Warning,
        entry.LocalReloadPending,
        entry.UpdatedAt
    );
}
