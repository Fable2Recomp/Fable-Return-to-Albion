using System;
using System.Collections.Generic;

namespace Refii.Gdb;

public sealed class GdbPlacement
{
    public uint Hash { get; set; }

    public uint ParentHash { get; set; }

    public string EntityName { get; set; } = string.Empty;

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Yaw { get; set; }

    public float RotX { get; set; }

    public float RotY { get; set; }

    public float RotZ { get; set; }

    public bool HasRotation { get; set; }

    /// <summary>
    /// Fable 3: the rotation is the quaternion (RotX, RotY, RotZ, RotW), not Fable 2's Euler angles.
    /// </summary>
    public bool RotationIsQuaternion { get; set; }

    public float RotW { get; set; }

    public float Scale { get; set; } = 1f;

    public uint ModelPathHash { get; set; }

    public bool ModelPathFilled { get; set; }

    public uint SkeletonFileHash { get; set; }

    public uint RetargetSkeletonFileHash { get; set; }

    public List<uint> ModelPathHashes { get; set; } = new();

    /// <summary>
    /// This placement declares a mesh component but no model hash came out of it.
    ///
    /// Only ever set when <see cref="ModelPathHashes"/> is empty, and it is what separates a
    /// placement that was never going to draw from one we FAILED to resolve. See
    /// <c>Engine_GdbPlacements.StatesAppearanceComponent</c>.
    /// </summary>
    public bool StatesAppearance { get; set; }

    public GdbPlacement Clone() => new()
    {
        Hash = Hash,
        ParentHash = ParentHash,
        EntityName = EntityName,
        X = X,
        Y = Y,
        Z = Z,
        Yaw = Yaw,
        RotX = RotX,
        RotY = RotY,
        RotZ = RotZ,
        HasRotation = HasRotation,
        RotationIsQuaternion = RotationIsQuaternion,
        RotW = RotW,
        Scale = Scale,
        ModelPathHash = ModelPathHash,
        ModelPathFilled = ModelPathFilled,
        SkeletonFileHash = SkeletonFileHash,
        RetargetSkeletonFileHash = RetargetSkeletonFileHash,
        ModelPathHashes = new List<uint>(ModelPathHashes),
        StatesAppearance = StatesAppearance,
    };
}

public static class GdbHash
{
    public const uint Null = 0x811C9DC5;
    public const uint Position = 0xBD7C27D4;
    public const uint Rotation = 0x21EBC83B;
    public const uint TransformComponent = 0xF73572C4;
    public const uint SimpleTransformComponent = 0x619F96CF;
    public const uint PhysicsSimulationKeyframedComponent = 0x6B177DD0;
    public const uint PhysicsSimulationStaticComponent = 0x5883C406;
    public const uint PhysicsSimulationDynamicComponent = 0xFC8A57C5;
    public const uint GraphicAppearanceComponent = 0xA7B6EF56;
    public const uint GraphicAppearanceAnimatedMeshComponent = 0x21D312CA;
    public const uint StaticMeshComponent = 0x29CF50D1;
    public const uint StaticMultipleMeshComponent = 0xCE642A15;
    public const uint StaticMultipleModelList = 0x77679B84;
    public const uint StaticMultipleModelSlotA = 0x03C915A0;
    public const uint StaticMultipleModelSlotD = 0x03C915A3;
    public const uint StaticMultipleModelFile = 0x1372D766;
    public const uint ModelFile = 0x0C17DB4E;
    public const uint ModelFile1 = 0x578E3BFB;
    public const uint ModelFile2 = 0x578E3BF8;
    public const uint SkeletonFile = 0xC3D06E3A;
    public const uint RetargetSkeletonFile = 0x64234AF2;
    public const uint PhysicsFile = 0x92F5FEEE;

    public const uint InlineVec3SchemaRel = 0x00000568;
}

public static class Engine_GdbPlacements
{
    public const float Pi = 3.14159265358979323846f;

    public static List<GdbPlacement> Parse(
        GdbView view, IReadOnlyList<KeyValuePair<uint, string>> saveMap)
    {
        var placements = new List<GdbPlacement>();
        var emitted = new HashSet<uint>();

        void Emit(uint instanceHash, string entityName)
        {
            if (instanceHash == 0 || !emitted.Add(instanceHash))
            {
                return;
            }

            if (!TryLookupPlacement(
                    view, instanceHash, entityName, entityName.Length != 0,
                    out GdbPlacement placement))
            {
                emitted.Remove(instanceHash);
                return;
            }

            if (placement.ModelPathHashes.Count > 1)
            {
                foreach (uint hash in placement.ModelPathHashes)
                {
                    GdbPlacement part = placement.Clone();
                    part.ModelPathHash = hash;
                    part.ModelPathHashes = new List<uint> { hash };
                    placements.Add(part);
                }
            }
            else
            {
                placements.Add(placement);
            }
        }

        if (saveMap.Count != 0)
        {
            foreach (KeyValuePair<uint, string> entry in saveMap)
            {
                Emit(entry.Key, entry.Value);
            }
        }
        else
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                Emit(view.RecordHash(i), string.Empty);
            }
        }

        return placements;
    }

    /// <summary>
    /// Why a placement lookup failed, split because the two causes need opposite fixes.
    ///
    /// "The record is not in this view" means we are looking in the wrong gdb — the creature is
    /// described by one database and placed by another. "The record is here but carries no
    /// transform we recognise" means the placement is stored in a shape the three readers below
    /// do not cover. A single noTransform count cannot tell those apart, and the two lead to
    /// completely different places.
    /// </summary>
    public static int MissingRecord;

    public static int MissingTransform;

    public static bool TryLookupPlacement(
        GdbView view,
        uint recordHash,
        string entityName,
        bool lenient,
        out GdbPlacement placement)
    {
        placement = null!;

        if (recordHash == 0 || !view.TryLookup(recordHash, out int record))
        {
            ++MissingRecord;
            return false;
        }

        var transform = new Transform();
        bool found =
            TryComponentTransformRecord(view, record, transform, lenient) ||
            TryTransformRecord(view, record, transform, lenient) ||
            TryIndexedInlineTransform(view, record, transform) ||
            TryAnyComponentTransform(view, record, transform, lenient);

        if (!found)
        {
            ++MissingTransform;
            return false;
        }

        var result = new GdbPlacement
        {
            Hash = recordHash,
            EntityName = entityName,
            X = transform.X,
            Y = transform.Y,
            Z = transform.Z,
            RotX = transform.RotX,
            RotY = transform.RotY,
            RotZ = transform.RotZ,
            HasRotation = transform.HasRotation,
            RotationIsQuaternion = transform.RotationIsQuaternion,
            RotW = transform.RotW,
            Scale = transform.Scale > 0.0001f ? transform.Scale : 1f,
        };

        result.Yaw = !result.HasRotation ? 0f
            : result.RotationIsQuaternion ? GdbTransformMath.QuaternionYaw(result.RotX, result.RotY, result.RotZ, result.RotW)
            : float.IsFinite(result.RotX) ? result.RotX : 0f;

        if (view.TryFindLocalRaw(record, GdbView.HashParent, 6, out uint parent, out _))
        {
            result.ParentHash = parent;
        }

        result.ModelPathHashes = CollectModelPathHashes(view, record);
        if (result.ModelPathHashes.Count == 0 && result.ParentHash != 0 &&
            view.TryLookup(result.ParentHash, out int parentRecord))
        {
            result.ModelPathHashes = CollectModelPathHashes(view, parentRecord);
        }

        if (result.ModelPathHashes.Count != 0)
        {
            result.ModelPathHash = result.ModelPathHashes[0];
        }
        else
        {
            // Only when there is nothing — this walks the parent chain a second time, and the
            // answer is only interesting for the placements that came back empty.
            result.StatesAppearance =
                StatesAppearanceComponent(view, record) ||
                (result.ParentHash != 0 &&
                    view.TryLookup(result.ParentHash, out int appearanceParent) &&
                    StatesAppearanceComponent(view, appearanceParent));
        }

        if (TryReadInheritedHash(view, record, GdbHash.SkeletonFile, out uint skeleton))
        {
            result.SkeletonFileHash = skeleton;
        }

        if (TryReadInheritedHash(
                view, record, GdbHash.RetargetSkeletonFile, out uint retarget))
        {
            result.RetargetSkeletonFileHash = retarget;
        }

        placement = result;
        return true;
    }

    private sealed class Transform
    {
        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public bool HasRotation;
        public bool RotationIsQuaternion;
        public float RotW;
        public float Scale = 1f;
    }

    private static bool TryComponentTransformRecord(
        GdbView view, int record, Transform transform, bool lenient)
    {
        return TryComponentTransformField(
                   view, record, GdbHash.TransformComponent, transform, lenient) ||
               TryComponentTransformField(
                   view, record, GdbHash.SimpleTransformComponent, transform, lenient) ||
               TryComponentTransformField(
                   view, record, GdbHash.PhysicsSimulationKeyframedComponent,
                   transform, lenient) ||
               TryComponentTransformField(
                   view, record, GdbHash.PhysicsSimulationStaticComponent,
                   transform, lenient) ||
               TryComponentTransformField(
                   view, record, GdbHash.PhysicsSimulationDynamicComponent,
                   transform, lenient);
    }

    /// <summary>
    /// Looks for a transform in ANY component the record references, not just the five we can
    /// name.
    ///
    /// An entity in this database is a bag of component references — every field on a creature
    /// record is a type-6 reference and none of them is a position. Naming the components we
    /// will look inside is a whitelist, and it fails exactly the way whitelists do: BWSSlums's
    /// childhood scenario has 65 creatures and 47 of them carry their transform in a component
    /// outside the list, so they were counted as "no transform" and never placed. Of the five
    /// components a villager carries, only CreatureComponent (0xC3B90D4F) is even identifiable
    /// by name.
    ///
    /// One level deep and no recursion: a component holds the transform, it does not hold
    /// another entity. Going deeper would start finding other entities' positions.
    /// </summary>
    private static bool TryAnyComponentTransform(
        GdbView view, int record, Transform transform, bool lenient)
    {
        foreach (GdbField field in view.Fields(record))
        {
            if ((byte)field.Type != 6 || field.Raw == 0 ||
                !view.TryLookup(field.Raw, out int componentRecord) ||
                componentRecord == record)
            {
                continue;
            }

            if (TryTransformRecord(view, componentRecord, transform, lenient))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryComponentTransformField(
        GdbView view, int record, uint componentFieldHash, Transform transform, bool lenient)
    {
        if (!view.TryFindLocalRaw(record, componentFieldHash, 6, out uint componentHash, out _) ||
            !view.TryLookup(componentHash, out int componentRecord))
        {
            return false;
        }

        return TryTransformRecord(view, componentRecord, transform, lenient);
    }

    private static bool TryTransformRecord(
        GdbView view, int record, Transform transform, bool lenient)
    {
        if (!view.TryFindFieldOwner(
                record, GdbHash.Position, 6, out GdbField position, out int owner))
        {
            return false;
        }

        if (!view.TryReadVec3Ref(position.Raw, out float x, out float y, out float z))
        {
            return false;
        }

        if (lenient)
        {
            if (!Finite3(x, y, z))
            {
                return false;
            }
        }
        else if (!PlausiblePosition(x, y, z))
        {
            return false;
        }

        transform.X = x;
        transform.Y = y;
        transform.Z = z;

        if (view.TryFindLocalRaw(owner, GdbHash.Rotation, 6, out uint rotationHash, out _) &&
            view.TryReadVec3Ref(rotationHash, out float rx, out float ry, out float rz) &&
            Finite3(rx, ry, rz))
        {
            transform.RotX = rx;
            transform.RotY = ry;
            transform.RotZ = rz;
            transform.HasRotation = true;

            // FABLE 3'S Rotation IS A QUATERNION: `sub_82512D00` reads its X, Y, Z and W, and
            // `sub_82B12A60` takes the four as a quaternion while |W| <= 1.003, converting X/Y/Z as
            // Euler angles only beyond it. Fable 2's record has no W.
            if (Refii.Core.Engine_Title.Active == Refii.Core.Engine_Title.Fable3 &&
                view.TryReadVecWRef(rotationHash, out float rw) && MathF.Abs(rw) <= 1.003f)
            {
                transform.RotW = rw;
                transform.RotationIsQuaternion = true;
            }
        }
        else if (view.TryFindLocalRaw(owner, GdbHash.Rotation, (int)GdbFieldType.Float, out uint rawYaw, out _))
        {
            float yaw = BitConverter.Int32BitsToSingle(unchecked((int)rawYaw));
            if (float.IsFinite(yaw))
            {
                transform.RotX = yaw;
                transform.RotY = 0f;
                transform.RotZ = 0f;
                transform.HasRotation = true;
            }
        }

        const uint HashScale = 0xEC462524;
        if (view.TryFindLocalRaw(owner, HashScale, (int)GdbFieldType.Float, out uint rawScale, out _) ||
            view.TryFindLocalRaw(record, HashScale, (int)GdbFieldType.Float, out rawScale, out _))
        {
            float s = BitConverter.Int32BitsToSingle(unchecked((int)rawScale));
            if (float.IsFinite(s) && s > 0.001f && s <= 100f)
            {
                transform.Scale = s;
            }
        }

        return true;
    }

    private static bool TryIndexedInlineTransform(
        GdbView view, int record, Transform transform)
    {
        if (!view.TryPayloadRange(record, out int start, out int end))
        {
            return false;
        }

        int shim = start >= 4 ? start - 4 : start;
        return TryInlineTransform(view, shim, end, transform);
    }

    private static bool TryInlineTransform(
        GdbView view, int rangeStart, int rangeEnd, Transform transform)
    {
        var candidates = new List<Transform>();

        for (int q = rangeStart + 4; q + 16 <= rangeEnd; q += 4)
        {
            if (!TryReadInlineVec3(view, q, rangeEnd, out float x, out float y, out float z))
            {
                continue;
            }

            if (!PlausiblePosition(x, y, z) || LooksLikeRotationTriplet(x, y, z))
            {
                continue;
            }

            var candidate = new Transform { X = x, Y = y, Z = z };
            candidate.HasRotation = TryFindNearbyInlineRotation(
                view, rangeStart, rangeEnd, q, candidate);

            bool duplicate = false;
            foreach (Transform existing in candidates)
            {
                float dx = existing.X - candidate.X;
                float dy = existing.Y - candidate.Y;
                float dz = existing.Z - candidate.Z;
                if (((dx * dx) + (dy * dy) + (dz * dz)) < 0.01f)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        Transform first = candidates[0];
        transform.X = first.X;
        transform.Y = first.Y;
        transform.Z = first.Z;
        transform.RotX = first.RotX;
        transform.RotY = first.RotY;
        transform.RotZ = first.RotZ;
        transform.HasRotation = first.HasRotation;
        return true;
    }

    private static bool TryFindNearbyInlineRotation(
        GdbView view, int rangeStart, int rangeEnd, int positionOffset, Transform transform)
    {
        int backBegin = positionOffset > rangeStart + 0x60
            ? positionOffset - 0x60
            : rangeStart;

        for (int q = positionOffset; q >= backBegin + 4;)
        {
            q -= 4;
            if (TryReadInlineVec3(view, q, rangeEnd, out float x, out float y, out float z) &&
                LooksLikeRotationTriplet(x, y, z))
            {
                transform.RotX = x;
                transform.RotY = y;
                transform.RotZ = z;
                return true;
            }

            if (q == 0)
            {
                break;
            }
        }

        int forwardEnd = Math.Min(rangeEnd, positionOffset + 0x60);
        for (int q = positionOffset + 4; q + 16 <= forwardEnd; q += 4)
        {
            if (TryReadInlineVec3(view, q, rangeEnd, out float x, out float y, out float z) &&
                LooksLikeRotationTriplet(x, y, z))
            {
                transform.RotX = x;
                transform.RotY = y;
                transform.RotZ = z;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadInlineVec3(
        GdbView view, int at, int rangeEnd, out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;

        if (at + 16 > rangeEnd)
        {
            return false;
        }

        if (view.ReadU32At(at) != GdbHash.InlineVec3SchemaRel)
        {
            return false;
        }

        z = view.ReadF32At(at + 4);
        y = view.ReadF32At(at + 8);
        x = view.ReadF32At(at + 12);
        return Finite3(x, y, z);
    }

    public static bool TryLookupModelPathHashes(
        GdbView view, uint recordHash, out List<uint> hashes, out uint parentHash)
    {
        hashes = new List<uint>();
        parentHash = 0;

        if (recordHash == 0 || !view.TryLookup(recordHash, out int record))
        {
            return false;
        }

        if (view.TryFindLocalRaw(record, GdbView.HashParent, 6, out uint parent, out _))
        {
            parentHash = parent;
        }

        hashes = CollectModelPathHashes(view, record);
        if (hashes.Count == 0 && parentHash != 0 &&
            view.TryLookup(parentHash, out int parentRecord))
        {
            hashes = CollectModelPathHashes(view, parentRecord);
        }

        return hashes.Count != 0;
    }

    public static List<uint> CollectModelPathHashes(GdbView view, int record)
    {
        var hashes = new List<uint>();
        var seen = new HashSet<int>();
        var strippedSlots = new HashSet<uint>();
        int current = record;

        for (int depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current))
            {
                break;
            }

            // Track any components stripped by this node or its descendants
            foreach (GdbField field in view.Fields(current))
            {
                if ((field.Hash == 0x9B41D00A || field.Hash == 0xAD0D6E07) && field.Raw != 0)
                {
                    strippedSlots.Add(field.Raw);
                }
            }

            // Check if current record declares an appearance component directly:
            // 1. Animated mesh component
            if (!strippedSlots.Contains(GdbHash.GraphicAppearanceAnimatedMeshComponent))
            {
                CollectAnimatedMeshModelPathHashes(view, current, hashes);
            }

            // 2. Static mesh component
            if (hashes.Count == 0 && !strippedSlots.Contains(GdbHash.StaticMeshComponent))
            {
                CollectStaticMeshModelPathHashes(view, current, hashes);
            }

            // 3. Static multiple mesh component
            if (hashes.Count == 0 && !strippedSlots.Contains(GdbHash.StaticMultipleMeshComponent))
            {
                CollectStaticMultipleModelPathHashes(view, current, hashes);
            }

            // 4. Direct ModelFile on the record itself
            if (hashes.Count == 0)
            {
                CollectModelFileFields(view, current, hashes);
            }

            // Once a record provides an appearance that resolves models, we stop!
            // Descendants override ancestral appearances.
            if (hashes.Count > 0)
            {
                break;
            }

            if (!view.TryFindLocalRaw(current, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || !view.TryLookup(parent, out int parentRecord))
            {
                break;
            }

            current = parentRecord;
        }

        return hashes;
    }

    /// <summary>
    /// Does this record, or any ancestor, STATE an appearance component at all?
    ///
    /// THE DENOMINATOR FOR "no model hash", which conflated two different facts and so could
    /// not be acted on. A placement with no `ModelFile` is either a trigger, a marker, a sound
    /// emitter or a region volume — content that was never going to draw and whose absence is
    /// correct — or an entity that DOES declare a mesh component whose hash the walk above
    /// failed to reach, which is our defect and a genuinely missing model. Ravenscar reported
    /// 462 of them as one number, so there was no way to tell how many were real.
    ///
    /// The test is the presence of the FIELD, not of anything it resolves to: the four
    /// collectors each begin by looking one of these up, so a record carrying none of them
    /// gives them nothing to do and its empty result is the honest answer rather than a
    /// dead end. Run only when the collection came back empty, so the common path is unchanged.
    /// </summary>
    public static bool StatesAppearanceComponent(GdbView view, int record)
    {
        var seen = new HashSet<int>();
        int current = record;

        for (int depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current))
            {
                return false;
            }

            if (view.TryFindLocalRaw(current, GdbHash.StaticMeshComponent, 6, out _, out _) ||
                view.TryFindLocalRaw(current, GdbHash.GraphicAppearanceAnimatedMeshComponent, 6, out _, out _) ||
                view.TryFindLocalRaw(current, GdbHash.StaticMultipleMeshComponent, 6, out _, out _) ||
                view.TryFindLocalRaw(current, GdbHash.GraphicAppearanceComponent, 6, out _, out _))
            {
                return true;
            }

            if (!view.TryFindLocalRaw(current, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || !view.TryLookup(parent, out int parentRecord))
            {
                return false;
            }

            current = parentRecord;
        }

        return false;
    }

    private static void AppendUnique(List<uint> hashes, uint hash)
    {
        if (hash == 0 || hash == GdbHash.Null || hashes.Contains(hash))
        {
            return;
        }

        hashes.Add(hash);
    }

    private static bool TryReadModelPathHashField(
        GdbView view, int record, uint fieldHash, out uint hash)
    {
        hash = 0;
        if (!view.TryFindLocalRaw(record, fieldHash, -1, out uint raw, out GdbFieldType type))
        {
            return false;
        }

        if (type is not (GdbFieldType.String or GdbFieldType.Float))
        {
            return false;
        }

        hash = raw;
        return hash != 0 && hash != GdbHash.Null;
    }

    private static bool TryReadInheritedHash(
        GdbView view, int record, uint fieldHash, out uint hash)
    {
        hash = 0;
        if (!view.TryFindFieldOwner(record, fieldHash, -1, out GdbField field, out _))
        {
            return false;
        }

        if (field.Type is not (GdbFieldType.Float or GdbFieldType.String or GdbFieldType.Reference))
        {
            return false;
        }

        hash = field.Raw;
        return hash != 0 && hash != GdbHash.Null;
    }

    private static void CollectModelFileFields(GdbView view, int record, List<uint> hashes)
    {
        if (TryReadModelPathHashField(view, record, GdbHash.ModelFile, out uint a))
        {
            AppendUnique(hashes, a);
        }

        if (TryReadModelPathHashField(view, record, GdbHash.ModelFile1, out uint b))
        {
            AppendUnique(hashes, b);
        }

        if (TryReadModelPathHashField(view, record, GdbHash.ModelFile2, out uint c))
        {
            AppendUnique(hashes, c);
        }
    }

    private static void CollectModelFileFieldsAndParents(
        GdbView view, int record, List<uint> hashes)
    {
        var seen = new HashSet<int>();
        int current = record;

        for (int depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current))
            {
                return;
            }

            int before = hashes.Count;
            bool hasModelFileField =
                view.TryFindLocalRaw(current, GdbHash.ModelFile, -1, out _, out _) ||
                view.TryFindLocalRaw(current, GdbHash.ModelFile1, -1, out _, out _) ||
                view.TryFindLocalRaw(current, GdbHash.ModelFile2, -1, out _, out _);

            CollectModelFileFields(view, current, hashes);
            if (hasModelFileField || hashes.Count != before)
            {
                return;
            }

            if (!view.TryFindLocalRaw(current, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || !view.TryLookup(parent, out int parentRecord))
            {
                return;
            }

            current = parentRecord;
        }
    }

    private static void CollectMeshRecordModelPathHashes(
        GdbView view, int meshRecord, List<uint> hashes)
    {
        CollectModelFileFieldsAndParents(view, meshRecord, hashes);

        if (!view.TryFindFieldOwner(
                meshRecord, GdbHash.StaticMultipleModelFile, -1,
                out GdbField mesh, out _))
        {
            return;
        }

        if (mesh.Raw == 0)
        {
            return;
        }

        if (mesh.Type == GdbFieldType.Reference)
        {
            if (view.TryLookup(mesh.Raw, out int modelRecord))
            {
                CollectModelFileFieldsAndParents(view, modelRecord, hashes);
            }

            AppendUnique(hashes, mesh.Raw);
        }
        else if (mesh.Type is GdbFieldType.Float or GdbFieldType.String)
        {
            AppendUnique(hashes, mesh.Raw);
        }
    }

    private static void CollectStaticMultipleSlotModelPathHashes(
        GdbView view, int listRecord, uint slotHash, List<uint> hashes)
    {
        foreach (GdbField field in view.Fields(listRecord))
        {
            if (field.Hash != slotHash || field.Type != GdbFieldType.Reference ||
                field.Raw == 0)
            {
                continue;
            }

            if (view.TryLookup(field.Raw, out int itemRecord))
            {
                CollectMeshRecordModelPathHashes(view, itemRecord, hashes);
            }
        }
    }

    private static void CollectStaticMultipleFallbackModelPathHashes(
        GdbView view, int listRecord, List<uint> hashes)
    {
        foreach (GdbField field in view.Fields(listRecord))
        {
            if (field.Hash == GdbView.HashParent ||
                field.Type != GdbFieldType.Reference || field.Raw == 0)
            {
                continue;
            }

            if (view.TryLookup(field.Raw, out int itemRecord))
            {
                CollectMeshRecordModelPathHashes(view, itemRecord, hashes);
            }
        }
    }

    private static void CollectStaticMeshModelPathHashes(
        GdbView view, int record, List<uint> hashes)
    {
        if (!view.TryFindLocalRaw(
                record, GdbHash.StaticMeshComponent, 6, out uint modelRaw, out _) ||
            !view.TryLookup(modelRaw, out int modelRecord))
        {
            return;
        }

        CollectModelFileFieldsAndParents(view, modelRecord, hashes);
    }

    private static readonly uint UseDisplacementHash = Refii.Core.Engine_GameHash.Fnv1("UseDisplacement");

    /// <summary>
    /// Whether a placement's static-mesh component states UseDisplacement (inherited): the guest
    /// then makes its mesh a displacement mesh (`sub_822370A8` 0x8223722C ORs 0x800), drawn only
    /// into the displacement target. Within <paramref name="view"/>.
    /// </summary>
    public static bool UsesDisplacement(GdbView view, uint recordHash)
    {
        if (!view.TryLookup(recordHash, out int record) ||
            !view.TryFindFieldOwner(record, GdbHash.StaticMeshComponent, 6, out GdbField component, out _) ||
            !view.TryLookup(component.Raw, out int componentRecord))
        {
            return false;
        }
        bool? stated = null;
        VisitRecordAndParents(view, componentRecord, r =>
        {
            if (stated is null && view.TryFindLocalRaw(r, UseDisplacementHash, (int)GdbFieldType.Bool, out uint raw, out _))
            {
                stated = raw != 0;
            }
        });
        return stated == true;
    }

    private static void VisitRecordAndParents(GdbView view, int record, Action<int> visit)
    {
        var seen = new HashSet<int>();
        int current = record;

        for (int depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current))
            {
                return;
            }

            visit(current);

            if (!view.TryFindLocalRaw(current, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || !view.TryLookup(parent, out int parentRecord))
            {
                return;
            }

            current = parentRecord;
        }
    }

    private static void CollectAnimatedMeshModelPathHashes(
        GdbView view, int record, List<uint> hashes)
    {
        if (!view.TryFindLocalRaw(
                record, GdbHash.GraphicAppearanceAnimatedMeshComponent, 6,
                out uint modelRaw, out _) ||
            !view.TryLookup(modelRaw, out int modelRecord))
        {
            return;
        }

        CollectModelFileFieldsAndParents(view, modelRecord, hashes);
    }

    private static void CollectStaticMultipleComponentModelPathHashes(
        GdbView view, int componentRecord, List<uint> hashes)
    {
        if (!view.TryFindFieldOwner(
                componentRecord, GdbHash.StaticMultipleModelList, 6,
                out GdbField list, out _) ||
            !view.TryLookup(list.Raw, out int listRecord))
        {
            return;
        }

        CollectStaticMultipleSlotModelPathHashes(
            view, listRecord, GdbHash.StaticMultipleModelSlotA, hashes);
        CollectStaticMultipleSlotModelPathHashes(
            view, listRecord, GdbHash.StaticMultipleModelSlotD, hashes);
        CollectStaticMultipleFallbackModelPathHashes(view, listRecord, hashes);
    }

    private static void CollectStaticMultipleModelPathHashes(
        GdbView view, int record, List<uint> hashes)
    {
        if (!view.TryFindLocalRaw(
                record, GdbHash.StaticMultipleMeshComponent, 6,
                out uint componentRaw, out _) ||
            !view.TryLookup(componentRaw, out int componentRecord))
        {
            return;
        }

        CollectStaticMultipleComponentModelPathHashes(view, componentRecord, hashes);
    }

    public static bool Finite3(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);

    public static bool PlausiblePosition(float x, float y, float z) =>
        Finite3(x, y, z) &&
        x >= -256f && x <= 768f &&
        y >= -256f && y <= 768f &&
        z >= -256f && z <= 512f &&
        (MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z)) > 1f;

    public static bool LooksLikeRotationTriplet(float x, float y, float z)
    {
        if (!Finite3(x, y, z))
        {
            return false;
        }

        if (MathF.Abs(x) > Pi + 0.25f ||
            MathF.Abs(y) > Pi + 0.25f ||
            MathF.Abs(z) > Pi + 0.25f)
        {
            return false;
        }

        if ((MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z)) < 1.0e-5f)
        {
            return true;
        }

        return (MathF.Abs(y) < 0.02f && MathF.Abs(z) < 0.02f) ||
               (MathF.Abs(y + Pi) < 0.02f && MathF.Abs(z + Pi) < 0.02f) ||
               (MathF.Abs(y - Pi) < 0.02f && MathF.Abs(z - Pi) < 0.02f);
    }
}
