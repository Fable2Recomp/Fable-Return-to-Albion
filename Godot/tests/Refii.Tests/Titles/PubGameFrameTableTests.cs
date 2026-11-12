namespace Refii.Tests.Titles;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2.PubGames;

/// <summary>
/// A pub game's timelines as managed tables: each frame's placements read once from the table the
/// scene builder writes, and a clip's frames placed, moved, tinted, swapped and removed from them
/// as they were from the Godot dictionaries. A library sprite's clips share one table.
/// </summary>
[TestSuite]
public class PubGameFrameTableTests
{
	private static readonly float[] Left = { 1f, 0f, 0f, 1f, 10f, 20f };
	private static readonly float[] Moved = { 2f, 0f, 0f, 2f, 30f, 40f };
	private static readonly float[] Red = { 1f, 0f, 0f, 0.5f };

	[TestCase, RequireGodotRuntime]
	public void TestTheTableReadsWhatTheBuilderWrites()
	{
		using Godot.Collections.Array table = Table(
			new[]
			{
				Place(1, 10, "left", Left, Red),
				Place(2, 11),
				Place(3, 12, string.Empty, new[] { 1f, 2f, 3f }, new[] { 1f, 1f }),
			},
			System.Array.Empty<Godot.Collections.Dictionary>());

		SwfFrameTable frames = SwfFrameTable.From(table);
		AssertThat(frames.Count).IsEqual(2);
		SwfPlacement[] first = frames.Placements(1).ToArray();
		AssertThat(first.Length).IsEqual(3);
		AssertThat(first[0].Depth).IsEqual(1);
		AssertThat(first[0].Character).IsEqual(10);
		AssertThat(first[0].Name).IsEqual("left");
		AssertThat(first[0].Matrix).IsEqual(new Transform2D(new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(10f, 20f)));
		AssertThat(first[0].Tint).IsEqual(new Color(1f, 0f, 0f, 0.5f));
		AssertThat(first[1].Name).IsNull();
		AssertThat(first[1].Matrix.HasValue).IsFalse();
		AssertThat(first[1].Tint.HasValue).IsFalse();
		AssertThat(first[2].Name).IsEqual(string.Empty);
		AssertThat(first[2].Matrix.HasValue).IsFalse();
		AssertThat(first[2].Tint.HasValue).IsFalse();
		AssertThat(frames.Placements(2).Length).IsEqual(0);
	}

	[TestCase]
	public void TestAFramePlacesExactlyItsOwnDepths()
	{
		SwfFrameTable frames = SwfFrameTable.Of(
			new[] { new SwfPlacement(7, 1, null, null, null), new SwfPlacement(2, 1, null, null, null) },
			System.Array.Empty<SwfPlacement>());
		AssertThat(frames.Places(1, 7)).IsTrue();
		AssertThat(frames.Places(1, 2)).IsTrue();
		AssertThat(frames.Places(1, 3)).IsFalse();
		AssertThat(frames.Places(2, 7)).IsFalse();
		AssertThat(frames.Placements(1)[0].Depth).IsEqual(7);
	}

	[TestCase, RequireGodotRuntime]
	public void TestLabelsReadAsTheFramesTheyName()
	{
		using var labels = new Godot.Collections.Dictionary { ["spin"] = 2, ["idle"] = 1 };
		Dictionary<string, int> frames = SwfFrameTable.LabelsFrom(labels);
		AssertThat(frames.Count).IsEqual(2);
		AssertThat(frames["spin"]).IsEqual(2);
		AssertThat(frames["idle"]).IsEqual(1);
	}

	/// <summary>
	/// Frame 1 places 10 (named, moved) and 11 (tinted); frame 2 moves 10, drops 11 and places 12;
	/// frame 3 swaps depth 1 to 11 and names a character with no template, which places nothing.
	/// </summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestAStageClipPlacesMovesSwapsAndRemovesAsItsFramesSay()
	{
		Node2D stage = Stage(out Node2D board);
		SwfSceneUi? scene = null;
		try
		{
			scene = SwfSceneUi.FromStage("Test", stage);
			var clip = (SwfSceneClip)scene.Root.GetChild("board")!;
			AssertThat(clip.TotalFrames).IsEqual(3);
			AssertThat(board.HasMeta(Engine_SwfScene.TimelineMeta)).IsTrue();

			clip.GotoAndStop(1);
			SwfSceneClip[] one = clip.Children.ToArray();
			AssertThat(one.Select(c => c.Depth).ToArray()).IsEqual(new[] { 1, 2 });
			AssertThat(one[0].CharacterId).IsEqual(10);
			AssertThat(one[0].Name).IsEqual("left");
			AssertThat(one[0].Node.Transform).IsEqual(new Transform2D(new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(10f, 20f)));
			AssertThat(one[1].CharacterId).IsEqual(11);
			AssertThat(one[1].Name).IsEqual("d2");
			AssertThat(one[1].Node.Modulate).IsEqual(new Color(1f, 0f, 0f, 0.5f));
			AssertThat(one.Select(c => c.Node.GetIndex()).ToArray()).IsEqual(new[] { 0, 1 });

			clip.GotoAndStop(2);
			SwfSceneClip[] two = clip.Children.ToArray();
			AssertThat(two.Select(c => c.Depth).ToArray()).IsEqual(new[] { 1, 3 });
			AssertThat(two[0]).IsSame(one[0]);
			AssertThat(two[0].Node.Transform).IsEqual(new Transform2D(new Vector2(2f, 0f), new Vector2(0f, 2f), new Vector2(30f, 40f)));
			AssertThat(two[1].CharacterId).IsEqual(12);
			AssertThat(one[1].Node.GetParent()).IsNull();

			clip.GotoAndStop(3);
			SwfSceneClip[] three = clip.Children.ToArray();
			AssertThat(three.Select(c => c.Depth).ToArray()).IsEqual(new[] { 1 });
			AssertThat(three[0]).IsNotSame(one[0]);
			AssertThat(three[0].CharacterId).IsEqual(11);
			AssertThat(three[0].Name).IsEqual("d1");
			AssertThat(three[0].Node.Transform).IsEqual(Transform2D.Identity);
			AssertThat(clip.CurrentFrame).IsEqual(3);
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	[TestCase, RequireGodotRuntime]
	public async Task TestAPlayingClipStepsAtTheStageRateAndWraps()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			scene = SwfSceneUi.FromStage("Test", stage);
			var clip = (SwfSceneClip)scene.Root.GetChild("board")!;
			clip.GotoAndPlay(1);
			double step = 1.0 / scene.FrameRate;
			scene.Advance(step * 0.5);
			AssertThat(clip.CurrentFrame).IsEqual(1);
			scene.Advance(step * 0.6);
			AssertThat(clip.CurrentFrame).IsEqual(2);
			AssertThat(clip.Children.Select(c => c.Depth).ToArray()).IsEqual(new[] { 1, 3 });
			scene.Advance(step * 2);
			AssertThat(clip.CurrentFrame).IsEqual(1);
			AssertThat(clip.Children.Select(c => c.CharacterId).ToArray()).IsEqual(new[] { 10, 11 });
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>A library sprite's timeline leaves its template, and every clip of it reads the one table.</summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestALibrarySpritesClipsShareOneTable()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			Node2D spinner = stage.GetNode<Node2D>($"{Engine_SwfScene.LibraryName}/spinner");
			scene = SwfSceneUi.FromStage("Test", stage);
			AssertThat(spinner.HasMeta(Engine_SwfScene.TimelineMeta)).IsFalse();
			AssertThat(spinner.HasMeta(Engine_SwfScene.LabelsMeta)).IsFalse();
			SwfFrameTable? shared = scene.TimelineOf(60)?.Table;
			AssertThat(shared is not null).IsTrue();
			AssertThat(shared!.Count).IsEqual(2);

			var a = (SwfSceneClip)scene.Root.AttachMovie("spinner", "a", 5)!;
			var b = (SwfSceneClip)scene.Root.AttachMovie("spinner", "b", 6)!;
			AssertThat(a.TotalFrames).IsEqual(2);

			// Stopped on frame 2 by the table's stop frame, and by label.
			a.GotoAndPlay(1);
			scene.Advance(1.0 / scene.FrameRate);
			AssertThat(a.CurrentFrame).IsEqual(2);
			scene.Advance(1.0);
			AssertThat(a.CurrentFrame).IsEqual(2);
			AssertThat(a.Children.Single().CharacterId).IsEqual(12);

			b.GotoAndStop("first");
			AssertThat(b.CurrentFrame).IsEqual(1);
			AssertThat(b.Children.Single().CharacterId).IsEqual(10);
			b.GotoAndStop("second");
			AssertThat(b.CurrentFrame).IsEqual(2);
			b.GotoAndStop("none");
			AssertThat(b.CurrentFrame).IsEqual(2);
			AssertThat(ReferenceEquals(scene.TimelineOf(60)!.Table, shared)).IsTrue();
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>
	/// A timeline left on a template's nodes (one without a character, so not lifted) wakes in every
	/// copy attached, the first and the later ones; a template without one wakes nothing.
	/// </summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestATimelineInsideAnAttachedCopyWakesInEveryCopy()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			var holder = new Node2D { Name = "holder" };
			holder.SetMeta(Engine_SwfScene.CharacterMeta, 70);
			var inner = new Node2D { Name = "inner" };
			inner.SetMeta(Engine_SwfScene.FrameCountMeta, 2);
			using (Godot.Collections.Array table = Table(new[] { Place(1, 10) }, new[] { Place(1, 11) }))
			{
				inner.SetMeta(Engine_SwfScene.TimelineMeta, table);
			}

			holder.AddChild(inner);
			stage.GetNode(Engine_SwfScene.LibraryName).AddChild(holder);

			scene = SwfSceneUi.FromStage("Test", stage);
			foreach (string name in new[] { "first", "second" })
			{
				var copy = (SwfSceneClip)scene.Root.AttachMovie("holder", name, name.Length)!;
				SwfSceneClip? woken = scene.Existing(copy.Node.GetNode<Node2D>("inner"));
				AssertThat(woken is not null).IsTrue();
				woken!.GotoAndPlay(1);
				scene.Advance(1.0 / scene.FrameRate);
				AssertThat(woken.CurrentFrame).IsEqual(2);
				AssertThat(woken.Children.Single().CharacterId).IsEqual(11);
			}

			var plain = (SwfSceneClip)scene.Root.AttachMovie("c10", "plain", 9)!;
			AssertThat(plain.Node.Name.ToString()).IsEqual("plain");
			AssertThat(plain.TotalFrames).IsEqual(1);
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>
	/// A static shape a timeline lets go of is kept and placed again for its template's next
	/// placement, as the template stands: the tint frame 1 gave depth 2's 11 is not on it when frame
	/// 3 places 11 at depth 1.
	/// </summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestAStaticCopyLetGoIsPlacedAgainAsTheTemplateStands()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			scene = SwfSceneUi.FromStage("Test", stage);
			var clip = (SwfSceneClip)scene.Root.GetChild("board")!;
			clip.GotoAndStop(1);
			Node2D tinted = clip.ChildrenUnshared().ToArray()[1].Node;
			AssertThat(tinted.Modulate).IsEqual(new Color(1f, 0f, 0f, 0.5f));

			clip.GotoAndStop(2);
			AssertThat(tinted.GetParent()).IsNull();
			AssertThat(GodotObject.IsInstanceValid(tinted) && !tinted.IsQueuedForDeletion()).IsTrue();

			clip.GotoAndStop(3);
			SwfSceneClip placed = clip.ChildrenUnshared().Single();
			AssertThat(placed.Node).IsSame(tinted);
			AssertThat(placed.CharacterId).IsEqual(11);
			AssertThat(placed.Node.Name.ToString()).IsEqual("d1");
			AssertThat(placed.Node.Modulate).IsEqual(Colors.White);
			AssertThat(placed.Node.Transform).IsEqual(Transform2D.Identity);
			AssertThat(placed.Node.GetIndex()).IsEqual(0);

			// The board going away frees what is kept.
			clip.GotoAndStop(1);
			clip.GotoAndStop(2);
			scene.ReleaseOnPredelete((int)GodotObject.NotificationPredelete);
			AssertThat(placed.Node.IsQueuedForDeletion()).IsTrue();
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>A clip the game was handed, or a copy made before a tint, is freed, never placed again.</summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestACopyTheGameSawOrATintReachedIsNotKept()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			scene = SwfSceneUi.FromStage("Test", stage);
			var clip = (SwfSceneClip)scene.Root.GetChild("board")!;
			clip.GotoAndStop(1);
			Node2D seen = ((SwfSceneClip)clip.GetChild("d2")!).Node;
			clip.GotoAndStop(2);
			AssertThat(seen.IsQueuedForDeletion()).IsTrue();
			clip.GotoAndStop(3);
			AssertThat(clip.ChildrenUnshared().Single().Node).IsNotSame(seen);

			clip.GotoAndStop(1);
			Node2D before = clip.ChildrenUnshared().ToArray()[1].Node;
			scene.Root.SetTint(1f, 1f, 1f, 0f, 0f, 0f);
			clip.GotoAndStop(2);
			AssertThat(before.IsQueuedForDeletion()).IsTrue();
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>Only a template nothing under changes by itself is kept: not a sprite with a timeline, frames to play or text.</summary>
	[TestCase, RequireGodotRuntime]
	public async Task TestOnlyStaticTemplatesAreKept()
	{
		Node2D stage = Stage(out _);
		SwfSceneUi? scene = null;
		try
		{
			var labelled = new Node2D { Name = "labelled" };
			labelled.SetMeta(Engine_SwfScene.CharacterMeta, 80);
			labelled.AddChild(new Label { Name = Engine_SwfScene.TextName });
			var framed = new Node2D { Name = "framed" };
			framed.SetMeta(Engine_SwfScene.CharacterMeta, 81);
			framed.SetMeta(Engine_SwfScene.FrameCountMeta, 3);
			Node library = stage.GetNode(Engine_SwfScene.LibraryName);
			library.AddChild(labelled);
			library.AddChild(framed);

			scene = SwfSceneUi.FromStage("Test", stage);
			AssertThat(scene.Spare(scene.Template(10)!)).IsTrue();
			AssertThat(scene.Spare(scene.Template(60)!)).IsFalse();
			AssertThat(scene.Spare(scene.Template(80)!)).IsFalse();
			AssertThat(scene.Spare(scene.Template(81)!)).IsFalse();
		}
		finally
		{
			await CleanupStageAsync(stage, scene);
		}
	}

	/// <summary>
	/// Releases detached spare copies, frees the stage root, and advances across an end-of-frame
	/// delete-queue flush so nodes queued via <see cref="Node.QueueFree"/> are destroyed before
	/// GdUnit4's orphan-node detector runs.
	/// </summary>
	private static async Task CleanupStageAsync(Node2D stage, SwfSceneUi? scene)
	{
		scene?.ReleaseOnPredelete((int)GodotObject.NotificationPredelete);
		stage.Free();
		if (Engine.GetMainLoop() is SceneTree tree)
		{
			// GdUnit4 invokes tests inside _Process (before SceneTree emits process_frame and before
			// _flush_delete_queue runs at the end of the frame), so awaiting two process_frame signals
			// guarantees a full end-of-frame delete-queue flush in between.
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		}
	}

	/// <summary>A stage with a three-frame clip "board" and a library of 10, 11, 12 and a two-frame sprite 60.</summary>
	private static Node2D Stage(out Node2D board)
	{
		var stage = new Node2D { Name = "stage" };
		stage.SetMeta(Engine_SwfScene.CharacterMeta, -1);
		stage.SetMeta(Engine_SwfScene.FrameRateMeta, 24f);

		var library = new Node2D { Name = Engine_SwfScene.LibraryName, Visible = false };
		stage.AddChild(library);
		foreach (int id in new[] { 10, 11, 12 })
		{
			var template = new Node2D { Name = $"c{id}" };
			template.SetMeta(Engine_SwfScene.CharacterMeta, id);
			library.AddChild(template);
		}

		var spinner = new Node2D { Name = "spinner" };
		spinner.SetMeta(Engine_SwfScene.CharacterMeta, 60);
		spinner.SetMeta(Engine_SwfScene.FrameCountMeta, 2);
		spinner.SetMeta(Engine_SwfScene.StopFramesMeta, new[] { 2 });
		using (Godot.Collections.Array spin = Table(new[] { Place(1, 10) }, new[] { Place(1, 12) }))
		{
			spinner.SetMeta(Engine_SwfScene.TimelineMeta, spin);
		}

		using (var labels = new Godot.Collections.Dictionary { ["first"] = 1, ["second"] = 2 })
		{
			spinner.SetMeta(Engine_SwfScene.LabelsMeta, labels);
		}

		library.AddChild(spinner);

		board = new Node2D { Name = "board" };
		board.SetMeta(Engine_SwfScene.CharacterMeta, 50);
		board.SetMeta(Engine_SwfScene.FrameCountMeta, 3);
		using (Godot.Collections.Array table = Table(
			new[] { Place(1, 10, "left", Left), Place(2, 11, tint: Red) },
			new[] { Place(1, 10, matrix: Moved), Place(3, 12) },
			new[] { Place(1, 11), Place(4, 99) }))
		{
			board.SetMeta(Engine_SwfScene.TimelineMeta, table);
		}

		stage.AddChild(board);
		return stage;
	}

	private static Godot.Collections.Array Table(params Godot.Collections.Dictionary[][] frames)
	{
		var table = new Godot.Collections.Array();
		foreach (Godot.Collections.Dictionary[] places in frames)
		{
			using var frame = new Godot.Collections.Array();
			foreach (Godot.Collections.Dictionary place in places)
			{
				frame.Add(place);
				place.Dispose();
			}

			table.Add(frame);
		}

		return table;
	}

	/// <summary>A placement as <c>Engine_SwfScene</c> writes it: the name, matrix and tint only when there are any.</summary>
	private static Godot.Collections.Dictionary Place(
		int depth, int character, string? name = null, float[]? matrix = null, float[]? tint = null)
	{
		var place = new Godot.Collections.Dictionary
		{
			[Engine_SwfScene.PlaceDepth] = depth,
			[Engine_SwfScene.PlaceCharacter] = character,
		};

		if (name is not null)
		{
			place[Engine_SwfScene.PlaceName] = name;
		}

		if (matrix is not null)
		{
			place[Engine_SwfScene.PlaceMatrix] = matrix;
		}

		if (tint is not null)
		{
			place[Engine_SwfScene.PlaceTint] = tint;
		}

		return place;
	}
}
