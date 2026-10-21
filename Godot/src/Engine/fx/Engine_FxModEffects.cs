using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Mods;

namespace Refii.Fx;

/// <summary>
/// MOD EFFECTS: every enabled mod's <c>fx/effects/*.json</c> (<see cref="Engine_FxEffectJson"/>)
/// overlaid on the shipped particle bank as it loads (<see cref="FableFx.SharedBank"/>), so the
/// game, the editor's bank view, <see cref="FableFx"/> and <c>play_fx</c> all draw from one bank.
/// </summary>
/// <remarks>
/// <para>THE RULES, applied in load order (lowest priority first, so the highest wins):</para>
/// <list type="bullet">
/// <item>An effect whose name (FNV-1 lower-cased, or an <c>FX_XXXXXXXX</c> token) is new ADDS an
/// effect; one the bank holds REPLACES it — every placement and every live guest spawn of that
/// effect draws the mod's parts, because the live feed joins a guest node to its effect by hash.</item>
/// <item>An emitter or system the bank holds, stated exactly as the bank holds it, is a REFERENCE:
/// the effect draws the shipped record (a mod effect may chain a shipped effect's own emitters, as
/// Spell Weaving's blades carry the Blades sword's effects) and neither bank is rewritten for it.</item>
/// <item>An emitter whose <c>entityId</c> the bank holds REPLACES that emitter's parameters in place,
/// in every effect that uses it; the guest's own spawns of shipped effects pick it up through the
/// same join. An emitter without one takes an id derived from its effect and slot and is added.</item>
/// <item>A system whose <c>id</c> the bank holds replaces that system the same way; one without is added.</item>
/// <item>Two mods stating the same effect, emitter or system are a conflict, recorded as a
/// <see cref="RowConflict"/> under <see cref="ConflictTable"/> (the later mod wins), the way text rows are.</item>
/// </list>
/// <para>The guest still states WHICH effect plays and WHERE. The same overlay reaches BOTH
/// simulations: Godot's bank copy here, and the guest's own bank, which the mount serves with the
/// stated records written in (<see cref="ComposeGuestBank"/>, <see cref="Engine_FxBankWriter"/>) — so
/// the groups the guest simulates and draws itself carry the mod's parameters under either look.
/// An effect's authored scale and tint, which the guest's record has no field for, reach its
/// instances through the dress table (<see cref="DressKey"/>); a remount reloads the guest's bank
/// (<see cref="ReloadGuestBank"/>).</para>
/// </remarks>
public static class Engine_FxModEffects
{
	public const string Folder = "fx/effects/";

	/// <summary>The table name a conflict is recorded under (ModIndex.Tally, the Mods panel).</summary>
	public const string ConflictTable = "fx/effects";

	/// <summary>One effect a mod ships, and where it came from.</summary>
	public sealed record Source(string Mod, string Path, Engine_FxEffectJson.Definition Effect);

	/// <summary>What one overlay did.</summary>
	public sealed class Result
	{
		public int EffectsAdded;
		public int EffectsReplaced;
		public int EmittersAdded;
		public int EmittersReplaced;
		public int SystemsAdded;
		public int SystemsReplaced;

		/// <summary>Shipped emitters and systems an effect stated exactly as the bank holds them: referenced, not replaced.</summary>
		public int EmittersReferenced;

		public int SystemsReferenced;
		public List<RowConflict> Conflicts { get; } = new();

		/// <summary>Every emitter (entity id), system (id) and effect (name hash) the overlay wrote, replaced or added: the records the guest bank's writer rewrites.</summary>
		public HashSet<uint> StatedEmitters { get; } = new();

		public HashSet<uint> StatedSystems { get; } = new();

		public HashSet<uint> StatedEffects { get; } = new();

		public override string ToString() =>
			$"{EffectsAdded} effect(s) added, {EffectsReplaced} replaced; {EmittersAdded} emitter(s) added, " +
			$"{EmittersReplaced} replaced; {SystemsAdded} system(s) added, {SystemsReplaced} replaced" +
			(EmittersReferenced + SystemsReferenced != 0 ? $"; {EmittersReferenced} shipped emitter(s) and {SystemsReferenced} system(s) referenced" : string.Empty) +
			(Conflicts.Count != 0 ? $"; {Conflicts.Count} stated by more than one mod" : string.Empty);
	}

	private static readonly object Gate = new();
	private static List<Source> _sources = new();
	private static int _generation;

	/// <summary>Steps every time the mounted set changes; the shared bank re-applies on a change.</summary>
	public static int Generation => System.Threading.Volatile.Read(ref _generation);

	/// <summary>The enabled mods' effects as the last mount read them, lowest priority first.</summary>
	public static IReadOnlyList<Source> Sources
	{
		get
		{
			lock (Gate)
			{
				return _sources.ToArray();
			}
		}
	}

	/// <summary>Replaces the mounted set (the mount, and tests).</summary>
	public static void SetSources(IReadOnlyList<Source> sources)
	{
		lock (Gate)
		{
			_sources = new List<Source>(sources);
		}

		System.Threading.Interlocked.Increment(ref _generation);
	}

	private static readonly Dictionary<string, List<Source>> _contributed = new(StringComparer.Ordinal);

	/// <summary>
	/// Effects another composer states for Godot's bank under <paramref name="owner"/> (replacing
	/// what it stated before): the recipes' pieces (<see cref="Engine_FxRecipes"/>), each an effect
	/// whose only content is its pieces. Godot draws them (<see cref="DrawnSources"/>); the guest's
	/// bank does not carry them (it has no piece kind).
	/// </summary>
	public static void Contribute(string owner, IReadOnlyList<Source> sources)
	{
		lock (Gate)
		{
			if (sources.Count == 0)
			{
				_contributed.Remove(owner);
			}
			else
			{
				_contributed[owner] = new List<Source>(sources);
			}
		}

		System.Threading.Interlocked.Increment(ref _generation);
	}

	/// <summary>What Godot's bank is overlaid with (<see cref="FableFx.SharedBank"/>): the mounted
	/// effects, then every contributed set.</summary>
	public static IReadOnlyList<Source> DrawnSources
	{
		get
		{
			lock (Gate)
			{
				var all = new List<Source>(_sources);
				foreach (List<Source> set in _contributed.Values)
				{
					all.AddRange(set);
				}

				return all;
			}
		}
	}

	/// <summary>
	/// Reads every overlay's <c>fx/effects/*.json</c> (mount order) into <see cref="Sources"/>. Returns
	/// one line per file read and one per refusal, for the mount log; an effect with an error is
	/// left out, the others still load. Safe off the main thread.
	/// </summary>
	public static List<string> Load(IReadOnlyList<ModOverlay> overlays)
	{
		var lines = new List<string>();
		var sources = new List<Source>();
		foreach (ModOverlay overlay in overlays)
		{
			foreach ((string path, string text) in ReadFiles(overlay, lines))
			{
				var issues = new List<Engine_FxEffectJson.Issue>();
				List<Engine_FxEffectJson.Definition> effects = Engine_FxEffectJson.Parse(text, issues);
				foreach (Engine_FxEffectJson.Issue issue in issues.Where(i => i.Error))
				{
					lines.Add($"REFUSED {overlay.Name}/{path}: {(issue.Pointer.Length != 0 ? issue.Pointer + ": " : string.Empty)}{issue.Message}");
				}

				foreach (Engine_FxEffectJson.Definition effect in effects)
				{
					sources.Add(new Source(overlay.Name, path, effect));
					lines.Add($"{overlay.Name}/{path}: '{effect.Name}' ({effect.Emitters.Count} emitter(s), {effect.Systems.Count} system(s))");
				}
			}
		}

		SetSources(sources);
		return lines;
	}

	/// <summary>
	/// Overlays <paramref name="sources"/> on <paramref name="bank"/> by the rules above. The bank's
	/// own objects are never edited: a replaced entry is a new object in the same slot, so a node
	/// already built from the old one keeps drawing what it built.
	/// </summary>
	public static Result Apply(FxBank bank, IReadOnlyList<Source> sources)
	{
		var result = new Result();
		var emitterAt = new Dictionary<uint, int>();
		for (int i = 0; i < bank.Emitters.Count; i++)
		{
			emitterAt.TryAdd(bank.Emitters[i].EntityId, i);
		}

		var systemAt = new Dictionary<uint, int>();
		for (int i = 0; i < bank.Systems.Count; i++)
		{
			systemAt.TryAdd(bank.Systems[i].Id, i);
		}

		var effectAt = new Dictionary<uint, int>();
		for (int i = 0; i < bank.Effects.Count; i++)
		{
			effectAt.TryAdd(bank.Effects[i].NameHash, i);
		}

		var shippedEmitters = new HashSet<uint>(emitterAt.Keys);
		var shippedSystems = new HashSet<uint>(systemAt.Keys);
		var shippedEffects = new HashSet<uint>(effectAt.Keys);
		var stated = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		void State(string key, string mod)
		{
			if (!stated.TryGetValue(key, out List<string>? mods))
			{
				mods = new List<string>();
				stated[key] = mods;
			}

			mods.Remove(mod);
			mods.Add(mod);
		}

		foreach (Source source in sources)
		{
			Engine_FxEffectJson.Definition d = source.Effect;
			uint hash = Engine_FxEffectJson.HashOf(d.Name);
			var localEmitter = new int[d.Emitters.Count];
			for (int i = 0; i < d.Emitters.Count; i++)
			{
				FxEmitter e = d.Emitters[i];
				uint id = e.EntityId != 0 ? e.EntityId : Engine_FxEffectJson.DerivedId(d.Name, "emitter", i);
				FxEmitter placed = CopyEmitter(e, id);
				if (emitterAt.TryGetValue(id, out int at) && shippedEmitters.Contains(id) && FileForm(bank.Emitters[at]) == FileForm(placed))
				{
					// A shipped emitter stated exactly as the bank holds it is a REFERENCE: the effect
					// draws the bank's own record (an effect may chain a shipped effect's emitters), and
					// neither bank is rewritten for it.
					localEmitter[i] = at;
					result.EmittersReferenced++;
					continue;
				}

				if (emitterAt.TryGetValue(id, out at))
				{
					bank.Emitters[at] = placed;
					if (shippedEmitters.Contains(id))
					{
						result.EmittersReplaced++;
					}
				}
				else
				{
					at = bank.Emitters.Count;
					bank.Emitters.Add(placed);
					emitterAt[id] = at;
					result.EmittersAdded++;
				}

				localEmitter[i] = at;
				result.StatedEmitters.Add(id);
				if (e.EntityId != 0)
				{
					State($"emitter 0x{id:X8}", source.Mod);
				}
			}

			var localSystem = new int[d.Systems.Count];
			for (int i = 0; i < d.Systems.Count; i++)
			{
				FxSystemDef s = d.Systems[i];
				uint id = s.Id != 0 ? s.Id : Engine_FxEffectJson.DerivedId(d.Name, "system", i);
				FxSystemDef placed = CopySystem(s, id);
				if (systemAt.TryGetValue(id, out int at) && shippedSystems.Contains(id) && FileForm(bank.Systems[at]) == FileForm(placed))
				{
					localSystem[i] = at;
					result.SystemsReferenced++;
					continue;
				}

				if (systemAt.TryGetValue(id, out at))
				{
					bank.Systems[at] = placed;
					if (shippedSystems.Contains(id))
					{
						result.SystemsReplaced++;
					}
				}
				else
				{
					at = bank.Systems.Count;
					bank.Systems.Add(placed);
					systemAt[id] = at;
					result.SystemsAdded++;
				}

				localSystem[i] = at;
				result.StatedSystems.Add(id);
				if (s.Id != 0)
				{
					State($"system 0x{id:X8}", source.Mod);
				}
			}

			var effect = new FxEffect
			{
				NameHash = hash,
				Name = d.Name,
				ZSortEmitters = d.ZSortEmitters,
				UpdateFrequency = d.UpdateFrequency,
				Scale = d.Scale,
				Tint = (float[])d.Tint.Clone(),
			};
			effect.Pieces.AddRange(d.Pieces);
			foreach (FxEffectPart part in d.Parts)
			{
				int Map(int[] local, int index) => index >= 0 && index < local.Length ? local[index] : -1;
				int emitter = Map(localEmitter, part.Emitter);
				if (emitter < 0)
				{
					continue;
				}

				effect.Parts.Add(new FxEffectPart(emitter, Map(localSystem, part.System), part.Group, part.Vector, Map(localSystem, part.TrailSystem)));
				bank.TypeToEffect.TryAdd(bank.Emitters[emitter].EntityId, hash);
			}

			if (effectAt.TryGetValue(hash, out int slot))
			{
				bank.Effects[slot] = effect;
				if (shippedEffects.Contains(hash))
				{
					result.EffectsReplaced++;
				}
			}
			else
			{
				effectAt[hash] = bank.Effects.Count;
				bank.Effects.Add(effect);
				result.EffectsAdded++;
			}

			result.StatedEffects.Add(hash);
			State($"effect '{d.Name}'", source.Mod);
		}

		foreach ((string key, List<string> mods) in stated)
		{
			if (mods.Count > 1)
			{
				var conflict = new RowConflict { Key = key, Winner = mods[^1] };
				conflict.Losers.AddRange(mods.Take(mods.Count - 1));
				result.Conflicts.Add(conflict);
			}
		}

		bank.Reindex();
		return result;
	}

	/// <summary>
	/// Where the guest opens its bank: <c>sub_822F3898</c> appends <c>"particle_bank.bnk"</c>
	/// (0x822f3970) to the <c>"Art\\Particles\\"</c> root <c>sub_8324B150</c> builds, under the data
	/// root. The mount answers the spelling with and without <c>data/</c> (<c>AliasesFor</c>).
	/// </summary>
	public const string GuestBankPath = "data/art/particles/particle_bank.bnk";

	private static byte[]? _shippedBank;

	/// <summary>
	/// The SHIPPED bank's bytes as the last composition read them, before any mod: what Godot's own
	/// copy is overlaid on (<see cref="FableFx.SharedBank"/>), since the mount now serves the
	/// composed file under the bank's name.
	/// </summary>
	public static byte[]? ShippedBank => System.Threading.Volatile.Read(ref _shippedBank);

	/// <summary>
	/// The guest's bank with the enabled mods' records written in, added to <paramref name="mounted"/>
	/// under <see cref="GuestBankPath"/> so the guest's loader reads it in place of the shipped file —
	/// attributed to the highest-priority mod that states an effect. Nothing is added while no mod
	/// states one: the guest then reads the shipped file as it always has. Returns the lines for the
	/// mount log. <paramref name="shipped"/> reads the untouched bank (the mount's base copy, else the
	/// game directory's file).
	/// </summary>
	public static List<string> ComposeGuestBank(Refii.Bnk.BnkFileSystem mounted, Func<byte[]?> shipped)
	{
		var lines = new List<string>();
		// An effect of pieces alone has nothing the guest's record can hold.
		IReadOnlyList<Source> sources = Sources.Where(s => s.Effect.Parts.Count != 0).ToList();
		if (sources.Count == 0)
		{
			return lines;
		}

		byte[]? bytes = shipped();
		if (bytes is null)
		{
			lines.Add($"REFUSED the guest's particle bank: {GuestBankPath} was not found, so the mods' effects reach Godot's drawing only");
			return lines;
		}

		System.Threading.Volatile.Write(ref _shippedBank, bytes);
		Engine_FxBankWriter.Composed? composed = Engine_FxBankWriter.Compose(bytes, sources, out string error);
		if (composed is null)
		{
			lines.Add($"REFUSED the guest's particle bank: {error}");
			return lines;
		}

		byte[] written = composed.Bytes;
		mounted.AddVirtualFile(GuestBankPath, "<fx:effects>", written.LongLength, () => written, sources[^1].Mod);
		lines.Add(
			$"the guest's particle bank carries {composed.Result.StatedEffects.Count} effect(s), " +
			$"{composed.Result.StatedEmitters.Count} emitter(s) and {composed.Result.StatedSystems.Count} system(s) " +
			$"from the mods ({bytes.Length} -> {written.Length} bytes)");

		string dress = DressText(sources);
		if (dress.Length != 0)
		{
			byte[] table = Encoding.UTF8.GetBytes(dress);
			mounted.AddVirtualFile(DressKey, "<fx:dress>", table.LongLength, () => table, sources[^1].Mod);
			lines.Add($"{dress.Count(c => c == '\n')} dress line(s) carry the mods' scale, tint, spin and mesh alignment onto the guest's own instances ({DressKey})");
		}

		return lines;
	}

	/// <summary>
	/// What the guest's particle hooks read beside its bank (src/vfs/src/hooks/particle_dress.cpp,
	/// particle_motion.cpp) — the model's fields the guest's records have no room for:
	/// <list type="bullet">
	/// <item><c>0xHASH scale r g b</c> — a mod effect whose authored <c>scale</c> or <c>tint</c> is not
	/// identity: put on every instance of the effect the guest builds, the scale through the
	/// instance's own setter, the tint on a copy of each emitter's material;</item>
	/// <item><c>spin 0xENTITY radiansPerSecond</c> — an emitter's <see cref="FxEmitter.Spin"/>, keyed by
	/// its entity id (the guest's type+4), applied after each step of the integrator;</item>
	/// <item><c>align 0xSYSTEM x y z w</c> — a system whose first visual is a mesh with a
	/// <see cref="FxVisual.MeshAlign"/>, keyed by the system's id (system+4), composed onto each mesh
	/// particle's birth orientation.</item>
	/// </list>
	/// </summary>
	public const string DressKey = "refii/fx_effect_dress.txt";

	/// <summary>
	/// The dress table's text for <paramref name="sources"/> (load order: a later source of the same
	/// effect wins, as <see cref="Apply"/> replaces), effects in hash order; empty when none has one.
	/// </summary>
	public static string DressText(IReadOnlyList<Source> sources)
	{
		var byHash = new SortedDictionary<uint, Engine_FxEffectJson.Definition>();
		foreach (Source source in sources)
		{
			byHash[Engine_FxEffectJson.HashOf(source.Effect.Name)] = source.Effect;
		}

		static string N(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
		var text = new StringBuilder();
		foreach ((uint hash, Engine_FxEffectJson.Definition d) in byHash)
		{
			float scale = d.Scale > 0.0001f ? d.Scale : 1f;
			float r = d.Tint.Length > 0 ? d.Tint[0] : 1f;
			float g = d.Tint.Length > 1 ? d.Tint[1] : 1f;
			float b = d.Tint.Length > 2 ? d.Tint[2] : 1f;
			if (scale == 1f && r == 1f && g == 1f && b == 1f)
			{
				continue;
			}

			text.Append("0x").Append(hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
				.Append(' ').Append(N(scale)).Append(' ').Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b))
				.Append('\n');
		}

		// The emitters and systems by the id the guest's bank carries for them (Apply's own rule: the
		// stated id, else the one derived from the effect and slot); a later source of the same id wins.
		var spins = new SortedDictionary<uint, float>();
		var aligns = new SortedDictionary<uint, float[]>();
		foreach (Source source in sources)
		{
			Engine_FxEffectJson.Definition d = source.Effect;
			for (int i = 0; i < d.Emitters.Count; i++)
			{
				FxEmitter e = d.Emitters[i];
				uint id = e.EntityId != 0 ? e.EntityId : Engine_FxEffectJson.DerivedId(d.Name, "emitter", i);
				if (e.Spin != 0f && float.IsFinite(e.Spin) && e.TypeId is 0 or 9)
				{
					spins[id] = e.Spin;
				}
				else
				{
					spins.Remove(id);
				}
			}

			for (int i = 0; i < d.Systems.Count; i++)
			{
				FxSystemDef s = d.Systems[i];
				uint id = s.Id != 0 ? s.Id : Engine_FxEffectJson.DerivedId(d.Name, "system", i);
				if (MeshAlignOf(s) is { } q)
				{
					aligns[id] = q;
				}
				else
				{
					aligns.Remove(id);
				}
			}
		}

		foreach ((uint id, float spin) in spins)
		{
			text.Append("spin 0x").Append(id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
				.Append(' ').Append(N(spin)).Append('\n');
		}

		foreach ((uint id, float[] q) in aligns)
		{
			text.Append("align 0x").Append(id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
				.Append(' ').Append(N(q[0])).Append(' ').Append(N(q[1])).Append(' ').Append(N(q[2])).Append(' ').Append(N(q[3]))
				.Append('\n');
		}

		return text.ToString();
	}

	/// <summary>
	/// The rest orientation a system's mesh particles are born with (<see cref="FxVisual.MeshAlign"/>,
	/// normalised): its first drawn visual's, when that visual is a mesh stating one; else null.
	/// </summary>
	public static float[]? MeshAlignOf(FxSystemDef system)
	{
		foreach (FxVisual v in system.Visuals)
		{
			if (!v.HasMesh && v.Texture.Length == 0)
			{
				continue;
			}

			if (!v.HasMesh || !v.HasMeshAlign || v.MeshAlign.Length < 4)
			{
				return null;
			}

			float l = MathF.Sqrt((v.MeshAlign[0] * v.MeshAlign[0]) + (v.MeshAlign[1] * v.MeshAlign[1]) +
				(v.MeshAlign[2] * v.MeshAlign[2]) + (v.MeshAlign[3] * v.MeshAlign[3]));
			if (!(l > 1e-6f) || !float.IsFinite(l))
			{
				return null;
			}

			// A quaternion already unit to float precision is stated as written (the guest normalises
			// what it reads either way).
			float k = MathF.Abs(l - 1f) < 1e-5f ? 1f : 1f / l;
			return new[] { v.MeshAlign[0] * k, v.MeshAlign[1] * k, v.MeshAlign[2] * k, v.MeshAlign[3] * k };
		}

		return null;
	}

	/// <summary>The guest's bank, or the dress table beside it: what <see cref="ReloadGuestBank"/> reloads for.</summary>
	public static bool IsGuestBankFile(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.EndsWith("art/particles/particle_bank.bnk", StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(DressKey, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A remount changed the guest's bank (<paramref name="changed"/> names it, or its dress table):
	/// the guest reads it again through its own loader on its next tick (<c>fx_reload</c>,
	/// particle_dress.cpp ReloadParticleBank) — every spawn after that builds from the new bank, live
	/// instances finish with the one they were built from. True when the reload was posted.
	/// </summary>
	public static bool ReloadGuestBank(IReadOnlyList<string> changed)
	{
		if (!changed.Any(IsGuestBankFile) || !Refii.Host.HostBridge.IsGuestUp)
		{
			return false;
		}

		Refii.Host.HostBridge.PostModCommand(ReloadCommand);
		return true;
	}

	/// <summary>The guest tick's command reading the bank again (src/vfs/src/hooks/mod_commands.cpp).</summary>
	public const string ReloadCommand = "fx_reload";

	/// <summary>
	/// A bank for previewing <paramref name="effect"/> without touching the shared one: the shared
	/// bank's lists copied (the entries shared, never edited) and the effect overlaid by the same
	/// rules. Hand it to <see cref="FableFx.BankOverride"/> and name the effect.
	/// </summary>
	public static FxBank PreviewBank(FxBank shared, Engine_FxEffectJson.Definition effect)
	{
		var bank = new FxBank { Ok = shared.Ok, Version = shared.Version };
		bank.Emitters.AddRange(shared.Emitters);
		bank.Systems.AddRange(shared.Systems);
		bank.Effects.AddRange(shared.Effects);
		foreach ((uint type, uint owner) in shared.TypeToEffect)
		{
			bank.TypeToEffect[type] = owner;
		}

		Apply(bank, new[] { new Source("preview", string.Empty, effect) });
		return bank;
	}

	/// <summary>
	/// The conflicts the enabled mods' effect files would have, without mounting: what the Mods
	/// panel tallies beside file and text-row conflicts.
	/// </summary>
	public static List<(string Table, RowConflict Conflict)> DetectConflicts(ModLoadOrder order, IReadOnlyList<ModInstall> installs)
	{
		var found = new List<(string Table, RowConflict Conflict)>();
		if (order.Enabled.Count < 2)
		{
			return found;
		}

		var sources = new List<Source>();
		var ignored = new List<string>();
		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			ModInstall? mod = installs.FirstOrDefault(m => string.Equals(m.Name, order.Enabled[i], StringComparison.OrdinalIgnoreCase));
			if (mod is null)
			{
				continue;
			}

			var overlay = new ModOverlay { Name = mod.Name, PackagePath = mod.PackagePath, AssetRoot = mod.PackagePath };
			foreach ((string path, string text) in ReadFiles(overlay, ignored))
			{
				foreach (Engine_FxEffectJson.Definition effect in Engine_FxEffectJson.Parse(text, new List<Engine_FxEffectJson.Issue>()))
				{
					sources.Add(new Source(mod.Name, path, effect));
				}
			}
		}

		if (sources.Count == 0)
		{
			return found;
		}

		foreach (RowConflict conflict in Apply(new FxBank(), sources).Conflicts)
		{
			found.Add((ConflictTable, conflict));
		}

		return found;
	}

	/// <summary>Whether a package path (any case, <c>files/</c> stripped) is an effect file, and its file name without <c>.json</c>.</summary>
	public static bool IsEffectFile(string path, out string id)
	{
		id = string.Empty;
		string normalized = path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
		if (normalized.StartsWith("files/", StringComparison.Ordinal))
		{
			normalized = normalized["files/".Length..];
		}

		if (!normalized.StartsWith(Folder, StringComparison.Ordinal) ||
			!normalized.EndsWith(".json", StringComparison.Ordinal) ||
			normalized.IndexOf('/', Folder.Length) >= 0)
		{
			return false;
		}

		id = normalized[Folder.Length..^".json".Length];
		return id.Length != 0;
	}

	/// <summary>The package path of an effect's own file.</summary>
	public static string PathOf(string effectName) => Folder + effectName.ToLowerInvariant() + ".json";

	/// <summary>
	/// One mod's effect files, by package path, in path order: a packed <c>.f2mod</c> or a loose
	/// asset tree, as the spells kind reads its files.
	/// </summary>
	private static List<(string Path, string Text)> ReadFiles(ModOverlay overlay, List<string> lines)
	{
		var found = new List<(string Path, string Text)>();
		string source = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
		if (File.Exists(source) && source.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
		{
			if (!F2ModPackage.TryOpen(source, out F2ModPackage? package, out string error) || package is null)
			{
				lines.Add($"REFUSED {overlay.Name}: {error}");
				return found;
			}

			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					if (!IsEffectFile(entry.Path, out string id))
					{
						continue;
					}

					try
					{
						found.Add((Folder + id + ".json", Encoding.UTF8.GetString(package.ReadEntryBytes(entry))));
					}
					catch (Exception exception)
					{
						lines.Add($"REFUSED {overlay.Name}/{entry.Path}: {exception.Message}");
					}
				}
			}
		}
		else if (Directory.Exists(overlay.AssetRoot))
		{
			foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
			{
				string relative = Path.GetRelativePath(overlay.AssetRoot, file);
				if (!IsEffectFile(relative, out string id))
				{
					continue;
				}

				try
				{
					found.Add((Folder + id + ".json", File.ReadAllText(file)));
				}
				catch (Exception exception)
				{
					lines.Add($"REFUSED {overlay.Name}/{relative}: {exception.Message}");
				}
			}
		}

		found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
		return found;
	}

	/// <summary>A source's emitter as the bank's own object, under its final id: a copy, so two applications never share one.</summary>
	private static FxEmitter CopyEmitter(FxEmitter e, uint id)
	{
		var d = new Engine_FxEffectJson.Definition { Name = "copy" };
		d.Emitters.Add(e);
		d.Parts.Add(new FxEffectPart(0, -1, 0, 0));
		FxEmitter copy = Engine_FxEffectJson.Copy(d).Emitters[0];
		copy.EntityId = id;
		return copy;
	}

	/// <summary>An emitter's file form, the record compared when deciding whether a statement only references it.</summary>
	private static string FileForm(FxEmitter e)
	{
		var d = new Engine_FxEffectJson.Definition { Name = "copy" };
		d.Emitters.Add(e);
		d.Parts.Add(new FxEffectPart(0, -1, 0, 0));
		return Engine_FxEffectJson.Serialize(d);
	}

	private static string FileForm(FxSystemDef s)
	{
		var d = new Engine_FxEffectJson.Definition { Name = "copy" };
		d.Emitters.Add(new FxEmitter { TypeId = 6 });
		d.Systems.Add(s);
		d.Parts.Add(new FxEffectPart(0, 0, 0, 0));
		return Engine_FxEffectJson.Serialize(d);
	}

	private static FxSystemDef CopySystem(FxSystemDef s, uint id)
	{
		var d = new Engine_FxEffectJson.Definition { Name = "copy" };
		d.Emitters.Add(new FxEmitter { TypeId = 6 });
		d.Systems.Add(s);
		d.Parts.Add(new FxEffectPart(0, 0, 0, 0));
		FxSystemDef copy = Engine_FxEffectJson.Copy(d).Systems[0];
		copy.Id = id;
		return copy;
	}
}
