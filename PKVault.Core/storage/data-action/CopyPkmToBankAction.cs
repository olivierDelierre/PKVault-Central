namespace PKVault.Core;

public record CopyPkmToBankActionInput(string[] pkmIds, uint sourceSaveId, string bankId)
{
    public Dictionary<string, string> CreatedVariantIds = [];
};

/// <summary>
/// Copies Pokémon of a save into a bank. The Pokémon stay in the save, and the save is NOT modified at all:
/// the copies are plain bank Pokémon, with no link to the save (unlike an "attached" move).
///
/// What this action must never do, because each of these would modify the save (and so, for a save
/// received from a device, make the server deliver a rewritten save back to it):
/// remove or write a Pokémon in the save, flush its party, change its trade records, synchronize with it.
/// </summary>
public class CopyPkmToBankAction(
    IServiceProvider sp,
    IBoxLoader boxLoader, IBankLoader bankLoader, IPkmVariantLoader pkmVariantLoader, ISavesLoadersService savesLoadersService,
    MainCreateBoxAction mainCreateBoxAction, StaticDataService staticDataService
) : DataAction<CopyPkmToBankActionInput>
{
    protected override async Task<DataActionPayload> Execute(CopyPkmToBankActionInput input, DataUpdateFlags flags)
    {
        if (input.pkmIds.Length == 0)
        {
            throw new ArgumentException($"Pkm ids cannot be empty");
        }

        var bank = await bankLoader.GetEntityRequired(input.bankId);

        if (bank.IsExternal)
        {
            throw new ArgumentException($"External bank cannot be used as copy target");
        }

        var saveLoaders = savesLoadersService.GetLoadersRequired(input.sourceSaveId);

        // everything is checked before anything is created, so a refusal does not leave a new box behind
        var savePkms = input.pkmIds
            .Select(pkmId => saveLoaders.Pkms.GetDto(pkmId)
                ?? throw new ArgumentException($"Save Pkm not found, id={pkmId}"))
            .ToList();

        foreach (var savePkm in savePkms)
        {
            if (!savePkm.CanMoveToMain)
            {
                throw new ArgumentException(
                    $"{savePkm.Nickname} cannot be copied to the bank (it is an egg, a shadow Pokémon, locked, already in the party flags, or invalid)"
                );
            }
        }

        await ThrowOnDuplicates(input.bankId, savePkms);

        var slots = await BankSlots.Allocate(boxLoader, pkmVariantLoader, mainCreateBoxAction, input.bankId, savePkms.Count);

        List<DataActionPayload> payloads = [];
        foreach (var savePkm in savePkms)
        {
            var (targetBox, targetBoxSlot) = slots.Take();

            var existingSlots = await pkmVariantLoader.GetEntitiesByBox(targetBox.Id, targetBoxSlot);
            if (existingSlots.Count > 0)
            {
                throw new Exception("Pkm already present");
            }

            // same id when the action is played again after an undo
            var inputKey = $"{targetBox}.{targetBoxSlot}";
            input.CreatedVariantIds.TryGetValue(inputKey, out var createdVariantId);
            var variant = await pkmVariantLoader.AddEntity(new(
                Id: createdVariantId,
                Box: targetBox,
                BoxSlot: targetBoxSlot,
                IsMain: true,
                IsExternal: false,
                AttachedSaveId: null,
                AttachedSavePkmIdBase: null,
                Context: savePkm.Context,
                Generation: savePkm.Generation,
                Pkm: savePkm.Pkm
            ));
            input.CreatedVariantIds[inputKey] = variant.Id;

            await EnableDex(savePkm);

            payloads.Add(new(
                type: DataActionType.COPY_PKM,
                parameters: [savePkm.Nickname, saveLoaders.Save.Version, targetBox.Name, targetBoxSlot, savePkm.Species]
            ));
        }

        return payloads[0];
    }

    /// <summary>
    /// The bank Pokémon is now owned, in the bank dex. Overridable since it needs the whole services of the app.
    /// </summary>
    protected virtual async Task EnableDex(PkmSaveDTO savePkm)
    {
        await new DexMainService(sp).EnablePKM(savePkm.Pkm, savePkm.Save);
    }

    /// <summary>
    /// A Pokémon already in the bank, or twice in the selection, is refused: copying it would only make
    /// a second identical one. The same hash as the one given to the bank Pokémon is used.
    /// </summary>
    private async Task ThrowOnDuplicates(string bankId, List<PkmSaveDTO> savePkms)
    {
        var evolves = await staticDataService.GetStaticEvolves();

        var bankBoxIds = (await boxLoader.GetEntitiesByBank(bankId)).Keys.ToHashSet();
        var hashesInBank = (await pkmVariantLoader.GetAllEntities()).Values
            .Where(variant => !variant.IsExternal && bankBoxIds.Contains(variant.BoxId))
            .Select(variant => variant.Hash)
            .ToHashSet();

        HashSet<string> hashesInSelection = [];
        foreach (var savePkm in savePkms)
        {
            var hash = savePkm.Pkm.GetPKMIdBase(evolves);

            if (hashesInBank.Contains(hash))
            {
                throw new ArgumentException($"{savePkm.Nickname} is already in the bank");
            }

            if (!hashesInSelection.Add(hash))
            {
                throw new ArgumentException($"{savePkm.Nickname} is selected twice (the save has two identical Pokémon)");
            }
        }
    }
}
