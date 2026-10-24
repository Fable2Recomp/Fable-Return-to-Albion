using System;
using System.Collections.Generic;

namespace Refii.Ui;

public static class Engine_QuickSlots
{
    public const int Count = 12;
    public const string EnabledKey = "enable_hotkeys";
    public const string RevisionKey = "quickslots.revision";
    public enum Kind { Automatic, Item, Expression, Weapon, Spell }
    public readonly record struct Binding(Kind Type, uint Id)
    {
        public bool Assigned => Type is >= Kind.Item and <= Kind.Spell && (Type == Kind.Spell ? Id < 8 : Id != 0);
    }
    public readonly record struct Choice(Binding Binding, string Label, string Icon = "");
    public static readonly string[] Banks = { "D-pad", "LT + D-pad", "RT + D-pad" };
    public static readonly string[] Directions = { "Up", "Down", "Left", "Right" };
    private static readonly string[] Keys = BuildKeys();

    public static int MatchingSlots(Binding binding, IReadOnlyList<Binding> bindings)
    {
        if (!binding.Assigned) return 0;
        int mask = 0;
        for (int i = 0; i < Math.Min(Count, bindings.Count); i++)
            if (bindings[i] == binding) mask |= 1 << i;
        return mask;
    }

    public static string AssignmentStatus(int mask)
    {
        mask &= (1 << Count) - 1;
        if (mask == 0) return "Not assigned";
        int first = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
        int others = System.Numerics.BitOperations.PopCount((uint)mask) - 1;
        return "Assigned: " + Banks[first / 4] + " - " + Directions[first % 4] +
            (others > 0 ? " (+" + others + ")" : "");
    }

    private static string[] BuildKeys()
    {
        var keys = new string[Count * 2];
        for (int i = 0; i < keys.Length; i++) keys[i] = "quickslots." + (i / 2) + (i % 2 == 0 ? ".low" : ".high");
        return keys;
    }

    public static Binding[] Read(Func<string, float?> read)
    {
        var result = new Binding[Count];
        for (int i = 0; i < Count; i++)
        {
            float low = read(Keys[i * 2]) ?? 0, high = read(Keys[i * 2 + 1]) ?? 0;
            if (!float.IsFinite(low) || !float.IsFinite(high) || low < 0 || low > 0xFFFFFF ||
                high < 0 || high > 0x4FF || low != MathF.Truncate(low) || high != MathF.Truncate(high)) continue;
            var value = new Binding((Kind)((uint)high >> 8), ((uint)high << 24) | (uint)low);
            if (value.Assigned) result[i] = value;
        }
        return result;
    }

    public static bool Save(IReadOnlyList<Binding> bindings, Func<string, float?> read, Func<string, float, bool> write)
    {
        if (bindings.Count != Count) return false;
        var before = new float[Keys.Length];
        for (int i = 0; i < Keys.Length; i++) before[i] = read(Keys[i]) ?? 0;
        for (int i = 0; i < Count; i++)
        {
            Binding b = bindings[i].Assigned ? bindings[i] : default;
            if (!write(Keys[i * 2], b.Id & 0xFFFFFF) || !write(Keys[i * 2 + 1], ((uint)b.Type << 8) | (b.Id >> 24)))
                return Rollback();
        }
        float revision = read(RevisionKey) ?? 0;
        if (!write(RevisionKey, float.IsFinite(revision) && revision is >= 0 and < 0xFFFFFF ? revision + 1 : 1))
            return Rollback();
        return true;

        bool Rollback()
        {
            for (int i = 0; i < Keys.Length; i++) write(Keys[i], before[i]);
            return false;
        }
    }

    public sealed class Input
    {
        private uint _down, _consumed;
        private bool _rightReserved;
        public int Bank { get; private set; }
        public readonly record struct Result(uint Buttons, bool ReserveRightTrigger, int Slot);

        public Result Step(uint buttons, uint leftTrigger, uint rightTrigger, bool allowed, IReadOnlyList<Binding> bindings, bool enabled = true)
        {
            uint down = buttons & 15u, pressed = down & ~_down;
            _down = down;
            if (!enabled)
            {
                _consumed = 0;
                _rightReserved = false;
                Bank = 0;
                return new Result(buttons, false, -1);
            }
            _consumed &= down;
            if (rightTrigger <= 30) _rightReserved = false;
            bool leftBank = false, rightBank = false;
            for (int i = 4; i < 8; i++) leftBank |= bindings[i].Assigned;
            for (int i = 8; i < Count; i++) rightBank |= bindings[i].Assigned;
            if (allowed && rightBank && rightTrigger > 30) _rightReserved = true;
            Bank = allowed ? (rightTrigger > 30 && rightBank ? 2 : leftTrigger > 30 && leftBank ? 1 : 0) : 0;
            int slot = -1;
            if (allowed && (rightTrigger <= 30 || rightBank))
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    uint bit = 1u << direction;
                    int candidate = Bank * 4 + direction;
                    if (bindings[candidate].Assigned || Bank != 0)
                    {
                        _consumed |= down & bit;
                        if (slot < 0 && (pressed & bit) != 0 && bindings[candidate].Assigned) slot = candidate;
                    }
                }
            }
            return new Result(buttons & ~_consumed, _rightReserved, slot);
        }
    }
}
