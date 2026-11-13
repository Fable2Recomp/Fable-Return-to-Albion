using System;
using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_fx_light_limit`: only the nearest N effect lights (torch flames, braziers, spells:
/// the <see cref="OmniLight3D"/> each <c>FableFx</c> makes per effect light) are lit at once;
/// the rest fade out. 0, the default, lights them all. Android only.
/// </summary>
/// <remarks>
/// WHY. Godot's Mobile renderer pairs each mesh instance with up to 8 omni lights and runs the
/// material's light() once per paired light on every pixel. The placed fixtures are budgeted
/// (<c>placed_light_limit</c>, Engine_LightShadowBudget); the effect lights were not, and a
/// frozen effect's light stays lit (FableFx leaves its lights as they are), so a town at night
/// pairs dozens of flames with the walls in view.
/// <para>ONE PLACE SEES THEM ALL. FableFx reports each light it makes (<see cref="Track"/>) and
/// routes its visibility and energy writes through <see cref="SetShown"/> and
/// <see cref="SetEnergy"/>; Engine_FxDriver calls <see cref="Tick"/> after stepping every
/// system. A light is drawn when the effect wants it AND it holds a slot (or is still fading
/// out of one), at the effect's own energy times the slot's fade level, so the effect's own
/// fade (LightFade, its keyframed energy, its distance fade) is kept and never fought: FableFx
/// compares against its own cache, which keeps the unscaled values.</para>
/// <para>WHEN IT RANKS. Every <see cref="Interval"/> seconds, from the nearest eye (split
/// screen ranks from both), and at once in the same frame when an effect lights a new light or
/// lets one go, so a spell's flash is never late. A light the camera is past the distance fade
/// of, or at no energy, takes no slot: Godot draws nothing for it either. Hysteresis
/// (<see cref="Key"/>): a lit light keeps its slot until an unlit one is clearly nearer, so two
/// lights at about the same distance do not trade the slot back and forth.</para>
/// <para>A slot's change is a fade over <see cref="FadeSeconds"/>, except for a light the effect
/// has only just lit, which arrives at once on its own ramp. Glow halos read the light, so a
/// far flame's halo fades with it.</para>
/// Live: a <c>cvar_set</c> of the key takes effect at the next tick; 0 gives every light back.
/// </remarks>
public static class AndroidFxLightLimit
{
	public const string Key = "android_fx_light_limit";

	/// <summary>Seconds between full re-rankings, about 4 Hz.</summary>
	public const double Interval = 0.25;

	/// <summary>How long a light takes to leave or regain its slot.</summary>
	public const float FadeSeconds = 0.25f;

	/// <summary>A lit light ranks at this share of its distance...</summary>
	public const float LitScale = 0.8f;

	/// <summary>...less this many metres, so an unlit light must be clearly nearer to take its slot.</summary>
	public const float LitMarginMetres = 1.0f;

	/// <summary>An energy at or below this lights nothing and takes no slot.</summary>
	public const float NoEnergy = 1e-4f;

	private sealed class Entry
	{
		public OmniLight3D Light = null!;

		/// <summary>What the effect last asked for: shown or not, and at what energy.</summary>
		public bool Wanted;
		public float Energy;

		/// <summary>Whether the light holds a slot, and its fade level toward that.</summary>
		public bool Allowed = true;
		public float Level = 1f;

		/// <summary>Lit by the effect since the last ranking: arrives at once if it takes a slot.</summary>
		public bool Fresh;

		/// <summary>Metres to the nearest eye when last measured; MaxValue past its own distance fade.</summary>
		public float Distance = float.MaxValue;

		/// <summary>Whether <see cref="Distance"/> was measured since the light was tracked or the limit engaged.</summary>
		public bool Measured;

		/// <summary>The visibility and energy last written to the node.</summary>
		public bool ShownNow = true;
		public float EnergyNow = -1f;
	}

	private static readonly Dictionary<OmniLight3D, Entry> Entries = new();
	private static readonly List<Entry> All = new();
	private static readonly List<Entry> Fading = new();
	private static readonly Vector3[] Eyes = new Vector3[2];
	private static float[] _distance = new float[64];
	private static bool[] _lit = new bool[64];
	private static bool[] _keep = new bool[64];
	private static Entry[] _candidates = new Entry[64];
	private static int _prunedAt = 64;

	private static int? _limit;
	private static bool _hooked;
	private static bool _active;
	private static bool _dirty;
	private static double _since = double.MaxValue;
	private static int _eyes;

	/// <summary>The configured limit, read once and again on each live change; always 0 off a phone.</summary>
	public static int Limit
	{
		get
		{
			if (_limit is { } known) return known;
			int limit = 0;
			try
			{
				if (Refii.Core.Engine_Platform.IsAndroid)
				{
					limit = Math.Max(0, Refii.Core.Engine_RefiiConfig.Int(
						Refii.Core.Engine_RefiiConfig.Read(out _), Key, 0));
				}
			}
			catch (Exception)
			{
				limit = 0;
			}

			if (!_hooked)
			{
				_hooked = true;
				Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
				{
					if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase)) _limit = null;
				};
			}

			_limit = limit;
			return limit;
		}
	}

	/// <summary>Lights holding a slot after the last ranking, for the debug channel.</summary>
	public static int Lit { get; private set; }

	/// <summary>Lights that competed for a slot at the last ranking.</summary>
	public static int Competing { get; private set; }

	/// <summary>
	/// The ranking key: the distance, or for a light already lit a shorter one
	/// (<see cref="LitScale"/>, <see cref="LitMarginMetres"/>). Pure.
	/// </summary>
	public static float RankKey(float distance, bool lit)
	{
		if (!float.IsFinite(distance)) return float.MaxValue;
		return lit ? MathF.Max(0f, distance * LitScale - LitMarginMetres) : distance;
	}

	/// <summary>
	/// Which lights keep a slot: the <paramref name="limit"/> smallest <see cref="RankKey"/>s,
	/// ties to a lit light and then to the earlier one. Writes <paramref name="keep"/> for the
	/// first <c>distance.Length</c> lights and returns how many are kept; a limit of 0 or below,
	/// or no more lights than it, keeps them all. Pure.
	/// </summary>
	public static int Choose(ReadOnlySpan<float> distance, ReadOnlySpan<bool> lit, int limit, Span<bool> keep)
	{
		int n = distance.Length;
		if (limit <= 0 || n <= limit)
		{
			keep[..n].Fill(true);
			return n;
		}

		keep[..n].Clear();
		int taken = 0;
		for (; taken < limit; taken++)
		{
			int best = -1;
			float bestKey = float.MaxValue;
			for (int i = 0; i < n; i++)
			{
				if (keep[i]) continue;
				float key = RankKey(distance[i], lit[i]);
				if (best < 0 || key < bestKey || (key == bestKey && lit[i] && !lit[best]))
				{
					best = i;
					bestKey = key;
				}
			}

			if (best < 0) break;
			keep[best] = true;
		}

		return taken;
	}

	/// <summary>The fade level one step on: toward 1 while the light holds a slot, toward 0 when not. Pure.</summary>
	public static float Step(float level, bool allowed, float delta)
	{
		float step = delta / FadeSeconds;
		return allowed ? MathF.Min(1f, level + step) : MathF.Max(0f, level - step);
	}

	/// <summary>A light an effect has just made. Main thread.</summary>
	public static void Track(OmniLight3D light)
	{
		// Off a phone the limit never engages, so SetShown/SetEnergy write straight through: nothing to keep.
		if (!Refii.Core.Engine_Platform.IsAndroid) return;
		if (Entries.ContainsKey(light)) return;
		var entry = new Entry { Light = light, Wanted = light.Visible, Energy = light.LightEnergy };
		if (_active)
		{
			// Dark until its effect first shows it, which ranks it that frame: a new light past
			// the limit never flashes on for a ranking's wait.
			entry.Wanted = false;
			entry.Allowed = false;
			entry.Level = 0f;
			entry.ShownNow = false;
			entry.EnergyNow = entry.Energy;
			light.Visible = false;
		}

		Entries[light] = entry;
		All.Add(entry);
		if (All.Count >= _prunedAt * 2)
		{
			Prune();
			_prunedAt = Math.Max(64, All.Count);
		}
	}

	/// <summary>The effect shows or hides its light. Main thread.</summary>
	public static void SetShown(OmniLight3D light, bool shown)
	{
		if (!_active || !Entries.TryGetValue(light, out Entry? entry))
		{
			light.Visible = shown;
			return;
		}

		if (entry.Wanted == shown) return;
		entry.Wanted = shown;
		if (shown)
		{
			entry.Fresh = true;
			if (!entry.Allowed) _dirty = true;
		}
		else if (entry.Allowed)
		{
			// Its slot goes to the nearest waiting light now, not at the next ranking.
			_dirty = true;
		}

		Write(entry);
	}

	/// <summary>The effect sets its light's energy. Main thread.</summary>
	public static void SetEnergy(OmniLight3D light, float energy)
	{
		if (!_active || !Entries.TryGetValue(light, out Entry? entry))
		{
			light.LightEnergy = energy;
			return;
		}

		bool rose = entry.Energy <= NoEnergy && energy > NoEnergy;
		entry.Energy = energy;
		if (rose && entry.Wanted)
		{
			entry.Fresh = true;
			if (!entry.Allowed) _dirty = true;
		}

		Write(entry);
	}

	/// <summary>
	/// After every effect has stepped: ranks when due or asked for, and moves the fades. Main
	/// thread, once a frame (Engine_FxDriver).
	/// </summary>
	public static void Tick(Camera3D? camera, double delta)
	{
		int limit = Limit;
		if (limit <= 0)
		{
			if (_active) Release();
			return;
		}

		if (!_active) Engage();

		_since += delta;
		if ((_since >= Interval || _dirty) && ReadEyes(camera))
		{
			bool full = _since >= Interval;
			if (full) _since = 0;
			Rank(limit, full);
			_dirty = false;
		}

		for (int i = Fading.Count - 1; i >= 0; i--)
		{
			Entry entry = Fading[i];
			if (!GodotObject.IsInstanceValid(entry.Light))
			{
				Fading.RemoveAt(i);
				continue;
			}

			entry.Level = Step(entry.Level, entry.Allowed, (float)delta);
			Write(entry);
			if (entry.Level is 0f or 1f) Fading.RemoveAt(i);
		}
	}

	/// <summary>Turning on: what each node shows now is what its effect asked for.</summary>
	private static void Engage()
	{
		Prune();
		foreach (Entry entry in All)
		{
			entry.Wanted = entry.Light.Visible;
			entry.Energy = entry.Light.LightEnergy;
			entry.Allowed = true;
			entry.Level = 1f;
			entry.Fresh = false;
			entry.Measured = false;
			entry.ShownNow = entry.Wanted;
			entry.EnergyNow = entry.Energy;
		}

		Fading.Clear();
		_active = true;
		_dirty = true;
		_since = double.MaxValue;
	}

	/// <summary>Turning off: every light back to what its effect asked for.</summary>
	private static void Release()
	{
		Prune();
		foreach (Entry entry in All)
		{
			// The effect's own values, a hidden light's too: FableFx rewrites only what changed.
			entry.Allowed = true;
			entry.Level = 1f;
			if (entry.EnergyNow != entry.Energy) entry.Light.LightEnergy = entry.Energy;
			if (entry.ShownNow != entry.Wanted) entry.Light.Visible = entry.Wanted;
			entry.EnergyNow = entry.Energy;
			entry.ShownNow = entry.Wanted;
		}

		Fading.Clear();
		_active = false;
		Lit = 0;
		Competing = 0;
	}

	private static bool ReadEyes(Camera3D? camera)
	{
		_eyes = 0;
		foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(Refii.Core.Engine_Views.Primary(camera)))
		{
			if (_eyes == Eyes.Length) break;
			Eyes[_eyes++] = view.GlobalPosition;
		}

		return _eyes > 0;
	}

	/// <summary>
	/// Ranks the lights that may light something. A full ranking re-reads every competing
	/// light's place; one asked for between them reads only the lights lit since.
	/// </summary>
	private static void Rank(int limit, bool full)
	{
		int n = 0;
		for (int i = All.Count - 1; i >= 0; i--)
		{
			Entry entry = All[i];
			OmniLight3D light = entry.Light;
			if (!GodotObject.IsInstanceValid(light))
			{
				Entries.Remove(light);
				All.RemoveAt(i);
				continue;
			}

			if (!entry.Wanted || entry.Energy <= NoEnergy || !light.IsInsideTree())
			{
				// Takes no slot; shown again only by a ranking.
				Withdraw(entry);
				continue;
			}

			if (full || entry.Fresh || !entry.Measured)
			{
				entry.Measured = true;
				entry.Distance = NearestEye(light.GlobalPosition);
				if (light.DistanceFadeEnabled &&
					entry.Distance > light.DistanceFadeBegin + light.DistanceFadeLength)
				{
					// Past its own fade: Godot draws nothing for it, so it holds no slot.
					entry.Distance = float.MaxValue;
				}
			}

			if (entry.Distance == float.MaxValue)
			{
				Withdraw(entry);
				continue;
			}

			EnsureCapacity(n + 1);
			_candidates[n] = entry;
			_distance[n] = entry.Distance;
			_lit[n] = entry.Allowed;
			n++;
		}

		Competing = n;
		Lit = Choose(_distance.AsSpan(0, n), _lit.AsSpan(0, n), limit, _keep.AsSpan(0, n));
		for (int c = 0; c < n; c++)
		{
			Entry entry = _candidates[c];
			bool keep = _keep[c];
			if (keep && !entry.Allowed && entry.Fresh)
			{
				// The effect has only just lit it: its own ramp, no fade from the slot.
				entry.Level = 1f;
			}

			entry.Fresh = false;
			if (entry.Allowed != keep)
			{
				entry.Allowed = keep;
				StartFade(entry);
			}

			Write(entry);
			_candidates[c] = null!;
		}
	}

	/// <summary>A light that takes no slot: hidden now, since it lights nothing either way.</summary>
	private static void Withdraw(Entry entry)
	{
		entry.Fresh = false;
		if (!entry.Allowed && entry.Level == 0f) return;
		entry.Allowed = false;
		entry.Level = 0f;
		Write(entry);
	}

	private static void StartFade(Entry entry)
	{
		if (!Fading.Contains(entry)) Fading.Add(entry);
	}

	private static float NearestEye(Vector3 at)
	{
		float best = float.MaxValue;
		for (int e = 0; e < _eyes; e++)
		{
			best = MathF.Min(best, Eyes[e].DistanceSquaredTo(at));
		}

		return MathF.Sqrt(best);
	}

	/// <summary>Writes what the node should show: wanted and in or fading out of a slot, at the effect's energy times the level.</summary>
	private static void Write(Entry entry)
	{
		if (!GodotObject.IsInstanceValid(entry.Light)) return;
		bool shown = entry.Wanted && (entry.Allowed || entry.Level > 0f);
		if (shown)
		{
			float energy = entry.Energy * entry.Level;
			if (MathF.Abs(energy - entry.EnergyNow) > 1e-5f)
			{
				entry.Light.LightEnergy = energy;
				entry.EnergyNow = energy;
			}
		}

		if (shown != entry.ShownNow)
		{
			entry.Light.Visible = shown;
			entry.ShownNow = shown;
		}
	}

	private static void Prune()
	{
		for (int i = All.Count - 1; i >= 0; i--)
		{
			if (!GodotObject.IsInstanceValid(All[i].Light))
			{
				Entries.Remove(All[i].Light);
				All.RemoveAt(i);
			}
		}
	}

	private static void EnsureCapacity(int n)
	{
		if (n <= _distance.Length) return;
		int size = Math.Max(n, _distance.Length * 2);
		Array.Resize(ref _distance, size);
		Array.Resize(ref _lit, size);
		Array.Resize(ref _keep, size);
		Array.Resize(ref _candidates, size);
	}
}
