using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Renders a Fable 2 Gameface screen so it can be eyeballed against the original game.
///
/// This is a development harness, not part of the game. Standing the screen up is
/// <see cref="Engine_UiPresentationHost"/>'s job — the same code the running game's front
/// end goes through, so what is eyeballed here is what ships. What this class adds is only
/// the things a harness needs and a game must not have: cycling every presentation with the
/// arrow keys, the <c>REFII_UI_*</c> dumps, and screenshot-and-quit.
///
/// The keyboard is the reason the split exists: a game that reached its front end by
/// instantiating this class would change which of the 52 screens was up on a direction press.
/// </summary>
public partial class UiScreenViewer : Engine_UiPresentationHost
{
	private Label? _status;

	public override void _Ready()
	{
		_status = GetNodeOrNull<Label>("%Status");

		// The viewer's historical default. The game sets its own start screen explicitly.
		if (StartScreen.Length == 0)
			StartScreen = "menubox";

		base._Ready();
	}

	/// <summary>
	/// Applies the environment overrides, then shows the screen and runs the probes.
	///
	/// REFII_UI_SCREEN picks the screen without editing the scene, so a capture run can
	/// target any of the 52.
	/// </summary>
	protected override void OnLoaded()
	{
		DumpFontAtlasIfRequested();

		if (int.TryParse(System.Environment.GetEnvironmentVariable("REFII_UI_SCENE_SLIDE"),
				out int wantSlide))
			SceneSlide = wantSlide;

		if (System.Environment.GetEnvironmentVariable("REFII_UI_LIST") == "1")
			GD.Print($"[ui] screens: {string.Join(", ", ScreenNames)}");

		if (System.Environment.GetEnvironmentVariable("REFII_UI_SCREEN") is { } want)
			StartScreen = want;

		base.OnLoaded();

		DumpElementCountsIfRequested();
		DumpAttributesIfRequested();
		DumpBindingsIfRequested();
		DumpMenuIfRequested();
		DumpTreeIfRequested();
		CaptureIfRequested();
	}

	protected override void OnScreenShown(
		Engine_UiScreen screen, Engine_UiSceneBuilder builder, Camera3D camera)
	{
		DriveValuesIfRequested(screen, builder);
		DumpWidescreenIfRequested(screen, builder);
		DumpSlides(screen);
		DumpQuads(screen);
		DumpSliceGeometry();
		DumpTextureIfRequested();
		DumpTextureFailures();

		Report($"[{CurrentIndex + 1}/{ScreenNames.Count}] {screen.Name}   " +
			$"{screen.CanvasWidth}x{screen.CanvasHeight}\n" +
			$"quads {builder.QuadsBuilt} (degenerate {builder.DegenerateQuads})   " +
			$"textures {builder.TexturesResolved}   " +
			$"masked {builder.MasksApplied}   " +
			$"detail {builder.DetailQuads}" +
			(builder.DetailMapsMissing > 0 ? $" ({builder.DetailMapsMissing} MISSING)" : "") +
			"   " +
			$"text {builder.TextsBuilt} ({builder.TextGlyphsBuilt} glyphs)   " +
			$"camera {camera.Name}\n" +
			"left/right: change screen");
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (ScreenNames.Count == 0 || @event is not InputEventKey { Pressed: true } key)
			return;

		int delta = key.Keycode switch
		{
			Key.Right or Key.Down => 1,
			Key.Left or Key.Up => -1,
			_ => 0,
		};
		if (delta == 0)
			return;

		Show((CurrentIndex + delta + ScreenNames.Count) % ScreenNames.Count);
		GetViewport().SetInputAsHandled();
	}

	/// <summary>
	/// With <c>REFII_UI_ELEMENTS=1</c>, prints how many elements the shown screen carries and
	/// how many of them are spatial.
	///
	/// Unique hashes against the total is the number that matters: they are far apart on the
	/// title screen, which is why the live state is keyed by ordinal and not by name hash.
	/// </summary>
	private void DumpElementCountsIfRequested()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_ELEMENTS") != "1" ||
			CurrentScreen is not { } probe)
			return;

		var hashes = new HashSet<uint>();
		int spatial = 0;
		foreach (BgfElement e in probe.Document.Elements)
		{
			hashes.Add(e.NameHash);
			if (Engine_UiScreen.IsSpatial(e.Type)) ++spatial;
		}

		GD.Print($"[ui] elements={probe.Document.Elements.Length} " +
			$"unique-hashes={hashes.Count} spatial={spatial} " +
			$"slides={probe.Document.Slides.Length}");
	}

	/// <summary>
	/// With <c>REFII_UI_ATTRS=&lt;name&gt;</c>, prints every element carrying that attribute and
	/// the value it carries. <c>REFII_UI_ATTRS=*</c> prints every attribute of every element.
	///
	/// How a live value finds its widget: the in-game HUD has no layout script and no table on
	/// our side naming the health bar. An element carries <c>MeterName = "PlayerHealth"</c> and
	/// the runtime registers it under a hash of that string, so the binding is authored in the
	/// file and this reads it out.
	///
	/// An attribute this dump names but <see cref="Engine_BgfAttributeNames"/> does not know
	/// still resolves, because the lookup is a forward hash of the name asked for rather than a
	/// reverse lookup of the id.
	/// </summary>
	private void DumpAttributesIfRequested()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_ATTRS") is not { } want ||
			want.Length == 0 || CurrentScreen is not { } probe)
			return;

		Engine_BgfDocument doc = probe.Document;
		bool all = want == "*";
		uint id = all ? 0u : Engine_GameHash.AttributeId(want);
		int found = 0;

		for (int i = 0; i < doc.Elements.Length; i++)
		{
			BgfElement e = doc.Elements[i];
			foreach (BgfAttribute a in e.Attributes)
			{
				if (!all && a.Id != id)
					continue;

				// The string form is the one that matters for a binding — a meter name is a
				// string index into the file's own table, and printing the raw index would say
				// nothing about which value the element draws.
				string value = a.Type switch
				{
					BgfValueType.String => $"\"{doc.GetString(a.AsStringIndex())}\"",
					BgfValueType.Float => a.AsFloat().ToString("0.####"),
					BgfValueType.Bool => a.AsBool().ToString(),
					_ => a.AsInt().ToString(),
				};

				GD.Print($"[ui-attr] element {i,5} {probe.Nodes[i].Name,-32} " +
					$"{Engine_BgfAttributeNames.Describe(a.Id),-18} {value}");
				++found;
			}
		}

		GD.Print($"[ui-attr] '{want}' matched {found} attribute(s) across " +
			$"{doc.Elements.Length} elements");
	}

	/// <summary>
	/// Drives the screen's live values with synthetic numbers, so the in-game UI can be checked
	/// without a guest.
	///
	/// Everything else about the HUD is checkable from a log — the bindings resolve, the hashes
	/// match, the map walks — and none of that says the fill looks right. The guest does not set a scale at all; it scrubs the
	/// element's authored animation with a percentage, and <see cref="Engine_UiLiveValues"/>
	/// approximates that by scaling x from the left pivot. Whether that approximation reads as
	/// a half-full health bar is a question only a picture answers, and with a live guest it is
	/// tangled up with whether the guest published anything at all.
	///
	/// <c>REFII_UI_METERS=50</c> puts every bound meter at 50%. <c>REFII_UI_METERS=PlayerHealth=25,
	/// PlayerHealthMax=80</c> names them individually — which is the form that catches a bar
	/// driven by the wrong neighbour, because the two health bars sit on top of each other and
	/// look identical when both are given the same value.
	///
	/// <c>REFII_UI_TEXTS=1</c> writes each bound text field's own name into it, so a field that
	/// resolves but never draws is told apart from one that was never bound.
	/// </summary>
	private void DriveValuesIfRequested(Engine_UiScreen screen, Engine_UiSceneBuilder builder)
	{
		string? meterSpec = System.Environment.GetEnvironmentVariable("REFII_UI_METERS");
		bool wantTexts = System.Environment.GetEnvironmentVariable("REFII_UI_TEXTS") == "1";
		if (meterSpec is null && !wantTexts)
			return;

		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		if (bindings.All.Count == 0)
		{
			GD.Print($"[ui-drive] '{screen.Name}' carries no live-value bindings — nothing to drive");
			return;
		}

		var values = new Engine_UiLiveValues { Theme = Theme };
		values.Bind(screen.Document, bindings, builder.ByOrdinal);

		var meters = new Dictionary<uint, float>();
		if (meterSpec is not null)
		{
			// A bare number means "every meter", which is the quick look. Named pairs are the
			// careful one — see the remarks.
			if (float.TryParse(meterSpec, out float all))
			{
				foreach ((string _, uint hash) in bindings.Names(Engine_UiBindings.Kind.Meter))
					meters[hash] = all;
			}
			else
			{
				foreach (string pair in meterSpec.Split(',', StringSplitOptions.RemoveEmptyEntries))
				{
					string[] halves = pair.Split('=', 2);
					if (halves.Length == 2 && float.TryParse(halves[1], out float percent))
						meters[Engine_GameHash.Fnv1(halves[0].Trim())] = percent;
					else
						GD.PushWarning($"[ui-drive] cannot read '{pair}' as name=percent");
				}
			}
		}

		var texts = new Dictionary<uint, string>();
		if (wantTexts)
		{
			foreach ((string name, uint hash) in bindings.Names(Engine_UiBindings.Kind.Text))
				texts[hash] = name;
		}

		values.Apply(meters, texts);
		GD.Print($"[ui-drive] {values.Describe(meters)}");

		// Where each driven bar ended up, in world space. A fill that scaled correctly but sits
		// behind an opaque frame, or off the canvas, looks like one that never moved.
		foreach ((string name, uint hash, Node3D? bound, float full) in values.Bars)
		{
			if (!meters.ContainsKey(hash) || bound is not { } fill)
				continue;

			// The fill's name and its authored full extent catch a meter driven the wrong way: a
			// fill named something other than `BarMiddle` means the slide search found it, and a
			// `full` that is not 1 means the artist's own limit is honoured.
			//
			// Basis column lengths, never `.Scale` — Godot's scale getter multiplies by
			// `SIGN(determinant)`, and SIGN(0) is 0, so a singular basis reports (0,0,0) on every
			// axis and hides which one actually went to zero. That is exactly how a bar that
			// drew nothing once reported a perfect fill percentage.
			Basis local = fill.Transform.Basis;
			GD.Print($"[ui-drive]   {name,-22} {meters[hash],6:0.#}%  fill='{fill.Name}' " +
				$"full={full:0.###}  cols=({local.X.Length():0.###}, {local.Y.Length():0.###}, " +
				$"{local.Z.Length():0.###}) det={local.Determinant():0.####}  " +
				$"visible={fill.IsVisibleInTree()}  world={fill.GlobalPosition}");
		}
	}

	/// <summary>
	/// With <c>REFII_UI_WIDE=1</c>, applies the game's WIDE layout and reports what moved.
	///
	/// The game ships two layouts per element and the authored one is 4:3, so a screen built
	/// straight from the file is in the wrong arrangement on a 16:9 display — the HUD's ABXY
	/// prompts land in the middle of the screen instead of a corner. This prints each override
	/// with the position BEFORE and AFTER, which is the only form that distinguishes "the wide
	/// layout was applied" from "the wide layout happened to match".
	/// </summary>
	private void DumpWidescreenIfRequested(Engine_UiScreen screen, Engine_UiSceneBuilder builder)
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_WIDE") != "1")
			return;

		Engine_UiWidescreen wide = Engine_UiWidescreen.Read(screen.Document);
		GD.Print($"[ui-wide] {wide.Describe()}");

		var before = new Dictionary<int, Vector3>();
		foreach (Engine_UiWidescreen.Override entry in wide.Overrides)
		{
			if (entry.ParentOrdinal < builder.ByOrdinal.Length &&
				builder.ByOrdinal[entry.ParentOrdinal] is { } node)
				before[entry.ParentOrdinal] = node.Position;
		}

		int moved = wide.Apply(builder);
		GD.Print($"[ui-wide] applied to {moved} element(s)");

		// The three NAMED fixups too, because they are the only hardcoded element PATHS in the
		// widescreen story and a rename in the file would otherwise fail invisibly.
		if (Built is not null)
		{
			Engine_UiWidescreen.ApplyNamedFixups(Built, out string fixups);
			GD.Print(fixups.Length > 0
				? $"[ui-wide] {fixups}"
				: "[ui-wide] this screen carries none of hudpositioningandscaling.lua's " +
				  "named fixups, which is expected for anything but the HUD");
		}

		foreach (Engine_UiWidescreen.Override entry in wide.Overrides)
		{
			if (entry.ParentOrdinal >= builder.ByOrdinal.Length ||
				builder.ByOrdinal[entry.ParentOrdinal] is not { } node ||
				!before.TryGetValue(entry.ParentOrdinal, out Vector3 was))
				continue;

			GD.Print($"[ui-wide]   {node.Name,-24} {was} -> {node.Position}" +
				$"{(entry.ScaleX is { } s ? $"  scale.x={s:0.###}" : string.Empty)}");
		}
	}

	/// <summary>
	/// With <c>REFII_UI_TREE=&lt;name&gt;</c>, prints that element's subtree as the FILE authored it.
	///
	/// The gap this fills sits between the two dumps either side of it. <c>REFII_UI_DEBUG</c>
	/// gives world extents, which say what a quad ended up as; <c>REFII_UI_QUADS</c> gives the
	/// atlas rectangle, which says what it samples. Neither says what the artist wrote, so a
	/// quad that comes out the wrong size cannot be blamed on the file or on us.
	///
	/// That is exactly the question a nine-slice frame raises. A slice called <c>left</c>
	/// measuring the full width of its box is either the file saying so — in which case the
	/// runtime must be differentiating the pieces some other way — or our transform chain
	/// losing the artist's local scale. The authored numbers decide it in one read.
	/// </summary>
	private void DumpTreeIfRequested()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_TREE") is not { } want ||
			want.Length == 0 || CurrentScreen is not { } probe)
			return;

		int found = 0;

		void Walk(UiNode node, int depth)
		{
			string pad = new string(' ', depth * 2);
			string quad = node.Quad is { } q
				? $"  quad u[{q.UMin:0.###}..{q.UMax:0.###}] v[{q.VMin:0.###}..{q.VMax:0.###}]" +
				  $" rgb({q.R:0.##},{q.G:0.##},{q.B:0.##})"
				: node.Text is not null ? "  text" : string.Empty;

			GD.Print($"[ui-tree] {pad}{node.Name,-22} #{node.Ordinal,-5} " +
				$"pos({node.PositionX,8:0.##},{node.PositionY,8:0.##},{node.PositionZ,7:0.##}) " +
				$"scale({node.ScaleX,7:0.####},{node.ScaleY,7:0.####}) " +
				$"pivot({node.PivotX,6:0.##},{node.PivotY,6:0.##}) " +
				$"op {node.Opacity,3:0} {(node.Visible ? " " : "OFF")}{quad}");

			foreach (UiNode child in node.Children)
				Walk(child, depth + 1);
		}

		foreach (UiNode node in probe.Nodes)
		{
			if (!string.Equals(node.Name, want, System.StringComparison.OrdinalIgnoreCase))
				continue;

			++found;
			GD.Print($"[ui-tree] --- match {found}: '{node.Name}' #{node.Ordinal}, parent " +
				$"'{node.Parent?.Name ?? "(root)"}' ---");
			Walk(node, 0);
		}

		GD.Print($"[ui-tree] '{want}' matched {found} element(s)");
	}

	/// <summary>
	/// With <c>REFII_UI_MENU=1</c>, prints the screen's <c>ExpandableMenu</c> button pool.
	///
	/// With no guest attached every button is necessarily EMPTY, and that is the point: it
	/// establishes the pool's size and that the walk down
	/// <c>ExpandableMenu/Buttons/MenuItemButtonN/Icon/Title</c> finds the labels, so when the
	/// running game prints the same line with entries in it the difference is the guest's
	/// spawns and nothing else.
	/// </summary>
	private void DumpMenuIfRequested()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_MENU") != "1" ||
			CurrentScreen is not { } probe)
			return;

		IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons =
			Engine_ExpandableMenuItems.Read(probe, null, null, null);

		foreach (Engine_ExpandableMenuItems.Button b in buttons)
			GD.Print($"[ui-menu] {b.Name,-20} element {b.Ordinal,5}  " +
				$"Icon/Title {b.TitleOrdinal,5}  authored \"{b.Authored}\"  " +
				$"text {(b.Text is null ? "(none)" : $"\"{b.Text}\"")}");

		GD.Print($"[ui-menu] {Engine_ExpandableMenuItems.Describe(buttons)}");

		// The wheel is a separate widget: the expandable menu has a pool of authored buttons the
		// guest fills, the wheel has items that stay empty until the template is copied in.
		GD.Print($"[ui-wheel] {Engine_UiMenuCloning.Describe(probe.MenuCloning)}");

		// The movie hosts, because a screen that can play video says so in the file and it is
		// otherwise invisible: the authored path is a placeholder the guest overwrites.
		foreach (Engine_UiScreen.MovieSlot slot in probe.MovieSlots)
			GD.Print($"[ui-movie] element {slot.Ordinal,5} {slot.Name,-32} \"{slot.AuthoredPath}\"");
	}

	/// <summary>
	/// Prints every texture the screen asked for and did not get, with which side failed.
	///
	/// Not gated on a flag: an untextured quad draws as a flat rectangle, which reads as a
	/// styling choice rather than a missing asset, and Godot's own "Cannot open file" names the
	/// path without saying whether the bank has it.
	/// </summary>
	private void DumpTextureFailures()
	{
		if (Textures is not { } cache || cache.Failures.Count == 0)
		{
			return;
		}

		foreach ((string path, string why) in cache.Failures)
		{
			GD.Print($"[ui-tex] {path}: {why}");
		}

		GD.Print($"[ui-tex] {cache.Failures.Count} texture(s) failed, {cache.Loaded} loaded");
	}

	/// <summary>
	/// With <c>REFII_UI_BINDINGS=1</c>, prints the live-value bindings
	/// <see cref="Engine_UiBindings"/> reads out of the shown screen.
	///
	/// The hash is the useful column: it is what the guest publishes each value under, so a
	/// binding whose hash does not appear in a live meter read is either a value nothing drives
	/// or a hash computed the wrong way — and those look identical in a screenshot of a bar
	/// that never moves. Front-end screens print nothing, which is the right answer for them.
	/// </summary>
	private void DumpBindingsIfRequested()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_BINDINGS") != "1" ||
			CurrentScreen is not { } probe)
			return;

		Engine_UiBindings bindings = Engine_UiBindings.Read(probe.Document);
		foreach (Engine_UiBindings.Binding b in bindings.All)
		{
			GD.Print($"[ui-bind] {b.Kind,-12} {b.Name,-24} 0x{b.NameHash:X8} " +
				$"element {b.ElementOrdinal,5} {b.ElementName}");
		}

		foreach ((Engine_UiBindings.Kind kind, string _) in Engine_UiBindings.Attributes)
		{
			foreach ((string name, uint hash) in bindings.Names(kind))
			{
				GD.Print($"[ui-bind] {kind} '{name}' 0x{hash:X8} drives " +
					$"{bindings.Elements(kind, hash).Count} element(s)");
			}
		}

		GD.Print(bindings.Describe());
	}

	/// <summary>
	/// With <c>REFII_FONT_DUMP=&lt;dir&gt;</c>, writes each loaded glyph atlas to a PNG and
	/// prints its channel statistics.
	///
	/// Whether the atlas carries colour or a coverage mask, and whether the 360's
	/// 16-bit byte swap is needed, cannot be settled from a composited screen — black
	/// glyph boxes look the same whether the alpha is wrong, the swap is wrong or the
	/// UVs are. Look at the atlas.
	/// </summary>
	private void DumpFontAtlasIfRequested()
	{
		string? directory = System.Environment.GetEnvironmentVariable("REFII_FONT_DUMP");
		if (string.IsNullOrEmpty(directory) || Fonts is null)
			return;

		DirAccess.MakeDirRecursiveAbsolute(directory!);

		foreach (int size in new[] { 12, 18, 24 })
		{
			if (Fonts.For(size) is not { } selection)
				continue;

			(UiFont font, float scale) = selection;
			Image image = font.Atlas.GetImage();
			if (image.IsCompressed())
				image.Decompress();

			// Channel ranges answer the question directly: a coverage mask is
			// white-on-black in RGB with alpha pinned at 1.
			float minA = 1f, maxA = 0f, minL = 1f, maxL = 0f;
			for (int y = 0; y < image.GetHeight(); y += 4)
			{
				for (int x = 0; x < image.GetWidth(); x += 4)
				{
					Color c = image.GetPixel(x, y);
					float l = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
					minA = Mathf.Min(minA, c.A); maxA = Mathf.Max(maxA, c.A);
					minL = Mathf.Min(minL, l);   maxL = Mathf.Max(maxL, l);
				}
			}

			string name = $"{Engine_UiFontLibrary.BucketFor(size)}".ToLowerInvariant();
			string path = Path.Combine(directory!, $"font_{name}.png");
			GD.Print($"font {name} (size {size} -> scale {scale:F3}): " +
				$"{image.GetWidth()}x{image.GetHeight()} {image.GetFormat()}  " +
				$"alpha [{minA:F2}..{maxA:F2}]  luma [{minL:F2}..{maxL:F2}]  " +
				$"lineHeight={font.Font.LineHeight} cellHeight={font.Font.CellHeight} " +
				$"glyphs={font.Font.GlyphCount}");
			GD.Print(image.SavePng(path) == Error.Ok ? $"  wrote {path}" : "  save failed");
		}
	}

	/// <summary>
	/// Writes the rendered viewport to <c>REFII_UI_SHOT</c> and quits. Rendering can
	/// only be confirmed by looking at pixels — a scene that loads and builds nodes
	/// still renders nothing if no camera is current.
	/// </summary>
	private async void CaptureIfRequested()
	{
		string? all = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT_ALL");
		if (!string.IsNullOrEmpty(all))
		{
			await CaptureEveryScreen(all!);
			return;
		}

		string? target = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT");
		if (string.IsNullOrEmpty(target))
			return;

		// Two frames: one to flush the scene change, one to draw it.
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		Error err = image.SavePng(target!);
		GD.Print(err == Error.Ok ? $"wrote {target}" : $"screenshot failed: {err}");
		GetTree().Quit();
	}

	/// <summary>
	/// With <c>REFII_UI_SHOT_ALL=&lt;dir&gt;</c>, renders every screen to a PNG in one
	/// process and quits.
	///
	/// One process, not 52: the bank mount and font load dominate a single-screen run,
	/// so capturing the set one process at a time costs minutes rather than seconds and
	/// makes a whole-set sweep something you avoid doing.
	/// </summary>
	private async System.Threading.Tasks.Task CaptureEveryScreen(string directory)
	{
		DirAccess.MakeDirRecursiveAbsolute(directory);

		for (int i = 0; i < ScreenNames.Count; i++)
		{
			Show(i);

			// Two frames: one to flush the scene change, one to draw it.
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

			Image image = GetViewport().GetTexture().GetImage();
			string path = Path.Combine(directory, $"{i:D2}_{ScreenNames[i]}.png");
			if (image.SavePng(path) != Error.Ok)
				GD.PrintErr($"screenshot failed: {path}");
		}

		GD.Print($"captured {ScreenNames.Count} screens to {directory}");
		GetTree().Quit();
	}

	/// <summary>
	/// With <c>REFII_UI_SLIDES</c> set, prints each owning element's slides and what each
	/// switches on.
	///
	/// Which named slide the game shows is not in the file — see
	/// <see cref="Engine_BgfDocument.ComputeVisibility"/> — so this is how you see what the
	/// alternatives even are before deciding what a static preview should pick.
	/// </summary>
	private void DumpSlides(Engine_UiScreen screen)
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_SLIDES") is null)
			return;

		Engine_BgfDocument doc = screen.Document;
		var byOwner = new Dictionary<int, List<BgfSlide>>();
		foreach (BgfSlide s in doc.Slides)
		{
			if (!byOwner.TryGetValue(s.OwnerElement, out var list))
				byOwner[s.OwnerElement] = list = new List<BgfSlide>();
			list.Add(s);
		}

		GD.Print($"slides for {screen.Name}: {doc.Slides.Length} across {byOwner.Count} owners");
		foreach ((int owner, List<BgfSlide> owned) in byOwner.OrderBy(p => p.Key))
		{
			string ownerName = owner >= 0 && owner < screen.Nodes.Length
				? screen.Nodes[owner].Name
				: $"<ordinal {owner}>";
			GD.Print($"  owner {owner} ({ownerName}) — {owned.Count} slide(s)");

			foreach (BgfSlide s in owned)
			{
				int on = s.Entries.Count(e => e.Active);
				int commands = s.Entries.Sum(e => e.CommandCount);
				int events = s.Entries.Sum(e => e.EventCount);
				GD.Print($"      {(s.IsMaster ? "*" : " ")} {s.Name,-28} {s.Entries.Length,4} entries, " +
					$"{on,4} on, {commands,5} commands, {events,4} events");

				foreach (BgfSlideEntry e in s.Entries)
				{
					// The tracks a slide activates — a fade's END POSE lives in the last key, not
					// in the command, which only holds the value the tween starts from.
					foreach (int index in e.ActivatedAnimations ?? System.Array.Empty<int>())
					{
						if (index < 0 || index >= doc.Animations.Length)
							continue;

						BgfAnimation track = doc.Animations[index];
						string trackTarget = track.ElementOrdinal >= 0 && track.ElementOrdinal < screen.Nodes.Length
							? screen.Nodes[track.ElementOrdinal].Name
							: $"<{track.ElementOrdinal}>";
						string keys = string.Join(" ", track.Keys.Select(k => $"{k.Time:0.###}:{k.Value:0.###}"));
						GD.Print($"          {(e.Active ? "on " : "off")} {trackTarget,-18} " +
							$"{Engine_BgfAttributeNames.Describe(track.AttributeId),-16} track      [{keys}]");
					}

					if (e.CommandCount == 0)
						continue;

					string target = e.ElementOrdinal >= 0 && e.ElementOrdinal < screen.Nodes.Length
						? screen.Nodes[e.ElementOrdinal].Name
						: $"<{e.ElementOrdinal}>";

					foreach (BgfAttribute a in e.Commands)
					{
						string value = a.Type switch
						{
							BgfValueType.Float => a.AsFloat().ToString("0.####"),
							BgfValueType.Bool => a.AsBool().ToString(),
							BgfValueType.String => $"str[{a.AsStringIndex()}]",
							_ => a.AsInt().ToString(),
						};
						GD.Print($"          {(e.Active ? "on " : "off")} {target,-18} " +
							$"{Engine_BgfAttributeNames.Describe(a.Id),-16} {a.Type,-10} {value}");
					}
				}
			}
		}
	}

	/// <summary>
	/// With <c>REFII_UI_QUADS</c> set, prints every quad's source record — path in the
	/// tree, atlas rectangle, tint, opacity and resolved texture.
	///
	/// The geometry dump answers "where is it"; this answers "what is it sampling",
	/// which is the question a flat-coloured quad actually poses. Reading it off the
	/// screen instead has produced wrong guesses.
	/// </summary>
	private void DumpQuads(Engine_UiScreen screen)
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_QUADS") is null)
			return;

		GD.Print($"quads for {screen.Name}  (canvas {screen.CanvasWidth}x{screen.CanvasHeight})");
		GD.Print("  path                                              u0     u1     v0     v1    " +
			"du     dv    tint            op   texture");

		foreach (UiNode n in screen.Walk())
		{
			if (n.Quad is not { } q)
				continue;

			var path = new List<string>();
			for (UiNode? walk = n; walk is not null; walk = walk.Parent)
				path.Add(walk.Name);
			path.Reverse();

			// A quad can be absent from the render for two very different reasons: its own
			// slide entry switched it off, or an ancestor's did and took the subtree with
			// it. Naming the ancestor is the difference between "this element is off" and
			// "this whole component is off", which reads identically on screen.
			string dropped = string.Empty;
			for (UiNode? walk = n; walk is not null; walk = walk.Parent)
			{
				if (!walk.Visible)
					dropped = ReferenceEquals(walk, n) ? "  OFF" : $"  OFF via {walk.Name}";
			}

			Texture2D? tex = q.TexturePath.Length == 0 ? null : Textures?.Load(q.TexturePath);
			string texInfo = q.TexturePath.Length == 0
				? $"<unresolved hash {q.TextureHash:X8}>"
				// The FULL path, not the leaf. Two textures called `empty.tex` under different
				// folders are indistinguishable by leaf, and the path is what REFII_TEX_DUMP
				// takes — so printing the leaf meant the next question always needed another
				// tool to answer.
				: $"{q.TexturePath} " +
				  (tex is null ? "(LOAD FAILED)" : $"{tex.GetWidth()}x{tex.GetHeight()}");

			GD.Print($"  {Truncate(string.Join('/', path), 48),-48} " +
				$"{q.UMin,6:F3} {q.UMax,6:F3} {q.VMin,6:F3} {q.VMax,6:F3} " +
				$"{q.UMax - q.UMin,6:F3} {q.VMax - q.VMin,6:F3} " +
				$"{q.R,4:F2},{q.G,4:F2},{q.B,4:F2} {n.Opacity,5:F0}  " +
				// The two record fields the quad itself does not carry into the scene. A cell
				// that samples empty atlas space is either aimed wrong or drawing from a slot
				// we never read, and those look identical without them.
				$"det{q.Source.DetailMapMode} sec{q.Source.SecondaryTexture}  {texInfo}" +
				(q.IsDegenerate ? "  STRETCH" : "") + dropped);
		}
	}

	private static IEnumerable<Camera3D> FindCameras(Node? node)
	{
		if (node is null)
			yield break;
		if (node is Camera3D cam)
			yield return cam;
		foreach (Node child in node.GetChildren())
			foreach (Camera3D found in FindCameras(child))
				yield return found;
	}

	private static string Truncate(string value, int width) =>
		value.Length <= width ? value : "…" + value[^(width - 1)..];

	/// <summary>
	/// With <c>REFII_UI_DEBUG</c> set, prints the world extent of every BUILT quad, by
	/// full path. Engine-measured geometry, so it settles pitch-versus-width questions
	/// that a screenshot can only suggest.
	///
	/// By full path, not grouped by parent name: slice names repeat across the frame
	/// groups in a screen, so all six of a menu's <c>UpperLeftCorner</c>s collapse into
	/// one bucket and a subtree that never got built looks indistinguishable from one
	/// that did. That is exactly how the missing paperbox edges hid.
	///
	/// Built quads only — pair it with <see cref="DumpQuads"/>, which walks the screen
	/// data, to see what the slide baseline dropped.
	/// </summary>
	private void DumpSliceGeometry()
	{
		if (System.Environment.GetEnvironmentVariable("REFII_UI_DEBUG") is null || Built is null)
			return;

		var rows = new List<(string path, Aabb box, string tex)>();

		void Walk(Node node, string prefix)
		{
			string path = prefix.Length == 0 ? node.Name.ToString() : prefix + "/" + node.Name;
			if (node is MeshInstance3D mesh)
			{
				rows.Add((path,
					mesh.GlobalTransform * mesh.GetAabb(),
					mesh.HasMeta("texture_path") ? (string)mesh.GetMeta("texture_path") : ""));
			}
			foreach (Node child in node.GetChildren())
				Walk(child, path);
		}

		Walk(Built, string.Empty);

		// Camera versus content, together: "the screen is empty" has two causes that look
		// identical — content built somewhere the camera is not pointing, and a camera
		// whose projection does not cover content that is exactly where it should be.
		Aabb content = default;
		bool seeded = false;
		foreach ((_, Aabb box, _) in rows)
		{
			content = seeded ? content.Merge(box) : box;
			seeded = true;
		}

		if (seeded)
		{
			GD.Print($"content bounds: centre {content.GetCenter()}  size {content.Size}  " +
				$"x=[{content.Position.X:F1}..{content.End.X:F1}] " +
				$"y=[{content.Position.Y:F1}..{content.End.Y:F1}] " +
				$"z=[{content.Position.Z:F1}..{content.End.Z:F1}]");
		}

		foreach (Camera3D cam in FindCameras(Built))
		{
			GD.Print($"camera {cam.Name}: current={cam.Current} {cam.Projection} " +
				$"fov={cam.Fov:F2} size={cam.Size:F2} near={cam.Near:F3} far={cam.Far:F1} " +
				$"keep={cam.KeepAspect}");
			GD.Print($"    at {cam.GlobalPosition}  forward {-cam.GlobalTransform.Basis.Z}  " +
				$"up {cam.GlobalTransform.Basis.Y}");
			if (cam.HasMeta("lookatpoint"))
				GD.Print($"    authored lookatpoint (parent space) {(Vector3)cam.GetMeta("lookatpoint")}");
		}

		GD.Print($"built quad world extents ({rows.Count} meshes):");
		foreach ((string path, Aabb box, string tex) in rows)
		{
			string leaf = tex.Length == 0 ? "" : Path.GetFileName(tex.Replace('\\', '/'));
			GD.Print($"  {Truncate(path, 54),-54} x=[{box.Position.X,8:F2} ..{box.End.X,8:F2}] w={box.Size.X,8:F2}  " +
				$"y=[{box.Position.Y,8:F2} ..{box.End.Y,8:F2}] h={box.Size.Y,8:F2}  {leaf}");
		}
	}

	/// <summary>
	/// With <c>REFII_TEX_DUMP=&lt;engine\path.tex&gt;</c> and <c>REFII_TEX_OUT</c>, writes that
	/// texture to a PNG. Lets an atlas cell be inspected directly instead of inferred
	/// from how it looks composited into a screen.
	/// </summary>
	private void DumpTextureIfRequested()
	{
		string? want = System.Environment.GetEnvironmentVariable("REFII_TEX_DUMP");
		string? outPath = System.Environment.GetEnvironmentVariable("REFII_TEX_OUT");
		if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(outPath) || Textures is null)
			return;

		Texture2D? tex = Textures.Load(want!);
		if (tex is null)
		{
			GD.Print($"texture dump failed: {want} -> " +
				(Textures.Failures.TryGetValue(want!, out string? why) ? why : "not found"));
			return;
		}

		Image image = tex.GetImage();
		// Decompress so the PNG carries actual pixels rather than block data.
		if (image.IsCompressed())
			image.Decompress();
		GD.Print(image.SavePng(outPath!) == Error.Ok
			? $"wrote {outPath} ({image.GetWidth()}x{image.GetHeight()}, {image.GetFormat()})"
			: "texture dump: SavePng failed");
	}

	protected override void Report(string message)
	{
		if (_status is not null)
			_status.Text = message;
		GD.Print(message);
	}
}
