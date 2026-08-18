using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Refii.Equipment;
using Refii.Level;

namespace Refii.Hero;

[Tool]
[GlobalClass]
public partial class Engine_HeroInstance : Refii.Entities.Engine_EntityInstance
{
    public const string FemaleMeta = "hero_female";
    public const string BodyModelsMeta = "hero_body_models";
    public const string OutfitMeta = "hero_outfit";
    public const string MorphMeta = "hero_morph";

    private readonly System.Collections.Generic.Dictionary<ClothingSlot, int> _selection = new();

    private readonly HeroMorphWeights _morphs = new();

    private readonly System.Collections.Generic.Dictionary<string, HeroMorphSet?> _morphSets =
        new(System.StringComparer.OrdinalIgnoreCase);

    private OutfitResult? _outfit;

    private System.Collections.Generic.Dictionary<string, BoneMorphOffset>? _skeletal;

    public bool Female => HasMeta(FemaleMeta) && GetMeta(FemaleMeta).AsBool();

    public override void _Ready() => Rebuild();

    public string[] BodyModels =>
        HasMeta(BodyModelsMeta) ? GetMeta(BodyModelsMeta).AsStringArray() : System.Array.Empty<string>();

    public void Rebuild()
    {
        var chosen = new System.Collections.Generic.Dictionary<ClothingSlot, ClothingEntry>();

        foreach (ClothingSlot slot in Engine_HeroWardrobe.SlotsFor(Female))
        {
            if (!_selection.TryGetValue(slot, out int index) || index <= 0)
            {
                continue;
            }

            List<WardrobeOption> options = Engine_HeroWardrobe.OptionsFor(slot, Female);
            if (index < options.Count && options[index].Entry is ClothingEntry entry)
            {
                chosen[slot] = entry;
            }
        }

        _skeletal = null;
        _outfit = Engine_HeroOutfit.Resolve(chosen, Female);

        var parts = new List<string>(BodyModels);
        foreach (string path in _outfit.ModelPaths)
        {
            if (!parts.Contains(path))
            {
                parts.Add(path);
            }
        }

        _textureMorphs = _morphs.Any
            ? Engine_HeroTextureMorph.Active(_morphs)
            : new System.Collections.Generic.Dictionary<uint, List<TextureMorphEntry>>();

        Refii.Material.Engine_MdlMaterial.TextureOverride = MorphTexture;
        Refii.Textures.Engine_TexLibrary.Invalidate();

        ArrayMesh? mesh;
        try
        {
            mesh = Refii.Models.Engine_MdlComposite.Build(parts, ResolvePart, out _, out _);
        }
        finally
        {
            Refii.Material.Engine_MdlMaterial.TextureOverride = null;
        }

        if (mesh is not null)
        {
            Mesh = mesh;
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                this, Engine_SkyEnvironmentNode.Current?.Lighting);
        }

        StoreSelection();
        StoreMorphs();
        NotifyPropertyListChanged();
    }

    private System.Collections.Generic.Dictionary<uint, List<TextureMorphEntry>>? _textureMorphs;

    private Texture2D? MorphTexture(string name, Refii.Textures.TexSemantic semantic)
    {
        if (semantic != Refii.Textures.TexSemantic.Albedo || _textureMorphs is null ||
            _textureMorphs.Count == 0)
        {
            return null;
        }

        return Engine_HeroTextureMorph.Resolve(name, _textureMorphs);
    }

    private Mesh? ResolvePart(string path)
    {
        HeroMorphSet? set = MorphSetFor(path);

        ArrayMesh? mesh = set is not null && _morphs.Any
            ? Engine_HeroMorph.Blend(path, set.Targets, _morphs, Engine_PropStreamer.Resolve)
            : Engine_PropStreamer.Resolve(path);

        if (mesh is null || !_morphs.Any)
        {
            return mesh;
        }

        return ApplySkeletal(path, mesh) ?? mesh;
    }

    private ArrayMesh? ApplySkeletal(string path, ArrayMesh mesh)
    {
        _skeletal ??= Engine_SkeletalMorphs.Accumulate(_morphs, Female);
        if (_skeletal.Count == 0)
        {
            return null;
        }

        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);
        return bytes is null ? null : Engine_HeroBodyMorph.Apply(bytes, mesh, _skeletal);
    }

    private HeroMorphSet? MorphSetFor(string path)
    {
        if (_morphSets.TryGetValue(path, out HeroMorphSet? cached))
        {
            return cached;
        }

        HeroMorphSet? set = Engine_HeroMorphCatalog.KindOfPath(path) is null
            ? Engine_HeroMorphCatalog.For(path, Female, Engine_PropStreamer.Resolve)
            : null;

        _morphSets[path] = set;
        return set;
    }

    public HeroMorphSet? BodyMorphs()
    {
        foreach (string path in BodyModels)
        {
            HeroMorphSet? set = MorphSetFor(path);
            if (set is not null)
            {
                return set;
            }
        }

        return null;
    }

    private void StoreMorphs()
    {
        var stored = new Dictionary();
        foreach (HeroMorphSlider slider in Engine_HeroMorphSliders.All)
        {
            stored[(int)slider.Kind] = _morphs[slider.Kind];
        }

        SetMeta(MorphMeta, stored);
    }

    private void LoadMorphs()
    {
        if (!HasMeta(MorphMeta))
        {
            return;
        }

        Dictionary stored = GetMeta(MorphMeta).AsGodotDictionary();
        foreach (Variant key in stored.Keys)
        {
            Engine_HeroMorphSliders.Set(
                _morphs, (HeroMorphKind)key.AsInt32(), (float)stored[key].AsDouble());
        }
    }

    private void StoreSelection()
    {
        var stored = new Dictionary();
        foreach (KeyValuePair<ClothingSlot, int> pair in _selection)
        {
            stored[(int)pair.Key] = pair.Value;
        }

        SetMeta(OutfitMeta, stored);
    }

    private void LoadSelection()
    {
        if (_selection.Count != 0 || !HasMeta(OutfitMeta))
        {
            return;
        }

        Dictionary stored = GetMeta(OutfitMeta).AsGodotDictionary();
        foreach (Variant key in stored.Keys)
        {
            _selection[(ClothingSlot)key.AsInt32()] = stored[key].AsInt32();
        }
    }

    public override Array<Dictionary> _GetPropertyList()
    {
        Array<Dictionary> list = base._GetPropertyList();
        LoadSelection();
        LoadMorphs();

        Group(list, "Morphs");
        {
            foreach (HeroMorphSlider slider in Engine_HeroMorphSliders.All)
            {
                list.Add(new Dictionary
                {
                    { "name", slider.Label },
                    { "type", (int)Variant.Type.Float },
                    { "usage", (int)PropertyUsageFlags.Default },
                    { "hint", (int)PropertyHint.Range },
                    { "hint_string", Engine_HeroMorphSliders.RangeHint },
                });
            }
        }

        Group(list, "Outfit");
        foreach (ClothingSlot slot in Engine_HeroWardrobe.SlotsFor(Female))
        {
            List<WardrobeOption> options = Engine_HeroWardrobe.OptionsFor(slot, Female);
            list.Add(new Dictionary
            {
                { "name", Engine_ClothingSlots.LabelOf(slot) },
                { "type", (int)Variant.Type.Int },
                { "usage", (int)PropertyUsageFlags.Default },
                { "hint", (int)PropertyHint.Enum },
                { "hint_string", Engine_HeroWardrobe.HintFor(options) },
            });
        }

        if (_outfit is not null && _outfit.Layers.Count != 0)
        {
            Group(list, "Worn");
            for (int i = 0; i < _outfit.Layers.Count; i++)
            {
                Row(list, "Layer " + (i + 1));
            }
        }

        if (_outfit is not null && _outfit.Occluded.Count != 0)
        {
            Group(list, "Hidden By Outer Layer");
            for (int i = 0; i < _outfit.Occluded.Count; i++)
            {
                Row(list, "Hidden " + (i + 1));
            }
        }

        if (_outfit is not null && _outfit.Missing.Count != 0)
        {
            Group(list, "Unresolved Models");
            for (int i = 0; i < _outfit.Missing.Count; i++)
            {
                Row(list, "Unresolved " + (i + 1));
            }
        }

        return list;
    }

    public override Variant _Get(StringName property)
    {
        string name = property.ToString();
        LoadSelection();
        LoadMorphs();

        if (Engine_HeroMorphSliders.TryFind(name, out HeroMorphKind morphKind))
        {
            return _morphs[morphKind];
        }

        foreach (ClothingSlot slot in Engine_HeroWardrobe.SlotsFor(Female))
        {
            if (Engine_ClothingSlots.LabelOf(slot) == name)
            {
                return _selection.TryGetValue(slot, out int index) ? index : 0;
            }
        }

        if (_outfit is not null)
        {
            if (TryIndexed("Layer ", name, _outfit.Layers.Count, out int layer))
            {
                OutfitLayer worn = _outfit.Layers[layer];
                return $"{Engine_ClothingSlots.LabelOf(worn.Slot)}: {worn.Name}  " +
                    $"(layer {worn.SortLayer}, areas 0x{worn.BodyAreasCovered:X}, " +
                    $"{worn.ClustersCovered.Count} cluster(s))";
            }

            if (TryIndexed("Hidden ", name, _outfit.Occluded.Count, out int hidden))
            {
                return _outfit.Occluded[hidden];
            }

            if (TryIndexed("Unresolved ", name, _outfit.Missing.Count, out int missing))
            {
                return _outfit.Missing[missing];
            }
        }

        return base._Get(property);
    }

    public override bool _Set(StringName property, Variant value)
    {
        string name = property.ToString();

        if (Engine_HeroMorphSliders.TryFind(name, out HeroMorphKind morphKind))
        {
            Engine_HeroMorphSliders.Set(_morphs, morphKind, (float)value.AsDouble());
            Rebuild();
            return true;
        }

        foreach (ClothingSlot slot in Engine_HeroWardrobe.SlotsFor(Female))
        {
            if (Engine_ClothingSlots.LabelOf(slot) != name)
            {
                continue;
            }

            _selection[slot] = value.AsInt32();
            Rebuild();
            return true;
        }

        return base._Set(property, value);
    }

    private static bool TryIndexed(string prefix, string name, int count, out int index)
    {
        index = 0;

        if (!name.StartsWith(prefix, System.StringComparison.Ordinal) ||
            !int.TryParse(name[prefix.Length..], out int parsed) ||
            parsed < 1 || parsed > count)
        {
            return false;
        }

        index = parsed - 1;
        return true;
    }

    private static void Group(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

    private static void Row(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
        });
}
