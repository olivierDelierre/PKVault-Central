using PKVault.Core;

/// <summary>A clock which only moves when told to.</summary>
public class ManualTimeProvider(DateTime start) : TimeProvider
{
    private DateTime now = start;

    public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);

    public void Advance(TimeSpan duration) => now += duration;
}

public class TransferHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TransferTestEnv env = new();
    private readonly ManualTimeProvider clock = new(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));

    private TransferCoordinator NewCoordinator()
        => new(env.Server, new TransferJournal(env.JournalFileSystem, env.JournalDirectory, clock), env.Device.FileIO, new FakeDeviceStore(), env.Refresher, clock);

    private async Task<TransferJournalEntry> DeliverOneSave()
    {
        var coordinator = NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(true, Ct);
        return env.OnlyEntry;
    }

    private IEnumerable<string> JournalFiles() => env.JournalFileSystem.AllFiles.Where(f => f.Contains("journal"));

    // --- lock

    [Fact]
    public async Task IsLocked_ShouldBeTrue_OnlyWhileTheSaveIsExchangedWithTheServer()
    {
        var coordinator = NewCoordinator();
        Assert.False(coordinator.IsLocked(TransferTestEnv.DevicePath));

        // waiting for edits on the server: the server holds the save
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        Assert.True(coordinator.IsLocked(TransferTestEnv.DevicePath));
        Assert.False(coordinator.IsLocked(TransferTestEnv.DevicePath + ".other"));

        await coordinator.CloseSession(env.OnlyEntry.LocalId, Ct);
        Assert.False(coordinator.IsLocked(TransferTestEnv.DevicePath));
    }

    [Fact]
    public async Task IsLocked_ShouldStayTrue_WhenWaitingForADecision_AndBeFalseOnceDelivered()
    {
        var coordinator = NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        env.Device.Files[TransferTestEnv.DevicePath] = TransferTestEnv.PlayedBytes.ToArray();
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);

        Assert.True(coordinator.IsLocked(TransferTestEnv.DevicePath));

        await coordinator.ResolveConflict(env.OnlyEntry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct);

        Assert.False(coordinator.IsLocked(TransferTestEnv.DevicePath));
    }

    // --- purge

    [Fact]
    public async Task Resume_ShouldKeepAFinishedTransfer_ForTheRetentionDelay_ThenRemoveItWithItsContents()
    {
        await DeliverOneSave();
        Assert.Equal(4, JournalFiles().Count());   // entry + 3 contents

        clock.Advance(TimeSpan.FromDays(29));
        await NewCoordinator().Resume(Ct);
        Assert.Equal(4, JournalFiles().Count());

        clock.Advance(TimeSpan.FromDays(2));
        await NewCoordinator().Resume(Ct);

        Assert.Empty(env.Journal.LoadAll());
        Assert.Empty(JournalFiles());
        // what was delivered stays on the device
        Assert.Equal(TransferTestEnv.NewBytes, env.DeviceBytes);
    }

    [Fact]
    public async Task PurgeFinished_ShouldNeverRemoveWhatIsNotDone()
    {
        // 1. in conflict
        var coordinator = NewCoordinator();
        await coordinator.StartUpload(TransferTestEnv.DevicePath, Ct);
        env.Device.Files[TransferTestEnv.DevicePath] = TransferTestEnv.PlayedBytes.ToArray();
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(TransferTestEnv.NewBytes);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);

        clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, coordinator.PurgeFinished());
        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);
        // entry + uploaded save + delivered save (nothing was written, so no copy of the device save)
        Assert.Equal(3, JournalFiles().Count());
    }

    [Fact]
    public async Task PurgeFinished_ShouldKeepAFinishedTransferWithAWarning_OrWithALocalReloadToDo()
    {
        env.Refresher.Result = false;
        var delivered = await DeliverOneSave();
        Assert.True(delivered.LocalReloadPending);

        clock.Advance(TimeSpan.FromDays(400));
        var coordinator = NewCoordinator();
        Assert.Equal(0, coordinator.PurgeFinished());

        // the reload is done: from then on it is a finished transfer like the others
        env.Refresher.Result = true;
        await coordinator.Resume(Ct);
        Assert.False(env.OnlyEntry.LocalReloadPending);
        Assert.Single(env.Journal.LoadAll());

        clock.Advance(TimeSpan.FromDays(31));
        await NewCoordinator().Resume(Ct);

        Assert.Empty(env.Journal.LoadAll());
    }

    [Fact]
    public async Task PurgeFinished_ShouldKeepAFinishedTransferWithAWarning()
    {
        var delivered = await DeliverOneSave();
        var journal = new TransferJournal(env.JournalFileSystem, env.JournalDirectory, clock);
        delivered.Warning = "The server forgot this delivery";
        await journal.Save(delivered);

        clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, NewCoordinator().PurgeFinished());
        Assert.Single(env.Journal.LoadAll());
    }

    [Fact]
    public async Task PurgeFinished_ShouldKeepAPausedTransfer()
    {
        env.Server.FaultAt = (_, _) => FaultKind.Before;
        var entry = await NewCoordinator().StartUpload(TransferTestEnv.DevicePath, Ct);
        Assert.Equal(DeviceTransferState.UPLOADING, entry.State);

        clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, NewCoordinator().PurgeFinished());
        Assert.Single(env.Journal.LoadAll());
    }

    [Fact]
    public async Task PurgeFinished_ShouldUseTheConfiguredRetention()
    {
        await DeliverOneSave();
        clock.Advance(TimeSpan.FromDays(2));
        var coordinator = NewCoordinator();
        coordinator.JournalRetention = TimeSpan.FromDays(1);

        Assert.Equal(1, coordinator.PurgeFinished());
    }

    [Fact]
    public async Task Purge_ShouldNotStopResume_WhenItFails()
    {
        await DeliverOneSave();
        clock.Advance(TimeSpan.FromDays(40));
        // a transfer which can move on, while the purge has a problem with the old one
        env.JournalFileSystem.File.SetAttributes(
            env.Journal.GetBlobPath(env.OnlyEntry.LocalId, TransferBlob.Original), System.IO.FileAttributes.ReadOnly
        );

        await NewCoordinator().Resume(Ct);

        // either purged or kept, but never a crash
        Assert.True(env.Journal.LoadAll().Count <= 1);
    }

    // --- journal delete

    [Fact]
    public async Task Journal_Delete_ShouldRemoveEverythingOfATransfer_AndNothingElse()
    {
        var journal = env.Journal;
        foreach (var id in new[] { "a", "b" })
        {
            await journal.Save(new TransferJournalEntry
            {
                LocalId = id,
                DeviceSavePath = "/saves/" + id,
                FileName = id,
                CreatedAt = DateTime.UtcNow,
            });
            foreach (var blob in Enum.GetValues<TransferBlob>())
            {
                await journal.WriteBlob(id, blob, [1]);
            }
        }

        journal.Delete("a");

        Assert.Equal(["b"], env.Journal.LoadAll().Select(e => e.LocalId));
        Assert.All(Enum.GetValues<TransferBlob>(), blob => Assert.False(journal.HasBlob("a", blob)));
        Assert.All(Enum.GetValues<TransferBlob>(), blob => Assert.True(journal.HasBlob("b", blob)));
    }

    [Fact]
    public void Journal_Delete_ShouldBeHarmless_ForAnUnknownTransfer()
    {
        env.Journal.Delete("nothing");
    }
}
