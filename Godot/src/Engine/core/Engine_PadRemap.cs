using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Core;

/// <summary>
/// The sixteen inputs of a pad, by POSITION: <see cref="A"/> is the south face button whatever
/// is printed on it, as Godot's <c>JoyButton</c> and the guest's XInput bits are positional.
/// </summary>
public enum PadInput
{
	A, B, X, Y, LB, RB, LT, RT, L3, R3, Start, Back, DpadUp, DpadDown, DpadLeft, DpadRight,
}

/// <summary>
/// One player's controller remap: for each of the GAME's buttons, the physical inputs that press
/// it. Applied per device where a joypad enters a pad (<c>GameInput.PadState.Merge</c> through
/// <c>GameInput.RemapPad</c>), so the keyboard, the debug channel and the replay — which state the
/// game's buttons directly — never pass through it.
/// </summary>
/// <remarks>
/// <para>The game has one flat pad scheme and each button does several things by context, so the
/// remap targets the game's BUTTONS, never an action: the row "Melee (X)" names which physical
/// inputs produce the game's X. A game button may have any number of physical sources (two
/// presses the button from either), none (unbound), and one physical input may press several
/// game buttons. Nothing is permuted behind the player's back: a swap is two assignments.</para>
/// <para>Settings: <c>padmap_&lt;button&gt;</c> for player one and <c>padmap2_&lt;button&gt;</c> for
/// player two (the button lowercased: <c>padmap_a</c>, <c>padmap2_dpadup</c>), each a comma list
/// of physical names (<see cref="Name"/>), <c>none</c> to unbind, absent or empty for the default
/// (the button itself). The prefix cannot collide with a mod's <c>pad_&lt;mod&gt;.&lt;action&gt;</c>.</para>
/// <para>A trigger pressing a trigger keeps its travel; any other pairing presses fully, and a
/// trigger pressing a button presses it past <see cref="TriggerPressed"/> (the XInput threshold).
/// No Godot types: what the tests cover.</para>
/// </remarks>
public sealed class Engine_PadRemap
{
	public const int Count = 16;

	public const string PlayerOnePrefix = "padmap_";

	public const string PlayerTwoPrefix = "padmap2_";

	/// <summary>The stored value that unbinds a game button.</summary>
	public const string Unbound = "none";

	/// <summary>A trigger counts as pressed past this, of 255.</summary>
	public const uint TriggerPressed = 30;

	/// <summary>The guest's XInput bit for each input; 0 for the triggers, which are analog.</summary>
	private static readonly uint[] Bits =
	{
		0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0, 0, 0x0040, 0x0080, 0x0010, 0x0020,
		0x0001, 0x0002, 0x0004, 0x0008,
	};

	/// <summary>Player one's and player two's remaps.</summary>
	private static readonly Engine_PadRemap[] Players = { new(), new() };

	/// <summary>Bumped whenever either player's remap changes, for a view that caches what it drew.</summary>
	public static int Generation { get; private set; }

	/// <summary>The remap of <paramref name="player"/> (1 or 2; anything else is player one's).</summary>
	public static Engine_PadRemap For(int player) => Players[player == 2 ? 1 : 0];

	/// <summary>Per game input, a mask over the physical inputs that press it.</summary>
	private readonly ushort[] _sources = Identity();

	private static ushort[] Identity()
	{
		var sources = new ushort[Count];
		for (int i = 0; i < Count; i++)
		{
			sources[i] = (ushort)(1 << i);
		}

		return sources;
	}

	/// <summary>Whether every game button is pressed by itself alone.</summary>
	public bool IsDefault
	{
		get
		{
			for (int i = 0; i < Count; i++)
			{
				if (_sources[i] != (ushort)(1 << i))
				{
					return false;
				}
			}

			return true;
		}
	}

	/// <summary>Whether <paramref name="game"/> is pressed by itself alone.</summary>
	public bool IsDefaultFor(PadInput game) => _sources[(int)game] == (ushort)(1 << (int)game);

	/// <summary>The physical inputs pressing <paramref name="game"/>, in <see cref="PadInput"/> order.</summary>
	public IReadOnlyList<PadInput> Sources(PadInput game) => Unpack(_sources[(int)game]);

	/// <summary>
	/// The physical input a prompt for <paramref name="game"/> shows: the button itself while it
	/// is one of the sources, else the first; null while the game button is unbound.
	/// </summary>
	public PadInput? Shown(PadInput game)
	{
		ushort mask = _sources[(int)game];
		if ((mask & (1 << (int)game)) != 0)
		{
			return game;
		}

		for (int i = 0; i < Count; i++)
		{
			if ((mask & (1 << i)) != 0)
			{
				return (PadInput)i;
			}
		}

		return null;
	}

	/// <summary>The game buttons <paramref name="physical"/> presses.</summary>
	public IReadOnlyList<PadInput> Targets(PadInput physical)
	{
		var targets = new List<PadInput>();
		for (int i = 0; i < Count; i++)
		{
			if ((_sources[i] & (1 << (int)physical)) != 0)
			{
				targets.Add((PadInput)i);
			}
		}

		return targets;
	}

	/// <summary>Makes exactly <paramref name="physical"/> press <paramref name="game"/>; empty unbinds it.</summary>
	public void Set(PadInput game, IEnumerable<PadInput> physical)
	{
		ushort mask = 0;
		foreach (PadInput input in physical)
		{
			mask |= (ushort)(1 << (int)input);
		}

		if (_sources[(int)game] != mask)
		{
			_sources[(int)game] = mask;
			++Generation;
		}
	}

	/// <summary>Every game button back to being pressed by itself.</summary>
	public void Reset()
	{
		if (!IsDefault)
		{
			Array.Copy(Identity(), _sources, Count);
			++Generation;
		}
	}

	/// <summary>
	/// Rewrites one device's buttons and triggers (the guest's XInput bits, 0..255 travel) into the
	/// game buttons they press.
	/// </summary>
	public void Apply(ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
	{
		if (IsDefault)
		{
			return;
		}

		uint inButtons = buttons, inLeft = leftTrigger, inRight = rightTrigger;
		buttons &= ~0xF3FFu;
		leftTrigger = rightTrigger = 0;
		for (int target = 0; target < Count; target++)
		{
			ushort mask = _sources[target];
			if (mask == 0)
			{
				continue;
			}

			uint travel = 0;
			for (int source = 0; source < Count && travel < 255; source++)
			{
				if ((mask & (1 << source)) == 0)
				{
					continue;
				}

				uint t = (PadInput)source switch
				{
					PadInput.LT => inLeft,
					PadInput.RT => inRight,
					_ => (inButtons & Bits[source]) != 0 ? 255u : 0u,
				};
				travel = Math.Max(travel, t);
			}

			switch ((PadInput)target)
			{
				case PadInput.LT:
					leftTrigger = Math.Max(leftTrigger, travel);
					break;
				case PadInput.RT:
					rightTrigger = Math.Max(rightTrigger, travel);
					break;
				default:
					if (travel > TriggerPressed)
					{
						buttons |= Bits[target];
					}

					break;
			}
		}
	}

	/// <summary>Reads this player's <c>padmap</c> settings; an absent, empty or unreadable value is the default.</summary>
	public void Read(IReadOnlyDictionary<string, string> values, int player)
	{
		for (int i = 0; i < Count; i++)
		{
			var game = (PadInput)i;
			IReadOnlyList<PadInput>? stored = values.TryGetValue(SettingKey(player, game), out string? raw)
				? ParseStored(raw)
				: null;
			Set(game, stored ?? new[] { game });
		}
	}

	/// <summary>Both players' remaps from the control settings.</summary>
	public static void ReadAll(IReadOnlyDictionary<string, string> values)
	{
		For(1).Read(values, 1);
		For(2).Read(values, 2);
	}

	/// <summary>The setting holding <paramref name="player"/>'s sources for <paramref name="game"/>.</summary>
	public static string SettingKey(int player, PadInput game) =>
		(player == 2 ? PlayerTwoPrefix : PlayerOnePrefix) + Name(game).ToLowerInvariant();

	/// <summary>
	/// A stored value's inputs: null for absent, empty or a list naming nothing known (the default
	/// stands), empty for <see cref="Unbound"/>.
	/// </summary>
	public static IReadOnlyList<PadInput>? ParseStored(string? raw)
	{
		string text = (raw ?? string.Empty).Trim().Trim('"').Trim();
		if (text.Length == 0)
		{
			return null;
		}

		if (text.Equals(Unbound, StringComparison.OrdinalIgnoreCase))
		{
			return Array.Empty<PadInput>();
		}

		var inputs = new List<PadInput>();
		foreach (string part in text.Split(','))
		{
			if (TryParse(part, out PadInput input) && !inputs.Contains(input))
			{
				inputs.Add(input);
			}
		}

		return inputs.Count == 0 ? null : inputs;
	}

	/// <summary>The stored form of a source list: <see cref="Unbound"/> for none, else the names comma-joined.</summary>
	public static string Format(IReadOnlyList<PadInput> inputs)
	{
		if (inputs.Count == 0)
		{
			return Unbound;
		}

		var text = new StringBuilder();
		foreach (PadInput input in inputs)
		{
			if (text.Length != 0)
			{
				text.Append(',');
			}

			text.Append(Name(input));
		}

		return text.ToString();
	}

	/// <summary>An input's name as the settings spell it — the Xbox position names the mods' pad rows use.</summary>
	public static string Name(PadInput input) => input.ToString();

	/// <summary>
	/// An input by name: the <see cref="PadInput"/> names, Godot's <c>JoyButton</c> names
	/// (LeftShoulder, LeftStick, ...), LS/RS, and the other families' printed names for the same
	/// positions' system buttons (Options/Menu/Plus are Start; Share/Create/View/Select/Minus are Back).
	/// </summary>
	public static bool TryParse(string? name, out PadInput input)
	{
		string n = (name ?? string.Empty).Trim().ToLowerInvariant();
		switch (n)
		{
			case "leftshoulder" or "l1": input = PadInput.LB; return true;
			case "rightshoulder" or "r1": input = PadInput.RB; return true;
			case "triggerleft" or "l2" or "zl": input = PadInput.LT; return true;
			case "triggerright" or "r2" or "zr": input = PadInput.RT; return true;
			case "leftstick" or "ls": input = PadInput.L3; return true;
			case "rightstick" or "rs": input = PadInput.R3; return true;
			case "options" or "menu" or "plus" or "+": input = PadInput.Start; return true;
			case "share" or "create" or "view" or "select" or "minus" or "-": input = PadInput.Back; return true;
			case "up": input = PadInput.DpadUp; return true;
			case "down": input = PadInput.DpadDown; return true;
			case "left": input = PadInput.DpadLeft; return true;
			case "right": input = PadInput.DpadRight; return true;
		}

		input = default;
		return n.Length != 0 && char.IsLetter(n[0]) &&
			Enum.TryParse(n, ignoreCase: true, out input) && Enum.IsDefined(input);
	}

	/// <summary>The guest's XInput bit for an input; 0 for a trigger.</summary>
	public static uint Bit(PadInput input) => Bits[(int)input];

	public static bool IsTrigger(PadInput input) => input is PadInput.LT or PadInput.RT;

	/// <summary>
	/// The input a glyph names: the glyph table's A/B/X/Y/LB/RB/LT/RT/Start/Back/LS/RS; null for
	/// DPad and anything else.
	/// </summary>
	public static PadInput? FromGlyphButton(string button) => button switch
	{
		"LS" => PadInput.L3,
		"RS" => PadInput.R3,
		"DPad" => null,
		_ => TryParse(button, out PadInput input) ? input : null,
	};

	private static IReadOnlyList<PadInput> Unpack(ushort mask)
	{
		var inputs = new List<PadInput>();
		for (int i = 0; i < Count; i++)
		{
			if ((mask & (1 << i)) != 0)
			{
				inputs.Add((PadInput)i);
			}
		}

		return inputs;
	}
}
