using System;
using System.Collections.Generic;
using System.Diagnostics;
using Refii.Core;
using Refii.Gdb;
using Refii.Host;
using Refii.Ui;
using static Refii.Ui.Engine_QuickSlots;

namespace Refii.Game;

public static class GameQuickSlots
{
    private static readonly Binding[][] Bindings = { new Binding[Count], new Binding[Count] };
    private static readonly Input[] Inputs = { new(), new() };
    private static readonly float[] Revisions = { -1, -1 };
    private static readonly Dictionary<Binding, Choice> Names = new();
    private static IReadOnlyList<Choice> _catalog = Array.Empty<Choice>();
    private static uint _catalogHero, _catalogGeneration, _runtimeHero;
    private static long _nextRead;
    private static bool _loaded;
    private static readonly Engine_UiHostOptionsPage?[] Pages = new Engine_UiHostOptionsPage?[3];
    private static uint _pageHero;
    private static int _pagePlayer;
    private static readonly uint[] AssignmentButtons = new uint[2], AssignmentPressed = new uint[2], AssignmentLeft = new uint[2], AssignmentRight = new uint[2];
    public static uint Generation { get; private set; }
    public static bool Enabled { get; private set; }
    public static Engine_UiQuickSlotAssignment? Assignment { get; private set; }
    public static bool Assigning => Assignment is { IsOpen: true };
    public static int LocalPlayer => CoopSession.IsClient ? 2 : 1;
    public static int MenuPlayer => GameInput.MenuOwner == 2 ? 2 : LocalPlayer;
    public static int Bank(int player) => Inputs[player - 1].Bank;
    public static Binding Assigned(int player, int direction) => Enabled ? Bindings[player - 1][Bank(player) * 4 + direction] : default;
    public static string Icon(Binding binding) => Names.TryGetValue(binding, out var choice) ? choice.Icon : "";
    public static int MatchingSlots(Binding binding, int player) => Engine_QuickSlots.MatchingSlots(binding, Bindings[player - 1]);

    public static void Configure(IReadOnlyDictionary<string, string> values)
    {
        bool enabled = Engine_RefiiConfig.Bool(values, EnabledKey, false);
        if (Enabled == enabled) return;
        Enabled = enabled;
        Assignment?.Cancel();
        _loaded = false;
        _nextRead = 0;
        Generation++;
    }

    public static bool CanAssign(Binding binding)
    {
        if (!Enabled || !binding.Assigned) return false;
        if (binding.Type != Kind.Spell) return true;
        var spells = HostBridge.GetSpellAssign();
        for (int tier = 0; tier < 5; tier++)
            if (spells.Offers(tier, (int)binding.Id)) return true;
        return false;
    }

    public static bool OpenAssignment(Binding binding, int player)
    {
        if (!Enabled) return false;
        uint hero = HostBridge.HeroEntityAddress();
        if (hero == 0 || player is < 1 or > 2 || !HostBridge.IsInGame || !CanAssign(binding)) return false;
        Refresh();
        Choice choice = Describe(binding);
        Names[binding] = choice;
        Assignment?.Cancel();
        Assignment = new Engine_UiQuickSlotAssignment(player, choice,
            () => Read(k => HostBridge.GetPlayerValue(player, k)),
            bindings => SaveFor(player, hero, bindings),
            b => Names.TryGetValue(b, out var known) ? known : Describe(b),
            () => HostBridge.IsInGame && HostBridge.HeroEntityAddress() == hero);
        return Assignment.IsOpen;
    }

    public static void DriveAssignment(double delta, int clickedSlot = -1) => Assignment?.Drive(delta,
        AssignmentButtons[Assignment.Player - 1], AssignmentLeft[Assignment.Player - 1],
        AssignmentRight[Assignment.Player - 1], clickedSlot);

    public static bool AssignmentButtonPressed(int player, uint button) =>
        (AssignmentPressed[player - 1] & button) != 0;

    private static bool SaveFor(int player, uint hero, Binding[] bindings)
    {
        if (!Enabled || hero == 0 || hero != HostBridge.HeroEntityAddress() || !HostBridge.IsInGame) return false;
        if (!Engine_QuickSlots.Save(bindings, k => HostBridge.GetPlayerValue(player, k),
            (k, v) => HostBridge.SetPlayerValue(player, k, v))) return false;
        Bindings[player - 1] = bindings;
        foreach (Binding b in bindings)
            if (b.Assigned && !Names.ContainsKey(b)) Names[b] = Describe(b);
        Generation++;
        _nextRead = 0;
        return true;
    }

    public static Binding SelectedExpression(int player)
    {
        Span<uint> word = stackalloc uint[1];
        bool Pointer(uint address) => address >= 0x10000 && address < 0xC0000000;
        uint at = HostBridge.GuestAddress(0x83496B08u);
        if (HostBridge.Peek(at, word) != 1 || !Pointer(word[0])) return default;
        if (HostBridge.Peek(word[0] + 32, word) != 1 || !Pointer(word[0])) return default;
        if (HostBridge.Peek(word[0] + 60, word) != 1 || !Pointer(word[0])) return default;
        Span<uint> wheel = stackalloc uint[15];
        if (HostBridge.Peek(word[0], wheel) != wheel.Length || wheel[0] != 1 || wheel[6] != player) return default;
        uint begin = wheel[13], end = wheel[14], selected = wheel[5];
        if (!Pointer(begin) || end < begin || end - begin > 12 * 64 || (end - begin) % 12 != 0 ||
            selected >= (end - begin) / 12) return default;
        Span<uint> entry = stackalloc uint[3];
        if (HostBridge.Peek(begin + selected * 12, entry) != entry.Length ||
            ((entry[1] >> 16) & 255) == 0 || (entry[1] & 255) != 0 || (entry[2] & 0xFFFF0000) != 0) return default;
        return new(Kind.Expression, entry[0]);
    }

    public static Engine_UiHostOptionsPage? PageFor(string command)
    {
        if (!Enabled || !command.StartsWith(Engine_UiQuickSlotOptions.Prefix, StringComparison.Ordinal) ||
            !int.TryParse(command[Engine_UiQuickSlotOptions.Prefix.Length..], out int bank) || bank is < 0 or > 2) return null;
        int player = MenuPlayer;
        uint hero = HostBridge.HeroEntityAddress();
        if (hero != _pageHero || player != _pagePlayer)
        {
            Array.Clear(Pages);
            _pageHero = hero;
            _pagePlayer = player;
        }
        return Pages[bank] ??= new Engine_UiQuickSlotOptions(bank, () =>
            {
                Refresh();
                return Bindings[player - 1];
            },
            Catalog, bindings =>
            {
                if (hero == 0 || hero != HostBridge.HeroEntityAddress() || MenuPlayer != player) return false;
                return SaveFor(player, hero, bindings);
            });
    }

    public static Input.Result Process(int player, uint buttons, uint left, uint right, bool allowed)
    {
        AssignmentPressed[player - 1] = buttons & ~AssignmentButtons[player - 1];
        AssignmentButtons[player - 1] = buttons;
        AssignmentLeft[player - 1] = left;
        AssignmentRight[player - 1] = right;
        if (!Enabled) return Inputs[player - 1].Step(buttons, left, right, false, Bindings[player - 1], enabled: false);
        Refresh();
        int bank = Bank(player);
        var result = Inputs[player - 1].Step(buttons, left, right, allowed && _loaded, Bindings[player - 1]);
        if (bank != Bank(player)) Generation++;
        if (result.Slot >= 0)
        {
            Binding binding = Bindings[player - 1][result.Slot];
            if (binding.Type == Kind.Weapon && CoopSession.IsClient)
            {
                CoopHeroKit.SetLocalWeapon(binding.Id, Engine_UiInventorySource.WeaponClassOf(binding.Id));
                return result;
            }
            HostBridge.PostFrontEndAction($"Quickslot:{player}:{(int)binding.Type}:{binding.Id}");
        }
        return result;
    }

    private static void Refresh()
    {
        if (!HostBridge.IsInGame)
        {
            if (_loaded)
            {
                _loaded = false;
                Generation++;
                _runtimeHero = 0;
                _nextRead = 0;
                for (int i = 0; i < 2; i++) { Array.Clear(Bindings[i]); Revisions[i] = -1; }
            }
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (now < _nextRead) return;
        _nextRead = now + Stopwatch.Frequency / 4;
        uint hero = HostBridge.HeroEntityAddress();
        if (hero == 0) { _loaded = false; return; }
        bool changed = _runtimeHero != hero || !_loaded;
        _runtimeHero = hero;
        _loaded = true;
        for (int player = 1; player <= 2; player++)
        {
            float revision = HostBridge.GetPlayerValue(player, RevisionKey) ?? 0;
            if (changed || Revisions[player - 1] != revision)
            {
                Bindings[player - 1] = Read(k => HostBridge.GetPlayerValue(player, k));
                Revisions[player - 1] = revision;
                Generation++;
                foreach (Binding b in Bindings[player - 1])
                    if (b.Assigned && !Names.ContainsKey(b)) Names[b] = Describe(b);
            }
        }
    }

    private static IReadOnlyList<Choice> Catalog()
    {
        uint hero = HostBridge.HeroEntityAddress(), generation = HostBridge.UiDataGeneration();
        if (hero == _catalogHero && generation == _catalogGeneration) return _catalog;
        _catalogHero = hero;
        _catalogGeneration = generation;
        var choices = new List<Choice>();
        if (hero != 0)
        {
            foreach (var row in Engine_UiInventorySource.Rows("use:2,3,4,5,6,9,10,11,12,13,14,16,24,26"))
                if (!row.IsHeader && !row.Ghosted && row.Id != 0 && row.Command?.StartsWith("Inventory:use:", StringComparison.Ordinal) == true)
                    choices.Add(new(new(Kind.Item, row.Id), "Item: " + row.Label, row.Icon));
            foreach (string category in new[] { "melee", "ranged" })
                foreach (var row in Engine_UiInventorySource.Weapons(category))
                    if (!row.IsHeader && !row.Ghosted && row.Id != 0)
                        choices.Add(new(new(Kind.Weapon, row.Id), "Weapon: " + row.Label, row.Icon));
            uint performer = HostBridge.EntityComponentAddress(hero, 154);
            if (performer != 0)
            {
                uint[] range = HostBridge.Peek(performer + 0x88, 2);
                if (range.Length == 2 && range[0] >= 0x10000 && range[1] >= range[0] &&
                    range[1] - range[0] <= 20 * 512 && (range[1] - range[0]) % 20 == 0)
                {
                    uint[] records = HostBridge.Peek(range[0], (range[1] - range[0]) / 4);
                    for (int i = 0; i + 4 < records.Length; i += 5)
                        if (records[i] != 0) choices.Add(Describe(new(Kind.Expression, records[i])));
                }
            }
            var spells = HostBridge.GetSpellAssign();
            for (uint spell = 0; spell < 8; spell++)
                for (int tier = 0; tier < 5; tier++)
                    if (spells.Offers(tier, (int)spell)) { choices.Add(Describe(new(Kind.Spell, spell))); break; }
        }
        foreach (Choice choice in choices) Names[choice.Binding] = choice;
        return _catalog = choices;
    }

    private static Choice Describe(Binding binding)
    {
        if (binding.Type is Kind.Item or Kind.Weapon) return Engine_UiInventorySource.QuickSlotChoice(binding);
        if (binding.Type == Kind.Spell && binding.Id < 8)
        {
            var will = Engine_UiScreenConstants.Wills[binding.Id];
            return new(binding, "Spell: " + Localize(will.Tag), Engine_UiScreenConstants.WillIconFolder + will.Icon + ".tex");
        }
        string Field(string field)
        {
            var gdb = Engine_UiInventorySource.Globals;
            return gdb is not null && gdb.TryLookup(binding.Id, out GdbCursor record) &&
                gdb.TryFindInherited(record, Engine_GameHash.Fnv1(field), (int)GdbFieldType.String,
                    out GdbCursor owner, out uint hash, out _) && owner.View.Names.TryGetValue(hash, out string? value)
                ? value : "";
        }
        string label = Field("NameTag"), icon = Field("IconGraphic");
        if (label.Length == 0) label = $"{binding.Id:X8}";
        return new(binding, binding.Type + ": " + Localize(label), icon);
    }

    private static string Localize(string tag) => Engine_UiInventorySource.Strings is { } strings &&
        strings.TryGetString(tag, out string text) && text.Length != 0 ? text : tag;
}
