using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Refii.Fx;

/// <summary>
/// THE BANK'S OWN MODEL AS JSON: one <see cref="FxEffect"/> with the <see cref="FxEmitter"/>s and
/// <see cref="FxSystemDef"/>s (and their <see cref="FxVisual"/>s) it draws, self-contained — a
/// mod's <c>fx/effects/&lt;name&gt;.json</c> (<see cref="Engine_FxModEffects"/> overlays it on the
/// shipped bank). Nothing here is a second model: a parsed definition is the same objects the
/// renderer draws from the bank, built through the same parameter linking
/// (<see cref="Engine_FxBank.LinkParameters"/>) and timeline baking
/// (<see cref="Xex.XexBankParser.BakeScalar"/>).
/// </summary>
/// <remarks>
/// <para>Shape:</para>
/// <code>
/// { "format": 1, "name": "my_mod_green_torch", "zSortEmitters": false, "updateFrequency": 1,
///   "scale": 1, "tint": [1, 1, 1],
///   "parts": [ { "emitter": 0, "system": 0, "trail": -1, "group": 1, "vector": 0 } ],
///   "emitters": [ { "entityId": "0x1A2B3C4D", "typeId": 0, "subType": 1, "maxActive": 40,
///                   "params": { "rate": [[0, 12]], "speed": [[0, 1.5], [1, 0.2]] },
///                   "vectors": { "vec1": [[0, 1, 1, 1]] }, ... } ],
///   "systems": [ { "id": "0x5E6F7081", "mode": 1, "visuals": [ { "texture": "...", ... } ] } ] }
/// </code>
/// <para>A file holds one effect object, or <c>{ "effects": [ ... ] }</c>. Every public field of the
/// model is a key, named in camelCase (<c>SpawnRadius</c> → <c>spawnRadius</c>); a key left out
/// takes the model's default, and the writer leaves out every field at its default. Hashes
/// (<c>entityId</c>, <c>haloTextureHash</c>, a system's <c>id</c>) are <c>"0x…"</c> text; a number is
/// read too. A generic parameter is <c>params.&lt;name&gt;</c> (<see cref="ParamNames"/>, or
/// <c>p&lt;index&gt;</c>) as <c>[[seconds, value], …]</c>; a vector parameter is
/// <c>vectors.vec0|vec1|vec2|lightColour</c> as <c>[[seconds, x, y, z], …]</c>, in the order the
/// bank's loader reads them (the first key starts the timeline). <c>parts</c> index this file's
/// own <c>emitters</c> and <c>systems</c>; <c>system</c>/<c>trail</c> -1 is none.</para>
/// <para>Derived fields are not keys: <c>HasMotion</c>, <c>IsLight</c>, <c>IsAttractor</c> and
/// <c>IsSpline</c> follow <c>typeId</c>, and the named parameter views (<c>Rate</c>, <c>LightRange</c>
/// …) ARE the generic parameters.</para>
/// </remarks>
public static class Engine_FxEffectJson
{
	public const int FormatVersion = 1;

	/// <summary>One effect and what it draws, its parts indexing its own lists.</summary>
	public sealed class Definition
	{
		public string Name = string.Empty;
		public bool ZSortEmitters;
		public uint UpdateFrequency;
		public float Scale = 1f;
		public float[] Tint = { 1f, 1f, 1f };
		public List<FxEffectPart> Parts { get; } = new();
		public List<FxEmitter> Emitters { get; } = new();
		public List<FxSystemDef> Systems { get; } = new();

		/// <summary>The authored pieces the effect draws beside its parts (<see cref="FxEffect.Pieces"/>).</summary>
		public List<Engine_FxRecipePieces.Piece> Pieces { get; } = new();
	}

	/// <summary>A problem in a file: <paramref name="Pointer"/> is the place (<c>emitters[2].params.rate</c>), empty for the file.</summary>
	public readonly record struct Issue(string Pointer, bool Error, string Message);

	/// <summary>
	/// The generic parameters' names by index, as the model names them; <c>p&lt;index&gt;</c> where it
	/// has none. 2..14 are an emitter's, 15..18 and 24 a light's, 26..27 an attractor's.
	/// </summary>
	public static readonly string[] ParamNames =
	{
		"p0", "p1", "rate", "rateSpread", "speed", "speedSpread", "accelDirScale", "gravityScale",
		"globalWindScale", "velX", "velY", "velZ", "wind", "p13", "p14", "lightRange",
		"lightIntensity", "lightCutoff", "lightFadeRamp", "p19", "p20", "p21", "p22", "p23",
		"lightConeAngle", "p25", "attractRadius", "attractStrength",
	};

	/// <summary>The vector parameters by index: 3 is a light's colour.</summary>
	public static readonly string[] VectorNames = { "vec0", "vec1", "vec2", "lightColour" };

	/// <summary>The entity types the bank's loader reads (0 standard emitter … 9 spline emitter).</summary>
	public static readonly uint[] EntityTypes = { 0, 3, 4, 5, 6, 7, 8, 9 };

	/// <summary>Model fields derived from <c>typeId</c> or held as timelines, never keys of their own.</summary>
	private static readonly HashSet<string> DerivedEmitterFields = new(StringComparer.Ordinal)
	{
		nameof(FxEmitter.HasMotion), nameof(FxEmitter.IsLight), nameof(FxEmitter.IsAttractor), nameof(FxEmitter.IsSpline),
	};

	/// <summary>The uint fields that are hashes, written as "0x…".</summary>
	private static readonly HashSet<string> HashFields = new(StringComparer.Ordinal)
	{
		nameof(FxEmitter.EntityId), nameof(FxEmitter.HaloTextureHash), nameof(FxSystemDef.Id),
	};

	private static readonly string[] EffectKeys =
	{
		"format", "name", "zSortEmitters", "updateFrequency", "scale", "tint", "parts", "emitters", "systems", "pieces",
	};

	/// <summary>A piece's keys, as a recipe's <c>pieces</c> state them (<see cref="Engine_FxRecipePieces.Piece"/>).</summary>
	public static readonly string[] PieceKeys =
	{
		"kind", "texture", "color", "colorEnd", "height", "radius", "radiusTop", "rate", "speed", "size", "sizeEnd",
		"count", "period", "width", "mesh", "tilt", "intensity", "blend",
	};

	/// <summary>The piece kinds <see cref="Engine_FxRecipePieces.Build"/> draws.</summary>
	public static readonly string[] PieceKinds = { "embers", "arcs", "ring", "blades" };

	private static readonly string[] PartKeys = { "emitter", "system", "trail", "group", "vector" };

	// ---------------------------------------------------------------------------------------
	// From the bank
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// <paramref name="effect"/> with every emitter and system it draws, as a definition of its own:
	/// a deep copy (the bank's objects are never handed out for editing), entity and system ids
	/// kept — saved as is, it REPLACES those emitters wherever the bank uses them.
	/// </summary>
	public static Definition Extract(FxBank bank, FxEffect effect, string name = "")
	{
		var d = new Definition
		{
			Name = name.Length != 0 ? name : effect.Name.Length != 0 ? effect.Name : $"FX_{effect.NameHash:X8}",
			ZSortEmitters = effect.ZSortEmitters,
			UpdateFrequency = effect.UpdateFrequency,
			Scale = effect.Scale,
			Tint = (float[])effect.Tint.Clone(),
		};
		d.Pieces.AddRange(effect.Pieces);
		var emitterAt = new Dictionary<int, int>();
		var systemAt = new Dictionary<int, int>();
		int Local<T>(Dictionary<int, int> map, List<T> source, List<T> into, int index)
		{
			if (index < 0 || index >= source.Count)
			{
				return -1;
			}

			if (!map.TryGetValue(index, out int local))
			{
				local = into.Count;
				map[index] = local;
				into.Add(source[index]);
			}

			return local;
		}

		foreach (FxEffectPart part in effect.Parts)
		{
			int emitter = Local(emitterAt, bank.Emitters, d.Emitters, part.Emitter);
			if (emitter < 0)
			{
				continue;
			}

			d.Parts.Add(new FxEffectPart(
				emitter, Local(systemAt, bank.Systems, d.Systems, part.System), part.Group, part.Vector,
				Local(systemAt, bank.Systems, d.Systems, part.TrailSystem)));
		}

		return Copy(d);
	}

	/// <summary>
	/// <paramref name="source"/> as a NEW effect named <paramref name="name"/>: a deep copy with every
	/// entity and system id cleared, so each takes an id derived from the new name and nothing the
	/// bank already holds is replaced.
	/// </summary>
	public static Definition AsNew(Definition source, string name)
	{
		Definition d = Copy(source);
		d.Name = name;
		foreach (FxEmitter e in d.Emitters)
		{
			e.EntityId = 0;
		}

		foreach (FxSystemDef s in d.Systems)
		{
			s.Id = 0;
		}

		return d;
	}

	/// <summary>A deep copy, through the file form.</summary>
	public static Definition Copy(Definition d)
	{
		var issues = new List<Issue>();
		List<Definition> parsed = Parse(Serialize(d), issues);
		if (parsed.Count != 1)
		{
			throw new InvalidOperationException("an effect definition did not survive its own file form: " +
				string.Join("; ", issues.Select(i => $"{i.Pointer}: {i.Message}")));
		}

		return parsed[0];
	}

	/// <summary>The hash the bank keys <paramref name="name"/> by: an <c>FX_XXXXXXXX</c> token's own, else FNV-1 lower-cased.</summary>
	public static uint HashOf(string name) =>
		FxBank.TryParseHashToken(name, out uint token) ? token : FxBank.Fnv1Lower(name);

	/// <summary>The id an emitter or system without one takes: stable for its effect and slot, never 0.</summary>
	public static uint DerivedId(string effectName, string kind, int index)
	{
		uint id = FxBank.Fnv1Lower($"{effectName}#{kind}{index}");
		return id == 0 ? 1u : id;
	}

	// ---------------------------------------------------------------------------------------
	// Writing
	// ---------------------------------------------------------------------------------------

	public static string Serialize(Definition d)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			WriteEffect(w, d);
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n") + "\n";
	}

	/// <summary>Several effects as one <c>{ "effects": [...] }</c> file.</summary>
	public static string SerializeMany(IEnumerable<Definition> effects)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			w.WriteStartObject();
			w.WriteNumber("format", FormatVersion);
			w.WriteStartArray("effects");
			foreach (Definition d in effects)
			{
				WriteEffect(w, d);
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n") + "\n";
	}

	private static void WriteEffect(Utf8JsonWriter w, Definition d)
	{
		w.WriteStartObject();
		w.WriteNumber("format", FormatVersion);
		w.WriteString("name", d.Name);
		if (d.ZSortEmitters)
		{
			w.WriteBoolean("zSortEmitters", true);
		}

		w.WriteNumber("updateFrequency", d.UpdateFrequency);
		if (d.Scale != 1f)
		{
			w.WritePropertyName("scale");
			WriteFloat(w, d.Scale);
		}

		if (d.Tint.Length >= 3 && (d.Tint[0] != 1f || d.Tint[1] != 1f || d.Tint[2] != 1f))
		{
			w.WritePropertyName("tint");
			WriteFloats(w, d.Tint);
		}

		w.WriteStartArray("parts");
		foreach (FxEffectPart part in d.Parts)
		{
			w.WriteStartObject();
			w.WriteNumber("emitter", part.Emitter);
			if (part.System >= 0)
			{
				w.WriteNumber("system", part.System);
			}

			if (part.TrailSystem >= 0)
			{
				w.WriteNumber("trail", part.TrailSystem);
			}

			w.WriteNumber("group", part.Group);
			w.WriteNumber("vector", part.Vector);
			w.WriteEndObject();
		}

		w.WriteEndArray();

		w.WriteStartArray("emitters");
		var emitterDefaults = new FxEmitter();
		foreach (FxEmitter e in d.Emitters)
		{
			w.WriteStartObject();
			WriteFields(w, e, emitterDefaults, DerivedEmitterFields);
			w.WriteStartObject("params");
			for (int i = 0; i < e.Generic.Length && i < ParamNames.Length; i++)
			{
				if (e.Generic[i].Keys.Count == 0)
				{
					continue;
				}

				w.WriteStartArray(ParamNames[i]);
				foreach ((float time, float value) in e.Generic[i].Keys)
				{
					w.WriteStartArray();
					WriteFloat(w, time);
					WriteFloat(w, value);
					w.WriteEndArray();
				}

				w.WriteEndArray();
			}

			w.WriteEndObject();
			w.WriteStartObject("vectors");
			FxVectorTimeline[] vectors = { e.Vec0, e.Vec1, e.Vec2, e.LightColour };
			for (int i = 0; i < vectors.Length; i++)
			{
				if (vectors[i].Keys.Count == 0)
				{
					continue;
				}

				w.WriteStartArray(VectorNames[i]);
				foreach ((float time, float x, float y, float z) in vectors[i].Keys)
				{
					w.WriteStartArray();
					WriteFloat(w, time);
					WriteFloat(w, x);
					WriteFloat(w, y);
					WriteFloat(w, z);
					w.WriteEndArray();
				}

				w.WriteEndArray();
			}

			w.WriteEndObject();
			w.WriteEndObject();
		}

		w.WriteEndArray();

		w.WriteStartArray("systems");
		var systemDefaults = new FxSystemDef();
		var visualDefaults = new FxVisual();
		foreach (FxSystemDef s in d.Systems)
		{
			w.WriteStartObject();
			WriteFields(w, s, systemDefaults, null);
			w.WriteStartArray("visuals");
			foreach (FxVisual v in s.Visuals)
			{
				w.WriteStartObject();
				WriteFields(w, v, visualDefaults, null);
				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		w.WriteEndArray();
		if (d.Pieces.Count != 0)
		{
			w.WriteStartArray("pieces");
			foreach (Engine_FxRecipePieces.Piece piece in d.Pieces)
			{
				WritePiece(w, piece);
			}

			w.WriteEndArray();
		}

		w.WriteEndObject();
	}

	/// <summary>One piece in its recipe form (the keys <see cref="PieceKeys"/> names).</summary>
	private static void WritePiece(Utf8JsonWriter w, Engine_FxRecipePieces.Piece p)
	{
		w.WriteStartObject();
		w.WriteString("kind", p.Kind);
		if (p.Texture.Length != 0)
		{
			w.WriteString("texture", p.Texture);
		}

		w.WritePropertyName("color");
		WriteFloats(w, new[] { p.Colour.R, p.Colour.G, p.Colour.B, p.Colour.A });
		w.WritePropertyName("colorEnd");
		WriteFloats(w, new[] { p.ColourEnd.R, p.ColourEnd.G, p.ColourEnd.B, p.ColourEnd.A });
		void Number(string key, float value)
		{
			w.WritePropertyName(key);
			WriteFloat(w, value);
		}

		Number("height", p.Height);
		Number("radius", p.Radius);
		Number("radiusTop", p.RadiusTop);
		Number("rate", p.Rate);
		Number("speed", p.Speed);
		Number("size", p.Size);
		Number("sizeEnd", p.SizeEnd);
		w.WriteNumber("count", p.Count);
		Number("period", p.Period);
		Number("width", p.Width);
		if (p.Mesh.Length != 0)
		{
			w.WriteString("mesh", p.Mesh);
		}

		Number("tilt", p.Tilt);
		Number("intensity", p.Intensity);
		if (p.Blend != "add")
		{
			w.WriteString("blend", p.Blend);
		}

		w.WriteEndObject();
	}


	/// <summary>The model's public fields of plain types in declaration order, the ones at their default left out.</summary>
	private static void WriteFields(Utf8JsonWriter w, object value, object defaults, HashSet<string>? skip)
	{
		foreach (FieldInfo field in PlainFields(value.GetType()))
		{
			if (skip is not null && skip.Contains(field.Name))
			{
				continue;
			}

			object? now = field.GetValue(value);
			if (SameValue(now, field.GetValue(defaults)))
			{
				continue;
			}

			w.WritePropertyName(KeyOf(field.Name));
			switch (now)
			{
				case float f: WriteFloat(w, f); break;
				case int i: w.WriteNumberValue(i); break;
				case uint u when HashFields.Contains(field.Name): w.WriteStringValue($"0x{u:X8}"); break;
				case uint u: w.WriteNumberValue(u); break;
				case bool b: w.WriteBooleanValue(b); break;
				case string s: w.WriteStringValue(s); break;
				case float[] a: WriteFloats(w, a); break;
				case float[][] rows:
					w.WriteStartArray();
					foreach (float[] row in rows)
					{
						WriteFloats(w, row);
					}

					w.WriteEndArray();
					break;
				default: w.WriteNullValue(); break;
			}
		}
	}

	private static void WriteFloats(Utf8JsonWriter w, float[] values)
	{
		w.WriteStartArray();
		foreach (float f in values)
		{
			WriteFloat(w, f);
		}

		w.WriteEndArray();
	}

	/// <summary>The shortest text that reads back as the same float; a non-finite value as text ("NaN", "Infinity").</summary>
	private static void WriteFloat(Utf8JsonWriter w, float f)
	{
		if (float.IsFinite(f))
		{
			w.WriteRawValue(f.ToString("R", CultureInfo.InvariantCulture));
		}
		else
		{
			w.WriteStringValue(f.ToString(CultureInfo.InvariantCulture));
		}
	}

	private static bool SameValue(object? a, object? b) => (a, b) switch
	{
		(float[] x, float[] y) => x.Length == y.Length && x.Zip(y).All(p => BitConverter.SingleToInt32Bits(p.First) == BitConverter.SingleToInt32Bits(p.Second)),
		(float[][] x, float[][] y) => x.Length == y.Length && x.Zip(y).All(p => SameValue(p.First, p.Second)),
		(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
		_ => Equals(a, b),
	};

	private static readonly Dictionary<Type, FieldInfo[]> PlainFieldCache = new();

	/// <summary>The public instance fields of the types the file carries directly, in declaration order.</summary>
	public static FieldInfo[] PlainFields(Type type)
	{
		lock (PlainFieldCache)
		{
			if (!PlainFieldCache.TryGetValue(type, out FieldInfo[]? fields))
			{
				fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
					.Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(uint) ||
						f.FieldType == typeof(bool) || f.FieldType == typeof(string) || f.FieldType == typeof(float[]) ||
						f.FieldType == typeof(float[][]))
					.OrderBy(DeclarationOrder)
					.ToArray();
				PlainFieldCache[type] = fields;
			}

			return fields;
		}
	}

	/// <summary>
	/// The field's metadata token, which follows declaration order. NativeAOT (the linux-bionic
	/// Android export) keeps no tokens and throws; there every field ranks equal and the stable
	/// sort keeps reflection's own order, which is also declaration order for these flat types.
	/// </summary>
	private static int DeclarationOrder(FieldInfo field)
	{
		try
		{
			return field.MetadataToken;
		}
		catch (InvalidOperationException)
		{
			return 0;
		}
	}

	public static string KeyOf(string fieldName) =>
		fieldName.Length == 0 ? fieldName : char.ToLowerInvariant(fieldName[0]) + fieldName[1..];

	// ---------------------------------------------------------------------------------------
	// Reading
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// Every effect in one file's text, with every problem in <paramref name="issues"/>. An effect
	/// with an error is left out; unknown keys are warnings (the reader ignores them).
	/// </summary>
	public static List<Definition> Parse(string text, List<Issue> issues)
	{
		var result = new List<Definition>();
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});
		}
		catch (JsonException exception)
		{
			issues.Add(new Issue(string.Empty, true, $"not valid JSON at line {(exception.LineNumber ?? 0) + 1}: {exception.Message}"));
			return result;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				issues.Add(new Issue(string.Empty, true, "must be an effect object, or { \"effects\": [ ... ] }; the file is ignored"));
				return result;
			}

			if (root.TryGetProperty("effects", out JsonElement list))
			{
				if (list.ValueKind != JsonValueKind.Array)
				{
					issues.Add(new Issue("effects", true, "must be a list of effect objects; the file is ignored"));
					return result;
				}

				foreach (JsonProperty property in root.EnumerateObject())
				{
					if (property.Name is not ("effects" or "format"))
					{
						issues.Add(new Issue(property.Name, false, $"'{property.Name}' is not a field of an effects file and is ignored"));
					}
				}

				int i = 0;
				foreach (JsonElement item in list.EnumerateArray())
				{
					if (ReadEffect(item, $"effects[{i++}]", issues) is { } d)
					{
						result.Add(d);
					}
				}

				return result;
			}

			if (ReadEffect(root, string.Empty, issues) is { } single)
			{
				result.Add(single);
			}
		}

		return result;
	}

	private static string At(string at, string key) => at.Length == 0 ? key : $"{at}.{key}";

	private static string At(string at, int index) => $"{at}[{index}]";

	private static Definition? ReadEffect(JsonElement e, string at, List<Issue> issues)
	{
		int errorsBefore = issues.Count(i => i.Error);
		if (e.ValueKind != JsonValueKind.Object)
		{
			issues.Add(new Issue(at, true, "must be an effect object; skipped"));
			return null;
		}

		Unknown(e, at, EffectKeys, issues);
		var d = new Definition();
		if (e.TryGetProperty("format", out JsonElement format) &&
			(format.ValueKind != JsonValueKind.Number || format.GetDouble() > FormatVersion))
		{
			issues.Add(new Issue(At(at, "format"), false, $"format {format.GetRawText()} is newer than this reader's {FormatVersion}; read as {FormatVersion}"));
		}

		if (!e.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
			(name.GetString() ?? string.Empty).Trim().Length == 0)
		{
			issues.Add(new Issue(At(at, "name"), true, "\"name\" is required: the effect's name (a new one adds an effect, a shipped one replaces it)"));
		}
		else
		{
			d.Name = name.GetString()!.Trim();
			if (d.Name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
			{
				issues.Add(new Issue(At(at, "name"), true, $"'{d.Name}' is not an effect name: letters, digits, '_', '-' and '.' only"));
			}
		}

		d.ZSortEmitters = Bool(e, "zSortEmitters", at, false, issues);
		d.UpdateFrequency = (uint)Int(e, "updateFrequency", at, 0, 0, int.MaxValue, issues);
		d.Scale = Float(e, "scale", at, 1f, issues);
		if (d.Scale <= 0f)
		{
			issues.Add(new Issue(At(at, "scale"), true, "must be above 0"));
		}

		if (e.TryGetProperty("tint", out JsonElement tint))
		{
			d.Tint = Floats(tint, At(at, "tint"), 3, issues) ?? d.Tint;
		}

		if (Array(e, "emitters", at, issues) is { } emitters)
		{
			int i = 0;
			foreach (JsonElement item in emitters.EnumerateArray())
			{
				d.Emitters.Add(ReadEmitter(item, At(At(at, "emitters"), i++), issues));
			}
		}

		if (Array(e, "systems", at, issues) is { } systems)
		{
			int i = 0;
			foreach (JsonElement item in systems.EnumerateArray())
			{
				d.Systems.Add(ReadSystem(item, At(At(at, "systems"), i++), issues));
			}
		}

		if (Array(e, "parts", at, issues) is { } parts)
		{
			int i = 0;
			foreach (JsonElement item in parts.EnumerateArray())
			{
				string where = At(At(at, "parts"), i++);
				if (item.ValueKind != JsonValueKind.Object)
				{
					issues.Add(new Issue(where, true, "must be an object { \"emitter\", \"system\", \"trail\", \"group\", \"vector\" }"));
					continue;
				}

				Unknown(item, where, PartKeys, issues);
				int emitter = Int(item, "emitter", where, -1, -1, int.MaxValue, issues);
				int system = Int(item, "system", where, -1, -1, int.MaxValue, issues);
				int trail = Int(item, "trail", where, -1, -1, int.MaxValue, issues);
				if (emitter < 0 || emitter >= d.Emitters.Count)
				{
					issues.Add(new Issue(At(where, "emitter"), true, $"must index this effect's emitters (0..{d.Emitters.Count - 1})"));
				}

				if (system >= d.Systems.Count)
				{
					issues.Add(new Issue(At(where, "system"), true, $"must index this effect's systems (0..{d.Systems.Count - 1}) or be -1"));
				}

				if (trail >= d.Systems.Count)
				{
					issues.Add(new Issue(At(where, "trail"), true, $"must index this effect's systems (0..{d.Systems.Count - 1}) or be -1"));
				}

				d.Parts.Add(new FxEffectPart(
					emitter, system, (uint)Int(item, "group", where, 0, 0, int.MaxValue, issues),
					Int(item, "vector", where, 0, 0, int.MaxValue, issues), trail));
			}
		}

		if (Array(e, "pieces", at, issues) is { } pieces)
		{
			int i = 0;
			foreach (JsonElement item in pieces.EnumerateArray())
			{
				if (ReadPiece(item, At(At(at, "pieces"), i++), issues) is { } piece)
				{
					d.Pieces.Add(piece);
				}
			}
		}

		if (d.Parts.Count == 0 && d.Pieces.Count == 0 && issues.Count(i => i.Error) == errorsBefore)
		{
			issues.Add(new Issue(At(at, "parts"), false, "no parts: the effect draws nothing"));
		}

		return issues.Count(i => i.Error) == errorsBefore ? d : null;
	}

	/// <summary>
	/// One piece, with the defaults a recipe's piece takes (Engine_FxRecipes.ParsePiece): colour
	/// white, its end the colour with alpha 0, height 2, radius 1, rate 10, speed 1, size 0.5 (its end
	/// 0.4 of it), count 64, period 1, width 0.1, tilt 20, intensity 1, blend "add".
	/// </summary>
	private static Engine_FxRecipePieces.Piece? ReadPiece(JsonElement item, string at, List<Issue> issues)
	{
		if (item.ValueKind != JsonValueKind.Object)
		{
			issues.Add(new Issue(at, true, "must be a piece object { \"kind\", ... }"));
			return null;
		}

		Unknown(item, at, PieceKeys, issues);
		string Text(string key) =>
			item.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
		string kind = Text("kind");
		if (!PieceKinds.Contains(kind))
		{
			issues.Add(new Issue(At(at, "kind"), true, $"'{kind}' is no piece kind ({string.Join(", ", PieceKinds)})"));
			return null;
		}

		Godot.Color Colour(string key, Godot.Color fallback)
		{
			if (!item.TryGetProperty(key, out JsonElement v))
			{
				return fallback;
			}

			float[]? c = Floats(v, At(at, key), 0, issues);
			return c is { Length: >= 3 } ? new Godot.Color(c[0], c[1], c[2], c.Length >= 4 ? c[3] : 1f) : fallback;
		}

		float size = Float(item, "size", at, 0.5f, issues);
		float radius = Float(item, "radius", at, 1f, issues);
		Godot.Color colour = Colour("color", Godot.Colors.White);
		string blend = Text("blend");
		return new Engine_FxRecipePieces.Piece(
			kind, Text("texture"), colour, Colour("colorEnd", colour with { A = 0f }),
			Float(item, "height", at, 2f, issues), radius, Float(item, "radiusTop", at, radius, issues),
			Float(item, "rate", at, 10f, issues), Float(item, "speed", at, 1f, issues), size,
			Float(item, "sizeEnd", at, size * 0.4f, issues), Int(item, "count", at, 64, 0, 100000, issues),
			Float(item, "period", at, 1f, issues), Float(item, "width", at, 0.1f, issues), Text("mesh"),
			Float(item, "tilt", at, 20f, issues), Float(item, "intensity", at, 1f, issues), blend.Length != 0 ? blend : "add");
	}

	private static FxEmitter ReadEmitter(JsonElement item, string at, List<Issue> issues)
	{
		var e = new FxEmitter();
		if (item.ValueKind != JsonValueKind.Object)
		{
			issues.Add(new Issue(at, true, "must be an emitter object"));
			return e;
		}

		ReadFields(item, at, e, DerivedEmitterFields, new[] { "params", "vectors" }, issues);
		if (!EntityTypes.Contains(e.TypeId))
		{
			issues.Add(new Issue(At(at, "typeId"), true,
				$"{e.TypeId} is no entity type the bank reads (0 standard emitter, 3 orbital, 4 attractor, 5 spline, 6 root, 7 point light, 8 spot light, 9 spline emitter)"));
		}

		var keys = new List<(float, float)>[28];
		for (int i = 0; i < keys.Length; i++)
		{
			keys[i] = new List<(float, float)>();
		}

		if (item.TryGetProperty("params", out JsonElement ps))
		{
			string where = At(at, "params");
			if (ps.ValueKind != JsonValueKind.Object)
			{
				issues.Add(new Issue(where, true, "must be an object of parameter timelines { \"rate\": [[seconds, value], ...] }"));
			}
			else
			{
				foreach (JsonProperty p in ps.EnumerateObject())
				{
					int index = ParamIndex(p.Name);
					if (index < 0)
					{
						issues.Add(new Issue(At(where, p.Name), false,
							$"'{p.Name}' is no parameter (the names are {string.Join(", ", ParamNames)}, or p0..p27) and is ignored"));
						continue;
					}

					foreach (float[] key in Keys(p.Value, At(where, p.Name), 2, issues))
					{
						keys[index].Add((key[0], key[1]));
					}
				}
			}
		}

		for (int i = 0; i < 28; i++)
		{
			e.Generic[i] = Engine_FxBank.MakeTimeline(i, keys[i]);
		}

		var vectors = new List<(float, float, float, float)>[4];
		for (int i = 0; i < vectors.Length; i++)
		{
			vectors[i] = new List<(float, float, float, float)>();
		}

		if (item.TryGetProperty("vectors", out JsonElement vs))
		{
			string where = At(at, "vectors");
			if (vs.ValueKind != JsonValueKind.Object)
			{
				issues.Add(new Issue(where, true, "must be an object of vector timelines { \"vec1\": [[seconds, x, y, z], ...] }"));
			}
			else
			{
				foreach (JsonProperty p in vs.EnumerateObject())
				{
					int index = System.Array.IndexOf(VectorNames, p.Name);
					if (index < 0)
					{
						issues.Add(new Issue(At(where, p.Name), false, $"'{p.Name}' is no vector parameter (vec0, vec1, vec2, lightColour) and is ignored"));
						continue;
					}

					foreach (float[] key in Keys(p.Value, At(where, p.Name), 4, issues))
					{
						vectors[index].Add((key[0], key[1], key[2], key[3]));
					}
				}
			}
		}

		e.Vec0 = Engine_FxBank.MakeVecTimeline(0, vectors[0]);
		e.Vec1 = Engine_FxBank.MakeVecTimeline(1, vectors[1]);
		e.Vec2 = Engine_FxBank.MakeVecTimeline(2, vectors[2]);
		e.LightColour = Engine_FxBank.MakeVecTimeline(3, vectors[3]);
		Engine_FxBank.LinkParameters(e);
		return e;
	}

	private static FxSystemDef ReadSystem(JsonElement item, string at, List<Issue> issues)
	{
		var s = new FxSystemDef();
		if (item.ValueKind != JsonValueKind.Object)
		{
			issues.Add(new Issue(at, true, "must be a system object"));
			return s;
		}

		ReadFields(item, at, s, null, new[] { "visuals" }, issues);
		if (s.Mode is < 1 or > 4)
		{
			issues.Add(new Issue(At(at, "mode"), false, $"mode {s.Mode} draws nothing (1 sprites, 2 and 3 ribbons, 4 meshes)"));
		}

		if (Array(item, "visuals", at, issues) is { } visuals)
		{
			int i = 0;
			foreach (JsonElement v in visuals.EnumerateArray())
			{
				string where = At(At(at, "visuals"), i++);
				var visual = new FxVisual();
				if (v.ValueKind != JsonValueKind.Object)
				{
					issues.Add(new Issue(where, true, "must be a visual object"));
					continue;
				}

				ReadFields(v, where, visual, null, System.Array.Empty<string>(), issues);
				s.Visuals.Add(visual);
			}
		}

		return s;
	}

	private static int ParamIndex(string name)
	{
		int index = System.Array.IndexOf(ParamNames, name);
		if (index >= 0)
		{
			return index;
		}

		return name.Length > 1 && name[0] == 'p' &&
			int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and < 28
				? n
				: -1;
	}

	/// <summary>The model's plain fields from the object's keys; an unknown key is a warning, a wrong type an error.</summary>
	private static void ReadFields(JsonElement item, string at, object target, HashSet<string>? skip, string[] extraKeys, List<Issue> issues)
	{
		var byKey = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
		foreach (FieldInfo field in PlainFields(target.GetType()))
		{
			if (skip is null || !skip.Contains(field.Name))
			{
				byKey[KeyOf(field.Name)] = field;
			}
		}

		foreach (JsonProperty property in item.EnumerateObject())
		{
			if (extraKeys.Contains(property.Name))
			{
				continue;
			}

			string where = At(at, property.Name);
			if (!byKey.TryGetValue(property.Name, out FieldInfo? field))
			{
				issues.Add(new Issue(where, false, $"'{property.Name}' is not a field here and is ignored"));
				continue;
			}

			JsonElement value = property.Value;
			object? current = field.GetValue(target);
			object? read = field.FieldType switch
			{
				Type t when t == typeof(float) => ReadFloat(value, where, issues),
				Type t when t == typeof(int) => ReadWhole(value, where, int.MinValue, int.MaxValue, issues) is { } n ? (object)(int)n : null,
				Type t when t == typeof(uint) => ReadWhole(value, where, 0, uint.MaxValue, issues) is { } n ? (object)(uint)n : null,
				Type t when t == typeof(bool) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
					? (object)value.GetBoolean()
					: Wrong(where, "true or false", value, issues),
				Type t when t == typeof(string) => value.ValueKind == JsonValueKind.String
					? (object)(value.GetString() ?? string.Empty)
					: Wrong(where, "text", value, issues),
				Type t when t == typeof(float[]) => Floats(value, where, current is float[] old ? old.Length : -1, issues),
				Type t when t == typeof(float[][]) => Rows(value, where, issues),
				_ => null,
			};
			if (read is not null)
			{
				field.SetValue(target, read);
			}
		}
	}

	private static object? Wrong(string at, string want, JsonElement value, List<Issue> issues)
	{
		issues.Add(new Issue(at, true, $"must be {want} (it is {Describe(value)})"));
		return null;
	}

	private static string Describe(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Object => "an object",
		JsonValueKind.Array => "a list",
		JsonValueKind.String => $"the text '{value.GetString()}'",
		JsonValueKind.Number => $"the number {value.GetRawText()}",
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		_ => "null",
	};

	private static object? ReadFloat(JsonElement value, string at, List<Issue> issues)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float f))
		{
			return f;
		}

		if (value.ValueKind == JsonValueKind.String &&
			float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float text))
		{
			return text;
		}

		return Wrong(at, "a number", value, issues);
	}

	/// <summary>A whole number in range: a JSON number, or a "0x…"/decimal text (how hashes are written).</summary>
	private static long? ReadWhole(JsonElement value, string at, long min, long max, List<Issue> issues)
	{
		long n;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && Math.Floor(d) == d)
		{
			n = (long)d;
		}
		else if (value.ValueKind == JsonValueKind.String && ParseWholeText(value.GetString() ?? string.Empty, out long parsed))
		{
			n = parsed;
		}
		else
		{
			Wrong(at, "a whole number (or \"0x…\" text)", value, issues);
			return null;
		}

		if (n < min || n > max)
		{
			issues.Add(new Issue(at, true, $"{n} is out of range ({min}..{max})"));
			return null;
		}

		return n;
	}

	private static bool ParseWholeText(string text, out long value)
	{
		text = text.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			bool ok = ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex) && hex <= uint.MaxValue;
			value = ok ? (long)hex : 0;
			return ok;
		}

		return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	/// <summary>A list of numbers; <paramref name="length"/> above 0 is the exact count wanted.</summary>
	private static float[]? Floats(JsonElement value, string at, int length, List<Issue> issues)
	{
		if (value.ValueKind != JsonValueKind.Array)
		{
			Wrong(at, length > 0 ? $"a list of {length} numbers" : "a list of numbers", value, issues);
			return null;
		}

		var list = new List<float>();
		int i = 0;
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (ReadFloat(item, At(at, i++), issues) is float f)
			{
				list.Add(f);
			}
			else
			{
				return null;
			}
		}

		if (length > 0 && list.Count != length)
		{
			issues.Add(new Issue(at, true, $"must hold exactly {length} numbers (it holds {list.Count})"));
			return null;
		}

		return list.ToArray();
	}

	private static float[][]? Rows(JsonElement value, string at, List<Issue> issues)
	{
		if (value.ValueKind != JsonValueKind.Array)
		{
			Wrong(at, "a list of [x, y, z] points", value, issues);
			return null;
		}

		var rows = new List<float[]>();
		int i = 0;
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (Floats(item, At(at, i++), 3, issues) is not { } row)
			{
				return null;
			}

			rows.Add(row);
		}

		return rows.ToArray();
	}

	/// <summary>A timeline's keys: a list of [seconds, value…] of <paramref name="width"/> numbers each.</summary>
	private static List<float[]> Keys(JsonElement value, string at, int width, List<Issue> issues)
	{
		var keys = new List<float[]>();
		if (value.ValueKind != JsonValueKind.Array)
		{
			Wrong(at, width == 2 ? "a list of [seconds, value] keys" : "a list of [seconds, x, y, z] keys", value, issues);
			return keys;
		}

		int i = 0;
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (Floats(item, At(at, i++), width, issues) is { } key)
			{
				keys.Add(key);
			}
		}

		if (keys.Count != 0 && (int)(keys[0][0] * Xex.XexConst.Fps) < 0)
		{
			issues.Add(new Issue(at, false,
				"the first key is before 0 s, so the timeline never starts and the parameter keeps its default (the bank's loader starts a timeline at its FIRST key)"));
		}

		return keys;
	}

	private static JsonElement? Array(JsonElement record, string key, string at, List<Issue> issues)
	{
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			return null;
		}

		if (value.ValueKind != JsonValueKind.Array)
		{
			Wrong(At(at, key), "a list", value, issues);
			return null;
		}

		return value;
	}

	private static bool Bool(JsonElement record, string key, string at, bool fallback, List<Issue> issues)
	{
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			return fallback;
		}

		if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			return value.GetBoolean();
		}

		Wrong(At(at, key), "true or false", value, issues);
		return fallback;
	}

	private static int Int(JsonElement record, string key, string at, int fallback, long min, long max, List<Issue> issues)
	{
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			return fallback;
		}

		return ReadWhole(value, At(at, key), min, max, issues) is { } n ? (int)n : fallback;
	}

	private static float Float(JsonElement record, string key, string at, float fallback, List<Issue> issues)
	{
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			return fallback;
		}

		return ReadFloat(value, At(at, key), issues) is float f ? f : fallback;
	}

	private static void Unknown(JsonElement record, string at, string[] known, List<Issue> issues)
	{
		foreach (JsonProperty property in record.EnumerateObject())
		{
			if (!known.Contains(property.Name))
			{
				issues.Add(new Issue(At(at, property.Name), false,
					$"'{property.Name}' is not a field here and is ignored (the fields are {string.Join(", ", known)})"));
			}
		}
	}
}
