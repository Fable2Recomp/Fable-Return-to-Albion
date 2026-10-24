using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Ui;

public sealed class Engine_UiQuickSlotHud : IReadOnlyList<float?[]?>
{
    private readonly Engine_UiScreen _screen;
    private readonly List<(int Owner, int ForcedSlide, BgfSlide[] Slides)> _owners = new();
    private readonly Dictionary<int, float?[]> _scaleRows = new();
    private IReadOnlyList<float?[]?>? _transforms;

    public Engine_UiQuickSlotHud(Engine_UiScreen screen, Engine_UiBindings bindings)
    {
        _screen = screen;
        foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.Meter, "SubtitlesY"))
        {
            if (ordinal < 0 || ordinal >= screen.Nodes.Length || screen.Nodes[ordinal].Name != "dpadmenu") continue;
            var menu = screen.Nodes[ordinal];
            var shown = AddOwner(menu, true);
            if (menu.Parent is { } mover)
            {
                AddOwner(mover, false);
                if (mover.Parent is { Name: "dpad" } root) AddOwner(root, false);
            }
            if (shown is null) continue;
            foreach (var entry in shown.Entries)
                if (entry.Commands.Any(c => c.Id == Engine_BgfEdit.ScaleXId || c.Id == Engine_BgfEdit.ScaleYId))
                    _scaleRows[entry.ElementOrdinal] = new float?[7];
        }
        foreach (string name in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
            foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, name))
                if (ordinal >= 0 && ordinal < screen.Nodes.Length) AddOwner(screen.Nodes[ordinal], false);
    }

    private BgfSlide? AddOwner(UiNode node, bool keepOpen)
    {
        var slides = _screen.Document.Slides.Where(s => s.OwnerElement == node.Ordinal && !s.IsMaster).ToArray();
        int shown = keepOpen ? Array.FindIndex(slides, s => string.Equals(s.Name, "in", StringComparison.OrdinalIgnoreCase)) + 1 : 0;
        if (slides.Length == 0 || (keepOpen && shown == 0)) return null;
        _owners.Add((node.Ordinal, shown, slides));
        return slides[Math.Max(shown, 1) - 1];
    }

    public void Apply(int[] slides, bool[] active, float?[] opacity)
    {
        foreach (var owner in _owners)
        {
            if (owner.Owner >= slides.Length) continue;
            int slide = owner.ForcedSlide > 0 ? owner.ForcedSlide : Math.Max(slides[owner.Owner], 1);
            if (slide > owner.Slides.Length) continue;
            slides[owner.Owner] = slide;
            Engine_UiElementResolve.ReleaseSlideEntries(_screen.Document, owner.Slides[slide - 1], active, opacity);
        }
    }

    public IReadOnlyList<float?[]?>? Transforms(IReadOnlyList<float?[]?>? source)
    {
        _transforms = source;
        return source is null ? null : this;
    }

    public int Count => _transforms?.Count ?? 0;
    public float?[]? this[int index]
    {
        get
        {
            var source = _transforms![index];
            if (source is null || !_scaleRows.TryGetValue(index, out var row)) return source;
            Array.Clear(row);
            Array.Copy(source, row, Math.Min(source.Length, row.Length));
            row[3] = row[4] = row[5] = null;
            return row;
        }
    }
    public IEnumerator<float?[]?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
