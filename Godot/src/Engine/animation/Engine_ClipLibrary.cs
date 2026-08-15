using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Animations;

/// <summary>
/// Builds one named clip into an <see cref="AnimationLibrary"/>, against a given skeleton.
///
/// ONE IMPLEMENTATION, TWO CALLERS. The editor's per-entity animator and the live locomotion
/// animator both need "put the clip called `Run` in this library", and they are looking at
/// DIFFERENT skeletons: the animator builds its own from the model, while a live character is
/// already carrying the one <see cref="Refii.Entities.Engine_EntitySkeleton"/> made and skinned
/// the mesh to. The skeleton is therefore a parameter and not something this decides, which is
/// what lets both go through here instead of growing a second copy of the lookup.
/// </summary>
public static class Engine_ClipLibrary
{
	/// <summary>
	/// Ensures <paramref name="animationName"/> is present, and returns its library key.
	///
	/// Null means the models carry no binding of that name. That is a real answer — a rig that
	/// genuinely has no `Sprint` should fall through to the next clip the state offers, not
	/// stall — so callers treat it as "try the next one", never as an error.
	/// </summary>
	public static string? Ensure(
		AnimationLibrary library,
		Skeleton3D skeleton,
		string skeletonName,
		IEnumerable<uint> modelHashes,
		string animationName)
	{
		if (animationName.Length == 0)
		{
			return null;
		}

		string key = Engine_AnimNaming.CleanClipName(animationName);
		if (library.HasAnimation(key))
		{
			return key;
		}

		if (!Engine_AnimCatalog.EnsureLoaded())
		{
			return null;
		}

		foreach (uint modelHash in modelHashes)
		{
			foreach (ModelAnimationBinding binding in
				Engine_AnimCatalog.BindingsForModelHash(modelHash))
			{
				if (!string.Equals(binding.AnimationName, animationName,
					System.StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				AnimClip? clip = Engine_AnimCatalog.ClipAt(binding.ClipIndex);
				if (clip is null)
				{
					continue;
				}

				Animation? animation = Engine_AnimBuilder.Build(
					clip, Engine_AnimCatalog.ActiveDecoder, skeleton, skeletonName);

				if (animation is null)
				{
					continue;
				}

				animation.LoopMode = Loops(animationName)
					? Animation.LoopModeEnum.Linear
					: Animation.LoopModeEnum.None;

				library.AddAnimation(key, animation);
				return key;
			}
		}

		return null;
	}

	/// <summary>
	/// Whether a locomotion clip cycles or plays once.
	///
	/// THE RULE IS THE CLIP'S NAME, which is unusual enough to be worth stating: the guest's
	/// own clip builder decides looping with three case-insensitive substring tests — a name
	/// containing `into`, `outof` or `turn` is a one-shot, everything else loops. So `Run` and
	/// `Walk` cycle while `Turn90Right` and `WalkFootstepOutofShort` run once and stop, which is
	/// what makes a transition end instead of stuttering forever.
	///
	/// This one is taken from a report of `sub_827F6EB0` rather than from a decompile I read
	/// myself. It is a naming heuristic rather than a struct offset, so a wrong call here costs
	/// a clip that loops when it should not — visible immediately, and not a corrupt read.
	/// </summary>
	private static bool Loops(string animationName)
	{
		foreach (string oneShot in OneShotMarkers)
		{
			if (animationName.Contains(oneShot, System.StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private static readonly string[] OneShotMarkers = { "into", "outof", "turn" };
}
