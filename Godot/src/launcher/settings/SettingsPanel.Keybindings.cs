using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Game;
using Refii.Launcher.Core;

namespace Refii.Launcher.Core
{
	/// <summary>
	/// Pure-C# helper for parsing, formatting, and editing primary/secondary keybinding slots in
	/// <c>refii.toml</c> without touching Godot UI nodes.
	/// </summary>
	public static class KeybindingSlotHelper
	{
		/// <summary>Parses a stored comma-separated binding value into individual trimmed tokens.</summary>
		public static List<string> ParseTokens(string? rawValue, string defaultValue, bool isModBinding)
		{
			string trimmed = (rawValue ?? string.Empty).Trim().Trim('"');
			if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
			{
				return new List<string>();
			}

			if (trimmed.Length == 0)
			{
				if (isModBinding)
				{
					return new List<string>();
				}

				trimmed = defaultValue;
			}

			return trimmed
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(t => !string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
				.ToList();
		}

		/// <summary>
		/// Updates slot <paramref name="slotIndex"/> (0 = Primary, 1 = Secondary) with <paramref name="newToken"/>
		/// (or clears that slot when <paramref name="newToken"/> is null/empty) and returns the serialized
		/// <c>refii.toml</c> string.
		/// </summary>
		public static string UpdateSlot(
			string? currentRaw,
			string defaultValue,
			bool isModBinding,
			int slotIndex,
			string? newToken)
		{
			List<string> tokens = ParseTokens(currentRaw, defaultValue, isModBinding);
			string cleaned = (newToken ?? string.Empty).Trim();

			if (cleaned.Length == 0)
			{
				if (slotIndex >= 0 && slotIndex < tokens.Count)
				{
					tokens.RemoveAt(slotIndex);
				}
			}
			else
			{
				int targetIdx = Math.Max(0, slotIndex);
				if (targetIdx < tokens.Count)
				{
					tokens[targetIdx] = cleaned;
				}
				else
				{
					tokens.Add(cleaned);
					targetIdx = tokens.Count - 1;
				}

				// Deduplicate if the newly bound key was also in another slot
				for (int i = tokens.Count - 1; i >= 0; i--)
				{
					if (i != targetIdx && string.Equals(tokens[i], cleaned, StringComparison.OrdinalIgnoreCase))
					{
						tokens.RemoveAt(i);
						if (i < targetIdx)
						{
							targetIdx--;
						}
					}
				}
			}

			if (tokens.Count == 0)
			{
				return "none";
			}

			return string.Join(",", tokens);
		}

		/// <summary>Formats a raw <c>refii.toml</c> input token as a compact human-readable keycap label.</summary>
		public static string FormatToken(string? token)
		{
			if (string.IsNullOrWhiteSpace(token) || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
			{
				return "\u2014";
			}

			return token!.Trim() switch
			{
				"MouseLeft" or "Mouse1" => "Left Click",
				"MouseRight" or "Mouse2" => "Right Click",
				"MouseMiddle" or "Mouse3" => "Mid Click",
				"Mouse4" => "Mouse 4",
				"Mouse5" => "Mouse 5",
				"PageUp" or "Pageup" => "PgUp",
				"PageDown" or "Pagedown" => "PgDn",
				"BracketLeft" or "Bracketleft" => "[",
				"BracketRight" or "Bracketright" => "]",
				"BackSpace" or "Backspace" => "Backspace",
				"Escape" => "Esc",
				var other => other,
			};
		}

		/// <summary>Extracts the mod's declared default keys from a mod keybinding description if present.</summary>
		public static string? ExtractModDefaultHint(string description)
		{
			const string marker = "keeps the mod's own (";
			int start = description.IndexOf(marker, StringComparison.Ordinal);
			if (start < 0)
			{
				return null;
			}

			start += marker.Length;
			int end = description.IndexOf(')', start);
			return end > start ? description[start..end] : null;
		}
	}
}

namespace Refii.Launcher
{
	public partial class SettingsPanel
	{
		private sealed record ActiveKeyCapture(
			RuntimeCVarDefinition Definition,
			int SlotIndex,
			Button TargetButton,
			string PreviousCaption,
			bool IsModBinding);

		private ActiveKeyCapture? _activeCapture;

		private void CancelKeyCapture()
		{
			if (_activeCapture is not { } cap)
			{
				return;
			}

			_activeCapture = null;
			if (IsInstanceValid(cap.TargetButton))
			{
				cap.TargetButton.Text = cap.PreviousCaption;
				StyleKeySlotButton(cap.TargetButton, capturing: false, empty: cap.PreviousCaption == "\u2014");
			}
		}

		/// <inheritdoc />
		public override void _Input(InputEvent @event)
		{
			if (_activeCapture is not { } cap)
			{
				return;
			}

			if (@event is InputEventKey { Pressed: true, Echo: false } keyEvent)
			{
				GetViewport().SetInputAsHandled();
				Key code = keyEvent.Keycode != Key.None ? keyEvent.Keycode : keyEvent.PhysicalKeycode;
				if (code == Key.None)
				{
					return;
				}

				if (code == Key.Escape)
				{
					CancelKeyCapture();
					UpdateStatusText("Cancelled keybinding capture.");
					return;
				}

				if (code == Key.Delete)
				{
					ApplyCapturedSlot(cap, null);
					return;
				}

				string keyName = OS.GetKeycodeString(code);
				if (!string.IsNullOrEmpty(keyName))
				{
					ApplyCapturedSlot(cap, keyName);
				}
				return;
			}

			if (@event is InputEventMouseButton { Pressed: true } mouseEvent)
			{
				string? mouseToken = mouseEvent.ButtonIndex switch
				{
					MouseButton.Left => "MouseLeft",
					MouseButton.Right => "MouseRight",
					MouseButton.Middle => "MouseMiddle",
					MouseButton.Xbutton1 => "Mouse4",
					MouseButton.Xbutton2 => "Mouse5",
					_ => null,
				};
				if (mouseToken is null)
				{
					return;
				}

				GetViewport().SetInputAsHandled();
				ApplyCapturedSlot(cap, mouseToken);
			}
		}

		private void ApplyCapturedSlot(ActiveKeyCapture cap, string? newToken)
		{
			_activeCapture = null;
			string currentRaw = _model.Values.TryGetValue(cap.Definition.Name, out string? v)
				? v
				: cap.Definition.DefaultValue;

			string updated = KeybindingSlotHelper.UpdateSlot(
				currentRaw,
				cap.Definition.DefaultValue,
				cap.IsModBinding,
				cap.SlotIndex,
				newToken);

			_model.Values[cap.Definition.Name] = updated;
			_model.Save();
			SettingPresentation meta = DesktopSettingMetadata.Classify(cap.Definition);
			UpdateStatusText(newToken is null
				? $"Cleared slot {cap.SlotIndex + 1} for {meta.Label}."
				: $"Bound {meta.Label} to {KeybindingSlotHelper.FormatToken(newToken)}.");
			Rebuild();
		}

		private void BuildKeybindingsTab()
		{
			// Instruction + Reset All Keys header card
			var introRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			introRow.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
			_list.AddChild(introRow);

			Label hint = MakeLabel(
				"Click a Primary or Secondary slot to bind a key or mouse button. Esc cancels; Delete or right-click clears.");
			hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			hint.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
			hint.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
			introRow.AddChild(hint);

			var resetAllKeysBtn = new Button { Text = "Reset All Keys" };
			StyleCompactButton(resetAllKeysBtn);
			resetAllKeysBtn.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			resetAllKeysBtn.Pressed += () =>
			{
				CancelKeyCapture();
				_model.ResetTabToDefaults(SettingsTab.Keybindings);
				UpdateStatusText("Reset all keybindings to defaults.");
				Rebuild();
			};
			introRow.AddChild(resetAllKeysBtn);

			(var essential, var advanced) = _model.BuildTabSections(SettingsTab.Keybindings);
			foreach ((string section, List<RuntimeCVarDefinition> rows) in essential.Concat(advanced))
			{
				VBoxContainer card = BuildSectionCard(section);
				foreach (RuntimeCVarDefinition def in rows)
				{
					AddKeybindingRowForDefinition(card, def);
				}

				_list.AddChild(card);
			}
		}

		private void AddKeybindingRowForDefinition(VBoxContainer parent, RuntimeCVarDefinition definition)
		{
			SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
			bool isModBinding = definition.Category.StartsWith(RuntimeCVarsGodot.ControlsCategory + ": ", StringComparison.Ordinal);

			string stored = _model.Values.TryGetValue(definition.Name, out string? val)
				? val
				: definition.DefaultValue;

			GameInput.KeyAction? builtInAction = null;
			if (!isModBinding && definition.Name.StartsWith("key_", StringComparison.Ordinal))
			{
				string actionId = definition.Name[4..];
				foreach (GameInput.KeyAction ka in GameInput.KeyActions)
				{
					if (ka.Name == actionId)
					{
						builtInAction = ka;
						break;
					}
				}
			}

			string effectiveRaw = builtInAction is { } action
				? action.Bound(stored)
				: stored;

			bool isModified = builtInAction is { } kaCheck
				? !kaCheck.IsDefault(stored)
				: !string.IsNullOrEmpty(stored.Trim());

			List<string> tokens;
			if (isModBinding && string.IsNullOrEmpty(stored.Trim()))
			{
				string? modDefault = KeybindingSlotHelper.ExtractModDefaultHint(definition.Description);
				tokens = string.IsNullOrEmpty(modDefault)
					? new List<string>()
					: KeybindingSlotHelper.ParseTokens(modDefault, string.Empty, isModBinding: false);
			}
			else
			{
				tokens = KeybindingSlotHelper.ParseTokens(effectiveRaw, definition.DefaultValue, isModBinding);
			}

			string primaryCaption = tokens.Count > 0 ? KeybindingSlotHelper.FormatToken(tokens[0]) : "\u2014";
			string secondaryCaption = tokens.Count > 1 ? KeybindingSlotHelper.FormatToken(tokens[1]) : "\u2014";
			if (tokens.Count > 2)
			{
				secondaryCaption += $" (+{tokens.Count - 2})";
			}

			var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			row.AddThemeConstantOverride("separation", 5);
			parent.AddChild(row);

			Label nameLabel = MakeLabel(meta.Label);
			nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			nameLabel.VerticalAlignment = VerticalAlignment.Center;
			nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
			nameLabel.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 1));
			nameLabel.TooltipText = BuildTooltip(definition, meta);
			row.AddChild(nameLabel);

			if (MakeOwnerTitleBadge(definition.OwnerTitle) is { } ownerBadge)
			{
				row.AddChild(ownerBadge);
			}

			string defaultHint = isModBinding
				? (KeybindingSlotHelper.ExtractModDefaultHint(definition.Description) ?? "Mod Default")
				: definition.DefaultValue;
			Button resetBtn = BuildInlineResetButton(isModified, () =>
			{
				CancelKeyCapture();
				_model.Values[definition.Name] = definition.DefaultValue;
				_model.Save();
				UpdateStatusText($"Reset {meta.Label} to default ({defaultHint}).");
				Rebuild();
			}, defaultHint);
			row.AddChild(resetBtn);

			var slotGroup = new HBoxContainer
			{
				CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
			};
			slotGroup.AddThemeConstantOverride("separation", LauncherScale.Px(6));
			slotGroup.AddChild(BuildSlotButton(definition, 0, primaryCaption, isModBinding, meta.Label));
			slotGroup.AddChild(BuildSlotButton(definition, 1, secondaryCaption, isModBinding, meta.Label));
			row.AddChild(slotGroup);
		}

		private Button BuildSlotButton(
			RuntimeCVarDefinition definition,
			int slotIndex,
			string caption,
			bool isModBinding,
			string actionLabel)
		{
			bool empty = caption == "\u2014";
			var btn = new Button
			{
				Text = caption,
				ClipText = true,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				CustomMinimumSize = new Vector2(LauncherScale.Px(76), 0),
				TooltipText =
					$"{actionLabel} ({(slotIndex == 0 ? "Primary" : "Secondary")})\n" +
					"Left-click to rebind; right-click to clear slot.",
			};
			Apply(btn);
			btn.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
			StyleKeySlotButton(btn, capturing: false, empty: empty);

			btn.GuiInput += @event =>
			{
				if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } &&
				    _activeCapture is null)
				{
					btn.AcceptEvent();
					string currentRaw = _model.Values.TryGetValue(definition.Name, out string? v)
						? v
						: definition.DefaultValue;
					_model.Values[definition.Name] = KeybindingSlotHelper.UpdateSlot(
						currentRaw, definition.DefaultValue, isModBinding, slotIndex, null);
					_model.Save();
					UpdateStatusText($"Cleared {(slotIndex == 0 ? "primary" : "secondary")} binding for {actionLabel}.");
					Rebuild();
				}
			};

			btn.Pressed += () =>
			{
				if (_activeCapture is { } existing && ReferenceEquals(existing.TargetButton, btn))
				{
					return;
				}

				CancelKeyCapture();
				btn.Text = "Press key...";
				StyleKeySlotButton(btn, capturing: true, empty: false);
				_activeCapture = new ActiveKeyCapture(definition, slotIndex, btn, caption, isModBinding);
				UpdateStatusText($"Listening for {actionLabel} — press any key or mouse button (Esc cancels, Delete clears).");
			};

			return btn;
		}

		private void StyleKeySlotButton(Button button, bool capturing, bool empty)
		{
			var normal = new StyleBoxFlat
			{
				BgColor = capturing
					? LauncherTokens.Emerald
					: empty
						? new Color(LauncherTokens.Parchment, 0.55f)
						: LauncherTokens.ParchmentLit,
				BorderColor = capturing
					? LauncherTokens.GoldBright
					: new Color(LauncherTokens.Gold, empty ? 0.30f : 0.60f),
				CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
				CornerRadiusBottomRight = LauncherTokens.RadiusSm,
				CornerRadiusTopLeft = LauncherTokens.RadiusSm,
				CornerRadiusTopRight = LauncherTokens.RadiusSm,
				ContentMarginLeft = 6,
				ContentMarginRight = 6,
				ContentMarginTop = 3,
				ContentMarginBottom = 3,
			};
			normal.SetBorderWidthAll(1);

			var hover = (StyleBoxFlat)normal.Duplicate();
			if (!capturing)
			{
				hover.BorderColor = LauncherTokens.Gold;
				hover.BgColor = LauncherTokens.ParchmentRaised;
			}

			button.AddThemeStyleboxOverride("normal", normal);
			button.AddThemeStyleboxOverride("hover", hover);
			button.AddThemeStyleboxOverride("pressed", hover);
			button.AddThemeStyleboxOverride("focus", hover);

			Color fg = capturing
				? LauncherTokens.Cream
				: empty
					? LauncherTokens.Sepia
					: LauncherTokens.Walnut;
			button.AddThemeColorOverride("font_color", fg);
			button.AddThemeColorOverride("font_hover_color", capturing ? Colors.White : LauncherTokens.Oak);
			button.AddThemeColorOverride("font_pressed_color", fg);
			button.AddThemeColorOverride("font_focus_color", fg);
		}
	}
}
