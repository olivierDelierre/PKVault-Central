namespace PKVault.Core;

/// <summary>
/// The free slots of a bank, to place new Pokémon in. When the bank does not have enough of them,
/// a box is created.
/// Shared by the actions which put Pokémon of a save in a bank (move, copy).
/// </summary>
public class BankSlots
{
    private readonly Dictionary<string, HashSet<int>> unoccupiedSlotsByBox;
    private readonly Dictionary<string, BoxDTO> boxesById;

    private BankSlots(Dictionary<string, HashSet<int>> unoccupiedSlotsByBox, Dictionary<string, BoxDTO> boxesById)
    {
        this.unoccupiedSlotsByBox = unoccupiedSlotsByBox;
        this.boxesById = boxesById;
    }

    /// <param name="count">How many Pokémon are going to be placed.</param>
    public static async Task<BankSlots> Allocate(
        IBoxLoader boxLoader, IPkmVariantLoader pkmVariantLoader, MainCreateBoxAction mainCreateBoxAction,
        string bankId, int count
    )
    {
        var mainBoxes = (await boxLoader.GetEntitiesByBank(bankId)).Values
            .Where(box => box.Type != BoxType.Inventory)
            .OrderBy(box => box.Order).ToList();

        var boxesOccupationDict = (
            await Task.WhenAll(mainBoxes.Select(async box => (
                box.Id,
                (await pkmVariantLoader.GetEntitiesByBox(box.Id))
                    .Select(dict => dict.Key)   // boxSlot
                    .ToHashSet()
            )))
        ).ToDictionary();
        var boxesUnoccupationDict = new Dictionary<string, HashSet<int>>();

        var availableSlotCount = 0;

        foreach (var boxId in boxesOccupationDict.Keys)
        {
            var box = mainBoxes.First(box => box.Id == boxId);
            HashSet<int> unoccupiedSlots = [];
            for (int slot = 0; slot < box.SlotCount; slot++)
            {
                if (!boxesOccupationDict[boxId].Contains(slot))
                {
                    unoccupiedSlots.Add(slot);
                    availableSlotCount++;
                }
            }
            if (unoccupiedSlots.Count > 0)
            {
                boxesUnoccupationDict.Add(boxId, unoccupiedSlots);
            }
        }

        if (availableSlotCount < count)
        {
            var missingSlotCount = count - availableSlotCount;
            var boxSlotCount = new int[] { missingSlotCount, 30 }.Max();

            var box = await mainCreateBoxAction.CreateBox(new(bankId, boxSlotCount));

            HashSet<int> unoccupiedSlots = [];
            for (int slot = 0; slot < box.SlotCount; slot++)
            {
                unoccupiedSlots.Add(slot);
                availableSlotCount++;
            }
            if (unoccupiedSlots.Count > 0)
            {
                boxesUnoccupationDict.Add(box.Id, unoccupiedSlots);
            }
        }

        var boxesDict = (await boxLoader.GetAllDtos())
            .Where(p => p.Type != BoxType.Inventory)
            .ToDictionary(p => p.Id);

        return new(boxesUnoccupationDict, boxesDict);
    }

    /// <summary>The next free slot, which is not free anymore afterwards.</summary>
    public (BoxDTO Box, int Slot) Take()
    {
        var boxId = unoccupiedSlotsByBox.Keys.First();
        var boxSlot = unoccupiedSlotsByBox[boxId].First();
        unoccupiedSlotsByBox[boxId].Remove(boxSlot);
        if (unoccupiedSlotsByBox[boxId].Count == 0)
        {
            unoccupiedSlotsByBox.Remove(boxId);
        }

        boxesById.TryGetValue(boxId, out var targetBox);
        ArgumentNullException.ThrowIfNull(targetBox);

        return (targetBox, boxSlot);
    }
}
