using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKHeX.Core;
using PKVault.Core;

/// <summary>
/// The copy of Pokémon from a save to a bank, with a real database & real loaders.
/// The point of the action is that the save is left exactly as it was: that is what most tests check.
/// </summary>
public class CopyPkmToBankActionTests : IAsyncLifetime
{
    private const string BANK_ID = "1";
    private const string BOX_ID = "1";

    private readonly string dbPath;
    private readonly SessionDbContext db;
    private readonly PkmVariantLoader pkmVariantLoader;
    private readonly BoxLoader boxLoader;
    private readonly BankLoader bankLoader;
    private readonly MainCreateBoxAction mainCreateBoxAction;
    private readonly StaticDataService staticDataService;
    private readonly IServiceProvider sp;

    private readonly SaveWrapper save;
    private SavePkmLoader savePkmLoader = null!;
    private readonly Mock<ISavesLoadersService> savesLoadersService = new();

    private StaticEvolvesData evolves = [];

    private class TestCopyAction(
        IServiceProvider sp,
        IBoxLoader boxLoader, IBankLoader bankLoader, IPkmVariantLoader pkmVariantLoader, ISavesLoadersService savesLoadersService,
        MainCreateBoxAction mainCreateBoxAction, StaticDataService staticDataService
    ) : CopyPkmToBankAction(sp, boxLoader, bankLoader, pkmVariantLoader, savesLoadersService, mainCreateBoxAction, staticDataService)
    {
        public List<ushort> DexEnabled { get; } = [];

        // the real one needs all the services of the app
        protected override Task EnableDex(PkmSaveDTO savePkm)
        {
            DexEnabled.Add(savePkm.Species);
            return Task.CompletedTask;
        }
    }

    public CopyPkmToBankActionTests()
    {
        Program.Initialize();

        dbPath = $"db-CopyPkmToBankActionTests-{Guid.NewGuid()}.db";

        var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>(), Directory.GetCurrentDirectory());
        var fileIOService = new FileIOService(mockFileSystem);
        fileIOService.Matcher.GetAllPaths = (rootDir, globs) => [.. mockFileSystem.AllFiles];

        var sessionService = new Mock<ISessionServiceMinimal>();
        sessionService.Setup(s => s.SessionDbPath).Returns(dbPath);

        db = new(sessionService.Object, new Mock<DbSeedingService>(fileIOService).Object);

        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetSettings()).Returns(new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "app", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "mock-db", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "mock-storage", BACKUP_PATH: "mock-bkp",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        ));

        staticDataService = new(mockSettings.Object);

        var pkmFileLoader = new PkmFileLoader(fileIOService, sessionService.Object, mockSettings.Object, db);
        pkmVariantLoader = new PkmVariantLoader(fileIOService, sessionService.Object, mockSettings.Object, pkmFileLoader, db, staticDataService);

        sp = new ServiceCollection()
            .AddScoped((_) => new Mock<IInventoryLoader>().Object)
            .BuildServiceProvider();
        boxLoader = new BoxLoader(sessionService.Object, db, pkmVariantLoader, sp);
        bankLoader = new BankLoader(sessionService.Object, db, boxLoader);
        mainCreateBoxAction = new MainCreateBoxAction(boxLoader);

        // Fire Red / Leaf Green: the save of the games which count the trades in their records
        save = new SaveWrapper(BlankSaveFile.Get(GameVersion.FR));

    }

    public async ValueTask InitializeAsync()
    {
        evolves = await StaticEvolvesLoader.LoadData();

        var convertService = new Mock<IPkmConvertService>();
        convertService.Setup(x => x
            .ConvertTo(It.IsAny<ImmutablePKM>(), It.IsAny<Type>(), It.IsAny<ConvertContext?>())
        ).Returns((ImmutablePKM pkm, Type _, ConvertContext? __) => pkm);
        savePkmLoader = new SavePkmLoader(convertService.Object, language: "en", evolves, save);

        savesLoadersService.Setup(x => x.GetLoadersRequired(save.Id)).Returns(new SaveLoadersRecord(
            save, new Mock<ISaveBoxLoader>().Object, savePkmLoader, new Mock<ISaveInventoryLoader>().Object, []
        ));

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await AddBank(BANK_ID, isExternal: false);
        await AddBox(BOX_ID, BANK_ID, slotCount: 30, order: 0);
    }

    public async ValueTask DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        GC.SuppressFinalize(this);
    }

    // --- helpers

    private async Task AddBank(string id, bool isExternal)
    {
        db.Banks.Add(new()
        {
            Id = id,
            IdInt = int.Parse(id),
            IsDefault = id == BANK_ID,
            IsExternal = isExternal,
            Name = $"Bank {id}",
            Order = int.Parse(id),
            View = new([], []),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddBox(string id, string bankId, int slotCount, int order)
    {
        await boxLoader.AddEntity(new BoxEntity
        {
            Id = id,
            IdInt = int.Parse(id),
            Name = $"Box {id}",
            Type = BoxType.Box,
            SlotCount = slotCount,
            Order = order,
            BankId = bankId,
        });
    }

    private ImmutablePKM CreatePkm(ushort species, uint pid, bool isEgg = false)
    {
        var pk = new PK3
        {
            Species = species,
            PID = pid,
            TID16 = 54321,
            Version = GameVersion.FR,
            Nickname = $"PKM{species}",
            IsEgg = isEgg,
        };
        pk.RefreshChecksum();
        return new ImmutablePKM(pk);
    }

    /// <summary>Puts a Pokémon in the save and returns its id there.</summary>
    private string PutInSave(ushort species, uint pid, int box, int slot, bool isEgg = false)
    {
        var dto = savePkmLoader.CreateDTO(save, CreatePkm(species, pid, isEgg), box, slot);
        savePkmLoader.WriteDto(dto);
        return dto.Id;
    }

    // a blank save cannot be written to bytes: its state is read instead (the real bytes are checked by the end to end tests)
    private string SaveBytes()
    {
        var frlg = (SAV3FRLG)save.GetSave();
        var records = string.Join(",", Enumerable.Range(0, 40).Select(frlg.GetRecord));
        var pkms = string.Join(";", save.GetAllPKM().Select(p => Convert.ToHexString(p.GetMutablePkm().Data)));
        return $"{records}|{pkms}|{save.GetPartyData().Count}";
    }

    private TestCopyAction CreateAction() => new(
        sp, boxLoader, bankLoader, pkmVariantLoader, savesLoadersService.Object, mainCreateBoxAction, staticDataService
    );

    private static CopyPkmToBankActionInput Input(uint saveId, string[] pkmIds, string bankId = BANK_ID) => new(pkmIds, saveId, bankId);

    private Task<List<PkmVariantEntity>> VariantsInBank()
        => VariantsInBoxes(boxLoader.GetEntitiesByBank(BANK_ID).ContinueWith(t => t.Result.Keys.ToHashSet()));

    private async Task<List<PkmVariantEntity>> VariantsInBoxes(Task<HashSet<string>> boxIds)
    {
        var ids = await boxIds;
        return [.. (await pkmVariantLoader.GetAllEntities()).Values.Where(v => ids.Contains(v.BoxId))];
    }

    private Task<DataActionPayload> Run(TestCopyAction action, CopyPkmToBankActionInput input)
        => action.ExecuteWithPayload(input, new DataUpdateFlags());

    // --- what it does

    [Fact]
    public async Task Copy_ShouldCreateUnlinkedVariants_InTheFirstFreeSlots_WithTheirHash()
    {
        var id1 = PutInSave(25, 111, box: 0, slot: 0);
        var id2 = PutInSave(150, 222, box: 0, slot: 1);

        await Run(CreateAction(), Input(save.Id, [id1, id2]));

        var variants = (await VariantsInBank()).OrderBy(v => v.BoxSlot).ToList();
        Assert.Equal(2, variants.Count);
        Assert.Equal([0, 1], variants.Select(v => v.BoxSlot));
        Assert.All(variants, v =>
        {
            Assert.Equal(BOX_ID, v.BoxId);
            Assert.Null(v.AttachedSaveId);
            Assert.Null(v.AttachedSavePkmIdBase);
            Assert.True(v.IsMain);
            Assert.False(v.IsExternal);
        });
        Assert.Equal(25, variants[0].Species);
        Assert.Equal(150, variants[1].Species);
        Assert.Equal(savePkmLoader.GetDto(id1)!.Pkm.GetPKMIdBase(evolves), variants[0].Hash);
    }

    [Fact]
    public async Task Copy_ShouldSkipSlotsAlreadyTaken()
    {
        var first = PutInSave(25, 111, 0, 0);
        var second = PutInSave(150, 222, 0, 1);
        await Run(CreateAction(), Input(save.Id, [first]));

        await Run(CreateAction(), Input(save.Id, [second]));

        Assert.Equal([0, 1], (await VariantsInBank()).Select(v => v.BoxSlot).Order());
    }

    [Fact]
    public async Task Copy_ShouldLeaveTheSaveExactlyAsItWas_ThePokemonStayInIt()
    {
        var id1 = PutInSave(25, 111, 0, 0);
        var id2 = PutInSave(150, 222, 0, 1);
        savePkmLoader.HasWritten = false;    // as after the save was loaded
        var bytesBefore = SaveBytes();
        var countBefore = savePkmLoader.GetAllDtos().Count;

        await Run(CreateAction(), Input(save.Id, [id1, id2]));

        // nothing is marked as written, so nothing would be written on disk or delivered to a device
        Assert.False(savePkmLoader.HasWritten);
        Assert.Equal(bytesBefore, SaveBytes());
        Assert.Equal(countBefore, savePkmLoader.GetAllDtos().Count);
        Assert.NotNull(savePkmLoader.GetDto(id1));
        Assert.NotNull(savePkmLoader.GetDto(id2));
    }

    [Fact]
    public void Control_TheTradeRecordCounter_WouldHaveChangedTheSave()
    {
        // what makes the previous test able to fail: this is what an "attached" move does to the in-memory save
        var bytesBefore = SaveBytes();

        MovePkmAction.IncrementSaveTradeRecord(save);

        Assert.NotEqual(bytesBefore, SaveBytes());
    }

    [Fact]
    public async Task Copy_ShouldEnableTheDexForEachPokemon()
    {
        var id1 = PutInSave(25, 111, 0, 0);
        var id2 = PutInSave(150, 222, 0, 1);
        var action = CreateAction();

        await Run(action, Input(save.Id, [id1, id2]));

        Assert.Equal<ushort>([25, 150], action.DexEnabled);
    }

    [Fact]
    public async Task Copy_ShouldDescribeTheFirstPokemon_ForTheActionsList()
    {
        var id = PutInSave(25, 111, 0, 0);

        var payload = await Run(CreateAction(), Input(save.Id, [id]));

        Assert.Equal(DataActionType.COPY_PKM, payload.type);
        Assert.Equal("PKM25", payload.parameters[0]);
        Assert.Equal(save.Version, payload.parameters[1]);
        Assert.Equal("Box 1", payload.parameters[2]);
        Assert.Equal(0, payload.parameters[3]);
        Assert.Equal<ushort>(25, Assert.IsType<ushort>(payload.parameters[4]));
    }

    [Fact]
    public async Task Copy_ShouldCreateABox_WhenTheBankIsFull()
    {
        // a bank of one box of 2 slots, full
        await AddBank("3", isExternal: false);
        await AddBox("30", "3", slotCount: 2, order: 5);
        var smallBox = (await boxLoader.GetAllDtos()).First(b => b.Id == "30");
        for (var slot = 0; slot < 2; slot++)
        {
            await pkmVariantLoader.AddEntity(new(
                Id: null, Box: smallBox, BoxSlot: slot, IsMain: true, IsExternal: false,
                AttachedSaveId: null, AttachedSavePkmIdBase: null,
                Context: EntityContext.Gen3, Generation: 3, Pkm: CreatePkm(1, (uint)(900 + slot))
            ));
        }
        var id = PutInSave(25, 111, 0, 0);

        await Run(CreateAction(), Input(save.Id, [id], bankId: "3"));

        var boxes = await boxLoader.GetEntitiesByBank("3");
        Assert.Equal(2, boxes.Count);
        var newBox = boxes.Values.Single(b => b.Id != "30");
        Assert.Equal(30, newBox.SlotCount);
        var copy = (await pkmVariantLoader.GetAllEntities()).Values.Single(v => v.BoxId == newBox.Id);
        Assert.Equal(25, copy.Species);
        Assert.Equal(0, copy.BoxSlot);
    }

    [Fact]
    public async Task Copy_PlayedAgainAfterAnUndo_ShouldGiveTheSameVariantIds()
    {
        var id = PutInSave(25, 111, 0, 0);
        var input = Input(save.Id, [id]);

        await Run(CreateAction(), input);
        var firstId = (await VariantsInBank()).Single().Id;
        Assert.Contains(firstId, input.CreatedVariantIds.Values);

        // the session is replayed without it, then with it again
        await pkmVariantLoader.DeleteEntityDBOnly((await VariantsInBank()).Single());
        await Run(CreateAction(), input);

        Assert.Equal(firstId, (await VariantsInBank()).Single().Id);
    }

    // --- what it refuses

    [Fact]
    public async Task Copy_ShouldRefuseAPokemonAlreadyInTheBank_AndAddNothing()
    {
        var id = PutInSave(25, 111, 0, 0);
        var other = PutInSave(150, 222, 0, 1);
        await Run(CreateAction(), Input(save.Id, [id]));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, [other, id])));

        Assert.Contains("PKM25 is already in the bank", ex.Message);
        // all or nothing: the other one was not copied either
        Assert.Single(await VariantsInBank());
    }

    [Fact]
    public async Task Copy_ShouldRefuseTheSamePokemonTwiceInTheSelection()
    {
        // two identical Pokémon in the save
        var id1 = PutInSave(25, 111, 0, 0);
        var id2 = PutInSave(25, 111, 0, 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, [id1, id2])));

        Assert.Contains("selected twice", ex.Message);
        Assert.Empty(await VariantsInBank());
    }

    [Fact]
    public async Task Copy_ShouldRefuseAnEgg_AndCreateNothing()
    {
        var egg = PutInSave(25, 111, 0, 0, isEgg: true);
        var normal = PutInSave(150, 222, 0, 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, [normal, egg])));

        Assert.Contains("cannot be copied", ex.Message);
        Assert.Empty(await VariantsInBank());
        // not even a box was created for it
        Assert.Single(await boxLoader.GetEntitiesByBank(BANK_ID));
    }

    [Fact]
    public async Task Copy_ShouldAcceptAPokemonOfTheParty_AndLeaveTheTeamAsItWas()
    {
        // like the move, which accepts the party: nothing is removed from it, nor flushed
        var dto = savePkmLoader.CreateDTO(save, CreatePkm(25, 111), (int)BoxType.Party, 0);
        savePkmLoader.WriteDto(dto);
        savePkmLoader.HasWritten = false;
        var bytesBefore = SaveBytes();

        await Run(CreateAction(), Input(save.Id, [dto.Id]));

        Assert.Single(await VariantsInBank());
        Assert.False(savePkmLoader.HasWritten);
        Assert.Equal(bytesBefore, SaveBytes());
        Assert.NotNull(savePkmLoader.GetDto(dto.Id));
    }

    [Fact]
    public async Task Copy_ShouldRefuseUnknownPokemon_EmptySelection_AndExternalBank()
    {
        var id = PutInSave(25, 111, 0, 0);
        await AddBank("2", isExternal: true);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, ["nope"])));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(CreateAction(), Input(save.Id, [id], bankId: "2")));

        Assert.Empty(await VariantsInBank());
    }

    // --- the slots, shared with the move

    [Fact]
    public async Task BankSlots_ShouldGiveTheFreeSlotsInOrder_AndNeverTwice()
    {
        await AddBox("2", BANK_ID, slotCount: 2, order: 1);
        var box1 = (await boxLoader.GetAllDtos()).First(b => b.Id == BOX_ID);
        // slots 0 and 2 of the first box are taken
        foreach (var slot in new[] { 0, 2 })
        {
            await pkmVariantLoader.AddEntity(new(
                Id: null, Box: box1, BoxSlot: slot, IsMain: true, IsExternal: false,
                AttachedSaveId: null, AttachedSavePkmIdBase: null,
                Context: EntityContext.Gen3, Generation: 3, Pkm: CreatePkm(1, (uint)(700 + slot))
            ));
        }

        var slots = await BankSlots.Allocate(boxLoader, pkmVariantLoader, mainCreateBoxAction, BANK_ID, count: 3);
        var taken = Enumerable.Range(0, 3).Select(_ => slots.Take()).Select(t => (t.Box.Id, t.Slot)).ToList();

        Assert.Equal([(BOX_ID, 1), (BOX_ID, 3), (BOX_ID, 4)], taken);
        Assert.Equal(taken.Count, taken.Distinct().Count());
        Assert.Single(await boxLoader.GetEntitiesByBank(BANK_ID), b => b.Key == BOX_ID);
    }

    [Fact]
    public async Task BankSlots_ShouldCreateABox_OnlyWhenThereAreNotEnoughSlots()
    {
        var enough = await BankSlots.Allocate(boxLoader, pkmVariantLoader, mainCreateBoxAction, BANK_ID, count: 30);
        Assert.Single(await boxLoader.GetEntitiesByBank(BANK_ID));
        _ = enough;

        var more = await BankSlots.Allocate(boxLoader, pkmVariantLoader, mainCreateBoxAction, BANK_ID, count: 31);

        Assert.Equal(2, (await boxLoader.GetEntitiesByBank(BANK_ID)).Count);
        // 30 slots in the first box, then the 31st in the new one
        for (var i = 0; i < 30; i++) Assert.Equal(BOX_ID, more.Take().Box.Id);
        Assert.NotEqual(BOX_ID, more.Take().Box.Id);
    }
}
