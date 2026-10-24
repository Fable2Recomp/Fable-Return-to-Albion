using System;
using System.Collections.Generic;
using System.Linq;
using static Refii.Ui.Engine_QuickSlots;

namespace Refii.Ui;

public sealed class Engine_UiQuickSlotOptions : Engine_UiHostOptionsPage
{
    public const string Prefix = "options:quickslots:";
    private readonly int _bank;
    private readonly Func<Binding[]> _read;
    private readonly Func<IReadOnlyList<Choice>> _catalog;
    private readonly Func<Binding[], bool> _save;
    private Binding[] _bindings = new Binding[Engine_QuickSlots.Count];
    private List<Choice> _choices = new();
    private IReadOnlyList<Choice>? _lastCatalog;
    private int[] _indices = new int[4];

    public Engine_UiQuickSlotOptions(int bank, Func<Binding[]> read, Func<IReadOnlyList<Choice>> catalog, Func<Binding[], bool> save)
    {
        _bank = Math.Clamp(bank, 0, 2);
        _read = read;
        _catalog = catalog;
        _save = save;
    }
    public override string Name => "Quickslots" + _bank;
    public override string Title => "Quickslots - " + Banks[_bank];
    public override int Count => 4;
    public override string RowTitle(int row) => Directions[row];
    public override int Choices(int row) => _choices.Count;
    public override string Caption(int row, int index) => index > 0 && index < _choices.Count
        ? _choices[index].Label : _bank == 0 ? "Automatic" : "Empty";

    public override int[] Read()
    {
        Binding[] bindings = _read();
        var catalog = _catalog();
        if (ReferenceEquals(catalog, _lastCatalog) && _bindings.SequenceEqual(bindings)) return (int[])_indices.Clone();
        _lastCatalog = catalog;
        _bindings = (Binding[])bindings.Clone();
        _choices = new List<Choice> { new(default, "") };
        _choices.AddRange(catalog.Where(c => c.Binding.Assigned).DistinctBy(c => c.Binding)
            .OrderBy(c => c.Binding.Type).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase));
        var indices = new int[4];
        for (int i = 0; i < 4; i++)
        {
            Binding b = _bindings[_bank * 4 + i];
            int at = _choices.FindIndex(c => c.Binding == b);
            if (at < 0 && b.Assigned)
            {
                at = _choices.Count;
                _choices.Add(new Choice(b, $"{b.Type}: unavailable ({b.Id:X8})"));
            }
            indices[i] = Math.Max(0, at);
        }
        _indices = indices;
        return (int[])indices.Clone();
    }

    public override bool Commit(Func<int, int> index)
    {
        var result = (Binding[])_read().Clone();
        for (int row = 0; row < 4; row++)
        {
            int at = index(row);
            if (at < 0 || at >= _choices.Count) return false;
            result[_bank * 4 + row] = _choices[at].Binding;
        }
        return _save(result);
    }
}
