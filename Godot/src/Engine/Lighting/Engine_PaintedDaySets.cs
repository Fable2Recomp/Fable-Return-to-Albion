using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// The region's PAINTED day sets: the environment grid the guest samples at the hero, and the
/// layer weights it blends those day sets in with.
/// </summary>
/// <remarks>
/// THE CONTRACT, from the binary (verified 2026-09-05). The theme is a layer stack, not one day
/// set per level: <c>sub_82547950</c> reads ONE level-wide day set from the first LevelData in
/// load order at full load, and on top of it the level's <c>.genv</c> grids paint a day-set record
/// hash per 4 m world cell. Every tick <c>sub_821ABFD8</c> samples the grid at the HERO's x,y
/// (<c>sub_822D6A20</c>: level bounds at level+152..164, then the tile whose int bounds at
/// tile+28..40 contain it; <c>sub_822D6BC8</c>: cell = (y − minY)/cell × width + (x − minX)/cell,
/// read from the u32 vector at tile+16; 0xFFFFFFFF = unpainted). The set under the hero is the
/// active layer; each layer's weight moves by target/(BlendTime × 15) per 15 Hz tick — up on
/// PaintedThemeBlendInTime, down on PaintedThemeBlendOutTime, both LevelData fields defaulting
/// to 5 s — and a layer is erased at zero. <c>sub_8218EAF8</c> then lerps each painted layer over
/// the level day set by its weight. This is how a tavern inside Bowerstone Market gets its own
/// ambient: its cells are painted (the market paints three day sets over 243 cells, one of them
/// a 16 × 12 m patch), and there is no interior flag anywhere.
///
/// File layout (the asset browser writes the same): the heightfield's 12-byte header (f32 origin
/// x/y first), be-u32 span x/y, be-u32 width/height, f32 cell size, then width × height be-u32
/// hashes. The guest reads the origin and span from the file itself (<c>sub_82354A98</c>: tile
/// bounds = (int)origin .. (int)origin + span) and the body with <c>sub_8265EF98</c>; the grid
/// spans the heightfield from its origin, one cell per 4 m.
///
/// FABLE 3 PAINTS A SECOND GRID. Its reader (<c>sub_8243D410</c> → <c>sub_828E3CA0</c>) takes the
/// same header and then TWO width × height be-u32 grids — the first into the vector at tile+12,
/// the second into the one at tile+28 — and its sampler (<c>sub_82298098</c>) indexes the SECOND
/// (data at tile+32) with the same cell arithmetic, bounds at tile+44..56. The second grid is the
/// day-set hash grid; the first is read but not sampled by the painted path. The file states no
/// version, so the title says which layout it is. A width or height of 0 is a tile that paints
/// nothing: its bounds still claim the span, so the sampler answers unpainted there (six Fable 3
/// files, the tabletop vistas among them).
/// </remarks>
public sealed class Engine_PaintedDaySets
{
    public const uint None = 0xFFFFFFFFu;

    public const float DefaultBlendSeconds = 5.0f;

    /// <summary>One grid tile, in Godot world X/Z (the guest's x/y map straight onto them).</summary>
    public sealed class Grid
    {
        public float OriginX;
        public float OriginZ;
        public int SpanX;
        public int SpanZ;
        public float Cell = 4.0f;
        public int Width;
        public int Height;

        /// <summary>The day-set hash per cell: the grid the guest samples.</summary>
        public uint[] Cells = Array.Empty<uint>();

        /// <summary>Fable 3's first grid (tile+12), read by the guest but not sampled for day sets.</summary>
        public uint[] LeadCells = Array.Empty<uint>();

        public float MaxX => OriginX + SpanX;

        public float MaxZ => OriginZ + SpanZ;

        /// <summary>The guest's tile bounds test: ints, (int)origin up to (int)origin + span.</summary>
        public bool Contains(float x, float z)
        {
            int ix = (int)x;
            int iz = (int)z;
            int minX = (int)OriginX;
            int minZ = (int)OriginZ;
            return ix >= minX && ix < minX + SpanX && iz >= minZ && iz < minZ + SpanZ;
        }

        public uint Sample(float x, float z)
        {
            // The guest truncates the hero's coordinates to ints before the bounds test and the
            // cell division; inside the bounds the offsets are non-negative, so truncation is floor.
            if (!Contains(x, z))
            {
                return None;
            }

            int cell = ((int)(((int)z - (int)OriginZ) / Cell) * Width) + (int)(((int)x - (int)OriginX) / Cell);
            if (cell < 0 || cell >= Cells.Length)
            {
                return None;
            }

            // Hash 0 names no day set: the guest resolves none for it, so it is unpainted, not a
            // layer. Fable 2 paints no 0; Fable 3 has 35,737 such cells across 32 files.
            uint hash = Cells[cell];
            return hash == 0u ? None : hash;
        }
    }

    public sealed class Layer
    {
        public uint Hash;
        public float Weight;
        public bool Active;

        /// <summary>
        /// The layer's own fade times, when its theme states them (Fable 3): <c>sub_82261768</c>
        /// ramps the entered theme up over its FadeInTime (theme+16, 0x82261a98) and the one it
        /// leaves down over its FadeOutTime (theme+20, 0x82261970). Null takes the level's.
        /// </summary>
        public float? BlendInSeconds;
        public float? BlendOutSeconds;
    }

    private readonly List<Grid> _grids = new();

    private readonly List<Layer> _layers = new();

    public float BlendInSeconds = DefaultBlendSeconds;

    public float BlendOutSeconds = DefaultBlendSeconds;

    public IReadOnlyList<Layer> Layers => _layers;

    public bool Any => _layers.Count != 0;

    public int GridCount => _grids.Count;

    public int PaintedCells { get; private set; }

    private readonly Dictionary<uint, int> _cellCounts = new();

    /// <summary>Cell counts per painted day-set hash across all added grids.</summary>
    public IReadOnlyDictionary<uint, int> CellCounts => _cellCounts;

    public float MaxWeight
    {
        get
        {
            float max = 0f;
            foreach (Layer layer in _layers)
            {
                max = MathF.Max(max, layer.Weight);
            }

            return max;
        }
    }

    /// <summary>How many width × height grids follow the header: Fable 3 paints two, the day sets second.</summary>
    public static int GridsFor(Refii.Core.Engine_Title title) =>
        title == Refii.Core.Engine_Title.Fable3 ? 2 : 1;

    /// <summary>One .genv at the origin the file states, in the active title's layout.</summary>
    public static bool TryParse(byte[] bytes, out Grid grid, out string error) =>
        TryParse(bytes, Refii.Core.Engine_Title.Active, out grid, out error);

    public static bool TryParse(byte[] bytes, Refii.Core.Engine_Title title, out Grid grid, out string error)
    {
        grid = new Grid();
        error = string.Empty;
        if (bytes.Length < 32)
        {
            error = $"{bytes.Length} bytes is shorter than the 32-byte header";
            return false;
        }

        float originX = BitConverter.Int32BitsToSingle((int)ReadU32(bytes, 0));
        float originZ = BitConverter.Int32BitsToSingle((int)ReadU32(bytes, 4));
        int spanX = (int)ReadU32(bytes, 12);
        int spanZ = (int)ReadU32(bytes, 16);
        int width = (int)ReadU32(bytes, 20);
        int height = (int)ReadU32(bytes, 24);
        float cell = BitConverter.Int32BitsToSingle((int)ReadU32(bytes, 28));
        int grids = GridsFor(title);
        if (!float.IsFinite(originX) || !float.IsFinite(originZ) || spanX < 0 || spanZ < 0)
        {
            error = $"implausible origin ({originX}, {originZ}) or span {spanX}x{spanZ}";
            return false;
        }

        if (width == 0 || height == 0)
        {
            grid = new Grid { OriginX = originX, OriginZ = originZ, SpanX = spanX, SpanZ = spanZ };
            return true;
        }

        long count = (long)width * height;
        long expected = 32 + (4L * grids * count);
        if (width < 0 || height < 0 || width > 4096 || height > 4096 || !(cell > 0f) ||
            bytes.Length < expected)
        {
            error = $"grid {width}x{height} x{grids} cell {cell} needs {expected} bytes, file has {bytes.Length}";
            return false;
        }

        uint[] ReadGrid(int index)
        {
            var values = new uint[count];
            int at = 32 + (int)(4L * index * count);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ReadU32(bytes, at + (4 * i));
            }

            return values;
        }

        grid = new Grid
        {
            OriginX = originX, OriginZ = originZ, SpanX = spanX, SpanZ = spanZ, Cell = cell,
            Width = width, Height = height, Cells = ReadGrid(grids - 1),
            LeadCells = grids > 1 ? ReadGrid(0) : Array.Empty<uint>(),
        };
        return true;
    }

    public void Add(Grid grid)
    {
        _grids.Add(grid);
        foreach (uint cell in grid.Cells)
        {
            if (cell != None)
            {
                PaintedCells++;
                _cellCounts[cell] = _cellCounts.TryGetValue(cell, out int n) ? n + 1 : 1;
            }
        }
    }

    /// <summary>
    /// Seeds the initial active painted layer when the region first stands up: if a single painted
    /// day set covers the majority (>= 50%) of all painted cells in the region (such as Wraithmarsh's
    /// <c>EnvThemeWraithMarsh_DaySet</c> at 95.6% or Bowerstone Market's <c>EnvThemeTownBowerstone_DaySet</c>
    /// at 86.0%), starts it at weight 1.0 so the region immediately has its painted atmosphere and
    /// lighting even before the hero's position is sampled.
    /// </summary>
    public void SeedDominantLayer()
    {
        if (_layers.Count != 0 || PaintedCells <= 0)
        {
            return;
        }

        uint bestHash = None;
        int bestCount = 0;
        foreach ((uint hash, int count) in _cellCounts)
        {
            if (count > bestCount)
            {
                bestCount = count;
                bestHash = hash;
            }
        }

        if (bestHash != None && bestCount * 2 >= PaintedCells)
        {
            _layers.Add(new Layer { Hash = bestHash, Weight = 1.0f, Active = true });
        }
    }

    public uint Sample(Vector3 hero)
    {
        foreach (Grid grid in _grids)
        {
            uint hash = grid.Sample(hero.X, hero.Z);
            if (hash != None)
            {
                return hash;
            }

            // The guest returns the FIRST tile whose bounds contain the point, painted or not.
            if (grid.Contains(hero.X, hero.Z))
            {
                return None;
            }
        }

        return None;
    }

    /// <summary>
    /// One tick of the guest's blend at a real delta. Returns how much the weights moved,
    /// plus 1.0 for every layer added or erased, so the caller can re-evaluate in steps.
    /// </summary>
    public float Tick(Vector3? hero, float deltaSeconds)
    {
        float moved = 0f;
        foreach (Layer layer in _layers)
        {
            layer.Active = false;
        }

        if (hero is { } at)
        {
            uint here = Sample(at);
            if (here != None)
            {
                Layer? found = null;
                foreach (Layer layer in _layers)
                {
                    if (layer.Hash == here)
                    {
                        found = layer;
                        break;
                    }
                }

                if (found is null)
                {
                    found = new Layer { Hash = here };
                    _layers.Add(found);
                    moved += 1f;
                    GD.Print($"[painted] entering day set 0x{here:X8} at ({at.X:0.0}, {at.Z:0.0})");
                }

                found.Active = true;
            }
        }

        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            Layer layer = _layers[i];
            float stepIn = deltaSeconds / MathF.Max(layer.BlendInSeconds ?? BlendInSeconds, 1e-3f);
            float stepOut = deltaSeconds / MathF.Max(layer.BlendOutSeconds ?? BlendOutSeconds, 1e-3f);
            float before = layer.Weight;
            layer.Weight = layer.Active
                ? MathF.Min(layer.Weight + stepIn, 1f)
                : MathF.Max(layer.Weight - stepOut, 0f);
            moved += MathF.Abs(layer.Weight - before);
            if (!layer.Active && layer.Weight <= 0f)
            {
                GD.Print($"[painted] day set 0x{layer.Hash:X8} faded out");
                _layers.RemoveAt(i);
                moved += 1f;
            }
        }

        return moved;
    }

    public string Describe()
    {
        var parts = new List<string>();
        foreach (Layer layer in _layers)
        {
            parts.Add($"0x{layer.Hash:X8}={layer.Weight:0.00}{(layer.Active ? "*" : string.Empty)}");
        }

        return $"grids={_grids.Count} painted={PaintedCells} layers=[{string.Join(" ", parts)}] " +
            $"blend in/out={BlendInSeconds:0.#}/{BlendOutSeconds:0.#}s";
    }

    private static uint ReadU32(byte[] d, int at) =>
        ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];
}
