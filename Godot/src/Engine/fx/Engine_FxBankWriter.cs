using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Refii.Fx.Xex;

namespace Refii.Fx;

/// <summary>
/// THE GUEST'S OWN PARTICLE BANK WITH THE MODS IN IT: the shipped <c>particle_bank.bnk</c> with every
/// emitter, system and effect a mod states (<see cref="Engine_FxModEffects"/>) written back into it
/// in the file's own layout, so the guest's loader builds the mod's types and its simulation runs
/// them — whichever look draws the result.
/// </summary>
/// <remarks>
/// <para>THE LAYOUT IS THE LOADER'S. The guest reads the bank once, on the "Permanent Bank Mounter"
/// thread (<c>sub_822F3898</c> opens <c>Art\Particles\particle_bank.bnk</c> at 0x822f3970 and hands
/// the stream to <c>sub_832361B8</c>), which checks the <c>ParticleBankFile</c> magic and the version,
/// then reads the header marker (<c>sub_83233928</c>), the materials (<c>sub_8322C848</c>), the
/// systems (<c>sub_8322C438</c>), the entity types (<c>sub_8322BFD8</c>) and the effects
/// (<c>sub_8322FF90</c>) — the sections <see cref="XexBankParser.ParseBank(byte[], XexBank, bool)"/>
/// mirrors, and the ones this writes.</para>
/// <para>WHAT IS WRITTEN: the header and the material section verbatim; in each other section every
/// record a mod does not state copied byte for byte from the shipped file, every one it replaces
/// encoded in its place, every one it adds appended, and the counts restated.</para>
/// <para>WHAT A RECORD NEEDS THAT THE MODEL DOES NOT CARRY (an entity's version and trailing word, a
/// standard emitter's wrapper marker, a spline's version, a timeline's marker, a timed material's
/// marker, header and trailing word) is taken from the shipped record it replaces, or for an added
/// one from the commonest value the shipped bank uses for that kind.</para>
/// <para>MATERIAL IDS ARE FRESH. A timed material's id is a REGISTRY key: <c>sub_8322C6F8</c> looks
/// the id up (<c>sub_8322C5F8</c>) and, when it is known, re-reads the stream INTO that existing
/// material (vtable +24) — so a written child reusing a shipped id would rewrite every system sharing
/// that material. Each written child takes an id no shipped material uses.</para>
/// </remarks>
public static class Engine_FxBankWriter
{
	/// <summary>The bank composed for the guest, and what the overlay did.</summary>
	public sealed record Composed(byte[] Bytes, Engine_FxModEffects.Result Result);

	/// <summary>
	/// The shipped bank <paramref name="shipped"/> with <paramref name="sources"/> overlaid by
	/// <see cref="Engine_FxModEffects.Apply"/>'s rules and written in the loader's layout; null with
	/// <paramref name="error"/> when the shipped bank does not parse.
	/// </summary>
	public static Composed? Compose(byte[] shipped, IReadOnlyList<Engine_FxModEffects.Source> sources, out string error)
	{
		var x = new XexBank();
		if (!XexBankParser.ParseBank(shipped, x))
		{
			error = x.Error.Length != 0 ? x.Error : "the shipped particle bank does not parse";
			return null;
		}

		// The model of the same bytes; a bank with no emitters yet is still a bank to add to.
		var bank = new FxBank();
		Engine_FxBank.Parse(shipped, bank);

		Engine_FxModEffects.Result result = Engine_FxModEffects.Apply(bank, sources);
		error = string.Empty;
		return new Composed(Write(shipped, x, bank, result), result);
	}

	/// <summary>
	/// The bytes: <paramref name="shipped"/> (parsed as <paramref name="x"/>) with the records
	/// <paramref name="result"/> states taken from <paramref name="bank"/>, the overlaid model.
	/// </summary>
	public static byte[] Write(byte[] shipped, XexBank x, FxBank bank, Engine_FxModEffects.Result result)
	{
		var t = new Templates(x);
		var w = new BigEndianWriter(shipped.Length + 4096);
		w.Raw(shipped, 0, x.Section3Offset);

		// Systems.
		var systems = new Dictionary<uint, FxSystemDef>();
		foreach (FxSystemDef s in bank.Systems)
		{
			systems[s.Id] = s;
		}

		w.U32(x.Section3Param);
		int count = w.Reserve();
		int n = 0;
		foreach (XexSystemDef xs in x.Systems)
		{
			if (result.StatedSystems.Contains(xs.Id) && systems.TryGetValue(xs.Id, out FxSystemDef? s))
			{
				EncodeSystem(w, s, x.BankVersion, t, xs, shipped);
			}
			else
			{
				w.Raw(shipped, xs.FileBegin, xs.FileEnd - xs.FileBegin);
			}

			n++;
		}

		foreach (FxSystemDef s in bank.Systems)
		{
			if (result.StatedSystems.Contains(s.Id) && !x.SystemById.ContainsKey(s.Id))
			{
				EncodeSystem(w, s, x.BankVersion, t, null, null);
				n++;
			}
		}

		w.Patch(count, (uint)n);

		// Entity types.
		var emitters = new Dictionary<uint, FxEmitter>();
		foreach (FxEmitter e in bank.Emitters)
		{
			emitters[e.EntityId] = e;
		}

		w.U32(x.Section4Param);
		count = w.Reserve();
		n = 0;
		foreach (XexEntityRef reference in x.EntityOrder)
		{
			XexEntityType? shippedEntity = EntityAt(x, reference);
			if (shippedEntity is null)
			{
				continue;
			}

			if (result.StatedEmitters.Contains(reference.EntityId) && emitters.TryGetValue(reference.EntityId, out FxEmitter? e))
			{
				EncodeEntity(w, e, x.BankVersion, x.Fable3Layout, t, shippedEntity.TypeId == e.TypeId ? shippedEntity : null);
			}
			else
			{
				w.Raw(shipped, shippedEntity.FileBegin, shippedEntity.FileEnd - shippedEntity.FileBegin);
			}

			n++;
		}

		foreach (FxEmitter e in bank.Emitters)
		{
			if (result.StatedEmitters.Contains(e.EntityId) && !x.EntityById.ContainsKey(e.EntityId))
			{
				EncodeEntity(w, e, x.BankVersion, x.Fable3Layout, t, null);
				n++;
			}
		}

		w.Patch(count, (uint)n);

		// Effects.
		w.U32(x.Section5Version);
		count = w.Reserve();
		n = 0;
		foreach (XexEffectCollection xe in x.Effects)
		{
			if (result.StatedEffects.Contains(xe.Id) && bank.FindByHash(xe.Id) is { } effect)
			{
				EncodeEffect(w, bank, effect, x.Section5Version, t);
			}
			else
			{
				w.Raw(shipped, xe.FileBegin, xe.FileEnd - xe.FileBegin);
			}

			n++;
		}

		foreach (FxEffect effect in bank.Effects)
		{
			if (result.StatedEffects.Contains(effect.NameHash) && !x.EffectById.ContainsKey(effect.NameHash))
			{
				EncodeEffect(w, bank, effect, x.Section5Version, t);
				n++;
			}
		}

		w.Patch(count, (uint)n);
		w.Raw(shipped, x.EndOffset, shipped.Length - x.EndOffset);
		return w.ToArray();
	}

	private static XexEntityType? EntityAt(XexBank x, XexEntityRef r) => r.Index < 0 ? null : r.TypeId switch
	{
		0 => x.StdEmitters[r.Index],
		3 => x.Orbitals[r.Index],
		4 => x.Attractors[r.Index],
		5 => x.SplineEntities[r.Index],
		6 => x.Roots[r.Index],
		7 => x.PointLights[r.Index],
		8 => x.SpotLights[r.Index],
		9 => x.SplineEmitters[r.Index],
		_ => null,
	};

	// ---------------------------------------------------------------------------------------
	// What the model does not carry
	// ---------------------------------------------------------------------------------------

	/// <summary>The shipped bank's commonest value for each word the model does not carry, and every material id it uses.</summary>
	public sealed class Templates
	{
		public readonly Dictionary<uint, uint> EntityVersion = new();
		public readonly Dictionary<uint, uint> EntityTrailing = new();
		public readonly (bool Marker, uint Version) StdWrapper;
		public readonly (bool Marker, uint Version) Spline;
		public readonly (bool Marker, uint Version) Timeline;
		public readonly (bool Marker, uint Version) Child;
		public readonly uint MaterialHeader;
		public readonly uint MaterialTrailing;
		public readonly uint SystemTrailing;
		public readonly uint EffectTrailing;
		public readonly HashSet<uint> MaterialIds = new();

		public Templates(XexBank x)
		{
			var entities = new List<XexEntityType>();
			entities.AddRange(x.StdEmitters);
			entities.AddRange(x.Orbitals);
			entities.AddRange(x.Attractors);
			entities.AddRange(x.SplineEntities);
			entities.AddRange(x.Roots);
			entities.AddRange(x.PointLights);
			entities.AddRange(x.SpotLights);
			entities.AddRange(x.SplineEmitters);
			foreach (IGrouping<uint, XexEntityType> type in entities.GroupBy(e => e.TypeId))
			{
				EntityVersion[type.Key] = Commonest(type.Select(e => e.Version), 0u);
				EntityTrailing[type.Key] = Commonest(type.Select(e => e.TrailingKey), 0u);
			}

			StdWrapper = Commonest(x.StdEmitters.Select(e => (e.HadMarker, e.WrapperVersion)), (true, 3u));
			Spline = Commonest(
				x.SplineEntities.Select(e => (e.Spline.HadMarker, e.Spline.Version))
					.Concat(x.SplineEmitters.Select(e => (e.Spline.HadMarker, e.Spline.Version))),
				(true, 4u));
			Timeline = Commonest(
				entities.SelectMany(e => e.Scalar).Where(s => s.Keys.Count != 0).Select(s => (s.HadMarker, s.MarkerVersion)),
				(false, 0u));
			IEnumerable<XexSystemChild> children = x.Systems.SelectMany(s => s.Children);
			Child = Commonest(children.Select(c => (c.HadMarker, c.MarkerVersion)), (false, 0u));
			MaterialHeader = Commonest(children.Select(c => c.Material.Hdr), 0u);
			MaterialTrailing = Commonest(children.Select(c => c.Material.TrailingId), 0u);
			SystemTrailing = Commonest(x.Systems.Select(s => s.TrailingId), 0u);
			EffectTrailing = Commonest(x.Effects.Select(e => e.TrailingId), 0u);
			foreach (XexMaterial m in x.Materials)
			{
				MaterialIds.Add(m.Id);
			}

			foreach (XexSystemChild c in children)
			{
				MaterialIds.Add(c.MaterialId);
			}
		}

		/// <summary>An id no material of the bank (nor one already handed out) uses, derived from the system and slot.</summary>
		public uint FreshMaterialId(uint system, int slot)
		{
			uint id = FxBank.Fnv1Lower($"0x{system:X8}#material{slot}");
			while (id == 0 || !MaterialIds.Add(id))
			{
				id = id * 0x01000193u ^ 0x5Au;
			}

			return id;
		}

		private static T Commonest<T>(IEnumerable<T> values, T fallback) where T : notnull
		{
			var counts = new Dictionary<T, int>();
			T best = fallback;
			int most = 0;
			foreach (T v in values)
			{
				int c = counts.TryGetValue(v, out int k) ? k + 1 : 1;
				counts[v] = c;
				if (c > most)
				{
					most = c;
					best = v;
				}
			}

			return best;
		}
	}

	// ---------------------------------------------------------------------------------------
	// Entity types (section 4)
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// One entity-type record from the model: the type, id and subtype words, then the type's own
	/// fields in the loader's order (<see cref="XexBankParser"/>'s <c>…LoadBin</c>). <paramref name="like"/>
	/// is the shipped record it replaces, for the words the model does not carry.
	/// </summary>
	public static void EncodeEntity(BigEndianWriter w, FxEmitter e, uint bankVersion, bool fable3, Templates t, XexEntityType? like)
	{
		w.U32(e.TypeId);
		w.U32(e.EntityId);
		w.U32(e.SubType);
		switch (e.TypeId)
		{
			case 0:
			{
				var std = like as XexStandardEmitterType;
				var shape = (XexEmitterShape)Math.Min(e.SubType, 3u);
				bool marker = std?.HadMarker ?? t.StdWrapper.Marker;
				uint version = std is { HadMarker: true } ? std.WrapperVersion : t.StdWrapper.Version;
				bool shapeStated = shape switch
				{
					XexEmitterShape.Sphere or XexEmitterShape.Disc => e.SpawnRadius != 0.1f || e.ShapeFlagA || e.ShapeFlagB,
					XexEmitterShape.Mesh => e.ShapeFlagA,
					_ => false,
				};
				if (!marker && (shapeStated || e.GlobalCollider || e.CollisionRestitution != 1f))
				{
					marker = true;
					version = Math.Max(t.StdWrapper.Version, 3u);
				}

				if (marker && version < 3 && (e.GlobalCollider || e.CollisionRestitution != 1f))
				{
					version = 3;
				}

				if (marker)
				{
					w.U32(XexConst.MagicBaad);
					w.U32(version);
					switch (shape)
					{
						case XexEmitterShape.Sphere:
						case XexEmitterShape.Disc:
							w.F32(e.SpawnRadius);
							w.Bool(e.ShapeFlagA);
							w.Bool(e.ShapeFlagB);
							break;
						case XexEmitterShape.Mesh:
							w.Bool(e.ShapeFlagA);
							break;
					}
				}

				Scalar(w, e, 4, like, t);
				Scalar(w, e, 5, like, t);
				Vector(w, e.Vec0, 0, like, t);
				for (int i = 6; i <= 11; i++)
				{
					Scalar(w, e, i, like, t);
				}

				Vector(w, e.Vec1, 1, like, t);
				Scalar(w, e, 12, like, t);
				Scalar(w, e, 13, like, t);
				Vector(w, e.Vec2, 2, like, t);
				Scalar(w, e, 14, like, t);
				w.Bool(e.ReleaseTrail);
				if (marker && version >= 3)
				{
					w.Bool(e.GlobalCollider);
					w.F32(e.CollisionRestitution);
				}

				EmitterCommon(w, e, bankVersion, fable3, t, like);
				break;
			}

			case 9:
			{
				var spline = like as XexSplineEmitterType;
				EmitterCommon(w, e, bankVersion, fable3, t, like);
				uint version = SplineHeader(w, spline?.Spline, t);
				w.Bool(e.SplineRandomGenerator);
				w.Bool(e.SplineReverseFlow);
				w.F32(e.SplineRateEmitter);
				w.Bool(e.SplineLooped);
				w.Bool(e.SplineRandomStart);
				if (version >= 3)
				{
					w.Bool(e.SplineStraight);
				}

				if (version >= 4)
				{
					w.Bool(spline?.Spline.ScaleEmissionRatesBySplineLength ?? e.SplineScaleEmissionByLength);
				}

				w.I32(e.AnimationMode);
				w.I32(e.NumSplineSegments);
				w.F32(e.RandomiseAnimMagnitude);
				w.F32(e.SmoothAnimationSpeed);
				w.F32(e.JerkyAnimationRate);
				w.Bool(e.RandomiseAnimMagnitudeFlag);
				w.Bool(e.RandomiseAnimationRates);
				break;
			}

			case 3:
				w.Bool(e.PositionalInteractable);
				Scalar(w, e, 25, like, t);
				w.F32(e.AngularRotationRate);
				Scalar(w, e, 0, like, t);
				Scalar(w, e, 1, like, t);
				EntityCommon(w, e, t, like);
				break;
			case 4:
				w.Bool(e.PositionalInteractable);
				Scalar(w, e, 26, like, t);
				Scalar(w, e, 27, like, t);
				w.Bool(e.GlobalAttractor);
				Scalar(w, e, 0, like, t);
				Scalar(w, e, 1, like, t);
				EntityCommon(w, e, t, like);
				break;
			case 5:
			{
				w.Bool(e.PositionalInteractable);
				uint version = SplineHeader(w, (like as XexSplineEntityType)?.Spline, t);
				w.U32((uint)e.SplinePoints.Length);
				foreach (float[] p in e.SplinePoints)
				{
					w.F32(p.Length > 0 ? p[0] : 0f);
					w.F32(p.Length > 1 ? p[1] : 0f);
					w.F32(p.Length > 2 ? p[2] : 0f);
				}

				w.Bool(e.SplineRandomGenerator);
				w.Bool(e.SplineReverseFlow);
				w.F32(e.SplineRate);
				w.Bool(e.SplineLooped);
				w.Bool(e.SplineRandomStart);
				if (version >= 3)
				{
					w.Bool(e.SplineStraight);
				}

				if (version >= 4)
				{
					w.Bool(e.SplineScaleEmissionByLength);
				}

				Scalar(w, e, 0, like, t);
				Scalar(w, e, 1, like, t);
				EntityCommon(w, e, t, like);
				break;
			}

			case 6:
				EntityCommon(w, e, t, like);
				break;
			case 7:
				w.Bool(e.PositionalInteractable);
				w.U32(e.HaloTextureHash);
				Vector(w, e.LightColour, 3, like, t);
				for (int i = 15; i <= 23; i++)
				{
					Scalar(w, e, i, like, t);
				}

				Scalar(w, e, 0, like, t);
				Scalar(w, e, 1, like, t);
				EntityCommon(w, e, t, like);
				break;
			case 8:
			{
				var spot = like as XexSpotLightEntityType;
				w.Bool(e.PositionalInteractable);
				w.U32(spot?.SpotLightTextureHash ?? 0u);
				w.U32(e.HaloTextureHash);
				w.Bool(spot?.SpotLightCastsShadows ?? false);
				Vector(w, e.LightColour, 3, like, t);
				Scalar(w, e, 15, like, t);
				Scalar(w, e, 24, like, t);
				for (int i = 16; i <= 23; i++)
				{
					Scalar(w, e, i, like, t);
				}

				Scalar(w, e, 0, like, t);
				Scalar(w, e, 1, like, t);
				EntityCommon(w, e, t, like);
				break;
			}

			default:
				throw new InvalidDataException($"entity type {e.TypeId} has no layout in the bank");
		}
	}

	private static uint SplineHeader(BigEndianWriter w, XexSplineObject? like, Templates t)
	{
		bool marker = like?.HadMarker ?? t.Spline.Marker;
		uint version = like is { HadMarker: true } ? like.Version : (marker ? t.Spline.Version : 0u);
		if (marker)
		{
			w.U32(XexConst.MagicSpline);
			w.U32(version);
		}

		return marker ? version : 0u;
	}

	private static void EmitterCommon(BigEndianWriter w, FxEmitter e, uint bankVersion, bool fable3, Templates t, XexEntityType? like)
	{
		w.Bool(e.PositionalInteractable);
		Scalar(w, e, 2, like, t);
		Scalar(w, e, 3, like, t);
		w.I32((int)e.MaxActive);
		w.F32(e.InitialNum);
		for (int i = 0; i < 4; i++)
		{
			w.F32(e.Fades.Length > i ? e.Fades[i] : 0f);
		}

		w.Bool(e.SortParticles);
		if (bankVersion >= 2)
		{
			w.Bool(e.ForceToFullSizeRenderBuffer);
		}

		w.Bool(e.AttachToRoot);
		if (fable3)
		{
			var emitter = like as XexEmitterType;
			w.F32(emitter?.InternalScattering ?? 0f);
			w.Bool(emitter?.AtmosScattering ?? false);
		}

		EntityCommon(w, e, t, like);
	}

	private static void EntityCommon(BigEndianWriter w, FxEmitter e, Templates t, XexEntityType? like)
	{
		w.U32(like?.Version ?? (t.EntityVersion.TryGetValue(e.TypeId, out uint v) ? v : 0u));
		for (int i = 0; i < 3; i++)
		{
			w.F32(e.Pos.Length > i ? e.Pos[i] : 0f);
		}

		for (int i = 0; i < 4; i++)
		{
			w.F32(e.Quat.Length > i ? e.Quat[i] : (i == 3 ? 1f : 0f));
		}

		w.Bool(e.UseLife);
		w.I32(e.LifetimeFrames);
		w.F32(e.ProcessRadius);
		w.U32(like?.TrailingKey ?? (t.EntityTrailing.TryGetValue(e.TypeId, out uint k) ? k : 0u));
	}

	/// <summary>
	/// The keys a timeline states as the guest reads them: the authored keys behind the samples
	/// Godot evaluates (<see cref="FxTimeline.Engine"/>), in their order — the first key starts the
	/// timeline — or none when the model evaluates the default.
	/// </summary>
	public static IReadOnlyList<(float Time, float Value)> KeysOf(FxTimeline t)
	{
		if (t.Engine.Keys.Count != 0 && (t.Keys.Count != 0 || !t.Engine.Present))
		{
			return t.Engine.Keys.Select(k => (k.TimeSecs, k.Value)).ToList();
		}

		return t.Keys;
	}

	public static IReadOnlyList<(float Time, float X, float Y, float Z)> KeysOf(FxVectorTimeline t)
	{
		if (t.Engine.Keys.Count != 0 && (t.Keys.Count != 0 || !t.Engine.Present))
		{
			return t.Engine.Keys.Select(k => (k.TimeSecs, k.X, k.Y, k.Z)).ToList();
		}

		return t.Keys;
	}

	private static void Scalar(BigEndianWriter w, FxEmitter e, int index, XexEntityType? like, Templates t)
	{
		XexScalarTimeline? shipped = like?.Scalar[index];
		TimelineMarker(w, shipped is null ? t.Timeline : (shipped.HadMarker, shipped.MarkerVersion));
		IReadOnlyList<(float Time, float Value)> keys = KeysOf(e.Generic[index]);
		w.I32(keys.Count);
		foreach ((float time, float value) in keys)
		{
			w.F32(time);
			w.F32(value);
		}
	}

	private static void Vector(BigEndianWriter w, FxVectorTimeline v, int index, XexEntityType? like, Templates t)
	{
		XexVecTimeline? shipped = like?.Vec[index];
		TimelineMarker(w, shipped is null ? t.Timeline : (shipped.HadMarker, shipped.MarkerVersion));
		IReadOnlyList<(float Time, float X, float Y, float Z)> keys = KeysOf(v);
		w.I32(keys.Count);
		foreach ((float time, float x, float y, float z) in keys)
		{
			w.F32(time);
			w.F32(x);
			w.F32(y);
			w.F32(z);
		}
	}

	private static void TimelineMarker(BigEndianWriter w, (bool Marker, uint Version) marker)
	{
		if (marker.Marker)
		{
			w.U32(XexConst.MagicTimeline);
			w.U32(marker.Version);
		}
	}

	// ---------------------------------------------------------------------------------------
	// Systems (section 3)
	// ---------------------------------------------------------------------------------------

	/// <summary>Seconds that the loader's <c>(int)(30 * s)</c> turns back into <paramref name="frames"/> exactly.</summary>
	public static float SecondsFor(int frames) => frames <= 0 ? 0f : (frames + 0.5f) / XexConst.Fps;

	/// <summary>
	/// One system (material timeline) from the model: one timed material per visual, each stating
	/// every tag its mode resolves (the visuals hold resolved values, so nothing is left to inherit
	/// from a neighbour), under a fresh material id. The timed materials the model does not hold —
	/// a replaced shipped system's segments with no texture or mesh, which draw nothing and which
	/// <see cref="Engine_FxBank"/> leaves out of <see cref="FxSystemDef.Visuals"/> — are copied from
	/// <paramref name="file"/> as they were, in their place in the timeline, so the segment spans the
	/// guest builds around them are the shipped ones.
	/// </summary>
	public static void EncodeSystem(BigEndianWriter w, FxSystemDef s, uint bankVersion, Templates t, XexSystemDef? like, byte[]? file)
	{
		XexSystemChild? model = like?.Children.FirstOrDefault();
		var kept = new List<XexSystemChild>();
		if (like is not null && file is not null && like.MaterialType == s.Mode)
		{
			for (int i = 0; i < like.Segments.Count; i++)
			{
				XexSegmentRecord r = like.Segments[i];
				bool drawn = s.Mode == 4 ? r.MeshPath.Length != 0 : r.TexPath.Length != 0;
				if (!drawn)
				{
					kept.Add(like.Children[like.ChildOrder[i]]);
				}
			}
		}

		w.U32(s.Id);
		w.U32(s.Mode);
		w.I32(s.Visuals.Count + kept.Count);
		int next = 0;
		for (int i = 0; i <= s.Visuals.Count; i++)
		{
			// The kept ones lead: a later segment with no texture or mesh of its own inherits its
			// predecessor's (BuildSegments' InheritTex / the MeshID fallback), so only those before
			// the first drawn one go undrawn. The record runs through its material's trailing word.
			int frames = i < s.Visuals.Count ? s.Visuals[i].DelayFrames : int.MaxValue;
			while (next < kept.Count && kept[next].StartFrames <= frames)
			{
				w.Raw(file!, kept[next].FileBegin, kept[next].FileEnd - kept[next].FileBegin);
				next++;
			}

			if (i == s.Visuals.Count)
			{
				break;
			}

			FxVisual v = s.Visuals[i];
			(bool marker, uint markerVersion) = model is null ? t.Child : (model.HadMarker, model.MarkerVersion);
			if (marker)
			{
				w.U32(XexConst.MagicChild);
				w.U32(markerVersion);
			}

			w.F32(SecondsFor(v.DelayFrames));
			w.U32(t.FreshMaterialId(s.Id, i));
			EncodeMaterial(w, s.Mode, v, bankVersion, model?.Material.Hdr ?? t.MaterialHeader);
			w.U32(model?.Material.TrailingId ?? t.MaterialTrailing);
		}

		w.F32(s.LoopStartFrames / XexConst.Fps);
		w.F32(s.LoopEndFrames / XexConst.Fps);
		w.U32((uint)s.LoopCount);
		w.U32(like?.TrailingId ?? t.SystemTrailing);
	}

	/// <summary>
	/// The tags <see cref="XexBankParser.BuildSegments"/> resolves for a mode, in the loader's bit
	/// order, each holding the bank's value: a field behind an unset <c>Has…</c> flag is one Godot's
	/// simulation (<c>Engine_FxSystem.BuildVisual</c>) draws as its default, so the default is
	/// written; a set one is written as stated, Godot's own draw-time clamps (a size of 0 drawn as
	/// 1, at most 16 trail segments) being Godot's and not the guest's.
	/// </summary>
	private static void EncodeMaterial(BigEndianWriter w, uint mode, FxVisual v, uint bankVersion, uint header)
	{
		var body = new BigEndianWriter(256);
		int tags = 0;
		void Tag(uint tag)
		{
			body.U32(tag);
			tags++;
		}

		Tag(XexTag.DiffuseAlpha);
		body.F32(v.HasAlpha ? v.Alpha : 1f);

		// Godot draws ColorSq; the loader squares what it reads (CMaterialParamDiffuseColor). The
		// stated colour is written when it squares to that, else the root of what is drawn.
		Tag(XexTag.DiffuseColor);
		for (int k = 0; k < 3; k++)
		{
			float drawn = v.ColorSq.Length > k ? v.ColorSq[k] : 1f;
			float stated = v.Color.Length > k ? v.Color[k] : 1f;
			body.F32(stated * stated == drawn ? stated : MathF.Sqrt(MathF.Max(0f, drawn)));
		}

		if (mode is 1 or 2 or 3 && v.HasTexture && v.Texture.Length != 0)
		{
			string path = v.Texture.Replace('/', '\\');
			Tag(XexTag.DiffuseTexture);
			body.U32(XexBankParser.Fnv1LowerResourceName(path));
			body.Str(path);
			body.U32((uint)Math.Max(1, v.TexCols));
			body.U32((uint)Math.Max(1, v.TexRows));
			body.Bool(v.RandomFrame);
			body.F32(v.FrameSpeed);
			body.Bool(v.Displacement);
			body.Bool(v.SaturationMask);
			if (bankVersion < 2)
			{
				body.Bool(v.SoftDepthContrast > 0f);
			}
			else
			{
				body.F32(v.SoftDepthContrast);
			}
		}

		if (mode == 1)
		{
			Tag(XexTag.Size);
			body.F32(v.HasSize ? v.SizeX : 1f);
			body.F32(v.HasSize ? v.SizeY : 1f);
			Tag(XexTag.SpriteNumPoints);
			body.U32((uint)Math.Max(0, v.SpritePoints));
		}

		if (mode is 1 or 2 or 3)
		{
			Tag(XexTag.SrcBlendMode);
			body.U32(v.SrcBlend >= 0 ? (uint)v.SrcBlend : 1u);
			Tag(XexTag.DestBlendMode);
			body.U32(v.DstBlend >= 0 ? (uint)v.DstBlend : 5u);
			Tag(XexTag.BlendOp);
			body.U32((uint)Math.Max(0, v.BlendOp));
		}

		if (mode == 1)
		{
			Tag(XexTag.RotationAngle);
			body.F32(v.HasRotation ? v.RotSpeed : 0f);
			body.F32(v.HasRotation ? v.RotSpread : 0f);
			body.Bool(v.HasRotation && v.RotInitialRandom);
		}

		if (mode is 2 or 3 or 4)
		{
			Tag(XexTag.FloatSize);
			body.F32(v.HasTrailWidth ? v.TrailWidth : 1f);
		}

		if (mode is 2 or 3)
		{
			Tag(XexTag.NumSegments);
			body.U32((uint)Math.Max(0, v.HasNumSegments ? v.NumSegments : v.HasTrailLen ? v.TrailLen : 1));
		}

		bool mesh = mode == 4 && v.HasMesh && v.MeshPath.Length != 0;
		if (mesh)
		{
			string path = v.MeshPath.Replace('/', '\\');
			Tag(XexTag.MeshID);
			body.U32(XexBankParser.Fnv1LowerResourceName(path));
			body.Str(path);
			body.Bool(v.Displacement);
		}

		if (mode == 1)
		{
			bool on = v.HasFlicker;
			Tag(XexTag.RandomSize);
			body.F32(on ? SecondsFor(v.FlickerFrames) : 0f);
			body.F32(on ? v.FlickerBaseX : 1f);
			body.F32(on ? v.FlickerBaseY : 1f);
			body.F32(on ? v.FlickerSpreadX : 0f);
			body.F32(on ? v.FlickerSpreadY : 0f);
			body.Bool(!on || v.FlickerShared);
		}
		else if (mesh)
		{
			// The mesh form of the tag; the loader picks it by the MeshID tag read before it.
			bool on = v.HasRandScale;
			int frames = on && v.RsFreq > 1e-4f ? Math.Max(1, (int)((v.RsFreq * XexConst.Fps) + 0.5f)) : 0;
			Tag(XexTag.RandomSize);
			body.F32(SecondsFor(frames));
			body.F32(on ? v.RsMin : 1f);
			body.F32(on ? v.RsRange : 0f);
		}

		if (mode == 4)
		{
			bool on = v.HasOrient;
			Tag(XexTag.RotationOrient);
			body.F32(on ? v.OrSpeed : 0f);
			body.F32(on ? v.OrSpread : 0f);
			body.Bool(on && v.OrRandAngle);
			for (int k = 0; k < 3; k++)
			{
				body.F32(on && v.OrAxis.Length > k ? v.OrAxis[k] : (k == 0 ? 1f : 0f));
			}

			body.Bool(!on || v.OrRandAxis);
		}

		if (mode is 1 or 2 or 3)
		{
			Tag(XexTag.SelfIllumFactor);
			body.F32(v.SelfIllum);
		}

		if (mode == 1)
		{
			Tag(XexTag.GlobalFrameScale);
			body.F32(v.HasGlobalScale && v.GlobalScale.Length > 0 ? v.GlobalScale[0] : 1f);
			body.F32(v.HasGlobalScale && v.GlobalScale.Length > 1 ? v.GlobalScale[1] : 1f);
			if (v.HasAlignQuat)
			{
				Tag(XexTag.AlignmentQuat);
				for (int k = 0; k < 4; k++)
				{
					body.F32(v.AlignQuat.Length > k ? v.AlignQuat[k] : 0f);
				}
			}
		}

		w.U32(header);
		w.I32(tags);
		w.Raw(body.ToArray());
	}

	// ---------------------------------------------------------------------------------------
	// Effects (section 5)
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// One effect record: its key, the z-sort and update-frequency words the section's version
	/// carries, and one type-vector per <see cref="FxEffectPart.Vector"/> run of parts, each item
	/// (entity type, system, trail system) by id — 0 for none.
	/// </summary>
	public static void EncodeEffect(BigEndianWriter w, FxBank bank, FxEffect effect, uint sectionVersion, Templates t)
	{
		w.U32(effect.NameHash);
		if (sectionVersion >= 2)
		{
			w.Bool(effect.ZSortEmitters);
		}

		if (sectionVersion >= 3)
		{
			w.U32(effect.UpdateFrequency);
		}

		var vectors = new List<List<FxEffectPart>>();
		int at = int.MinValue;
		foreach (FxEffectPart part in effect.Parts)
		{
			if (part.Emitter < 0 || part.Emitter >= bank.Emitters.Count)
			{
				continue;
			}

			if (vectors.Count == 0 || part.Vector != at)
			{
				vectors.Add(new List<FxEffectPart>());
				at = part.Vector;
			}

			vectors[^1].Add(part);
		}

		uint SystemId(int index) => index >= 0 && index < bank.Systems.Count ? bank.Systems[index].Id : 0u;

		w.I32(vectors.Count);
		foreach (List<FxEffectPart> vector in vectors)
		{
			w.U32(vector[0].Group);
			w.I32(vector.Count);
			foreach (FxEffectPart part in vector)
			{
				w.U32(bank.Emitters[part.Emitter].EntityId);
				w.U32(SystemId(part.System));
				w.U32(SystemId(part.TrailSystem));
			}
		}

		w.U32(t.EffectTrailing);
	}

	// ---------------------------------------------------------------------------------------

	/// <summary>The bank's byte order: big-endian words, one-byte flags, length-prefixed NUL-terminated text.</summary>
	public sealed class BigEndianWriter
	{
		private byte[] _bytes;
		private int _length;

		public BigEndianWriter(int capacity) => _bytes = new byte[Math.Max(16, capacity)];

		public int Length => _length;

		private void Ensure(int more)
		{
			if (_length + more > _bytes.Length)
			{
				Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + more));
			}
		}

		public void U8(byte v)
		{
			Ensure(1);
			_bytes[_length++] = v;
		}

		public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

		public void U32(uint v)
		{
			Ensure(4);
			_bytes[_length] = (byte)(v >> 24);
			_bytes[_length + 1] = (byte)(v >> 16);
			_bytes[_length + 2] = (byte)(v >> 8);
			_bytes[_length + 3] = (byte)v;
			_length += 4;
		}

		public void I32(int v) => U32((uint)v);

		public void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));

		/// <summary>The loader's string: a length word, the bytes, a NUL.</summary>
		public void Str(string s)
		{
			byte[] text = System.Text.Encoding.ASCII.GetBytes(s);
			U32((uint)text.Length);
			Raw(text);
			U8(0);
		}

		public void Raw(byte[] source) => Raw(source, 0, source.Length);

		public void Raw(byte[] source, int offset, int count)
		{
			if (count <= 0)
			{
				return;
			}

			Ensure(count);
			Buffer.BlockCopy(source, offset, _bytes, _length, count);
			_length += count;
		}

		/// <summary>A word written later (<see cref="Patch"/>), e.g. a count.</summary>
		public int Reserve()
		{
			int at = _length;
			U32(0);
			return at;
		}

		public void Patch(int at, uint v)
		{
			_bytes[at] = (byte)(v >> 24);
			_bytes[at + 1] = (byte)(v >> 16);
			_bytes[at + 2] = (byte)(v >> 8);
			_bytes[at + 3] = (byte)v;
		}

		public byte[] ToArray()
		{
			var o = new byte[_length];
			Buffer.BlockCopy(_bytes, 0, o, 0, _length);
			return o;
		}
	}
}
