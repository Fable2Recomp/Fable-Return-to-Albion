using System.Collections.Generic;

using Refii.Ui;

namespace Refii.Titles.Fable3;

/// <summary>
/// THE SHIPPED PRESENTATION LUA, CUT: what Fable 3's scripts did to the layout, done by Godot from
/// the guest's own calls. A headless presentation runs no Lua; headless_presentation_hooks.cpp
/// states the two inputs the scripts had instead —
/// <list type="bullet">
/// <item>`@Lua.Param(pres, name, value)`: the Parameters element (`g_Param`), staged by
/// SetParameterString sub_82B3F470 and SetParameterFloat sub_82B3F3A8 (r3 name, f1 value, r5 pres).</item>
/// <item>`@Lua.Chunk(pres, text)`: the chunk the C++ passed the runner sub_82819D40.</item>
/// </list>
/// Of the 56 + 61 shipped chunks only the format-2 ones load (the undump sub_822AA278 rejects any
/// other format at 0x822AA31C) and only a few are attached to a .bgf; their layout writes are:
/// <list type="bullet">
/// <item>`minigamebarcontrol.lua` — the whole mini-game bar (<see cref="Fable3MiniGameBar"/>).</item>
/// <item>The layer-opacity chunks: `g_HUD:Enable()` / `g_HUD:Disable()` (sub_825C1C38 0x825C1C90,
/// the HUD update sub_822597B0 0x822598B8 / 0x82259900) run hud.lua's
/// `setAttribute(getElement("Scene.Layer"), "opacity", 100 / 0)`; the interaction mode runs the
/// literal `setAttribute(getElement('Scene.Layer'), 'opacity', 0)` on its hide (sub_825C6958
/// 0x825C6988, counter IM+12 = 3) and `… 100` from its tick while that counter runs (0x825C3BF4).</item>
/// <item>`widescreenadapter.lua` — static file data, applied by Fable3HudDocument.StateWidescreen.</item>
/// </list>
/// Everything else the attached scripts do is Lua state, an event or a write the C++ makes itself.
/// </summary>
public static class Fable3LuaContracts
{
	private sealed class Presentation
	{
		public Dictionary<string, Fable3LuaParameter> Parameters { get; } = new(System.StringComparer.Ordinal);
		public Fable3MiniGameBar Bar { get; } = new();
		public float? LayerOpacity { get; set; }
		public ulong LayerStamp { get; set; }
		public ulong BarStamp { get; set; }
	}

	private static readonly Dictionary<string, Presentation> Documents = new(System.StringComparer.Ordinal);

	/// <summary>Every chunk heard that no contract here takes, by text, once each (the census).</summary>
	public static readonly HashSet<string> Untaken = new(System.StringComparer.Ordinal);

	private static Presentation Of(string document)
	{
		if (!Documents.TryGetValue(document, out Presentation? presentation))
		{
			presentation = new Presentation();
			Documents[document] = presentation;
		}

		return presentation;
	}

	/// <summary>The bar of a document, or null when no chunk for it was heard.</summary>
	public static Fable3MiniGameBar? BarOf(string document) =>
		Documents.TryGetValue(document, out Presentation? presentation) ? presentation.Bar : null;

	/// <summary>The stamp of the bar's last write in a document, 0 when none.</summary>
	public static ulong BarStampOf(string document) =>
		Documents.TryGetValue(document, out Presentation? presentation) ? presentation.BarStamp : 0u;

	/// <summary>`@Lua.Param(pres, name, value)`.</summary>
	public static void Param(Engine_UiModel.GuiNativeCall call)
	{
		string document = Fable3Elements.DocumentOfKey(call.Argument(0).Text);
		Engine_UiModel.GuiNativeArgument value = call.Argument(2);
		Of(document).Parameters[call.Argument(1).Text] = value.Kind == Engine_UiModel.GuiNativeArgumentKind.Number
			? new Fable3LuaParameter(null, value.Number)
			: new Fable3LuaParameter(value.Text, null);
	}

	/// <summary>`@Lua.Chunk(pres, text)`: the contract the chunk names, run on the presentation's parameters.</summary>
	public static void Chunk(Engine_UiModel.GuiNativeCall call)
	{
		string document = Fable3Elements.DocumentOfKey(call.Argument(0).Text);
		string text = call.Argument(1).Text;
		Presentation presentation = Of(document);

		if (presentation.Bar.Run(text, presentation.Parameters))
		{
			presentation.BarStamp = Fable3Elements.NextStamp();
			return;
		}

		if (LayerOpacity(text) is { } opacity)
		{
			presentation.LayerOpacity = opacity;
			presentation.LayerStamp = Fable3Elements.NextStamp();
			return;
		}

		if (Untaken.Count < 64 && Untaken.Add(text))
		{
			Godot.GD.Print($"[fable3-lua] '{document}' chunk '{text}' carries no layout (Lua state or an event)");
		}
	}

	/// <summary>
	/// The `Scene.Layer` opacity a chunk sets: hud.lua's `g_HUD:Enable()` 100 / `g_HUD:Disable()` 0
	/// (scripts/gameface/hud/hud.lua, `Enable` / `Disable`), or the literal
	/// `setAttribute(getElement('Scene.Layer'), 'opacity', N)`. Null for any other chunk.
	/// </summary>
	public static float? LayerOpacity(string chunk)
	{
		string text = chunk.Trim();
		if (text == "g_HUD:Enable()")
		{
			return 100f;
		}

		if (text == "g_HUD:Disable()")
		{
			return 0f;
		}

		const string prefix = "setAttribute(getElement('Scene.Layer'), 'opacity',";
		if (text.StartsWith(prefix, System.StringComparison.Ordinal) && text.EndsWith(')'))
		{
			string value = text[prefix.Length..^1].Trim();
			if (float.TryParse(value, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float opacity))
			{
				return opacity;
			}
		}

		return null;
	}

	/// <summary>
	/// The statements the contracts make on a document: the layer's opacity, the bar's slide, the
	/// templates' opacity (their x and scale go through <see cref="Transforms"/>; a statement has
	/// no scale channel). The HitHighlights slide is by name, resolved by the caller.
	/// </summary>
	public static IEnumerable<Fable3HudStatements.Statement> StatementsFor(string document, System.Func<string, string, int?> slideNamed)
	{
		if (!Documents.TryGetValue(document, out Presentation? presentation))
		{
			yield break;
		}

		if (presentation.LayerOpacity is { } layer)
		{
			yield return new Fable3HudStatements.Statement(
				Fable3HudStatements.LayerSegment, null, layer, null, null, presentation.LayerStamp);
		}

		Fable3MiniGameBar bar = presentation.Bar;
		ulong stamp = presentation.BarStamp;
		if (stamp == 0u)
		{
			yield break;
		}

		if (bar.BarSlide is { } slide)
		{
			yield return new Fable3HudStatements.Statement(Fable3MiniGameBar.BarPath, slide, null, null, null, stamp);
		}

		if (bar.Background is { } background && slideNamed(Fable3MiniGameBar.HighlightsPath, background) is { } highlight)
		{
			yield return new Fable3HudStatements.Statement(
				Fable3MiniGameBar.HighlightsPath, highlight, null, null, null, stamp);
		}

		foreach (Fable3MiniGameBar.Element element in bar.Elements)
		{
			if (element.Copy == 0 && element.Opacity is { } opacity)
			{
				yield return new Fable3HudStatements.Statement(
					Fable3MiniGameBar.ButtonsPath + "." + Fable3MiniGameBar.TemplateName(element.Colour),
					null, opacity, null, null, stamp);
			}
		}
	}

	/// <summary>
	/// The transform channels the contracts write — (path, row index, value): the pip's x and each
	/// template's x and scale.x (row 0 position x, row 3 scale x, Fable3HudStatements.TransformWidth).
	/// </summary>
	public static IEnumerable<(string Path, int Channel, float Value)> Transforms(string document)
	{
		if (BarOf(document) is not { } bar)
		{
			yield break;
		}

		if (bar.PipX is { } pip)
		{
			yield return (Fable3MiniGameBar.PipPath, 0, pip);
		}

		foreach (Fable3MiniGameBar.Element element in bar.Elements)
		{
			if (element.Copy != 0)
			{
				continue;
			}

			string path = Fable3MiniGameBar.ButtonsPath + "." + Fable3MiniGameBar.TemplateName(element.Colour);
			if (element.X is { } x)
			{
				yield return (path, 0, x);
			}

			if (element.ScaleX is { } scale)
			{
				yield return (path, 3, scale);
			}
		}
	}

	/// <summary>A presentation load forgets what its Lua held (the script state lived in the presentation).</summary>
	public static void Forget(string document) => Documents.Remove(document);

	/// <summary>Every contract's state dropped (the tests' reset).</summary>
	public static void Reset()
	{
		Documents.Clear();
		Untaken.Clear();
	}
}
