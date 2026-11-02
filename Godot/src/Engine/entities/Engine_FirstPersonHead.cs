using Godot;

namespace Refii.Entities;

/// <summary>
/// A hero's head, hair and hat, gone while that hero's player has the first-person camera: the
/// camera mode the game states for the player is CAMERA_FIRST_PERSON (<see cref="Engine_FirstPersonView"/>)
/// — the game's own first-person look (the left bumper, View on the touch controls) and a
/// first-person camera override (a mod's <c>camera_override</c> hold, a quest's first-person shot)
/// alike, all of which put the camera at the head.
/// </summary>
/// <remarks>
/// FOR A FIRST PERSON THE GAME DOES NOT FADE. Where the game's cameras fade the hero the stated
/// opacity hides all of it, hair and hat included (Engine_EntityAppearance): the shipped
/// FirstPersonCamera:FrameUpdate calls FadeEntityBasedOnPositionIfNeeded(self.Player, 1.5, 0, 3),
/// which sets camera alpha max(0, (d - 0.75) / 0.75) = 0 with the camera at the head, and a
/// SimpleCamera's SourceEntityToHide and the occlusion pull-ins take the same path. A first-person
/// hold that keeps the body in view leaves that fade out (the First Person mod, mod/camera.lua:
/// "No fade: the body stays in view"), so the hero is drawn opaque and nothing the game states
/// takes the head away from around the eye; this does, keyed on the stated mode, not the opacity,
/// so the game's own look (faded to 0 at once) is unchanged by it.
///
/// So a SkeletonModifier3D on the hero's body skeleton states, while the mode holds, the neck bone
/// (and with it the head and every face bone) hidden in its player's view, and the rig's
/// <see cref="Engine_HiddenPartModifier"/> (made beside it, kept after it) draws it at nothing there
/// after the game has posed it. Drawing only: the game's pose is never written, and the shadow proxy
/// has its own skeleton, so the shadow keeps its head. The mode is read from
/// <c>RefiiHost.camera_mode</c>, which the host hears whether or not a mod script is loaded; the
/// player is restated by the entity walk on every pass.
///
/// SPLIT SCREEN HIDES IT IN ONE VIEW (<see cref="Engine_ViewHides"/>).
/// Both views draw the one skeleton, so there the head goes by instance: a <see cref="Engine_ViewTwin"/>
/// collapsing the neck subtree — and any part a mod hides for the same player — is drawn in the
/// player's own view alone. On one view the head goes from the pose. Either player, or both, may be
/// in first person; each hero answers to its own player.
/// </remarks>
public partial class Engine_FirstPersonHead : SkeletonModifier3D
{
	public const string NodeName = "FirstPersonHead";

	private int _bone = -1;
	private int _player;
	private Engine_HiddenPartModifier? _parts;

	/// <summary>
	/// Gives <paramref name="skeleton"/> a head hider unless it has one, and states whose hero it is
	/// (<paramref name="player"/> 1 or 2, 0 for none); false when it has no head bone. The rig's
	/// <see cref="Engine_HiddenPartModifier"/>, which draws the head away, is made with it.
	/// </summary>
	public static bool Ensure(Skeleton3D skeleton, int player)
	{
		if (skeleton.GetNodeOrNull<Engine_FirstPersonHead>(NodeName) is { } hider)
		{
			hider._player = player;
			hider._parts = Engine_HiddenPartModifier.Ensure(skeleton);
			return true;
		}

		int head = HeadBone(skeleton);
		if (head < 0)
		{
			return false;
		}

		// The back of the head and the neck are skinned to the neck bone: shrinking only the head
		// left them stretched up under the camera.
		int bone = head;
		int parent = skeleton.GetBoneParent(head);
		if (parent >= 0 && (skeleton.GetBoneName(parent).ToLowerInvariant().EndsWith("neck") ||
			Refii.Core.Engine_TitleGame.Active.Rig.Is(skeleton.GetBoneName(parent), Refii.Animations.Engine_TitleRig.RigRole.Neck)))
		{
			bone = parent;
		}

		var made = new Engine_FirstPersonHead { Name = NodeName, _bone = bone, _player = player };
		skeleton.AddChild(made);
		made._parts = Engine_HiddenPartModifier.Ensure(skeleton);
		return true;
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		Skeleton3D? skeleton = GetSkeleton();
		if (skeleton is null || _bone < 0 || _parts is not { } parts || !IsInstanceValid(parts))
		{
			return;
		}

		int mode = _player is 1 or 2 && Refii.Mods.ModGodot.Host is { } host ? host.camera_mode(_player) : -1;
		bool hidden = !Refii.Game.GameWorld.WorldBuildHeld && Engine_FirstPersonView.HeadHidden(_player, mode);
		parts.StateHead(hidden ? _player : 0, _bone);
	}

	/// <summary>
	/// The head bone the face, jaw, eyes, hair and hat hang off: the topmost bone named for the
	/// head, else the title's head (<see cref="Refii.Animations.Engine_TitleRig.RigRole.Head"/>) on
	/// a skeleton whose bones are named by hash.
	/// </summary>
	private static int HeadBone(Skeleton3D skeleton)
	{
		for (int b = 0; b < skeleton.GetBoneCount(); b++)
		{
			if (!skeleton.GetBoneName(b).ToLowerInvariant().EndsWith("head"))
			{
				continue;
			}

			int parent = skeleton.GetBoneParent(b);
			if (parent < 0 || !skeleton.GetBoneName(parent).ToLowerInvariant().EndsWith("head"))
			{
				return b;
			}
		}

		return Refii.Core.Engine_TitleGame.Active.Rig.Bone(skeleton, Refii.Animations.Engine_TitleRig.RigRole.Head);
	}
}
