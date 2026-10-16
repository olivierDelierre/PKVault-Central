using System.IO.Abstractions.TestingHelpers;
using Moq;
using PKVault.Core;

public enum FaultKind { None, Before, After }

/// <summary>
/// In-memory server with the same rules as the real one for what the device relies on:
/// idempotent upload, one pending delivery per save, ack by hash, abort only when allowed.
/// Every call can be lost before the server handled it, or after (the answer never comes back).
/// </summary>
public class FakeRemoteServer : IRemoteApiClient
{
    public const uint SAVE_ID = 1506469232;

    public class Delivery
    {
        public required string Id { get; init; }
        public required string DeviceId { get; init; }
        public required byte[] OriginalBytes { get; init; }
        public required byte[] NewBytes { get; init; }
        public RemoteTransferState State { get; set; } = RemoteTransferState.READY;
        public long Seq { get; init; }
    }

    public Dictionary<uint, byte[]> OpenSessions { get; } = [];
    public List<Delivery> Deliveries { get; } = [];
    public List<string> Calls { get; } = [];

    public bool AbortAllowed { get; set; } = true;
    public bool SaveIdTakenByAnotherSource { get; set; }

    /// <summary>(call index, operation) => fault to apply</summary>
    public Func<int, string, FaultKind>? FaultAt { get; set; }

    /// <summary>Raised on download, to simulate bytes corrupted on the way.</summary>
    public Func<byte[], byte[]>? CorruptDownload { get; set; }

    private long seq;

    /// <summary>What the server does when a commit moves the save: the delivery is recorded, the session ends.</summary>
    public Delivery Commit(byte[] newBytes, string deviceId = FakeDeviceStore.DEVICE_ID)
    {
        var original = OpenSessions[SAVE_ID];
        OpenSessions.Remove(SAVE_ID);

        var delivery = new Delivery
        {
            Id = Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            OriginalBytes = original,
            NewBytes = newBytes,
            Seq = ++seq,
        };
        Deliveries.Add(delivery);
        return delivery;
    }

    public static string Hash(byte[] bytes) => RemoteSaveService.ComputeHash(bytes);

    private static RemoteTransferDTO ToDTO(Delivery d) => new(
        d.Id, SAVE_ID, "main.sav", Hash(d.OriginalBytes), Hash(d.NewBytes), d.NewBytes.Length, d.Seq, DateTime.UtcNow, d.State.ToString()
    );

    private async Task<T> Run<T>(string op, Func<T> action)
    {
        await Task.Yield();

        var index = Calls.Count;
        Calls.Add(op);

        var fault = FaultAt?.Invoke(index, op) ?? FaultKind.None;
        if (fault == FaultKind.Before)
        {
            throw new RemoteUnreachableException($"lost before {op}");
        }

        var result = action();

        if (fault == FaultKind.After)
        {
            throw new RemoteUnreachableException($"lost after {op}");
        }

        return result;
    }

    public Task<RemotePingDTO> Ping(CancellationToken ct = default)
        => Run("ping", () => new RemotePingDTO("test", seq));

    public Task<RemoteOpenSessionResult> OpenSession(string deviceId, string fileName, byte[] saveBytes, CancellationToken ct = default)
        => Run("open", () =>
        {
            if (SaveIdTakenByAnotherSource)
            {
                throw new RemoteApiException(409, "same save id from another source", ConflictException.SAVE_ID_CONFLICT);
            }

            var pending = Deliveries.FirstOrDefault(d => d.State == RemoteTransferState.READY);
            if (pending != null)
            {
                throw new RemoteApiException(409, "pending", ConflictException.PENDING_DELIVERY, pending.Id);
            }

            OpenSessions[SAVE_ID] = saveBytes.ToArray();
            return new RemoteOpenSessionResult(SAVE_ID, Hash(saveBytes));
        });

    public Task CloseSession(string deviceId, uint saveId, CancellationToken ct = default)
        => Run("close", () =>
        {
            OpenSessions.Remove(saveId);
            return true;
        });

    public Task<List<RemoteTransferDTO>> GetTransfers(string deviceId, CancellationToken ct = default)
        => Run("list", () => Deliveries
            .Where(d => d.DeviceId == deviceId && d.State == RemoteTransferState.READY)
            .Select(ToDTO)
            .ToList());

    public Task<RemoteDownload> DownloadTransfer(string transferId, CancellationToken ct = default)
        => Run("download", () =>
        {
            var delivery = Deliveries.FirstOrDefault(d => d.Id == transferId)
                ?? throw new RemoteApiException(404, "transfer not found");

            var bytes = CorruptDownload?.Invoke(delivery.NewBytes) ?? delivery.NewBytes;
            return new RemoteDownload(bytes.ToArray(), Hash(delivery.OriginalBytes), Hash(delivery.NewBytes));
        });

    public Task<RemoteTransferDTO> Ack(string transferId, string newHash, CancellationToken ct = default)
        => Run("ack", () =>
        {
            var delivery = Deliveries.FirstOrDefault(d => d.Id == transferId)
                ?? throw new RemoteApiException(404, "transfer not found");

            if (!string.Equals(Hash(delivery.NewBytes), newHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteApiException(409, "hash mismatch", ConflictException.HASH_MISMATCH);
            }

            delivery.State = RemoteTransferState.ACKED;
            return ToDTO(delivery);
        });

    public Task Abort(string transferId, CancellationToken ct = default)
        => Run("abort", () =>
        {
            var delivery = Deliveries.FirstOrDefault(d => d.Id == transferId)
                ?? throw new RemoteApiException(404, "transfer not found");

            if (delivery.State != RemoteTransferState.READY || !AbortAllowed)
            {
                throw new RemoteApiException(409, "abort not possible", ConflictException.ABORT_NOT_POSSIBLE);
            }

            // like the backup restore: delivery gone, uploaded save loaded again
            Deliveries.Remove(delivery);
            OpenSessions[SAVE_ID] = delivery.OriginalBytes;
            return true;
        });
}

public enum WriteFault { None, FailKeepingOld, TruncateThenFail, TruncateThenCancel, SilentlyCorrupt }

/// <summary>
/// Device save files. A write can fail the way a SAF write does: the file is truncated, then it fails.
/// </summary>
public class FakeDevice
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public int WriteCalls { get; private set; }

    /// <summary>write index => fault</summary>
    public Func<int, WriteFault>? WriteFaultAt { get; set; }

    public CancellationTokenSource? CancelOnFault { get; set; }

    public Mock<IFileIOService> Mock { get; } = new();

    public FakeDevice()
    {
        Mock.Setup(x => x.Exists(It.IsAny<string>())).Returns((string p) => Files.ContainsKey(p));
        Mock.Setup(x => x.ReadBytes(It.IsAny<string>())).Returns((string p) =>
            Files.TryGetValue(p, out var bytes)
                ? Task.FromResult(bytes.ToArray())
                : throw new FileNotFoundException(p));
        Mock.Setup(x => x.WriteBytes(It.IsAny<string>(), It.IsAny<byte[]>())).Returns(async (string p, byte[] bytes) =>
        {
            await Task.Yield();

            // counted even without fault injection: tests assert "nothing was written"
            var writeIndex = WriteCalls++;
            var fault = WriteFaultAt?.Invoke(writeIndex) ?? WriteFault.None;
            switch (fault)
            {
                case WriteFault.None:
                    Files[p] = bytes.ToArray();
                    break;
                case WriteFault.SilentlyCorrupt:
                    // no error, but not what was given: only a read-back can notice it
                    Files[p] = [.. bytes.Select(b => (byte)(b ^ 0x5A))];
                    break;
                case WriteFault.FailKeepingOld:
                    throw new IOException("write failed");
                case WriteFault.TruncateThenFail:
                    Files[p] = bytes[..(bytes.Length / 2)];
                    throw new IOException("write failed after truncation");
                case WriteFault.TruncateThenCancel:
                    // the app is killed in the middle of the write
                    Files[p] = bytes[..(bytes.Length / 2)];
                    CancelOnFault?.Cancel();
                    throw new OperationCanceledException();
            }
        });
    }

    public IFileIOService FileIO => Mock.Object;
}

public class FakeDeviceStore : IRemoteDeviceStore
{
    public const string DEVICE_ID = "device-0001";
    public string DeviceId => DEVICE_ID;
}

public class FakeLocalSavesRefresher : ILocalSavesRefresher
{
    public int Calls { get; private set; }
    public bool Result { get; set; } = true;

    public Task<bool> TryRefresh()
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Everything that survives an app restart (journal files, device files, server) lives here;
/// <see cref="NewCoordinator"/> builds a fresh coordinator over it, like a restart would.
/// </summary>
public class TransferTestEnv
{
    public static readonly string DevicePath = Path.Combine(Directory.GetCurrentDirectory(), "saves", "main.sav");

    public static byte[] OriginalBytes { get; } = [.. Enumerable.Range(0, 200).Select(i => (byte)(i % 251))];
    public static byte[] NewBytes { get; } = [.. Enumerable.Range(0, 200).Select(i => (byte)((i * 7 + 3) % 251))];
    public static byte[] PlayedBytes { get; } = [.. Enumerable.Range(0, 200).Select(i => (byte)((i * 5 + 1) % 251))];

    public MockFileSystem JournalFileSystem { get; } = new(new Dictionary<string, MockFileData>(), Directory.GetCurrentDirectory());
    public string JournalDirectory { get; } = Path.Combine(Directory.GetCurrentDirectory(), "journal");

    public FakeRemoteServer Server { get; } = new();
    public FakeDevice Device { get; } = new();
    public FakeLocalSavesRefresher Refresher { get; } = new();

    public TransferJournal Journal => new(JournalFileSystem, JournalDirectory, TimeProvider.System);

    public TransferTestEnv()
    {
        Device.Files[DevicePath] = OriginalBytes.ToArray();
    }

    public TransferCoordinator NewCoordinator()
        => new(Server, Journal, Device.FileIO, new FakeDeviceStore(), Refresher, TimeProvider.System);

    public byte[] DeviceBytes => Device.Files[DevicePath];

    public TransferJournalEntry OnlyEntry => Assert.Single(Journal.LoadAll());

    /// <summary>The save must always be the uploaded one or the delivered one, never anything else.</summary>
    public void AssertDeviceSaveIsSane()
    {
        var bytes = DeviceBytes;
        Assert.True(
            bytes.SequenceEqual(OriginalBytes) || bytes.SequenceEqual(NewBytes) || bytes.SequenceEqual(PlayedBytes),
            "Device save is neither the original, the delivered nor the played one"
        );
    }
}
