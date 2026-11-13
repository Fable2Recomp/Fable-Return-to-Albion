using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Refii.Models;

/// <summary>
/// The replacement source the `shaderCeiling` diagnostic draws a category with (Engine_ShaderCeiling):
/// a spatial shader's own code cut down to the least a draw of it can do, so the difference
/// against the original prices the original's FRAGMENT work and what is left prices the geometry.
/// </summary>
/// <remarks>
/// COMPILE TIME, NOT A UNIFORM. A bypass behind a runtime branch measured nothing on the Adreno 640
/// (the registers are allocated for the worst path whatever is taken), so the fragment work is
/// removed from the source: `fragment()` and `light()` are replaced, `unshaded` is added, and every
/// function nothing calls any more is dropped. What stays is what the draw is placed and rasterised
/// by: the render mode's blend, depth and cull words, every declaration (so the material's own
/// parameters and the instances' parameters still bind by name), and `vertex()` only where it
/// moves the vertex (wind, tree sway, a classic position) — with its writes to varyings removed,
/// so the per-vertex lighting it fed (the PRT transfer) goes with the fragment that read it.
///
/// THE INCLUDES ARE RESOLVED HERE, conditionals included, because the bodies are shared files
/// compiled under defines: which `fragment()` a shader has, and whether its vertex moves, is only
/// known after `#ifdef FABLE_WIND` is decided. `#define` lines are kept as written, so Godot still
/// expands every macro; only the conditionals and includes are taken out.
/// </remarks>
internal static class Engine_ShaderCeilingSource
{
	/// <summary>A replacement: its code, whether the vertex stage was kept, whether a discard was kept.</summary>
	internal readonly record struct Built(string Code, bool KeptVertex, bool AlphaTested, bool Cut, bool WritesAlpha);

	/// <summary>Godot's built-in `CURRENT_RENDERER` values (shader_preprocessor.cpp).</summary>
	internal const int RendererCompatibility = 0;

	internal const int RendererMobile = 1;

	internal const int RendererForwardPlus = 2;

	private static readonly Regex SpatialType = new(@"\bshader_type\s+spatial\s*;", RegexOptions.CultureInvariant);

	private static readonly Regex FunctionHeader = new(
		@"^[\w\s\[\]]*?\b([A-Za-z_]\w*)\s*\(([^()]*)\)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

	private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.CultureInvariant);

	private static readonly Regex VaryingDeclaration = new(
		@"^varying\s+(?:(?:flat|smooth|highp|mediump|lowp|[A-Z][A-Z0-9_]*)\s+)*\w+\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

	private static readonly Regex Sampler2D = new(
		@"^uniform\s+(?:(?:highp|mediump|lowp)\s+)?sampler2D\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

	private static readonly Regex BoolUniform = new(@"^uniform\s+bool\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

	private static readonly Regex FloatUniform = new(
		@"^uniform\s+(?:(?:highp|mediump|lowp)\s+)?float\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

	/// <summary>A write to the drawn position: `VERTEX = `, `VERTEX += `, `POSITION.xy = `, not `==`.</summary>
	private static readonly Regex PositionWrite = new(
		@"\b(?:VERTEX|POSITION)\b\s*(?:\.\s*[xyzw]+)?\s*[-+*/]?=(?!=)", RegexOptions.CultureInvariant);

	private static readonly Regex AlphaWrite = new(@"\bALPHA\b\s*[-+*/]?=(?!=)", RegexOptions.CultureInvariant);

	private static readonly Regex ScissorWrite = new(
		@"\b(?:ALPHA_SCISSOR_THRESHOLD|ALPHA_HASH_SCALE)\b\s*=(?!=)", RegexOptions.CultureInvariant);

	private static readonly Regex Discard = new(@"\bdiscard\b", RegexOptions.CultureInvariant);

	/// <summary>Render-mode words that place or rasterise the draw; everything else is shading.</summary>
	private static readonly string[] KeptModePrefixes =
	{
		"blend_", "depth_draw_", "depth_test_", "depth_prepass_alpha", "cull_", "alpha_to_coverage",
		"wireframe", "particle_trails", "fog_disabled",
	};

	/// <summary>The spatial render-mode groups of which a shader may state one word (shader_types.cpp).</summary>
	private static readonly string[][] ExclusiveModeGroups =
	{
		new[] { "blend_mix", "blend_add", "blend_sub", "blend_mul", "blend_premul_alpha" },
		new[] { "depth_draw_opaque", "depth_draw_always", "depth_draw_never" },
		new[] { "depth_test_default", "depth_test_disabled", "depth_test_inverted" },
		new[] { "cull_back", "cull_front", "cull_disabled" },
		new[] { "diffuse_lambert", "diffuse_lambert_wrap", "diffuse_burley", "diffuse_toon" },
		new[] { "specular_schlick_ggx", "specular_toon", "specular_disabled" },
	};

	/// <summary>Words that change what `vertex()` means; kept only with it.</summary>
	private static readonly string[] VertexModes = { "skip_vertex_transform", "world_vertex_coords" };

	/// <summary>Whether <paramref name="code"/> states a spatial shader; nothing else is replaced.</summary>
	internal static bool IsSpatial(string? code) => code is not null && SpatialType.IsMatch(StripComments(code));

	/// <summary>
	/// The replacement for one shader. Null when the source is not a spatial shader. Throws when the
	/// source cannot be resolved (a missing include, a conditional it cannot evaluate); the caller
	/// then draws <see cref="Plain"/>.
	/// </summary>
	/// <param name="code">The shader's own code, includes unresolved.</param>
	/// <param name="path">Its resource path, for relative includes; may be empty.</param>
	/// <param name="readInclude">An include's code by `res://` path, or null when it does not exist.</param>
	/// <param name="cut">Keep an alpha test where the original has one (`flatcut`).</param>
	/// <param name="albedo">The constant colour, as three numbers 0..1.</param>
	/// <param name="renderer">Godot's `CURRENT_RENDERER` for the conditionals.</param>
	/// <param name="keepVaryings">`flatvs`: keep the whole vertex stage, its varying writes included,
	/// and read in the pixel stage every varying it writes and every vertex built-in the original's
	/// pixel and light stages read, so the compiler can drop none of that work.</param>
	internal static Built? Build(
		string code, string path, Func<string, string?> readInclude, bool cut, (float R, float G, float B) albedo,
		int renderer = RendererMobile, bool keepVaryings = false)
	{
		string active = Preprocess(code, path, readInclude, renderer);
		if (!SpatialType.IsMatch(active))
		{
			return null;
		}

		List<Item> items = Split(active);
		var macros = new Dictionary<string, string>(StringComparer.Ordinal);
		var functions = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
		var varyings = new HashSet<string>(StringComparer.Ordinal);
		var varyingTypes = new Dictionary<string, string>(StringComparer.Ordinal);
		var samplers = new List<string>();
		var bools = new HashSet<string>(StringComparer.Ordinal);
		var floats = new HashSet<string>(StringComparer.Ordinal);
		var modes = new List<string>();
		foreach (Item item in items)
		{
			switch (item.Kind)
			{
				case ItemKind.Directive when MacroOf(item.Text) is { } macro:
					macros[macro.Name] = macro.Body;
					break;
				case ItemKind.Function:
					if (!functions.TryGetValue(item.Name, out List<Item>? overloads))
					{
						functions[item.Name] = overloads = new List<Item>();
					}
					overloads.Add(item);
					break;
				case ItemKind.Declaration:
				{
					string text = item.Text.Trim();
					if (text.StartsWith("render_mode", StringComparison.Ordinal))
					{
						foreach (string word in text["render_mode".Length..].TrimEnd(';').Split(','))
						{
							if (word.Trim() is { Length: > 0 } trimmed)
							{
								modes.Add(trimmed);
							}
						}
					}
					else if (VaryingDeclaration.Match(text) is { Success: true } varying)
					{
						varyings.Add(varying.Groups[1].Value);
						if (TypedVarying.Match(text) is { Success: true } typed)
						{
							varyingTypes[typed.Groups[2].Value] = typed.Groups[1].Value;
						}
					}
					else if (Sampler2D.Match(text) is { Success: true } sampler)
					{
						samplers.Add(sampler.Groups[1].Value);
					}
					else if (BoolUniform.Match(text) is { Success: true } flag)
					{
						bools.Add(flag.Groups[1].Value);
					}
					else if (FloatUniform.Match(text) is { Success: true } scalar)
					{
						floats.Add(scalar.Groups[1].Value);
					}
					break;
				}
			}
		}

		// What the original's pixel stage does that decides where its draw lands: a discard or a
		// scissor (no early depth, no LRZ write) and an ALPHA write (the transparent list).
		string fragmentReach = Reach(new[] { "fragment" }, functions, macros, includeMacros: true);
		bool discards = Discard.IsMatch(fragmentReach);
		bool scissor = ScissorWrite.IsMatch(fragmentReach);
		bool writesAlpha = AlphaWrite.IsMatch(fragmentReach) && !scissor;
		bool alphaTested = discards || scissor;

		// The vertex stage stays only where it moves the vertex; its varyings feed nothing now.
		// Under `flatvs` it stays whole, and the new pixel stage reads what it hands on.
		bool keepVertex = false;
		string? keep = null;
		if (functions.TryGetValue("vertex", out List<Item>? vertex))
		{
			if (keepVaryings)
			{
				string pixelReach = Reach(new[] { "fragment", "light" }, functions, macros, includeMacros: true);
				keep = KeptReads(vertex, varyings, varyingTypes, pixelReach);
				keepVertex = true;
			}
			else
			{
				foreach (Item overload in vertex)
				{
					overload.Text = StripVaryingWrites(overload.Text, varyings);
				}
				keepVertex = PositionWrite.IsMatch(Reach(new[] { "vertex" }, functions, macros, includeMacros: true));
			}
		}

		string fragment = Fragment(cut && alphaTested, discards, scissor, writesAlpha, samplers, bools, floats, albedo, keep);

		// Every function the kept stages no longer reach goes: one using a pixel built-in would
		// not compile uncalled, and one that is never called costs nothing anyway. The replaced
		// stages are no roots: the new `fragment()` names itself.
		functions.Remove("fragment");
		functions.Remove("light");
		if (!keepVertex)
		{
			functions.Remove("vertex");
		}
		var roots = new List<string>();
		if (keepVertex)
		{
			roots.Add("vertex");
		}
		foreach (Item item in items)
		{
			if (item.Kind == ItemKind.Declaration)
			{
				foreach (Match name in Identifier.Matches(item.Text))
				{
					roots.Add(name.Value);
				}
			}
		}
		foreach (Match name in Identifier.Matches(fragment))
		{
			roots.Add(name.Value);
		}
		HashSet<string> reached = Reached(roots, functions, macros);

		var output = new StringBuilder();
		output.Append("// shaderCeiling: ").Append(keepVaryings ? "flatvs" : cut && alphaTested ? "flatcut" : "flat").Append(" of ")
			.Append(path.Length != 0 ? path : "an unnamed shader").Append(keepVertex ? ", its vertex kept" : ", plain vertex")
			.Append('\n');
		bool modeWritten = false;
		foreach (Item item in items)
		{
			switch (item.Kind)
			{
				case ItemKind.Function when item.Name is "fragment" or "light":
				case ItemKind.Function when item.Name == "vertex" && !keepVertex:
				case ItemKind.Function when !reached.Contains(item.Name):
					continue;
				case ItemKind.Declaration when item.Text.TrimStart().StartsWith("render_mode", StringComparison.Ordinal):
					continue;
			}

			output.Append(item.Text.Trim()).Append('\n');
			if (!modeWritten && item.Kind == ItemKind.Declaration && SpatialType.IsMatch(item.Text + ";"))
			{
				output.Append(RenderMode(modes, keepVertex)).Append('\n');
				modeWritten = true;
			}
		}
		output.Append(fragment);
		return new Built(output.ToString(), keepVertex, alphaTested, cut && alphaTested, writesAlpha);
	}

	/// <summary>The prefix of a `+` word that defines a preprocessor name instead of a render mode.</summary>
	internal const string DefinePrefix = "define:";

	/// <summary>
	/// The shader's ORIGINAL code with render-mode words added (the `+mode` ceiling): merged into
	/// its own top-level `render_mode`, or stated after `shader_type` where only an include states
	/// one. A word the shader already states anywhere, its includes included, is skipped — Godot
	/// refuses a duplicated mode. A `define:NAME` word puts `#define NAME` in the top-level text
	/// after shader_type and render_mode and before the first `#include`, so the included body
	/// compiles under it (`define:FABLE_SOLID`); a name the shader already defines is skipped.
	/// Relative includes are made absolute, since the copy has no path.
	/// </summary>
	/// <returns>The code, or null when every word is already stated and nothing would change.</returns>
	internal static string? WithRenderModes(
		string code, string path, Func<string, string?> readInclude, IReadOnlyList<string> modes,
		int renderer = RendererMobile)
	{
		string active = Preprocess(code, path, readInclude, renderer);
		var stated = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match statement in Regex.Matches(active, @"\brender_mode\b([^;]*);", RegexOptions.CultureInvariant))
		{
			foreach (string word in statement.Groups[1].Value.Split(','))
			{
				stated.Add(word.Trim());
			}
		}
		var defined = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match define in Regex.Matches(active, @"(?m)^\s*#\s*define\s+([A-Za-z_]\w*)", RegexOptions.CultureInvariant))
		{
			defined.Add(define.Groups[1].Value);
		}

		var missing = new List<string>();
		var defines = new List<string>();
		foreach (string mode in modes)
		{
			if (mode.StartsWith(DefinePrefix, StringComparison.Ordinal))
			{
				string name = mode[DefinePrefix.Length..];
				if (!defined.Contains(name) && !defines.Contains(name))
				{
					defines.Add(name);
				}
			}
			else if (!stated.Contains(mode) && !missing.Contains(mode))
			{
				missing.Add(mode);
			}
		}
		if (missing.Count == 0 && defines.Count == 0)
		{
			return null;
		}
		// One word per group (shader_language.cpp, "Redefinition of render mode"): refused here, so a
		// shader that declares no uniform, whose failed compile the caller could not see, falls back too.
		foreach (string word in missing)
		{
			string[]? group = Array.Find(ExclusiveModeGroups, g => Array.IndexOf(g, word) >= 0);
			string? clash = group is null ? null : Array.Find(group, stated.Contains);
			if (clash is not null)
			{
				throw new FormatException($"render mode '{word}' conflicts with the shader's '{clash}'");
			}
		}

		string source = Regex.Replace(
			StripComments(code),
			@"(?m)^(\s*#\s*include\s+"")([^""]+)("")",
			m => m.Groups[1].Value + Resolve(path, m.Groups[2].Value) + m.Groups[3].Value,
			RegexOptions.CultureInvariant);
		if (missing.Count != 0)
		{
			string added = string.Join(", ", missing);
			Match own = Regex.Match(source, @"\brender_mode\b([^;]*);", RegexOptions.CultureInvariant);
			if (own.Success)
			{
				string words = own.Groups[1].Value.Trim();
				source = source[..own.Index] + "render_mode " + (words.Length != 0 ? words + ", " : string.Empty) + added + ";" +
					source[(own.Index + own.Length)..];
			}
			else
			{
				int end = SpatialEnd(source);
				source = source[..end] + "\nrender_mode " + added + ";" + source[end..];
			}
		}
		if (defines.Count != 0)
		{
			var lines = new StringBuilder();
			foreach (string name in defines)
			{
				lines.Append("#define ").Append(name).Append('\n');
			}
			// Before the first include, where the body is read; after the statements that open the file.
			Match include = Regex.Match(source, @"(?m)^[ \t]*#[ \t]*include\b", RegexOptions.CultureInvariant);
			if (include.Success)
			{
				source = source[..include.Index] + lines + source[include.Index..];
			}
			else
			{
				Match own = Regex.Match(source, @"\brender_mode\b[^;]*;", RegexOptions.CultureInvariant);
				int end = own.Success ? own.Index + own.Length : SpatialEnd(source);
				source = source[..end] + "\n" + lines + source[end..];
			}
		}
		return source;
	}

	private static int SpatialEnd(string source)
	{
		Match type = SpatialType.Match(source);
		return type.Success
			? type.Index + type.Length
			: throw new FormatException("no shader_type spatial to state the render mode after");
	}

	/// <summary>What a shader compiles, for the reply's census.</summary>
	/// <param name="Discards">Its pixel stage compiles a `discard` or an alpha scissor (no early depth, no LRZ write on it).</param>
	/// <param name="ScissorGate">It declares the standard body's `alpha_scissor_on`, the runtime gate on its scissor.</param>
	internal readonly record struct Facts(bool Discards, bool ScissorGate);

	/// <summary>Reads <see cref="Facts"/> from a spatial shader's resolved source; throws as <see cref="Build"/> does.</summary>
	internal static Facts Inspect(string code, string path, Func<string, string?> readInclude, int renderer = RendererMobile)
	{
		List<Item> items = Split(Preprocess(code, path, readInclude, renderer));
		var macros = new Dictionary<string, string>(StringComparer.Ordinal);
		var functions = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
		bool gate = false;
		foreach (Item item in items)
		{
			if (item.Kind == ItemKind.Directive && MacroOf(item.Text) is { } macro)
			{
				macros[macro.Name] = macro.Body;
			}
			else if (item.Kind == ItemKind.Function)
			{
				if (!functions.TryGetValue(item.Name, out List<Item>? overloads))
				{
					functions[item.Name] = overloads = new List<Item>();
				}
				overloads.Add(item);
			}
			else if (item.Kind == ItemKind.Declaration &&
				BoolUniform.Match(item.Text.Trim()) is { Success: true } flag && flag.Groups[1].Value == "alpha_scissor_on")
			{
				gate = true;
			}
		}
		// An alpha scissor is a discard too: Godot compiles ALPHA_SCISSOR_THRESHOLD into one.
		string fragment = Reach(new[] { "fragment" }, functions, macros, includeMacros: true);
		return new Facts(Discard.IsMatch(fragment) || ScissorWrite.IsMatch(fragment), gate);
	}

	/// <summary>
	/// The fallback, from the shader's own top-level code alone: its blend, depth and cull words and
	/// a constant colour, no vertex stage, no discard. Drawn where <see cref="Build"/> could not be.
	/// </summary>
	internal static string Plain(string code, (float R, float G, float B) albedo)
	{
		string stripped = StripComments(code);
		var modes = new List<string>();
		foreach (Match statement in Regex.Matches(stripped, @"\brender_mode\b([^;]*);", RegexOptions.CultureInvariant))
		{
			foreach (string word in statement.Groups[1].Value.Split(','))
			{
				if (word.Trim() is { Length: > 0 } trimmed)
				{
					modes.Add(trimmed);
				}
			}
		}
		bool writesAlpha = AlphaWrite.IsMatch(stripped) && !ScissorWrite.IsMatch(stripped);
		return "// shaderCeiling: plain fallback\nshader_type spatial;\n" + RenderMode(modes, keptVertex: false) + "\n" +
			Fragment(false, false, false, writesAlpha, new List<string>(), new HashSet<string>(), new HashSet<string>(), albedo);
	}

	private static string RenderMode(List<string> modes, bool keptVertex)
	{
		var kept = new List<string> { "unshaded" };
		foreach (string mode in modes)
		{
			bool keep = Array.Exists(KeptModePrefixes, prefix => mode.StartsWith(prefix, StringComparison.Ordinal)) ||
				(keptVertex && Array.IndexOf(VertexModes, mode) >= 0);
			if (keep && !kept.Contains(mode))
			{
				kept.Add(mode);
			}
		}
		return "render_mode " + string.Join(", ", kept) + ";";
	}

	/// <summary>
	/// The constant pixel stage. With <paramref name="cut"/>: the albedo texture's alpha against the
	/// original's own threshold, as a discard or a scissor as the original does it, so early depth
	/// and LRZ behave as they did. A shader that discards with no albedo to sample keeps a discard
	/// that is never taken: the compiled discard, not the coverage, is what turns early Z off.
	/// </summary>
	private static string Fragment(
		bool cut, bool discards, bool scissor, bool writesAlpha, List<string> samplers, HashSet<string> bools,
		HashSet<string> floats, (float R, float G, float B) albedo, string? keep = null)
	{
		var body = new StringBuilder("void fragment() {\n");
		if (cut)
		{
			string? sampler = AlbedoSampler(samplers);
			if (sampler is null)
			{
				body.Append("\tif (FRAGCOORD.x < 0.0) {\n\t\tdiscard;\n\t}\n");
			}
			else
			{
				string alpha = "fable_ceiling_texel.a";
				string threshold = "0.5";
				if (floats.Contains("alpha_scissor_threshold"))
				{
					threshold = "alpha_scissor_threshold";
				}
				else if (floats.Contains("alpha_threshold"))
				{
					threshold = "alpha_threshold";
					if (floats.Contains("alpha_scale"))
					{
						alpha = "(fable_ceiling_texel.a * alpha_scale)";
					}
				}
				string gate = bools.Contains("alpha_scissor_on") ? "alpha_scissor_on && " : string.Empty;
				body.Append("\tvec4 fable_ceiling_texel = texture(").Append(sampler).Append(", UV);\n");
				if (discards || !scissor)
				{
					body.Append("\tif (").Append(gate).Append(alpha).Append(" < ").Append(threshold)
						.Append(") {\n\t\tdiscard;\n\t}\n");
				}
				else
				{
					body.Append("\tALPHA = ").Append(alpha).Append(";\n\tALPHA_SCISSOR_THRESHOLD = ")
						.Append(threshold).Append(";\n");
				}
			}
		}
		body.Append("\tALBEDO = vec3(").Append(Number(albedo.R)).Append(", ").Append(Number(albedo.G))
			.Append(", ").Append(Number(albedo.B)).Append(");\n");
		if (keep is not null)
		{
			// Never taken (FRAGCOORD is not negative) and not foldable: every input stays live.
			body.Append("\tALBEDO += (FRAGCOORD.x < 0.0 ? 1.0 : 0.0) * vec3(").Append(keep).Append(");\n");
		}
		if (writesAlpha)
		{
			// The original writes ALPHA: it draws in the transparent list, and so does this.
			body.Append("\tALPHA = 1.0;\n");
		}
		return body.Append("}\n").ToString();
	}

	/// <summary>A varying's type and name, for `flatvs`: `varying smooth vec3 v` (a qualifier may be a macro, `FABLE_VARY_MP`).</summary>
	private static readonly Regex TypedVarying = new(
		@"^varying\s+(?:(?:flat|smooth|highp|mediump|lowp|[A-Z][A-Z0-9_]*)\s+)*(\w+)\s+([A-Za-z_]\w*)\s*;", RegexOptions.CultureInvariant);

	/// <summary>The vertex built-ins a pixel stage can read, each read in full.</summary>
	private static readonly (string Name, string Read)[] PixelInputs =
	{
		("NORMAL", "NORMAL.x + NORMAL.y + NORMAL.z"),
		("UV", "UV.x + UV.y"),
		("UV2", "UV2.x + UV2.y"),
		("COLOR", "COLOR.r + COLOR.g + COLOR.b + COLOR.a"),
	};

	/// <summary>
	/// `flatvs`: one float summing every component of each varying the vertex stage writes and of
	/// each vertex built-in the original pixel stages read (the tangent frame when they read it or
	/// a normal map), so no output of the vertex stage is dead. "0.0" when there is none.
	/// </summary>
	private static string KeptReads(
		List<Item> vertex, HashSet<string> varyings, Dictionary<string, string> types, string pixelReach)
	{
		var terms = new List<string>();
		string vertexText = string.Concat(vertex.ConvertAll(v => v.Text));
		foreach (string varying in varyings)
		{
			bool written = Regex.IsMatch(vertexText,
				@"(?<![\w.])" + Regex.Escape(varying) + @"\s*(?:\.\s*\w+)?\s*[-+*/]?=(?!=)", RegexOptions.CultureInvariant);
			if (written && types.TryGetValue(varying, out string? type) && Components(varying, type) is { } read)
			{
				terms.Add(read);
			}
		}
		foreach ((string name, string read) in PixelInputs)
		{
			if (Regex.IsMatch(pixelReach, @"\b" + name + @"\b", RegexOptions.CultureInvariant))
			{
				terms.Add(read);
			}
		}
		if (Regex.IsMatch(pixelReach, @"\b(?:TANGENT|BINORMAL|NORMAL_MAP)\b", RegexOptions.CultureInvariant))
		{
			terms.Add("TANGENT.x + TANGENT.y + TANGENT.z + BINORMAL.x + BINORMAL.y + BINORMAL.z");
		}
		return terms.Count == 0 ? "0.0" : string.Join(" + ", terms);
	}

	/// <summary>Every component of a varying as one float sum; null for a type it does not read (a matrix, an array).</summary>
	private static string? Components(string name, string type)
	{
		int count = type switch
		{
			"float" or "int" or "uint" => 1,
			"vec2" or "ivec2" or "uvec2" => 2,
			"vec3" or "ivec3" or "uvec3" => 3,
			"vec4" or "ivec4" or "uvec4" => 4,
			_ => 0,
		};
		if (count == 0)
		{
			return null;
		}
		if (count == 1)
		{
			return $"float({name})";
		}
		var parts = new List<string>();
		for (int i = 0; i < count; i++)
		{
			parts.Add($"float({name}.{"xyzw"[i]})");
		}
		return string.Join(" + ", parts);
	}

	private static string? AlbedoSampler(List<string> samplers)
	{
		foreach (string key in new[] { "albedo", "diffuse", "base", "colour", "color" })
		{
			string? found = samplers.Find(s => s.Contains(key, StringComparison.OrdinalIgnoreCase));
			if (found is not null)
			{
				return found;
			}
		}
		return samplers.Count != 0 ? samplers[0] : null;
	}

	private static string Number(float value) =>
		Math.Clamp(value, 0f, 1f).ToString("0.0###", CultureInfo.InvariantCulture);

	/// <summary>
	/// A function's text with every statement that writes a varying removed: `v = ...;`, `v.xy += ...;`.
	/// What computed only those values is then dead and the compiler drops it.
	/// </summary>
	private static string StripVaryingWrites(string function, HashSet<string> varyings)
	{
		if (varyings.Count == 0)
		{
			return function;
		}
		int open = function.IndexOf('{');
		if (open < 0)
		{
			return function;
		}
		string body = function[open..];
		foreach (string varying in varyings)
		{
			var write = new Regex(
				@"(?<![\w.])" + Regex.Escape(varying) + @"\s*(?:\[[^\]]*\])?\s*(?:\.\s*\w+)?\s*[-+*/]?=(?!=)[^;{}]*;",
				RegexOptions.CultureInvariant);
			// A varying the stage also reads (an impact's uv, then its position) feeds the vertex: it stays.
			int writes = write.Matches(body).Count;
			int uses = Regex.Matches(body, @"(?<![\w.])" + Regex.Escape(varying) + @"\b", RegexOptions.CultureInvariant).Count;
			if (writes != 0 && uses == writes)
			{
				body = write.Replace(body, string.Empty);
			}
		}
		return function[..open] + body;
	}

	/// <summary>The text of every function and macro reachable from <paramref name="entries"/>, joined.</summary>
	private static string Reach(
		IEnumerable<string> entries, Dictionary<string, List<Item>> functions, Dictionary<string, string> macros,
		bool includeMacros)
	{
		var text = new StringBuilder();
		foreach (string name in Reached(entries, functions, macros))
		{
			if (functions.TryGetValue(name, out List<Item>? overloads))
			{
				foreach (Item overload in overloads)
				{
					text.Append(overload.Text).Append('\n');
				}
			}
			else if (includeMacros && macros.TryGetValue(name, out string? body))
			{
				text.Append(body).Append('\n');
			}
		}
		return text.ToString();
	}

	private static HashSet<string> Reached(
		IEnumerable<string> entries, Dictionary<string, List<Item>> functions, Dictionary<string, string> macros)
	{
		var reached = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<string>();
		foreach (string entry in entries)
		{
			if (reached.Add(entry))
			{
				pending.Push(entry);
			}
		}
		while (pending.Count != 0)
		{
			string name = pending.Pop();
			IEnumerable<string> texts = functions.TryGetValue(name, out List<Item>? overloads)
				? overloads.ConvertAll(o => o.Text[Math.Max(0, o.Text.IndexOf('{'))..])
				: macros.TryGetValue(name, out string? body) ? new[] { body } : Array.Empty<string>();
			foreach (string text in texts)
			{
				foreach (Match reference in Identifier.Matches(text))
				{
					if ((functions.ContainsKey(reference.Value) || macros.ContainsKey(reference.Value)) &&
						reached.Add(reference.Value))
					{
						pending.Push(reference.Value);
					}
				}
			}
		}
		return reached;
	}

	private static (string Name, string Body)? MacroOf(string directive)
	{
		Match match = Regex.Match(directive, @"^\s*#\s*define\s+([A-Za-z_]\w*)(\([^)]*\))?(.*)$",
			RegexOptions.CultureInvariant | RegexOptions.Singleline);
		return match.Success ? (match.Groups[1].Value, match.Groups[3].Value) : null;
	}

	private enum ItemKind
	{
		Directive,
		Declaration,
		Function,
	}

	private sealed class Item
	{
		public ItemKind Kind;
		public string Name = string.Empty;
		public string Text = string.Empty;
	}

	/// <summary>The preprocessed source as its top-level pieces: directive lines, `;` statements, functions.</summary>
	private static List<Item> Split(string source)
	{
		var items = new List<Item>();
		int i = 0;
		int n = source.Length;
		while (i < n)
		{
			while (i < n && char.IsWhiteSpace(source[i]))
			{
				++i;
			}
			if (i >= n)
			{
				break;
			}

			int start = i;
			if (source[i] == '#')
			{
				int end = source.IndexOf('\n', i);
				end = end < 0 ? n : end;
				items.Add(new Item { Kind = ItemKind.Directive, Text = source[start..end] });
				i = end;
				continue;
			}

			int paren = 0;
			int j = i;
			bool done = false;
			while (j < n && !done)
			{
				char c = source[j];
				if (c == '(')
				{
					++paren;
				}
				else if (c == ')')
				{
					--paren;
				}
				else if (c == ';' && paren == 0)
				{
					items.Add(new Item { Kind = ItemKind.Declaration, Text = source[start..(j + 1)] });
					i = j + 1;
					done = true;
					continue;
				}
				else if (c == '{' && paren == 0)
				{
					int close = MatchBrace(source, j);
					Match header = FunctionHeader.Match(source[start..j].Trim());
					if (header.Success)
					{
						items.Add(new Item
						{
							Kind = ItemKind.Function, Name = header.Groups[1].Value, Text = source[start..(close + 1)],
						});
						i = close + 1;
						done = true;
						continue;
					}
					// A struct: its statement ends at the `;` after the braces.
					j = close;
				}
				++j;
			}
			if (!done)
			{
				items.Add(new Item { Kind = ItemKind.Declaration, Text = source[start..] });
				i = n;
			}
		}
		return items;
	}

	private static int MatchBrace(string source, int open)
	{
		int depth = 0;
		for (int k = open; k < source.Length; k++)
		{
			if (source[k] == '{')
			{
				++depth;
			}
			else if (source[k] == '}' && --depth == 0)
			{
				return k;
			}
		}
		throw new FormatException("unbalanced braces");
	}

	/// <summary>
	/// The code with its includes inlined (each once, as Godot does), the conditionals decided, and
	/// comments removed. `#define` and `#undef` lines stay, for Godot to expand.
	/// </summary>
	internal static string Preprocess(string code, string path, Func<string, string?> readInclude, int renderer = RendererMobile)
	{
		var state = new PreprocessState(readInclude);
		state.Defines["CURRENT_RENDERER"] = renderer.ToString(CultureInfo.InvariantCulture);
		state.Defines["RENDERER_COMPATIBILITY"] = "0";
		state.Defines["RENDERER_MOBILE"] = "1";
		state.Defines["RENDERER_FORWARD_PLUS"] = "2";
		Process(state, code, path, 0);
		return state.Output.ToString();
	}

	private sealed class PreprocessState(Func<string, string?> readInclude)
	{
		public readonly Func<string, string?> ReadInclude = readInclude;
		public readonly Dictionary<string, string> Defines = new(StringComparer.Ordinal);
		public readonly HashSet<string> Included = new(StringComparer.Ordinal);
		public readonly StringBuilder Output = new();
	}

	private sealed class Branch(bool outer, bool taken)
	{
		public readonly bool Outer = outer;
		public bool Taken = taken;
	}

	private static void Process(PreprocessState state, string code, string file, int depth)
	{
		if (depth > 25)
		{
			throw new FormatException("include depth exceeded");
		}

		var branches = new Stack<Branch>();
		bool active = true;
		foreach (string line in StripComments(code).Split('\n'))
		{
			string trimmed = line.TrimStart();
			if (!trimmed.StartsWith('#'))
			{
				if (active)
				{
					state.Output.Append(line).Append('\n');
				}
				continue;
			}

			Match directive = Regex.Match(trimmed, @"^#\s*([a-z]+)\s*(.*)$", RegexOptions.CultureInvariant);
			string word = directive.Success ? directive.Groups[1].Value : string.Empty;
			string rest = directive.Success ? directive.Groups[2].Value.Trim() : string.Empty;
			switch (word)
			{
				case "ifdef":
				case "ifndef":
				case "if":
				{
					bool condition = active && (word == "if"
						? Evaluate(rest, state.Defines)
						: state.Defines.ContainsKey(FirstWord(rest)) == (word == "ifdef"));
					branches.Push(new Branch(active, condition));
					active = condition;
					break;
				}
				case "elif":
				{
					Branch branch = branches.Count != 0 ? branches.Peek() : throw new FormatException("#elif without #if");
					bool condition = branch.Outer && !branch.Taken && Evaluate(rest, state.Defines);
					branch.Taken |= condition;
					active = condition;
					break;
				}
				case "else":
				{
					Branch branch = branches.Count != 0 ? branches.Peek() : throw new FormatException("#else without #if");
					active = branch.Outer && !branch.Taken;
					branch.Taken = true;
					break;
				}
				case "endif":
					active = branches.Count != 0 ? branches.Pop().Outer : throw new FormatException("#endif without #if");
					break;
				case "include" when active:
				{
					Match quoted = Regex.Match(rest, "^\"([^\"]+)\"", RegexOptions.CultureInvariant);
					if (!quoted.Success)
					{
						throw new FormatException($"bad include in {file}");
					}
					string included = Resolve(file, quoted.Groups[1].Value);
					if (state.Included.Add(included))
					{
						string text = state.ReadInclude(included) ?? throw new FormatException($"missing include {included}");
						Process(state, text, included, depth + 1);
					}
					break;
				}
				case "define" when active:
					state.Defines[FirstWord(rest)] = Regex.Replace(rest, @"^[A-Za-z_]\w*(\([^)]*\))?", string.Empty).Trim();
					state.Output.Append(trimmed).Append('\n');
					break;
				case "undef" when active:
					state.Defines.Remove(FirstWord(rest));
					state.Output.Append(trimmed).Append('\n');
					break;
				case "error" when active:
					throw new FormatException($"#error in {file}: {rest}");
				default:
					if (active)
					{
						state.Output.Append(trimmed).Append('\n');
					}
					break;
			}
		}
		if (branches.Count != 0)
		{
			throw new FormatException($"unterminated conditional in {file}");
		}
	}

	private static string FirstWord(string text) => Identifier.Match(text) is { Success: true } m ? m.Value : string.Empty;

	/// <summary>An include path against the including file, as Godot resolves it.</summary>
	internal static string Resolve(string file, string include)
	{
		if (include.StartsWith("res://", StringComparison.Ordinal))
		{
			return Simplify(include);
		}
		int slash = file.LastIndexOf('/');
		string directory = slash >= 0 ? file[..slash] : "res:/";
		return Simplify(directory + "/" + include);
	}

	private static string Simplify(string path)
	{
		const string scheme = "res://";
		bool rooted = path.StartsWith(scheme, StringComparison.Ordinal);
		string body = rooted ? path[scheme.Length..] : path;
		var parts = new List<string>();
		foreach (string part in body.Split('/'))
		{
			if (part.Length == 0 || part == ".")
			{
				continue;
			}
			if (part == ".." && parts.Count != 0)
			{
				parts.RemoveAt(parts.Count - 1);
				continue;
			}
			parts.Add(part);
		}
		return (rooted ? scheme : string.Empty) + string.Join("/", parts);
	}

	/// <summary>Comments out of the code, line breaks kept; a `//` inside quotes (an include path) stays.</summary>
	internal static string StripComments(string code)
	{
		var output = new StringBuilder(code.Length);
		bool block = false;
		bool line = false;
		bool quoted = false;
		for (int i = 0; i < code.Length; i++)
		{
			char c = code[i];
			char next = i + 1 < code.Length ? code[i + 1] : '\0';
			if (block)
			{
				if (c == '*' && next == '/')
				{
					block = false;
					++i;
					output.Append(' ');
				}
				else if (c == '\n')
				{
					output.Append('\n');
				}
				continue;
			}
			if (line)
			{
				if (c == '\n')
				{
					line = false;
					output.Append('\n');
				}
				continue;
			}
			if (c == '\r')
			{
				continue;
			}
			if (c == '\n')
			{
				quoted = false;
			}
			else if (c == '"')
			{
				quoted = !quoted;
			}
			else if (!quoted && c == '/' && next == '/')
			{
				line = true;
				++i;
				continue;
			}
			else if (!quoted && c == '/' && next == '*')
			{
				block = true;
				++i;
				continue;
			}
			output.Append(c);
		}
		return output.ToString();
	}

	/// <summary>
	/// An `#if`/`#elif` expression: integers, defined names (their value; an undefined name is 0),
	/// `defined(X)`, `!`, comparisons, `&amp;&amp;`, `||` and parentheses. Anything else throws.
	/// </summary>
	internal static bool Evaluate(string expression, IReadOnlyDictionary<string, string> defines)
	{
		var tokens = new List<string>();
		foreach (Match token in Regex.Matches(expression, @"\s*([A-Za-z_]\w*|\d+|&&|\|\||==|!=|<=|>=|[!<>()])", RegexOptions.CultureInvariant))
		{
			tokens.Add(token.Groups[1].Value);
		}
		if (string.Concat(tokens).Length != Regex.Replace(expression, @"\s+", string.Empty).Length)
		{
			throw new FormatException($"unsupported #if expression: {expression}");
		}
		int at = 0;
		long value = Or(tokens, ref at, defines, 0);
		if (at != tokens.Count)
		{
			throw new FormatException($"unsupported #if expression: {expression}");
		}
		return value != 0;
	}

	private static long Or(List<string> t, ref int at, IReadOnlyDictionary<string, string> d, int depth)
	{
		long left = And(t, ref at, d, depth);
		while (at < t.Count && t[at] == "||")
		{
			++at;
			long right = And(t, ref at, d, depth);
			left = left != 0 || right != 0 ? 1 : 0;
		}
		return left;
	}

	private static long And(List<string> t, ref int at, IReadOnlyDictionary<string, string> d, int depth)
	{
		long left = Compare(t, ref at, d, depth);
		while (at < t.Count && t[at] == "&&")
		{
			++at;
			long right = Compare(t, ref at, d, depth);
			left = left != 0 && right != 0 ? 1 : 0;
		}
		return left;
	}

	private static long Compare(List<string> t, ref int at, IReadOnlyDictionary<string, string> d, int depth)
	{
		long left = Unary(t, ref at, d, depth);
		while (at < t.Count && t[at] is "==" or "!=" or "<" or ">" or "<=" or ">=")
		{
			string op = t[at++];
			long right = Unary(t, ref at, d, depth);
			left = op switch
			{
				"==" => left == right ? 1 : 0,
				"!=" => left != right ? 1 : 0,
				"<" => left < right ? 1 : 0,
				">" => left > right ? 1 : 0,
				"<=" => left <= right ? 1 : 0,
				_ => left >= right ? 1 : 0,
			};
		}
		return left;
	}

	private static long Unary(List<string> t, ref int at, IReadOnlyDictionary<string, string> d, int depth)
	{
		if (at >= t.Count)
		{
			throw new FormatException("truncated #if expression");
		}
		string token = t[at++];
		if (token == "!")
		{
			return Unary(t, ref at, d, depth) == 0 ? 1 : 0;
		}
		if (token == "(")
		{
			long inner = Or(t, ref at, d, depth);
			if (at >= t.Count || t[at++] != ")")
			{
				throw new FormatException("unbalanced #if expression");
			}
			return inner;
		}
		if (token == "defined")
		{
			bool parenthesised = at < t.Count && t[at] == "(";
			if (parenthesised)
			{
				++at;
			}
			string name = at < t.Count ? t[at++] : throw new FormatException("truncated defined()");
			if (parenthesised && (at >= t.Count || t[at++] != ")"))
			{
				throw new FormatException("unbalanced defined()");
			}
			return d.ContainsKey(name) ? 1 : 0;
		}
		if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
		{
			return number;
		}
		if (char.IsLetter(token[0]) || token[0] == '_')
		{
			if (!d.TryGetValue(token, out string? value) || value.Length == 0)
			{
				return 0;
			}
			if (depth > 8)
			{
				throw new FormatException("#if macro recursion");
			}
			int inner = 0;
			var tokens = new List<string>();
			foreach (Match m in Regex.Matches(value, @"\s*([A-Za-z_]\w*|\d+|&&|\|\||==|!=|<=|>=|[!<>()])", RegexOptions.CultureInvariant))
			{
				tokens.Add(m.Groups[1].Value);
			}
			return Or(tokens, ref inner, d, depth + 1);
		}
		throw new FormatException($"unsupported #if token {token}");
	}
}
