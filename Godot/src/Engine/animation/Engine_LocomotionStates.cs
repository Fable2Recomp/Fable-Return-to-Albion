using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Animations;

/// <summary>Whether a state belongs to the combat pose or the neutral one.</summary>
public enum LocomotionCombatState
{
	Null = 0,
	NotSet = 1,
	Set = 2,
}

/// <summary>Which foot the character leads with. GOOFY is the mirrored footing.</summary>
public enum LocomotionStance
{
	Null = 0,
	Neutral = 1,
	Regular = 2,
	Goofy = 3,
}

public enum LocomotionTransition
{
	Null = 0,
	Rotation = 1,
	TranslationAccelerate = 2,
	TranslationDecelerate = 3,
	MatchFooting = 4,
}

public enum LocomotionFootMatch
{
	Null = 0,
	Absolute = 1,
	Relative = 2,
}

public enum LocomotionAnimBlend
{
	Null = 0,
	Blend = 1,
	MatchFooting = 2,
}

/// <summary>Which way the character is hemmed in. A BITMASK, not an ordinal.</summary>
[Flags]
public enum LocomotionConfinement
{
	None = 0,
	Front = 1,
	Back = 2,
	Left = 4,
	Right = 8,
	BackCliffEdge = 16,
	All = 31,
}

/// <summary>
/// A numeric range, stored as the script states it.
///
/// <see cref="Contains"/> is STRICT at both ends because the native test is: the area check at
/// `0x822CC720` compares with `fcmpu`/`ble`/`bge` and rejects on equality. That is not a
/// rounding detail — it is why one state's bound is `180.1` rather than `180`, since a sample
/// at exactly pi would fail a strict `&lt; 180`.
/// </summary>
public readonly record struct LocomotionRange(float Min, float Max)
{
	public bool Contains(float value) => value > Min && value < Max;

	/// <summary>Inclusive form, used only by the vertical-velocity test, which is not strict.</summary>
	public bool ContainsInclusive(float value) => value >= Min && value <= Max;
}

/// <summary>
/// A region of the (magnitude, heading) plane, as the script's CVector3 pair states it.
///
/// The script writes these as `{ CVector3(minMag, minAngle, 0), CVector3(maxMag, maxAngle, 0) }`
/// — a min corner and a max corner, NOT a centre and an extent. The third component is 0 in
/// every one of the 34 states that carries an area, so it is read and kept but means nothing
/// yet; it is not silently dropped in case a later state uses it.
/// </summary>
public readonly record struct LocomotionArea(LocomotionRange Magnitude, LocomotionRange Angle)
{
	/// <summary>
	/// What the native reader writes for an area the script omits: `(-1, -181)` to
	/// `(-1, +181)`.
	///
	/// This is a SENTINEL MEANING "ALWAYS PASS", not an empty box. Both magnitudes being
	/// negative is the flag the area test at `0x822CC720` reads to skip the magnitude
	/// comparison entirely, and ±181 degrees is wider than any angle a sample can hold. An
	/// omitted area that defaulted to all-zero would instead reject everything, which for the
	/// turn states — none of which state an entry `VelocityArea` — would make them unreachable.
	/// </summary>
	public static readonly LocomotionArea Disabled =
		new(new LocomotionRange(-1, -1), new LocomotionRange(-181, 181));

	/// <summary>True when the script stated this area rather than leaving the sentinel.</summary>
	public bool IsPresent => !Equals(Disabled);

	/// <summary>
	/// The native test, verbatim from `0x822CC720`.
	///
	/// <paramref name="magnitude"/> is raw for a control area and a FRACTION OF MAX SPEED for a
	/// velocity area — the caller normalises, because the two areas are stored identically and
	/// only the call site knows which is which. <paramref name="angleRadians"/> is radians
	/// while the stored bounds are DEGREES, converted here at test time exactly as the guest
	/// does. There is no angle wrapping anywhere in the native test, so none is added here.
	/// </summary>
	public bool Contains(float magnitude, float angleRadians)
	{
		if (Magnitude.Min >= 0 || Magnitude.Max >= 0)
		{
			if (!Magnitude.Contains(magnitude))
			{
				return false;
			}
		}

		return angleRadians > Mathf.DegToRad(Angle.Min) &&
			angleRadians < Mathf.DegToRad(Angle.Max);
	}
}

/// <summary>
/// Movement tuning, as an OVERLAY.
///
/// A state's own `MovementParams` names only the handful of fields it changes — `Idle` states
/// six, `Sprint` four — so every field is nullable and an absent one means "keep what the
/// character already had". Collapsing this to a plain struct of floats would turn every field
/// the state did not mention into a zero, which for `MaxSpeed` means a character that cannot
/// move and for `FrictionCoefficient` means one that never stops.
/// </summary>
public sealed class LocomotionMovementParams
{
	public float? MinControlMagForMovement { get; init; }

	public float? MinVelMagForMovement { get; init; }

	public float? MaxTurnSpeedStatic { get; init; }

	public float? MaxTurnSpeedRunning { get; init; }

	public float? MaxSpeed { get; init; }

	public float? SprintSpeedMultiplier { get; init; }

	public float? SprintTerminationSpeedFraction { get; init; }

	public float? FrictionCoefficient { get; init; }

	public float? MaxSpeedLateralStatic { get; init; }

	public float? MaxSpeedLateralRunning { get; init; }

	public static LocomotionMovementParams From(Engine_LuaTable? table) => table is null
		? new LocomotionMovementParams()
		: new LocomotionMovementParams
		{
			MinControlMagForMovement = Optional(table, "MinControlMagForMovement"),
			MinVelMagForMovement = Optional(table, "MinVelMagForMovement"),
			MaxTurnSpeedStatic = Optional(table, "MaxTurnSpeedStatic"),
			MaxTurnSpeedRunning = Optional(table, "MaxTurnSpeedRunning"),
			MaxSpeed = Optional(table, "MaxSpeed"),
			SprintSpeedMultiplier = Optional(table, "SprintSpeedMultiplier"),
			SprintTerminationSpeedFraction = Optional(table, "SprintTerminationSpeedFraction"),
			FrictionCoefficient = Optional(table, "FrictionCoefficient"),
			MaxSpeedLateralStatic = Optional(table, "MaxSpeedLateralStatic"),
			MaxSpeedLateralRunning = Optional(table, "MaxSpeedLateralRunning"),
		};

	/// <summary>This overlay laid over <paramref name="under"/>, field by field.</summary>
	public LocomotionMovementParams Over(LocomotionMovementParams under) => new()
	{
		MinControlMagForMovement = MinControlMagForMovement ?? under.MinControlMagForMovement,
		MinVelMagForMovement = MinVelMagForMovement ?? under.MinVelMagForMovement,
		MaxTurnSpeedStatic = MaxTurnSpeedStatic ?? under.MaxTurnSpeedStatic,
		MaxTurnSpeedRunning = MaxTurnSpeedRunning ?? under.MaxTurnSpeedRunning,
		MaxSpeed = MaxSpeed ?? under.MaxSpeed,
		SprintSpeedMultiplier = SprintSpeedMultiplier ?? under.SprintSpeedMultiplier,
		SprintTerminationSpeedFraction =
			SprintTerminationSpeedFraction ?? under.SprintTerminationSpeedFraction,
		FrictionCoefficient = FrictionCoefficient ?? under.FrictionCoefficient,
		MaxSpeedLateralStatic = MaxSpeedLateralStatic ?? under.MaxSpeedLateralStatic,
		MaxSpeedLateralRunning = MaxSpeedLateralRunning ?? under.MaxSpeedLateralRunning,
	};

	private static float? Optional(Engine_LuaTable table, string key) =>
		table.Get(key) is double value ? (float)value : null;
}

/// <summary>
/// One locomotion state, exactly as the shipped script defines it.
///
/// TWO CONDITION SETS, NOT ONE. `VelocityArea`/`ControlArea` are the ENTRY test — whether this
/// state may be moved into — and the `Termination*` family is a separate, wider test for
/// whether the state may be stayed in. `Turn90CW` enters on a facing error of 45..135 degrees
/// and terminates only below 2, so treating either set as the other would make every turn
/// either unreachable or unleaveable.
/// </summary>
public sealed class LocomotionState
{
	public required string Name { get; init; }

	/// <summary>
	/// Clip names, in the order the script lists them. Usually variants of one motion — Idle
	/// offers {Pose, PoseLF, PoseRF}, LocomoteForwards offers {Run, WalkSlow, Walk}.
	/// </summary>
	public required IReadOnlyList<string> Anim { get; init; }

	/// <summary>Successor states this one may move to, in the script's order.</summary>
	public required IReadOnlyList<string> States { get; init; }

	public float BlendInTime { get; init; }

	public LocomotionArea VelocityArea { get; init; }

	public LocomotionArea ControlArea { get; init; }

	public LocomotionArea TerminationVelocityArea { get; init; }

	public LocomotionArea TerminationControlArea { get; init; }

	/// <summary>Facing error, in degrees, that lets this state be entered.</summary>
	public LocomotionRange TargetFacingAngles { get; init; }

	public LocomotionRange TerminationTargetFacingAngles { get; init; }

	public LocomotionRange VerticalVelocityRange { get; init; }

	public LocomotionRange TerminationVerticalVelocityRange { get; init; }

	public LocomotionCombatState CombatState { get; init; }

	public LocomotionStance Stance { get; init; }

	public LocomotionTransition TransitionType { get; init; }

	public LocomotionFootMatch FootMatchType { get; init; }

	public LocomotionAnimBlend AnimBlendType { get; init; }

	public LocomotionConfinement Confinement { get; init; }

	public bool Symmetrical { get; init; }

	public bool IdleState { get; init; }

	/// <summary>Defaults TRUE. Only an idle state with this off is rejected while looking.</summary>
	public bool AllowLooking { get; init; } = true;

	public bool AvailableToChild { get; init; } = true;

	/// <summary>
	/// Seconds this state's condition must have held before it may be entered.
	///
	/// DEFAULTS TO -1, meaning "immediately" — the scan treats anything below 1e-4 as no wait.
	/// Defaulting it to 0 would be almost right and occasionally wrong; defaulting it to a
	/// positive number would stall every state the script does not mention.
	/// </summary>
	public float ValidTime { get; init; } = -1;

	/// <summary>Defaults to 1.0, which is loose enough to admit any pose.</summary>
	public float FeetMatchPoseTolerance { get; init; } = 1.0f;

	/// <summary>Whether the confinement mask must match exactly rather than overlap.</summary>
	public bool ExactConfinement { get; init; }

	public LocomotionMovementParams MovementParams { get; init; } = new();

	/// <summary>Which optional fields the script actually stated, so absent never reads as 0.</summary>
	public required HashSet<string> Stated { get; init; }

	public bool Has(string field) => Stated.Contains(field);

	public override string ToString() => Name;
}

/// <summary>
/// The hero's locomotion state machine, read from the player's own copy of the game.
///
/// WHERE THIS COMES FROM AND WHY IT IS NOT A TABLE IN THIS REPO. The 41 states live in
/// `scripts/miscellaneous/herolocomotionstates.lua` inside `gamescripts.bnk`, as compiled
/// bytecode. Transcribing them into C# would put derived game data in the tree, so instead
/// the chunk is read out of the bank and evaluated on the player's machine every run. It
/// costs about a millisecond once.
///
/// The enums it references (`ECombatState`, `EStance`, `EConfinementType`, …) are defined by
/// sibling scripts and are seeded here as constants rather than read. They are a handful of
/// small integers that are part of the contract — the same class of thing as a struct offset —
/// and several of those sibling scripts ship as plain SOURCE, which this reader deliberately
/// cannot parse. `EConfinementType` is a bitmask (1/2/4/8/16), not an ordinal.
/// </summary>
public static class Engine_LocomotionStates
{
	public const string ScriptPath = "scripts/miscellaneous/herolocomotionstates.lua";

	private const string RegisterGlobal = "HeroLocomotionStateRegister";

	private const string StatePrefix = "HeroLocomotionState";

	private const string ParametersPrefix = "HeroLocomotionData";

	private static Func<string, byte[]?>? _resolver;

	private static readonly List<LocomotionState> Ordered = new();

	private static readonly Dictionary<string, LocomotionState> ByName = new();

	private static readonly Dictionary<string, LocomotionMovementParams> ParameterSets = new();

	private static bool _tried;

	public static void SetResolver(Func<string, byte[]?>? resolver)
	{
		if (_resolver == resolver)
		{
			return;
		}

		_resolver = resolver;
		Reset();
	}

	public static void Reset()
	{
		_tried = false;
		Ordered.Clear();
		ByName.Clear();
		ParameterSets.Clear();
	}

	/// <summary>The states in the script's own registration order.</summary>
	public static IReadOnlyList<LocomotionState> All
	{
		get
		{
			EnsureLoaded();
			return Ordered;
		}
	}

	/// <summary>The `HeroLocomotionData*` tuning sets, keyed without their prefix.</summary>
	public static IReadOnlyDictionary<string, LocomotionMovementParams> Parameters
	{
		get
		{
			EnsureLoaded();
			return ParameterSets;
		}
	}

	public static LocomotionState? Find(string? name)
	{
		EnsureLoaded();
		return name is not null && ByName.TryGetValue(name, out LocomotionState? state)
			? state
			: null;
	}

	public static bool EnsureLoaded()
	{
		if (_tried)
		{
			return Ordered.Count != 0;
		}

		_tried = true;

		byte[]? bytes = _resolver?.Invoke(ScriptPath);
		if (bytes is null)
		{
			GD.Print($"[loco] {ScriptPath} not found in the mounted banks");
			return false;
		}

		if (!Engine_LuaChunk.TryLoad(bytes, out Engine_LuaProto? proto, out string error))
		{
			GD.Print($"[loco] {ScriptPath} is not a readable Lua chunk: {error}");
			return false;
		}

		var globals = Seed();
		if (!Engine_LuaDataVm.TryEvaluate(proto, globals, Natives, out error))
		{
			GD.Print($"[loco] {ScriptPath} did not evaluate: {error}");
			return false;
		}

		Build(globals);
		return Ordered.Count != 0;
	}

	private static void Build(Dictionary<string, object?> globals)
	{
		foreach ((string name, object? value) in globals)
		{
			if (name.StartsWith(ParametersPrefix, StringComparison.Ordinal) &&
				value is Engine_LuaTable parameters)
			{
				ParameterSets[name[ParametersPrefix.Length..]] =
					LocomotionMovementParams.From(parameters);
			}
		}

		var register = globals.GetValueOrDefault(RegisterGlobal) as Engine_LuaTable;
		if (register is null)
		{
			GD.Print($"[loco] {RegisterGlobal} is missing; no states registered");
			return;
		}

		var undefined = new List<string>();

		foreach (object? entry in register.Items)
		{
			if (entry is not string name)
			{
				continue;
			}

			if (globals.GetValueOrDefault(name) is not Engine_LuaTable table)
			{
				// The shipped register names two states the shipped script never defines.
				// They are not referenced as anyone's successor either, so they are vestigial
				// rather than a read failure — reported once, then dropped.
				undefined.Add(name);
				continue;
			}

			LocomotionState state = Read(name, table);
			ByName[name] = state;
			Ordered.Add(state);
		}

		int dangling = 0;
		foreach (LocomotionState state in Ordered)
		{
			foreach (string successor in state.States)
			{
				if (!ByName.ContainsKey(successor))
				{
					dangling++;
				}
			}
		}

		GD.Print(
			$"[loco] {Ordered.Count} state(s), {ParameterSets.Count} tuning set(s)" +
			(undefined.Count != 0 ? $", {undefined.Count} registered but undefined " +
				$"({string.Join(", ", undefined)})" : string.Empty) +
			(dangling != 0 ? $", {dangling} unresolved successor reference(s)" : string.Empty));
	}

	/// <summary>
	/// One state, with the defaults the NATIVE READER applies — not C#'s zeroes.
	///
	/// `sub_827F2BB8` reads each field with an explicit default, and several of those defaults
	/// are not zero: `AllowLooking` and `Stance` and `AnimBlendType` come in as 1,
	/// `FeetMatchPoseTolerance` as 1.0, `ValidTime` as -1.0, the two areas as an always-pass
	/// sentinel, `VerticalVelocityRange` as ±100 and `TargetFacingAngles` as ±181. Taking the
	/// language default for any of them silently inverts that field's meaning.
	///
	/// The `Termination*` family defaults to a COPY of its entry counterpart — the reader does
	/// a literal byte copy at `0x827F3030` — because those fields answer "may I stay?" against
	/// the same box that answered "may I enter?" unless the script widens them.
	/// </summary>
	private static LocomotionState Read(string name, Engine_LuaTable table)
	{
		var stated = new HashSet<string>(table.Fields.Keys, StringComparer.Ordinal);

		LocomotionArea control = AreaOr(table, "ControlArea", LocomotionArea.Disabled);
		LocomotionArea velocity = AreaOr(table, "VelocityArea", LocomotionArea.Disabled);
		LocomotionRange vertical = PairOr(table, "VerticalVelocityRange", new(-100, 100));
		LocomotionRange facing = PairOr(table, "TargetFacingAngles", new(-181, 181));

		var combatState = (LocomotionCombatState)table.Int("CombatState");

		return new LocomotionState
		{
			Name = name,
			Stated = stated,
			Anim = Names(table.Table("Anim")),
			States = Names(table.Table("States")),
			BlendInTime = table.Float("BlendInTime"),
			ControlArea = control,
			VelocityArea = velocity,
			VerticalVelocityRange = vertical,
			TargetFacingAngles = facing,
			TerminationControlArea = AreaOr(table, "TerminationControlArea", control),
			TerminationVelocityArea = AreaOr(table, "TerminationVelocityArea", velocity),
			TerminationVerticalVelocityRange =
				PairOr(table, "TerminationVerticalVelocityRange", vertical),
			TerminationTargetFacingAngles =
				PairOr(table, "TerminationTargetFacingAngles", facing),
			CombatState = combatState,
			Stance = (LocomotionStance)table.Int("Stance", (int)LocomotionStance.Neutral),
			TransitionType = (LocomotionTransition)table.Int("TransitionType"),
			FootMatchType = FootMatch(table, combatState),
			AnimBlendType =
				(LocomotionAnimBlend)table.Int("AnimBlendType", (int)LocomotionAnimBlend.Blend),
			Confinement = (LocomotionConfinement)table.Int("Confinement"),
			ExactConfinement = table.Bool("ExactConfinement"),
			Symmetrical = table.Bool("Symmetrical"),
			IdleState = table.Bool("IdleState"),
			AllowLooking = table.Bool("AllowLooking", true),
			AvailableToChild = table.Bool("AvailableToChild", true),
			ValidTime = table.Float("ValidTime", -1.0f),
			FeetMatchPoseTolerance = table.Float("FeetMatchPoseTolerance", 1.0f),
			MovementParams = LocomotionMovementParams.From(table.Table("MovementParams")),
		};
	}

	/// <summary>
	/// `FootMatchType`, including the fixup the script never states.
	///
	/// When the field is absent the reader derives it from combat rather than leaving it zero:
	/// `*(def+24) = (*(def+44) == 2) + 1` at `0x827F2F90`, so a combat state gets RELATIVE
	/// matching and everything else gets ABSOLUTE. Eleven of the 41 states state it; the other
	/// thirty get this.
	/// </summary>
	private static LocomotionFootMatch FootMatch(
		Engine_LuaTable table, LocomotionCombatState combatState)
	{
		int stated = table.Int("FootMatchType");
		if (stated != 0)
		{
			return (LocomotionFootMatch)stated;
		}

		return combatState == LocomotionCombatState.Set
			? LocomotionFootMatch.Relative
			: LocomotionFootMatch.Absolute;
	}

	private static LocomotionArea AreaOr(
		Engine_LuaTable table, string key, LocomotionArea fallback) =>
		table.Table(key) is { } area ? Area(area) : fallback;

	private static LocomotionRange PairOr(
		Engine_LuaTable table, string key, LocomotionRange fallback) =>
		table.Get(key) is Vector3 vector
			? new LocomotionRange(vector.X, vector.Y)
			: fallback;

	private static IReadOnlyList<string> Names(Engine_LuaTable? table)
	{
		if (table is null)
		{
			return Array.Empty<string>();
		}

		var names = new List<string>(table.Items.Count);
		foreach (object? item in table.Items)
		{
			if (item is string name)
			{
				names.Add(name);
			}
		}

		return names;
	}

	/// <summary>A `{ CVector3 min, CVector3 max }` pair as its two independent ranges.</summary>
	private static LocomotionArea Area(Engine_LuaTable? table)
	{
		if (table is null || table.Items.Count < 2 ||
			table.Items[0] is not Vector3 min || table.Items[1] is not Vector3 max)
		{
			return default;
		}

		return new LocomotionArea(
			new LocomotionRange(min.X, max.X),
			new LocomotionRange(min.Y, max.Y));
	}

	private static Dictionary<string, object?> Seed() => new()
	{
		["CVector3"] = new Engine_LuaNative("CVector3"),
		["ECombatState"] = Enum(("COMBAT_STATE_NULL", 0), ("COMBAT_STATE_NOT_SET", 1),
			("COMBAT_STATE_SET", 2)),
		["EStance"] = Enum(("STANCE_NULL", 0), ("STANCE_NEUTRAL", 1), ("STANCE_REGULAR", 2),
			("STANCE_GOOFY", 3), ("MAX_STANCE", 4)),
		["ETransitionType"] = Enum(("TRANSITION_NULL", 0), ("TRANSITION_ROTATION", 1),
			("TRANSITION_TRANSLATION_ACCELERATE", 2), ("TRANSITION_TRANSLATION_DECELERATE", 3),
			("TRANSITION_MATCH_FOOTING", 4)),
		["EFootMatchType"] = Enum(("FOOT_MATCH_NULL", 0), ("FOOT_MATCH_ABSOLUTE", 1),
			("FOOT_MATCH_RELATIVE", 2)),
		["EAnimBlendType"] = Enum(("ANIM_BLEND_NULL", 0), ("ANIM_BLEND_BLEND", 1),
			("ANIM_BLEND_MATCH_FOOTING", 2)),
		["EConfinementType"] = Enum(("CONFINED_NULL", 0), ("CONFINED_FRONT", 1),
			("CONFINED_BACK", 2), ("CONFINED_LEFT", 4), ("CONFINED_RIGHT", 8),
			("CONFINED_BACK_CLIFF_EDGE", 16), ("CONFINED_ALL", 31)),
	};

	private static Engine_LuaTable Enum(params (string Name, int Value)[] members)
	{
		var table = new Engine_LuaTable();
		foreach ((string name, int value) in members)
		{
			table.Set(name, (double)value);
		}

		return table;
	}

	private static object? Natives(string name, IReadOnlyList<object?> arguments)
	{
		if (name != "CVector3")
		{
			throw new InvalidOperationException($"{ScriptPath} calls unknown global {name}");
		}

		return new Vector3(Component(arguments, 0), Component(arguments, 1),
			Component(arguments, 2));
	}

	private static float Component(IReadOnlyList<object?> arguments, int index) =>
		index < arguments.Count && arguments[index] is double value ? (float)value : 0;
}
