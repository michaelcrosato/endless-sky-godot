using System;
using System.Collections.Generic;
using System.Linq;

namespace GdCcT;

/// <summary>
/// Stackable item bag. Deliberately a plain C# class with no Godot base type:
/// that keeps its tests runnable on the bare .NET host, with no engine to boot.
/// <see cref="InventoryNode"/> is the thin bridge that exposes it to the scene tree.
/// </summary>
public sealed class Inventory
{
    private readonly Dictionary<string, int> _slots = new();

    /// <summary>Distinct item kinds currently held.</summary>
    public int SlotCount => _slots.Count;

    /// <summary>Adds <paramref name="quantity"/> of an item. Blank names and non-positive amounts are ignored.</summary>
    public void Add(string item, int quantity = 1)
    {
        if (string.IsNullOrWhiteSpace(item) || quantity <= 0)
            return;

        _slots[item] = CountOf(item) + quantity;
    }

    /// <summary>Removes up to <paramref name="quantity"/> and returns how many were actually taken.</summary>
    public int Remove(string item, int quantity = 1)
    {
        if (quantity <= 0 || item is null || !_slots.TryGetValue(item, out int held))
            return 0;

        int taken = Math.Min(quantity, held);
        if (held == taken)
            _slots.Remove(item);          // Empty stacks must not linger as zero-count slots.
        else
            _slots[item] = held - taken;

        return taken;
    }

    public int CountOf(string item) =>
        item is not null && _slots.TryGetValue(item, out int held) ? held : 0;

    public bool Has(string item, int quantity = 1) => CountOf(item) >= quantity;

    /// <summary>Item names ordered alphabetically, for stable display and assertions.</summary>
    public string[] Items() => _slots.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
}
