using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using GdUnit4;
using Refii.Fx;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The per-frame shortcuts in the effect and HUD paths against what they stand in for: the same
/// bytes on the server, the same released opacities, the same writes on the materials.
/// </summary>
[TestSuite]
public sealed class FxUiFrameCostTests
{
	private const int Stride = 48;

	private static (Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)[] Batch(Random random, int count)
	{
		float Next() => (float)((random.NextDouble() * 200.0) - 100.0);
		var batch = new (Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)[count];
		for (int i = 0; i < count; i++)
		{
			batch[i] = (new Vector3(Next(), Next(), Next()), new Vector2(Next(), Next()), new Color(Next(), Next(), Next(), Next()),
				new Vector2(Next(), Next()), new Vector4(Next(), Next(), Next(), Next()));
		}

		return batch;
	}

	/// <summary>The packing the in-place write used to do, a slice per field.</summary>
	internal static void PackBySlices(
		ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list,
		Span<byte> vertices, Span<byte> attributes, int stride, int uvAt, int uv2At, int motionAt, int colourAt)
	{
		for (int i = 0; i < list.Length; i++)
		{
			(Vector3 pos, Vector2 uv, Color col, Vector2 off, Vector4 motion) = list[i];
			MemoryMarshal.Write(vertices.Slice(i * 12, 12), in pos);
			Span<byte> a = attributes.Slice(i * stride, stride);
			MemoryMarshal.Write(a.Slice(uvAt, 8), in uv);
			MemoryMarshal.Write(a.Slice(uv2At, 8), in off);
			MemoryMarshal.Write(a.Slice(motionAt, 16), in motion);
			MemoryMarshal.Write(a.Slice(colourAt, 16), in col);
		}
	}

	[TestCase]
	public void TheBatchPacksToTheSameBytes()
	{
		var random = new Random(3);
		foreach (int count in new[] { 0, 1, 6, 96, 1000 })
		{
			var batch = Batch(random, count);
			byte[] v1 = new byte[count * 12], a1 = new byte[count * Stride], v2 = new byte[count * 12], a2 = new byte[count * Stride];
			PackBySlices(batch, v1, a1, Stride, 0, 8, 16, 32);
			Engine_FxSystem.PackInPlace(batch, v2, a2, Stride, 0, 8, 16, 32);
			AssertThat(v2.SequenceEqual(v1)).IsTrue();
			AssertThat(a2.SequenceEqual(a1)).IsTrue();
		}
	}

	[TestCase]
	public void APackedBatchIsRefusedPastItsBuffer()
	{
		var batch = new (Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)[2];
		bool refused = false;
		try
		{
			Engine_FxSystem.PackInPlace(batch, new byte[12], new byte[Stride * 2], Stride, 0, 8, 16, 32);
		}
		catch (ArgumentOutOfRangeException)
		{
			refused = true;
		}

		AssertThat(refused).IsTrue();
	}

	/// <summary>
	/// The server's copy, updated only over the range the write sends, is byte for byte the writer's
	/// after every write, as it was when each write sent the whole surface.
	/// </summary>
	[TestCase]
	public void TheServerHoldsWhatAWholeUploadWouldHaveLeft()
	{
		var random = new Random(9);
		const int capacity = 384;
		byte[] vertices = new byte[capacity * 12], attributes = new byte[capacity * Stride];

		// What the surface was built with: a tail of the last point, which the first write replaces.
		byte[] serverVertices = new byte[capacity * 12], serverAttributes = new byte[capacity * Stride];
		random.NextBytes(serverVertices);
		random.NextBytes(serverAttributes);
		int last = 0;
		bool uploaded = false;
		long sentAll = 0, sentWhole = 0;
		for (int write = 0; write < 400; write++)
		{
			int n = random.Next(0, capacity / 6) * 6;
			var batch = Batch(random, n);
			Engine_FxSystem.PackInPlace(batch, vertices, attributes, Stride, 0, 8, 16, 32);
			if (n < last)
			{
				vertices.AsSpan(n * 12, (last - n) * 12).Clear();
				attributes.AsSpan(n * Stride, (last - n) * Stride).Clear();
			}

			int sent = Engine_FxSystem.InPlaceUploadVertices(n, last, capacity, uploaded);
			AssertThat(sent).IsBetween(n, capacity);
			vertices.AsSpan(0, sent * 12).CopyTo(serverVertices);
			attributes.AsSpan(0, sent * Stride).CopyTo(serverAttributes);
			last = n;
			uploaded = true;
			sentAll += sent;
			sentWhole += capacity;
			AssertThat(serverVertices.AsSpan().SequenceEqual(vertices)).IsTrue();
			AssertThat(serverAttributes.AsSpan().SequenceEqual(attributes)).IsTrue();
		}

		AssertThat(sentAll).IsLess(sentWhole);
	}

	/// <summary>The release as it read the entries before they were folded, with its break and its order.</summary>
	private static int ReleaseByEntries(Engine_BgfDocument file, BgfSlide slide, bool[] active, float?[] stated)
	{
		int released = 0;
		foreach (BgfSlideEntry entry in slide.Entries)
		{
			int ordinal = entry.ElementOrdinal;
			if (ordinal >= 0 && ordinal < stated.Length)
			{
				if (ordinal < active.Length)
				{
					active[ordinal] = entry.Active;
				}

				foreach (BgfAttribute command in entry.Commands)
				{
					if (command.Id == Engine_UiElementResolve.OpacityAttribute && stated[ordinal] is not null)
					{
						stated[ordinal] = null;
						++released;
						break;
					}
				}
			}

			foreach (int index in entry.ActivatedAnimations ?? Array.Empty<int>())
			{
				if (index < 0 || index >= file.Animations.Length)
				{
					continue;
				}

				BgfAnimation track = file.Animations[index];
				if (track.AttributeId == Engine_UiElementResolve.OpacityAttribute && track.ElementOrdinal >= 0 &&
					track.ElementOrdinal < stated.Length && stated[track.ElementOrdinal] is not null)
				{
					stated[track.ElementOrdinal] = null;
					++released;
				}
			}
		}

		return released;
	}

	private static Engine_BgfDocument Document(Random random, int elements, int slides)
	{
		var file = new Engine_BgfDocument();
		uint other = Engine_UiElementResolve.OpacityAttribute + 1;
		file.Animations = Enumerable.Range(0, 40).Select(_ => new BgfAnimation
		{
			ElementOrdinal = random.Next(-2, elements + 3),
			AttributeId = random.Next(2) == 0 ? Engine_UiElementResolve.OpacityAttribute : other,
		}).ToArray();
		file.Slides = new BgfSlide[slides];
		for (int s = 0; s < slides; s++)
		{
			var entries = new BgfSlideEntry[random.Next(0, 30)];
			for (int e = 0; e < entries.Length; e++)
			{
				var commands = new BgfAttribute[random.Next(0, 4)];
				for (int c = 0; c < commands.Length; c++)
				{
					commands[c] = new BgfAttribute(random.Next(3) == 0 ? Engine_UiElementResolve.OpacityAttribute : other, BgfValueType.Float, 0u);
				}

				entries[e] = new BgfSlideEntry
				{
					ElementOrdinal = random.Next(-2, elements + 3),
					Active = random.Next(2) == 0,
					Commands = commands,
					ActivatedAnimations = random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(-2, 44)).ToArray(),
				};
			}

			file.Slides[s] = new BgfSlide
			{
				Name = random.Next(5) == 0 ? "Master Slide" : $"s{s}",
				OwnerElement = random.Next(0, 6),
				Entries = entries,
			};
		}

		return file;
	}

	[TestCase]
	public void TheFoldedReleaseIsTheEntries()
	{
		var random = new Random(21);
		for (int trial = 0; trial < 300; trial++)
		{
			const int elements = 24;
			Engine_BgfDocument file = Document(random, elements, 6);
			foreach (BgfSlide slide in file.Slides)
			{
				// Twice: the first call folds, the second reads the fold.
				for (int again = 0; again < 2; again++)
				{
					int activeLength = random.Next(elements - 4, elements + 1);
					bool[] active = Enumerable.Range(0, activeLength).Select(_ => random.Next(2) == 0).ToArray();
					float?[] stated = Enumerable.Range(0, elements).Select(_ => random.Next(3) == 0 ? (float?)null : random.Next(100)).ToArray();
					bool[] activeThen = (bool[])active.Clone();
					float?[] statedThen = (float?[])stated.Clone();
					int expected = ReleaseByEntries(file, slide, activeThen, statedThen);
					int got = Engine_UiElementResolve.ReleaseSlideEntries(file, slide, active, stated);
					AssertThat(got).IsEqual(expected);
					AssertThat(active.SequenceEqual(activeThen)).IsTrue();
					AssertThat(stated.SequenceEqual(statedThen)).IsTrue();
				}
			}
		}
	}

	/// <summary>An inserted element renumbers what the slides name, so what was folded goes.</summary>
	[TestCase]
	public void AnInsertedElementFoldsTheReleaseAgain()
	{
		Engine_BgfDocument file = Engine_BgfEdit.NewDocument("edit", 960, 720, false);
		int first = Engine_BgfEdit.AddChild(file, 0, "first");
		int second = Engine_BgfEdit.AddChild(file, 0, "second");
		var slide = new BgfSlide
		{
			Name = "on",
			OwnerElement = second,
			Entries = new[]
			{
				new BgfSlideEntry
				{
					ElementOrdinal = second,
					Active = true,
					Commands = new[] { new BgfAttribute(Engine_UiElementResolve.OpacityAttribute, BgfValueType.Float, 0u) },
				},
			},
		};
		file.Slides = new[] { slide };
		float?[] stated = { 1f, 1f, 1f, 1f, 1f };
		AssertThat(Engine_UiElementResolve.ReleaseSlideEntries(file, slide, new bool[5], stated)).IsEqual(1);
		AssertThat(stated[second]).IsNull();
		AssertThat(ReferenceEquals(Engine_UiElementResolve.SlideOf(file, second, 1), slide)).IsTrue();

		Engine_BgfEdit.AddChild(file, first, "under the first");
		int moved = second + 1;
		stated = new float?[] { 1f, 1f, 1f, 1f, 1f };
		AssertThat(Engine_UiElementResolve.ReleaseSlideEntries(file, slide, new bool[5], stated)).IsEqual(1);
		AssertThat(stated[moved]).IsNull();
		AssertThat(stated[second]).IsNotNull();
		AssertThat(ReferenceEquals(Engine_UiElementResolve.SlideOf(file, moved, 1), slide)).IsTrue();
		AssertThat(Engine_UiElementResolve.SlideOf(file, second, 1)).IsNull();
	}

	[TestCase]
	public void TheIndexedSlideIsTheScannedOne()
	{
		var random = new Random(77);
		for (int trial = 0; trial < 200; trial++)
		{
			Engine_BgfDocument file = Document(random, 24, 40);
			for (int owner = -1; owner < 8; owner++)
			{
				for (int wanted = -1; wanted < 12; wanted++)
				{
					BgfSlide? scanned = null;
					if (wanted > 0)
					{
						int seen = 0;
						foreach (BgfSlide slide in file.Slides)
						{
							if (slide.OwnerElement == owner && !slide.IsMaster && ++seen == wanted)
							{
								scanned = slide;
								break;
							}
						}
					}

					AssertThat(ReferenceEquals(Engine_UiElementResolve.SlideOf(file, owner, wanted), scanned)).IsTrue();
				}
			}
		}
	}

	private static Engine_UiScreen Screen(params UiNode[] nodes)
	{
		for (int i = 0; i < nodes.Length; i++)
		{
			nodes[i].Ordinal = i;
			nodes[i].ObjectId = (uint)(i + 1);
		}

		return new Engine_UiScreen { Nodes = nodes, Document = new Engine_BgfDocument() };
	}

	/// <summary>A channel behind an array or behind a list resolves alike, however long it is.</summary>
	[TestCase]
	public void AnArrayChannelResolvesAsAListDoes()
	{
		var random = new Random(5);
		var nodes = new UiNode[64];
		for (int i = 0; i < nodes.Length; i++)
		{
			nodes[i] = new UiNode { Opacity = random.Next(101), Parent = i > 0 && random.Next(4) != 0 ? nodes[random.Next(i)] : null };
		}

		Engine_UiScreen screen = Screen(nodes);
		for (int trial = 0; trial < 50; trial++)
		{
			bool[] active = Enumerable.Range(0, random.Next(40, 80)).Select(_ => random.Next(5) != 0).ToArray();
			bool[] switched = Enumerable.Range(0, random.Next(40, 80)).Select(_ => random.Next(5) == 0).ToArray();
			float?[] stated = Enumerable.Range(0, random.Next(40, 80)).Select(_ => random.Next(3) == 0 ? (float?)null : random.Next(-10, 130)).ToArray();
			var arrays = new Engine_UiElementResolve(screen);
			var lists = new Engine_UiElementResolve(screen);
			arrays.Apply(null, active, stated, switched);
			lists.Apply(null, active.ToList(), stated.ToList(), switched.ToList());
			AssertThat(arrays.Drawn.SequenceEqual(lists.Drawn)).IsTrue();
			AssertThat(arrays.Active.SequenceEqual(lists.Active)).IsTrue();
			AssertThat(arrays.Stated.SequenceEqual(lists.Stated)).IsTrue();
			AssertThat(arrays.Own.SequenceEqual(lists.Own)).IsTrue();
			AssertThat(arrays.Opacity.SequenceEqual(lists.Opacity)).IsTrue();
		}
	}

	private static float? Opacity(ShaderMaterial material)
	{
		Variant value = material.GetShaderParameter("opacity");
		return value.VariantType == Variant.Type.Nil ? null : (float)value;
	}

	/// <summary>
	/// The binding writes what it wrote when it read the material every sample: a change of opacity,
	/// a new picture, and the next material after a node had none. A material replaced under an
	/// unchanged opacity carries its own and is left alone, as it always was.
	/// </summary>
	[TestCase]
	[RequireGodotRuntime]
	public void TheBindingWritesWhatTheEverySampleReadWrote()
	{
		var root = new Node3D { Name = "Root" };
		var quad = new MeshInstance3D { Name = "Quad", MaterialOverride = new ShaderMaterial() };
		var label = new MeshInstance3D { Name = "Label" };
		root.AddChild(quad);
		root.AddChild(label);
		try
		{
			Engine_UiScreen screen = Screen(new UiNode(), new UiNode { Quad = new UiQuad() }, new UiNode());
			screen.Nodes[1].Parent = screen.Nodes[0];
			screen.Nodes[2].Parent = screen.Nodes[0];
			root.SetMeta("object_id", 1u);
			quad.SetMeta("object_id", 2u);
			label.SetMeta("object_id", 3u);

			int asked = 0;
			var picture = new PlaceholderTexture2D();
			var binding = new Engine_UiCloneBinding
			{
				ResolveTexture = _ =>
				{
					++asked;
					return picture;
				},
			};
			binding.Bind(root, screen);
			var resolve = new Engine_UiElementResolve(screen);
			uint[] textures = new uint[3];
			void Sample(float percent)
			{
				resolve.Apply(null, null, new float?[] { 100f, percent, percent });
				binding.Apply(resolve, textures);
			}

			var first = (ShaderMaterial)quad.MaterialOverride;
			Sample(50f);
			AssertThat(Opacity(first)).IsEqual(0.5f);

			// Replaced, the opacity unchanged: left alone. Then changed: written to the new one.
			var second = new ShaderMaterial();
			quad.MaterialOverride = second;
			Sample(50f);
			AssertThat(Opacity(second)).IsNull();
			Sample(25f);
			AssertThat(Opacity(second)).IsEqual(0.25f);
			AssertThat(Opacity(first)).IsEqual(0.5f);

			// Cleared, as a label is when its text goes, then given a fresh one: it must be written
			// though the opacity has not moved.
			quad.MaterialOverride = null;
			Engine_UiCloneBinding.NoteMaterialCleared();
			Sample(25f);
			var third = new ShaderMaterial();
			quad.MaterialOverride = third;
			Sample(25f);
			AssertThat(Opacity(third)).IsEqual(0.25f);

			// A label that never had one is written when it gets one.
			var late = new ShaderMaterial();
			Sample(25f);
			label.MaterialOverride = late;
			Sample(25f);
			AssertThat(Opacity(late)).IsEqual(0.25f);

			// A new picture is written on the standing material and asked for once.
			textures[1] = 7u;
			Sample(25f);
			Sample(25f);
			AssertThat(asked).IsEqual(1);
			AssertThat(ReferenceEquals(third.GetShaderParameter("diffuse_tex").AsGodotObject(), picture)).IsTrue();
			AssertThat(Opacity(third)).IsEqual(0.25f);
		}
		finally
		{
			root.Free();
		}
	}

	/// <summary>One write of a piece's basis and origin leaves what the two it replaced left.</summary>
	[TestCase]
	[RequireGodotRuntime]
	public void ThePlacementIsTheTwoWritesItReplaced()
	{
		var two = new Node3D();
		var one = new Node3D();
		try
		{
			var random = new Random(8);
			foreach (Node3D node in new[] { two, one })
			{
				node.Scale = new Vector3(2f, 3f, 1f);
				node.Rotation = new Vector3(0.1f, 0.2f, 0.3f);
			}

			for (int i = 0; i < 200; i++)
			{
				Basis basis = new Basis(Vector3.Back, (float)random.NextDouble()).Scaled(new Vector3((float)random.NextDouble(), (float)random.NextDouble(), 1f));
				var at = new Vector3((float)random.NextDouble() * 900f, (float)random.NextDouble() * 700f, 0.4f);
				two.Transform = new Transform3D(basis, two.Transform.Origin);
				two.Position = at;
				one.Transform = new Transform3D(basis, at);
				AssertThat(one.Transform == two.Transform).IsTrue();
				AssertThat(one.Position == two.Position).IsTrue();
			}
		}
		finally
		{
			two.Free();
			one.Free();
		}
	}

	/// <summary>
	/// An effect's node that has not moved has nothing to rebase: its transform against itself is
	/// the identity within the test the rebase makes, over the scales and distances the game places.
	/// </summary>
	[TestCase]
	public void AnUnmovedNodeNeverRebased()
	{
		var random = new Random(13);
		for (int trial = 0; trial < 200000; trial++)
		{
			var axis = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
			if (axis.LengthSquared() < 0.01f) continue;
			float size = (float)(0.05 + (random.NextDouble() * 20.0));
			var t = new Transform3D(
				new Basis(axis.Normalized(), (float)random.NextDouble() * 6.28f).Scaled(new Vector3(size, size, size)),
				new Vector3((float)(random.NextDouble() - 0.5) * 8000f, (float)(random.NextDouble() - 0.5) * 800f, (float)(random.NextDouble() - 0.5) * 8000f));
			Transform3D delta = t.AffineInverse() * t;
			AssertThat(delta.IsEqualApprox(Transform3D.Identity)).OverrideFailureMessage($"{t} -> {delta}").IsTrue();
		}
	}
}
