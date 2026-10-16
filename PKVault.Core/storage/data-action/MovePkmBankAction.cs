using PKHeX.Core;

namespace PKVault.Core;

public record MovePkmBankActionInput(string[] pkmIds, uint? sourceSaveId, string bankId, bool attached)
{
    public Dictionary<string, string> CreatedVariantIds = [];
};

public class MovePkmBankAction(
    IServiceProvider sp,
    IBoxLoader boxLoader, IBankLoader bankLoader, IPkmVariantLoader pkmVariantLoader, ISavesLoadersService savesLoadersService,
    MainCreateBoxAction mainCreateBoxAction, SynchronizePkmAction synchronizePkmAction
) : DataAction<MovePkmBankActionInput>
{
    protected override async Task<DataActionPayload> Execute(MovePkmBankActionInput input, DataUpdateFlags flags)
    {
        if (input.pkmIds.Length == 0)
        {
            throw new ArgumentException($"Pkm ids cannot be empty");
        }

        var bank = await bankLoader.GetEntityRequired(input.bankId);

        if (bank.IsExternal)
        {
            throw new ArgumentException($"External bank cannot be used as move target");
        }

        var slots = await BankSlots.Allocate(boxLoader, pkmVariantLoader, mainCreateBoxAction, input.bankId, input.pkmIds.Length);

        async Task<DataActionPayload> act(string pkmId)
        {
            var (targetBox, boxSlot) = slots.Take();

            if (input.sourceSaveId == null)
            {
                return await MainToMain(input, pkmId, targetBox, boxSlot);
            }

            return await SaveToMain(input, flags, pkmId, targetBox, boxSlot);
        }

        // log.LogInformation($"ENTRIES [{moveDirection}]:\n{string.Join('\n', entries.Select(e => e.Item1 + "_" + e.Item2 + "_" + e.Item3))}");

        List<DataActionPayload> payloads = [];
        foreach (var pkmId in input.pkmIds)
        {
            payloads.Add(await act(pkmId));
        }

        return payloads[0];
    }

    private async Task<DataActionPayload> MainToMain(MovePkmBankActionInput input, string pkmVariantId, BoxDTO targetBox, int targetBoxSlot)
    {
        var baseEntity = await pkmVariantLoader.GetEntityRequired(pkmVariantId);
        var entities = await pkmVariantLoader.GetEntitiesByBox(baseEntity.BoxId, baseEntity.BoxSlot);
        var pkm = await pkmVariantLoader.GetPKM(baseEntity);

        var pkmsAlreadyPresent = (await pkmVariantLoader.GetEntitiesByBox(targetBox.Id, targetBoxSlot)).Values;
        if (pkmsAlreadyPresent.Count != 0)
        {
            throw new Exception("Pkm already present");
        }

        foreach (var entity in entities.Values)
        {
            entity.BoxId = targetBox.Id;
            entity.BoxSlot = targetBoxSlot;
            await pkmVariantLoader.UpdateEntity(entity, targetBox);
        }

        return new(
            type: DataActionType.MOVE_PKM,
            parameters: [pkm.Nickname, null, null, targetBox.Name, targetBoxSlot, input.attached, pkm.Species]
        );
    }

    private async Task<DataActionPayload> SaveToMain(MovePkmBankActionInput input, DataUpdateFlags flags, string pkmId, BoxDTO targetBox, int targetBoxSlot)
    {
        var saveLoaders = savesLoadersService.GetLoadersRequired((uint)input.sourceSaveId!);

        var savePkm = saveLoaders.Pkms.GetDto(pkmId)
            ?? throw new ArgumentException($"Save Pkm not found, id={pkmId}");

        if (input.attached)
        {
            if (savePkm.IsDuplicate)
            {
                throw new ArgumentException($"Target save already have a pkm with same ID, move attached cannot be done.");
            }
        }

        var existingSlots = await pkmVariantLoader.GetEntitiesByBox(targetBox.Id, targetBoxSlot);
        if (existingSlots.Count > 0)
        {
            throw new Exception("Pkm already present");
        }

        await SaveToMainWithoutCheckTarget(
                input, flags, (uint)input.sourceSaveId, targetBox, targetBoxSlot, savePkm
            );

        saveLoaders.Pkms.FlushParty();

        MovePkmAction.IncrementSaveTradeRecord(saveLoaders.Save);

        return new(
            type: DataActionType.MOVE_PKM,
            parameters: [savePkm.Nickname, saveLoaders.Save.Version, null, targetBox.Name, targetBoxSlot, input.attached, savePkm.Species]
        );
    }

    private async Task SaveToMainWithoutCheckTarget(
        MovePkmBankActionInput input,
        DataUpdateFlags flags,
        uint sourceSaveId, BoxDTO targetBox, int targetBoxSlot,
        PkmSaveDTO savePkm
    )
    {
        var saveLoaders = savesLoadersService.GetLoadersRequired(sourceSaveId);

        if (savePkm.Pkm.GetMutablePkm() is IShadowCapture savePkmShadow && savePkmShadow.IsShadow)
        {
            throw new ArgumentException($"Action forbidden for PkmSave shadow for id={savePkm.Id}");
        }

        if (savePkm.Pkm.IsEgg)
        {
            throw new ArgumentException($"Action forbidden for PkmSave egg for id={savePkm.Id}");
        }

        // get pkm-version
        var pkmVariantEntity = await pkmVariantLoader.GetEntityBySave(savePkm.SaveId, savePkm.IdBase);
        var mainPkmAlreadyExists = pkmVariantEntity != null;

        // create pkm-version
        var inputKey = $"{targetBox}.{targetBoxSlot}";
        input.CreatedVariantIds.TryGetValue(inputKey, out var createdVariantId);
        pkmVariantEntity ??= await pkmVariantLoader.AddEntity(new(
            Id: createdVariantId,
            Box: targetBox,
            BoxSlot: targetBoxSlot,
            IsMain: true,
            IsExternal: false,
            AttachedSaveId: input.attached ? sourceSaveId : null,
            AttachedSavePkmIdBase: input.attached ? savePkm.IdBase : null,
            Context: savePkm.Context,
            Generation: savePkm.Generation,
            Pkm: savePkm.Pkm
        ));
        input.CreatedVariantIds[inputKey] = pkmVariantEntity.Id;

        // if moved to already attached pkm, just update it
        if (mainPkmAlreadyExists && pkmVariantEntity.AttachedSaveId != null)
        {
            var entityBox = await boxLoader.GetEntity(pkmVariantEntity.BoxId);
            if (entityBox?.BankId != targetBox.BankId)
                throw new ArgumentException($"Attached pkm cannot change bank");

            var variantPkm = await pkmVariantLoader.GetPKM(pkmVariantEntity);
            if (!variantPkm.IsEnabled)
                throw new ArgumentException($"Attached variant is invalid and cannot be synchronized");

            await synchronizePkmAction.SynchronizeSaveToPkmVariant(
                new([(pkmVariantEntity.Id, pkmVariantEntity.AttachedSavePkmIdBase!)]),
                savePkmId: input.attached ? null : savePkm.Id
            );

            if (!input.attached)
            {
                pkmVariantEntity.AttachedSaveId = null;
                pkmVariantEntity.AttachedSavePkmIdBase = null;
                await pkmVariantLoader.UpdateEntity(pkmVariantEntity);
            }
        }

        if (!input.attached)
        {
            // remove pkm from save
            saveLoaders.Pkms.DeleteDto(savePkm.Id);
        }

        await new DexMainService(sp).EnablePKM(savePkm.Pkm, savePkm.Save);
    }
}
