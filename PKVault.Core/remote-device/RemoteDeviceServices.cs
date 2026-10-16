using System.IO.Abstractions;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace PKVault.Core;

public static class RemoteDeviceServices
{
    /// <summary>
    /// Registers the device side of the remote bank. The host (the Android app) has to register
    /// <see cref="IRemoteDeviceStore"/> and <see cref="IRemoteSecretStore"/> first.
    /// </summary>
    public static IServiceCollection AddRemoteDevice(this IServiceCollection services)
    {
        services.AddSingleton<IRemoteConnectionInfo, RemoteConnectionInfo>();

        services.AddSingleton(sp => new RemoteHttpConnection(
            new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                // timeouts are set per request, a commit takes much longer than a status check
                Timeout = Timeout.InfiniteTimeSpan,
            },
            sp.GetRequiredService<IRemoteConnectionInfo>()
        ));
        services.AddSingleton<IRemoteApiClient, HttpRemoteApiClient>();

        services.AddSingleton(sp => new TransferJournal(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IRemoteDeviceStore>().JournalDirectory,
            sp.GetRequiredService<TimeProvider>()
        ));
        services.AddSingleton<ILocalSavesRefresher, LocalSavesRefresher>();

        // singletons: the coordinator runs one transfer step at a time for the whole app
        services.AddSingleton<TransferCoordinator>();
        services.AddSingleton<RemoteProxy>();
        services.AddSingleton<RemoteDeviceService>();

        return services;
    }
}
