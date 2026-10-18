using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// THE SECONDARY PICK on the spell selectors — a mod's. A mod ships <c>ui/spellwheel.json</c>:
/// <code>
/// { "secondary": {
///     "setting": "spell_weaving.partner_{slot}", "settingPlayerTwo": "spell_weaving.partner2_{slot}",
///     "value": "mod:spell_weaving.partner_active_{slot}", "valuePlayerTwo": "mod:spell_weaving.partner2_active_{slot}",
///     "chosen": "mod:spell_weaving.partner_chosen_{slot}", "chosenPlayerTwo": "...",
///     "empty": "art/gui/hud/spell_weaving_slot.tex",
///     "scale": 0.55, "exclude": [2, 7],
///     "unlocked": "mod:spell_weaving.level",
///     "apply": "SPELL_WEAVING_SET_PARTNER", "remove": "SPELL_WEAVING_REMOVE_PARTNER" } }
/// </code>
/// On both selectors (the quick wheel and the abilities blade) X on the highlighted will writes
/// that will's type to the setting and Y writes -1. A <c>{slot}</c> in a key is the highlighted
/// slot's number, 1..5, so a mod may keep one pick per slot; without it one pick serves every slot.
///
/// <c>unlocked</c> names the mod value that says the player HAS the pick (positive); until then
/// the spell page says nothing about it. <c>apply</c> and <c>remove</c> are the Babel tags of the
/// two PROMPTS the abilities blade shows once it is unlocked — an X row and a Y row in the page's
/// own prompt style (glyph, then the words, as the book's "Back"), on the free left side of the
/// page's prompt band, beside the authored A "Copy Spell to Slots Above" and B "Done".
///
/// WHAT IS DRAWN: a small frame in the lower-right corner of every ASSIGNED slot's icon, always
/// there once the mod declares a pick, so the player can see the slot takes one. Its state:
/// <list type="bullet">
/// <item>empty — the <c>empty</c> art alone, faint: the slot can take a pick and has none;</item>
/// <item>chosen — the will the mod states in <c>value</c>, at full strength;</item>
/// <item>chosen but not in effect — the will in <c>chosen</c> when <c>value</c> is -1, at half
/// strength (unlearned, or beyond the mod's own gate);</item>
/// <item>disabled — the frame greyed down when the slot's own will is in <c>exclude</c>; it comes
/// back the moment a will the pick allows is assigned there.</item>
/// </list>
/// The host knows nothing of what the pick means; the mod does.
/// </summary>
public sealed partial class Engine_UiSpellAssign
{
	public const string SecondaryPath = "ui/spellwheel.json";

	public sealed record SecondaryBinding(
		string Mod, string[] Settings, string[] Values, string[] Chosen, string Empty, float Scale,
		HashSet<int> Excluded, string Unlocked, string Apply, string Remove);

	private const float EmptyOpacity = 0.45f;
	private const float DisabledOpacity = 0.22f;
	private const float PendingOpacity = 0.5f;

	private static SecondaryBinding? _secondary;
	private static bool _secondaryLoaded;

	/// <summary>The binding the enabled mods state, the last in load order winning; null when none.</summary>
	public static SecondaryBinding? Secondary
	{
		get
		{
			if (!_secondaryLoaded)
			{
				_secondary = LoadSecondary();
				_secondaryLoaded = true;
			}

			return _secondary;
		}
	}

	/// <summary>Drops the loaded binding; the next read loads it again.</summary>
	public static void ReloadSecondary() => _secondaryLoaded = false;

	/// <summary>Which player's hero this copy shows: 1, or 2 for the joiner's wheel.</summary>
	public int Player { get; set; } = 1;

	/// <summary>What a press on the selectors asks of the partner: X applies the highlighted will, Y removes the partner.</summary>
	public enum SecondaryAction
	{
		None,
		Apply,
		Remove,
	}

	public static SecondaryAction SecondaryPressed(bool flip, bool equip) =>
		Secondary is null ? SecondaryAction.None : equip ? SecondaryAction.Apply : flip ? SecondaryAction.Remove : SecondaryAction.None;

	/// <summary>
	/// Whether the player has the pick: the mod value <c>unlocked</c> names is positive. A binding
	/// that names none is unlocked from the start.
	/// </summary>
	public static bool SecondaryUnlocked() =>
		Secondary is { } binding &&
		(binding.Unlocked.Length == 0 ||
		 (HostBridge.TryGetModValue(binding.Unlocked, out float level) && level > 0f));

	/// <summary>The prompt rows this copy added to the book's blade, X then Y, glyphs and words.</summary>
	private readonly List<Node3D> _prompts = new();

	/// <summary>
	/// THE PROMPTS, in the page's own band. `abilitiesscreen.bgf` authors `Blade.Buttons` at
	/// (80, -265) with the A pair on its row y 3 and the B pair on row y 35, all to the right of
	/// x 218; the left of the band, from the page margin at x 24, is empty. The X row goes on the
	/// B row and the Y row on the A row, laid out as the book's own glyph-first `Back` prompt
	/// (glyph at x, words left-aligned 21 units on). The glyph is the authored B quad duplicated
	/// and re-cut to the X or Y column of the `abyx` atlas (columns A, B, Y, X), with its keycap
	/// for the keyboard; the words are a label in the page's prompt colour and size. Built once
	/// per Buttons node and adopted on a rebuild, so a blade that comes and goes never doubles
	/// them. Hidden until <see cref="ShowPrompts"/> says otherwise.
	/// </summary>
	public void EnsurePrompts(Engine_UiOwnedBook book)
	{
		if (Secondary is not { } binding || Book is null)
		{
			return;
		}

		if (_prompts.Count > 0 && GodotObject.IsInstanceValid(_prompts[0]))
		{
			return;
		}

		_prompts.Clear();
		if (book.Find("Blade") is not { } blade ||
			Engine_UiTheme.FindByName(blade, "Buttons") is not Node3D buttons ||
			Engine_UiTheme.FindByName(buttons, "icon_button_b") is not MeshInstance3D glyph ||
			Engine_UiTheme.FindByName(buttons, "Done") is not MeshInstance3D done)
		{
			GD.PushWarning("[spell-wheel] the blade's prompt band is not in this book; no partner prompts");
			return;
		}

		// A rebuild of the widget finds the rows a previous copy left on the same Buttons node.
		foreach (string name in new[] { "icon_button_x", "SetPartner", "icon_button_y", "RemovePartner" })
		{
			if (Engine_UiTheme.FindByName(buttons, name) is Node3D kept)
			{
				_prompts.Add(kept);
			}
		}

		if (_prompts.Count == 4)
		{
			return;
		}

		foreach (Node3D stale in _prompts)
		{
			stale.QueueFree();
		}

		_prompts.Clear();
		int size = done.HasMeta("text_size") ? (int)done.GetMeta("text_size") : 18;
		Color colour = Engine_UiSceneBuilder.TextColour(done);
		float rowStep = 32f;  // the authored A row (y 3) to the B row (y 35)
		foreach ((string button, int column, string tag, string name, float y) in new[]
			{
				("X", 3, binding.Apply, "SetPartner", done.Position.Y),
				("Y", 2, binding.Remove, "RemovePartner", done.Position.Y - rowStep),
			})
		{
			var quad = (MeshInstance3D)glyph.Duplicate();
			quad.Name = "icon_button_" + button.ToLowerInvariant();
			quad.Position = new Vector3(PromptGlyphX, y + (glyph.Position.Y - done.Position.Y), glyph.Position.Z);
			if (quad.MaterialOverride is ShaderMaterial shared)
			{
				var material = (ShaderMaterial)shared.Duplicate();
				Vector2 offset = material.GetShaderParameter("uv1_offset").AsVector2();
				float inset = offset.X - (Mathf.Floor((offset.X * 4f) + 0.01f) * 0.25f);
				material.SetShaderParameter("uv1_offset", new Vector2((column * 0.25f) + inset, offset.Y));
				material.SetShaderParameter("keycap_tex", Engine_UiKeyGlyphs.KeycapFor(button));
				quad.MaterialOverride = material;
			}

			quad.Visible = false;
			buttons.AddChild(quad);
			_prompts.Add(quad);

			MeshInstance3D words = Book.Label(Book.Resolve(tag), size, colour);
			words.Name = name;
			words.Position = new Vector3(PromptGlyphX + PromptWordsOffset, y, done.Position.Z);
			words.Visible = false;
			buttons.AddChild(words);
			_prompts.Add(words);
		}
	}

	/// <summary>The glyph column, in Buttons' own frame: page x 44, twenty units in from the margin.</summary>
	private const float PromptGlyphX = -36f;

	/// <summary>Words start 21 units right of the glyph's centre — the book's `Back` prompt spacing.</summary>
	private const float PromptWordsOffset = 21f;

	/// <summary>Shows or hides the partner prompts — up while the blade is known and the pick unlocked.</summary>
	public void ShowPrompts(bool shown)
	{
		foreach (Node3D node in _prompts)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				node.Visible = shown;
			}
		}
	}

	/// <summary>Y on a slot: its partner is removed, and its frame goes back to empty.</summary>
	public static void RemoveSecondary(int player, int slot)
	{
		if (Secondary is not { } binding || slot < 0 || slot >= Engine_UiModel.SpellAssignState.Slots)
		{
			return;
		}

		string key = KeyFor(binding.Settings[player == 2 && binding.Settings.Length > 1 ? 1 : 0], slot);
		GD.Print($"[spell-wheel] partner removed: player {player} slot {slot + 1} (was {SecondaryChosen(player, slot)})");
		Refii.Mods.ModSettings.Set(key, -1f);
	}

	/// <summary>A key with its <c>{slot}</c> filled in — the slot's number, 1..5.</summary>
	private static string KeyFor(string template, int slot) =>
		template.Replace("{slot}", (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static int WillValue(string[] keys, int player, int slot)
	{
		if (keys.Length == 0)
		{
			return -1;
		}

		string key = KeyFor(keys[player == 2 && keys.Length > 1 ? 1 : 0], slot);
		return HostBridge.TryGetModValue(key, out float value) && value >= 0f && value < Engine_UiModel.SpellAssignState.Spells
			? (int)value
			: -1;
	}

	/// <summary>The will the mod states as IN EFFECT for <paramref name="player"/> on <paramref name="slot"/>, or -1.</summary>
	public static int SecondaryWill(int player, int slot) =>
		Secondary is { } binding ? WillValue(binding.Values, player, slot) : -1;

	/// <summary>The will the mod states as CHOSEN there, in effect or not; falls back to the one in effect.</summary>
	public static int SecondaryChosen(int player, int slot)
	{
		if (Secondary is not { } binding)
		{
			return -1;
		}

		int chosen = WillValue(binding.Chosen, player, slot);
		return chosen >= 0 ? chosen : WillValue(binding.Values, player, slot);
	}

	/// <summary>
	/// The pick on a slot: the will under the cursor becomes the slot's partner. The slot's OWN
	/// spell (<paramref name="primary"/>) picked as partner means "no partner" — it can never be
	/// one — and the will already chosen is kept, never toggled off: a second press on the same
	/// will used to clear it, which read as the pick not taking.
	/// </summary>
	public static void ChooseSecondary(int player, int slot, int? will, int primary = -1)
	{
		if (Secondary is not { } binding || will is not { } chosen || chosen < 0 ||
			slot < 0 || slot >= Engine_UiModel.SpellAssignState.Slots)
		{
			return;
		}

		string key = KeyFor(binding.Settings[player == 2 && binding.Settings.Length > 1 ? 1 : 0], slot);
		int current = SecondaryChosen(player, slot);
		if (chosen == primary)
		{
			GD.Print($"[spell-wheel] partner pick: player {player} slot {slot + 1}: will {chosen} is the slot's own spell; kept {current} (Y removes)");
			return;
		}

		if (binding.Excluded.Contains(chosen))
		{
			GD.Print($"[spell-wheel] partner pick: player {player} slot {slot + 1}: will {chosen} cannot be a partner; kept {current}");
			return;
		}

		if (chosen == current)
		{
			GD.Print($"[spell-wheel] partner pick: player {player} slot {slot + 1}: will {chosen} already the partner; kept");
			return;
		}

		GD.Print($"[spell-wheel] partner pick: player {player} slot {slot + 1}: will {chosen} (was {current})");
		Refii.Mods.ModSettings.Set(key, chosen);
	}

	/// <summary>The will under the carousel's centre on this copy's own cursor — the abilities blade's pick.</summary>
	public int? CursorWill()
	{
		List<int> choices = _state.Choices(Row_);
		return _cell >= 0 && _cell < choices.Count ? choices[_cell] : null;
	}

	/// <summary>The small frame under each assigned slot's icon, in one of its four states.</summary>
	private void PaintSecondary()
	{
		if (Secondary is not { } binding)
		{
			return;
		}

		for (int slot = 0; slot < _rows.Count; ++slot)
		{
			Row row = _rows[slot];
			bool assigned = row.Shown >= 0 && row.Icon is not null && GodotObject.IsInstanceValid(row.Icon);
			if (!assigned)
			{
				if (row.Mini is not null && GodotObject.IsInstanceValid(row.Mini))
				{
					row.Mini.Visible = false;
				}

				continue;
			}

			MeshInstance3D? mini = EnsureMini(row, binding.Scale);
			if (mini is null)
			{
				continue;
			}

			// The state, as one number the row remembers: -3 disabled, -2 empty, will + 8 pending,
			// will in effect.
			int active = SecondaryWill(Player, slot);
			int chosen = SecondaryChosen(Player, slot);
			int state = binding.Excluded.Contains(row.Shown) ? -3
				: active >= 0 ? active
				: chosen >= 0 ? chosen + 8
				: -2;
			if (state != row.MiniShown)
			{
				row.MiniShown = state;
				switch (state)
				{
					case -3:
						PaintFrame(mini, binding.Empty, DisabledOpacity);
						break;
					case -2:
						PaintFrame(mini, binding.Empty, EmptyOpacity);
						break;
					case >= 8:
						Paint(mini, state - 8);
						Engine_UiSceneBuilder.SetQuadOpacity(mini, PendingOpacity);
						break;
					default:
						Paint(mini, state);
						break;
				}
			}

			mini.Visible = true;
		}
	}

	/// <summary>The frame art, the mod's own <c>.tex</c>, at an opacity; the quad is hidden when the art is missing.</summary>
	private void PaintFrame(MeshInstance3D node, string texture, float opacity)
	{
		Texture2D? picture = texture.Length == 0 ? null : ResolveTexture?.Invoke(texture);
		if (picture is null && texture.Length != 0)
		{
			picture = ResolveTexture?.Invoke(texture.Replace('/', '\\'));
		}

		if (picture is null || node.MaterialOverride is not ShaderMaterial material)
		{
			node.Visible = false;
			return;
		}

		node.Visible = true;
		material.SetShaderParameter("diffuse_tex", picture);
		Engine_UiSceneBuilder.SetQuadOpacity(node, opacity);
	}

	/// <summary>A copy of the slot's icon quad, under it, at the lower-right corner, at <paramref name="scale"/>.</summary>
	private static MeshInstance3D? EnsureMini(Row row, float scale)
	{
		if (row.Mini is not null && GodotObject.IsInstanceValid(row.Mini))
		{
			return row.Mini;
		}

		if (row.Icon is not { } icon || icon.Mesh is null)
		{
			return null;
		}

		var mini = new MeshInstance3D
		{
			Name = "Secondary",
			Mesh = icon.Mesh,
			MaterialOverride = icon.MaterialOverride?.Duplicate() as Material,
		};
		Vector3 size = icon.Mesh.GetAabb().Size;
		// Its centre sits so the small quad overhangs the big one's corner by a little.
		float dx = (size.X * 0.5f) - (size.X * scale * 0.35f);
		float dy = (size.Y * 0.5f) - (size.Y * scale * 0.35f);
		mini.Position = new Vector3(dx, -dy, 1f);
		mini.Scale = new Vector3(scale, scale, 1f);
		icon.AddChild(mini);
		row.Mini = mini;
		row.MiniShown = int.MinValue;
		return mini;
	}

	private static SecondaryBinding? LoadSecondary()
	{
		SecondaryBinding? found = null;
		foreach ((string mod, string text) in Refii.Mods.ModEntries.ReadEnabledText(SecondaryPath))
		{
			Variant parsed = Json.ParseString(text);
			if (parsed.VariantType != Variant.Type.Dictionary ||
				!parsed.AsGodotDictionary().TryGetValue("secondary", out Variant value) ||
				value.VariantType != Variant.Type.Dictionary)
			{
				GD.PushWarning($"[spell-wheel] {mod}:{SecondaryPath} has no \"secondary\" object; ignored");
				continue;
			}

			var record = value.AsGodotDictionary();
			string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
			string setting = Text("setting");
			string stated = Text("value");
			if (setting.Length == 0 || stated.Length == 0)
			{
				GD.PushWarning($"[spell-wheel] {mod}: secondary needs setting and value; ignored");
				continue;
			}

			string settingTwo = Text("settingPlayerTwo");
			string statedTwo = Text("valuePlayerTwo");
			string chosen = Text("chosen");
			string chosenTwo = Text("chosenPlayerTwo");
			float scale = record.TryGetValue("scale", out Variant s) && s.VariantType is Variant.Type.Float or Variant.Type.Int
				? Mathf.Clamp((float)s.AsDouble(), 0.2f, 1f)
				: 0.55f;
			static string Bare(string key) => key.StartsWith("mod:", System.StringComparison.Ordinal) ? key[4..] : key;
			var excluded = new HashSet<int>();
			if (record.TryGetValue("exclude", out Variant exclude) && exclude.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in exclude.AsGodotArray())
				{
					excluded.Add(item.AsInt32());
				}
			}

			found = new SecondaryBinding(
				mod,
				settingTwo.Length == 0 ? new[] { setting } : new[] { setting, settingTwo },
				statedTwo.Length == 0 ? new[] { Bare(stated) } : new[] { Bare(stated), Bare(statedTwo) },
				chosen.Length == 0 ? System.Array.Empty<string>()
					: chosenTwo.Length == 0 ? new[] { Bare(chosen) } : new[] { Bare(chosen), Bare(chosenTwo) },
				Text("empty"), scale, excluded, Bare(Text("unlocked")), Text("apply"), Text("remove"));
		}

		if (found is not null)
		{
			GD.Print($"[spell-wheel] secondary pick from {found.Mod}: X applies, Y removes" +
				(found.Unlocked.Length == 0 ? string.Empty : $", unlocked by {found.Unlocked}"));
		}

		return found;
	}
}
