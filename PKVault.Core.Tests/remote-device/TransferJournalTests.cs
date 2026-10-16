using System.IO.Abstractions.TestingHelpers;
using PKVault.Core;

public class TransferJournalTests
{
    private readonly MockFileSystem fileSystem = new(new Dictionary<string, MockFileData>(), Directory.GetCurrentDirectory());
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "journal");

    private TransferJournal CreateJournal() => new(fileSystem, directory, TimeProvider.System);

    private static TransferJournalEntry CreateEntry(string id, DateTime? createdAt = null) => new()
    {
        LocalId = id,
        DeviceSavePath = "/saves/main.sav",
        FileName = "main.sav",
        CreatedAt = createdAt ?? DateTime.UtcNow,
    };

    [Fact]
    public void LoadAll_ShouldBeEmpty_WhenNothingWasEverSaved()
    {
        Assert.Empty(CreateJournal().LoadAll());
    }

    [Fact]
    public async Task Save_ShouldRoundTripEveryField()
    {
        var journal = CreateJournal();
        var entry = CreateEntry("a");
        entry.State = DeviceTransferState.CONFLICT;
        entry.SaveId = 123;
        entry.OriginalHash = "OH";
        entry.TransferId = "tid";
        entry.NewHash = "NH";
        entry.ConflictReason = DeviceConflictReason.DEVICE_SAVE_CHANGED;
        entry.DeviceHashAtConflict = "DH";
        entry.LastError = "err";
        entry.Warning = "warn";
        entry.LocalReloadPending = true;

        await journal.Save(entry);
        var loaded = Assert.Single(CreateJournal().LoadAll());

        Assert.Equal("a", loaded.LocalId);
        Assert.Equal(DeviceTransferState.CONFLICT, loaded.State);
        Assert.Equal(123u, loaded.SaveId);
        Assert.Equal("OH", loaded.OriginalHash);
        Assert.Equal("tid", loaded.TransferId);
        Assert.Equal("NH", loaded.NewHash);
        Assert.Equal(DeviceConflictReason.DEVICE_SAVE_CHANGED, loaded.ConflictReason);
        Assert.Equal("DH", loaded.DeviceHashAtConflict);
        Assert.Equal("err", loaded.LastError);
        Assert.Equal("warn", loaded.Warning);
        Assert.True(loaded.LocalReloadPending);
        Assert.Equal("/saves/main.sav", loaded.DeviceSavePath);
    }

    [Fact]
    public async Task Save_ShouldStoreStatesAsReadableNames()
    {
        var entry = CreateEntry("a");
        entry.State = DeviceTransferState.COMMITTED_ON_SERVER;

        await CreateJournal().Save(entry);

        var json = fileSystem.File.ReadAllText(Path.Combine(directory, "a.json"));
        Assert.Contains("\"COMMITTED_ON_SERVER\"", json);
    }

    [Fact]
    public async Task Save_ShouldReplaceExistingEntry_AndLeaveNoTempFile()
    {
        var journal = CreateJournal();
        var entry = CreateEntry("a");
        await journal.Save(entry);

        entry.State = DeviceTransferState.UPLOADED;
        await journal.Save(entry);

        Assert.Equal(DeviceTransferState.UPLOADED, Assert.Single(journal.LoadAll()).State);
        Assert.DoesNotContain(fileSystem.AllFiles, f => f.Contains(".tmp-"));
    }

    [Fact]
    public async Task LoadAll_ShouldReturnEntriesOldestFirst()
    {
        var journal = CreateJournal();
        var now = DateTime.UtcNow;
        await journal.Save(CreateEntry("newer", now));
        await journal.Save(CreateEntry("older", now.AddMinutes(-5)));

        Assert.Equal(["older", "newer"], journal.LoadAll().Select(e => e.LocalId));
    }

    [Fact]
    public async Task LoadAll_ShouldSkipUnreadableEntry_WithoutDeletingIt()
    {
        var journal = CreateJournal();
        await journal.Save(CreateEntry("good"));
        var corruptPath = Path.Combine(directory, "corrupt.json");
        fileSystem.AddFile(corruptPath, new MockFileData("{ not json"));

        Assert.Equal("good", Assert.Single(journal.LoadAll()).LocalId);
        // it may be the only record of a delivery: left for a human
        Assert.True(fileSystem.File.Exists(corruptPath));
    }

    [Fact]
    public async Task Blobs_ShouldRoundTrip_PerKind_AndNotBeListedAsEntries()
    {
        var journal = CreateJournal();
        await journal.Save(CreateEntry("a"));

        await journal.WriteBlob("a", TransferBlob.Original, [1, 2, 3]);
        await journal.WriteBlob("a", TransferBlob.New, [4, 5]);

        Assert.Equal([1, 2, 3], await journal.ReadBlob("a", TransferBlob.Original));
        Assert.Equal([4, 5], await journal.ReadBlob("a", TransferBlob.New));
        Assert.Null(await journal.ReadBlob("a", TransferBlob.DeviceBeforeWrite));
        Assert.True(journal.HasBlob("a", TransferBlob.New));
        Assert.False(journal.HasBlob("a", TransferBlob.DeviceBeforeWrite));
        Assert.Single(journal.LoadAll());
    }

    [Fact]
    public async Task WriteBlob_ShouldReplaceContent_AtomicallyLeavingNoTempFile()
    {
        var journal = CreateJournal();

        await journal.WriteBlob("a", TransferBlob.New, [1, 1, 1, 1]);
        await journal.WriteBlob("a", TransferBlob.New, [2]);

        Assert.Equal([2], await journal.ReadBlob("a", TransferBlob.New));
        Assert.DoesNotContain(fileSystem.AllFiles, f => f.Contains(".tmp-"));
    }

    [Fact]
    public async Task Get_ShouldFindByLocalId()
    {
        var journal = CreateJournal();
        await journal.Save(CreateEntry("a"));
        await journal.Save(CreateEntry("b"));

        Assert.Equal("b", journal.Get("b")!.LocalId);
        Assert.Null(journal.Get("nope"));
    }

    [Theory]
    [InlineData(DeviceTransferState.ACKED, true)]
    [InlineData(DeviceTransferState.CLOSED, true)]
    [InlineData(DeviceTransferState.CONFLICT, false)]
    [InlineData(DeviceTransferState.WRITTEN, false)]
    [InlineData(DeviceTransferState.UPLOADED, false)]
    public void IsTerminal_ShouldOnlyBeTrue_WhenNothingIsLeftToDo(DeviceTransferState state, bool expected)
    {
        var entry = CreateEntry("a");
        entry.State = state;

        Assert.Equal(expected, entry.IsTerminal);
    }
}
