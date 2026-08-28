using Godot;

namespace GdCcT;

/// <summary>
/// Scene-tree facing wrapper around the engine-independent <see cref="Inventory"/>.
/// Everything here is a one-line forward -- keep real logic in the plain class so it
/// stays testable without a Godot runtime.
/// </summary>
[GlobalClass]
public partial class InventoryNode : RefCounted
{
    private readonly Inventory _inventory = new();

    [Signal]
    public delegate void ItemAddedEventHandler(string item, int quantity);

    public int SlotCount => _inventory.SlotCount;

    public void Add(string item, int quantity = 1)
    {
        int before = _inventory.CountOf(item);
        _inventory.Add(item, quantity);

        int gained = _inventory.CountOf(item) - before;
        if (gained > 0)
            EmitSignal(SignalName.ItemAdded, item, gained);
    }

    public int Remove(string item, int quantity = 1) => _inventory.Remove(item, quantity);

    public int CountOf(string item) => _inventory.CountOf(item);

    public bool Has(string item, int quantity = 1) => _inventory.Has(item, quantity);

    public string[] Items() => _inventory.Items();
}
