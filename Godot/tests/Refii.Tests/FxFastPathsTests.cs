using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `fx_cpu_fastpaths` against the original loops: the same simulation emitted both ways gives the
/// same vertex buffers, float for float.
/// </summary>
[TestSuite]
public sealed class FxFastPathsTests
{
	/// <summary>One assembled batch, copied out of the system's pooled list.</summary>
	private sealed record Batch(string Mode, (Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)[] Vertices);

	private static Dictionary<string, Batch> Snapshot(Engine_FxSystem system) =>
		system.AssembledBatches().ToDictionary(b => b.Key, b => new Batch(b.Mode, b.Vertices.ToArray()));

	private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

	private static bool Same(Vector2 a, Vector2 b) => Bits(a.X, b.X) && Bits(a.Y, b.Y);

	private static bool Same(Vector3 a, Vector3 b) => Bits(a.X, b.X) && Bits(a.Y, b.Y) && Bits(a.Z, b.Z);

	private static bool Same(Vector4 a, Vector4 b) => Bits(a.X, b.X) && Bits(a.Y, b.Y) && Bits(a.Z, b.Z) && Bits(a.W, b.W);

	private static bool Same(Color a, Color b) => Bits(a.R, b.R) && Bits(a.G, b.G) && Bits(a.B, b.B) && Bits(a.A, b.A);

	/// <summary>Every field of every vertex, bit for bit (padding is not compared).</summary>
	internal static bool Same(
		ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> a,
		ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++)
		{
			if (!Same(a[i].Pos, b[i].Pos) || !Same(a[i].Uv, b[i].Uv) || !Same(a[i].Col, b[i].Col) ||
				!Same(a[i].Off, b[i].Off) || !Same(a[i].Motion, b[i].Motion))
				return false;
		}

		return true;
	}

	private static string Differs(Dictionary<string, Batch> slow, Dictionary<string, Batch> fast)
	{
		if (!slow.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(fast.Keys.OrderBy(k => k, StringComparer.Ordinal)))
			return $"batches {string.Join(",", slow.Keys)} vs {string.Join(",", fast.Keys)}";
		foreach ((string key, Batch batch) in slow)
		{
			if (!Same(batch.Vertices, fast[key].Vertices))
				return $"{key}: {batch.Vertices.Length} vs {fast[key].Vertices.Length} vertices differ";
		}

		return string.Empty;
	}

	private static string DiffersMeshes(Dictionary<string, Engine_FxSystem.MeshInstanceData> slow, Dictionary<string, Engine_FxSystem.MeshInstanceData> fast)
	{
		foreach ((string key, Engine_FxSystem.MeshInstanceData data) in slow)
		{
			List<(Transform3D Transform, Color Colour, Vector4 Motion)> other = fast[key].Instances;
			if (data.Instances.Count != other.Count) return $"{key}: {data.Instances.Count} vs {other.Count} instances";
			for (int i = 0; i < other.Count; i++)
			{
				if (data.Instances[i] != other[i]) return $"{key}[{i}] differs";
			}
		}

		return string.Empty;
	}

	private static FxEmitter Emitter(float rate, float speed)
	{
		var e = new FxEmitter { TypeId = 0, SubType = 1, MaxActive = 64, InitialNum = 4, SpawnRadius = 0.4f, AttachToRoot = true };
		e.Fades = new[] { 0.5f, 1f, 30f, 40f };
		for (int i = 0; i < 28; i++)
		{
			e.Generic[i] = Engine_FxBank.MakeTimeline(i, i switch
			{
				2 => new[] { (0f, rate), (1f, rate * 0.5f) },
				4 => new[] { (0f, speed) },
				5 => new[] { (0f, 0.3f) },
				7 => new[] { (0f, 0.2f) },
				9 => new[] { (0f, 0.5f) },
				10 => new[] { (0f, 0.5f) },
				11 => new[] { (0f, 1f) },
				_ => Array.Empty<(float, float)>(),
			});
		}

		e.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		e.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
		e.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		e.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(e);
		return e;
	}

	/// <summary>A sprite system in one blend: a flame fading in, a smoke fading to nothing, one aligned quad.</summary>
	private static FxSystemDef Sprites(int src, int dst, int op, int mode = 1)
	{
		var system = new FxSystemDef { Mode = (uint)mode, LoopCount = -1, LastStartFrames = 30, ChildCount = mode == 1 ? 1 : 2 };
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = $"art/fx/pfx_textures/flame{src}{dst}{op}.tex", TexCols = 4, TexRows = 4, FrameSpeed = 15f,
			HasColor = true, Color = new[] { 0.5f, 1f, 0.5f }, ColorSq = new[] { 0.25f, 1f, 0.25f }, HasSize = true, SizeX = 0.4f, SizeY = 0.6f,
			HasAlpha = true, Alpha = 0.00305f, SrcBlend = src, DstBlend = dst, BlendOp = op, HasRotation = true, RotSpeed = 2f, RotSpread = 1f,
			TrailWidth = 0.3f, HasTrailWidth = true,
		});
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = $"art/fx/pfx_textures/smoke{src}{dst}{op}.tex", Delay = 0.3f, DelayFrames = 9, InvSpan = 1f / 9f,
			HasAlpha = true, Alpha = 0.9f, HasSize = true, SizeX = 0.8f, SizeY = 0.8f,
		});
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true, Texture = $"art/fx/pfx_textures/fade{src}{dst}{op}.tex", Delay = 0.8f, DelayFrames = 24, InvSpan = 1f / 6f,
			HasAlpha = true, Alpha = 0f, HasSize = true, SizeX = 1.2f, SizeY = 0.5f,
			HasAlignQuat = true, AlignQuat = new[] { 0.3826834f, 0f, 0f, 0.9238795f },
		});
		return system;
	}

	/// <summary>One effect with an emitter per blend (mix, add, addone, mixalpha, darken, sub, revsub) and a ribbon.</summary>
	private static FxBank Bank()
	{
		var bank = new FxBank { Ok = true };
		var effect = new FxEffect { NameHash = FxBank.Fnv1Lower("fastpaths"), Name = "fastpaths", UpdateFrequency = 1 };
		(int Src, int Dst, int Op, int Mode)[] blends =
		{
			(1, 5, 0, 1), (4, 1, 0, 1), (1, 1, 0, 1), (4, 5, 0, 1), (0, 5, 0, 1), (4, 1, 2, 1), (1, 5, 2, 1), (4, 1, 0, 2),
		};
		for (int i = 0; i < blends.Length; i++)
		{
			bank.Emitters.Add(Emitter(6f + i, 0.5f + (0.1f * i)));
			bank.Systems.Add(Sprites(blends[i].Src, blends[i].Dst, blends[i].Op, blends[i].Mode));
			effect.Parts.Add(new FxEffectPart(i, i, 1, i));
		}

		bank.Effects.Add(effect);
		var half = new FxEffect { NameHash = FxBank.Fnv1Lower("fastpaths_half"), Name = "fastpaths_half", UpdateFrequency = 0 };
		half.Parts.AddRange(effect.Parts);
		bank.Effects.Add(half);
		return bank;
	}

	private static readonly Vector3[] Eyes =
	{
		new(0f, 1.5f, 3f), new(0.2f, 0.2f, 0.6f), new(0f, 2f, 34f), new(5f, 1f, 45f), new(-12f, 4f, -8f),
	};

	/// <summary>Assembles the system from each eye with the fast paths off and on and compares.</summary>
	private static void AssertSameEverywhere(Engine_FxSystem system, string label, Action<Dictionary<string, Batch>, Dictionary<string, Batch>, Vector3>? more = null)
	{
		foreach (Vector3 eye in Eyes)
		{
			var slowMeshes = new Dictionary<string, Engine_FxSystem.MeshInstanceData>();
			var fastMeshes = new Dictionary<string, Engine_FxSystem.MeshInstanceData>();
			system.Assemble(eye, Vector3.Right, Vector3.Up, slowMeshes, true, false);
			Dictionary<string, Batch> slow = Snapshot(system);
			system.Assemble(eye, Vector3.Right, Vector3.Up, fastMeshes, true, true);
			Dictionary<string, Batch> fast = Snapshot(system);
			string differs = Differs(slow, fast);
			AssertThat(differs).OverrideFailureMessage($"{label} from {eye}: {differs}").IsEmpty();
			string meshes = DiffersMeshes(slowMeshes, fastMeshes);
			AssertThat(meshes).OverrideFailureMessage($"{label} from {eye}: {meshes}").IsEmpty();
			more?.Invoke(slow, fast, eye);
		}
	}

	private static Engine_FxSystem Built(FxBank bank, params (string Effect, Vector3 At)[] placements)
	{
		var system = new Engine_FxSystem();
		system.Build(bank, placements.Select(p => new FxPlacement
		{
			EffectName = p.Effect, Pos = new[] { p.At.X, p.At.Z, p.At.Y },
		}));
		return system;
	}

	/// <summary>Steps with the fast paths on, so each emitter's summary for the whole-emitter test is written.</summary>
	private static void Step(Engine_FxSystem system, int frames, bool summarize = true)
	{
		bool before = Engine_FxFastPaths.Enabled;
		try
		{
			Engine_FxFastPaths.Enabled = summarize;
			for (int frame = 0; frame < frames; frame++) system.Update(1f / 30f, Transform3D.Identity);
		}
		finally { Engine_FxFastPaths.Enabled = before; }
	}

	/// <summary>
	/// Bit-identical batches at every age, distance and intensity, with most particles rejected
	/// early and whole emitters passed over (one placement sits past its fade from most eyes).
	/// </summary>
	[TestCase]
	public void TheEarlyRejectionDrawsTheSameParticles()
	{
		FxBank bank = Bank();
		Engine_FxSystem system = Built(bank, ("fastpaths", Vector3.Zero), ("fastpaths_half", new Vector3(3f, 0f, -2f)), ("fastpaths", new Vector3(0f, 0f, 38f)));
		AssertThat(system.IsEmpty).IsFalse();
		long early = Engine_FxSystem.SkipEarly, emitters = Engine_FxSystem.SkipEmitters;
		for (int frame = 0; frame < 150; frame++)
		{
			// Intensity goes away and comes back: particles born at zero stay at zero.
			system.SetIntensity(frame is >= 40 and < 100 ? 0f : 1f);
			Step(system, 1);
			if (frame % 10 == 9) AssertSameEverywhere(system, $"frame {frame}");
		}

		AssertThat(system.AliveCount()).IsGreater(0);
		AssertThat(Engine_FxSystem.SkipEarly - early).IsGreater(0L);
		AssertThat(Engine_FxSystem.SkipEmitters - emitters).IsGreater(0L);
	}

	/// <summary>The summary the fast paths write during a step reads the particles and changes nothing.</summary>
	[TestCase]
	public void TheStepSummaryChangesNoParticle()
	{
		FxBank bank = Bank();
		Engine_FxSystem plain = Built(bank, ("fastpaths", Vector3.Zero), ("fastpaths_half", new Vector3(3f, 0f, -2f)));
		Engine_FxSystem summarized = Built(bank, ("fastpaths", Vector3.Zero), ("fastpaths_half", new Vector3(3f, 0f, -2f)));
		var meshes = new Dictionary<string, Engine_FxSystem.MeshInstanceData>();
		for (int frame = 0; frame < 60; frame++)
		{
			Step(plain, 1, false);
			Step(summarized, 1, true);
			plain.Assemble(Eyes[0], Vector3.Right, Vector3.Up, meshes, true, false);
			Dictionary<string, Batch> a = Snapshot(plain);
			summarized.Assemble(Eyes[0], Vector3.Right, Vector3.Up, meshes, true, false);
			string differs = Differs(a, Snapshot(summarized));
			AssertThat(differs).OverrideFailureMessage($"frame {frame}: {differs}").IsEmpty();
		}
	}

	/// <summary>The box test errs only towards drawing: never past the fade while a point is within it.</summary>
	[TestCase]
	public void TheFadeBoxNeverPassesOverAVisiblePoint()
	{
		var random = new Random(11);
		for (int trial = 0; trial < 20000; trial++)
		{
			float span = trial % 4 == 0 ? 1000f : 40f;
			Vector3 a = new((float)(random.NextDouble() - 0.5) * span, (float)(random.NextDouble() - 0.5) * span, (float)(random.NextDouble() - 0.5) * span);
			Vector3 b = a + new Vector3((float)random.NextDouble() * 5f, (float)random.NextDouble() * 5f, (float)random.NextDouble() * 5f);
			Vector3 eye = new((float)(random.NextDouble() - 0.5) * span, (float)(random.NextDouble() - 0.5) * span, (float)(random.NextDouble() - 0.5) * span);
			float end = (float)random.NextDouble() * 60f;
			if (!Engine_FxFastPaths.BeyondFadeOut(a, b, eye, end)) continue;
			for (int k = 0; k < 16; k++)
			{
				Vector3 p = new(
					k % 2 == 0 ? a.X : (k % 4 == 1 ? b.X : a.X + ((b.X - a.X) * (float)random.NextDouble())),
					(k / 2) % 2 == 0 ? a.Y : b.Y, (k / 4) % 2 == 0 ? a.Z : b.Z);
				AssertThat((eye - p).Length() > end).IsTrue();
			}
		}

		AssertThat(Engine_FxFastPaths.BeyondFadeOut(Vector3.Zero, Vector3.One, new Vector3(0f, 0f, 50f), 40f)).IsTrue();
		AssertThat(Engine_FxFastPaths.BeyondFadeOut(Vector3.Zero, Vector3.One, new Vector3(0f, 0f, 40.5f), 40f)).IsFalse();
		AssertThat(Engine_FxFastPaths.BeyondFadeOut(Vector3.Zero, Vector3.One, new Vector3(0f, 0f, 50f), float.NaN)).IsFalse();
	}

	/// <summary>The shipped bank (when this tree or REFII_FX_BANK has it): every effect, the same batches.</summary>
	[TestCase]
	public void EveryShippedEffectDrawsTheSame()
	{
		FxBank? bank = ShippedBank();
		if (bank is null) return;
		int compared = 0;
		foreach (FxEffect effect in bank.Effects)
		{
			var system = new Engine_FxSystem();
			system.Build(bank, new[] { new FxPlacement { EffectHash = effect.NameHash } });
			// A displacement mesh particle notes itself to the engine's frame counter.
			if (system.IsEmpty || (system.HasDisplacement && system.HasMeshParticles)) continue;
			Step(system, 45);
			AssertSameEverywhere(system, effect.Name);
			system.SetIntensity(0f);
			Step(system, 10);
			AssertSameEverywhere(system, effect.Name + " dark");
			compared++;
		}

		AssertThat(compared).IsGreater(100);
	}

	internal static FxBank? ShippedBank()
	{
		string? path = System.Environment.GetEnvironmentVariable("REFII_FX_BANK");
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			path = null;
			for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null && path is null; dir = dir.Parent)
			{
				foreach (string candidate in new[]
				{
					Path.Combine(dir.FullName, "assets", "game", "data", "art", "particles", "particle_bank.bnk"),
					Path.Combine(dir.FullName, "Godot", "build", "x86_64", "assets", "game", "data", "art", "particles", "particle_bank.bnk"),
				})
				{
					if (File.Exists(candidate)) { path = candidate; break; }
				}
			}
		}

		if (path is null) return null;
		var bank = new FxBank();
		return Engine_FxBank.Parse(File.ReadAllBytes(path), bank) ? bank : null;
	}

	/// <summary>The ceiling bounds every lerp between two keyframes; a non-finite alpha turns the shortcut off.</summary>
	[TestCase]
	public void TheAlphaCeilingBoundsEveryLerp()
	{
		var random = new Random(7);
		for (int trial = 0; trial < 2000; trial++)
		{
			float[] alphas = { (float)(random.NextDouble() * 4 - 2) * (trial % 3 == 0 ? 1000f : 1f), (float)(random.NextDouble() * 2), (float)random.NextDouble() * 0.01f };
			float ceiling = Engine_FxFastPaths.AlphaCeiling(alphas);
			foreach (float x in alphas)
			{
				foreach (float y in alphas)
				{
					for (int k = 0; k <= 64; k++)
					{
						float w = k / 64f;
						float lerp = x + ((y - x) * w);
						AssertThat(lerp <= ceiling).OverrideFailureMessage($"{x} {y} {w}").IsTrue();
						float term = (float)random.NextDouble() * (k % 2 == 0 ? 1f : 0.001f);
						if (Engine_FxFastPaths.RejectedByFade(ceiling, term)) AssertThat(lerp * term <= 0.003f).IsTrue();
					}
				}
			}
		}

		AssertThat(float.IsPositiveInfinity(Engine_FxFastPaths.AlphaCeiling(new[] { 1f, float.NaN }))).IsTrue();
		AssertThat(Engine_FxFastPaths.RejectedByFade(1f, float.NaN)).IsFalse();
		AssertThat(Engine_FxFastPaths.RejectedByFade(1f, -1f)).IsFalse();
		AssertThat(Engine_FxFastPaths.RejectedByFade(1f, 0f)).IsTrue();
		AssertThat(Engine_FxFastPaths.RejectedByFade(float.PositiveInfinity, 0f)).IsFalse();
	}

	/// <summary>The original sorter, kept as the reference: the farthest quad first, copied tuple by tuple.</summary>
	private static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> OriginalSort(
		IEnumerable<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> input, Vector3 eye)
	{
		var verts = input.ToList();
		int quads = verts.Count / 6;
		if (quads < 2) return verts;
		var order = new List<(float D, int Q)>();
		for (int q = 0; q < quads; q++)
		{
			Vector3 c = 0.5f * (verts[q * 6].Pos + verts[(q * 6) + 1].Pos);
			order.Add(((c - eye).LengthSquared(), q));
		}

		order.Sort((l, r) => r.D.CompareTo(l.D));
		var sorted = new List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>();
		foreach ((_, int q) in order) for (int k = 0; k < 6; k++) sorted.Add(verts[(q * 6) + k]);
		return sorted;
	}

	private static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> FastSort(
		(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)[] input, Vector3 eye, ref ulong[] keys)
	{
		var verts = input.ToList();
		var scratch = new List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>();
		List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> result =
			Engine_FxFastPaths.SortQuadsBackToFront(verts, eye, scratch, new List<(float D, int Q)>(), ref keys);
		AssertThat(Same(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts), input)).OverrideFailureMessage("the batch itself was changed").IsTrue();
		return result;
	}

	/// <summary>Distinct, tied, NaN and remainder batches give the original order, vertex for vertex.</summary>
	[TestCase]
	public void TheFastSortGivesTheOriginalOrder()
	{
		var random = new Random(3);
		ulong[] keys = Array.Empty<ulong>();
		foreach (int quads in new[] { 0, 1, 2, 3, 17, 200, 3000 })
		{
			foreach (int ties in new[] { 0, 1, 2 })
			{
				var verts = new List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>();
				for (int q = 0; q < quads; q++)
				{
					// ties 1: many quads share a centre; ties 2: a NaN centre among them.
					Vector3 c = ties == 1 && q % 3 == 0 ? new Vector3(1f, 2f, 3f)
						: ties == 2 && q == quads / 2 ? new Vector3(float.NaN, 0f, 0f)
						: new Vector3((float)random.NextDouble() * 50f, (float)random.NextDouble() * 10f, (float)random.NextDouble() * 50f);
					for (int k = 0; k < 6; k++)
						verts.Add((c, new Vector2(q, k), new Color(q * 0.001f, k, 0.5f, 1f), new Vector2(k - 3f, q % 5), new Vector4(q, k, 0f, 1f)));
				}

				if (ties == 2 && quads > 0) verts.Add(verts[0]);  // a stray vertex past the last whole quad
				Vector3 eye = new(10f, 1.7f, -4f);
				var expected = OriginalSort(verts, eye);
				var actual = FastSort(verts.ToArray(), eye, ref keys);
				AssertThat(Same(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(expected), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(actual)))
					.OverrideFailureMessage($"{quads} quads, ties {ties}").IsTrue();
			}
		}
	}

	/// <summary>
	/// On assembled batches: an order-dependent blend sorts to the original's order; an
	/// order-independent one, left unsorted, holds the same quads.
	/// </summary>
	[TestCase]
	public void TheSortIsSkippedOnlyWhereOrderCannotShow()
	{
		AssertThat(Engine_FxFastPaths.OrderIndependent("add") && Engine_FxFastPaths.OrderIndependent("addone") &&
			Engine_FxFastPaths.OrderIndependent("disp") && Engine_FxFastPaths.OrderIndependent("sub")).IsTrue();
		AssertThat(Engine_FxFastPaths.OrderIndependent("mix") || Engine_FxFastPaths.OrderIndependent("mixalpha") ||
			Engine_FxFastPaths.OrderIndependent("darken") || Engine_FxFastPaths.OrderIndependent("revsub")).IsFalse();
		FxBank bank = Bank();
		Engine_FxSystem system = Built(bank, ("fastpaths", Vector3.Zero), ("fastpaths_half", new Vector3(3f, 0f, -2f)));
		Step(system, 40);
		ulong[] keys = Array.Empty<ulong>();
		var modes = new HashSet<string>();
		var meshes = new Dictionary<string, Engine_FxSystem.MeshInstanceData>();
		foreach (Vector3 eye in Eyes)
		{
			system.Assemble(eye, Vector3.Right, Vector3.Up, meshes, true, true);
			foreach ((string key, Batch batch) in Snapshot(system))
			{
				modes.Add(batch.Mode);
				var expected = OriginalSort(batch.Vertices, eye);
				if (!Engine_FxFastPaths.OrderIndependent(batch.Mode))
				{
					var actual = FastSort(batch.Vertices, eye, ref keys);
					AssertThat(Same(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(expected), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(actual)))
						.OverrideFailureMessage(key).IsTrue();
					continue;
				}

				static string Quad(ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> v) =>
					string.Join(";", v.ToArray().Select(x => $"{x.Pos}{x.Uv}{x.Col}{x.Off}{x.Motion}"));
				var unsorted = Enumerable.Range(0, batch.Vertices.Length / 6).Select(q => Quad(batch.Vertices.AsSpan(q * 6, 6))).OrderBy(x => x, StringComparer.Ordinal);
				var sorted = Enumerable.Range(0, expected.Count / 6).Select(q => Quad(expected.ToArray().AsSpan(q * 6, 6))).OrderBy(x => x, StringComparer.Ordinal);
				AssertThat(unsorted.SequenceEqual(sorted)).OverrideFailureMessage(key).IsTrue();
			}
		}

		AssertThat(modes.Contains("add") && modes.Contains("mix") && modes.Contains("sub") && modes.Contains("revsub")).IsTrue();
	}

	/// <summary>
	/// The box is the exact extent of the points (the original's Expand chain drifts from it by
	/// rounding at most), the reach is the original's float exactly, across surfaces and batches.
	/// </summary>
	[TestCase]
	public void TheBoundsAreTheExactBoxAndTheSameReach()
	{
		FxBank bank = Bank();
		Engine_FxSystem system = Built(bank, ("fastpaths", Vector3.Zero), ("fastpaths_half", new Vector3(3f, 0f, -2f)), ("fastpaths", new Vector3(0f, 0f, 38f)));
		Step(system, 50);
		var meshes = new Dictionary<string, Engine_FxSystem.MeshInstanceData>();
		system.Assemble(Eyes[0], Vector3.Right, Vector3.Up, meshes, true, false);
		var batches = Snapshot(system).Values.ToList();
		AssertThat(batches.Count).IsGreater(3);
		Aabb original = default; bool originalHas = false; float originalReach = 0f;
		Vector3 lo = default, hi = default; bool has = false; float reach = 0f;
		int classes = 0, expectedClasses = 0;
		foreach (Batch batch in batches)
		{
			foreach (var v in batch.Vertices)
			{
				if (!originalHas) { original = new Aabb(v.Pos, Vector3.Zero); originalHas = true; }
				else original = original.Expand(v.Pos);
				float r = v.Off.Length();
				if (r > originalReach) originalReach = r;
				if (v.Motion.X != 0f || v.Motion.Y != 0f || v.Motion.Z != 0f) expectedClasses |= v.Motion.W > 0.5f ? 2 : 1;
			}

			Engine_FxFastPaths.Bounds(batch.Vertices, ref lo, ref hi, ref has, ref reach, ref classes);
			AssertThat(has == originalHas && Bits(reach, originalReach)).IsTrue();
		}

		foreach (Batch batch in batches)
		{
			foreach (var v in batch.Vertices)
				AssertThat(v.Pos.X >= lo.X && v.Pos.Y >= lo.Y && v.Pos.Z >= lo.Z && v.Pos.X <= hi.X && v.Pos.Y <= hi.Y && v.Pos.Z <= hi.Z).IsTrue();
		}

		// In Modern mode, all emitters step at full 30 Hz (Step = 1) while preserving AuthoredStep
		// for per-second drag and spread, so moving vertices belong to rate class 1 (full-rate).
		AssertThat(classes).IsEqual(expectedClasses);
		AssertThat(classes).IsEqual(1);

		var box = new Aabb(lo, hi - lo);
		AssertThat((box.Position - original.Position).Length() < 1e-4f && (box.End - original.End).Length() < 1e-4f)
			.OverrideFailureMessage($"{box} vs {original}").IsTrue();

		// Empty batches leave the box unseeded; a batch is read without allocating.
		Vector3 l2 = default, h2 = default; bool has2 = false; float reach2 = 0f; int c2 = 0;
		Engine_FxFastPaths.Bounds(ReadOnlySpan<(Vector3, Vector2, Color, Vector2, Vector4)>.Empty, ref l2, ref h2, ref has2, ref reach2, ref c2);
		AssertThat(has2 || c2 != 0).IsFalse();
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 20; i++) Engine_FxFastPaths.Bounds(batches[0].Vertices, ref l2, ref h2, ref has2, ref reach2, ref c2);
		AssertThat(GC.GetAllocatedBytesForCurrentThread() - allocated).IsEqual(0L);
	}

	/// <summary>A still vertex reads neither uniform; a moving one the uniform of its rate class.</summary>
	[TestCase]
	public void TheMotionClassesNameTheUniformsRead()
	{
		var still = (Vector3.One, Vector2.Zero, Colors.White, Vector2.One, new Vector4(0f, 0f, 0f, 1f));
		var full = (Vector3.One, Vector2.Zero, Colors.White, Vector2.One, new Vector4(0.1f, 0f, 0f, 0f));
		var half = (Vector3.One, Vector2.Zero, Colors.White, Vector2.One, new Vector4(0f, -0.2f, 0f, 1f));
		var odd = (Vector3.One, Vector2.Zero, Colors.White, Vector2.One, new Vector4(0f, 0f, 0.3f, 0.5f));
		foreach (((Vector3, Vector2, Color, Vector2, Vector4)[] batch, int expected) in new[]
		{
			(new[] { still, still }, 0), (new[] { still, full }, 1), (new[] { half }, 2), (new[] { full, half }, 3), (new[] { odd }, 3),
		})
		{
			Vector3 lo = default, hi = default; bool has = false; float reach = 0f; int classes = 0;
			Engine_FxFastPaths.Bounds(batch, ref lo, ref hi, ref has, ref reach, ref classes);
			AssertThat(classes).IsEqual(expected);
		}
	}

	/// <summary>The MultiMesh buffer holds each instance as the per-instance setters store it, the undrawn tail a copy of the first.</summary>
	[TestCase]
	public void TheMeshBufferIsTheServersLayout()
	{
		var basis = new Basis(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector3(7f, 8f, 9f));
		var t = new Transform3D(basis, new Vector3(10f, 11f, 12f));
		float[] buffer = new float[3 * Engine_FxFastPaths.MeshInstanceStride];
		Engine_FxFastPaths.WriteInstance(buffer, 0, t, new Color(0.1f, 0.2f, 0.3f, 0.4f));
		Engine_FxFastPaths.FillTail(buffer, 1);
		// The documented order: basis.x.x, basis.y.x, basis.z.x, origin.x, then .y, then .z; then RGBA.
		float[] expected = { 1f, 4f, 7f, 10f, 2f, 5f, 8f, 11f, 3f, 6f, 9f, 12f, 0.1f, 0.2f, 0.3f, 0.4f };
		for (int i = 0; i < 3; i++)
			AssertThat(buffer.AsSpan(i * 16, 16).SequenceEqual(expected)).IsTrue();
		Engine_FxFastPaths.WriteTransform(buffer, 1, Transform3D.Identity);
		AssertThat(buffer.AsSpan(16, 16).SequenceEqual(new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0.1f, 0.2f, 0.3f, 0.4f })).IsTrue();
	}

	/// <summary>Reads leave state alone and invalid values are rejected.</summary>
	[TestCase]
	public void TheDebugLeverValidates()
	{
		bool before = Engine_FxFastPaths.Enabled;
		try
		{
			using var empty = JsonDocument.Parse("{}");
			AssertThat(Engine_FxFastPaths.ReadRequest(empty.RootElement) is null).IsTrue();
			using var on = JsonDocument.Parse("{\"fxFastPaths\":true}");
			AssertThat(Engine_FxFastPaths.ReadRequest(on.RootElement)).IsEqual(true);
			using var invalid = JsonDocument.Parse("{\"fxFastPaths\":1}");
			bool rejected = false;
			try { Engine_FxFastPaths.ReadRequest(invalid.RootElement); } catch (ArgumentException) { rejected = true; }
			AssertThat(rejected).IsTrue();
			Engine_FxFastPaths.Configure(new Dictionary<string, string>());
			AssertThat(Engine_FxFastPaths.Enabled).IsFalse();
			Engine_FxFastPaths.Configure(new Dictionary<string, string> { [Engine_FxFastPaths.Key] = "true" });
			AssertThat(Engine_FxFastPaths.Enabled).IsTrue();
		}
		finally { Engine_FxFastPaths.Enabled = before; }
	}
}
