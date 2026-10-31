using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// `ShaderMaterial.Duplicate()` without the render-thread syncs, where the result is provably
/// the one Godot's own would make.
/// </summary>
/// <remarks>
/// WHAT A DUPLICATE COSTS. `Resource::_duplicate` walks the property list, and a ShaderMaterial's
/// (`ShaderMaterial::_get_property_list`) asks the server for its shader's uniforms
/// (`get_shader_parameter_list`, a `FUNC2SC`) and, for each uniform the material holds no
/// compatible value for, its default (`shader_get_parameter_default`, a `FUNC2RC`), which it then
/// writes into the material's own parameter cache. Under Safe those are reads; with the separate
/// render thread each waits until that thread has drained its queue — the rest of a draw, or a
/// compile task it picked up while yielding. A load makes thousands of copies (bake-mode builds,
/// light-group variants, per-chunk ground, the UI's own materials), from the main thread inside
/// frame budgets and from the region build's worker.
///
/// WHEN THE COPY IS MADE HERE. When every uniform of the shader already holds a value of a
/// compatible type in the source (the same test as Godot's: equal types; an array the uniform's
/// type converts to; an object of the uniform's class), Godot's duplicate would write nothing back
/// into the source, and the copy it makes is the base properties, every uniform's value and the
/// metadata — which is what this sets, from the source's own cache (`get_shader_parameter` reads
/// no server). The uniform list is the server's, asked once per shader and kept here until the
/// shader changes. A uniform whose value is exactly the server's default (nil for a sampler never
/// bound) passes too: Godot's replacement would write the same value back (see <see cref="Values"/>).
/// Otherwise — a uniform never set whose default is not nil, an incompatible value, a script — it
/// is Godot's own duplicate, which back-fills the source; the next copy of that source is made
/// here. Each fallback is counted by reason, caller and uniform (<see cref="Report"/>).
/// Values are shared exactly as a shallow duplicate shares them (arrays and objects by reference).
/// </remarks>
public static class Engine_MaterialCopy
{
	private sealed class Uniform
	{
		public required StringName Name;
		public required Variant.Type Type;
		public required string HintString;
		public required bool Stored;

		/// <summary>The server's default (`shader_get_parameter_default`), once asked; under <see cref="Gate"/>.</summary>
		public Variant Default;
		public bool DefaultKnown;
	}

	private sealed class Uniforms
	{
		public required Uniform[] List;
	}

	private static readonly ConditionalWeakTable<Shader, Uniforms> ByShader = new();

	/// <summary>Shaders whose `changed` this class listens to, so a new code drops the list.</summary>
	private static readonly ConditionalWeakTable<Shader, object> Watched = new();

	private static readonly object Gate = new();

	/// <summary>Copies made here, and Godot duplicates taken instead (for the metrics reply).</summary>
	public static long Made, FellBack;

	/// <summary>Why a copy was left to Godot's own duplicate: the first uniform (or property) that decided it.</summary>
	public enum Reason
	{
		/// <summary>The material carries a script; its properties are not ours to list.</summary>
		Script,
		/// <summary>A uniform holds nothing and its default is not nil: Godot writes the default back into the source.</summary>
		Unset,
		/// <summary>A uniform holds a value of another type than the uniform's, which is not its default: Godot replaces it with the default.</summary>
		TypeMismatch,
		/// <summary>A sampler holds an object not of the uniform's class (a cubemap's hint names two), which is not its default.</summary>
		ObjectClass,
		/// <summary>A sampler holds a null object.</summary>
		NullObject,
		/// <summary>A PackedFloat32Array for a vec4 array: Godot converts it in the source.</summary>
		PackedVector4,
	}

	private static readonly int ReasonCount = System.Enum.GetValues<Reason>().Length;
	private static readonly long[] Reasons = new long[ReasonCount];

	/// <summary>Copies made here only because a failing uniform's value is exactly its default (nil included).</summary>
	public static long MadeAtDefault;

	/// <summary>Per caller (file.member): copies made here, fallbacks, and the fallbacks by reason.</summary>
	private static readonly System.Collections.Generic.Dictionary<string, long[]> BySite = new();

	/// <summary>Fallbacks by "reason:uniform", bounded; the rest under "other".</summary>
	private static readonly System.Collections.Generic.Dictionary<string, long> ByUniform = new();
	private const int UniformKeys = 48;

	/// <summary>A shallow copy of <paramref name="source"/>, as `Duplicate()` makes it. Any thread.</summary>
	public static ShaderMaterial Shallow(ShaderMaterial source,
		[CallerFilePath] string callerFile = "", [CallerMemberName] string callerMember = "")
	{
		Shader? shader = source.Shader;
		Uniform[] list = shader is null ? System.Array.Empty<Uniform>() : ListOf(shader);
		Reason reason = Reason.Script;
		string failing = string.Empty;
		bool atDefault = false;
		bool[]? borrowed = null;
		Variant[]? values = source.GetScript().VariantType != Variant.Type.Nil
			? null
			: Values(source, shader, list, out reason, out failing, out atDefault, out borrowed);
		string site = System.IO.Path.GetFileNameWithoutExtension(callerFile) + "." + callerMember;
		Note(site, values is null ? reason : null, failing, atDefault);
		if (values is null)
		{
			System.Threading.Interlocked.Increment(ref FellBack);
			long from = Refii.Core.Engine_ServerSyncs.Begin();
			var duplicate = (ShaderMaterial)source.Duplicate();
			Refii.Core.Engine_ServerSyncs.End(Refii.Core.Engine_ServerSyncs.Site.MaterialDuplicate, from);
			return duplicate;
		}

		System.Threading.Interlocked.Increment(ref Made);

		// In the order `_duplicate` sets them: Resource, Material, ShaderMaterial, the shader's
		// parameters, the metadata. Material hides next_pass and render_priority unless the shader
		// is spatial (`Material::_validate_property`), and a hidden property is not stored.
		var copy = new ShaderMaterial
		{
			ResourceLocalToScene = source.ResourceLocalToScene,
			ResourceName = source.ResourceName,
		};
		if (shader is not null && shader.GetMode() == Shader.Mode.Spatial)
		{
			copy.RenderPriority = source.RenderPriority;
			copy.NextPass = source.NextPass;
		}
		copy.Shader = shader;
		for (int i = 0; i < list.Length; i++)
		{
			// Nil is Godot's `set(name, nil)` on a copy with no server material yet: it erases
			// what is not there.
			if (list[i].Stored && values[i].VariantType != Variant.Type.Nil)
			{
				copy.SetShaderParameter(list[i].Name, values[i]);
			}
			// A borrowed value is the shader's cached default, shared by every copy.
			if (!borrowed![i])
			{
				values[i].Dispose();
			}
		}
		foreach (StringName meta in source.GetMetaList())
		{
			using Variant held = source.GetMeta(meta);
			copy.SetMeta(meta, held);
		}

		return copy;
	}

	/// <summary>
	/// Each uniform's value in the source, or null (with the first reason) when Godot's duplicate
	/// would change the source.
	/// </summary>
	/// <remarks>
	/// AT ITS DEFAULT IS NOT A CHANGE. Godot replaces a cached value it finds incompatible (nil
	/// among them: "not cached") with the server's default, in the source's cache only. When the
	/// value is exactly that default — nil for a sampler never bound (`get_default_datatype_value`
	/// has no case for samplers), or a default whose type is not the uniform's, which Godot
	/// re-finds incompatible at every duplicate — the replacement writes the same value back and
	/// the copy receives it: so every duplicate of such a source took the fallback, for good.
	/// Here the copy takes the value and the source is left as Godot leaves it. The default is
	/// asked once per shader and uniform (counted, ShaderParamDefault).
	/// </remarks>
	private static Variant[]? Values(ShaderMaterial source, Shader? shader, Uniform[] list,
		out Reason reason, out string failing, out bool atDefault, out bool[] borrowed)
	{
		reason = Reason.Unset;
		failing = string.Empty;
		atDefault = false;
		var values = new Variant[list.Length];
		borrowed = new bool[list.Length];
		for (int i = 0; i < list.Length; i++)
		{
			Variant value = source.GetShaderParameter(list[i].Name);
			Reason? why = Incompatible(list[i], value);
			if (why is { } found && found != Reason.PackedVector4 && shader is not null)
			{
				Variant fallback = DefaultOf(shader, list[i]);
				if (SameValue(value, fallback))
				{
					why = null;
					atDefault = true;
				}
				else if (found is Reason.Unset or Reason.TypeMismatch)
				{
					// NOT CACHED, OR NOT OF THE UNIFORM'S TYPE: Godot's duplicate swaps the default
					// into the source's cache and copies it, so its copy holds the default. Here the
					// copy holds the same default and the source is left alone; the source draws with
					// that default either way, so the picture is the one Godot's copies give.
					value.Dispose();
					value = fallback;
					borrowed[i] = true;
					why = null;
					atDefault = true;
				}
			}

			if (why is { } decided)
			{
				reason = decided;
				failing = list[i].Name.ToString();
				value.Dispose();
				for (int j = 0; j < i; j++)
				{
					if (!borrowed[j])
					{
						values[j].Dispose();
					}
				}
				return null;
			}
			values[i] = value;
		}

		return values;
	}

	/// <summary>
	/// `ShaderMaterial::_get_property_list`'s test of a cached value against its uniform, as the
	/// reason it fails or null: arrays by `Variant::can_convert` (equal, or Array and a packed array
	/// either way round), an object by `is_class` of the uniform's hint, anything else by equal
	/// type. Nil is "not cached".
	/// </summary>
	private static Reason? Incompatible(Uniform uniform, Variant value)
	{
		Variant.Type cached = value.VariantType;
		if (cached == Variant.Type.Nil)
		{
			return Reason.Unset;
		}

		if (!TypesCompatible(uniform.Type, cached))
		{
			return uniform.Type == Variant.Type.PackedVector4Array && cached == Variant.Type.PackedFloat32Array
				? Reason.PackedVector4
				: Reason.TypeMismatch;
		}

		if (cached == Variant.Type.Object)
		{
			// A null object would be an engine crash there; here it is Godot's own duplicate.
			GodotObject? held = value.AsGodotObject();
			if (held is null)
			{
				return Reason.NullObject;
			}
			return held.IsClass(uniform.HintString) ? null : Reason.ObjectClass;
		}

		return null;
	}

	/// <summary>
	/// Whether a cached value is the default exactly: the same type and value. Objects, Arrays and
	/// Dictionaries are references Godot would replace, so they never are.
	/// </summary>
	private static bool SameValue(Variant value, Variant fallback)
	{
		Variant.Type type = value.VariantType;
		if (type != fallback.VariantType)
		{
			return false;
		}

		switch (type)
		{
			case Variant.Type.Nil:
				return true;
			case Variant.Type.Object:
			case Variant.Type.Array:
			case Variant.Type.Dictionary:
			case Variant.Type.Callable:
			case Variant.Type.Signal:
			case Variant.Type.Rid:
				return false;
		}

		object? a = value.Obj, b = fallback.Obj;
		return type > Variant.Type.Array
			? System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(a, b)
			: Equals(a, b);
	}

	/// <summary>The uniform's server default, asked once per shader and uniform.</summary>
	private static Variant DefaultOf(Shader shader, Uniform uniform)
	{
		lock (Gate)
		{
			if (uniform.DefaultKnown)
			{
				return uniform.Default;
			}
		}

		long from = Refii.Core.Engine_ServerSyncs.Begin();
		Variant fetched = RenderingServer.ShaderGetParameterDefault(shader.GetRid(), uniform.Name);
		Refii.Core.Engine_ServerSyncs.End(Refii.Core.Engine_ServerSyncs.Site.ShaderParamDefault, from);
		lock (Gate)
		{
			if (!uniform.DefaultKnown)
			{
				uniform.Default = fetched;
				uniform.DefaultKnown = true;
			}
			return uniform.Default;
		}
	}

	private static void Note(string site, Reason? fellBack, string uniform, bool atDefault)
	{
		lock (Gate)
		{
			if (!BySite.TryGetValue(site, out long[]? row))
			{
				row = new long[2 + ReasonCount];
				BySite[site] = row;
			}

			if (fellBack is not { } reason)
			{
				++row[0];
				if (atDefault)
				{
					++MadeAtDefault;
				}
				return;
			}

			++row[1];
			++row[2 + (int)reason];
			++Reasons[(int)reason];
			string key = reason + ":" + uniform;
			if (ByUniform.ContainsKey(key) || ByUniform.Count < UniformKeys)
			{
				ByUniform[key] = System.Collections.Generic.CollectionExtensions.GetValueOrDefault(ByUniform, key) + 1;
			}
			else
			{
				ByUniform["other"] = System.Collections.Generic.CollectionExtensions.GetValueOrDefault(ByUniform, "other") + 1;
			}
		}
	}

	/// <summary>The copies' census for the metrics reply: totals, fallbacks by reason, by caller, by uniform.</summary>
	public static System.Collections.Generic.Dictionary<string, object?> Report()
	{
		lock (Gate)
		{
			var reasons = new System.Collections.Generic.Dictionary<string, object?>();
			for (int i = 0; i < ReasonCount; i++)
			{
				if (Reasons[i] != 0)
				{
					reasons[((Reason)i).ToString()] = Reasons[i];
				}
			}

			var sites = new System.Collections.Generic.Dictionary<string, object?>();
			foreach ((string site, long[] row) in BySite)
			{
				var why = new System.Collections.Generic.Dictionary<string, object?>();
				for (int i = 0; i < ReasonCount; i++)
				{
					if (row[2 + i] != 0)
					{
						why[((Reason)i).ToString()] = row[2 + i];
					}
				}
				sites[site] = new System.Collections.Generic.Dictionary<string, object?>
				{
					["made"] = row[0],
					["fellBack"] = row[1],
					["reasons"] = why,
				};
			}

			var uniforms = new System.Collections.Generic.Dictionary<string, object?>();
			foreach ((string key, long count) in ByUniform)
			{
				uniforms[key] = count;
			}

			return new System.Collections.Generic.Dictionary<string, object?>
			{
				["made"] = System.Threading.Interlocked.Read(ref Made),
				["madeAtDefault"] = MadeAtDefault,
				["fellBack"] = System.Threading.Interlocked.Read(ref FellBack),
				["reasons"] = reasons,
				["sites"] = sites,
				["uniforms"] = uniforms,
			};
		}
	}

	/// <summary>
	/// The type half of the test, pure: nil is never compatible (not cached); an array (Array or
	/// a packed array, `Variant::is_array`) passes `Variant::can_convert(uniform, cached)` — equal,
	/// or Array and a packed array either way round; anything else must be equal.
	/// </summary>
	internal static bool TypesCompatible(Variant.Type uniform, Variant.Type cached)
	{
		if (cached == Variant.Type.Nil)
		{
			return false;
		}

		if (cached >= Variant.Type.Array)
		{
			return uniform == cached ||
				(cached == Variant.Type.Array && uniform > Variant.Type.Array && uniform < Variant.Type.Max) ||
				(cached > Variant.Type.Array && uniform == Variant.Type.Array);
		}

		return uniform == cached;
	}

	/// <summary>The shader's uniforms as `get_shader_uniform_list(groups)` lists them, groups left out.</summary>
	private static Uniform[] ListOf(Shader shader)
	{
		lock (Gate)
		{
			if (ByShader.TryGetValue(shader, out Uniforms? held))
			{
				return held.List;
			}
		}

		long from = Refii.Core.Engine_ServerSyncs.Begin();
		Godot.Collections.Array raw = shader.GetShaderUniformList(true);
		Refii.Core.Engine_ServerSyncs.End(Refii.Core.Engine_ServerSyncs.Site.ShaderUniformList, from);

		var list = new System.Collections.Generic.List<Uniform>(raw.Count);
		foreach (Variant entry in raw)
		{
			Godot.Collections.Dictionary info = entry.AsGodotDictionary();
			long usage = info["usage"].AsInt64();
			if (usage == (long)PropertyUsageFlags.Group)
			{
				continue;
			}

			list.Add(new Uniform
			{
				Name = info["name"].AsStringName(),
				Type = (Variant.Type)info["type"].AsInt64(),
				HintString = info["hint_string"].AsString(),
				Stored = (usage & (long)PropertyUsageFlags.Storage) != 0,
			});
		}

		Uniform[] built = list.ToArray();
		lock (Gate)
		{
			ByShader.AddOrUpdate(shader, new Uniforms { List = built });
			if (!Watched.TryGetValue(shader, out _))
			{
				Watched.Add(shader, Gate);
				// Weakly: the shader holds this callable, and a strong capture would keep it alive.
				var weak = new System.WeakReference<Shader>(shader);
				shader.Connect(Resource.SignalName.Changed, Callable.From(() =>
				{
					if (weak.TryGetTarget(out Shader? changed))
					{
						lock (Gate)
						{
							ByShader.Remove(changed);
						}
					}
				}));
			}
		}

		return built;
	}
}
