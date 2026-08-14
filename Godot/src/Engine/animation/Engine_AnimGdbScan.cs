using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Animations;

public sealed class ModelAnimationBinding
{
    public uint ModelPathHash { get; set; }

    public uint SkeletonFileHash { get; set; }

    public uint RetargetSkeletonFileHash { get; set; }

    public uint AnimationRecordHash { get; set; }

    public uint AnimationKey { get; set; }

    public uint SourceRecordHash { get; set; }

    public int ClipIndex { get; set; }

    public string AnimationName { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;
}

public static class Engine_AnimGdbScan
{
    private sealed class AnimationRecord
    {
        public int ClipIndex;

        public uint AnimHash;

        public string Label = string.Empty;
    }

    private sealed class RefSet
    {
        public readonly List<ClipRef> Items = new();

        private readonly HashSet<(int Clip, uint Key)> _seen = new();

        public void Add(ClipRef reference)
        {
            if (_seen.Add((reference.ClipIndex, reference.AnimationKey)))
            {
                Items.Add(reference);
            }
        }
    }

    private static Dictionary<uint, List<ClipRef>>? _groupCache;

    private sealed class ClipRef
    {
        public int ClipIndex;

        public uint AnimationRecordHash;

        public uint AnimationKey;

        public string Label = string.Empty;
    }

    public static int Scan(
        GdbView view,
        IReadOnlyDictionary<uint, int> byKey0,
        List<AnimClip> clips,
        List<ModelAnimationBinding> bindings,
        HashSet<(uint Model, int Clip, uint Key)> seen)
    {
        BuildParentMap(view);

        var records = new Dictionary<uint, AnimationRecord>();
        Dictionary<uint, List<int>> children = ChildrenByParent(view);

        for (int i = 0; i < view.RecordCount; i++)
        {
            uint recordHash = view.RecordHash(i);

            if (!view.TryFindFieldOwner(i, Engine_AnimGdbHashes.Animation,
                    (int)GdbFieldType.String, out GdbField field, out int owner))
            {
                continue;
            }

            if (!byKey0.TryGetValue(field.Raw, out int clipIndex))
            {
                continue;
            }

            string label = LabelForAnimationSource(view, recordHash, field.Raw);
            records[recordHash] = new AnimationRecord
            {
                ClipIndex = clipIndex,
                AnimHash = field.Raw,
                Label = label,
            };

            if (owner == i)
            {
                AssignLabel(clips, clipIndex, label);
            }
        }

        for (int i = 0; i < view.RecordCount; i++)
        {
            foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(i))
            {
                string label = NameForHash(view, hash);
                if (IsGenericFieldName(hash, label))
                {
                    continue;
                }

                if (type == GdbFieldType.Reference &&
                    records.TryGetValue(raw, out AnimationRecord? referenced))
                {
                    AssignLabel(clips, referenced.ClipIndex, label);
                }
                else if (type == GdbFieldType.String && byKey0.TryGetValue(raw, out int keyed))
                {
                    AssignLabel(clips, keyed, label);
                }
            }
        }

        int added = 0;

        for (int i = 0; i < view.RecordCount; i++)
        {
            var modelHashes = new List<uint>();
            CollectAnimatedModelHashes(view, i, modelHashes);
            if (modelHashes.Count == 0)
            {
                continue;
            }

            var refs = new RefSet();
            CollectClipRefs(view, i, children, records, byKey0, refs);
            if (refs.Items.Count == 0)
            {
                continue;
            }

            FindInherited(view, i, Engine_AnimGdbHashes.SkeletonFile, out uint skeletonHash);
            FindInherited(view, i, Engine_AnimGdbHashes.RetargetSkeletonFile, out uint retargetHash);

            uint recordHash = view.RecordHash(i);

            foreach (uint modelHash in modelHashes)
            {
                foreach (ClipRef reference in refs.Items)
                {
                    if (reference.ClipIndex < 0 || reference.ClipIndex >= clips.Count)
                    {
                        continue;
                    }

                    if (!seen.Add((modelHash, reference.ClipIndex, reference.AnimationKey)))
                    {
                        continue;
                    }

                    bindings.Add(new ModelAnimationBinding
                    {
                        ModelPathHash = modelHash,
                        SkeletonFileHash = skeletonHash,
                        RetargetSkeletonFileHash = retargetHash,
                        AnimationRecordHash = reference.AnimationRecordHash,
                        AnimationKey = reference.AnimationKey,
                        SourceRecordHash = recordHash,
                        ClipIndex = reference.ClipIndex,
                        AnimationName = reference.Label.Length != 0
                            ? reference.Label
                            : clips[reference.ClipIndex].Name,
                        SourceName = reference.Label,
                    });

                    added++;
                }
            }
        }

        return added;
    }

    private static Dictionary<uint, List<int>> ChildrenByParent(GdbView view)
    {
        var map = new Dictionary<uint, List<int>>();

        for (int i = 0; i < view.RecordCount; i++)
        {
            if (!view.TryFindLocal(i, GdbView.HashParent, (int)GdbFieldType.Reference,
                    out GdbField parent) || parent.Raw == 0)
            {
                continue;
            }

            if (!map.TryGetValue(parent.Raw, out List<int>? list))
            {
                list = new List<int>();
                map[parent.Raw] = list;
            }

            list.Add(i);
        }

        return map;
    }

    private static int[]? _parentOf;

    private static void BuildParentMap(GdbView view)
    {
        var parents = new int[view.RecordCount];

        for (int i = 0; i < view.RecordCount; i++)
        {
            parents[i] = view.TryFindLocal(i, GdbView.HashParent, (int)GdbFieldType.Reference,
                    out GdbField parent) &&
                parent.Raw != 0 &&
                view.TryLookup(parent.Raw, out int index) &&
                index != i
                ? index
                : -1;
        }

        _parentOf = parents;
        _inherited = null;
        _groupCache = null;
    }

    private static void VisitRecordAndParents(GdbView view, int record, System.Action<int> visit)
    {
        int[] parents = _parentOf ?? System.Array.Empty<int>();
        int current = record;

        for (int depth = 0; depth < 64; depth++)
        {
            if (current < 0 || current >= parents.Length)
            {
                return;
            }

            visit(current);
            current = parents[current];
        }
    }

    private struct InheritedEntry
    {
        public bool Done;

        public bool Found;

        public uint Raw;
    }

    private static Dictionary<uint, InheritedEntry[]>? _inherited;

    private static bool TryLocalValue(GdbView view, int record, uint fieldHash, out uint raw)
    {
        foreach ((uint hash, GdbFieldType type, uint value) in view.RawFields(record))
        {
            if (hash != fieldHash)
            {
                continue;
            }

            if (type is not (GdbFieldType.Reference or GdbFieldType.ResourceReference
                or GdbFieldType.String or GdbFieldType.Float))
            {
                continue;
            }

            raw = value;
            return true;
        }

        raw = 0;
        return false;
    }

    private static bool FindInherited(GdbView view, int record, uint fieldHash, out uint raw)
    {
        raw = 0;

        int[] parents = _parentOf ?? System.Array.Empty<int>();
        if (record < 0 || record >= parents.Length)
        {
            return false;
        }

        _inherited ??= new Dictionary<uint, InheritedEntry[]>();
        if (!_inherited.TryGetValue(fieldHash, out InheritedEntry[]? cache))
        {
            cache = new InheritedEntry[parents.Length];
            _inherited[fieldHash] = cache;
        }

        var path = new List<int>();
        int current = record;
        InheritedEntry result = new() { Done = true };

        for (int depth = 0; depth < 64; depth++)
        {
            if (current < 0 || current >= parents.Length)
            {
                break;
            }

            if (cache[current].Done)
            {
                result = cache[current];
                break;
            }

            if (TryLocalValue(view, current, fieldHash, out uint value))
            {
                result = new InheritedEntry { Done = true, Found = true, Raw = value };
                cache[current] = result;
                break;
            }

            path.Add(current);
            current = parents[current];
        }

        foreach (int node in path)
        {
            cache[node] = result;
        }

        raw = result.Raw;
        return result.Found && result.Raw != 0 && result.Raw != Engine_AnimGdbHashes.FnvOffsetBasis;
    }

    private static void CollectModelFileHashes(GdbView view, int record, List<uint> outHashes)
    {
        VisitRecordAndParents(view, record, current =>
        {
            foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(current))
            {
                if (type is not (GdbFieldType.Float or GdbFieldType.String
                    or GdbFieldType.ResourceReference))
                {
                    continue;
                }

                if (hash == Engine_AnimGdbHashes.ModelFile ||
                    hash == Engine_AnimGdbHashes.ModelFile1 ||
                    hash == Engine_AnimGdbHashes.ModelFile2)
                {
                    PushUnique(outHashes, raw);
                }
            }
        });
    }

    private static void CollectAnimatedModelHashes(GdbView view, int record, List<uint> outHashes)
    {
        void CollectComponent(uint componentHash)
        {
            if (view.TryLookup(componentHash, out int componentRecord))
            {
                CollectModelFileHashes(view, componentRecord, outHashes);
            }
        }

        if (FindInherited(view, record,
                Engine_AnimGdbHashes.GraphicAppearanceAnimatedMeshComponent, out uint direct))
        {
            CollectComponent(direct);
        }

        if (FindInherited(view, record,
                Engine_AnimGdbHashes.GraphicAppearanceComponent, out uint appearance) &&
            view.TryLookup(appearance, out int appearanceRecord))
        {
            VisitRecordAndParents(view, appearanceRecord, current =>
            {
                if (view.TryFindLocal(current,
                        Engine_AnimGdbHashes.GraphicAppearanceAnimatedMeshComponent,
                        (int)GdbFieldType.Reference, out GdbField component))
                {
                    CollectComponent(component.Raw);
                }
            });
        }

        if (!FindInherited(view, record,
                Engine_AnimGdbHashes.GraphicAppearanceMorphComponent, out uint morph) ||
            !view.TryLookup(morph, out int morphRecord))
        {
            return;
        }

        if (!FindInherited(view, morphRecord,
                Engine_AnimGdbHashes.CompositeModelRecord, out uint composite) ||
            !view.TryLookup(composite, out int compositeRecord))
        {
            return;
        }

        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(compositeRecord);

        while (stack.Count != 0 && visited.Count < 512)
        {
            int current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            var resolved = new HashSet<uint>();
            var inherited = new HashSet<int>();
            int cursor = current;

            for (int depth = 0; depth < 64; depth++)
            {
                if (!inherited.Add(cursor))
                {
                    break;
                }

                foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(cursor))
                {
                    if (hash == GdbView.HashParent || !resolved.Add(hash))
                    {
                        continue;
                    }

                    if (Engine_AnimGdbHashes.IsModelField(hash) &&
                        type is GdbFieldType.Float or GdbFieldType.String
                            or GdbFieldType.ResourceReference)
                    {
                        PushUnique(outHashes, raw);
                    }

                    if (type is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                        Engine_AnimGdbHashes.IsAppearanceLink(hash) &&
                        raw != 0 && raw != Engine_AnimGdbHashes.FnvOffsetBasis &&
                        view.TryLookup(raw, out int linked))
                    {
                        stack.Push(linked);
                    }
                }

                if (!view.TryFindLocal(cursor, GdbView.HashParent, (int)GdbFieldType.Reference,
                        out GdbField parent) ||
                    !view.TryLookup(parent.Raw, out int parentRecord))
                {
                    break;
                }

                cursor = parentRecord;
            }
        }
    }

    private static void CollectClipRefs(
        GdbView view,
        int record,
        Dictionary<uint, List<int>> children,
        Dictionary<uint, AnimationRecord> records,
        IReadOnlyDictionary<uint, int> byKey0,
        RefSet outRefs)
    {
        void CollectScope(int scopeRecord)
        {
            if (FindInherited(view, scopeRecord, Engine_AnimGdbHashes.Animation, out uint raw) &&
                byKey0.TryGetValue(raw, out int clipIndex))
            {
                outRefs.Add(new ClipRef
                {
                    ClipIndex = clipIndex,
                    AnimationRecordHash = view.RecordHash(scopeRecord),
                    AnimationKey = raw,
                    Label = LabelForAnimationSource(view, view.RecordHash(scopeRecord), raw),
                });
            }

            uint[] groups =
            {
                Engine_AnimGdbHashes.AnimationList,
                Engine_AnimGdbHashes.Animations,
                Engine_AnimGdbHashes.AnimationSet,
            };

            foreach (uint groupField in groups)
            {
                if (FindInherited(view, scopeRecord, groupField, out uint groupHash))
                {
                    foreach (ClipRef reference in
                        GroupRefs(view, groupHash, children, records, byKey0))
                    {
                        outRefs.Add(reference);
                    }
                }
            }
        }

        CollectScope(record);

        if (FindInherited(view, record, Engine_AnimGdbHashes.AnimationManagerComponent,
                out uint manager) &&
            view.TryLookup(manager, out int managerRecord) && managerRecord != record)
        {
            CollectScope(managerRecord);
        }
    }

    private static List<ClipRef> GroupRefs(
        GdbView view,
        uint groupHash,
        Dictionary<uint, List<int>> children,
        Dictionary<uint, AnimationRecord> records,
        IReadOnlyDictionary<uint, int> byKey0)
    {
        _groupCache ??= new Dictionary<uint, List<ClipRef>>();
        if (_groupCache.TryGetValue(groupHash, out List<ClipRef>? cached))
        {
            return cached;
        }

        var outRefs = new RefSet();
        _groupCache[groupHash] = outRefs.Items;

        if (!view.TryLookup(groupHash, out int groupRecord))
        {
            return outRefs.Items;
        }

        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(groupRecord);

        while (stack.Count != 0)
        {
            int current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(current))
            {
                string label = NameForHash(view, hash);
                if (IsGenericFieldName(hash, label))
                {
                    continue;
                }

                if (type == GdbFieldType.Reference &&
                    records.TryGetValue(raw, out AnimationRecord? animationRecord))
                {
                    outRefs.Add(new ClipRef
                    {
                        ClipIndex = animationRecord.ClipIndex,
                        AnimationRecordHash = raw,
                        AnimationKey = animationRecord.AnimHash,
                        Label = label,
                    });
                }
                else if (type == GdbFieldType.String && byKey0.TryGetValue(raw, out int clipIndex))
                {
                    outRefs.Add(new ClipRef
                    {
                        ClipIndex = clipIndex,
                        AnimationRecordHash = 0,
                        AnimationKey = raw,
                        Label = label,
                    });
                }
            }

            if (children.TryGetValue(view.RecordHash(current), out List<int>? kids))
            {
                foreach (int child in kids)
                {
                    stack.Push(child);
                }
            }
        }

        return outRefs.Items;
    }

    private static void PushUnique(List<uint> values, uint value)
    {
        if (value == 0 || value == Engine_AnimGdbHashes.FnvOffsetBasis)
        {
            return;
        }

        foreach (uint existing in values)
        {
            if (existing == value)
            {
                return;
            }
        }

        values.Add(value);
    }

    private static string NameForHash(GdbView view, uint hash) =>
        view.Names.TryGetValue(hash, out string? name) ? name : string.Empty;

    private static string LabelForAnimationSource(GdbView view, uint recordHash, uint animHash)
    {
        string name = NameForHash(view, animHash);
        if (name.Length != 0)
        {
            return name;
        }

        name = NameForHash(view, recordHash);
        if (name.Length != 0)
        {
            return name;
        }

        if (view.RecordNames.TryGetValue(recordHash, out string? recordName) &&
            recordName.Length != 0)
        {
            return recordName;
        }

        return $"gdb_{recordHash:x8}";
    }

    private static bool IsGenericFieldName(uint hash, string label)
    {
        if (hash is GdbView.HashParent or Engine_AnimGdbHashes.AnimationName
            or Engine_AnimGdbHashes.AnimName or Engine_AnimGdbHashes.Animation
            or Engine_AnimGdbHashes.Animations)
        {
            return true;
        }

        if (hash is Engine_AnimGdbHashes.SkeletonFile
            or Engine_AnimGdbHashes.RetargetSkeletonFile)
        {
            return true;
        }

        return label.Length == 0 ||
            label == "Animation" ||
            label == "Animations" ||
            label == "AnimationName" ||
            label == "AnimName" ||
            label == "parent" ||
            label.EndsWith("File", System.StringComparison.Ordinal) ||
            IsNumberedSlot(label);
    }

    private static bool IsNumberedSlot(string label)
    {
        int digits = label.Length;
        while (digits > 0 && char.IsDigit(label[digits - 1]))
        {
            digits--;
        }

        if (digits == label.Length || digits == 0)
        {
            return false;
        }

        string stem = label[..digits];
        return stem is "Anim" or "Animation" or "Slot" or "Entry";
    }

    private static void AssignLabel(List<AnimClip> clips, int index, string label)
    {
        if (index < 0 || index >= clips.Count || label.Length == 0)
        {
            return;
        }

        AnimClip clip = clips[index];
        if (clip.Name.StartsWith("id_", System.StringComparison.Ordinal) ||
            clip.Name.StartsWith("gdb_", System.StringComparison.Ordinal))
        {
            clip.Name = label;
        }
    }
}
