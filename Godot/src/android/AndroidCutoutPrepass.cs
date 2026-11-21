using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_cutout_depth_prepass`: cut-out surfaces (by default the two-sided builds: hedges, bushes, railings;
/// <see cref="ScopeKey"/>) drawn twice in Godot Mobile's opaque pass, depth first and colour after, so each covered
/// pixel is shaded once instead of once per stacked card. The same picture. Off by default; Medium's since preset
/// revision 27; Android only; read once, so a change applies at the next start.
/// Needs templates with godot-patches/0017; on others the builds change and nothing else does.
/// </summary>
/// <remarks>
/// WHY. A cut-out draws with a `discard` and writes depth, so an Adreno 640 runs its depth test after the shader and
/// shades every layer of a hedge or a canopy in full (about 40% of the frame at Medium forest and road stops,
/// `area-census-2026-11-15/leads/where-the-time-goes-1117.md`). A discard without a depth write keeps the early test.
/// <para>HOW. The marked builds declare `depth_prepass_alpha`, the render mode upstream's cut-outs use for Forward+'s
/// depth pre-pass (standard_material_body.gdshaderinc). Under Mobile it only defines USE_OPAQUE_PREPASS, which discards
/// nothing in a build that writes no ALPHA, so the build draws as before; patch 0017 reads it as the request.
/// With <see cref="EngineSetting"/> on, the engine draws every such surface first with the build's shadow version
/// (MODE_RENDER_DEPTH: the vertex stage, the alpha fetch, the fade dither and the discard; colour masked), before the
/// opaque list, and then in the list's own order with the colour version and no depth write, compare EQUAL: only the
/// fragment that wrote the pre-pass depth is shaded. <see cref="EqualKey"/> = false compares GREATER_OR_EQUAL as
/// every opaque draw does (the discard, kept, cuts the holes), which keeps the Adreno's coarse depth test (LRZ) on for
/// those draws but lets every layer's hole fragments through the early test wherever a hedge is see-through, and those
/// are shaded before the discard drops them. Phones, 2026-11-17 (Medium, one launch each): EQUAL -8% at the S10e's
/// road, -4% at the OP7's; GREATER_OR_EQUAL -3% and +1%.</para>
/// <para>WHAT IS KEPT. Shadows: the shadow pass draws the same builds with their discard, unchanged. The prop and tree
/// fade dither and the alpha scissor run identically in both draws (FRAGCOORD, UV and the same vertex code;
/// gl_Position is invariant). A translucent material's blended second pass, characters' cut-outs and solid builds
/// are not marked; the translucent material's opaque alpha-tested pass is.</para>
/// <para>COST. One more draw per marked surface and its vertex work again; two more pipelines per marked build
/// (its depth draw, and its colour draw without depth write in place of the usual one). For the GPU-bound Adreno 640s;
/// the draw-bound S25+ is unlikely to gain.</para>
/// </remarks>
public static class AndroidCutoutPrepass
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "android_cutout_depth_prepass";

    /// <summary>`debug_cutout_prepass_equal` (testing, default true, in no preset): false makes the colour draws compare GREATER_OR_EQUAL instead of EQUAL.</summary>
    public const string EqualKey = "debug_cutout_prepass_equal";

    /// <summary>The marker a marked build's specialisation carries; it adds <see cref="RenderMode"/>.</summary>
    public const string Define = "FABLE_CUTOUT_PREPASS";

    /// <summary>The render mode patch 0017 reads as a cut-out's request for the pre-pass.</summary>
    public const string RenderMode = "depth_prepass_alpha";

    /// <summary>The engine setting godot-patches/0017 reads each frame: 0 off, 1 GREATER_OR_EQUAL, 2 EQUAL.</summary>
    public const string EngineSetting = "rendering/renderer/cutout_depth_prepass_mobile";

    /// <summary>Whether the values ask for it; unset is off. Pure.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>The engine value for these choices: 0 off, 1 GREATER_OR_EQUAL, 2 EQUAL. Pure.</summary>
    public static int EngineValue(bool on, bool equal) => !on ? 0 : equal ? 2 : 1;

    /// <summary>
    /// `debug_cutout_prepass_scope` (testing, "two_sided", in no preset): which cut-outs are marked, so one build A/Bs
    /// the scopes. A comma list of builds: `two_sided` (hedges, bushes, railings; the default), `wind` (swaying
    /// undergrowth), `trees` (tree leaves), or `all`; `no_translucent` added leaves out the opaque pass of a
    /// translucent material. Medium, 2026-11-17: two_sided -10% at the Oakfield road on both Adreno 640s and 0 at
    /// Westcliff camp; trees +5 to +7% at the road and worse under the canopy (the depth draw runs the trees' dense,
    /// swaying vertex work a second time for little overdraw); two_sided,wind -8%.
    /// </summary>
    public const string ScopeKey = "debug_cutout_prepass_scope";

    /// <summary>The scope's parts.</summary>
    [Flags]
    public enum Scope
    {
        None = 0,
        TwoSided = 1,
        Wind = 2,
        Trees = 4,
        Translucent = 8,
        All = TwoSided | Wind | Trees | Translucent,
    }

    /// <summary>The scope when <see cref="ScopeKey"/> is unset or names no build: the two-sided builds (hedges, bushes).</summary>
    public const Scope DefaultScope = Scope.TwoSided | Scope.Translucent;

    /// <summary>
    /// <see cref="ScopeKey"/> as text: a list names builds ("all" is all three), and translucent materials' opaque
    /// passes come with them unless `no_translucent`; unset or naming no build is <see cref="DefaultScope"/>'s builds. Pure.
    /// </summary>
    public static Scope ParseScope(string? text)
    {
        Scope builds = Scope.None;
        bool noTranslucent = false;
        foreach (string raw in (text ?? string.Empty).Trim().Trim('"').ToLowerInvariant().Split(',', ' ', ';'))
        {
            switch (raw.Trim())
            {
                case "all": builds |= Scope.TwoSided | Scope.Wind | Scope.Trees; break;
                case "two_sided": builds |= Scope.TwoSided; break;
                case "wind": builds |= Scope.Wind; break;
                case "trees": builds |= Scope.Trees; break;
                case "no_translucent": noTranslucent = true; break;
            }
        }

        if (builds == Scope.None)
        {
            builds = DefaultScope & ~Scope.Translucent;
        }

        return noTranslucent ? builds : builds | Scope.Translucent;
    }

    /// <summary>A scope as the key would write it. Pure.</summary>
    public static string ScopeText(Scope scope)
    {
        var parts = new List<string>(4);
        if ((scope & (Scope.TwoSided | Scope.Wind | Scope.Trees)) == (Scope.TwoSided | Scope.Wind | Scope.Trees))
        {
            parts.Add("all");
        }
        else
        {
            if (scope.HasFlag(Scope.TwoSided)) parts.Add("two_sided");
            if (scope.HasFlag(Scope.Wind)) parts.Add("wind");
            if (scope.HasFlag(Scope.Trees)) parts.Add("trees");
        }

        if (!scope.HasFlag(Scope.Translucent))
        {
            parts.Add("no_translucent");
        }

        return string.Join(",", parts);
    }

    private static readonly Lazy<(bool On, bool Equal, Scope Scope)> Chosen = new(() =>
    {
        try
        {
            if (!Engine_Platform.IsAndroid)
            {
                return (false, false, DefaultScope);
            }

            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            bool on = Wanted(values);
            bool equal = Engine_RefiiConfig.Bool(values, EqualKey, true);
            Scope scope = ParseScope(Engine_RefiiConfig.String(values, ScopeKey, "two_sided"));
            if (on)
            {
                GD.Print($"[material] {Key}: cut-outs drawn depth first, colour compare {(equal ? "EQUAL" : "GREATER_OR_EQUAL")}, scope {ScopeText(scope)} (relaunch to change)");
            }

            return (on, equal, scope);
        }
        catch (Exception)
        {
            return (false, false, DefaultScope);
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's choice: false off Android.</summary>
    public static bool On => Chosen.Value.On;

    /// <summary>This process's <see cref="ScopeKey"/>.</summary>
    public static Scope ChosenScope => Chosen.Value.Scope;

    /// <summary>The build a cut-out or grass pass on <paramref name="path"/> is on, if one that can discard. Pure.</summary>
    private static Scope BuildOf(string path) => Refii.Models.Engine_ShaderVariant.FullPathOf(path) switch
    {
        Refii.Models.Engine_ShaderFamily.StandardTreePath => Scope.Trees,
        Refii.Models.Engine_ShaderFamily.StandardWindPath => Scope.Wind,
        Refii.Models.Engine_ShaderFamily.StandardPath => Scope.TwoSided,
        _ => Scope.None,
    };

    /// <summary>
    /// Whether a material pass on the lite build <paramref name="path"/> takes <see cref="Define"/>: a cut-out (alpha
    /// scissor or foliage) or grass pass on a build that can discard (two-sided, tree or wind) within
    /// <paramref name="scope"/>, not a character's. Pure.
    /// </summary>
    /// <remarks>
    /// A translucent material (diffuse flag 0x80) carries its blended fact on both passes: the opaque alpha-tested
    /// pass on the two-sided build is marked like any cut-out (unless the scope says `no_translucent`); its blended
    /// second pass is on the translucent build, which has no discard and is never marked. The blended pass then tests
    /// against the same depth as before.
    /// </remarks>
    public static bool Marks(string path, bool cutout, bool grass, bool blended, bool character, bool on, Scope scope = Scope.All) =>
        on && Row(path, cutout, grass, blended, character, scope) is Census.TwoSided or Census.Tree or Census.Wind or Census.TranslucentOpaque;

    // ---------------------------------------------------------------- census

    /// <summary>The census's rows: what a cut-out or grass pass built under the key became.</summary>
    public enum Census
    {
        TwoSided,
        Tree,
        Wind,
        TranslucentOpaque,
        OutOfScope,
        Character,
        OtherBuild,
    }

    private static readonly int[] Counts = new int[Enum.GetValues<Census>().Length];

    private static int _printedTotal = -1;

    private static System.Threading.Timer? _censusTimer;

    /// <summary>
    /// The census row a pass falls in under <paramref name="scope"/>, or null for a pass that is neither cut out nor
    /// grass. The first four rows are marked; a translucent material's opaque pass counts in its own row. Pure.
    /// </summary>
    public static Census? Row(string path, bool cutout, bool grass, bool blended, bool character, Scope scope = Scope.All)
    {
        if (!cutout && !grass)
        {
            return null;
        }

        if (character)
        {
            return Census.Character;
        }

        Scope build = BuildOf(path);
        if (build == Scope.None)
        {
            return Census.OtherBuild;
        }

        if ((scope & build) == 0 || (blended && !scope.HasFlag(Scope.Translucent)))
        {
            return Census.OutOfScope;
        }

        if (blended)
        {
            return Census.TranslucentOpaque;
        }

        return build switch
        {
            Scope.Trees => Census.Tree,
            Scope.Wind => Census.Wind,
            _ => Census.TwoSided,
        };
    }

    /// <summary>
    /// Counts one built material pass (Engine_MdlMaterial.SpecialisationDefines) while the key is on; a
    /// `[material] cut-out pre-pass census` line follows within 15 s of a change. Any thread.
    /// </summary>
    public static void Note(string path, bool cutout, bool grass, bool blended, bool character)
    {
        if (!On || Row(path, cutout, grass, blended, character, ChosenScope) is not { } row)
        {
            return;
        }

        System.Threading.Interlocked.Increment(ref Counts[(int)row]);
        if (_censusTimer is null)
        {
            var timer = new System.Threading.Timer(_ => PrintCensus(), null, 15000, 15000);
            if (System.Threading.Interlocked.CompareExchange(ref _censusTimer, timer, null) is not null)
            {
                timer.Dispose();
            }
        }
    }

    private static void PrintCensus()
    {
        int[] c = new int[Counts.Length];
        for (int i = 0; i < c.Length; ++i)
        {
            c[i] = System.Threading.Volatile.Read(ref Counts[i]);
        }

        int total = 0;
        foreach (int n in c)
        {
            total += n;
        }

        if (total == _printedTotal)
        {
            return;
        }

        _printedTotal = total;
        GD.Print(CensusLine(c, ChosenScope));
    }

    /// <summary>The census line for these counts (in <see cref="Census"/> order) under <paramref name="scope"/>. Pure.</summary>
    public static string CensusLine(IReadOnlyList<int> c, Scope scope)
    {
        int total = 0;
        foreach (int n in c)
        {
            total += n;
        }

        int marked = c[(int)Census.TwoSided] + c[(int)Census.Tree] + c[(int)Census.Wind] + c[(int)Census.TranslucentOpaque];
        return $"[material] cut-out pre-pass census, scope {ScopeText(scope)}: {marked} of {total} cut-out/grass material passes marked " +
            $"(two-sided {c[(int)Census.TwoSided]}, tree {c[(int)Census.Tree]}, wind {c[(int)Census.Wind]}, " +
            $"opaque pass of a translucent {c[(int)Census.TranslucentOpaque]}); not marked: out of scope {c[(int)Census.OutOfScope]}, " +
            $"character {c[(int)Census.Character]}, other build {c[(int)Census.OtherBuild]}";
    }

    /// <summary>
    /// <paramref name="code"/> with <see cref="RenderMode"/> in its render mode when <paramref name="defines"/> carry
    /// <see cref="Define"/>; otherwise unchanged. Pure.
    /// </summary>
    public static string Compose(string code, IReadOnlyList<string> defines)
    {
        foreach (string define in defines)
        {
            if (define == Define)
            {
                return Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(code, RenderMode);
            }
        }

        return code;
    }

    private static int? _applied;

    /// <summary>
    /// Sets <see cref="EngineSetting"/> to this process's choice, once, when the engine defines it (templates with
    /// patch 0017). Main thread (AndroidEnginePatchSwitches.Apply).
    /// </summary>
    public static void Apply()
    {
        int wanted = EngineValue(Chosen.Value.On, Chosen.Value.Equal);
        if (_applied == wanted)
        {
            return;
        }

        _applied = wanted;
        if (ProjectSettings.HasSetting(EngineSetting))
        {
            ProjectSettings.SetSetting(EngineSetting, wanted);
            GD.Print($"[android] engine {EngineSetting} = {wanted}");
        }
        else if (wanted != 0)
        {
            GD.Print($"[android] {Key}: the engine has no {EngineSetting} (templates without godot-patches/0017); the cut-outs draw as before");
        }
    }
}
