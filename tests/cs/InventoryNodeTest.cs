namespace GdCcT.Tests;

using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Bridge tests. These touch Godot's object model, so each one is marked
/// [RequireGodotRuntime] and gdUnit4 runs it inside a real engine process.
/// </summary>
[TestSuite]
public class InventoryNodeTest
{
    [TestCase]
    [RequireGodotRuntime]
    public void WrapperIsAGodotRefCounted()
    {
        var bag = AutoFree(new InventoryNode())!;
        AssertString(bag.GetClass()).IsEqual("RefCounted");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void WrapperForwardsToTheLogicCore()
    {
        var bag = AutoFree(new InventoryNode())!;
        bag.Add("potion", 3);
        bag.Add("apple");

        AssertInt(bag.CountOf("potion")).IsEqual(3);
        AssertInt(bag.SlotCount).IsEqual(2);
        AssertArray(bag.Items()).ContainsExactly("apple", "potion");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddEmitsItemAddedWithTheGainedAmount()
    {
        var bag = AutoFree(new InventoryNode())!;

        AssertSignal(bag).IsSignalExists("ItemAdded");

        string? seenItem = null;
        int seenQty = 0;
        bag.ItemAdded += (item, qty) => { seenItem = item; seenQty = qty; };

        bag.Add("ember", 4);

        AssertString(seenItem).IsEqual("ember");
        AssertInt(seenQty).IsEqual(4);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RejectedAddsEmitNothing()
    {
        var bag = AutoFree(new InventoryNode())!;

        int emissions = 0;
        bag.ItemAdded += (_, _) => emissions++;

        bag.Add("", 5);
        bag.Add("rope", 0);

        AssertInt(emissions).IsEqual(0);
        AssertInt(bag.SlotCount).IsEqual(0);
    }
}
