using Microsoft.Extensions.DependencyInjection;

namespace PKVault.Core.remote_device.routes;

/// <summary>
/// The remote bank seen from the device. Always handled by the app itself, even in remote mode.
/// Only available in the Android app: elsewhere there is no device to speak of.
/// </summary>
[Route("api/[controller]")]
public class DeviceController(IServiceProvider sp)
{
    private RemoteDeviceService Service => sp.GetService<RemoteDeviceService>()
        ?? throw new InvalidOperationException("The remote bank is only available in the Android app");

    [HttpGet("status")]
    public async Task<DeviceRemoteStatusDTO> GetStatus()
    {
        return await Service.GetStatus();
    }

    [HttpPut("config")]
    public async Task<DeviceRemoteStatusDTO> SetConfig(DeviceRemoteConfigInput input)
    {
        return await Service.SetConfig(input);
    }

    [HttpPost("test")]
    public async Task<RemotePingDTO> TestConnection()
    {
        return await Service.TestConnection();
    }

    [HttpPut("mode")]
    public async Task<DeviceRemoteStatusDTO> SetMode(DeviceBankMode mode)
    {
        return await Service.SetMode(mode);
    }

    [HttpGet("saves")]
    public List<DeviceSaveDTO> GetSaves()
    {
        return Service.GetDeviceSaves();
    }

    [HttpPost("saves/{saveId}/open")]
    public async Task<DeviceTransferDTO> OpenOnServer(uint saveId)
    {
        return await Service.OpenOnServer(saveId);
    }

    [HttpPost("transfers/{localId}/close")]
    public async Task<DeviceTransferDTO> CloseTransfer(string localId)
    {
        return await Service.CloseOnServer(localId);
    }

    [HttpPost("transfers/{localId}/resolve")]
    public async Task<DeviceTransferDTO> ResolveConflict(string localId, TransferConflictResolution resolution)
    {
        return await Service.ResolveConflict(localId, resolution);
    }

    [HttpPost("sync")]
    public async Task<DeviceRemoteStatusDTO> Sync()
    {
        return await Service.Sync();
    }
}
