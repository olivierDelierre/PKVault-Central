using PKVault.Core;

/// <summary>
/// Systematic interruption tests: the same complete flow is run once per possible failure point,
/// then the app is "restarted" (fresh coordinator over the same journal, device and server).
/// Whatever happens, the save must never be anything but the uploaded, the delivered or the played one,
/// and the transfer must end delivered once the failure is gone.
/// </summary>
public class TransferFaultSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // server calls of the flow: open, list (commit), download, ack... a few more to cover repeats
    public static IEnumerable<object[]> LostCalls()
    {
        for (var index = 0; index < 8; index++)
        {
            yield return [index, FaultKind.Before];
            yield return [index, FaultKind.After];
        }
    }

    public static IEnumerable<object[]> FailedWrites()
    {
        foreach (var fault in new[] { WriteFault.FailKeepingOld, WriteFault.TruncateThenFail })
        {
            for (var index = 0; index < 3; index++)
            {
                yield return [index, fault];
            }
        }
    }

    private static async Task RestartUntil(TransferTestEnv env, Func<TransferJournalEntry, bool> condition)
    {
        for (var i = 0; i < 5; i++)
        {
            await env.NewCoordinator().Resume(Ct);
            env.AssertDeviceSaveIsSane();

            if (condition(env.OnlyEntry))
            {
                return;
            }
        }
    }

    [Theory]
    [MemberData(nameof(LostCalls))]
    public async Task AnyLostServerCall_ShouldNeverHurtTheSave_AndTheTransferCompletesAfterRestart(int callIndex, FaultKind kind)
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (index, _) => index == callIndex ? kind : FaultKind.None;

        // 1. upload
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        env.AssertDeviceSaveIsSane();
        await RestartUntil(env, e => e.State == DeviceTransferState.UPLOADED);
        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);

        // 2. the player edits & commits on the server
        coordinator = env.NewCoordinator();
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(responseReceived: true, Ct);
        env.AssertDeviceSaveIsSane();

        // 3. restarts until nothing is left to do
        await RestartUntil(env, e => e.State == DeviceTransferState.ACKED);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.ACKED, entry.State);
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
        Assert.Equal(RemoteTransferState.ACKED, Assert.Single(env.Server.Deliveries).State);
        Assert.Equal(TransferTestEnv.OriginalBytes, await env.Journal.ReadBlob(entry.LocalId, TransferBlob.Original));
        Assert.Equal(TransferTestEnv.NewBytes, await env.Journal.ReadBlob(entry.LocalId, TransferBlob.New));
    }

    [Theory]
    [MemberData(nameof(LostCalls))]
    public async Task AnyLostServerCall_WhilePlayerPlayedMeanwhile_ShouldNeverOverwriteThePlayedSave(int callIndex, FaultKind kind)
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (index, _) => index == callIndex ? kind : FaultKind.None;

        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        await RestartUntil(env, e => e.State == DeviceTransferState.UPLOADED);

        // progress in the game after the upload
        env.Device.Files[TransferTestEnv.DevicePath] = TransferTestEnv.PlayedBytes.ToArray();

        coordinator = env.NewCoordinator();
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(responseReceived: true, Ct);
        await RestartUntil(env, e => e.State == DeviceTransferState.CONFLICT);

        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.PlayedBytes, env.DeviceBytes);
        Assert.Equal(0, env.Device.WriteCalls);
        Assert.Equal(RemoteTransferState.READY, Assert.Single(env.Server.Deliveries).State);
    }

    [Theory]
    [MemberData(nameof(FailedWrites))]
    public async Task AnyFailedWrite_ShouldNeverLeaveAHalfWrittenSave_AndTheTransferCompletes(int writeIndex, WriteFault fault)
    {
        var env = new TransferTestEnv();
        env.Device.WriteFaultAt = index => index == writeIndex ? fault : WriteFault.None;

        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(responseReceived: true, Ct);

        await RestartUntil(env, e => e.State == DeviceTransferState.ACKED);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AnyKillDuringWrite_ShouldBeCompletedAtNextStart(int killedWrite)
    {
        var env = new TransferTestEnv();
        using var cts = new CancellationTokenSource();
        env.Device.CancelOnFault = cts;
        env.Device.WriteFaultAt = index => index == killedWrite ? WriteFault.TruncateThenCancel : WriteFault.FailKeepingOld;

        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);

        // writes before the killed one fail normally and are retried
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.Resume(cts.Token));

        env.Device.WriteFaultAt = null;
        await RestartUntil(env, e => e.State == DeviceTransferState.ACKED);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
        Assert.Equal(
            TransferTestEnv.OriginalBytes,
            await env.Journal.ReadBlob(env.OnlyEntry.LocalId, TransferBlob.DeviceBeforeWrite)
        );
    }
}
