namespace PKVault.Core.remote.routes;

/// <summary>
/// API for remote devices using this bank, see <see cref="RemoteBankService"/>.
/// The commit itself is the usual "api/storage/action/save".
/// </summary>
[Route("api/[controller]")]
public class RemoteController(RemoteBankService remoteBankService)
{
    [HttpGet("ping")]
    public async Task<RemotePingDTO> Ping()
    {
        return await remoteBankService.Ping();
    }

    [HttpPost("session/open")]
    public async Task<RemoteOpenSessionDTO> OpenSession(CoreFile saveFile, string deviceId, string fileName)
    {
        return await remoteBankService.OpenSession(deviceId, fileName, saveFile.Stream);
    }

    [HttpPost("session/close")]
    public async Task<DataDTO> CloseSession(string deviceId, uint saveId)
    {
        return await remoteBankService.CloseSession(deviceId, saveId);
    }

    [HttpGet("transfers")]
    public async Task<List<RemoteTransferDTO>> GetTransfers(string deviceId)
    {
        return await remoteBankService.GetTransfers(deviceId);
    }

    [HttpGet("transfers/{id}/download")]
    public async Task<CoreFileResponse> Download(string id)
    {
        return await remoteBankService.Download(id);
    }

    [HttpPost("transfers/{id}/ack")]
    public async Task<RemoteTransferDTO> Ack(string id, string newHash)
    {
        return await remoteBankService.Ack(id, newHash);
    }

    [HttpPost("transfers/{id}/abort")]
    public async Task<DataDTO> Abort(string id)
    {
        return await remoteBankService.Abort(id);
    }
}
