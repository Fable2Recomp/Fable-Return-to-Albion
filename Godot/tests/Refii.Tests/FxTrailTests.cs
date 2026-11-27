using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class FxTrailTests
{
	private static FxBank MakeBank(
		uint mode,
		float widthStart,
		float widthEnd,
		int trailLen,
		float delaySeconds = 0.3f,
		string texture = "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex",
		uint updateFrequency = 1u)
	{
		var emitter = new FxEmitter
		{
			TypeId = 0,
			SubType = 0,
			MaxActive = -1,
			InitialNum = 1f,
			AttachToRoot = false,
			Fades = new[] { 0f, 0f, 100f, 120f },
		};
		for (int i = 0; i < 28; i++)
		{
			emitter.Generic[i] = Engine_FxBank.MakeTimeline(i, i switch
			{
				2 => new[] { (0f, 1f) },
				11 => new[] { (0f, 1f) },
				_ => Array.Empty<(float, float)>(),
			});
		}
		emitter.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		emitter.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
		emitter.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		emitter.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(emitter);

		var system = new FxSystemDef
		{
			Mode = mode,
			ChildCount = 2,
			LoopCount = 0,
			LastStartFrames = (int)MathF.Round(delaySeconds * 30f),
		};
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = texture,
			Delay = 0f,
			DelayFrames = 0,
			InvSpan = 0f,
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			BlendOp = 0,
			HasTrailWidth = true,
			TrailWidth = widthStart,
			HasTrailLen = true,
			TrailLen = trailLen,
		});
		int endFrame = Math.Max(1, (int)MathF.Round(delaySeconds * 30f));
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = texture,
			Delay = delaySeconds,
			DelayFrames = endFrame,
			InvSpan = 1f / endFrame,
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 0.2f,
			SrcBlend = 1,
			DstBlend = 1,
			BlendOp = 0,
			HasTrailWidth = true,
			TrailWidth = widthEnd,
			HasTrailLen = true,
			TrailLen = trailLen,
		});

		var effect = new FxEffect { NameHash = FxBank.Fnv1Lower("test_trail"), Name = "test_trail", UpdateFrequency = updateFrequency };
		effect.Parts.Add(new FxEffectPart(0, 0, 1u, 0));

		var bank = new FxBank { Ok = true };
		bank.Emitters.Add(emitter);
		bank.Systems.Add(system);
		bank.Effects.Add(effect);
		return bank;
	}

	private static Engine_FxSystem Build(FxBank bank)
	{
		var sys = new Engine_FxSystem();
		sys.Build(bank, new[] { new FxPlacement { EffectName = bank.Effects[0].Name, Pos = new[] { 0f, 0f, 0f } } });
		return sys;
	}

	[TestCase]
	public void Mode2Ribbon_UsesGuestUvOrientation_AndTapersWidth()
	{
		FxBank bank = MakeBank(mode: 2u, widthStart: 2.0f, widthEnd: 0.2f, trailLen: 2, delaySeconds: 0.2f);
		Engine_FxSystem sys = Build(bank);

		// Move along +X across 5 ticks so the ribbon has 5 distinct control points.
		for (int i = 0; i < 5; i++)
		{
			var xform = new Transform3D(Basis.Identity, new Vector3(i * 0.5f, 0f, 0f));
			sys.Update(1f / 30f, xform);
		}

		sys.Assemble(new Vector3(1f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);

		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(0);

		// Guest contract: UV.x in {0, 1} across width, UV.y in [0, 1] along trail length.
		foreach (var v in batch.Vertices)
		{
			AssertThat(v.Uv.X == 0f || v.Uv.X == 1f).IsTrue();
			AssertThat(v.Uv.Y).IsBetween(0f, 1f);
		}

		// Head (v == 0, oldest age or newest? Let's verify width at v == 0 vs v == 1).
		// Older particles (index 0, v == 0) have larger age -> tapered width; newest (v == 1) have widthStart=2.0.
		var headSide0 = batch.Vertices.First(v => MathF.Abs(v.Uv.Y) < 1e-4f && v.Uv.X == 0f).Pos;
		var headSide1 = batch.Vertices.First(v => MathF.Abs(v.Uv.Y) < 1e-4f && v.Uv.X == 1f).Pos;
		var tailSide0 = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var tailSide1 = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;

		float olderWidth = headSide0.DistanceTo(headSide1);
		float newerWidth = tailSide0.DistanceTo(tailSide1);
		AssertThat(newerWidth).IsEqualApprox(2.0f, 0.05f);
		AssertThat(olderWidth).IsLess(newerWidth * 0.6f);
	}

	[TestCase]
	public void Mode3DoubleTrail_WithWeaponSpan_SpansEndpointsAndScalesFromMidpoint()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.0f, trailLen: 2, delaySeconds: 0.2f);
		Engine_FxSystem sys = Build(bank);
		AssertThat(sys.HasDoubleTrail).IsTrue();

		Vector3 localHold = new(0f, 0f, 0.135f);
		Vector3 localTop = new(0f, 0f, -0.847f);
		float fullBladeLen = localHold.DistanceTo(localTop);

		// Swing the weapon around Y across 6 ticks while feeding node-local Hold/Top span.
		for (int i = 0; i < 6; i++)
		{
			var basis = new Basis(Vector3.Up, i * 0.2f);
			var xform = new Transform3D(basis, new Vector3(0f, 1f, 0f));
			sys.SetDoubleTrailSpan(localHold, localTop);
			sys.Update(1f / 30f, xform);
		}

		sys.Assemble(new Vector3(0f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);

		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(0);

		foreach (var v in batch.Vertices)
		{
			AssertThat(v.Uv.X == 0f || v.Uv.X == 1f).IsTrue();
			AssertThat(v.Uv.Y).IsBetween(0f, 1f);
		}

		// Newest edge (v == 1) should span the full weapon blade (~0.982m) at Hold/Top.
		var newestA = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var newestB = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(newestA.DistanceTo(localHold)).IsLess(0.01f);
		AssertThat(newestB.DistanceTo(localTop)).IsLess(0.01f);
		AssertThat(newestA.DistanceTo(newestB)).IsEqualApprox(fullBladeLen, 0.01f);

		// Oldest edge (v == 0, age ~5 frames of 6) should taper strongly toward midpoint.
		var oldestA = batch.Vertices.First(v => MathF.Abs(v.Uv.Y) < 1e-4f && v.Uv.X == 0f).Pos;
		var oldestB = batch.Vertices.First(v => MathF.Abs(v.Uv.Y) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(oldestA.DistanceTo(oldestB)).IsLess(fullBladeLen * 0.35f);
	}

	[TestCase]
	public void Mode3DoubleTrail_WithoutWeaponSpan_EmitsFallbackRibbon()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.5f, trailLen: 2, delaySeconds: 0.3f);
		Engine_FxSystem sys = Build(bank);

		for (int i = 0; i < 4; i++)
		{
			var xform = new Transform3D(Basis.Identity, new Vector3(MathF.Cos(i * 0.4f), 1f, MathF.Sin(i * 0.4f)));
			sys.Update(1f / 30f, xform);
		}

		sys.Assemble(new Vector3(0f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);

		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(0);
		var newestA = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var newestB = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(newestA.DistanceTo(newestB)).IsEqualApprox(1.0f, 0.05f);
	}

	[TestCase]
	public void Mode3DoubleTrail_RequireSpan_SuppressesFallbackUntilSpanSet()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.0f, trailLen: 2, delaySeconds: 0.2f);
		Engine_FxSystem sys = Build(bank);
		sys.RequireDoubleTrailSpan = true;

		// Before any span arrives, no vertical fallback ribbon should be spawned or emitted.
		for (int i = 0; i < 3; i++)
		{
			var xform = new Transform3D(Basis.Identity, new Vector3(0f, 1f, i * 0.3f));
			sys.Update(1f / 30f, xform);
		}

		sys.Assemble(new Vector3(0f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		AssertThat(sys.AssembledBatches().Any()).IsFalse();

		// Once SetDoubleTrailSpan provides real endpoints, particles spawn and span them.
		Vector3 edgeA = new(0f, 0f, 0.2f);
		Vector3 edgeB = new(0f, 0f, -0.8f);
		for (int i = 0; i < 4; i++)
		{
			var xform = new Transform3D(new Basis(Vector3.Up, i * 0.2f), new Vector3(0f, 1f, 0f));
			sys.SetDoubleTrailSpan(edgeA, edgeB);
			sys.Update(1f / 30f, xform);
		}

		sys.Assemble(new Vector3(0f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(0);
		var newestA = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var newestB = batch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(newestA.DistanceTo(edgeA)).IsLess(0.01f);
		AssertThat(newestB.DistanceTo(edgeB)).IsLess(0.01f);
	}

	[TestCase]
	public void PerParticleTrail_EmitsOldestAtV0_AndNewestAtV1()
	{
		var emitter = new FxEmitter
		{
			TypeId = 0,
			SubType = 0,
			MaxActive = 1,
			InitialNum = 1f,
			AttachToRoot = false,
			ReleaseTrail = true,
			Fades = new[] { 0f, 0f, 100f, 120f },
		};
		for (int i = 0; i < 28; i++)
		{
			emitter.Generic[i] = Engine_FxBank.MakeTimeline(i, i switch
			{
				2 => new[] { (0f, 0f) },
				6 => new[] { (0f, 30f) },
				12 => new[] { (0f, 1f) },
				13 => new[] { (0f, 1f) },
				_ => Array.Empty<(float, float)>(),
			});
		}
		emitter.Vec0 = Engine_FxBank.MakeVecTimeline(0, new[] { (0f, 1f, 0f, 0f) });
		emitter.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
		emitter.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		emitter.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(emitter);

		var headSystem = new FxSystemDef { Mode = 1u, ChildCount = 1, LoopCount = 0, LastStartFrames = 30 };
		headSystem.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = "art/fx/pfx_textures/fxt_head_spark.tex",
			Delay = 0f,
			DelayFrames = 0,
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			BlendOp = 0,
			HasSize = true,
			SizeX = 0.2f,
			SizeY = 0.2f,
		});

		var trailSystem = new FxSystemDef { Mode = 2u, ChildCount = 2, LoopCount = 0, LastStartFrames = 20 };
		trailSystem.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = "art/fx/pfx_textures/fxt_blue_trail_02.tex",
			Delay = 0f,
			DelayFrames = 0,
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			BlendOp = 0,
			HasTrailWidth = true,
			TrailWidth = 0.5f,
			HasTrailLen = true,
			TrailLen = 1,
		});

		var effect = new FxEffect
		{
			NameHash = FxBank.Fnv1Lower("test_release_trail"),
			Name = "test_release_trail",
			UpdateFrequency = 1,
		};
		effect.Parts.Add(new FxEffectPart(0, 0, 1u, 0, TrailSystem: 1));

		var bank = new FxBank { Ok = true };
		bank.Emitters.Add(emitter);
		bank.Systems.Add(headSystem);
		bank.Systems.Add(trailSystem);
		bank.Effects.Add(effect);

		Engine_FxSystem sys = Build(bank);
		for (int i = 0; i < 6; i++)
		{
			sys.Update(1f / 30f, Transform3D.Identity);
		}

		sys.Assemble(new Vector3(0f, 3f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);

		var trailBatch = sys.AssembledBatches().Single(b => b.Key.Contains("fxt_blue_trail_02.tex", StringComparison.Ordinal));
		AssertThat(trailBatch.Vertices.Count).IsGreater(0);
		float tailX = trailBatch.Vertices.Where(v => MathF.Abs(v.Uv.Y) < 1e-4f).Average(v => v.Pos.X);
		float headX = trailBatch.Vertices.Where(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f).Average(v => v.Pos.X);
		AssertThat(headX).IsGreater(tailX + 0.1f);
	}

	[TestCase]
	public void ShaderClampContract_ModernFxShadersUseRepeatDisable()
	{
		string? dir = AppContext.BaseDirectory;
		string? fxDir = null;
		while (!string.IsNullOrEmpty(dir))
		{
			string a = Path.Combine(dir, "src", "Engine", "fx");
			string b = Path.Combine(dir, "Godot", "src", "Engine", "fx");
			if (Directory.Exists(a)) { fxDir = a; break; }
			if (Directory.Exists(b)) { fxDir = b; break; }
			dir = Path.GetDirectoryName(dir);
		}
		AssertThat(fxDir).IsNotNull();

		string[] shaders =
		{
			"fx_particle.gdshader",
			"fx_particle_add.gdshader",
			"fx_particle_addone.gdshader",
			"fx_particle_mixalpha.gdshader",
			"fx_particle_darken.gdshader",
			"fx_particle_sub.gdshader",
			"fx_particle_disp.gdshader",
		};
		foreach (string file in shaders)
		{
			string src = File.ReadAllText(Path.Combine(fxDir!, file));
			AssertThat(src.Contains("repeat_disable", StringComparison.Ordinal)).IsTrue();
			AssertThat(src.Contains("repeat_enable", StringComparison.Ordinal)).IsFalse();
		}
	}

	[TestCase]
	public void Mode3DoubleTrail_UsesLiveEmitterIntensity_NotStaleBirthIntensity()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.5f, trailLen: 2, delaySeconds: 0.3f,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 0u);
		Engine_FxSystem sys = Build(bank);

		sys.SetIntensity(1.0f);
		for (int i = 1; i <= 3; i++)
		{
			sys.SetDoubleTrailSpan(new Vector3(i * 0.5f, 0f, 0f), new Vector3(i * 0.5f, 0f, 1f),
				hasGuestCursor: true, guestTail: (uint)i, guestCount: (uint)(i + 1));
			sys.Update(1f / 30f, Transform3D.Identity);
		}

		// Dropping emitter intensity to zero must hide existing ribbon vertices immediately.
		sys.SetIntensity(0.0f);
		sys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		AssertThat(sys.AssembledBatches().Any()).IsFalse();

		// Ramping emitter intensity cleanly modulates all emitted ribbon vertices.
		sys.SetIntensity(0.25f);
		sys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(0);
		float maxAlpha = batch.Vertices.Max(v => v.Col.A);
		AssertThat(maxAlpha).IsBetween(0.04f, 0.26f);
	}

	[TestCase]
	public void Mode3DoubleTrail_DeduplicatesUnchangedGuestSamples()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.5f, trailLen: 2, delaySeconds: 0.5f,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 0u);
		Engine_FxSystem sys = Build(bank);

		// First guest sample arrives at tick 0 -> 1 alive particle.
		sys.SetDoubleTrailSpan(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f),
			hasGuestCursor: true, guestTail: 1u, guestCount: 2u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(1);

		// Intermediate 30 Hz tick with unchanged 15 Hz guest cursor must NOT spawn a duplicate.
		sys.SetDoubleTrailSpan(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f),
			hasGuestCursor: true, guestTail: 1u, guestCount: 2u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(1);

		// Advancing guest cursor spawns the second distinct sample.
		sys.SetDoubleTrailSpan(new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 0f, 1f),
			hasGuestCursor: true, guestTail: 2u, guestCount: 3u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(2);
	}

	[TestCase]
	public void Mode3DoubleTrail_FlushesOnTeleportAndRingReset()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.5f, trailLen: 2, delaySeconds: 0.5f,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 0u);
		Engine_FxSystem sys = Build(bank);

		for (int i = 0; i < 3; i++)
		{
			sys.SetDoubleTrailSpan(new Vector3(i * 0.5f, 0f, 0f), new Vector3(i * 0.5f, 0f, 1f),
				hasGuestCursor: true, guestTail: (uint)(i + 1), guestCount: (uint)(i + 2));
			sys.Update(1f / 30f, Transform3D.Identity);
		}
		AssertThat(sys.AliveCount()).IsEqual(3);

		// Teleporting > 4.5m across the scene flushes stale points so no bridge quad forms.
		sys.SetDoubleTrailSpan(new Vector3(15f, 0f, 0f), new Vector3(15f, 0f, 1f),
			hasGuestCursor: true, guestTail: 4u, guestCount: 5u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(1);

		// Build 2 points at the new location, then shrink guestCount to simulate a ring reset.
		sys.SetDoubleTrailSpan(new Vector3(15.5f, 0f, 0f), new Vector3(15.5f, 0f, 1f),
			hasGuestCursor: true, guestTail: 5u, guestCount: 6u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(2);

		sys.SetDoubleTrailSpan(new Vector3(16.0f, 0f, 0f), new Vector3(16.0f, 0f, 1f),
			hasGuestCursor: true, guestTail: 1u, guestCount: 2u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(1);
	}

	[TestCase]
	public void Mode3DoubleTrail_SetsNonZeroHeadMotion_AndLiveWeaponTipLocksToBlade()
	{
		// 1. 15 Hz spell circle: newly spawned head inherits previous particle's position -> non-zero CUSTOM0 at full step class (w=0).
		FxBank spellBank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.5f, trailLen: 2, delaySeconds: 0.5f,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 0u);
		Engine_FxSystem spellSys = Build(spellBank);
		AssertThat(spellSys.IsWeaponDoubleTrail).IsFalse();

		spellSys.SetDoubleTrailSpan(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f),
			hasGuestCursor: true, guestTail: 1u, guestCount: 2u);
		spellSys.Update(1f / 30f, Transform3D.Identity);
		spellSys.SetDoubleTrailSpan(new Vector3(0.8f, 0f, 0f), new Vector3(0.8f, 0f, 1f),
			hasGuestCursor: true, guestTail: 2u, guestCount: 3u);
		spellSys.Update(1f / 30f, Transform3D.Identity);

		spellSys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var spellBatch = spellSys.AssembledBatches().Single();
		var headVert = spellBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f);
		AssertThat(headVert.Motion.X).IsEqualApprox(0.8f, 0.01f);
		AssertThat(headVert.Motion.W).IsEqualApprox(0.0f, 0.01f);

		// 2. Weapon trail: live display-rate tip locks onto the drawn blade even before next tick.
		FxBank weaponBank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.0f, trailLen: 1, delaySeconds: 0.3f);
		Engine_FxSystem weaponSys = Build(weaponBank);
		AssertThat(weaponSys.IsWeaponDoubleTrail).IsTrue();

		weaponSys.SetDoubleTrailSpan(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f));
		weaponSys.Update(1f / 30f, Transform3D.Identity);
		// Display frame moves the weapon blade without advancing a sim tick.
		Vector3 liveHold = new(0.4f, 0f, 0f);
		Vector3 liveTop = new(0.4f, 0f, 1f);
		weaponSys.SetDoubleTrailSpan(liveHold, liveTop);
		AssertThat(weaponSys.ConsumeLiveWeaponTipDirty()).IsTrue();

		weaponSys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var weaponBatch = weaponSys.AssembledBatches().Single();
		var tipA = weaponBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var tipB = weaponBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(tipA.DistanceTo(liveHold)).IsLess(0.01f);
		AssertThat(tipB.DistanceTo(liveTop)).IsLess(0.01f);
	}

	[TestCase]
	public void Mode2SingleTrail_GuestDrivenHead_TracksStatedPositionAndFlushesOnJump()
	{
		FxBank bank = MakeBank(mode: 2u, widthStart: 0.4f, widthEnd: 0.2f, trailLen: 2, delaySeconds: 0.5f,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 1u);
		Engine_FxSystem sys = Build(bank);
		sys.RequireSingleTrailHead = true;

		// Before guest states a head point, no bogus line spawns at the origin.
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(0);

		// Two stated head samples along X=8m perimeter.
		sys.SetSingleTrailHead(new Vector3(8f, 0f, 0f), hasGuestCursor: true, guestTail: 1u, guestCount: 2u);
		sys.Update(1f / 30f, Transform3D.Identity);
		sys.SetSingleTrailHead(new Vector3(8f, 0f, 0.8f), hasGuestCursor: true, guestTail: 2u, guestCount: 3u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(2);

		sys.Assemble(new Vector3(8f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.All(v => v.Pos.X > 7.5f)).IsTrue();

		// Jumping across the hero (> 3.5m) flushes stale perimeter points.
		sys.SetSingleTrailHead(new Vector3(-8f, 0f, 0f), hasGuestCursor: true, guestTail: 3u, guestCount: 4u);
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(1);
	}

	[TestCase]
	public void IsWeaponDoubleTrail_DistinguishesWeaponFromSpellCircle()
	{
		FxBank weapon = MakeBank(mode: 3u, widthStart: 1f, widthEnd: 0f, trailLen: 2,
			texture: "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex");
		FxBank spell = MakeBank(mode: 3u, widthStart: 1f, widthEnd: 0.5f, trailLen: 5,
			texture: "art/fx/pfx_textures/fxt_spell_circle_trail.tex", updateFrequency: 0u);

		Engine_FxSystem weaponSys = Build(weapon);
		Engine_FxSystem spellSys = Build(spell);

		AssertThat(weaponSys.HasRibbon).IsTrue();
		AssertThat(weaponSys.IsWeaponDoubleTrail).IsTrue();
		AssertThat(spellSys.HasRibbon).IsTrue();
		AssertThat(spellSys.IsWeaponDoubleTrail).IsFalse();
	}

	[TestCase]
	public void WeaponDoubleTrail_ZeroShaderMotionPullback_AndPinsLeadingEdgeOnTickAndSubframe()
	{
		FxBank weaponBank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 0.0f, trailLen: 2, delaySeconds: 0.3f);
		Engine_FxSystem sys = Build(weaponBank);
		AssertThat(sys.IsWeaponDoubleTrail).IsTrue();

		// Fast flourish swing across 4 simulation ticks (0.8m per tick = 24 m/s tip velocity).
		Vector3 lastHold = Vector3.Zero;
		Vector3 lastTop = Vector3.Zero;
		for (int i = 0; i < 4; i++)
		{
			lastHold = new Vector3(i * 0.4f, 0f, 0f);
			lastTop = new Vector3(i * 0.8f, 0f, 1f);
			sys.SetDoubleTrailSpan(lastHold, lastTop);
			sys.Update(1f / 30f, Transform3D.Identity);
		}

		// Right on a 30 Hz tick frame (StepAlpha == 0), every breadcrumb must carry zero shader motion
		// (CUSTOM0.xyz == 0) so the vertex shader never pulls the leading edge 33ms behind the sword.
		sys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var tickBatch = sys.AssembledBatches().Single();
		foreach (var v in tickBatch.Vertices)
		{
			AssertThat(v.Motion.X).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Y).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Z).IsEqualApprox(0f, 1e-5f);
		}

		var tickTipA = tickBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var tickTipB = tickBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(tickTipA.DistanceTo(lastHold)).IsLess(0.01f);
		AssertThat(tickTipB.DistanceTo(lastTop)).IsLess(0.01f);

		// Intermediate 120 Hz display frame advances the drawn sword without ticking simulation.
		Vector3 subHold = new(1.45f, 0f, 0.05f);
		Vector3 subTop = new(2.9f, 0f, 1.05f);
		sys.SetDoubleTrailSpan(subHold, subTop);
		sys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var subBatch = sys.AssembledBatches().Single();
		foreach (var v in subBatch.Vertices)
		{
			AssertThat(v.Motion.X).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Y).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Z).IsEqualApprox(0f, 1e-5f);
		}

		var subTipA = subBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 0f).Pos;
		var subTipB = subBatch.Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-4f && v.Uv.X == 1f).Pos;
		AssertThat(subTipA.DistanceTo(subHold)).IsLess(0.01f);
		AssertThat(subTipB.DistanceTo(subTop)).IsLess(0.01f);
	}

	[TestCase]
	public void Type9BeamEmitter_RequireBeamSpline_SuppressesFeetBar_AndSustains324PointCircle()
	{
		uint beamEntityId = 0x521C9F01u;
		var emitter = new FxEmitter
		{
			TypeId = 9,
			EntityId = beamEntityId,
			HasMotion = true,
			MaxActive = -1,
			InitialNum = 1f,
			UseLife = true,
			LifetimeFrames = 20,
			NumSplineSegments = 5,
			AnimationMode = 1,
			RandomiseAnimMagnitude = 0.25f,
			JerkyAnimationRate = 0.03f,
			AttachToRoot = false,
			Fades = new[] { 0f, 0f, 100f, 120f },
		};
		for (int i = 0; i < 28; i++)
		{
			emitter.Generic[i] = Engine_FxBank.MakeTimeline(i, Array.Empty<(float, float)>());
		}
		emitter.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		emitter.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
		emitter.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		emitter.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Engine_FxBank.LinkParameters(emitter);

		var system = new FxSystemDef
		{
			Mode = 2u,
			ChildCount = 2,
			LoopCount = 0,
			LastStartFrames = 20,
		};
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = "art/fx/pfx_textures/fxt_spell_circle_beam.tex",
			Delay = 0f,
			DelayFrames = 0,
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			BlendOp = 0,
			HasTrailWidth = true,
			TrailWidth = 0.4f,
			HasTrailLen = true,
			TrailLen = 1,
		});

		var effect = new FxEffect { NameHash = FxBank.Fnv1Lower("test_dad44f56"), Name = "test_dad44f56", UpdateFrequency = 1u };
		effect.Parts.Add(new FxEffectPart(0, 0, 1u, 0));

		var bank = new FxBank { Ok = true };
		bank.Emitters.Add(emitter);
		bank.Systems.Add(system);
		bank.Effects.Add(effect);

		Engine_FxSystem sys = Build(bank);
		sys.RequireBeamSpline = true;
		AssertThat(sys.HasBeam).IsTrue();
		AssertThat(sys.BeamEmitterCount).IsEqual(1);

		// Without a stated guest spline (`*(node + 0x1B0) == 0`), NO 3-metre bar may appear at the Hero's feet.
		for (int i = 0; i < 4; i++)
		{
			sys.Update(1f / 30f, Transform3D.Identity);
		}
		AssertThat(sys.AliveCount()).IsEqual(0);
		sys.Assemble(new Vector3(0f, 5f, 5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		AssertThat(sys.AssembledBatches().Any()).IsFalse();

		// Feed a 324-point closed max-range circle (`first == last`) at radius 11m around Z = -12m.
		var circle = new Vector3[324];
		Vector3 center = new(0f, 0f, -12f);
		for (int i = 0; i < circle.Length; i++)
		{
			float a = (i == circle.Length - 1) ? 0f : (MathF.Tau * i / (circle.Length - 1));
			circle[i] = center + new Vector3(MathF.Cos(a) * 11f, 0f, MathF.Sin(a) * 11f);
		}

		AssertThat(sys.SetBeamSplineForType(beamEntityId, 0, circle)).IsEqual(0);
		AssertThat(sys.ConsumeBeamDirty()).IsTrue();

		// Step 60 frames (3x past authored LifetimeFrames=20) to verify the held circle never disappears at max range.
		for (int i = 0; i < 60; i++)
		{
			sys.Update(1f / 30f, Transform3D.Identity);
		}
		AssertThat(sys.AliveCount()).IsEqual(324);
		AssertThat(sys.ActiveBoundRadius).IsGreater(20f);

		sys.Assemble(new Vector3(0f, 8f, 0f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Count).IsGreater(300 * 6);

		// Verify closed-loop BeamSegLen (> 0 even with first == last) produced real animation jitter and kept ring around r=11m.
		bool sawJitter = false;
		foreach (var v in batch.Vertices)
		{
			float radial = new Vector2(v.Pos.X - center.X, v.Pos.Z - center.Z).Length();
			AssertThat(radial).IsBetween(10.2f, 11.8f);
			if (MathF.Abs(v.Pos.Y) > 1e-4f)
			{
				sawJitter = true;
			}
		}
		AssertThat(sawJitter).IsTrue();

		// Clearing the beam spline immediately extinguishes the beam on the next tick.
		sys.ClearBeamSplines();
		sys.Update(1f / 30f, Transform3D.Identity);
		AssertThat(sys.AliveCount()).IsEqual(0);
	}

	[TestCase]
	public void Type9BeamEmitter_MultipleBeamsPerHandle_TrackDistinctSplines()
	{
		uint beamTypeId = 0x11223344u;
		FxEmitter MakeBeamEmitter()
		{
			var em = new FxEmitter
			{
				TypeId = 9,
				EntityId = beamTypeId,
				HasMotion = true,
				MaxActive = -1,
				InitialNum = 1f,
				NumSplineSegments = 4,
				AnimationMode = 0,
				AttachToRoot = false,
				Fades = new[] { 0f, 0f, 100f, 120f },
			};
			for (int i = 0; i < 28; i++)
			{
				em.Generic[i] = Engine_FxBank.MakeTimeline(i, Array.Empty<(float, float)>());
			}
			em.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
			em.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
			em.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
			em.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
			Engine_FxBank.LinkParameters(em);
			return em;
		}

		var system = new FxSystemDef { Mode = 2u, ChildCount = 2, LoopCount = 0, LastStartFrames = 30 };
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = "art/fx/pfx_textures/fxt_lightning_arc.tex",
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			HasTrailWidth = true,
			TrailWidth = 0.25f,
			HasTrailLen = true,
			TrailLen = 1,
		});

		var effect = new FxEffect { NameHash = FxBank.Fnv1Lower("test_04d80053"), Name = "test_04d80053", UpdateFrequency = 1u };
		effect.Parts.Add(new FxEffectPart(0, 0, 1u, Vector: 0));
		effect.Parts.Add(new FxEffectPart(1, 0, 1u, Vector: 1));

		var bank = new FxBank { Ok = true };
		bank.Emitters.Add(MakeBeamEmitter());
		bank.Emitters.Add(MakeBeamEmitter());
		bank.Systems.Add(system);
		bank.Effects.Add(effect);

		Engine_FxSystem sys = Build(bank);
		sys.RequireBeamSpline = true;
		AssertThat(sys.BeamEmitterCount).IsEqual(2);

		sys.SetBeamSplineForType(beamTypeId, 0, new[] { new Vector3(-5f, 0f, 0f), new Vector3(-5f, 2f, 0f) });
		sys.SetBeamSplineForType(beamTypeId, 1, new[] { new Vector3(5f, 0f, 0f), new Vector3(5f, 2f, 0f) });
		sys.Update(1f / 30f, Transform3D.Identity);

		sys.Assemble(new Vector3(0f, 3f, 8f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();
		AssertThat(batch.Vertices.Any(v => v.Pos.X < -4.5f)).IsTrue();
		AssertThat(batch.Vertices.Any(v => v.Pos.X > 4.5f)).IsTrue();
	}

	[TestCase]
	public void MultiRootChains_PlaceDistinctTypeVectorsAlongSpellPath()
	{
		FxEmitter MakeChainEmitter()
		{
			var em = new FxEmitter
			{
				TypeId = 0,
				SubType = 0,
				HasMotion = true,
				MaxActive = 4,
				InitialNum = 1f,
				AttachToRoot = false,
				Fades = new[] { 0f, 0f, 100f, 120f },
			};
			for (int i = 0; i < 28; i++)
			{
				em.Generic[i] = Engine_FxBank.MakeTimeline(i, i == 2 ? new[] { (0f, 1f) } : Array.Empty<(float, float)>());
			}
			em.Vec0 = Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
			em.Vec1 = Engine_FxBank.MakeVecTimeline(1, new[] { (0f, 1f, 1f, 1f) });
			em.Vec2 = Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
			em.LightColour = Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
			Engine_FxBank.LinkParameters(em);
			return em;
		}

		var system = new FxSystemDef { Mode = 1u, ChildCount = 2, LoopCount = 0, LastStartFrames = 30 };
		system.Visuals.Add(new FxVisual
		{
			HasTexture = true,
			Texture = "art/fx/pfx_textures/fxt_shock_spark.tex",
			HasColor = true,
			Color = new[] { 1f, 1f, 1f },
			ColorSq = new[] { 1f, 1f, 1f },
			HasAlpha = true,
			Alpha = 1f,
			SrcBlend = 1,
			DstBlend = 1,
			HasSize = true,
			SizeX = 0.3f,
			SizeY = 0.3f,
		});

		var effect = new FxEffect { NameHash = FxBank.Fnv1Lower("test_ae98bf4d"), Name = "test_ae98bf4d", UpdateFrequency = 1u };
		effect.Parts.Add(new FxEffectPart(0, 0, 1u, Vector: 0));
		effect.Parts.Add(new FxEffectPart(1, 0, 1u, Vector: 1));
		effect.Parts.Add(new FxEffectPart(2, 0, 1u, Vector: 2));

		var bank = new FxBank { Ok = true };
		bank.Emitters.Add(MakeChainEmitter());
		bank.Emitters.Add(MakeChainEmitter());
		bank.Emitters.Add(MakeChainEmitter());
		bank.Systems.Add(system);
		bank.Effects.Add(effect);

		Engine_FxSystem sys = Build(bank);
		sys.SetChainRoots(new[]
		{
			new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f)),
			new Transform3D(Basis.Identity, new Vector3(0f, 0f, -10f)),
			new Transform3D(Basis.Identity, new Vector3(0f, 0f, -20f)),
		});
		sys.Update(1f / 30f, Transform3D.Identity);

		AssertThat(sys.ActiveBoundRadius).IsGreater(20f);
		List<Vector3> positions = sys.ParticlePositions().ToList();
		AssertThat(positions.Any(p => MathF.Abs(p.Z - 0f) < 0.2f)).IsTrue();
		AssertThat(positions.Any(p => MathF.Abs(p.Z - (-10f)) < 0.2f)).IsTrue();
		AssertThat(positions.Any(p => MathF.Abs(p.Z - (-20f)) < 0.2f)).IsTrue();
	}

	[TestCase]
	public void WeaponTrailMatching_ToleratesFastFlourishSpin_AndRejectsGroundCircles()
	{
		Vector3 swordHold = new(0f, 1.1f, 0f);
		Vector3 swordTop = new(0f, 1.1f, -1.0f);

		// Fast 360-degree flourish: hilt shifted 0.65m and tip swept 1.35m between 15 Hz ring and 30 Hz pose.
		Vector3 flourishA = new(0.55f, 1.05f, -0.15f);
		Vector3 flourishB = new(1.15f, 1.05f, -0.75f);
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			swordHold, swordTop, flourishA, flourishB, out bool swapped, out _)).IsTrue();
		AssertThat(swapped).IsFalse();

		// Reversed Hold/Top endpoint order on the sword still matches with swapped == true.
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			swordHold, swordTop, flourishB, flourishA, out bool revSwapped, out _)).IsTrue();
		AssertThat(revSwapped).IsTrue();

		// Ground magic circle segment 3.5m away or 4.5m wide must never match a hand weapon.
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			swordHold, swordTop, new Vector3(3.5f, 0f, 0f), new Vector3(3.5f, 0f, -1.0f), out _, out _)).IsFalse();
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			swordHold, swordTop, new Vector3(0f, 0.8f, 0f), new Vector3(0f, 0.8f, -4.5f), out _, out _)).IsFalse();

		// Hovering Blades spell Damocles corkscrew span (0.28m wide, ~0.85m from Hero) must never
		// false-latch onto a drawn sword (1.0m) or short cutlass/dagger (0.55m).
		Vector3 damoclesA = new(0.45f, 1.15f, -0.20f);
		Vector3 damoclesB = new(0.45f, 1.15f, -0.48f);
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			swordHold, swordTop, damoclesA, damoclesB, out _, out _)).IsFalse();
		Vector3 daggerHold = new(0f, 1.1f, 0f);
		Vector3 daggerTop = new(0f, 1.1f, -0.55f);
		AssertThat(Refii.Game.GameEntities.IsCompatibleWeaponTrailMatch(
			daggerHold, daggerTop, damoclesA, damoclesB, out _, out _)).IsFalse();
	}

	[TestCase]
	public void UnlatchedWeaponDoubleTrail_BladesProjectile_DeduplicatesGuestCursor_AndInterpolatesSubStepMotion()
	{
		// `FX_B2848F7C` uses `fxt_weapon_trail_flourish_01.tex` (`IsWeaponDoubleTrail == true`),
		// but when fired by the Blades spell (`owner == 0, attached == 0`) it is driven by the
		// guest's trail ring (`hasGuestCursor: true`).
		FxBank bank = MakeBank(
			mode: 3u,
			widthStart: 1.0f,
			widthEnd: 1.0f,
			trailLen: 1,
			delaySeconds: 0.4f,
			texture: "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex");
		Engine_FxSystem sys = Build(bank);
		AssertThat(sys.IsWeaponDoubleTrail).IsTrue();

		// Push 3 corkscrew spans 1.34m apart along -Z, repeating each sample on an off-step to
		// verify guest-cursor deduplication even on `IsWeaponDoubleTrail`.
		for (int step = 0; step < 3; step++)
		{
			float z = -step * 1.34f;
			float angle = step * 1.1f;
			Vector3 mid = new(0f, 1.2f, z);
			Vector3 halfSpan = new(MathF.Cos(angle) * 0.14f, MathF.Sin(angle) * 0.14f, 0f);
			Vector3 edgeA = mid - halfSpan;
			Vector3 edgeB = mid + halfSpan;
			uint count = (uint)(step + 2);
			uint tail = (uint)((step + 2) & 7);

			sys.SetDoubleTrailSpan(edgeA, edgeB, hasGuestCursor: true, guestTail: tail, guestCount: count);
			AssertThat(sys.IsDoubleTrailHostDriven).IsFalse();
			sys.Update(1f / 30f, Transform3D.Identity);

			if (step < 2)
			{
				// Intermediate 30 Hz step where the guest ring has not advanced yet: must not
				// duplicate the control point.
				sys.SetDoubleTrailSpan(edgeA, edgeB, hasGuestCursor: true, guestTail: tail, guestCount: count);
				sys.Update(1f / 30f, Transform3D.Identity);
			}
		}

		AssertThat(sys.AliveCount()).IsEqual(3);

		sys.Assemble(new Vector3(4f, 2f, -1.5f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var batch = sys.AssembledBatches().Single();

		// Adaptive Catmull-Rom subdivision must subdivide the two 1.34m corkscrew spans into smooth
		// multi-rib segments (2 spans * 6 sub-ribs * 6 verts = 72 verts) rather than 2 coarse quads.
		AssertThat(batch.Vertices.Count).IsGreaterEqual(60);

		// Leading rib vertices must carry non-zero `CUSTOM0` motion so `fx_billboard.gdshaderinc`
		// smoothly extends from `P_{t-1}` to `P_t` across `fx_step_alpha` instead of popping ahead.
		var leadingVert = batch.Vertices[^1];
		float motionLen = new Vector3(leadingVert.Motion.X, leadingVert.Motion.Y, leadingVert.Motion.Z).Length();
		AssertThat(motionLen).IsGreater(1.0f);
	}

	[TestCase]
	public void AttachToRootDoubleTrail_WithWorldSpanOverride_RebasesWhenNodeMoves()
	{
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 1.0f, trailLen: 1, delaySeconds: 0.5f);
		bank.Emitters[0].AttachToRoot = true;
		Engine_FxSystem sys = Build(bank);

		Transform3D node0 = new(Basis.Identity, Vector3.Zero);
		sys.SetDoubleTrailSpan(new Vector3(-0.15f, 1f, 0f), new Vector3(0.15f, 1f, 0f), hasGuestCursor: true, guestTail: 2u, guestCount: 2u);
		sys.Update(1f / 30f, node0);

		// Move the effect node 3m along -Z while spawning a second world span at Z = -3m (local 0).
		Transform3D node1 = new(Basis.Identity, new Vector3(0f, 0f, -3f));
		sys.SetDoubleTrailSpan(new Vector3(-0.15f, 1f, 0f), new Vector3(0.15f, 1f, 0f), hasGuestCursor: true, guestTail: 3u, guestCount: 3u);
		sys.Update(1f / 30f, node1);

		AssertThat(sys.LastRebased).IsTrue();
		List<Vector3> localPts = sys.ParticlePositions().ToList();
		AssertThat(localPts.Count).IsEqual(2);
		// First particle must have rebased to local Z = +3m (world Z = 0m) rather than dragging to Z = -3m.
		AssertThat(MathF.Abs(localPts[0].Z - 3f)).IsLess(0.05f);
		AssertThat(MathF.Abs(localPts[1].Z - 0f)).IsLess(0.05f);
	}

	[TestCase]
	public void RemasterMagicCircle_ExtractsSlopedOffOriginProfile_BuildsRings_AndRespectsSettings()
	{
		// 128-point closed circle centered 14m away on a sloped hill (`y = 38.5 + 0.25 * dx`) with
		// a duplicated closing point (`pts[128] == pts[0]`), matching `FX_DAD44F56`.
		const int loopPts = 128;
		var spline = new Vector3[loopPts + 1];
		Vector3 expectedCenter = new(-14f, 38.5f, 172f);
		const float expectedRadius = 4.5f;
		for (int i = 0; i <= loopPts; i++)
		{
			float ang = (i % loopPts) / (float)loopPts * MathF.Tau;
			float dx = MathF.Cos(ang) * expectedRadius;
			float dz = MathF.Sin(ang) * expectedRadius;
			spline[i] = new Vector3(expectedCenter.X + dx, expectedCenter.Y + (0.25f * dx), expectedCenter.Z + dz);
		}

		var profile = new Engine_RemasterMagicCircle.CircleProfile();
		AssertThat(Engine_RemasterMagicCircle.TryExtractProfile(spline, profile)).IsTrue();
		AssertThat(profile.Center.DistanceTo(expectedCenter)).IsLess(0.08f);
		AssertThat(MathF.Abs(profile.Radius - expectedRadius)).IsLess(0.05f);
		// Bin 0 (+X uphill) must sit ~2.2m higher than Bin 32 (-X downhill).
		AssertThat(profile.PerimeterY[0] - profile.PerimeterY[Engine_RemasterMagicCircle.AngularBins / 2]).IsGreater(1.8f);

		var positions = new Vector3[Engine_RemasterMagicCircle.RingVertexCount];
		var uvs = new Vector2[Engine_RemasterMagicCircle.RingVertexCount];
		var colors = new Color[Engine_RemasterMagicCircle.RingVertexCount];
		var perimY = new float[Engine_RemasterMagicCircle.AngularBins];
		int written = Engine_RemasterMagicCircle.BuildRingVertices(
			profile, profile.Center, profile.Radius, 0.6f, null,
			positions, uvs, colors, perimY);
		AssertThat(written).IsEqual(Engine_RemasterMagicCircle.RingVertexCount);
		AssertThat(positions.Take(written).All(p => p.IsFinite())).IsTrue();
		AssertThat(positions.Take(written).Max(p => p.Length())).IsGreater(expectedRadius * 1.02f);

		// Aimed small target circles (0.20m) and far/wide Level 5 circles (35m) must both extract cleanly.
		var aimedSmall = new Vector3[65];
		var farWide = new Vector3[65];
		for (int i = 0; i <= 64; i++)
		{
			float ang = (i % 64) / 64f * MathF.Tau;
			aimedSmall[i] = new Vector3(40f + (MathF.Cos(ang) * 0.20f), 12f, -35f + (MathF.Sin(ang) * 0.20f));
			farWide[i] = new Vector3(-50f + (MathF.Cos(ang) * 35.0f), 12f, 60f + (MathF.Sin(ang) * 35.0f));
		}
		var aimedProfile = new Engine_RemasterMagicCircle.CircleProfile();
		AssertThat(Engine_RemasterMagicCircle.TryExtractProfile(aimedSmall, aimedProfile)).IsTrue();
		AssertThat(aimedProfile.Radius).IsEqualApprox(0.20f, 0.02f);
		AssertThat(Engine_RemasterMagicCircle.TryExtractProfile(farWide, aimedProfile)).IsTrue();
		AssertThat(aimedProfile.Radius).IsEqualApprox(35.0f, 0.2f);

		// Settings & Classic-mode isolation invariants.
		var modernCfg = new Dictionary<string, string> { ["materials_modernized"] = "true" };
		var unmodernCfg = new Dictionary<string, string> { ["materials_modernized"] = "false" };
		var explicitOff = new Dictionary<string, string> { ["materials_modernized"] = "true", ["fx_remaster_magic_circle"] = "false" };
		AssertThat(Engine_RemasterMagicCircle.ResolveEnabled(modernCfg, classicMaterialActive: false)).IsTrue();
		AssertThat(Engine_RemasterMagicCircle.ResolveEnabled(modernCfg, classicMaterialActive: true)).IsFalse();
		AssertThat(Engine_RemasterMagicCircle.ResolveEnabled(unmodernCfg, classicMaterialActive: false)).IsFalse();
		AssertThat(Engine_RemasterMagicCircle.ResolveEnabled(explicitOff, classicMaterialActive: false)).IsFalse();
		AssertThat(Engine_RemasterMagicCircle.ResolveEnabled(unmodernCfg, classicMaterialActive: false, envOverride: "1")).IsTrue();
	}

	[TestCase]
	public void RemasterMagicCircle_HugsHillyTerrainAndBridges_AndGeneratesPerimeterHeights()
	{
		// Build a 128-point spline at radius 6m on a hillside with a steep +1.4m mound inside the circle
		// (r ~ 3m along +X) that linear CenterY -> PerimeterY interpolation would clip right through.
		Vector3 center = new(10f, 38.5f, -20f);
		const float radius = 6.0f;
		float HillHeight(float wx, float wz)
		{
			float dx = wx - (center.X + 3.0f);
			float dz = wz - center.Z;
			float mound = 1.4f * MathF.Exp(-((dx * dx) + (dz * dz)) / 3.0f);
			return center.Y + (0.12f * (wx - center.X)) + mound;
		}

		bool SampleHill(Vector3 p, out float y)
		{
			y = HillHeight(p.X, p.Z);
			return true;
		}

		const int loopPts = 128;
		var spline = new Vector3[loopPts + 1];
		for (int i = 0; i <= loopPts; i++)
		{
			float ang = (i % loopPts) / (float)loopPts * MathF.Tau;
			float wx = center.X + (MathF.Cos(ang) * radius);
			float wz = center.Z + (MathF.Sin(ang) * radius);
			spline[i] = new Vector3(wx, HillHeight(wx, wz), wz);
		}

		var profile = new Engine_RemasterMagicCircle.CircleProfile();
		AssertThat(Engine_RemasterMagicCircle.TryExtractProfile(spline, profile)).IsTrue();

		var positions = new Vector3[Engine_RemasterMagicCircle.RingVertexCount];
		var uvs = new Vector2[Engine_RemasterMagicCircle.RingVertexCount];
		var colors = new Color[Engine_RemasterMagicCircle.RingVertexCount];
		var perimLocalY = new float[Engine_RemasterMagicCircle.AngularBins];

		int written = Engine_RemasterMagicCircle.BuildRingVertices(
			profile, profile.Center, profile.Radius, 1.2f, SampleHill,
			positions, uvs, colors, perimLocalY);
		AssertThat(written).IsEqual(Engine_RemasterMagicCircle.RingVertexCount);

		// Every single ring vertex must sit cleanly above the exact hill heightfield (zero mound clipping!).
		for (int i = 0; i < written; i++)
		{
			Vector3 worldVert = profile.Center + positions[i];
			float groundAtVert = HillHeight(worldVert.X, worldVert.Z);
			AssertThat(worldVert.Y - groundAtVert).IsGreaterEqual(Engine_RemasterMagicCircle.LiftMeters - 0.01f);
		}

		// Perimeter height bins for rising motes must track the exact terrain height at every angle.
		for (int s = 0; s < Engine_RemasterMagicCircle.AngularBins; s++)
		{
			float ang = (float)s / Engine_RemasterMagicCircle.AngularBins * MathF.Tau;
			float wx = profile.Center.X + (MathF.Cos(ang) * profile.Radius);
			float wz = profile.Center.Z + (MathF.Sin(ang) * profile.Radius);
			float expectedLocalY = HillHeight(wx, wz) - profile.Center.Y;
			AssertThat(MathF.Abs(perimLocalY[s] - expectedLocalY)).IsLess(0.02f);
		}

		// Elevated bridge case: bridge deck at Y = 42.0 above riverbed terrain at Y = 37.0.
		// Ring must remain cleanly on the bridge deck instead of sinking down to the riverbed.
		bool SampleRiverbed(Vector3 _, out float y) { y = 37.0f; return true; }
		float bridgeSurface = Engine_RemasterMagicCircle.ResolveSurfaceY(
			wx: 10f, wz: -20f, fallbackY: 42.0f, centerY: 42.0f,
			centerGroundY: 37.0f, hasCenterGround: true,
			sampleGround: SampleRiverbed);
		AssertThat(bridgeSurface).IsEqualApprox(42.0f, 1e-4f);
	}

	[TestCase]
	public void RemasterMagicCircle_SpellPalettesChargeLightScaling_AndOriginalSuppression()
	{
		// Calibrated 8-spell HDR palettes:
		// 1 = Inferno (rich fiery red, low green so HDR tonemapping never washes to yellow)
		var inferno = Engine_RemasterMagicCircle.ResolveSpellPalette(1);
		AssertThat(inferno.Core.R).IsGreaterEqual(0.95f);
		AssertThat(inferno.Core.G).IsLess(0.08f);
		AssertThat(inferno.Core.B).IsLess(0.03f);

		// 2 = Time Control (luminous white with normalized luminance scale)
		var timeControl = Engine_RemasterMagicCircle.ResolveSpellPalette(2);
		AssertThat(timeControl.Core.R).IsGreaterEqual(0.90f);
		AssertThat(timeControl.Core.G).IsGreaterEqual(0.90f);
		AssertThat(timeControl.Core.B).IsGreaterEqual(0.90f);
		AssertThat(timeControl.LuminanceScale).IsLess(0.90f);

		// 3 = Blades (radiant gold)
		var blades = Engine_RemasterMagicCircle.ResolveSpellPalette(3);
		AssertThat(blades.Core.R).IsGreaterEqual(0.95f);
		AssertThat(blades.Core.G).IsGreater(0.50f);
		AssertThat(blades.Core.B).IsLess(0.12f);

		// 4 = Vortex (vibrant green)
		var vortex = Engine_RemasterMagicCircle.ResolveSpellPalette(4);
		AssertThat(vortex.Core.G).IsGreaterEqual(0.90f);
		AssertThat(vortex.Core.R).IsLess(0.15f);

		// 6 = Force Push (crisp light blue)
		var forcePush = Engine_RemasterMagicCircle.ResolveSpellPalette(6);
		AssertThat(forcePush.Core.B).IsGreaterEqual(0.95f);
		AssertThat(forcePush.Core.G).IsGreater(0.70f);
		AssertThat(forcePush.Core.R).IsGreater(0.20f);

		// 7 = Raise Dead (spectral necrotic teal / ghostly turquoise — distinct from Chaos purple-magenta)
		var raiseDead = Engine_RemasterMagicCircle.ResolveSpellPalette(7);
		AssertThat(raiseDead.Core.G).IsGreaterEqual(0.80f);
		AssertThat(raiseDead.Core.B).IsGreaterEqual(0.65f);
		AssertThat(raiseDead.Core.R).IsLess(0.15f);

		// Chaos (5) remains purple-magenta (high R & B, low G), clearly distinct from Raise Dead.
		var chaos = Engine_RemasterMagicCircle.ResolveSpellPalette(5);
		AssertThat(chaos.Core.R).IsGreaterEqual(0.85f);
		AssertThat(chaos.Core.B).IsGreaterEqual(0.60f);
		AssertThat(chaos.Core.G).IsLess(0.20f);

		// DRY host-driven radius: completely independent of spell ID across any mix-and-match slot order
		// (e.g. [5, 7, 5, 2, 1] = Chaos, Raise Dead, Chaos, Time Control, Inferno), strictly monotonic
		// from Charge Level 1..5 in surround mode with zero collapse during slot transitions.
		Vector3 heroPos = new(12f, 38.5f, -18f);
		float prevR = 0f;
		for (int step = 0; step <= 16; step++)
		{
			float lvl = 1f + (4f * (step / 16f));
			float r = Engine_RemasterMagicCircle.ResolveTargetRadius(lvl, heroPos, heroPos);
			AssertThat(r).IsGreater(prevR);
			prevR = r;
		}
		float r1 = Engine_RemasterMagicCircle.ResolveTargetRadius(1f, heroPos, heroPos);
		float r3 = Engine_RemasterMagicCircle.ResolveTargetRadius(3f, heroPos, heroPos);
		float r5 = Engine_RemasterMagicCircle.ResolveTargetRadius(5f, heroPos, heroPos);
		AssertThat(r1).IsGreaterEqual(2.0f);
		AssertThat(r3).IsGreater(r1 + 2.5f);
		AssertThat(r5).IsGreater(9.5f);

		// Aimed casting (> 3.25m away from Hero) smoothly transitions to the compact aimed reticle curve.
		Vector3 aimedCenter = heroPos + new Vector3(8.0f, 0f, 0f);
		float aimedR1 = Engine_RemasterMagicCircle.ResolveTargetRadius(1f, aimedCenter, heroPos);
		float aimedR5 = Engine_RemasterMagicCircle.ResolveTargetRadius(5f, aimedCenter, heroPos);
		AssertThat(aimedR1).IsEqualApprox(1.35f, 1e-3f);
		AssertThat(aimedR5).IsEqualApprox(2.50f, 1e-3f);

		// Unified ground hugging: even when guest spline profile is a tiny 0.45m placeholder,
		// expanding to 10.4m hugs sloped hillsides cleanly with zero spell-specific branches.
		Vector3 hillCenter = new(0f, 38.5f, 0f);
		float SlopeY(float wx, float wz) => hillCenter.Y + (0.20f * wx);
		bool SampleSlope(Vector3 p, out float y) { y = SlopeY(p.X, p.Z); return true; }
		var smallProfile = new Engine_RemasterMagicCircle.CircleProfile { Center = hillCenter, CenterY = hillCenter.Y, Radius = 0.45f };
		Array.Fill(smallProfile.PerimeterY, hillCenter.Y);
		var pos = new Vector3[Engine_RemasterMagicCircle.RingVertexCount];
		var uv = new Vector2[Engine_RemasterMagicCircle.RingVertexCount];
		var col = new Color[Engine_RemasterMagicCircle.RingVertexCount];
		var perimY = new float[Engine_RemasterMagicCircle.AngularBins];
		int written = Engine_RemasterMagicCircle.BuildRingVertices(
			smallProfile, hillCenter, r5, 0.5f, SampleSlope, pos, uv, col, perimY);
		AssertThat(written).IsEqual(Engine_RemasterMagicCircle.RingVertexCount);
		// Uphill +X perimeter bin must be ~4.1m higher than downhill -X bin at r ~ 10.4m.
		AssertThat(perimY[0] - perimY[Engine_RemasterMagicCircle.AngularBins / 2]).IsGreater(3.5f);
		for (int i = 0; i < written; i++)
		{
			Vector3 wv = hillCenter + pos[i];
			AssertThat(MathF.Abs((wv.Y - SlopeY(wv.X, wv.Z)) - Engine_RemasterMagicCircle.LiftMeters)).IsLess(0.02f);
		}

		// Charge-level radiance, opacity, and ground OmniLight3D energy denote spell strength (Levels 1 -> 5):
		// Level 1 starts noticeably subtler, and each consecutive tier steps up visibly.
		AssertThat(Engine_RemasterMagicCircle.ResolveChargeRadianceScale(1f)).IsLessEqual(0.35f);
		AssertThat(Engine_RemasterMagicCircle.ResolveChargeLightEnergy(1f)).IsLessEqual(0.60f);
		AssertThat(Engine_RemasterMagicCircle.ResolveChargeAlphaScale(1f)).IsLessEqual(0.65f);
		AssertThat(Engine_RemasterMagicCircle.ResolveChargeAlphaScale(5f)).IsEqualApprox(1.0f, 1e-4f);
		for (int tier = 1; tier < 5; tier++)
		{
			AssertThat(Engine_RemasterMagicCircle.ResolveChargeRadianceScale(tier + 1))
				.IsGreaterEqual(Engine_RemasterMagicCircle.ResolveChargeRadianceScale(tier) * 1.50f);
			AssertThat(Engine_RemasterMagicCircle.ResolveChargeLightEnergy(tier + 1))
				.IsGreaterEqual(Engine_RemasterMagicCircle.ResolveChargeLightEnergy(tier) * 1.70f);
			AssertThat(Engine_RemasterMagicCircle.ResolveChargeAlphaScale(tier + 1))
				.IsGreater(Engine_RemasterMagicCircle.ResolveChargeAlphaScale(tier));
		}

		// Original FX magic circle + wave rings are suppressed only while Modern remaster is active.
		AssertThat(Engine_RemasterMagicCircle.ShouldSuppressOriginalEffect(Engine_RemasterMagicCircle.MagicCircleEffectHash, remasterActive: true)).IsTrue();
		AssertThat(Engine_RemasterMagicCircle.ShouldSuppressOriginalEffect(Engine_RemasterMagicCircle.MagicCircleWaveEffectHash, remasterActive: true)).IsTrue();
		AssertThat(Engine_RemasterMagicCircle.ShouldSuppressOriginalEffect(0xB2848F7Cu, remasterActive: true)).IsFalse();
		AssertThat(Engine_RemasterMagicCircle.ShouldSuppressOriginalEffect(Engine_RemasterMagicCircle.MagicCircleEffectHash, remasterActive: false)).IsFalse();
		AssertThat(Engine_RemasterMagicCircle.ShouldSuppressOriginalEffect(Engine_RemasterMagicCircle.MagicCircleWaveEffectHash, remasterActive: false)).IsFalse();
	}

	[TestCase]
	public void DoubleTrailRingOverride_UsesExactGuestPointsAndKeyTimeFade_BladesFlight()
	{
		// Exact FX_B2848F7C keyframe shape from live dump of node 422D3270:
		// Key 0 (t=0, sword tip): scale = 0.0 (sharp point), alpha = 1.0
		// Key 1 (t=1, faded tail): scale = 1.0 (full 0.28m flare), alpha = 0.0
		FxBank bank = MakeBank(mode: 3u, widthStart: 0.0f, widthEnd: 1.0f, trailLen: 1, delaySeconds: 0.3f,
			texture: "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex");
		bank.Systems[0].Visuals[0].Alpha = 1.0f;
		bank.Systems[0].Visuals[1].Alpha = 0.0f;
		Engine_FxSystem sys = Build(bank);
		sys.RequireDoubleTrailSpan = true;

		// Feed a 4-point flying Blades ring in node-local coordinates (oldest -> newest) without
		// needing multi-tick accumulation: must emit immediately on the very first frame.
		var ring = new Engine_FxSystem.DoubleTrailRingPoint[]
		{
			new(new Vector3(-0.14f, 0f, -3.6f), new Vector3(0.14f, 0f, -3.6f), 1.0f),
			new(new Vector3(-0.14f, 0f, -2.4f), new Vector3(0.14f, 0f, -2.4f), 0.65f),
			new(new Vector3(-0.14f, 0f, -1.2f), new Vector3(0.14f, 0f, -1.2f), 0.30f),
			new(new Vector3(-0.14f, 0f, 0.0f), new Vector3(0.14f, 0f, 0.0f), 0.0f),
		};
		sys.SetDoubleTrailRing(ring);
		AssertThat(sys.HasDoubleTrailRing).IsTrue();
		AssertThat(sys.DoubleTrailRingCount).IsEqual(4);
		AssertThat(sys.ConsumeLiveWeaponTipDirty()).IsTrue();
		AssertThat(sys.ActiveBoundRadius).IsGreater(7.0f);

		// Emit with fast paths ON as well as OFF to verify DrawsNothing never culls an active ring.
		sys.Assemble(new Vector3(4f, 2f, -2f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: true);
		var batch = sys.AssembledBatches().Single();

		// 3 spans of 1.2m each -> adaptive subdivision (>= 7 sub-ribs/span -> >= 120 verts).
		AssertThat(batch.Vertices.Count).IsGreaterEqual(100);

		// All ring vertices must carry zero CUSTOM0 shader motion so the unsynchronized 30 Hz
		// `fx_step_alpha` never produces sawtooth pullback jitter on 15 Hz guest rings.
		foreach (var v in batch.Vertices)
		{
			AssertThat(v.Motion.X).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Y).IsEqualApprox(0f, 1e-5f);
			AssertThat(v.Motion.Z).IsEqualApprox(0f, 1e-5f);
		}

		// Leading tip (UV.y == 1.0, KeyTime == 0.0): scale == 0.0 (collapsed to midpoint) & alpha ~= 1.0.
		var tipVerts = batch.Vertices.Where(v => MathF.Abs(v.Uv.Y - 1f) < 1e-3f).ToList();
		AssertThat(tipVerts.Count).IsGreater(0);
		foreach (var v in tipVerts)
		{
			AssertThat(MathF.Abs(v.Pos.X)).IsLess(0.02f);
			AssertThat(v.Col.A).IsGreater(0.9f);
		}

		// Trailing tail (UV.y == 0.0, KeyTime == 1.0): full 0.28m flare & alpha == 0.0.
		var tailVerts = batch.Vertices.Where(v => v.Uv.Y < 1e-3f).ToList();
		AssertThat(tailVerts.Count).IsGreater(0);
		float tailSpan = tailVerts.Max(v => v.Pos.X) - tailVerts.Min(v => v.Pos.X);
		AssertThat(tailSpan).IsGreater(0.24f);
		AssertThat(tailVerts.All(v => v.Col.A <= 0.01f)).IsTrue();

		// Rising sword whose ring slots have all reached KeyTime == 1.0 (fully faded in guest):
		// must emit ZERO quads.
		var fadedRing = new Engine_FxSystem.DoubleTrailRingPoint[]
		{
			new(new Vector3(-0.14f, 0f, -1.0f), new Vector3(0.14f, 0f, -1.0f), 1.0f),
			new(new Vector3(-0.14f, 0f, 0.0f), new Vector3(0.14f, 0f, 0.0f), 1.0f),
		};
		sys.SetDoubleTrailRing(fadedRing);
		sys.Assemble(new Vector3(4f, 2f, -2f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: true);
		AssertThat(sys.AssembledBatches().Any()).IsFalse();

		// Rebase must transform unrefreshed ring points when the node moves between guest ticks.
		sys.SetDoubleTrailRing(ring);
		sys.Update(1f / 30f, new Transform3D(Basis.Identity, Vector3.Zero));
		sys.Update(1f / 30f, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f)));
		AssertThat(sys.LastRebased).IsTrue();
		sys.Assemble(new Vector3(4f, 2f, -2f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
		var rebasedTip = sys.AssembledBatches().Single().Vertices.First(v => MathF.Abs(v.Uv.Y - 1f) < 1e-3f);
		AssertThat(MathF.Abs(rebasedTip.Pos.Z - 2.0f)).IsLess(0.05f);
	}

	[TestCase]
	public void DoubleTrailSpanInterpolation_PreservesWidthAcrossHelicalTwist_AndIncreasesSubdivision()
	{
		// Flying Blades twists ~150 degrees per guest step around its flight axis (span = 0.28m).
		// Linear EdgeA/EdgeB lerp collapses width by ~75% (to 0.072m) at t = 0.5; rotational
		// interpolation must preserve the full 0.28m width at every intermediate display frame.
		const float half = 0.14f;
		float ang1 = 150f * (MathF.PI / 180f);
		Vector3 prevA = new(0f, -half, 0f);
		Vector3 prevB = new(0f, half, 0f);
		Vector3 currA = new(-half * MathF.Sin(ang1), -half * MathF.Cos(ang1), 1.3f);
		Vector3 currB = new(half * MathF.Sin(ang1), half * MathF.Cos(ang1), 1.3f);

		foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
		{
			Engine_FxSystem.InterpolateDoubleTrailSpan(prevA, prevB, currA, currB, t, out Vector3 outA, out Vector3 outB);
			AssertThat(outA.DistanceTo(outB)).IsEqualApprox(0.28f, 1e-3f);
			Vector3 mid = 0.5f * (outA + outB);
			AssertThat(mid.Z).IsEqualApprox(1.3f * t, 1e-3f);
		}

		// Helical 4-point double-trail ring twisting 150 deg per step must trigger twist-aware
		// subdivision (>= 14 segments/span -> >= 252 vertices across 3 spans) AND preserve constant
		// 0.28m width at every intermediate Catmull-Rom rib (zero mid-span bead pinching!).
		FxBank bank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 1.0f, trailLen: 1, delaySeconds: 0.3f,
			texture: "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex");
		bank.Systems[0].Visuals[0].Alpha = 1.0f;
		bank.Systems[0].Visuals[1].Alpha = 0.5f;
		Engine_FxSystem sys = Build(bank);
		sys.RequireDoubleTrailSpan = true;

		var helixRing = new Engine_FxSystem.DoubleTrailRingPoint[4];
		for (int i = 0; i < 4; i++)
		{
			float a = i * ang1;
			Vector3 h = new(half * MathF.Sin(a), half * MathF.Cos(a), 0f);
			Vector3 c = new(0f, 0f, (i - 3) * 1.3f);
			helixRing[i] = new Engine_FxSystem.DoubleTrailRingPoint(c - h, c + h, (3 - i) / 3f);
		}
		sys.SetDoubleTrailRing(helixRing);
		sys.Assemble(new Vector3(4f, 2f, -2f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: true);
		var batch = sys.AssembledBatches().Single();
		// 3 spans * 14 segments/span * 6 verts/quad = 252 verts (twist-aware adaptive subdivision).
		AssertThat(batch.Vertices.Count).IsGreaterEqual(252);
		for (int q = 0; q + 5 < batch.Vertices.Count; q += 6)
		{
			// EmitRibbonQuads emits (rA[j], rA[j+1], rB[j], rB[j], rA[j+1], rB[j+1]).
			float ribWidth0 = batch.Vertices[q].Pos.DistanceTo(batch.Vertices[q + 2].Pos);
			float ribWidth1 = batch.Vertices[q + 1].Pos.DistanceTo(batch.Vertices[q + 5].Pos);
			AssertThat(ribWidth0).IsGreaterEqual(0.27f);
			AssertThat(ribWidth1).IsGreaterEqual(0.27f);
		}
	}

	[TestCase]
	public void BladesDoubleTrail_15HzTipAlphaContinuity_AndSelfIllumLitAttenuation()
	{
		// 15 Hz guest ring (arrives every 2 guest ticks: prevStamp = 98, currStamp = 100).
		// Across DrawTick = 99.0 .. 101.0 (spanning two 30 Hz ticks), tipAlpha must advance
		// monotonically 0.0 -> 0.25 -> 0.50 -> 0.75 -> 1.0 without stalling at 1.0 or snapping back.
		AssertThat(Refii.Game.GameFx.ComputeDoubleTrailTipAlpha(99.0, 98.0, 100.0)).IsEqualApprox(0.0f, 1e-4f);
		AssertThat(Refii.Game.GameFx.ComputeDoubleTrailTipAlpha(99.5, 98.0, 100.0)).IsEqualApprox(0.25f, 1e-4f);
		AssertThat(Refii.Game.GameFx.ComputeDoubleTrailTipAlpha(100.0, 98.0, 100.0)).IsEqualApprox(0.50f, 1e-4f);
		AssertThat(Refii.Game.GameFx.ComputeDoubleTrailTipAlpha(100.5, 98.0, 100.0)).IsEqualApprox(0.75f, 1e-4f);
		AssertThat(Refii.Game.GameFx.ComputeDoubleTrailTipAlpha(101.0, 98.0, 100.0)).IsEqualApprox(1.0f, 1e-4f);

		// Broadside melee sword swing (span ⊥ motion, gentle twist) retains 100% lit brightness,
		// whereas point-first flight (span ∥ motion) and 150-deg corkscrewing addone flight attenuate cleanly.
		var broadA = new List<Vector3> { new(0f, 0f, 0f), new(1f, 0f, 0f), new(2f, 0f, 0f) };
		var broadB = new List<Vector3> { new(0f, 0.8f, 0f), new(1f, 0.8f, 0f), new(2f, 0.8f, 0f) };
		float broadAtten = Engine_FxSystem.ComputeDoubleRibbonLitAttenuation(broadA, broadB, 1, new Vector3(1f, 0.4f, 4f), "addone");
		AssertThat(broadAtten).IsEqualApprox(1.0f, 1e-3f);

		var spearA = new List<Vector3> { new(0f, 0f, 0f), new(1f, 0f, 0f), new(2f, 0f, 0f) };
		var spearB = new List<Vector3> { new(0.8f, 0.02f, 0f), new(1.8f, 0.02f, 0f), new(2.8f, 0.02f, 0f) };
		float spearAtten = Engine_FxSystem.ComputeDoubleRibbonLitAttenuation(spearA, spearB, 1, new Vector3(-2f, 0f, 0f), "addone");
		AssertThat(spearAtten).IsLess(0.30f);

		// EmissionBoost compensation: SelfIllum = 0 diffuse scene light cancels EmissionBoost on additive
		// ribbons while SelfIllum = 1 emissive colour remains completely unaffected.
		float prevBoost = Refii.Lighting.Engine_PostFxParams.EmissionBoost;
		try
		{
			Refii.Lighting.Engine_PostFxParams.EmissionBoost = 2.5f;
			FxBank litBank = MakeBank(mode: 3u, widthStart: 1.0f, widthEnd: 1.0f, trailLen: 1, delaySeconds: 0.3f,
				texture: "art/fx/pfx_textures/fxt_weapon_trail_flourish_01.tex");
			litBank.Systems[0].Visuals[0].SelfIllum = 0.0f;
			litBank.Systems[0].Visuals[1].SelfIllum = 0.0f;
			litBank.Systems[0].Visuals[0].SrcBlend = 1;
			litBank.Systems[0].Visuals[0].DstBlend = 1;
			litBank.Systems[0].Visuals[1].SrcBlend = 1;
			litBank.Systems[0].Visuals[1].DstBlend = 1;
			Engine_FxSystem litSys = Build(litBank);
			litSys.SetAmbient(2.0f, 2.0f, 2.0f);
			litSys.SetDoubleTrailRing(new Engine_FxSystem.DoubleTrailRingPoint[]
			{
				new(new Vector3(0f, 0f, 0f), new Vector3(0f, 0.8f, 0f), 0.5f),
				new(new Vector3(1f, 0f, 0f), new Vector3(1f, 0.8f, 0f), 0.0f),
			});
			litSys.Assemble(new Vector3(0.5f, 0.4f, 4f), Vector3.Right, Vector3.Up,
				new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false);
			Color litVertCol = litSys.AssembledBatches().Single().Vertices[0].Col;
			// Broadside normalAtten == 1.0, Lit = 2.0, invBoost = 1 / 2.5 => vertex Col.R == 0.80 (so * 2.5 in shader == exact 2.0!).
			AssertThat(litVertCol.R).IsEqualApprox(0.80f, 1e-3f);
		}
		finally
		{
			Refii.Lighting.Engine_PostFxParams.EmissionBoost = prevBoost;
		}
	}

	[TestCase]
	public void RemasterMagicCircle_NormalizesAuthoredLowIntensity_AndUsesSingleLinearBlendAdd()
	{
		// Live inspection proved FX_DAD44F56 caps guest intensity at 0.050 rather than 1.0.
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(0.05f, 1.0f, 1.0f)).IsEqualApprox(1.0f, 1e-4f);
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(0.025f, 1.0f, 1.0f)).IsEqualApprox(0.5f, 1e-4f);
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(0.05f, 0.5f, 0.5f)).IsEqualApprox(0.25f, 1e-4f);
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(0.0f, 1.0f, 1.0f)).IsEqualApprox(0.0f, 1e-4f);
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(float.NaN, 1.0f, 1.0f)).IsEqualApprox(0.0f, 1e-4f);
		AssertThat(Engine_RemasterMagicCircle.NormalizeCircleStrength(1.0f, 1.0f, 1.0f)).IsEqualApprox(1.25f, 1e-4f);

		// Verify shader adheres to scene-referred HDR & single linear blend_add rules.
		string? dir = AppContext.BaseDirectory;
		string? shaderPath = null;
		while (dir is not null)
		{
			string candidate = System.IO.Path.Combine(dir, "Godot", "src", "Engine", "fx", "fx_magic_circle_remaster.gdshader");
			if (System.IO.File.Exists(candidate))
			{
				shaderPath = candidate;
				break;
			}
			dir = System.IO.Directory.GetParent(dir)?.FullName;
		}
		AssertThat(shaderPath).IsNotNull();
		string shaderSrc = System.IO.File.ReadAllText(shaderPath!);
		string codeOnly = string.Join('\n', shaderSrc.Split('\n').Select(line =>
		{
			int comment = line.IndexOf("//", StringComparison.Ordinal);
			return comment >= 0 ? line[..comment] : line;
		}));
		AssertThat(codeOnly.Contains("ALBEDO = rgb;", StringComparison.Ordinal)).IsTrue();
		AssertThat(codeOnly.Contains("ALBEDO = rgb * (raw_alpha", StringComparison.Ordinal)).IsFalse();
		AssertThat(codeOnly.Contains("discard;", StringComparison.Ordinal)).IsFalse();
		AssertThat(codeOnly.Contains("uniform bool is_pbr", StringComparison.Ordinal)).IsTrue();
		AssertThat(codeOnly.Contains("fable_emission_boost", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void GroundConformAoE_HugsHillsidesAndPreservesBridges_ForInfernoChaosShock()
	{
		// Verify Inferno (all 5 rings + burst), Chaos (all 5 rings), and Shock (all 3 tiers) match,
		// while non-ground effects (e.g. Blades FX_B2848F7C) stay untouched.
		foreach (uint h in new uint[]
		{
			0x795222A7u, 0x795222A4u, 0x795222A5u, 0x795222A2u, 0x795222A3u, 0xA2E649EAu,
			0xC02AE6F6u, 0xC02AE6F5u, 0xC02AE6F4u, 0xC02AE6F3u, 0xC02AE6F2u,
			0x96895970u, 0x96895972u, 0x96895974u,
		})
		{
			AssertThat(Engine_FxGroundConform.ShouldConformEffect(h)).IsTrue();
		}
		AssertThat(Engine_FxGroundConform.ShouldConformEffect(0xB2848F7Cu)).IsFalse();
		AssertThat(Engine_FxGroundConform.ShouldConformEffect(0xDAD44F56u)).IsFalse();

		// 1. Horizontal align=True ground quad (20x20m Chaos/Shock/Inferno style) on a sloped hillside
		// must tessellate into a multi-quad grid whose every vertex sits cleanly above the slope.
		var quadBank = MakeBank(mode: 1u, widthStart: 0f, widthEnd: 0f, trailLen: 0, delaySeconds: 1.0f,
			texture: "art/fx/pfx_textures/fxt_white_ring_01.tex");
		quadBank.Emitters[0].InitialNum = 1;
		quadBank.Emitters[0].MaxActive = 1;
		quadBank.Systems[0].Visuals[0].HasAlignQuat = true;
		// Identity AlignQuat lies in the guest XY (horizontal) plane -> Godot XZ plane.
		quadBank.Systems[0].Visuals[0].AlignQuat = new[] { 0f, 0f, 0f, 1f };
		quadBank.Systems[0].Visuals[0].HasSize = true;
		quadBank.Systems[0].Visuals[0].SizeX = 20f;
		quadBank.Systems[0].Visuals[0].SizeY = 20f;
		quadBank.Systems[0].Visuals[1].HasAlignQuat = true;
		quadBank.Systems[0].Visuals[1].AlignQuat = new[] { 0f, 0f, 0f, 1f };
		quadBank.Systems[0].Visuals[1].HasSize = true;
		quadBank.Systems[0].Visuals[1].SizeX = 20f;
		quadBank.Systems[0].Visuals[1].SizeY = 20f;
		Engine_FxSystem quadSys = Build(quadBank);
		quadSys.GroundConform = true;
		quadSys.Update(1f / 30f, new Transform3D(Basis.Identity, new Vector3(0f, 38.5f, 0f)));

		float HillY(float wx, float wz) => 38.5f + (0.25f * wx) + (0.15f * wz);
		bool SampleHill(Vector3 p, out float y) { y = HillY(p.X, p.Z); return true; }
		var hillTransform = new Transform3D(Basis.Identity, new Vector3(0f, 38.5f, 0f));
		var hillCtx = new Engine_FxGroundConform.GroundContext(hillTransform, SampleHill);
		quadSys.Assemble(new Vector3(0f, 8f, 12f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false, groundConform: hillCtx);
		var quadBatch = quadSys.AssembledBatches().Single();
		// 16x16 grid = 256 quads * 6 verts = 1536 verts (instead of 6 verts for a flat quad).
		AssertThat(quadBatch.Vertices.Count).IsEqual(16 * 16 * 6);
		foreach (var v in quadBatch.Vertices)
		{
			Vector3 worldVert = hillTransform * v.Pos;
			float expectedGround = HillY(worldVert.X, worldVert.Z);
			AssertThat(worldVert.Y - expectedGround).IsGreaterEqual(Engine_FxGroundConform.LiftMeters - 0.01f);
		}

		// Elevated bridge: deck at Y = 42.0 above riverbed at Y = 37.0 -> ring stays cleanly on the bridge deck.
		bool SampleRiverbed(Vector3 _, out float y) { y = 37.0f; return true; }
		var bridgeTransform = new Transform3D(Basis.Identity, new Vector3(0f, 42.0f, 0f));
		var bridgeCtx = new Engine_FxGroundConform.GroundContext(bridgeTransform, SampleRiverbed);
		quadSys.Assemble(new Vector3(0f, 8f, 12f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false, groundConform: bridgeCtx);
		foreach (var v in quadSys.AssembledBatches().Single().Vertices)
		{
			AssertThat((bridgeTransform * v.Pos).Y).IsEqualApprox(42.0f + Engine_FxGroundConform.LiftMeters, 1e-3f);
		}

		// 2. Radial billboard ring particles (Inferno R = 8m style) follow the hillside heightfield at every azimuth.
		var ringBank = MakeBank(mode: 1u, widthStart: 0f, widthEnd: 0f, trailLen: 0, delaySeconds: 1.0f,
			texture: "art/fx/pfx_textures/fxt_flames_double_03.tex");
		ringBank.Emitters[0].SubType = 2; // horizontal circle/ring emitter
		ringBank.Emitters[0].SpawnRadius = 8.0f;
		ringBank.Emitters[0].EmitOnSurface = true;
		ringBank.Emitters[0].InitialNum = 16;
		ringBank.Emitters[0].MaxActive = 16;
		ringBank.Systems[0].Visuals[0].HasSize = true;
		ringBank.Systems[0].Visuals[0].SizeX = 2.0f;
		ringBank.Systems[0].Visuals[0].SizeY = 2.0f;
		Engine_FxSystem ringSys = Build(ringBank);
		ringSys.GroundConform = true;
		ringSys.Update(1f / 30f, hillTransform);
		ringSys.Assemble(new Vector3(0f, 8f, 12f), Vector3.Right, Vector3.Up,
			new Dictionary<string, Engine_FxSystem.MeshInstanceData>(), sceneQuads: true, fast: false, groundConform: hillCtx);
		var ringBatch = ringSys.AssembledBatches().Single();
		AssertThat(ringBatch.Vertices.Count).IsEqual(16 * 6);
		float minVertY = ringBatch.Vertices.Min(v => (hillTransform * v.Pos).Y);
		float maxVertY = ringBatch.Vertices.Max(v => (hillTransform * v.Pos).Y);
		// Across R = 8m on a 0.25x + 0.15z slope, uphill flames must be > 3m higher than downhill flames.
		AssertThat(maxVertY - minVertY).IsGreater(3.0f);
		foreach (var v in ringBatch.Vertices)
		{
			Vector3 worldVert = hillTransform * v.Pos;
			AssertThat(MathF.Abs(worldVert.Y - HillY(worldVert.X, worldVert.Z))).IsLess(0.25f);
		}
	}
}
