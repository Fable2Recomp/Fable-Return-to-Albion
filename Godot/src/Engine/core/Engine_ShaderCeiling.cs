using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Refii.Core;

/// <summary>
/// The debug channel's `shaderCeiling` lever: one category of the world drawn with a compile-time
/// minimal shader, or not at all, so a frame's cost splits into geometry and pixel work per
/// category. Off unless a request asks; a diagnostic, never a quality setting.
/// </summary>
/// <remarks>
/// <para>The modes, per category (props, trees, terrain, entities):
/// `off` the original materials; `flat` the original's own source cut to a constant unshaded colour
/// (Engine_ShaderCeilingSource: same cull, blend and depth words, the vertex stage only where it
/// moves the vertex); `flatcut` the same, but a material that alpha-tests keeps a discard on its
/// albedo's alpha, so early Z and LRZ behave as they did; `flatvs` flatcut's pixel stage with the
/// original's whole vertex stage kept and every varying and vertex built-in it hands on read, so
/// flatvs against flatcut prices the vertex work and its interpolation, and off against flatvs
/// the pixel work alone; `hide` the category not drawn at all;
/// `+word,word` the ORIGINAL shader with those render-mode words added (`+shadows_disabled`,
/// `+ambient_light_disabled`), to price what a word removes inside the real material — a word the
/// shader already states is skipped, and a material whose amended shader does not compile keeps
/// its own (counted as a fallback). A `define:NAME` word defines NAME ahead of the shader's
/// includes instead (`+define:FABLE_SOLID` compiles the standard body's two discards out; it may
/// change an alpha-tested surface's picture, which is the measurement).
/// `all` names the mode for every category not named; `"all":"unshaded"` is instead Godot's own
/// VIEWPORT_DEBUG_DRAW_UNSHADED on every viewport, which in 4.7 Mobile is NOT a flat ceiling: every
/// material's own fragment() still runs, and what goes is Godot's lighting — no lights, probes or
/// decals in the render data (so no light loops and no shadow passes), ambient forced to white,
/// camera effects off (renderer_scene_render_rd.cpp, render_scene_data_rd.cpp).</para>
/// <para>THE SWAP IS ON THE MATERIAL, not the instance: each ShaderMaterial a category draws is given
/// the replacement Shader and later its own back. A ShaderMaterial keeps its parameter values across
/// a shader change (param_cache, and the server's own params), and the replacement keeps every
/// declaration, so textures, thresholds and instance parameters bind as they did, and every path a
/// material is bound by — override, surface override, the mesh's own surface, a MultiMesh's mesh,
/// an overlay, a next pass — is covered at once. Restoring sets each material's own shader back,
/// so an A/B in one process is exact and can be repeated.</para>
/// <para>STREAMING: while any mode is on, the category roots are walked again a few hundred nodes a
/// frame, so cells streamed in and bodies re-dressed since the request are swapped too, and a copy
/// of a swapped material made meanwhile is recognised by its shader and restored with the rest. A
/// measurement should still be taken with streaming settled: the first draw of each replacement
/// compiles its pipelines. A body variant built while a ceiling is on keeps the merge decision it
/// made then (Engine_BodyVariants merges by shader path) until the region reloads.</para>
/// </remarks>
internal sealed class Engine_ShaderCeiling
{
	internal enum Category
	{
		Props,
		Trees,
		Terrain,
		Entities,
	}

	internal enum Mode
	{
		Off,
		Flat,
		FlatCut,
		Hide,
		FlatVs,
		RenderModes,
	}

	/// <summary>
	/// A parsed request: a mode per category, the render-mode words a `+` mode adds per category
	/// (null elsewhere), and whether the viewport draws unshaded.
	/// </summary>
	internal readonly record struct Request(Mode[] Modes, string[]?[] Words, bool Unshaded);

	internal const string Key = "shaderCeiling";

	internal static readonly string[] CategoryNames = { "props", "trees", "terrain", "entities" };

	private static readonly string[] ModeNames = { "off", "flat", "flatcut", "hide", "flatvs" };

	/// <summary>Each category's constant colour, so a screenshot says which one is swapped.</summary>
	private static readonly (float R, float G, float B)[] Colours =
	{
		(0.62f, 0.52f, 0.42f),
		(0.16f, 0.46f, 0.30f),
		(0.42f, 0.62f, 0.24f),
		(0.80f, 0.26f, 0.62f),
	};

	/// <summary>Nodes visited per frame while a mode is on: a few hundred keeps the walk off the frame.</summary>
	private const int NodesPerFrame = 384;

	/// <summary>Frames between the end of one walk and the start of the next.</summary>
	private const int FramesBetweenWalks = 60;

	private readonly Func<Node, IEnumerable<(Node Root, Category Category)>> _roots;
	private readonly Func<GeometryInstance3D, bool> _isTree;
	private readonly Mode[] _modes = new Mode[CategoryNames.Length];
	private readonly string[]?[] _words = new string[]?[CategoryNames.Length];

	/// <summary>Each swapped material, its own shader and the category that claimed it.</summary>
	private readonly Dictionary<ShaderMaterial, (Shader Original, Category Owner)> _swapped = new();

	/// <summary>The shader each replacement stands for: how a copy made while swapped is known.</summary>
	private readonly Dictionary<Shader, Shader> _originalOf = new();

	/// <summary>Each shader's replacement per mode and category, built once; null where none applies.</summary>
	private readonly Dictionary<(Shader Original, string Variant), Shader?> _replacements = new();
	private readonly Dictionary<Node3D, bool> _hidden = new();
	private readonly Dictionary<Viewport, Viewport.DebugDrawEnum> _debugDraw = new();

	private readonly HashSet<ulong>[] _nodes = NewSets();
	private readonly long[] _instances = new long[CategoryNames.Length];
	private readonly int[] _hiddenNodes = new int[CategoryNames.Length];
	private readonly HashSet<ulong> _skipped = new();
	private readonly HashSet<ulong> _shared = new();

	/// <summary>Materials drawn by any node with a fade band or an arrival under way: their dither discards.</summary>
	private readonly HashSet<ShaderMaterial> _banded = new();

	private readonly Dictionary<Shader, Refii.Models.Engine_ShaderCeilingSource.Facts?> _facts = new();
	private readonly List<(Node Node, Category Category, bool Root)> _walk = new();
	private int _fallbacks;
	private int _wordsPresent;
	private int _keptVertex;
	private int _cut;
	private int _walks;
	private int _idleFrames;

	internal Engine_ShaderCeiling(
		Func<Node, IEnumerable<(Node Root, Category Category)>> roots, Func<GeometryInstance3D, bool> isTree)
	{
		_roots = roots;
		_isTree = isTree;
	}

	/// <summary>
	/// Reads the optional `shaderCeiling` key: a string (one mode for every category, or "unshaded")
	/// or an object of `props`, `trees`, `terrain`, `entities` and `all`. Each request states the
	/// whole ceiling: a category it does not name takes `all`'s mode, or `off`.
	/// </summary>
	/// <returns>Null when the key is absent.</returns>
	/// <exception cref="JsonException">A value is not one of the modes.</exception>
	internal static Request? Parse(JsonElement request)
	{
		if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(Key, out JsonElement value))
		{
			return null;
		}

		var modes = new Mode[CategoryNames.Length];
		var words = new string[]?[CategoryNames.Length];
		bool unshaded = false;
		if (value.ValueKind == JsonValueKind.String)
		{
			ReadAll(value, modes, words, ref unshaded);
			return new Request(modes, words, unshaded);
		}
		if (value.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException($"{Key} must be a mode string or an object of categories.");
		}

		if (value.TryGetProperty("all", out JsonElement all))
		{
			ReadAll(all, modes, words, ref unshaded);
		}
		foreach (JsonProperty property in value.EnumerateObject())
		{
			if (property.Name == "all")
			{
				continue;
			}
			int category = Array.IndexOf(CategoryNames, property.Name);
			if (category < 0)
			{
				throw new JsonException($"{Key}: unknown category '{property.Name}' (props, trees, terrain, entities, all).");
			}
			modes[category] = ModeOf(property.Value, out words[category]);
		}
		return new Request(modes, words, unshaded);
	}

	private static void ReadAll(JsonElement value, Mode[] modes, string[]?[] words, ref bool unshaded)
	{
		if (value.ValueKind == JsonValueKind.String && value.GetString() == "unshaded")
		{
			unshaded = true;
			return;
		}
		Array.Fill(modes, ModeOf(value, out string[]? added));
		Array.Fill(words, added);
	}

	private static Mode ModeOf(JsonElement value, out string[]? words)
	{
		words = null;
		string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		if (text is { Length: > 1 } && text[0] == '+')
		{
			var list = new List<string>();
			foreach (string word in text[1..].Split(','))
			{
				string trimmed = word.Trim();
				if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^(?:[a-z][a-z0-9_]*|define:[A-Za-z_][A-Za-z0-9_]*)$"))
				{
					throw new JsonException($"{Key}: '{trimmed}' is neither a render-mode word nor define:NAME (\"+shadows_disabled,define:FABLE_SOLID\").");
				}
				if (!list.Contains(trimmed))
				{
					list.Add(trimmed);
				}
			}
			words = list.ToArray();
			return Mode.RenderModes;
		}
		int mode = text is null ? -1 : Array.IndexOf(ModeNames, text);
		return mode >= 0
			? (Mode)mode
			: throw new JsonException($"{Key}: a mode is one of off, flat, flatcut, flatvs, hide, +word[,word] (and \"all\":\"unshaded\").");
	}

	/// <summary>A mode as the reply states it: `+` modes with their words.</summary>
	private string ModeName(int category) =>
		_modes[category] == Mode.RenderModes ? "+" + string.Join(",", _words[category] ?? Array.Empty<string>()) : ModeNames[(int)_modes[category]];

	/// <summary>
	/// Restores whatever the last request changed, then applies this one over the whole of the
	/// current world at once; the per-frame walk keeps it applied afterwards.
	/// </summary>
	/// <param name="root">The scene tree's root.</param>
	/// <param name="viewports">The viewports `unshaded` applies to: the main one and the mirrors'.</param>
	internal void Apply(Request request, Node root, IEnumerable<Viewport> viewports)
	{
		Restore(root);
		Array.Copy(request.Modes, _modes, _modes.Length);
		Array.Copy(request.Words, _words, _words.Length);
		if (request.Unshaded)
		{
			foreach (Viewport viewport in viewports)
			{
				if (GodotObject.IsInstanceValid(viewport) && !_debugDraw.ContainsKey(viewport))
				{
					_debugDraw[viewport] = viewport.DebugDraw;
					viewport.DebugDraw = Viewport.DebugDrawEnum.Unshaded;
				}
			}
		}

		if (Array.Exists(_modes, m => m != Mode.Off))
		{
			Queue(root);
			Walk(int.MaxValue);
			_walks = 1;
		}
	}

	/// <summary>Per frame: a bounded step of the walk while a mode is on; nothing at all otherwise.</summary>
	internal void Tick(Node root)
	{
		if (!Array.Exists(_modes, m => m != Mode.Off))
		{
			return;
		}
		if (_walk.Count == 0)
		{
			if (++_idleFrames < FramesBetweenWalks)
			{
				return;
			}
			_idleFrames = 0;
			Queue(root);
			++_walks;
		}
		Walk(NodesPerFrame);
	}

	/// <summary>The state for the render reply: modes, and what was swapped or hidden per category.</summary>
	internal Dictionary<string, object?> Describe()
	{
		var modes = new Dictionary<string, object?>();
		var categories = new Dictionary<string, object?>();
		var materials = new int[CategoryNames.Length];
		var discarding = new int[CategoryNames.Length];
		var solid = new int[CategoryNames.Length];
		foreach ((ShaderMaterial material, (Shader original, Category owner)) in _swapped)
		{
			if (!GodotObject.IsInstanceValid(material) || FactsOf(original) is not { Discards: true } facts)
			{
				continue;
			}
			++discarding[(int)owner];
			Variant scissor = material.GetShaderParameter("alpha_scissor_on");
			if (facts.ScissorGate && !(scissor.VariantType == Variant.Type.Bool && scissor.AsBool()) &&
				!_banded.Contains(material))
			{
				++solid[(int)owner];
			}
		}
		foreach ((Shader _, Category owner) in _swapped.Values)
		{
			++materials[(int)owner];
		}
		for (int c = 0; c < CategoryNames.Length; c++)
		{
			modes[CategoryNames[c]] = ModeName(c);
			categories[CategoryNames[c]] = new Dictionary<string, object?>
			{
				["materials"] = materials[c],
				["nodes"] = _nodes[c].Count,
				["instances"] = _instances[c],
				["hidden"] = _hiddenNodes[c],
				// Of the swapped materials: those whose own shader compiles a discard, and of those the
				// standard-body ones with alpha_scissor_on false and no node drawing them with a fade
				// band or an arrival (xr_tree_fade_out.x > 0, xr_fade_alpha < 1) — solid without a change.
				["discardMaterials"] = discarding[c],
				["solidCandidates"] = solid[c],
			};
		}
		return new Dictionary<string, object?>
		{
			["modes"] = modes,
			["unshaded"] = _debugDraw.Count != 0,
			["swapped"] = categories,
			// The replacements built in this process (cached across requests): how many keep the
			// original's vertex stage, how many keep a cutout, and how many could only be drawn plain.
			["replacementShaders"] = _originalOf.Count,
			["replacementsKeepingVertex"] = _keptVertex,
			["replacementsCut"] = _cut,
			["replacementFallbacks"] = _fallbacks,
			// `+` modes: shaders that already stated every word, so their materials were left alone.
			["renderModesAlreadyStated"] = _wordsPresent,
			["skippedMaterials"] = _skipped.Count,
			["sharedMaterials"] = _shared.Count,
			["walks"] = _walks,
		};
	}

	private void Queue(Node root)
	{
		_walk.Clear();
		foreach ((Node node, Category category) in _roots(root))
		{
			_walk.Add((node, category, true));
		}
	}

	private void Walk(int budget)
	{
		while (_walk.Count != 0 && budget-- > 0)
		{
			(Node node, Category category, bool root) = _walk[^1];
			_walk.RemoveAt(_walk.Count - 1);
			if (!GodotObject.IsInstanceValid(node))
			{
				continue;
			}
			if (!Visit(node, category, root))
			{
				continue;
			}
			foreach (Node child in node.GetChildren())
			{
				_walk.Add((child, category, false));
			}
		}
	}

	/// <summary>One node under the mode its category asks; false when its children need no visit.</summary>
	private bool Visit(Node node, Category category, bool root)
	{
		// A root whose every category hides goes as one node: nothing under it is drawn.
		if (root && node is Node3D container && (category == Category.Props
			? _modes[(int)Category.Props] == Mode.Hide && _modes[(int)Category.Trees] == Mode.Hide
			: _modes[(int)category] == Mode.Hide))
		{
			Hide(container, category);
			return false;
		}
		if (node is not GeometryInstance3D geometry)
		{
			return true;
		}

		if (category == Category.Props && _isTree(geometry))
		{
			category = Category.Trees;
		}
		switch (_modes[(int)category])
		{
			case Mode.Flat:
			case Mode.FlatCut:
			case Mode.FlatVs:
			case Mode.RenderModes:
				Swap(geometry, category);
				break;
			case Mode.Hide:
				Hide(geometry, category);
				break;
		}
		return true;
	}

	private void Hide(Node3D node, Category category)
	{
		if (!node.Visible)
		{
			return;
		}
		// The first value seen is the one restored: a later re-hide after something showed the
		// node again keeps it.
		if (_hidden.TryAdd(node, true))
		{
			++_hiddenNodes[(int)category];
		}
		node.Visible = false;
	}

	/// <summary>Every material the node draws with, each given the category's replacement.</summary>
	private void Swap(GeometryInstance3D geometry, Category category)
	{
		bool any = false;
		bool banded = HasFadeBand(geometry);
		if (geometry.MaterialOverride is { } single)
		{
			any |= SwapChain(single, category, banded);
		}
		else if (geometry is MeshInstance3D { Mesh: { } mesh } instance)
		{
			for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			{
				if ((instance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s)) is { } surface)
				{
					any |= SwapChain(surface, category, banded);
				}
			}
		}
		else if (geometry is MultiMeshInstance3D { Multimesh: { Mesh: { } batched } })
		{
			for (int s = 0; s < batched.GetSurfaceCount(); s++)
			{
				if (batched.SurfaceGetMaterial(s) is { } surface)
				{
					any |= SwapChain(surface, category, banded);
				}
			}
		}
		if (geometry.MaterialOverlay is { } overlay)
		{
			any |= SwapChain(overlay, category, banded);
		}

		if (any && _nodes[(int)category].Add(geometry.GetInstanceId()))
		{
			_instances[(int)category] += geometry is MultiMeshInstance3D { Multimesh: { } multi }
				? multi.VisibleInstanceCount >= 0 ? multi.VisibleInstanceCount : multi.InstanceCount
				: 1;
		}
	}

	/// <summary>A material and its next passes; true when any of them now draws a replacement.</summary>
	private bool SwapChain(Material first, Category category, bool banded)
	{
		bool any = false;
		int guard = 0;
		for (Material? pass = first; pass is not null && guard++ < 8; pass = pass.NextPass)
		{
			if (pass is not ShaderMaterial shaded || shaded.Shader is not { } current)
			{
				_skipped.Add(pass.GetInstanceId());
				continue;
			}
			if (banded)
			{
				_banded.Add(shaded);
			}

			if (_swapped.TryGetValue(shaded, out (Shader Original, Category Owner) record))
			{
				// Claimed already, by this walk or another category's: it draws that one's replacement.
				if (record.Owner != category)
				{
					_shared.Add(shaded.GetInstanceId());
				}
				any = true;
				continue;
			}

			// A copy of a swapped material, made while the ceiling was on: its shader names its original.
			Shader original = _originalOf.TryGetValue(current, out Shader? own) ? own : current;
			if (Replacement(original, category) is not { } replacement)
			{
				// A `+` mode with nothing to add, or whose shader would not compile, keeps its own.
				if (_modes[(int)category] != Mode.RenderModes)
				{
					_skipped.Add(shaded.GetInstanceId());
				}
				continue;
			}
			_swapped[shaded] = (original, category);
			shaded.Shader = replacement;
			any = true;
		}
		return any;
	}

	/// <summary>
	/// The replacement for one shader, built once per mode and category (per word list for a `+`
	/// mode); null if not spatial, or for a `+` mode with nothing to add or that did not compile.
	/// </summary>
	private Shader? Replacement(Shader original, Category category)
	{
		Mode mode = _modes[(int)category];
		bool vertex = mode == Mode.FlatVs;
		bool cut = mode is Mode.FlatCut or Mode.FlatVs;
		string variant = mode == Mode.RenderModes
			? ModeName((int)category)
			: $"{ModeNames[(int)mode]}:{CategoryNames[(int)category]}";
		if (_replacements.TryGetValue((original, variant), out Shader? cached))
		{
			return cached;
		}

		string code = original.Code ?? string.Empty;
		if (!Refii.Models.Engine_ShaderCeilingSource.IsSpatial(code))
		{
			_replacements[(original, variant)] = null;
			return null;
		}
		if (mode == Mode.RenderModes)
		{
			Shader? amended = WithRenderModes(original, code, _words[(int)category] ?? Array.Empty<string>());
			_replacements[(original, variant)] = amended;
			return amended;
		}

		(float R, float G, float B) colour = Colours[(int)category];
		Refii.Models.Engine_ShaderCeilingSource.Built? built = null;
		string? failure = null;
		try
		{
			built = Refii.Models.Engine_ShaderCeilingSource.Build(
				code, original.ResourcePath ?? string.Empty, ReadInclude, cut, colour, CurrentRenderer(), vertex);
		}
		catch (Exception e)
		{
			failure = e.Message;
		}

		var replacement = new Shader { Code = built?.Code ?? Refii.Models.Engine_ShaderCeilingSource.Plain(code, colour) };
		// A FAILED COMPILE STILL RETURNS A Shader; what tells is the uniform list, which only a
		// compile fills (tools/ShaderCheck.cs). The cut-down source keeps every declaration, so it
		// lists what the original lists, or it did not compile.
		if (built is not null && replacement.GetShaderUniformList().Count != original.GetShaderUniformList().Count)
		{
			failure = "the cut-down source did not compile (SHADER ERROR above)";
			built = null;
			replacement.Code = Refii.Models.Engine_ShaderCeilingSource.Plain(code, colour);
		}
		if (built is { } done)
		{
			_keptVertex += done.KeptVertex ? 1 : 0;
			_cut += done.Cut ? 1 : 0;
		}
		else
		{
			++_fallbacks;
			GD.Print($"[shaderCeiling] {original.ResourcePath}: drawn plain ({failure ?? "unresolved"})");
		}

		_originalOf[replacement] = original;
		_replacements[(original, variant)] = replacement;
		return replacement;
	}

	/// <summary>The original code with the words added, or null: nothing to add, or no compile (a fallback).</summary>
	private Shader? WithRenderModes(Shader original, string code, string[] words)
	{
		string? amended;
		try
		{
			amended = Refii.Models.Engine_ShaderCeilingSource.WithRenderModes(
				code, original.ResourcePath ?? string.Empty, ReadInclude, words, CurrentRenderer());
		}
		catch (Exception e)
		{
			++_fallbacks;
			GD.Print($"[shaderCeiling] {original.ResourcePath}: keeps its own shader ({e.Message})");
			return null;
		}
		if (amended is null)
		{
			++_wordsPresent;
			return null;
		}

		var replacement = new Shader { Code = amended };
		// The same compile test as the flat build: an amended shader lists what the original lists.
		// A define may compile a uniform out (FABLE_LITE drops parallax), so with one only an empty
		// list, where the original has uniforms, tells a failed compile.
		int listed = replacement.GetShaderUniformList().Count;
		int own = original.GetShaderUniformList().Count;
		bool defines = Array.Exists(words, w => w.StartsWith(Refii.Models.Engine_ShaderCeilingSource.DefinePrefix, StringComparison.Ordinal));
		if (defines ? listed == 0 && own != 0 : listed != own)
		{
			++_fallbacks;
			GD.Print($"[shaderCeiling] {original.ResourcePath}: keeps its own shader (+{string.Join(",", words)} did not compile, SHADER ERROR above)");
			return null;
		}
		_originalOf[replacement] = original;
		return replacement;
	}

	/// <summary>The census facts of an original shader, read once; null where its source does not resolve.</summary>
	private Refii.Models.Engine_ShaderCeilingSource.Facts? FactsOf(Shader original)
	{
		if (!_facts.TryGetValue(original, out Refii.Models.Engine_ShaderCeilingSource.Facts? facts))
		{
			try
			{
				facts = Refii.Models.Engine_ShaderCeilingSource.Inspect(
					original.Code ?? string.Empty, original.ResourcePath ?? string.Empty, ReadInclude, CurrentRenderer());
			}
			catch (Exception)
			{
				facts = null;
			}
			_facts[original] = facts;
		}
		return facts;
	}

	/// <summary>Whether the node's own instance parameters put it in a dither: a fade band, or an arrival.</summary>
	private static bool HasFadeBand(GeometryInstance3D geometry)
	{
		Variant band = geometry.GetInstanceShaderParameter("xr_tree_fade_out");
		Variant arrival = geometry.GetInstanceShaderParameter("xr_fade_alpha");
		return (band.VariantType == Variant.Type.Vector4 && band.AsVector4().X > 0f) ||
			(arrival.VariantType == Variant.Type.Float && arrival.AsSingle() < 0.999f);
	}

	private static string? ReadInclude(string path) =>
		ResourceLoader.Exists(path) ? ResourceLoader.Load<ShaderInclude>(path)?.Code : null;

	private static int CurrentRenderer() => RenderingServer.GetCurrentRenderingMethod() switch
	{
		"forward_plus" => Refii.Models.Engine_ShaderCeilingSource.RendererForwardPlus,
		"mobile" => Refii.Models.Engine_ShaderCeilingSource.RendererMobile,
		_ => Refii.Models.Engine_ShaderCeilingSource.RendererCompatibility,
	};

	/// <summary>
	/// Every material back on its own shader, every hidden node shown, the viewports' debug draw
	/// returned, and the counts cleared. A material whose shader something else changed meanwhile
	/// is left with that one.
	/// </summary>
	private void Restore(Node root)
	{
		foreach ((ShaderMaterial material, (Shader original, Category _)) in _swapped)
		{
			if (GodotObject.IsInstanceValid(material) && material.Shader is { } now && _originalOf.ContainsKey(now))
			{
				material.Shader = original;
			}
		}
		_swapped.Clear();

		// Copies made since the last walk carry a replacement nothing recorded: find them by it.
		if (_originalOf.Count != 0 && Array.Exists(_modes, m => m is Mode.Flat or Mode.FlatCut or Mode.FlatVs or Mode.RenderModes))
		{
			Queue(root);
			while (_walk.Count != 0)
			{
				(Node node, Category _, bool _) = _walk[^1];
				_walk.RemoveAt(_walk.Count - 1);
				if (!GodotObject.IsInstanceValid(node))
				{
					continue;
				}
				if (node is GeometryInstance3D geometry)
				{
					RestoreCopies(geometry);
				}
				foreach (Node child in node.GetChildren())
				{
					_walk.Add((child, Category.Props, false));
				}
			}
		}

		foreach ((Node3D node, bool visible) in _hidden)
		{
			if (GodotObject.IsInstanceValid(node) && !node.Visible)
			{
				node.Visible = visible;
			}
		}
		_hidden.Clear();

		foreach ((Viewport viewport, Viewport.DebugDrawEnum before) in _debugDraw)
		{
			if (GodotObject.IsInstanceValid(viewport) && viewport.DebugDraw == Viewport.DebugDrawEnum.Unshaded)
			{
				viewport.DebugDraw = before;
			}
		}
		_debugDraw.Clear();

		Array.Fill(_modes, Mode.Off);
		Array.Fill(_words, null);
		foreach (HashSet<ulong> set in _nodes)
		{
			set.Clear();
		}
		Array.Clear(_instances);
		Array.Clear(_hiddenNodes);
		_skipped.Clear();
		_shared.Clear();
		_banded.Clear();
		_walk.Clear();
		_walks = 0;
		_idleFrames = 0;
	}

	private void RestoreCopies(GeometryInstance3D geometry)
	{
		void Back(Material? first)
		{
			int guard = 0;
			for (Material? pass = first; pass is not null && guard++ < 8; pass = pass.NextPass)
			{
				if (pass is ShaderMaterial { Shader: { } now } shaded && _originalOf.TryGetValue(now, out Shader? original))
				{
					shaded.Shader = original;
				}
			}
		}

		Back(geometry.MaterialOverride);
		Back(geometry.MaterialOverlay);
		if (geometry is MeshInstance3D { Mesh: { } mesh } instance)
		{
			for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			{
				Back(instance.GetSurfaceOverrideMaterial(s));
				Back(mesh.SurfaceGetMaterial(s));
			}
		}
		else if (geometry is MultiMeshInstance3D { Multimesh: { Mesh: { } batched } })
		{
			for (int s = 0; s < batched.GetSurfaceCount(); s++)
			{
				Back(batched.SurfaceGetMaterial(s));
			}
		}
	}

	private static HashSet<ulong>[] NewSets()
	{
		var sets = new HashSet<ulong>[CategoryNames.Length];
		for (int i = 0; i < sets.Length; i++)
		{
			sets[i] = new HashSet<ulong>();
		}
		return sets;
	}
}
