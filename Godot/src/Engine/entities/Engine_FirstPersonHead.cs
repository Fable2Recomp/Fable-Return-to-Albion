using Godot;

namespace Refii.Entities;

/// <summary>
/// A hero's head, hair and hat, gone while the camera is inside them: the game's own first-person
/// look (the left bumper, View on the touch controls) puts the camera in the hero's head.
/// </summary>
/// <remarks>
/// WHY NOT THE FADE. In first person the guest fades the hero by its camera alpha (0), and the
/// body node goes fully transparent (Engine_EntityAppearance). But the hair, the hat and the
/// lashes are cutout (alpha-scissor) surfaces, and Godot sets a cutout fragment's alpha to 1
/// after its test, so the instance's transparency never reaches them: they filled the view, seen
/// from inside.
///
/// So, as the First Person mod does for its own camera, a SkeletonModifier3D on the hero's body
/// skeleton shrinks the neck bone (and with it the head and every face bone) to nothing after the
/// game has posed it, while the camera is within <see cref="Radius"/> of the head. Drawing only:
/// the game's pose is never written, and the shadow proxy has its own skeleton, so the shadow
/// keeps its head. Third person never comes that close.
/// </remarks>
public partial class Engine_FirstPersonHead : SkeletonModifier3D
{
	public const string NodeName = "FirstPersonHead";

	/// <summary>Metres from the head within which the camera counts as inside it.</summary>
	public const float Radius = 0.6f;

	private int _head = -1;
	private int _bone = -1;

	/// <summary>Gives <paramref name="skeleton"/> a head hider unless it has one; false when it has no head bone.</summary>
	public static bool Ensure(Skeleton3D skeleton)
	{
		if (skeleton.GetNodeOrNull(NodeName) is not null)
		{
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
		if (parent >= 0 && skeleton.GetBoneName(parent).ToLowerInvariant().EndsWith("neck"))
		{
			bone = parent;
		}

		skeleton.AddChild(new Engine_FirstPersonHead { Name = NodeName, _head = head, _bone = bone });
		return true;
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		Skeleton3D? skeleton = GetSkeleton();
		Camera3D? camera = IsInsideTree() ? GetViewport().GetCamera3D() : null;
		if (skeleton is null || camera is null || _head < 0 || _bone < 0)
		{
			return;
		}

		Vector3 eyes = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(_head).Origin;
		if (eyes.DistanceTo(camera.GlobalPosition) < Radius)
		{
			skeleton.SetBonePoseScale(_bone, Vector3.One * 0.001f);
		}
	}

	/// <summary>The topmost bone named for the head (Shadow_Head): the face, jaw, eyes, hair and hat hang off it.</summary>
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

		return -1;
	}
}
