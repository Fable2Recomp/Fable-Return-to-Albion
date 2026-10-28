using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;

namespace Refii.Models;

/// <summary>
/// Still casters of DIFFERENT models drawn into the classic cascades as one mesh per cell.
/// </summary>
/// <remarks>
/// A cascade keeps only depth, and a plain opaque surface's triangles write the same depth
/// whichever model they belong to, so the draw per model per cascade buys nothing but render
/// thread time. A member is a static caster whose every surface Engine_ClassicCasterMerge joins
/// (<see cref="Engine_ClassicCasterMerge.CellGeometry"/>); cells are <see cref="CellSize"/>
/// square in the ground plane and keyed by the caster's cascade layers, each one surface per
/// cull mode, positions only. Levels of detail merge as Engine_ShadowProxy.MergedLods does: a
/// level is taken only where every member allows it, so no member casts coarser than alone.
///
/// A member keeps its own cut and visibility: hidden, its vertices in the cell are written to
/// one point (zero area, nothing rasterised); shown, they are written back. A prop batch joins whole,
/// each copy placed by its built transform (Engine_PropShadow's record), and leaves whole when any
/// copy is moved or hidden after the build. Its own instance
/// draws until a build including it is in, and again once it leaves the cell (it moved, its
/// mesh changed, its node left). Builds run on a worker once a cell's members stop arriving.
/// </remarks>
public static class Engine_ClassicCasterCells
{
    private const float CellSize = 32f;

    /// <summary>Frames without a new member before a cell is built, and the longest a dirty cell waits.</summary>
    private const int QuietFrames = 15, StaleFrames = 120;

    /// <summary>Bytes per vertex of a positions-only surface; a build with any other stride is not used.</summary>
    private const int Stride = 12;

    internal sealed class Member
    {
        internal required Rid Own;
        /// <summary>The surfaces left out of the cell (Engine_ClassicCasterMerge.KeptOnly): what the own instance draws while in it; none when all joined.</summary>
        internal Rid Kept;
        internal required Engine_ClassicCasterMerge.Group?[] Geometry;
        internal required Transform3D World;
        /// <summary>A batch's instance transforms, in the batch's space, as built; null for one model.</summary>
        internal Transform3D[]? Instances;
        internal required Cell Cell;
        internal bool Shown;
        internal bool Left;

        /// <summary>A build that holds it is in: the cell draws its joined surfaces, its own instance only the kept ones.</summary>
        public bool Built { get; internal set; }

        /// <summary>Whether its own instance still draws something while it is in a cell.</summary>
        public bool HasKept => Kept.IsValid;

        /// <summary>First vertex per cull surface in the cell's current mesh.</summary>
        internal readonly int[] Start = new int[3];
    }

    internal sealed class Cell
    {
        public required (int X, int Z, uint Layers) Key;
        public required Vector3 Origin;
        public readonly List<Member> Members = new();
        public Rid Instance;
        public ArrayMesh? Mesh;
        public readonly int[] SurfaceOf = { -1, -1, -1 };
        public long Dirty = -1, FirstDirty = -1;
        public Task<Result?>? Building;
        public List<(Member Member, bool Shown)>? Snapshot;
        public int ShownBuilt;
    }

    internal sealed class Result
    {
        public required ArrayMesh Mesh;
        public required int[] SurfaceOf;
        public required int[][] Starts;
    }

    private static readonly Dictionary<(int, int, uint), Cell> Cells = new();
    private static World3D? _world;
    private static long _frame;

    /// <summary>Members drawn by a cell now, and builds done, for the census.</summary>
    public static int BuiltMembers { get; private set; }

    public static int Builds { get; private set; }

    public static int CellCount => Cells.Count;

    /// <summary>
    /// Takes a still caster into its cell, or null when its model cannot share one. Its own
    /// instance keeps drawing until the cell's next build is in.
    /// </summary>
    /// <param name="instances">A batch's instance transforms (copied by the caller), or null for one model.</param>
    /// <param name="at">Where the caster stands, in world space: which cell it joins.</param>
    /// <param name="geometry">Its joinable triangles (Engine_ClassicCasterMerge.CellGeometry).</param>
    /// <param name="kept">Its kept surfaces' cast mesh, or default when every surface joins.</param>
    internal static Member Join(Rid own, Engine_ClassicCasterMerge.Group?[] geometry, Rid kept, Transform3D world, Transform3D[]? instances,
        Vector3 at, uint layers, bool shown, World3D scene)
    {
        _world = scene;
        var key = ((int)Mathf.Floor(at.X / CellSize), (int)Mathf.Floor(at.Z / CellSize), layers);
        if (!Cells.TryGetValue(key, out Cell? cell))
        {
            cell = new Cell
            {
                Key = key,
                Origin = new Vector3(key.Item1 * CellSize, 0f, key.Item2 * CellSize),
            };
            Cells[key] = cell;
        }
        var member = new Member { Own = own, Kept = kept, Geometry = geometry, World = world, Instances = instances, Cell = cell, Shown = shown };
        cell.Members.Add(member);
        cell.Dirty = _frame;
        if (cell.FirstDirty < 0)
        {
            cell.FirstDirty = _frame;
        }
        return member;
    }

    /// <summary>The member's cut or visibility changed.</summary>
    internal static void Show(Member member, bool shown)
    {
        if (member.Shown == shown)
        {
            return;
        }
        member.Shown = shown;
        if (member.Built)
        {
            Write(member.Cell, member, shown);
            member.Cell.ShownBuilt += shown ? 1 : -1;
            RenderingServer.InstanceSetVisible(member.Cell.Instance, member.Cell.ShownBuilt > 0);
        }
    }

    /// <summary>
    /// Takes the member out of its cell; the caller shows its own instance again. Its vertices
    /// stay in the cell as one point until the cell is next built.
    /// </summary>
    internal static void Leave(Member member)
    {
        Cell cell = member.Cell;
        member.Left = true;
        cell.Members.Remove(member);
        if (member.Built)
        {
            member.Built = false;
            --BuiltMembers;
            if (member.Shown)
            {
                Write(cell, member, false);
                --cell.ShownBuilt;
                RenderingServer.InstanceSetVisible(cell.Instance, cell.ShownBuilt > 0);
            }
        }
        if (cell.Members.Count == 0 && cell.Building is null)
        {
            Free(cell);
        }
    }

    /// <summary>Starts the builds of cells whose members have settled and swaps in finished ones. Main thread.</summary>
    public static void Pump()
    {
        ++_frame;
        List<Cell>? emptied = null;
        foreach (Cell cell in Cells.Values)
        {
            if (cell.Building is { IsCompleted: true } done)
            {
                Swap(cell, done.IsCompletedSuccessfully ? done.Result : null);
                if (cell.Members.Count == 0)
                {
                    (emptied ??= new List<Cell>()).Add(cell);
                }
            }
            if (cell.Building is null && cell.Dirty >= 0 &&
                (_frame - cell.Dirty >= QuietFrames || _frame - cell.FirstDirty >= StaleFrames))
            {
                Start(cell);
            }
        }
        if (emptied is not null)
        {
            foreach (Cell cell in emptied)
            {
                Free(cell);
            }
        }
    }

    /// <summary>Every cell freed: a level change drops the casters with it.</summary>
    public static void Clear()
    {
        foreach (Cell cell in Cells.Values)
        {
            if (cell.Instance.IsValid)
            {
                RenderingServer.FreeRid(cell.Instance);
            }
        }
        Cells.Clear();
        BuiltMembers = 0;
    }

    private static void Start(Cell cell)
    {
        cell.Dirty = -1;
        cell.FirstDirty = -1;
        var snapshot = new List<(Member, bool)>(cell.Members.Count);
        var input = new List<(Engine_ClassicCasterMerge.Group?[], Transform3D, Transform3D[]?, bool)>(cell.Members.Count);
        foreach (Member member in cell.Members)
        {
            snapshot.Add((member, member.Shown));
            input.Add((member.Geometry, member.World, member.Instances, member.Shown));
        }
        cell.Snapshot = snapshot;
        var materials = new[]
        {
            Engine_ClassicCasterMerge.DepthMaterial(0),
            Engine_ClassicCasterMerge.DepthMaterial(1),
            Engine_ClassicCasterMerge.DepthMaterial(2),
        };
        Vector3 origin = cell.Origin;
        cell.Building = Task.Run(() => Build(origin, input, materials));
    }

    private static Result? Build(Vector3 origin, List<(Engine_ClassicCasterMerge.Group?[] Geometry, Transform3D World, Transform3D[]? Instances, bool Shown)> input, ShaderMaterial[] materials)
    {
        var mesh = new ArrayMesh();
        var surfaceOf = new[] { -1, -1, -1 };
        var starts = new int[input.Count][];
        for (int m = 0; m < input.Count; m++)
        {
            starts[m] = new int[3];
        }
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 high = -low;
        for (int cull = 0; cull < 3; cull++)
        {
            int vertices = 0, indices = 0;
            foreach ((Engine_ClassicCasterMerge.Group?[] geometry, _, Transform3D[]? instances, _) in input)
            {
                if (geometry[cull] is { } group)
                {
                    int copies = instances?.Length ?? 1;
                    vertices += group.Positions.Length * copies;
                    indices += group.Indices.Length * copies;
                }
            }
            if (vertices == 0)
            {
                continue;
            }
            var positions = new Vector3[vertices];
            var index = new int[indices];
            var levels = new List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)>();
            int v = 0, i = 0;
            for (int m = 0; m < input.Count; m++)
            {
                (Engine_ClassicCasterMerge.Group?[] geometry, Transform3D world, Transform3D[]? instances, bool shown) = input[m];
                if (geometry[cull] is not { } group)
                {
                    continue;
                }
                starts[m][cull] = v;
                int copies = instances?.Length ?? 1;
                for (int copy = 0; copy < copies; copy++)
                {
                    Transform3D local = (instances is null ? world : world * instances[copy]).Translated(-origin);
                    for (int k = 0; k < group.Positions.Length; k++)
                    {
                        Vector3 p = local * group.Positions[k];
                        positions[v + k] = shown ? p : Vector3.Zero;
                        low = low.Min(p);
                        high = high.Max(p);
                    }
                    for (int k = 0; k < group.Indices.Length; k++)
                    {
                        index[i + k] = v + group.Indices[k];
                    }
                    foreach ((int at, int[] full, List<(float Edge, int[] Indices)> lods) in group.Levels)
                    {
                        levels.Add((v + at, full, lods));
                    }
                    v += group.Positions.Length;
                    i += group.Indices.Length;
                }
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Index] = index;
            Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays, null,
                Refii.Entities.Engine_ShadowProxy.MergedLods(levels));
            int surface = mesh.GetSurfaceCount() - 1;
            mesh.SurfaceSetMaterial(surface, materials[cull]);
            if (RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)mesh.SurfaceGetFormat(surface), vertices) != Stride)
            {
                return null;
            }
            surfaceOf[cull] = surface;
        }
        if (low.X > high.X)
        {
            return null;
        }
        // Hidden members sit at the origin, which the box must hold too.
        mesh.CustomAabb = new Aabb(low.Min(Vector3.Zero), high.Max(Vector3.Zero) - low.Min(Vector3.Zero));
        return new Result { Mesh = mesh, SurfaceOf = surfaceOf, Starts = starts };
    }

    private static void Swap(Cell cell, Result? result)
    {
        List<(Member Member, bool Shown)> snapshot = cell.Snapshot!;
        cell.Building = null;
        cell.Snapshot = null;
        if (result is null)
        {
            // Nothing usable: the members keep drawing on their own.
            GD.PushWarning($"[classic] caster cell {cell.Key} not built; its {cell.Members.Count} members cast alone");
            return;
        }
        ++Builds;
        if (!cell.Instance.IsValid)
        {
            cell.Instance = RenderingServer.InstanceCreate();
            RenderingServer.InstanceSetScenario(cell.Instance, (_world ?? Engine_ClassicCasters.World).Scenario);
            RenderingServer.InstanceSetLayerMask(cell.Instance, cell.Key.Layers);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(cell.Instance, RenderingServer.ShadowCastingSetting.Off);
            RenderingServer.InstanceSetTransform(cell.Instance, new Transform3D(Basis.Identity, cell.Origin));
        }
        ArrayMesh? old = cell.Mesh;
        cell.Mesh = result.Mesh;
        System.Array.Copy(result.SurfaceOf, cell.SurfaceOf, 3);
        RenderingServer.InstanceSetBase(cell.Instance, result.Mesh.GetRid());
        old?.Dispose();

        int shownBuilt = 0;
        for (int m = 0; m < snapshot.Count; m++)
        {
            (Member member, bool shownThen) = snapshot[m];
            System.Array.Copy(result.Starts[m], member.Start, 3);
            if (member.Left)
            {
                // Left while the build ran: it is in the new mesh, so it is put out there.
                if (shownThen)
                {
                    Write(cell, member, false);
                }
                continue;
            }
            if (!member.Built)
            {
                member.Built = true;
                ++BuiltMembers;
                if (member.HasKept)
                {
                    // The cell draws the rest; the own instance keeps only what could not join.
                    RenderingServer.InstanceSetBase(member.Own, member.Kept);
                }
                else
                {
                    RenderingServer.InstanceSetVisible(member.Own, false);
                }
            }
            if (member.Shown != shownThen)
            {
                Write(cell, member, member.Shown);
            }
            shownBuilt += member.Shown ? 1 : 0;
        }
        cell.ShownBuilt = shownBuilt;
        RenderingServer.InstanceSetVisible(cell.Instance, shownBuilt > 0);
    }

    /// <summary>The member's vertices in the cell's mesh: in place, or all at one point.</summary>
    private static void Write(Cell cell, Member member, bool shown)
    {
        if (cell.Mesh is not { } mesh)
        {
            return;
        }
        int copies = member.Instances?.Length ?? 1;
        for (int cull = 0; cull < 3; cull++)
        {
            if (member.Geometry[cull] is not { } group || cell.SurfaceOf[cull] < 0)
            {
                continue;
            }
            int n = group.Positions.Length;
            var positions = new Vector3[n * copies];
            if (shown)
            {
                for (int copy = 0; copy < copies; copy++)
                {
                    Transform3D local = (member.Instances is null ? member.World : member.World * member.Instances[copy])
                        .Translated(-cell.Origin);
                    for (int k = 0; k < n; k++)
                    {
                        positions[copy * n + k] = local * group.Positions[k];
                    }
                }
            }
            RenderingServer.MeshSurfaceUpdateVertexRegion(mesh.GetRid(), cell.SurfaceOf[cull], member.Start[cull] * Stride,
                MemoryMarshal.AsBytes(new System.Span<Vector3>(positions)).ToArray());
        }
    }

    private static void Free(Cell cell)
    {
        if (cell.Instance.IsValid)
        {
            RenderingServer.FreeRid(cell.Instance);
            cell.Instance = default;
        }
        cell.Mesh = null;
        Cells.Remove(cell.Key);
    }
}
