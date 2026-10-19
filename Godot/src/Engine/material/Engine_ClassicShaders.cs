using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Models;

/// <summary>
/// The title's own shaders as Godot shaders: the classic renderer's material source.
/// </summary>
/// <remarks>
/// READ FROM THE PLAYER'S WORKSPACE, NEVER FROM THE PROJECT. The launcher's Compile Shaders step
/// translates <c>Shaders.sbk</c> on the player's machine into one <c>.gdshaderinc</c> per shader
/// under <c>&lt;assets&gt;/unpacked/shaders/godot/</c>; they are derived from the game's data and
/// cannot ship, so nothing here is a resource. Only the support include they all pull in
/// (<c>xenos_common.gdshaderinc</c>) is ours.
///
/// A Godot shader holds both stages, and the bank keeps them apart, so a shader here is a
/// composition: the header, the vertex include and the pixel include concatenated into one code
/// string. The includes declare their varyings behind guards and prefix their uniforms by stage,
/// which is what lets two of them share a file. Compositions are cached by their pair.
///
/// The bank's entry names (<c>VSHADER_STANDARDMATERIAL_BLT1_GLT1_MIST0</c>) are the keys; the
/// extract manifest the pipeline writes maps each to its blob, and the blob to its file.
/// </remarks>
public static class Engine_ClassicShaders
{
    private static readonly object Gate = new();
    private static bool _scanned;
    private static string _reason = "not scanned";
    private static string _root = string.Empty;
    // Entry name (upper case, as the bank spells it) -> the translated include's path.
    private static readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Shader?> _composed = new();
    private static readonly ConcurrentDictionary<string, string> _sources = new();

    /// <summary>Guest world (x, y, z) to Godot's (-x, z, y), as a matrix.</summary>
    public static readonly Projection GuestToGodot = new(
        new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

    /// <summary>
    /// Guest world to the clip space Godot rasterises, for a stage that writes POSITION from its
    /// own matrix (<c>xr_use_guest_position</c>). The camera's projection is GL's; the one Godot
    /// renders with carries a y flip and a reversed [0, 1] depth (z' = 0.5 w - 0.5 z), which is the
    /// PROJECTION_MATRIX a stage sees.
    /// </summary>
    public static Projection GuestToClip(Camera3D camera)
    {
        var correction = new Projection(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, -1f, 0f, 0f),
            new Vector4(0f, 0f, -0.5f, 0f), new Vector4(0f, 0f, 0.5f, 1f));
        return correction * camera.GetCameraProjection() * new Projection(camera.GlobalTransform.AffineInverse()) * GuestToGodot;
    }

    /// <summary>True when the translated shaders are on disk and the manifest read.</summary>
    public static bool Available
    {
        get
        {
            Scan();
            return _entries.Count != 0;
        }
    }

    /// <summary>Why <see cref="Available"/> is false, for the log.</summary>
    public static string Reason
    {
        get
        {
            Scan();
            return _reason;
        }
    }

    /// <summary>Whether the bank has a shader of this name and it was translated.</summary>
    public static bool HasEntry(string entryName)
    {
        Scan();
        lock (Gate)
        {
            return _entries.ContainsKey(entryName);
        }
    }

    /// <summary>
    /// The entry of a family whose permutation tokens are exactly <paramref name="tokens"/>, or
    /// failing that the one with the fewest extra tokens that still has every requested one.
    /// </summary>
    /// <remarks>
    /// BY TOKEN SET, NOT BY SPELLING. A name is <c>PSHADER_STANDARDMATERIAL_BLT1_GLT1_MIST0_NRM1_SP2</c>:
    /// the stage, the family, then the permutation's tokens, whose order in the name is the
    /// bank's business. The guest resolves a missing permutation by stepping UP to one that has
    /// every feature it asked for plus some (sub_82B03008), never down, and that is the fallback
    /// here too.
    /// </remarks>
    public static string FindEntry(string stage, string family, IReadOnlyCollection<string> tokens)
    {
        // Memoised: a lookup walks every entry's name, and the prop streamer asks per surface of
        // every placement it pages in.
        string key = stage + "|" + family + "|" + string.Join(' ', tokens);
        if (_found.TryGetValue(key, out string? known))
        {
            return known;
        }
        string result = FindEntryUncached(stage, family, tokens);
        _found[key] = result;
        return result;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _found = new();

    private static string FindEntryUncached(string stage, string family, IReadOnlyCollection<string> tokens)
    {
        Scan();
        string prefix = stage + "_" + family + "_";
        string best = string.Empty;
        int bestExtra = int.MaxValue;
        lock (Gate)
        {
            // The family's bare entry (PSHADER_PARTICLEMATERIAL) is the exact answer to no tokens;
            // the prefix below needs a trailing token and would hand back the first one-token
            // variant instead.
            string bare = stage + "_" + family;
            if (tokens.Count == 0)
            {
                foreach (string name in _entries.Keys)
                {
                    if (name.Equals(bare, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }

            foreach (string name in _entries.Keys)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string[] have = name.Substring(prefix.Length).Split('_', StringSplitOptions.RemoveEmptyEntries);
                bool superset = true;
                foreach (string token in tokens)
                {
                    if (Array.FindIndex(have, t => t.Equals(token, StringComparison.OrdinalIgnoreCase)) < 0)
                    {
                        superset = false;
                        break;
                    }
                }
                if (!superset)
                {
                    continue;
                }
                int extra = have.Length - tokens.Count;
                if (extra < bestExtra)
                {
                    bestExtra = extra;
                    best = name;
                    if (extra == 0)
                    {
                        break;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The Godot shader for a vertex and pixel pair, or null when either is missing or the
    /// composition fails to load. <paramref name="renderMode"/> is the shader's render_mode line
    /// body ("unshaded, cull_back").
    /// </summary>
    public static Shader? Compose(string vertexEntry, string pixelEntry, string renderMode) =>
        Compose(vertexEntry, pixelEntry, renderMode, 0);

    /// <summary>
    /// A composition for light-group pass <paramref name="pass"/> (0..2): the same pair, reading
    /// the pass's own row uniform (xenos_common.gdshaderinc, <c>XR_ROW_UNIFORM</c>).
    /// </summary>
    public static Shader? Compose(string vertexEntry, string pixelEntry, string renderMode, int pass) =>
        Compose(vertexEntry, pixelEntry, renderMode, pass, string.Empty);

    /// <summary>
    /// As above with <paramref name="defines"/>, preprocessor lines placed before the two
    /// stages (the composition's own switches, such as <c>XR_ALPHA_OUT</c>).
    /// </summary>
    public static Shader? Compose(string vertexEntry, string pixelEntry, string renderMode, int pass, string defines)
    {
        string key = vertexEntry + "|" + pixelEntry + "|" + renderMode + "|" + pass + "|" + defines;
        return _composed.GetOrAdd(key, _ => Build(vertexEntry, pixelEntry, renderMode, pass, defines));
    }

    private sealed class ComposedAt
    {
        public volatile bool Ready;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Shader, ComposedAt> Composed = new();

    /// <summary>Main-thread compositions compiling on the pool at once.</summary>
    private static readonly System.Threading.SemaphoreSlim CompileSlots = new(Math.Clamp(System.Environment.ProcessorCount / 2, 2, 8));

    private static int _compiling;

    /// <summary>Compositions handed to the pool and not yet compiled (the load hold waits for none).</summary>
    public static int Compiling => System.Threading.Volatile.Read(ref _compiling);

    /// <summary>
    /// Whether a composed shader has been compiled. A Shader makes its server shader on its first
    /// `get_rid()` (`Shader::_check_shader_rid`), and on Vulkan `shader_create_from_code` parses and
    /// compiles it on the calling thread, so the first material bound to it would compile it on
    /// the main thread (18-35 ms for the title's larger stages). <see cref="Build"/> makes that
    /// first call off the main thread; a shader this class did not compose counts as settled.
    /// </summary>
    public static bool Settled(Shader shader)
    {
        lock (Composed)
        {
            return !Composed.TryGetValue(shader, out ComposedAt? at) || at.Ready;
        }
    }

    /// <summary>Compiles the shader on this thread if the pool has not yet (waiting for it if it is compiling now).</summary>
    public static void Settle(Shader shader)
    {
        shader.GetRid();
        lock (Composed)
        {
            if (Composed.TryGetValue(shader, out ComposedAt? at))
            {
                at.Ready = true;
            }
        }
    }

    /// <summary>Creates the shader's server shader where it costs no frame: here on a worker, else on the pool.</summary>
    private static void Prepare(Shader shader)
    {
        var at = new ComposedAt();
        lock (Composed)
        {
            Composed.AddOrUpdate(shader, at);
        }
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            shader.GetRid();
            at.Ready = true;
            return;
        }
        System.Threading.Interlocked.Increment(ref _compiling);
        System.Threading.Tasks.Task.Run(async () =>
        {
            await CompileSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                shader.GetRid();
            }
            finally
            {
                at.Ready = true;
                CompileSlots.Release();
                System.Threading.Interlocked.Decrement(ref _compiling);
            }
        });
    }

    /// <summary>Forget every composition: the next request rebuilds from disk.</summary>
    public static void Clear()
    {
        _composed.Clear();
        _sources.Clear();
        _found.Clear();
        lock (Gate)
        {
            _scanned = false;
            _entries.Clear();
        }
    }

    private const string TreePrtRow0 = "vs_g_PRTConstants[min(0 + a0, 11)]";
    private static readonly string[] TreeAtmosphereSamplers = { "vs_s16", "vs_s17" };

    /// <summary>
    /// Vertex stages whose textures the title binds clamped and linear, with the samplers they
    /// declare. The landscape's height and normal maps (`Render_TextureConstantSetup_INFERRED`,
    /// bindings 311 and 359) and its paint map (`sub_8221FC40`, binding 342) all take
    /// g_Address2DMode (2, clamp) and dword_83316FC0; so does the water's mask. Repeating, the
    /// last row and column of a heightfield blended with the first: a region's far edge rose
    /// into walls of the opposite edge's heights.
    /// </summary>
    private static readonly (string Prefix, string[] Samplers)[] ClampedVertexSamplers =
    {
        ("VSHADER_LANDSCAPEMATERIAL", new[] { "vs_s16", "vs_s17", "vs_s18" }),
        ("VSHADER_OCEAN_WATER", new[] { "vs_s16" }),
    };

    private static Shader? Build(string vertexEntry, string pixelEntry, string renderMode, int pass, string defines)
    {
        string? vertex = Source(vertexEntry);
        // No pixel entry is the title's own "no pixel shader" (SetPixelShader(dev, 0)): a
        // depth-only draw such as a sun-cascade caster.
        string? pixel = pixelEntry.Length == 0 ? "void fragment() {}\n" : Source(pixelEntry);
        if (vertex is null || pixel is null)
        {
            GD.Print($"[classic] no translated shader for {(vertex is null ? vertexEntry : pixelEntry)}");
            return null;
        }

        // A composition that defines XR_TREE_PRT_ROW0 takes the AMB2 tree stages' cluster-mean read
        // through it: every one reads row 0 as this one expression, and the array is a material
        // uniform where the value is the placement's (Engine_ClassicMaterial.TreeDefines).
        if (defines.Contains("XR_TREE_PRT_ROW0", StringComparison.Ordinal))
        {
            vertex = vertex.Replace(TreePrtRow0, "XR_TREE_PRT_ROW0(" + TreePrtRow0 + ", a0)", StringComparison.Ordinal);
            // g_TreeExtinction (s16) and g_TreeInScattering (s17) are the pixel stage's two tables,
            // sampled the way `Render_MaterialConstantSetup_INFERRED` (0x8220C1B0) sets them:
            // g_Address2DMode 2 (clamp) and g_Filter2DMode 1 (linear). The translator cannot name
            // a vertex sampler's state and left them repeating, so a tree just past the fog start
            // read the extinction table's 2 km end into its first texel and took up to ten times
            // its in-scatter: trunks 15-19 m away washed out to the sky colour.
            foreach (string sampler in TreeAtmosphereSamplers)
            {
                vertex = vertex.Replace(
                    "uniform sampler2D " + sampler + " : hint_default_black;",
                    "uniform sampler2D " + sampler + " : hint_default_black, filter_linear, repeat_disable;",
                    StringComparison.Ordinal);
            }
        }

        // THE VIEW'S OWN MATERIAL CONSTANTS (split screen): the reads of each name the composition
        // lists (ViewParam) take the second view's host register in that view, the uniform in the first.
        if (defines.Contains(ViewParamDirective, StringComparison.Ordinal))
        {
            RewriteViewParams(defines, ref vertex, ref pixel);
        }

        // THE VIEW'S OWN TEXTURES (split screen): the fetches of each sampler the composition lists
        // (ViewSampler) read its second-view twin in that view.
        if (defines.Contains(ViewSamplerDirective, StringComparison.Ordinal))
        {
            RewriteViewSamplers(defines, ref pixel);
        }

        // THE VIEW'S OWN PIXEL BOOLEANS (split screen, ViewBooleans): the stage's reads of
        // xr_booleans take the second view's value in that view.
        if (defines.Contains(ViewBooleansDirective, StringComparison.Ordinal))
        {
            pixel = System.Text.RegularExpressions.Regex.Replace(pixel, @"\bxr_booleans\b",
                "(XR_SECOND_VIEW ? " + SecondViewBooleans + " : xr_booleans)");
            pixel = "uniform uint " + SecondViewBooleans + " = 0u;\n" + pixel;
        }

        // XR_PRT_ROW_FETCH: the AMB2 stage's g_PRTConstants come from the placement's row of the
        // shared texture instead of a material per placement (Engine_ClassicPrtRows).
        if (defines.Contains("XR_PRT_ROW_FETCH", StringComparison.Ordinal))
        {
            vertex = System.Text.RegularExpressions.Regex.Replace(vertex, @"uniform\s+vec4\s+vs_g_PRTConstants\[\d+\];", string.Empty);
            vertex = System.Text.RegularExpressions.Regex.Replace(vertex, @"\bvs_g_PRTConstants\[([^\]]*)\]", "xr_prt($1)");
        }

        // XR_VERTEX_BAKE_FETCH: the AMB1 stage's bake stream (COLOR) comes from the placement's
        // block of the shared bake texture instead (Engine_ClassicVertexBakes).
        if (defines.Contains("XR_VERTEX_BAKE_FETCH", StringComparison.Ordinal))
        {
            vertex = System.Text.RegularExpressions.Regex.Replace(vertex, @"\bCOLOR\b", "xr_vertex_bake(VERTEX_ID)");
        }

        foreach ((string prefix, string[] samplers) in ClampedVertexSamplers)
        {
            if (!vertexEntry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string sampler in samplers)
            {
                vertex = vertex.Replace(
                    "uniform sampler2D " + sampler + " : hint_default_black;",
                    "uniform sampler2D " + sampler + " : hint_default_black, filter_linear, repeat_disable;",
                    StringComparison.Ordinal);
            }
        }

        // Godot's fog never touches a title shader: the title fogs in its own pixel code
        // (atmosphere LUTs, mist rows), and Godot applies fog to unshaded output too.
        if (!renderMode.Contains("fog_disabled", StringComparison.Ordinal))
        {
            renderMode += ", fog_disabled";
        }

        var code = new System.Text.StringBuilder(vertex.Length + pixel.Length + 512);
        code.Append("shader_type spatial;\n");
        code.Append("render_mode ").Append(renderMode).Append(";\n");
        code.Append("// ").Append(vertexEntry).Append(" + ").Append(pixelEntry).Append('\n');
        if (pass > 0)
        {
            code.Append("#define XR_ROW_UNIFORM xr_light_row").Append(pass + 1).Append('\n');
        }
        if (defines.Length != 0)
        {
            code.Append(defines).Append('\n');
        }
        // An opaque composition writes no ALPHA: Godot would otherwise draw it in the blended
        // alpha pass at the shader's output alpha (see XR_WRITE_ALPHA).
        if (renderMode.Contains("depth_draw_opaque") && !renderMode.Contains("blend_"))
        {
            code.Append("#define XR_WRITE_ALPHA(a)\n");
        }
        code.Append(vertex).Append('\n').Append(pixel);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string shaderCode = code.ToString();
        if (Engine_ClassicWeather.Supports(pixelEntry, pass)) shaderCode = Engine_ClassicWeather.Adapt(shaderCode);
        var shader = new Shader { Code = shaderCode };
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int built = System.Threading.Interlocked.Increment(ref _built);
        _buildMs += ms;
        if (ms > 40.0)
        {
            GD.Print($"[classic] composed {vertexEntry} + {pixelEntry} (pass {pass}) in {ms:0} ms; {built} compositions, {_buildMs:0} ms in all");
        }
        Prepare(shader);
        return shader;
    }

    private static int _built;
    private static double _buildMs;

    private const string ViewParamDirective = "// xr_view_param ";

    /// <summary>
    /// A composition's directive naming a material constant that belongs to the view drawing it:
    /// the first view reads the material's uniform, the second the host register
    /// <paramref name="register"/> of its own block (xenos_common.gdshaderinc XR_VIEW_PARAM), which
    /// its owner writes per view. <paramref name="gate"/>, a material bool, limits it to the
    /// materials whose value is the view's (a trail's z axis is the camera's only when not fixed).
    /// A comment in the shader; <see cref="RewriteViewParams"/> applies it.
    /// </summary>
    public static string ViewParam(string name, bool pixel, int register, string gate = "true") =>
        ViewParamDirective + name + (pixel ? " ps " : " vs ") + register + " " + gate;

    /// <summary>Rewrites the stages' reads of each name a directive lists (the declaration stays).</summary>
    private static void RewriteViewParams(string defines, ref string vertex, ref string pixel)
    {
        foreach (string line in defines.Split('\n'))
        {
            int at = line.IndexOf(ViewParamDirective, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            string[] parts = line.Substring(at + ViewParamDirective.Length)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !int.TryParse(parts[2], out int register))
            {
                continue;
            }
            string name = parts[0];
            bool pixelStage = parts[1] == "ps";
            string gate = parts.Length > 3 ? parts[3] : "true";
            string registers = pixelStage ? "xr_ps_constants" : "xr_vs_constants";
            string stage = pixelStage ? pixel : vertex;
            string escaped = System.Text.RegularExpressions.Regex.Escape(name);
            bool array = System.Text.RegularExpressions.Regex.IsMatch(stage, @"\buniform\s+vec4\s+" + escaped + @"\s*\[");
            stage = array
                ? System.Text.RegularExpressions.Regex.Replace(stage,
                    @"(?<!uniform\s+vec4\s+)\b" + escaped + @"\[([^\]]*)\]",
                    "XR_VIEW_PARAM_AT(" + name + ", " + registers + ", " + register + ", ($1), " + gate + ")")
                : System.Text.RegularExpressions.Regex.Replace(stage,
                    @"(?<!uniform\s+vec4\s+)\b" + escaped + @"\b",
                    "XR_VIEW_PARAM(" + name + ", " + registers + ", " + register + ", " + gate + ")");
            if (pixelStage)
            {
                pixel = stage;
            }
            else
            {
                vertex = stage;
            }
        }
    }

    private const string ViewSamplerDirective = "// xr_view_sampler ";

    /// <summary>
    /// A composition's directive naming a pixel-stage sampler whose texture belongs to the view
    /// drawing it: the first view fetches the material's <paramref name="name"/>, the second its
    /// twin <see cref="SecondViewSampler"/> (declared beside it with the same hints), which the
    /// owner binds per view. A comment in the shader; <see cref="RewriteViewSamplers"/> applies it.
    /// </summary>
    public static string ViewSampler(string name) => ViewSamplerDirective + name;

    private const string ViewBooleansDirective = "// xr_view_booleans";

    /// <summary>
    /// A composition's directive making the pixel stage's booleans (xenos_common's
    /// <c>xr_booleans</c>, the b registers) the view's: the first view reads the material's
    /// <c>xr_booleans</c>, the second <see cref="SecondViewBooleans"/>, which the owner sets per view.
    /// </summary>
    public static string ViewBooleans() => ViewBooleansDirective;

    /// <summary>The material uniform holding the second view's pixel booleans (<see cref="ViewBooleans"/>).</summary>
    public const string SecondViewBooleans = "xr_second_booleans";

    /// <summary>The second view's twin of a sampler a composition lists with <see cref="ViewSampler"/>.</summary>
    public static string SecondViewSampler(string name) => "xr_second_" + name;

    /// <summary>
    /// Declares each listed sampler's twin and rewrites its fetches into
    /// <c>(XR_SECOND_VIEW ? xr_tfetch2D(twin, ...) : xr_tfetch2D(name, ...))</c>.
    /// </summary>
    private static void RewriteViewSamplers(string defines, ref string pixel)
    {
        foreach (string line in defines.Split('\n'))
        {
            int at = line.IndexOf(ViewSamplerDirective, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            string name = line.Substring(at + ViewSamplerDirective.Length).Trim();
            if (name.Length == 0)
            {
                continue;
            }
            string twin = SecondViewSampler(name);
            string escaped = System.Text.RegularExpressions.Regex.Escape(name);
            System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(
                pixel, @"uniform\s+sampler2D\s+" + escaped + @"\b([^;]*);");
            if (!declaration.Success)
            {
                continue;
            }
            pixel = pixel.Insert(declaration.Index + declaration.Length,
                "\nuniform sampler2D " + twin + declaration.Groups[1].Value + ";");

            string call = "xr_tfetch2D(" + name + ",";
            var rewritten = new System.Text.StringBuilder(pixel.Length + 256);
            int from = 0;
            while (true)
            {
                int start = pixel.IndexOf(call, from, StringComparison.Ordinal);
                if (start < 0)
                {
                    break;
                }
                int open = start + "xr_tfetch2D".Length, depth = 0, end = -1;
                for (int i = open; i < pixel.Length; i++)
                {
                    if (pixel[i] == '(')
                    {
                        depth++;
                    }
                    else if (pixel[i] == ')' && --depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
                if (end < 0)
                {
                    break;
                }
                string args = pixel.Substring(start + call.Length, end - (start + call.Length));
                rewritten.Append(pixel, from, start - from)
                    .Append("(XR_SECOND_VIEW ? xr_tfetch2D(").Append(twin).Append(',').Append(args)
                    .Append(") : xr_tfetch2D(").Append(name).Append(',').Append(args).Append("))");
                from = end + 1;
            }
            rewritten.Append(pixel, from, pixel.Length - from);
            pixel = rewritten.ToString();
        }
    }

    /// <summary>Compositions built and the milliseconds their shader compiles took, for the log.</summary>
    public static (int Built, double Milliseconds) BuildCounts => (_built, _buildMs);

    /// <summary>The translated stage's text by entry name (or a res:// path), null when absent.</summary>
    public static string? Source(string entryName)
    {
        // A stage of our own (a vertex stage standing in for one Godot cannot run), by path.
        if (entryName.StartsWith("res://", StringComparison.Ordinal))
        {
            return _sources.GetOrAdd(entryName, p => Godot.FileAccess.GetFileAsString(p)) is { Length: > 0 } own ? own : null;
        }
        Scan();
        string path;
        lock (Gate)
        {
            if (!_entries.TryGetValue(entryName, out path!))
            {
                return null;
            }
        }
        return _sources.GetOrAdd(path, p =>
        {
            try
            {
                return File.ReadAllText(p);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[classic] cannot read {p}: {exception.Message}");
                return string.Empty;
            }
        }) is { Length: > 0 } text ? text : null;
    }

    /// <summary>
    /// Reads the extract manifest once. Rows are tab-separated; the `entry` rows pair an entry
    /// name with a blob index, and the translated include sits where the pipeline put it:
    /// <c>godot/&lt;bank&gt;/&lt;blob stem&gt;.godot.gdshaderinc</c>, the blob stem being the
    /// blob file's name without its extension.
    /// </summary>
    private static void Scan()
    {
        lock (Gate)
        {
            if (_scanned)
            {
                return;
            }
            _scanned = true;

            string game = Refii.Core.Engine_GameData.Resolve();
            if (game.Length == 0)
            {
                _reason = "no game data";
                return;
            }
            _root = Path.Combine(Refii.Core.Engine_GameData.UnpackedRootFor(game), "shaders");
            string manifest = Path.Combine(_root, "extract_manifest.tsv");
            if (!File.Exists(manifest))
            {
                _reason = "shaders not compiled: " + manifest + " is missing (Compile Shaders in the launcher)";
                return;
            }

            int bank = -1, entryName = -1, outputPath = -1, rowType = -1;
            int missing = 0;
            foreach (string line in File.ReadLines(manifest))
            {
                string[] cells = line.Split('\t');
                if (rowType < 0)
                {
                    rowType = Array.IndexOf(cells, "row_type");
                    bank = Array.IndexOf(cells, "bank");
                    entryName = Array.IndexOf(cells, "entry_name");
                    outputPath = Array.IndexOf(cells, "output_path");
                    if (rowType < 0 || bank < 0 || entryName < 0 || outputPath < 0)
                    {
                        _reason = "extract manifest has an unexpected header: " + manifest;
                        return;
                    }
                    continue;
                }
                if (cells.Length <= Math.Max(Math.Max(bank, entryName), outputPath) ||
                    cells[rowType] != "entry" || cells[entryName].Length == 0 ||
                    cells[outputPath].Length == 0)
                {
                    continue;
                }

                string bankStem = Path.GetFileNameWithoutExtension(cells[bank]).ToLowerInvariant();
                string blobStem = Path.GetFileNameWithoutExtension(cells[outputPath]);
                string include = Path.Combine(_root, "godot", bankStem, blobStem + ".godot.gdshaderinc");
                if (!File.Exists(include))
                {
                    ++missing;
                    continue;
                }
                _entries[cells[entryName]] = include;
            }

            _reason = _entries.Count == 0
                ? "no translated shaders under " + Path.Combine(_root, "godot")
                : $"{_entries.Count} shaders, {missing} not translated";
            GD.Print($"[classic] translated shaders: {_reason}");
        }
    }
}
