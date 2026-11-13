using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// The levers on RIGID entity props (crates, stall parts, bottles, railings: an entity node with no
/// rig, GameEntities' unrigged set, and the entity batches that stand for the still ones):
/// `android_entity_batches`, `android_entity_batch_cell_metres`, `android_entity_prop_lod_bias` and
/// `android_entity_prop_distance_scale`. Characters are left alone (AndroidCharacterLevers has theirs).
/// </summary>
/// <remarks>
/// <para>WHAT THEY COST. Clocked on the S10e (Low, lean modern, Bowerstone Market by day, base about
/// 15.0 Mcycles a frame): no entity drawn -3.3 Mcycles (-22%), no character body drawn -1.8 (-12%),
/// so the rigid props about -1.5 (10%) for about 130 draws and 170K triangles in view. Low renders no
/// shadow pass (scalls 0), so that is the main pass alone, and at about 12 cycles a triangle on the
/// Adreno 640 (Engine_MdlAutoLod) their triangles account for all of it: the cost is the vertices,
/// not the draws (Mobile issues one draw a surface whatever is batched, a few microseconds of GPU
/// each) nor the pixels (a 40% render scale). Their materials are the level props' own (the feed
/// resolves the same meshes through Engine_PropStreamer.Resolve), so the lite builds, the material
/// folds, the near ground AO and the normal-map mode are already the props'.</para>
/// <para>`android_entity_batches`: whether still rigid props are drawn from Engine_EntityBatches.
/// <c>auto</c> (default) is what the phone did before: off under <c>android_lean_modern</c>, the
/// shared default (on) otherwise. <c>on</c> is upstream's batching, 64 m cells; <c>cells</c> the same
/// batching in <see cref="CellKey"/> cells. WHY BATCHES COST TRIANGLES: Godot Mobile picks one level
/// for a MultiMesh from the camera to the nearest point of its box, and measures its visibility
/// range to the box's centre (render_forward_mobile.cpp `_fill_render_list`), so a 64 m cell draws
/// every member at its near corner's level and to its range plus up to ~45 m of spread (the props'
/// size-tiered cull gets the same widening in Engine_PropStreamer.ApplyRange). That was the S10e's
/// 190K more triangles and 6-7 ms of GPU; Godot's primitive counter adds a LOD'd MultiMesh's
/// triangles once, not per copy, so the true figure was higher. An 8 m cell spreads ~6 m: the
/// members take nearly their own levels and ranges, and a cell pairs only the lamps reaching that
/// square, about what its members pair one by one. A batch draws no new pipeline (Mobile's multimesh flag is a
/// branch in the instance data) and per-instance values are the batch's: under the modern model
/// instance lighting is the theme's and the batch key holds the layers (and so the lamp zone).
/// Live: a change empties every batch first, then batches again at the new size.</para>
/// <para>`android_entity_prop_lod_bias`: Godot's lod_bias on rigid props and their batches (1 keeps
/// `mesh_lod_pixels`'s rule; 0.5 switches each level at half the distance). Generated levels end at
/// 3% of a model's extent (mesh_auto_lod), which a prop reaches at about four times its size, so this
/// moves the near props, and `mesh_auto_lod_far` the far ones.</para>
/// <para>`android_entity_prop_distance_scale`: a rigid prop's stated draw distance and its size-tiered
/// cull (prop_cull_distance_scale's tiers), times this; characters keep `entity_draw_distance_scale`'s.
/// The per-copy dither moves with it. 0.75 on `entity_draw_distance_scale` measured -8% for both.</para>
/// <para>The two prop levers are live (every rigid prop and batch on the next frame); nothing on the desktop.</para>
/// </remarks>
public static class AndroidEntityProps
{
    public const string BatchesKey = "android_entity_batches";

    public const string CellKey = "android_entity_batch_cell_metres";

    public const string LodBiasKey = "android_entity_prop_lod_bias";

    public const string DistanceKey = "android_entity_prop_distance_scale";

    public const string Auto = "auto";
    public const string Off = "off";
    public const string On = "on";
    public const string Cells = "cells";

    /// <summary>The cell `cells` batches by unless set.</summary>
    public const float DefaultCellMetres = 8f;

    public const float MinCellMetres = 4f;

    /// <summary>Upstream's own cell: past it `cells` is `on`.</summary>
    public const float MaxCellMetres = Refii.Level.Engine_LevelProps.BatchCellMetres * 2f;

    /// <summary>What a stated mode reads as; anything else is <see cref="Auto"/>. Pure.</summary>
    public static string Mode(IReadOnlyDictionary<string, string> values)
    {
        string mode = Engine_RefiiConfig.String(values, BatchesKey, Auto).ToLowerInvariant();
        return mode is Off or On or Cells ? mode : Auto;
    }

    /// <summary>The cell size the configuration asks for, held to <see cref="MinCellMetres"/>..<see cref="MaxCellMetres"/>. Pure.</summary>
    public static float WantedCell(IReadOnlyDictionary<string, string> values)
    {
        float metres = Engine_RefiiConfig.Float(values, CellKey, DefaultCellMetres);
        return float.IsFinite(metres) && metres > 0f ? Math.Clamp(metres, MinCellMetres, MaxCellMetres) : DefaultCellMetres;
    }

    /// <summary>The bias the configuration asks for: 1 unless set, held to 0.1..4. Pure.</summary>
    public static float WantedBias(IReadOnlyDictionary<string, string> values)
    {
        float bias = Engine_RefiiConfig.Float(values, LodBiasKey, 1f);
        return float.IsFinite(bias) && bias > 0f ? Math.Clamp(bias, 0.1f, 4f) : 1f;
    }

    /// <summary>The distance scale the configuration asks for: 1 unless set, held to 0.25..2. Pure.</summary>
    public static float WantedDistance(IReadOnlyDictionary<string, string> values)
    {
        float scale = Engine_RefiiConfig.Float(values, DistanceKey, 1f);
        return float.IsFinite(scale) && scale > 0f ? Math.Clamp(scale, 0.25f, 2f) : 1f;
    }

    /// <summary>
    /// Whether batches are on and the cell they key by, for a mode and the lean-modern state:
    /// auto is off under lean modern and upstream's otherwise; on is upstream's 64 m. Pure.
    /// </summary>
    public static (bool Enabled, float CellMetres) Batching(string mode, bool leanModern, float cellMetres) => mode switch
    {
        Off => (false, MaxCellMetres),
        On => (true, MaxCellMetres),
        Cells => (true, cellMetres),
        _ => (!leanModern, MaxCellMetres),
    };

    private static float _bias = float.NaN, _distance = float.NaN;
    private static bool _hooked, _rangesOwed, _batchingOwed;
    private static (bool Enabled, float CellMetres)? _pendingBatching;
    private static ulong _tickedFrame = ulong.MaxValue;

    /// <summary>The rigid props' lod_bias in force: 1 off the phone.</summary>
    public static float LodBias
    {
        get
        {
            Load();
            return _bias;
        }
    }

    /// <summary>The rigid props' distance scale in force: 1 off the phone.</summary>
    public static float DistanceScale
    {
        get
        {
            Load();
            return _distance;
        }
    }

    private static void Load()
    {
        if (!float.IsNaN(_bias) && !float.IsNaN(_distance))
        {
            return;
        }

        float bias = 1f, distance = 1f;
        if (Engine_Platform.IsAndroid)
        {
            try
            {
                IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
                bias = WantedBias(values);
                distance = WantedDistance(values);
            }
            catch (Exception)
            {
                (bias, distance) = (1f, 1f);
            }
        }

        _bias = bias;
        _distance = distance;
        Hook();
    }

    private static void Hook()
    {
        if (_hooked || !Engine_Platform.IsAndroid)
        {
            return;
        }

        _hooked = true;
        Engine_RefiiConfig.OnConfigChanged += (name, _) =>
        {
            if (string.Equals(name, LodBiasKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, DistanceKey, StringComparison.OrdinalIgnoreCase))
            {
                _bias = _distance = float.NaN;
                _rangesOwed = true;
            }
            else if (string.Equals(name, BatchesKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, CellKey, StringComparison.OrdinalIgnoreCase))
            {
                _batchingOwed = true;
            }
        };
    }

    /// <summary>
    /// The batching the configuration asks for, at start (AndroidGameHost, where lean modern used to
    /// turn batches off). Main thread.
    /// </summary>
    public static void ApplyBatching()
    {
        if (!Engine_Platform.IsAndroid)
        {
            return;
        }

        (bool enabled, float cell) = WantedBatching();
        Refii.Entities.Engine_EntityBatches.Enabled = enabled;
        Refii.Entities.Engine_EntityBatches.CellMetres = cell;
        Hook();
    }

    private static (bool Enabled, float CellMetres) WantedBatching()
    {
        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            return Batching(Mode(values), AndroidLeanModern.Fx, WantedCell(values));
        }
        catch (Exception)
        {
            return Batching(Auto, AndroidLeanModern.Fx, DefaultCellMetres);
        }
    }

    /// <summary>
    /// A rigid prop's (or its batch's) range and level bias: the shared range rule at
    /// <see cref="DistanceScale"/>, then <see cref="LodBias"/> on it and its cast-only twin. On the
    /// desktop exactly the shared rule. Main thread.
    /// </summary>
    public static void ApplyVisibilityRange(GeometryInstance3D node, Mesh mesh)
    {
        Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh, DistanceScale);
        if (!Engine_Platform.IsAndroid)
        {
            return;
        }

        float bias = LodBias;
        if (node.LodBias != bias)
        {
            node.LodBias = bias;
        }

        if (node.GetNodeOrNull<GeometryInstance3D>(Refii.Entities.Engine_ShadowProxy.NodeName) is { } twin && twin.LodBias != bias)
        {
            twin.LodBias = bias;
        }
    }

    /// <summary>One frame's catch-up after a live change, from the host's per-frame tick. Main thread.</summary>
    public static void Tick()
    {
        if (!_rangesOwed && !_batchingOwed && _pendingBatching is null)
        {
            return;
        }

        ulong frame = Engine.GetProcessFrames();
        if (frame == _tickedFrame)
        {
            return;
        }

        _tickedFrame = frame;
        if (_rangesOwed)
        {
            _rangesOwed = false;
            Refii.Game.GameEntities.Current?.UpdateAllVisibilityRanges();
        }

        if (_batchingOwed)
        {
            _batchingOwed = false;
            (bool enabled, float cell) = WantedBatching();
            if (!enabled || Refii.Entities.Engine_EntityBatches.Batched == 0 ||
                cell == Refii.Entities.Engine_EntityBatches.CellMetres)
            {
                // Off (the feed empties the batches at its next walk), or nothing batched at the old size.
                Refii.Entities.Engine_EntityBatches.Enabled = enabled;
                Refii.Entities.Engine_EntityBatches.CellMetres = cell;
                _pendingBatching = null;
            }
            else
            {
                // A new size: every prop back to its node first, then batched again at the new size.
                Refii.Entities.Engine_EntityBatches.Enabled = false;
                _pendingBatching = (enabled, cell);
            }
        }
        else if (_pendingBatching is { } pending && Refii.Entities.Engine_EntityBatches.Batched == 0)
        {
            Refii.Entities.Engine_EntityBatches.CellMetres = pending.CellMetres;
            Refii.Entities.Engine_EntityBatches.Enabled = pending.Enabled;
            _pendingBatching = null;
        }
    }

    /// <summary>For `charcensus`: the rigid levers and the batches standing.</summary>
    public static Dictionary<string, object?> Census()
    {
        string mode = Auto;
        try
        {
            mode = Mode(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
        }

        return new Dictionary<string, object?>
        {
            [BatchesKey] = mode,
            ["batchesEnabled"] = Refii.Entities.Engine_EntityBatches.Enabled,
            [CellKey] = Refii.Entities.Engine_EntityBatches.CellMetres,
            ["batched"] = Refii.Entities.Engine_EntityBatches.Batched,
            ["batches"] = Refii.Entities.Engine_EntityBatches.Batches,
            ["promotions"] = Refii.Entities.Engine_EntityBatches.Promotions,
            ["demotions"] = Refii.Entities.Engine_EntityBatches.Demotions,
            [LodBiasKey] = LodBias,
            [DistanceKey] = DistanceScale,
        };
    }
}
