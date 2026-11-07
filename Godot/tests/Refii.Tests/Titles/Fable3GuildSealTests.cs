namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Titles.Fable3;
using Refii.Ui;
using Statement = Refii.Titles.Fable3.Fable3HudStatements.Statement;

/// <summary>
/// Fable 3's guild seal against the shipped <c>hud.bgf</c>, joined and resolved as
/// <see cref="Fable3HudDocument"/> does. `Layer.FollowerCollection` carries
/// `SlideSetName = GuildSealCollection1`; its slides are master, `hide`, `show`, `center`.
/// `hide` names the component, `Numbers` and `GuildSeal` active and commands `GuildSeal`'s
/// opacity to 0 — the component stays lit, its art is dark. `GuildSeal` is the
/// `GuildSealFillMeter1` meter: its `Slide1` runs opacity tracks on the corner and edge pieces
/// that stand at 100 late in the fill (`Spinning_corner` 250:0 → 254:100). The seal's hide
/// (sub_825F8020) states slide 1 and leaves the meter's time where the fill left it.
/// No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3GuildSealTests
{
	/// <summary>Fable 3's presentations bank beside a packaged install in the tree; null without one.</summary>
	private static string? ShippedBank()
	{
		string data = Path.Combine("assets", "Fable3", "game", "data", "guiscripts.bnk");
		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			if (File.Exists(Path.Combine(at, data)))
			{
				return Path.Combine(at, data);
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					if (File.Exists(Path.Combine(package, data)))
					{
						return Path.Combine(package, data);
					}
				}
			}
		}

		return null;
	}

	private static Engine_UiScreen? Hud()
	{
		string? bank = ShippedBank();
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	private sealed class Seal
	{
		public required Engine_UiScreen Screen { get; init; }
		public required Fable3HudStatements Join { get; init; }
		public int Collection { get; init; }
		public int Numbers { get; init; }
		public int Meter { get; init; }
	}

	private static Seal? Load()
	{
		if (Hud() is not { } screen)
		{
			return null;
		}

		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		var join = new Fable3HudStatements(
			Fable3HudDocument.PathsOf(screen),
			(attribute, value) => Fable3HudDocument.Bound(bindings, attribute, value),
			Fable3HudDocument.SlideOwners(screen.Document));
		UiNode collection = screen.Nodes.First(n => n.Name == "FollowerCollection" && n.Parent?.Name == "Layer");
		return new Seal
		{
			Screen = screen,
			Join = join,
			Collection = collection.Ordinal,
			Numbers = collection.Children.First(n => n.Name == "Numbers").Ordinal,
			Meter = collection.Children.First(n => n.Name == "GuildSeal").Ordinal,
		};
	}

	private static bool Under(Engine_UiScreen screen, int ordinal, int root)
	{
		for (UiNode? at = screen.Nodes[ordinal]; at is not null; at = at.Parent)
		{
			if (at.Ordinal == root)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The opacity tracks the meter's own slides switch on, beneath it: what its time scrubs.</summary>
	private static List<BgfAnimation> OpacityTracks(Seal seal)
	{
		Engine_BgfDocument file = seal.Screen.Document;
		var owned = new HashSet<int>();
		foreach (BgfSlide slide in file.Slides.Where(s => s.OwnerElement == seal.Meter))
		{
			foreach (BgfSlideEntry entry in slide.Entries)
			{
				owned.UnionWith((entry.ActivatedAnimations ?? Array.Empty<int>()).Where(i => i >= 0));
			}
		}

		return owned.Where(i => i < file.Animations.Length)
			.Select(i => file.Animations[i])
			.Where(a => a.AttributeId == Engine_UiElementResolve.OpacityAttribute && a.Keys.Length > 0 &&
				Under(seal.Screen, a.ElementOrdinal, seal.Meter))
			.ToList();
	}

	/// <summary>Folds the statements and resolves, as Fable3HudDocument.Feed does.</summary>
	private static (Engine_UiElementResolve Resolve, int[] Slides, float?[] Times) Fold(Seal seal, IEnumerable<Statement> statements)
	{
		int count = seal.Screen.Nodes.Length;
		var slides = new int[count];
		var opacity = new float?[count];
		var texts = new string?[count];
		var times = new float?[count];
		seal.Join.Apply(statements, slides, opacity, texts, times);
		var resolve = new Engine_UiElementResolve(seal.Screen) { AncestorsWinAtRest = true };
		resolve.Apply(slides, null, opacity);
		return (resolve, slides, times);
	}

	/// <summary>The spellings the guest states the seal's component under.</summary>
	private static readonly string[] Spellings =
	{
		"SlideSetName:GuildSealCollection1", "Layer.FollowerCollection", "FollowerCollection",
	};

	private const float FilledTime = 940f;

	private static List<Statement> Shown(string spelling) => new()
	{
		// The screen's first tick writes every owner at rest.
		new("Layer.FollowerCollection", 1, null, null, null, 1),
		new("Layer.FollowerCollection.GuildSeal", 1, null, null, null, 2),
		// ShowGuildSeal (sub_825F7E58): the collection and numbers to slide 2, then the fill.
		new(spelling, 2, null, null, null, 3),
		new("SlideSetName:GuildSealNumbers1", 2, null, null, null, 4),
		new("SlideSetName:1_GSNum_1", 2, null, null, null, 5),
		new("MeterName:GuildSealFillMeter1", null, null, null, FilledTime, 6),
	};

	[TestCase]
	public void EverySpellingNamesTheOneComponent()
	{
		if (Load() is not { } seal)
		{
			return;
		}

		foreach (string spelling in Spellings)
		{
			AssertThat(seal.Join.Resolve(spelling)).ContainsExactly(seal.Collection);
		}

		AssertThat(seal.Join.Resolve("MeterName:GuildSealFillMeter1")).ContainsExactly(seal.Meter);
	}

	[TestCase]
	public void TheShownSealDrawsItsCorners()
	{
		if (Load() is not { } seal)
		{
			return;
		}

		(Engine_UiElementResolve resolve, int[] slides, float?[] times) = Fold(seal, Shown(Spellings[0]));
		AssertThat(slides[seal.Collection]).IsEqual(2);
		AssertThat(times[seal.Meter]).IsEqual(FilledTime);
		AssertThat(resolve.Opacity[seal.Meter]).IsEqual(1f);
		AssertThat(OpacityTracks(seal).Any(track =>
			resolve.TrackOpacity(track.ElementOrdinal, track.Evaluate(FilledTime)) > 1f)).IsTrue();
	}

	[TestCase]
	public void TheHiddenSealDrawsNothingAtTheFillsTime()
	{
		if (Load() is not { } seal)
		{
			return;
		}

		List<BgfAnimation> tracks = OpacityTracks(seal);

		// The regression: these stand at 100 at the fill's time, whatever slide is up.
		AssertThat(tracks.Count(track => track.Evaluate(FilledTime) >= 100f)).IsGreater(0);

		foreach (string shownAs in Spellings)
		{
			foreach (string hiddenAs in Spellings)
			{
				// HideGuildSeal (sub_825F8020): slide 1 on both slide sets; the meter's time stays.
				List<Statement> statements = Shown(shownAs);
				statements.Add(new(hiddenAs, 1, null, null, null, 7));
				statements.Add(new("SlideSetName:GuildSealNumbers1", 1, null, null, null, 8));
				(Engine_UiElementResolve resolve, int[] slides, float?[] times) = Fold(seal, statements);

				AssertThat(slides[seal.Collection]).IsEqual(1);
				AssertThat(slides[seal.Numbers]).IsEqual(1);
				AssertThat(times[seal.Meter]).IsEqual(FilledTime);

				// `hide` authors the component lit and its art dark.
				AssertThat(resolve.Drawn[seal.Collection]).IsTrue();
				AssertThat(resolve.Opacity[seal.Meter]).IsEqual(0f);
				for (int i = 0; i < seal.Screen.Nodes.Length; ++i)
				{
					if (i != seal.Collection && Under(seal.Screen, i, seal.Collection) &&
						seal.Screen.Nodes[i].Type is BgfElementType.Node or BgfElementType.Component &&
						resolve.Drawn[i] && resolve.Opacity[i] > 0f && seal.Screen.Nodes[i].Children.Count > 0 &&
						seal.Screen.Nodes[i].Children.Any(c => c.Type == BgfElementType.Material))
					{
						AssertThat($"{seal.Screen.Nodes[i].Name}#{i} at {resolve.Opacity[i]} ({shownAs} / {hiddenAs})")
							.IsEqual("dark");
					}
				}

				// What the view writes for each scrubbed opacity track (Engine_HudView.ApplyTrack).
				foreach (BgfAnimation track in tracks)
				{
					AssertThat(resolve.TrackOpacity(track.ElementOrdinal, track.Evaluate(times[seal.Meter]!.Value)))
						.IsEqual(0f);
				}
			}
		}
	}
}
