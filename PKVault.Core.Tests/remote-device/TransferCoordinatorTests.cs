using PKVault.Core;

public class TransferCoordinatorTests
{
    private static readonly string DevicePath = TransferTestEnv.DevicePath;
    private static readonly byte[] Original = TransferTestEnv.OriginalBytes;
    private static readonly byte[] New = TransferTestEnv.NewBytes;
    private static readonly byte[] Played = TransferTestEnv.PlayedBytes;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash(byte[] bytes) => RemoteSaveService.ComputeHash(bytes);

    /// <summary>Upload, then a commit that moves the save on the server.</summary>
    private static async Task<TransferCoordinator> UploadAndCommit(TransferTestEnv env)
    {
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(DevicePath, Ct);
        await coordinator.BeginCommit(Ct);
        env.Server.Commit(New);
        return coordinator;
    }

    // --- happy path

    [Fact]
    public async Task FullFlow_ShouldDeliverTheNewSave_AckIt_AndKeepEverythingInTheJournal()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();

        var entry = await coordinator.StartUpload(DevicePath, Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, entry.State);
        Assert.Equal(FakeRemoteServer.SAVE_ID, entry.SaveId);
        Assert.Equal(Hash(Original), entry.OriginalHash);
        Assert.Equal(Original, env.Server.OpenSessions[FakeRemoteServer.SAVE_ID]);

        await coordinator.BeginCommit(Ct);
        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, env.OnlyEntry.State);

        env.Server.Commit(New);
        await coordinator.EndCommit(responseReceived: true, Ct);

        var done = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.ACKED, done.State);
        Assert.Null(done.LastError);
        Assert.Equal(New, env.DeviceBytes);
        Assert.Equal(RemoteTransferState.ACKED, Assert.Single(env.Server.Deliveries).State);

        // local saves reloaded once
        Assert.Equal(1, env.Refresher.Calls);
        Assert.False(done.LocalReloadPending);

        // nothing is thrown away: contents stay to recover from any later problem
        var journal = env.Journal;
        Assert.Equal(Original, await journal.ReadBlob(done.LocalId, TransferBlob.Original));
        Assert.Equal(New, await journal.ReadBlob(done.LocalId, TransferBlob.New));
        Assert.Equal(Original, await journal.ReadBlob(done.LocalId, TransferBlob.DeviceBeforeWrite));
    }

    [Fact]
    public async Task StartUpload_ShouldReturnTheTransferInProgress_InsteadOfUploadingAgain()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();

        var first = await coordinator.StartUpload(DevicePath, Ct);
        var second = await coordinator.StartUpload(DevicePath, Ct);

        Assert.Equal(first.LocalId, second.LocalId);
        Assert.Single(env.Journal.LoadAll());
        Assert.Single(env.Server.Calls, "open");
    }

    [Fact]
    public async Task StartUpload_ShouldRefuseMissingDeviceSave_AndRecordNothing()
    {
        var env = new TransferTestEnv();
        env.Device.Files.Clear();

        await Assert.ThrowsAsync<FileNotFoundException>(() => env.NewCoordinator().StartUpload(DevicePath, Ct));

        Assert.Empty(env.Journal.LoadAll());
        Assert.Empty(env.Server.Calls);
    }

    // --- upload problems

    [Fact]
    public async Task Upload_ShouldPause_WhenServerUnreachable_AndResumeLater()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (i, _) => i == 0 ? FaultKind.Before : FaultKind.None;

        var entry = await env.NewCoordinator().StartUpload(DevicePath, Ct);

        Assert.Equal(DeviceTransferState.UPLOADING, entry.State);
        Assert.NotNull(env.OnlyEntry.LastError);
        Assert.Empty(env.Server.OpenSessions);

        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
        Assert.Null(env.OnlyEntry.LastError);
    }

    [Fact]
    public async Task Upload_Retry_ShouldSendTheExactBytesFirstRead_EvenIfTheDeviceSaveChangedMeanwhile()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (i, _) => i == 0 ? FaultKind.Before : FaultKind.None;
        await env.NewCoordinator().StartUpload(DevicePath, Ct);

        // the player plays between the failed attempt and the retry
        env.Device.Files[DevicePath] = Played.ToArray();
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(Original, env.Server.OpenSessions[FakeRemoteServer.SAVE_ID]);
        Assert.Equal(Hash(Original), env.OnlyEntry.OriginalHash);
    }

    [Fact]
    public async Task Upload_WithLostAnswer_ShouldBeRepeatedSafely()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (i, _) => i == 0 ? FaultKind.After : FaultKind.None;

        await env.NewCoordinator().StartUpload(DevicePath, Ct);
        Assert.Equal(DeviceTransferState.UPLOADING, env.OnlyEntry.State);

        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
        Assert.Single(env.Server.OpenSessions);
    }

    [Fact]
    public async Task Upload_ShouldClose_WhenServerAlreadyHasTheSaveFromAnotherSource()
    {
        var env = new TransferTestEnv();
        env.Server.SaveIdTakenByAnotherSource = true;

        var entry = await env.NewCoordinator().StartUpload(DevicePath, Ct);

        Assert.Equal(DeviceTransferState.CLOSED, entry.State);
        Assert.Equal(ConflictException.SAVE_ID_CONFLICT, entry.LastError);

        env.Server.Calls.Clear();
        await env.NewCoordinator().Resume(Ct);
        Assert.Empty(env.Server.Calls);
    }

    [Fact]
    public async Task Upload_ShouldFinishAnExistingDelivery_WhenTheServerStillHasOne()
    {
        // ex: app data cleared while a delivery was waiting on the server
        var env = new TransferTestEnv();
        env.Server.OpenSessions[FakeRemoteServer.SAVE_ID] = Original.ToArray();
        env.Server.Commit(New);

        var entry = await env.NewCoordinator().StartUpload(DevicePath, Ct);

        Assert.Equal(DeviceTransferState.ACKED, entry.State);
        Assert.Equal(New, env.DeviceBytes);
        Assert.Equal(RemoteTransferState.ACKED, Assert.Single(env.Server.Deliveries).State);
    }

    // --- commit

    [Fact]
    public async Task Commit_WithAnswerLost_ShouldStillDeliver_AfterRestart()
    {
        var env = new TransferTestEnv();
        var coordinator = await UploadAndCommit(env);
        // the app is killed before it learns the result of the commit
        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, env.OnlyEntry.State);

        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
        _ = coordinator;
    }

    [Fact]
    public async Task Commit_ThatDeliversNothingForThisSave_ShouldGoBackToWaiting()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(DevicePath, Ct);
        await coordinator.BeginCommit(Ct);

        // the commit worked, but changed nothing on this save: no delivery
        await coordinator.EndCommit(responseReceived: true, Ct);

        Assert.Equal(DeviceTransferState.UPLOADED, env.OnlyEntry.State);
        Assert.Equal(Original, env.DeviceBytes);
    }

    [Fact]
    public async Task Commit_WithUnknownResult_ShouldStayMaybeCommitted_NotGoBackToWaiting()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(DevicePath, Ct);
        await coordinator.BeginCommit(Ct);

        await coordinator.EndCommit(responseReceived: false, Ct);

        Assert.Equal(DeviceTransferState.COMMIT_REQUESTED, env.OnlyEntry.State);

        // the commit did go through on the server
        env.Server.Commit(New);
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
    }

    [Fact]
    public async Task BeginCommit_ShouldOnlyConcernSavesWaitingForEdits()
    {
        var env = new TransferTestEnv();
        env.Server.SaveIdTakenByAnotherSource = true;
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(DevicePath, Ct);    // closed

        await coordinator.BeginCommit(Ct);

        Assert.Equal(DeviceTransferState.CLOSED, env.OnlyEntry.State);
    }

    // --- closing

    [Fact]
    public async Task CloseSession_ShouldEndAWaitingTransfer_AndCloseItOnTheServer()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();
        var entry = await coordinator.StartUpload(DevicePath, Ct);

        await coordinator.CloseSession(entry.LocalId, Ct);

        Assert.Equal(DeviceTransferState.CLOSED, env.OnlyEntry.State);
        Assert.Empty(env.Server.OpenSessions);
        Assert.Equal(Original, env.DeviceBytes);
    }

    [Fact]
    public async Task CloseSession_ShouldDeliverInstead_WhenACommitDidCreateADelivery()
    {
        // closing would forget the delivery: Pokémon moved to the bank would be lost from the save
        var env = new TransferTestEnv();
        var coordinator = await UploadAndCommit(env);

        await coordinator.CloseSession(env.OnlyEntry.LocalId, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
        Assert.DoesNotContain("close", env.Server.Calls);
    }

    [Fact]
    public async Task CloseSession_ShouldRefuse_WhenTheDeliveryIsUnderWay()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (_, op) => op == "download" ? FaultKind.Before : FaultKind.None;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.COMMITTED_ON_SERVER, env.OnlyEntry.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CloseSession(env.OnlyEntry.LocalId, Ct));
    }

    // --- download

    [Fact]
    public async Task Download_ShouldNeverAcceptCorruptedContent()
    {
        var env = new TransferTestEnv();
        var corrupt = true;
        env.Server.CorruptDownload = bytes => corrupt ? [.. bytes.Select(b => (byte)(b ^ 0xFF))] : bytes;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.COMMITTED_ON_SERVER, entry.State);
        Assert.Contains("hash", entry.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(env.Journal.HasBlob(entry.LocalId, TransferBlob.New));
        Assert.Equal(Original, env.DeviceBytes);

        corrupt = false;
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
    }

    [Fact]
    public async Task Download_ShouldNotBeRepeated_WhenAlreadyStoredAndVerified()
    {
        var env = new TransferTestEnv();
        // after the download is stored, the write is the next step: make it fail the first time
        env.Device.WriteFaultAt = _ => WriteFault.FailKeepingOld;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);
        var downloads = env.Server.Calls.Count(c => c == "download");

        env.Device.WriteFaultAt = null;
        await env.NewCoordinator().ResolveConflict(env.OnlyEntry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct);

        Assert.Equal(downloads, env.Server.Calls.Count(c => c == "download"));
        Assert.Equal(New, env.DeviceBytes);
    }

    [Fact]
    public async Task Delivery_UnknownToTheServer_ShouldBeAConflict_NeverTouchingTheDevice()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (i, op) => op == "download" && env.Server.Calls.Count(c => c == "download") == 1
            ? FaultKind.Before : FaultKind.None;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.COMMITTED_ON_SERVER, env.OnlyEntry.State);

        // ex: the server bank was restored from an older backup
        env.Server.Deliveries.Clear();
        await env.NewCoordinator().Resume(Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.CONFLICT, entry.State);
        Assert.Equal(DeviceConflictReason.DELIVERY_GONE, entry.ConflictReason);
        Assert.Equal(Original, env.DeviceBytes);
        Assert.Equal(0, env.Device.WriteCalls);

        // there is nothing to write or cancel, only to forget
        var c = env.NewCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.ResolveConflict(entry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.ResolveConflict(entry.LocalId, TransferConflictResolution.ABORT_ON_SERVER, Ct));
        await c.ResolveConflict(entry.LocalId, TransferConflictResolution.DISCARD, Ct);
        Assert.Equal(DeviceTransferState.CLOSED, env.OnlyEntry.State);
        Assert.Equal(Original, env.DeviceBytes);
    }

    // --- the player played since the upload

    private static async Task<TransferTestEnv> CreateDeviceChangedConflict()
    {
        var env = new TransferTestEnv();
        var coordinator = env.NewCoordinator();
        await coordinator.StartUpload(DevicePath, Ct);

        // progress made on the console/game after the upload
        env.Device.Files[DevicePath] = Played.ToArray();

        await coordinator.BeginCommit(Ct);
        env.Server.Commit(New);
        await coordinator.EndCommit(true, Ct);
        return env;
    }

    [Fact]
    public async Task DeviceSaveChangedSinceUpload_ShouldBeAConflict_AndNothingIsWritten()
    {
        var env = await CreateDeviceChangedConflict();

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.CONFLICT, entry.State);
        Assert.Equal(DeviceConflictReason.DEVICE_SAVE_CHANGED, entry.ConflictReason);
        Assert.Equal(Hash(Played), entry.DeviceHashAtConflict);
        Assert.Equal(Played, env.DeviceBytes);
        Assert.Equal(0, env.Device.WriteCalls);
        // the delivery is kept on the server, the bank and the save stay consistent
        Assert.Equal(RemoteTransferState.READY, Assert.Single(env.Server.Deliveries).State);

        // and stays so, however many times it is resumed
        await env.NewCoordinator().Resume(Ct);
        await env.NewCoordinator().Resume(Ct);
        Assert.Equal(Played, env.DeviceBytes);
        Assert.Equal(0, env.Device.WriteCalls);
    }

    [Fact]
    public async Task Conflict_Abort_ShouldCancelOnTheServer_AndLeaveTheDeviceAlone()
    {
        var env = await CreateDeviceChangedConflict();

        await env.NewCoordinator().ResolveConflict(env.OnlyEntry.LocalId, TransferConflictResolution.ABORT_ON_SERVER, Ct);

        Assert.Equal(DeviceTransferState.CLOSED, env.OnlyEntry.State);
        Assert.Empty(env.Server.Deliveries);
        Assert.Empty(env.Server.OpenSessions);
        Assert.Equal(Played, env.DeviceBytes);
    }

    [Fact]
    public async Task Conflict_Abort_WhenRefusedByServer_ShouldStayInConflict()
    {
        var env = await CreateDeviceChangedConflict();
        env.Server.AbortAllowed = false;

        var ex = await Assert.ThrowsAsync<RemoteApiException>(
            () => env.NewCoordinator().ResolveConflict(env.OnlyEntry.LocalId, TransferConflictResolution.ABORT_ON_SERVER, Ct)
        );

        Assert.Equal(ConflictException.ABORT_NOT_POSSIBLE, ex.Code);
        Assert.Equal(DeviceTransferState.CONFLICT, env.OnlyEntry.State);
        Assert.Equal(Played, env.DeviceBytes);
        Assert.Single(env.Server.Deliveries);
    }

    [Fact]
    public async Task Conflict_Overwrite_ShouldWriteTheDelivery_AfterKeepingWhatWasPlayed()
    {
        var env = await CreateDeviceChangedConflict();
        var localId = env.OnlyEntry.LocalId;

        await env.NewCoordinator().ResolveConflict(localId, TransferConflictResolution.OVERWRITE_DEVICE, Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.ACKED, entry.State);
        Assert.Equal(New, env.DeviceBytes);
        Assert.Equal(Played, await env.Journal.ReadBlob(localId, TransferBlob.DeviceBeforeWrite));
        Assert.Null(entry.ConflictReason);
    }

    [Fact]
    public async Task ResolveConflict_ShouldRefuseATransferWhichIsNotInConflict()
    {
        var env = new TransferTestEnv();
        var entry = await env.NewCoordinator().StartUpload(DevicePath, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.NewCoordinator().ResolveConflict(entry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct)
        );
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => env.NewCoordinator().ResolveConflict("unknown", TransferConflictResolution.DISCARD, Ct)
        );
    }

    [Fact]
    public async Task DeviceSaveMissing_ShouldBeAConflict_AndOverwriteCreatesIt()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (_, op) => op == "download" ? FaultKind.Before : FaultKind.None;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);
        env.Server.FaultAt = null;

        env.Device.Files.Clear();
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceConflictReason.DEVICE_SAVE_MISSING, env.OnlyEntry.ConflictReason);

        await env.NewCoordinator().ResolveConflict(env.OnlyEntry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
    }

    // --- writing

    [Theory]
    [InlineData(WriteFault.FailKeepingOld)]
    [InlineData(WriteFault.TruncateThenFail)]
    public async Task Write_ShouldBeRetried_WhenItFailsOnce(WriteFault fault)
    {
        var env = new TransferTestEnv();
        env.Device.WriteFaultAt = i => i == 0 ? fault : WriteFault.None;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
        Assert.Equal(2, env.Device.WriteCalls);
    }

    [Fact]
    public async Task Write_ThatSilentlyStoresWrongContent_ShouldBeNoticedByReadBack_AndRetried()
    {
        var env = new TransferTestEnv();
        env.Device.WriteFaultAt = i => i == 0 ? WriteFault.SilentlyCorrupt : WriteFault.None;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
        Assert.Equal(2, env.Device.WriteCalls);
    }

    [Fact]
    public async Task Write_ThatAlwaysStoresWrongContent_ShouldNeverBeAcknowledged_AndTheOldSaveIsPutBack()
    {
        var env = new TransferTestEnv();
        env.Device.WriteFaultAt = i => i < 3 ? WriteFault.SilentlyCorrupt : WriteFault.None;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.CONFLICT, entry.State);
        Assert.Equal(DeviceConflictReason.WRITE_FAILED, entry.ConflictReason);
        Assert.Equal(Original, env.DeviceBytes);
        // the server must not be told the delivery was applied
        Assert.DoesNotContain("ack", env.Server.Calls);
        Assert.Equal(RemoteTransferState.READY, Assert.Single(env.Server.Deliveries).State);
    }

    [Fact]
    public async Task Write_ThatKeepsFailing_ShouldPutTheOldSaveBack_AndAskTheUser()
    {
        var env = new TransferTestEnv();
        // the 3 attempts fail leaving a truncated file, the restore works
        env.Device.WriteFaultAt = i => i < 3 ? WriteFault.TruncateThenFail : WriteFault.None;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.CONFLICT, entry.State);
        Assert.Equal(DeviceConflictReason.WRITE_FAILED, entry.ConflictReason);
        Assert.Equal(Original, env.DeviceBytes);
        // the server keeps the delivery: nothing was acknowledged
        Assert.Equal(RemoteTransferState.READY, Assert.Single(env.Server.Deliveries).State);

        // when the problem is gone, writing can be retried
        env.Device.WriteFaultAt = null;
        await env.NewCoordinator().ResolveConflict(entry.LocalId, TransferConflictResolution.OVERWRITE_DEVICE, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
    }

    [Fact]
    public async Task Write_ThatKeepsFailing_WhenRestoreFailsToo_ShouldKeepThePreviousSaveInTheJournal()
    {
        var env = new TransferTestEnv();
        env.Device.WriteFaultAt = _ => WriteFault.TruncateThenFail;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.CONFLICT, entry.State);
        Assert.NotNull(entry.Warning);
        // the device file is damaged, but nothing is lost: both versions are in the journal
        Assert.NotEqual(Original, env.DeviceBytes);
        Assert.Equal(Original, await env.Journal.ReadBlob(entry.LocalId, TransferBlob.DeviceBeforeWrite));
        Assert.Equal(New, await env.Journal.ReadBlob(entry.LocalId, TransferBlob.New));
    }

    [Fact]
    public async Task Write_InterruptedByAKill_ShouldBeCompletedAtNextStart()
    {
        var env = new TransferTestEnv();
        using var cts = new CancellationTokenSource();
        env.Device.CancelOnFault = cts;
        env.Device.WriteFaultAt = i => i == 0 ? WriteFault.TruncateThenCancel : WriteFault.None;
        var coordinator = await UploadAndCommit(env);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.Resume(cts.Token));

        // the app died with a half written save
        Assert.Equal(DeviceTransferState.WRITING, env.OnlyEntry.State);
        Assert.Equal(Original.Length / 2, env.DeviceBytes.Length);
        Assert.Equal(Original, await env.Journal.ReadBlob(env.OnlyEntry.LocalId, TransferBlob.DeviceBeforeWrite));

        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);
        // the previous save is the real one, not the half written file
        Assert.Equal(Original, await env.Journal.ReadBlob(env.OnlyEntry.LocalId, TransferBlob.DeviceBeforeWrite));
    }

    [Fact]
    public async Task Write_ThatWasCompletedBeforeAKill_ShouldNotBeWrittenAgain()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (_, op) => op == "ack" ? FaultKind.Before : FaultKind.None;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);
        Assert.Equal(DeviceTransferState.WRITTEN, env.OnlyEntry.State);
        var writes = env.Device.WriteCalls;

        env.Server.FaultAt = null;
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(writes, env.Device.WriteCalls);
        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
    }

    // --- ack

    [Theory]
    [InlineData(FaultKind.Before)]
    [InlineData(FaultKind.After)]
    public async Task Ack_Lost_ShouldBeSentAgain_WithoutTouchingTheDeviceSave(FaultKind fault)
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (_, op) => op == "ack" ? fault : FaultKind.None;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        Assert.Equal(DeviceTransferState.WRITTEN, env.OnlyEntry.State);
        Assert.Equal(New, env.DeviceBytes);

        env.Server.FaultAt = null;
        await env.NewCoordinator().Resume(Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.Equal(RemoteTransferState.ACKED, Assert.Single(env.Server.Deliveries).State);
        Assert.Equal(1, env.Device.WriteCalls);
    }

    [Fact]
    public async Task Ack_ForADeliveryTheServerForgot_ShouldFinish_WithAWarning_AndKeepContents()
    {
        var env = new TransferTestEnv();
        env.Server.FaultAt = (_, op) => op == "ack" ? FaultKind.Before : FaultKind.None;
        var coordinator = await UploadAndCommit(env);
        await coordinator.EndCommit(true, Ct);

        env.Server.FaultAt = null;
        env.Server.Deliveries.Clear();
        await env.NewCoordinator().Resume(Ct);

        var entry = env.OnlyEntry;
        Assert.Equal(DeviceTransferState.ACKED, entry.State);
        Assert.NotNull(entry.Warning);
        Assert.Equal(New, env.DeviceBytes);
        Assert.NotNull(await env.Journal.ReadBlob(entry.LocalId, TransferBlob.New));
    }

    // --- local reload

    [Fact]
    public async Task LocalReload_WhichCannotBeDoneNow_ShouldBeRetriedAtNextResume()
    {
        var env = new TransferTestEnv();
        env.Refresher.Result = false;
        var coordinator = await UploadAndCommit(env);

        await coordinator.EndCommit(true, Ct);

        Assert.Equal(DeviceTransferState.ACKED, env.OnlyEntry.State);
        Assert.True(env.OnlyEntry.LocalReloadPending);

        env.Refresher.Result = true;
        await env.NewCoordinator().Resume(Ct);

        Assert.False(env.OnlyEntry.LocalReloadPending);
        Assert.Equal(2, env.Refresher.Calls);

        await env.NewCoordinator().Resume(Ct);
        Assert.Equal(2, env.Refresher.Calls);
    }
}
