namespace GdCcT.Tests;

using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>Pure-logic tests: these run on the plain .NET host, no Godot process.</summary>
[TestSuite]
public class InventoryTest
{
    [TestCase]
    public void AddAccumulatesIntoOneStack()
    {
        var bag = new Inventory();
        bag.Add("potion", 2);
        bag.Add("potion", 3);

        AssertInt(bag.CountOf("potion")).IsEqual(5);
        AssertInt(bag.SlotCount).IsEqual(1);
    }

    [TestCase]
    public void AddRejectsBlankNamesAndNonPositiveQuantities()
    {
        var bag = new Inventory();
        bag.Add("", 5);
        bag.Add("   ", 5);
        bag.Add("rope", 0);
        bag.Add("rope", -4);

        AssertInt(bag.SlotCount).IsEqual(0);
    }

    [TestCase]
    public void RemoveReturnsAmountActuallyTaken()
    {
        var bag = new Inventory();
        bag.Add("arrow", 10);

        AssertInt(bag.Remove("arrow", 4)).IsEqual(4);
        AssertInt(bag.CountOf("arrow")).IsEqual(6);
        // Asking for more than is held takes only what remains.
        AssertInt(bag.Remove("arrow", 99)).IsEqual(6);
        AssertInt(bag.Remove("arrow")).IsEqual(0);
    }

    [TestCase]
    public void EmptiedStacksAreDroppedNotZeroed()
    {
        var bag = new Inventory();
        bag.Add("torch", 1);
        bag.Remove("torch", 1);

        AssertInt(bag.SlotCount).IsEqual(0);
        AssertBool(bag.Has("torch")).IsFalse();
    }

    [TestCase]
    public void UnknownItemsReadAsAbsentRatherThanThrowing()
    {
        var bag = new Inventory();

        AssertInt(bag.CountOf("ghost")).IsEqual(0);
        AssertInt(bag.Remove("ghost", 3)).IsEqual(0);
        AssertBool(bag.Has("ghost")).IsFalse();
    }

    [TestCase]
    public void ItemsAreListedAlphabetically()
    {
        var bag = new Inventory();
        bag.Add("shield");
        bag.Add("apple");
        bag.Add("map");

        AssertArray(bag.Items()).ContainsExactly("apple", "map", "shield");
    }
}
