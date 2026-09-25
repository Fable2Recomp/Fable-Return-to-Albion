using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Animations;

[Tool]
[GlobalClass]
public partial class Engine_EntityAnimator : Node3D
{
	public const string NodeName = "Animator";

	public const string LibraryName = "fable2";

	public const string PlayerName = "AnimationPlayer";

	public const int DefaultClipLimit = 24;

	private bool _built;

	[Export]
	public int ClipLimit { get; set; } = DefaultClipLimit;

	[Export]
	public bool RootMotion { get; set; } = true;

	[Export]
	public bool Rebuild
	{
		get => false;
		set
		{
			if (value)
			{
				Clear();
				Build();
			}
		}
	}

	public Skeleton3D? Skeleton => GetNodeOrNull<Skeleton3D>(Engine_AnimSkeleton.SkeletonName);

	public AnimationPlayer? Player => GetNodeOrNull<AnimationPlayer>(PlayerName);

	public override void _Ready() => Build();

	public void Clear()
	{
		_built = false;
		Unbind();

		foreach (Node child in GetChildren())
		{
			RemoveChild(child);
			child.QueueFree();
		}
	}

	public void Build()
	{
		if (_built || Player is not null)
		{
			return;
		}

		string[] models = ModelPaths();
		if (models.Length == 0)
		{
			_built = true;
			return;
		}

		Engine_MdlRig? composite = CompositeRig();
		MdlInfo? info = composite is null ? FirstRig(models) : null;

		if (composite is null && info is null)
		{
			return;
		}

		if (!Engine_AnimCatalog.EnsureLoaded())
		{
			return;
		}

		uint[] hashes = ModelHashes();
		if (hashes.Length == 0)
		{
			_built = true;
			return;
		}

		var timer = System.Diagnostics.Stopwatch.StartNew();

		Skeleton3D? skeleton = Skeleton;
		if (skeleton is null)
		{
			skeleton = composite is { } rig
				? Engine_AnimSkeleton.Build(rig)
				: Engine_AnimSkeleton.Build(info!);

			if (skeleton is null)
			{
				_built = true;
				return;
			}

			AddChild(skeleton);
		}

		var library = new AnimationLibrary();
		var seen = new HashSet<int>();
		int added = 0;

		foreach (uint modelHash in hashes)
		{
			foreach (ModelAnimationBinding binding in
				Engine_AnimCatalog.BindingsForModelHash(modelHash))
			{
				if (added >= ClipLimit)
				{
					break;
				}

				if (!seen.Add(binding.ClipIndex))
				{
					continue;
				}

				AnimClip? clip = Engine_AnimCatalog.ClipAt(binding.ClipIndex);
				if (clip is null)
				{
					continue;
				}

				Animation? animation = Engine_AnimBuilder.Build(
					clip, Engine_AnimCatalog.ActiveDecoder, skeleton,
					Engine_AnimSkeleton.SkeletonName);

				if (animation is null)
				{
					continue;
				}

				string label = binding.AnimationName.Length != 0
					? binding.AnimationName
					: clip.Name;

				string name = Engine_AnimNaming.CleanClipName(label);
				if (library.HasAnimation(name))
				{
					name = $"{name}_{added}";
				}

				library.AddAnimation(name, animation);
				added++;
			}

			if (added >= ClipLimit)
			{
				break;
			}
		}

		_built = added != 0 || Engine_AnimCatalog.BindingsReady;

		if (added == 0)
		{
			GD.Print($"[anim] no clips for {GetParent()?.Name ?? Name}: " +
				$"catalog={Engine_AnimCatalog.AllClips.Count} clip(s), " +
				$"bindingsReady={Engine_AnimCatalog.BindingsReady}, " +
				$"bindings={Engine_AnimCatalog.AllBindings.Count}, " +
				$"modelHashes={hashes.Length}, matched=0");
			return;
		}

		var player = new AnimationPlayer { Name = PlayerName };
		AddChild(player);
		player.AddAnimationLibrary(LibraryName, library);

		if (RootMotion && RootBone(skeleton) is string rootBone)
		{
			player.RootMotionTrack = $"{Engine_AnimSkeleton.SkeletonName}:{rootBone}";
		}

		Bind(skeleton);

		GD.Print($"[anim] {added} clip(s), {skeleton.GetBoneCount()} bone(s) on " +
			$"{GetParent()?.Name ?? Name} in {timer.ElapsedMilliseconds} ms");
	}

	/// <summary>
	/// Puts one NAMED clip in the library, building it if it is not there yet.
	///
	/// WHY ON DEMAND. <see cref="Build"/> takes the first <see cref="ClipLimit"/> bindings it
	/// happens to walk, which is fine for "show me this character moving" and useless for
	/// locomotion: the states ask for `Run`, `Walk` and `Pose` by name, and whether those made
	/// the first 24 is luck. Locomotion asks for exactly what it needs instead.
	///
	/// Returns the library key, or null when the entity's models carry no binding of that name —
	/// which is a real answer for a clip that character genuinely does not have, not a failure.
	/// </summary>
	public string? EnsureClip(string animationName)
	{
		if (animationName.Length == 0)
		{
			return null;
		}

		AnimationPlayer? player = EnsurePlayer();
		Skeleton3D? skeleton = Skeleton;
		if (player is null || skeleton is null)
		{
			return null;
		}

		return Engine_ClipLibrary.Ensure(
			player.GetAnimationLibrary(LibraryName), skeleton,
			Engine_AnimSkeleton.SkeletonName, ModelHashes(), animationName);
	}

	/// <summary>Plays a clip BY ITS SHIPPED NAME, blending over <paramref name="blend"/> seconds.</summary>
	public bool PlayClip(string animationName, float blend)
	{
		if (EnsureClip(animationName) is not string key || Player is not { } player)
		{
			return false;
		}

		string path = $"{LibraryName}/{key}";
		if (player.CurrentAnimation == path)
		{
			return true;
		}

		player.Play(path, blend);
		return true;
	}

	/// <summary>
	/// The player, created if <see cref="Build"/> made a skeleton but found no clips worth
	/// adding. Without this an entity whose bindings all fell outside the clip limit would have
	/// a perfectly good skeleton and nothing able to drive it.
	/// </summary>
	private AnimationPlayer? EnsurePlayer()
	{
		Build();

		if (Player is { } existing)
		{
			return existing;
		}

		if (Skeleton is null)
		{
			return null;
		}

		var player = new AnimationPlayer { Name = PlayerName };
		AddChild(player);
		player.AddAnimationLibrary(LibraryName, new AnimationLibrary());

		if (RootMotion && RootBone(Skeleton) is string rootBone)
		{
			player.RootMotionTrack = $"{Engine_AnimSkeleton.SkeletonName}:{rootBone}";
		}

		return player;
	}

	private void Bind(Skeleton3D skeleton)
	{
		if (GetParent() is not MeshInstance3D surface)
		{
			return;
		}

		if (surface is Entities.Engine_EntityInstance entity)
		{
			entity.EnsureMesh();
		}

		if (Entities.Engine_BodyVariants.SourceMesh(surface) is not { } source)
		{
			return;
		}

		if (!FullySkinned(source))
		{
			GD.Print($"[anim] {Name}: mesh has unskinned surfaces, leaving it unbound");
			return;
		}

		surface.Skin = skeleton.CreateSkinFromRestTransforms();
		surface.Skeleton = surface.GetPathTo(skeleton);
	}

	public void Rebind()
	{
		Skeleton3D? skeleton = Skeleton;
		if (skeleton is null || GetParent() is not MeshInstance3D surface)
		{
			return;
		}

		if (Engine_MdlRig.TryFrom(Entities.Engine_BodyVariants.SourceMesh(surface)) is not Engine_MdlRig rig ||
			rig.BoneCount != skeleton.GetBoneCount())
		{
			Bind(skeleton);
			return;
		}

		int root = RootIndex(skeleton);
		Vector3 before = root >= 0 ? skeleton.GetBoneRest(root).Origin : Vector3.Zero;

		Engine_AnimSkeleton.ApplyRests(skeleton, rig);

		if (root >= 0 && !skeleton.GetBoneRest(root).Origin.IsEqualApprox(before))
		{
			Clear();
			Build();
			return;
		}

		Bind(skeleton);
	}

	private static int RootIndex(Skeleton3D skeleton)
	{
		for (int bone = 0; bone < skeleton.GetBoneCount(); bone++)
		{
			if (skeleton.GetBoneParent(bone) < 0)
			{
				return bone;
			}
		}

		return -1;
	}

	private static string? RootBone(Skeleton3D skeleton)
	{
		int root = RootIndex(skeleton);
		return root < 0 ? null : skeleton.GetBoneName(root);
	}

	private static bool FullySkinned(Mesh mesh)
	{
		for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
		{
			if (!Refii.Models.Engine_MdlSurfaceArrays.Skinned(mesh, surface))
			{
				return false;
			}
		}

		return true;
	}

	private void Unbind()
	{
		if (GetParent() is MeshInstance3D surface)
		{
			surface.Skin = null;
			surface.Skeleton = new NodePath("..");
		}
	}

	private Entities.Engine_EntityResource? Resource =>
		GetParent() is MeshInstance3D surface
			? Entities.Engine_BodyVariants.SourceMesh(surface) as Entities.Engine_EntityResource
			: null;

	private uint[] ModelHashes()
	{
		foreach (Node? candidate in new[] { GetParent(), this })
		{
			if (candidate is null ||
				!candidate.HasMeta(Entities.Engine_EntityInstance.ModelHashesMeta))
			{
				continue;
			}

			long[] raw = candidate
				.GetMeta(Entities.Engine_EntityInstance.ModelHashesMeta)
				.AsInt64Array();

			var hashes = new uint[raw.Length];
			for (int i = 0; i < raw.Length; i++)
			{
				hashes[i] = unchecked((uint)raw[i]);
			}

			return hashes;
		}

		return Resource?.ModelHashes.ToArray() ?? System.Array.Empty<uint>();
	}

	private string[] ModelPaths()
	{
		foreach (Node? candidate in new[] { GetParent(), this })
		{
			if (candidate is not null &&
				candidate.HasMeta(Entities.Engine_EntityInstance.ModelsMeta))
			{
				return candidate
					.GetMeta(Entities.Engine_EntityInstance.ModelsMeta)
					.AsStringArray();
			}
		}

		return Resource?.ModelPaths.ToArray() ?? System.Array.Empty<string>();
	}

	private Engine_MdlRig? CompositeRig()
	{
		if (GetParent() is not MeshInstance3D surface)
		{
			return null;
		}

		if (surface is Entities.Engine_EntityInstance entity)
		{
			entity.EnsureMesh();
		}

		return Engine_MdlRig.TryFrom(Entities.Engine_BodyVariants.SourceMesh(surface));
	}

	private static MdlInfo? FirstRig(string[] models)
	{
		MdlInfo? best = null;

		foreach (string path in models)
		{
			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path);
			if (bytes is null)
			{
				continue;
			}

			if (!Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _))
			{
				continue;
			}

			if (!info.HasBoneTransforms || info.Bones.Count == 0)
			{
				continue;
			}

			if (best is null || info.Bones.Count > best.Bones.Count)
			{
				best = info;
			}
		}

		return best;
	}
}
