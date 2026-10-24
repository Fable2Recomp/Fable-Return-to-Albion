using System;
using System.Collections.Generic;
using static Refii.Ui.Engine_QuickSlots;

namespace Refii.Ui;

public sealed class Engine_UiQuickSlotAssignment
{
    private readonly Func<Binding[]> _read;
    private readonly Func<Binding[], bool> _save;
    private readonly Func<Binding, Choice> _describe;
    private readonly Func<bool> _valid;
    private readonly Choice[] _slots = new Choice[Count];
    private uint _down = uint.MaxValue;
    private double _resultTime;
    public int Player { get; }
    public Choice Selected { get; }
    public bool IsOpen { get; private set; }
    public int CurrentBank { get; private set; }
    public int AssignedMask { get; private set; }
    public int ResultSlot { get; private set; } = -1;
    public string Status { get; private set; } = "Press D-pad, LT + D-pad or RT + D-pad to assign";
    public uint Revision { get; private set; }
    public IReadOnlyList<Choice> Slots => _slots;

    public Engine_UiQuickSlotAssignment(int player, Choice choice, Func<Binding[]> read,
        Func<Binding[], bool> save, Func<Binding, Choice> describe, Func<bool> valid)
    {
        Player = player;
        Selected = choice;
        _read = read;
        _save = save;
        _describe = describe;
        _valid = valid;
        IsOpen = choice.Binding.Assigned && player is 1 or 2 && valid();
        if (IsOpen) Refresh(read());
    }

    public void Cancel() => IsOpen = false;

    public void Drive(double delta, uint buttons, uint leftTrigger, uint rightTrigger, int clickedSlot = -1)
    {
        if (!IsOpen) return;
        if (!_valid()) { Cancel(); return; }
        uint pressed = buttons & ~_down;
        _down = buttons;
        if ((pressed & 0x2000) != 0 || clickedSlot == -2) { Cancel(); return; }
        if (ResultSlot >= 0)
        {
            _resultTime += Math.Max(0, delta);
            if (_resultTime >= 0.65) Cancel();
            return;
        }
        CurrentBank = rightTrigger > 30 ? 2 : leftTrigger > 30 ? 1 : 0;
        if (((pressed & 0x4000) != 0 || clickedSlot == -3) && AssignedMask != 0) { Remove(); return; }
        uint direction = buttons & 15;
        if (clickedSlot is >= 0 and < Count) Assign(clickedSlot);
        else if (direction != 0 && (direction & (direction - 1)) == 0 && (pressed & direction) != 0)
            Assign(CurrentBank * 4 + System.Numerics.BitOperations.TrailingZeroCount(direction));
    }

    private void Refresh(Binding[] bindings)
    {
        AssignedMask = MatchingSlots(Selected.Binding, bindings);
        for (int i = 0; i < Count; i++)
            _slots[i] = i < bindings.Length && bindings[i].Assigned ? _describe(bindings[i]) : new(default, i < 4 ? "Automatic" : "Empty");
        Revision++;
    }

    private void Assign(int slot)
    {
        if (!_valid()) { Cancel(); return; }
        var bindings = (Binding[])_read().Clone();
        if (bindings.Length != Count) { Fail(); return; }
        bindings[slot] = Selected.Binding;
        if (!_save(bindings)) { Fail(); return; }
        ResultSlot = slot;
        Status = "Assigned to " + Banks[slot / 4] + " - " + Directions[slot % 4];
        Refresh(bindings);
    }

    private void Remove()
    {
        if (!_valid()) { Cancel(); return; }
        var bindings = (Binding[])_read().Clone();
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i] == Selected.Binding) bindings[i] = default;
        if (!_save(bindings)) { Fail(); return; }
        Status = "Assignment removed. Press a direction to assign";
        Refresh(bindings);
    }

    private void Fail()
    {
        Status = "Could not save the quickslot. Please try again.";
        Revision++;
    }

    public static Binding FromRow(uint screen, Engine_UiExpandableMenu.Item? row)
    {
        if (row is null || row.Ghosted || row.Children.Count != 0 || row.EventType == 301) return default;
        if (screen == 17 && row.EventType == 61 && row.IdLow is >= 0 and <= 65535 && row.IdHigh is >= 0 and <= 65535)
            return new(Kind.Expression, ((uint)row.IdHigh << 16) | (uint)row.IdLow);
        string command = row.Command ?? "";
        if (screen == 15)
        {
            const string use = "Inventory:use:", equip = "Inventory:equip:";
            if (command.StartsWith(use, StringComparison.Ordinal) && uint.TryParse(command.AsSpan(use.Length), out uint item))
                return new(Kind.Item, item);
            if (command.StartsWith(equip, StringComparison.Ordinal) && uint.TryParse(command.AsSpan(equip.Length), out uint weapon))
                return new(Kind.Weapon, weapon);
        }
        const string learn = "Ability:learn:";
        if (screen == 14 && command.StartsWith(learn, StringComparison.Ordinal) &&
            uint.TryParse(command.AsSpan(learn.Length), out uint ability) && ability is >= 7 and <= 14)
            return new(Kind.Spell, ability - 7);
        return default;
    }

    public static (int Width, int Height, int X, int Y) FitIcon(int width, int height, int size = 128)
    {
        if (width <= 0 || height <= 0 || size < 2) return default;
        double scale = size * 0.84 / Math.Max(width, height);
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        return (w, h, (size - w) / 2, (size - h) / 2);
    }
}

