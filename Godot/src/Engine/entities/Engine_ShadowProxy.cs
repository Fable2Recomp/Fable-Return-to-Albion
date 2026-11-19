using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Entities;

/// <summary>
/// One draw per shadow pass for a character, instead of one per surface.
/// </summary>
/// <remarks>
/// A composed character is dozens of surfaces (the hero is 79), and every surface is a draw
/// call in EVERY shadow pass that reaches it: six cube faces for each fixture holding a map,
/// and each of the sun's cascades. A shadow pass reads no material, so the split buys nothing
/// there.
///
/// So a skinned body gets a child that casts and is never drawn — the SAME vertices on the SAME
/// skeleton and skin, every opaque surface merged into one — and the body itself stops casting.
/// The shadow is the same shape; the draws are not. What is kept apart:
///   - an ALPHA-TESTED surface (hair, lace) stays a surface of its own with its own material,
///     because the shadow pass must still run that material's discard;
///   - a surface <see cref="Engine_HiddenClusters"/> has collapsed is left out, as the body
///     does not draw or cast it.
/// A RIGID prop of two or more surfaces gets the same child without the skeleton. Its caster
/// flag is <c>Engine_ShadowReach</c>'s, so the proxy is what that policy is handed and the body
/// is reported through <see cref="IsProxied"/> as no longer its to write.
///
/// The merged surface carries LEVELS OF DETAIL: level k is every source surface's level k (its
/// full index list where it has none), keyed by the largest of their edge lengths, so a distant
/// caster costs the shadow passes what the body's own LODs did.
///
/// The proxy is also the shadow's CADENCE: Engine_ShadowSkeletons poses and places it on the
/// guest's frames while the body interpolates every displayed frame, so a lantern map holding a
/// character re-renders at the guest's rate, not the display's.
/// </remarks>
public static class Engine_ShadowProxy
{
	public const string NodeName = "ShadowProxy";

	/// <summary>Debug lever: false hands every body its own shadow back on its next <see cref="Sync"/>.</summary>
	/// <remarks>
	/// OFF WHEN THE SUN CASTS NOTHING (`sun_shadow_distance` 0, as a phone's Low and Medium presets set it). The
	/// proxies are mostly the sun's casters; without them every rig keeps a second skeleton posed,
	/// placed and culled for shadows nobody draws. A spot light the data marks to cast still gets
	/// each body's own shadow, at the body's cadence. Measured in Bowerstone Market on the S25+:
	/// main-thread work -0.6 of 5.0 ms per frame (NativeAOT), 5% fewer draw calls. It follows the
	/// sun's setting live until something sets it.
	/// </remarks>
	public static bool Enabled
	{
		get => _enabled ?? Refii.Lighting.Engine_LevelLighting.SunFarDepth > 0f;
		set => _enabled = value;
	}

	private static bool? _enabled;

	/// <summary>
	/// Every skinned body gets a proxy, whatever it saves in draws: the proxy is what lets a
	/// character's shadow follow at the guest's rate (Engine_ShadowSkeletons) while the body
	/// keeps interpolating.
	/// </summary>
	private const int MinSkinnedSurfaces = 1;

	/// <summary>Surfaces the hidden-cluster mask can express; a body past it is left alone.</summary>
	private const int MaxSurfaces = 128;

	private const int MinRigidSurfaces = 2;

	/// <summary>Bodies casting through a proxy, by instance id: not <c>Engine_ShadowReach</c>'s to write.</summary>
	private static readonly HashSet<ulong> Proxied = new();

	public static bool IsProxied(ulong body) => Proxied.Contains(body);

	/// <summary>For another owner of cast-only twins (the prop batches): whose caster flag the reach policy leaves alone.</summary>
	public static void MarkProxied(ulong instance, bool proxied)
	{
		if (proxied)
		{
			Proxied.Add(instance);
		}
		else
		{
			Proxied.Remove(instance);
		}
	}

	/// <summary>
	/// The cast-only mesh of a rigid model with nothing hidden, or null when merging saves
	/// nothing; <paramref name="merged"/> is how many surfaces became one. Cached per mesh.
	/// </summary>
	public static ArrayMesh? MergedCaster(ArrayMesh source, out int merged, bool always = false)
	{
		merged = 0;
		int surfaces = source.GetSurfaceCount();
		if (surfaces < (always ? MinSkinnedSurfaces : MinRigidSurfaces) || surfaces > 64 ||
			(source.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatBones) != 0)
		{
			return null;
		}

		ArrayMesh? caster = MergedFor(source, null, UInt128.Zero, skinned: false, always: always);
		if (caster is not null)
		{
			merged = surfaces - (caster.GetSurfaceCount() - 1);
		}

		return caster;
	}

	/// <summary>For a level change, where the bodies go away.</summary>
	public static void Forget() => Proxied.Clear();

	/// <summary>Bodies casting through a proxy NOW; then merged meshes built and the surfaces they stand for, as run totals.</summary>
	public static int Active;

	public static int Built;

	public static int SurfacesMerged;

	private static readonly StringName StateMeta = "shadow_proxy_state";

	private static readonly StringName AlphaScissor = "alpha_scissor_on";

	/// <summary>Per source mesh, per hidden-surface mask. Weak: a crowd shares meshes and they retire.</summary>
	private static readonly ConditionalWeakTable<Mesh, Dictionary<(UInt128 Hidden, bool Always), ArrayMesh?>> Cache = new();

	private const string CasterShaderPath = "res://src/Engine/entities/shadow_caster.gdshader";
	private const string SolidCasterShaderPath = "res://src/Engine/entities/shadow_caster_solid.gdshader";
	private const string CutoutCasterShaderPath = "res://src/Engine/entities/shadow_caster_cutout.gdshader";

	private static readonly Lazy<ShaderMaterial> CasterMaterial = new(() => new ShaderMaterial
	{
		Shader = GD.Load<Shader>(CasterShaderPath),
	});

	private static readonly Lazy<ShaderMaterial> SolidCasterMaterial = new(() => new ShaderMaterial
	{
		Shader = GD.Load<Shader>(SolidCasterShaderPath),
	});

	private static readonly Lazy<Shader> CutoutCasterShader = new(() => GD.Load<Shader>(CutoutCasterShaderPath));

	private static readonly ConditionalWeakTable<ShaderMaterial, ShaderMaterial> CutoutCasters = new();

	private static readonly StringName AlbedoTexParam = "albedo_tex";
	private static readonly StringName AlphaScissorThresholdParam = "alpha_scissor_threshold";

	/// <summary>
	/// Whether a proxy uses the zero-discard depth-only solid caster instead of the
	/// dither-faded prop caster (true for characters, weapons, and indoor entity batches).
	/// </summary>
	public static bool UsesSolidProxyCaster(bool skinned, bool always) => skinned || always;

	/// <summary>
	/// Whether a shadow proxy belongs to static <see cref="Refii.Lighting.Engine_ShadowReach"/>
	/// culling. Skinned characters and moving attached followers (held/sheathed weapons) travel
	/// across the region and must not be culled against spawn-time static bounds.
	/// </summary>
	public static bool UsesStaticShadowReach(bool skinned, bool follows) => !skinned && !follows;

	/// <summary>
	/// Whether a kept alpha-scissor surface on a proxy can use the 1-tap derivative-free
	/// <c>shadow_caster_cutout.gdshader</c> (non-swaying standard materials on characters/entities).
	/// </summary>
	public static bool CanUseDedicatedCutoutCaster(bool skinned, bool always, string? shaderResourcePath)
	{
		if ((!skinned && !always) || string.IsNullOrEmpty(shaderResourcePath))
		{
			return false;
		}

		return shaderResourcePath.EndsWith("/standard_material.gdshader", StringComparison.Ordinal) ||
			shaderResourcePath.EndsWith("/standard_material_culled.gdshader", StringComparison.Ordinal) ||
			shaderResourcePath.EndsWith("/standard_material_phong.gdshader", StringComparison.Ordinal);
	}

	private static ShaderMaterial GetOrCreateCutoutCaster(ShaderMaterial source)
	{
		lock (CutoutCasters)
		{
			if (CutoutCasters.TryGetValue(source, out ShaderMaterial? cached))
			{
				return cached;
			}

			var mat = new ShaderMaterial
			{
				Shader = CutoutCasterShader.Value,
			};

			Variant albedo = source.GetShaderParameter(AlbedoTexParam);
			if (albedo.VariantType == Variant.Type.Object && albedo.AsGodotObject() is Texture2D tex)
			{
				mat.SetShaderParameter(AlbedoTexParam, tex);
			}

			Variant threshold = source.GetShaderParameter(AlphaScissorThresholdParam);
			if (threshold.VariantType is Variant.Type.Float or Variant.Type.Int)
			{
				mat.SetShaderParameter(AlphaScissorThresholdParam, threshold.AsSingle());
			}

			CutoutCasters.AddOrUpdate(source, mat);
			return mat;
		}
	}

	/// <summary>
	/// Eagerly initializes the caster material on the main thread to prevent worker thread GD.Load deadlocks.
	/// </summary>
	public static void WarmShader()
	{
		_ = CasterMaterial.Value;
		_ = SolidCasterMaterial.Value;
		_ = CutoutCasterShader.Value;
	}

	/// <summary>
	/// What the merged surface is drawn with: two-sided, as the bodies' own materials are, and
	/// INVISIBLE OUTSIDE THE SHADOW PASS (`shadow_caster.gdshader` discards there), so a cast-only
	/// mesh is never seen even if something switches its cast setting.
	/// </summary>
	/// <remarks>Lazy: casters are prebuilt on the prop streamer's workers, so two can race.</remarks>
	private static ShaderMaterial Caster => CasterMaterial.Value;

	/// <summary>
	/// Rigid bodies that cast through a FOLLOWING proxy — held weapons and other attached
	/// children — and whether their holder is a principal (placed every frame) or not (placed at
	/// the guest's rate). Written by the attachment pass each frame; see <see cref="MarkFollower"/>.
	/// </summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, StrongBox<bool>> Followers = new();

	/// <summary>
	/// Marks or unmarks a rigid body as an attached child whose shadow follows at the guest's
	/// rate. True when the marking changed, and then the caller must <see cref="Sync"/> the body.
	/// </summary>
	/// <remarks>
	/// An attached child is placed on its holder's bone every displayed frame, and a caster that
	/// moves every frame re-renders every lantern shadow map it stands in. Casting through a
	/// sibling proxy that moves on guest frames lets those maps rest between guest frames. The
	/// hero's and partner's weapons are placed every frame, as their bodies are.
	/// </remarks>
	public static bool MarkFollower(MeshInstance3D body, bool follows, bool principal)
	{
		bool known = Followers.TryGetValue(body, out StrongBox<bool>? box);
		if (!follows)
		{
			if (!known)
			{
				return false;
			}

			Followers.Remove(body);
			return true;
		}

		if (known)
		{
			if (box!.Value != principal)
			{
				box.Value = principal;
				Engine_ShadowSkeletons.SetFollowerEveryFrame(body, principal);
			}

			return false;
		}

		Followers.Add(body, new StrongBox<bool>(principal));
		return true;
	}

	private static bool IsFollower(MeshInstance3D body, out bool principal)
	{
		if (Followers.TryGetValue(body, out StrongBox<bool>? box))
		{
			principal = box.Value;
			return true;
		}

		principal = false;
		return false;
	}

	/// <summary>The hidden set each body's proxy was last built for.</summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, StrongBox<(UInt128 Hidden, bool Enabled)>> BuiltFor = new();

	/// <summary>
	/// Each body's proxy. A character's proxy stands BESIDE its body (same parent) so its placement
	/// is the shadow skeletons' to write at the guest's rate; a rigid prop's stays a child.
	/// </summary>
	private static readonly ConditionalWeakTable<MeshInstance3D, MeshInstance3D> Proxies = new();

	/// <summary>The body's proxy, if it has one.</summary>
	public static MeshInstance3D? ProxyOf(MeshInstance3D body) =>
		Proxies.TryGetValue(body, out MeshInstance3D? proxy) && GodotObject.IsInstanceValid(proxy) ? proxy : null;


	private static UInt128 EffectiveCollapsedMask(MeshInstance3D body) =>
		Refii.Models.Engine_ClassicMaterial.Active
			? Engine_HiddenClusters.CollapsedMask(body)
			: Engine_HiddenClusters.ShadowCollapsedMask(body);

	/// <summary>
	/// Computes the render layer mask for <paramref name="body"/>'s shadow proxy so <c>RendererSceneCull</c>
	/// pairs dynamic character/weapon proxies with placed lights in the same indoor/outdoor zone (dirtying
	/// their shadow maps whenever the character moves) without leaking shadows across building walls.
	/// </summary>
	public static uint ComputeProxyLayers(uint bodyLayers, bool follows)
	{
		if (!follows)
		{
			return bodyLayers & ~(Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask |
				Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask);
		}

		bool indoor = (bodyLayers & Refii.Lighting.Engine_LightZones.IndoorMask) != 0u;
		return indoor
			? (Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask)
			: (1u | Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask);
	}

	/// <summary>
	/// The reconcile cadence's call: <see cref="Sync"/> only when the hidden set moved since the
	/// proxy was built. No engine call otherwise — this runs for every entity, every second.
	/// </summary>
	public static void SyncHidden(MeshInstance3D body)
	{
		if (!BuiltFor.TryGetValue(body, out StrongBox<(UInt128 Hidden, bool Enabled)>? built) ||
			built.Value != (EffectiveCollapsedMask(body), Enabled))
		{
			Sync(body);
		}
	}

	/// <summary>
	/// Brings <paramref name="body"/>'s proxy in line with its mesh, skeleton, layers and hidden
	/// surfaces. Cheap when nothing changed. Call after any of those change. Main thread.
	/// </summary>
	public static void Sync(MeshInstance3D body)
	{
		if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree())
		{
			return;
		}

		// Under classic Godot's lights cast nothing: the body casts into the guest's cascades
		// through its classic caster, which shares its mesh, skeleton and surface overrides.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			// Recorded as the proxy is, so SyncHidden re-registers only when the collapse changed.
			BuiltFor.GetOrCreateValue(body).Value = (Engine_HiddenClusters.CollapsedMask(body), Enabled);
			Refii.Models.Engine_ClassicCasters.Register(body, dynamic: true);
			return;
		}

		// The guest states this renderable casts nothing (flag 0x40): no proxy, and the reach
		// policy never hands it a shadow.
		if (Refii.Models.Engine_ClassicCasters.NeverCasts(body))
		{
			if (ProxyOf(body) is { } unwanted)
			{
				Engine_ShadowSkeletons.Forget(body);
				Proxies.Remove(body);
				unwanted.QueueFree();
				Proxied.Remove(body.GetInstanceId());
			}
			body.SetMeta(StateMeta, "never");
			Refii.Lighting.Engine_ShadowReach.Exclude(body);
			return;
		}

		UInt128 collapsed = Engine_HiddenClusters.ShadowCollapsedMask(body);
		BuiltFor.GetOrCreateValue(body).Value = (collapsed, Enabled);

		// The source's surfaces: the hidden set is indexed by them, whatever the body shows.
		var source = Engine_BodyVariants.SourceMesh(body) as ArrayMesh;
		int surfaces = source?.GetSurfaceCount() ?? 0;
		UInt128 hidden = UInt128.Zero;
		bool skinned = surfaces != 0 && (source!.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatBones) != 0;
		// Characters and attached children (held weapons) cast through a cadence proxy; see
		// Engine_ShadowSkeletons for what the proxy is for.
		bool principalHolder = false;
		bool follower = !skinned && IsFollower(body, out principalHolder);
		bool follows = skinned || follower;
		bool isLampHost = (body.Layers & (Refii.Lighting.Engine_LevelLights.LampHostLayerMask | Refii.Lighting.Engine_LightZones.IndoorLampHostMask)) != 0u;
		bool indoor = (body.Layers & Refii.Lighting.Engine_LightZones.IndoorMask) != 0u;
		bool alwaysProxy = (follows || indoor) && !isLampHost;
		bool eligible = Enabled && !isLampHost && source is not null && surfaces <= MaxSurfaces &&
			surfaces >= (alwaysProxy ? MinSkinnedSurfaces : MinRigidSurfaces);
		if (eligible)
		{
			// Engine_HiddenClusters is the override slots' only writer and keeps what it wrote.
			hidden = surfaces >= MaxSurfaces ? collapsed : collapsed & ((UInt128.One << surfaces) - UInt128.One);
		}

		string state = eligible
			? $"{source!.GetInstanceId()}:{hidden:X}:{body.Skeleton}:{body.Layers:X}:{body.VisibilityRangeEnd:0.#}:{(follows ? 'f' : 'c')}"
			: string.Empty;
		if (body.HasMeta(StateMeta) && body.GetMeta(StateMeta).AsString() == state)
		{
			// Engine_ShadowReach.Release hands a body its shadow back when a prop turns out to be a
			// character; with a proxy standing that is BOTH casting, which is dearer than neither.
			if (state.Length != 0 && body.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off &&
				ProxyOf(body) is not null)
			{
				body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			}

			return;
		}

		body.SetMeta(StateMeta, state);
		MeshInstance3D? proxy = ProxyOf(body);
		ArrayMesh? merged = eligible ? MergedFor(source!, body, hidden, skinned, always: alwaysProxy) : null;
		ulong bodyId = body.GetInstanceId();
		if (merged is null)
		{
			if (proxy is not null)
			{
				Engine_ShadowSkeletons.Forget(body);
				Proxies.Remove(body);
				proxy.QueueFree();
				body.CastShadow = isLampHost
					? GeometryInstance3D.ShadowCastingSetting.Off
					: GeometryInstance3D.ShadowCastingSetting.On;
				if (Proxied.Remove(bodyId) && !skinned && !isLampHost)
				{
					Refii.Lighting.Engine_ShadowReach.Adopt(body);
				}
			}

			if (isLampHost)
			{
				body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
				Refii.Lighting.Engine_ShadowReach.Exclude(body);
			}
			else if (!follows && (body.Layers & (Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask | Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask)) != 0u)
			{
				body.Layers &= ~(Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask | Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask);
			}

			return;
		}

		// Beside the body, or under it: a proxy standing on the wrong side is rebuilt.
		bool beside = follows && body.GetParent() is Node3D && body.GetParent() is not BoneAttachment3D;
		if (proxy is not null && ReferenceEquals(proxy.GetParent(), body) == beside)
		{
			Engine_ShadowSkeletons.Forget(body);
			Proxies.Remove(body);
			proxy.QueueFree();
			proxy = null;
		}

		if (proxy is null)
		{
			proxy = new MeshInstance3D
			{
				Name = NodeName + "_" + body.Name,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
				GIMode = GeometryInstance3D.GIModeEnum.Disabled,
			};

			// A character's proxy is a SIBLING: under the body it would move at display rate
			// with its parent and re-dirty every lantern map each frame whatever its skeleton did.
			// Beside it, the shadow skeletons place it on guest frames and mirror its visibility.
			// A rigid prop's proxy stays a child, as does one whose body rides a bone attachment
			// (the attachment moves at display rate whichever side of it the proxy is on).
			if (beside)
			{
				var parent = (Node3D)body.GetParent();
				parent.AddChild(proxy);
				proxy.GlobalTransform = body.GlobalTransform;
				proxy.Visible = body.IsVisibleInTree();

				// Freed with its body: a sibling is not.
				MeshInstance3D standing = proxy;
				body.TreeExiting += () =>
				{
					if (GodotObject.IsInstanceValid(standing) && standing.IsInsideTree())
					{
						standing.QueueFree();
					}
				};
			}
			else
			{
				body.AddChild(proxy);
			}

			Proxies.AddOrUpdate(body, proxy);
			Active++;

			// Leaving the tree is the one event every ending shares: freed here, or with its body.
			// A proxy rebuilt on the other side of its body leaves AFTER its successor stands,
			// so it only unregisters itself.
			MeshInstance3D me = proxy;
			proxy.TreeExiting += () =>
			{
				Active--;
				if (GodotObject.IsInstanceValid(body) && ReferenceEquals(ProxyOf(body), me))
				{
					Proxied.Remove(bodyId);
					Engine_ShadowSkeletons.Forget(body);
					Proxies.Remove(body);
				}
				else if (!GodotObject.IsInstanceValid(body))
				{
					Proxied.Remove(bodyId);
				}
			};
		}

		proxy.Mesh = merged;
		uint targetLayers = ComputeProxyLayers(body.Layers, follows);
		bool layersChanged = proxy.Layers != targetLayers;
		if (layersChanged)
		{
			proxy.Layers = targetLayers;
		}

		// The body's screen-size cull is the caster's too: a range is per instance, not inherited.
		// For skinned proxies (NPCs, doors, chests, tables) whose body has no explicit draw cutoff,
		// cap the shadow proxy at 75 m so distant entities do not render into the far sun cascade.
		proxy.VisibilityRangeEnd = body.VisibilityRangeEnd > 0f
			? body.VisibilityRangeEnd
			: (skinned ? 75f : 0f);
		proxy.VisibilityRangeEndMargin = body.VisibilityRangeEnd > 0f
			? body.VisibilityRangeEndMargin
			: (skinned ? 8f : 0f);
		proxy.VisibilityRangeFadeMode = body.VisibilityRangeEnd > 0f
			? body.VisibilityRangeFadeMode
			: GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
		// And its dither band, where a tier-culled prop has one: the shadow fades with the body.
		Refii.Level.Engine_PropFade.Copy(body, proxy);

		// Same mesh space as the body (a sibling placed with its transform, or a child at
		// identity), so the body's stated box bounds the caster; an unset box copies as none.
		proxy.CustomAabb = body.CustomAabb;
		if (skinned && proxy.CustomAabb.Size.LengthSquared() < 1e-4f)
		{
			proxy.CustomAabb = new Aabb(new Vector3(-1.5f, -0.5f, -1.5f), new Vector3(3.0f, 3.0f, 3.0f));
		}

		if (!body.Skeleton.IsEmpty && body.GetNodeOrNull(body.Skeleton) is Skeleton3D bones)
		{
			// Not the body's skeleton: the proxy's own, under the proxy, posed at the guest's rate
			// (every frame for a principal) by the same writers — see Engine_ShadowSkeletons.
			Skeleton3D shadowBones = Engine_ShadowSkeletons.Ensure(body, proxy, bones);
			proxy.Skeleton = proxy.GetPathTo(shadowBones);
			proxy.Skin = body.Skin;
		}
		else
		{
			proxy.Skeleton = new NodePath();
			proxy.Skin = null;
			if (beside || follower)
			{
				// No bones (or awaiting rig): the cadence entry places the sibling proxy and mirrors its visibility.
				Engine_ShadowSkeletons.Follow(body, proxy, principalHolder);
			}
			else
			{
				Engine_ShadowSkeletons.Forget(body);
			}
		}

		if (layersChanged && follows)
		{
			Engine_ShadowSkeletons.Placed(body);
		}

		body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		Proxied.Add(bodyId);

		// A static rigid prop's shadow is withdrawn by size and distance; a character and any
		// moving attached follower (held/sheathed weapons) keep their own. A prop that rigs or
		// follows later takes its proxy back, cast-only and visibility-synced as it was.
		if (UsesStaticShadowReach(skinned, follows))
		{
			Refii.Lighting.Engine_ShadowReach.Adopt(proxy);
		}
		else
		{
			Refii.Lighting.Engine_ShadowReach.Release(proxy);
			proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
			if (beside)
			{
				proxy.Visible = body.IsVisibleInTree();
			}
		}
	}

	/// <param name="always">
	/// A caster even when merging saves nothing: the proxy exists for its cadence (a character, an
	/// attached child), not for its draw count. False keeps a rigid prop on its own shadow unless
	/// two or more surfaces become one.
	/// </param>
	private static ArrayMesh? MergedFor(ArrayMesh source, MeshInstance3D? body, UInt128 hidden, bool skinned, bool always)
	{
		Dictionary<(UInt128 Hidden, bool Always), ArrayMesh?> perMask = Cache.GetOrCreateValue(source);
		long from = System.Diagnostics.Stopwatch.GetTimestamp();
		lock (perMask)
		{
			long locked = System.Diagnostics.Stopwatch.GetTimestamp();
			_mergeWaitMs += (locked - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (perMask.TryGetValue((hidden, always), out ArrayMesh? cached))
			{
				return cached;
			}
		}

		long buildFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		ArrayMesh? built = Build(source, body, hidden, skinned, always);
		_mergeBuildMs += (System.Diagnostics.Stopwatch.GetTimestamp() - buildFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		long storeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		lock (perMask)
		{
			_mergeWaitMs += (System.Diagnostics.Stopwatch.GetTimestamp() - storeFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (perMask.TryGetValue((hidden, always), out ArrayMesh? raced))
			{
				return raced;
			}

			perMask[(hidden, always)] = built;
			return built;
		}
	}

	[ThreadStatic] private static double _mergeWaitMs;
	[ThreadStatic] private static double _mergeBuildMs;

	/// <summary>
	/// This thread's time in the caster merge since the last call: waiting for another thread
	/// building the same mesh, and building it here. Zeroes both.
	/// </summary>
	public static (double WaitMs, double BuildMs) TakeMergeTimes()
	{
		(double, double) taken = (_mergeWaitMs, _mergeBuildMs);
		_mergeWaitMs = 0;
		_mergeBuildMs = 0;
		return taken;
	}

	/// <summary>The cast-only mesh, or null when a surface cannot be merged and the body must keep casting.</summary>
	private static ArrayMesh? Build(ArrayMesh source, MeshInstance3D? body, UInt128 hidden, bool skinned, bool always)
	{
		var positions = new List<Vector3>();
		var bones = new List<int>();
		var weights = new List<float>();
		var indices = new List<int>();
		var kept = new List<(Godot.Collections.Array Arrays, Material Material, Godot.Collections.Dictionary? Lods)>();
		var levels = new List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)>();
		int merged = 0;

		for (int s = 0; s < source.GetSurfaceCount(); s++)
		{
			if ((hidden & (UInt128.One << s)) != UInt128.Zero)
			{
				continue;
			}

			if (source.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
			{
				return null;
			}

			Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(source, s);
			Vector3[] vertex = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			int[] index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
			int[] bone = skinned ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array() : System.Array.Empty<int>();
			float[] weight = skinned ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array() : System.Array.Empty<float>();
			if (vertex.Length == 0 || index.Length == 0 ||
				(skinned && (bone.Length != vertex.Length * 4 || weight.Length != vertex.Length * 4)))
			{
				return null;
			}

			Godot.Collections.Dictionary? lods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, s);

			// An override is only ever the collapse, on a body showing its source; hidden surfaces
			// were skipped above, so the source's own material is the one that casts.
			Material? material = source.SurfaceGetMaterial(s);
			if (material is ShaderMaterial shaded &&
				shaded.GetShaderParameter(AlphaScissor) is { VariantType: Variant.Type.Bool } scissor &&
				scissor.AsBool())
			{
				var own = new Godot.Collections.Array();
				own.Resize((int)Mesh.ArrayType.Max);
				own[(int)Mesh.ArrayType.Vertex] = vertex;
				own[(int)Mesh.ArrayType.TexUV] = arrays[(int)Mesh.ArrayType.TexUV];
				if (skinned)
				{
					own[(int)Mesh.ArrayType.Bones] = bone;
					own[(int)Mesh.ArrayType.Weights] = weight;
				}

				own[(int)Mesh.ArrayType.Index] = index;
				kept.Add((own, shaded, lods));
				continue;
			}

			int at = positions.Count;
			positions.AddRange(vertex);
			bones.AddRange(bone);
			weights.AddRange(weight);
			foreach (int i in index)
			{
				indices.Add(at + i);
			}

			var sorted = new List<(float Edge, int[] Indices)>();
			if (lods is not null)
			{
				foreach (Variant key in lods.Keys)
				{
					sorted.Add((key.AsSingle(), lods[key].AsInt32Array()));
				}

				sorted.Sort((a, b) => a.Edge.CompareTo(b.Edge));
			}

			levels.Add((at, index, sorted));
			merged++;
		}

		// A cadence proxy is built from whatever is there: one merged surface, or the kept
		// surfaces alone when the body's only surface is alpha-cut. A rigid prop's draw-saving
		// twin needs two surfaces to become one, or the prop keeps its own shadow.
		if (merged < 2 && !always)
		{
			return null;
		}

		if (merged == 0 && kept.Count == 0)
		{
			return null;
		}

		var result = new ArrayMesh();
		Godot.Collections.Array? one = null;
		Godot.Collections.Dictionary? mergedLods = null;
		ShaderMaterial? casterMat = null;
		long estBytes = 4096L;
		if (merged != 0)
		{
			one = new Godot.Collections.Array();
			one.Resize((int)Mesh.ArrayType.Max);
			one[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
			if (skinned)
			{
				one[(int)Mesh.ArrayType.Bones] = bones.ToArray();
				one[(int)Mesh.ArrayType.Weights] = weights.ToArray();
			}

			one[(int)Mesh.ArrayType.Index] = indices.ToArray();
			mergedLods = MergedLods(levels);
			casterMat = UsesSolidProxyCaster(skinned, always) ? SolidCasterMaterial.Value : Caster;
			estBytes = System.Math.Max(4096L, positions.Count * 24L + indices.Count * 4L);
		}

		for (int i = 0; i < kept.Count; i++)
		{
			estBytes += Refii.Core.Engine_ArrayMeshes.EstimateSurfaceBytes(kept[i].Arrays);
		}

		if (Refii.Core.Engine_RdHealth.IsDeviceLost)
		{
			Refii.Core.Engine_RdHealth.NoteSuppressed();
			return null;
		}

		bool aborted = false;
		Refii.Core.Engine_ServerSyncs.RunGpuUpload(estBytes, () =>
		{
			if (Refii.Core.Engine_RdHealth.IsDeviceLost)
			{
				Refii.Core.Engine_RdHealth.NoteSuppressed();
				aborted = true;
				return;
			}

			if (one is not null && casterMat is not null)
			{
				int countBefore = result.GetSurfaceCount();
				Refii.Core.Engine_ArrayMeshes.AddSurfaceDirect(result, Mesh.PrimitiveType.Triangles, one, null, mergedLods);
				if (result.GetSurfaceCount() <= countBefore)
				{
					aborted = true;
					return;
				}
				result.SurfaceSetMaterial(countBefore, casterMat);
			}

			foreach ((Godot.Collections.Array arrays, Material material, Godot.Collections.Dictionary? lods) in kept)
			{
				if (Refii.Core.Engine_RdHealth.IsDeviceLost)
				{
					aborted = true;
					return;
				}
				int countBefore = result.GetSurfaceCount();
				Refii.Core.Engine_ArrayMeshes.AddSurfaceDirect(result, Mesh.PrimitiveType.Triangles, arrays, null, lods);
				int countAfter = result.GetSurfaceCount();
				if (countAfter <= countBefore)
				{
					aborted = true;
					return;
				}
				Material surfaceMaterial = material is ShaderMaterial shadedMat &&
					CanUseDedicatedCutoutCaster(
						skinned,
						always,
						Refii.Models.Engine_ShaderVariant.FullPathOf(
							Refii.Models.Engine_ShaderSpecialisation.PathOf(shadedMat.Shader)))
					? GetOrCreateCutoutCaster(shadedMat)
					: material;
				result.SurfaceSetMaterial(countAfter - 1, surfaceMaterial);
			}
		});

		if (aborted || Refii.Core.Engine_RdHealth.IsDeviceLost || result.GetSurfaceCount() == 0)
		{
			return null;
		}

		Built++;
		SurfacesMerged += merged;
		return result;
	}

	/// <summary>
	/// Level k of the merged surface: each source's level k, its coarsest where it has fewer, its
	/// full index list where it has none; keyed by the largest edge length among them. Null when
	/// no source has any.
	/// </summary>
	internal static Godot.Collections.Dictionary? MergedLods(List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)> sources)
	{
		int depth = 0;
		foreach ((_, _, List<(float Edge, int[] Indices)> lods) in sources)
		{
			depth = System.Math.Max(depth, lods.Count);
		}

		if (depth == 0)
		{
			return null;
		}

		var result = new Godot.Collections.Dictionary();
		float lastEdge = 0f;
		for (int k = 0; k < depth; k++)
		{
			float edge = 0f;
			var level = new List<int>();
			foreach ((int at, int[] full, List<(float Edge, int[] Indices)> lods) in sources)
			{
				int[] take = full;
				if (lods.Count != 0)
				{
					(float Edge, int[] Indices) own = lods[System.Math.Min(k, lods.Count - 1)];
					take = own.Indices;
					edge = System.Math.Max(edge, own.Edge);
				}

				foreach (int i in take)
				{
					level.Add(at + i);
				}
			}

			// Keys must rise; two levels that tie would overwrite one another.
			if (edge <= lastEdge)
			{
				continue;
			}

			lastEdge = edge;
			result[edge] = level.ToArray();
		}

		return result.Count == 0 ? null : result;
	}
}
