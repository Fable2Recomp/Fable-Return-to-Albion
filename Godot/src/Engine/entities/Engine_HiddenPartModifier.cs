using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// Draws a rig's hidden bones at nothing — a mod's parts (<see cref="Engine_HiddenParts"/>) and the
/// first-person head (<see cref="Engine_FirstPersonHead"/>) — in every view or in one player's view,
/// as <see cref="Engine_ViewHides"/> plans them: a <see cref="SkeletonModifier3D"/> on the body
/// skeleton that, after the game has posed it, shrinks each pose bone (and with it every bone under
/// it) to nothing, and stands a <see cref="Engine_ViewTwin"/> in each split view that hides bones of
/// its own.
/// </summary>
/// <remarks>
/// Drawing only: the pose the game holds is untouched, and the shadow proxy has its own skeleton
/// (Engine_ShadowSkeletons), so the shadow keeps the part. A bone is named as the rig's bone table
/// names it; an exact match first, then one ignoring case. It runs after the skeleton's other
/// modifiers (it is kept the last child), so the head hider has stated its head for this frame and
/// no later modifier puts a shrunk bone back. With nothing hidden it does nothing: no twin, no pose
/// write, the body's instance mask its own.
/// </remarks>
public partial class Engine_HiddenPartModifier : SkeletonModifier3D
{
	public const string NodeName = "HiddenParts";

	private uint _handle;
	private int _revision = -1;
	private int _resolved;
	private readonly List<int> _every = new();
	private readonly List<int> _one = new();
	private readonly List<int> _two = new();
	private int _headPlayer;
	private int _headBone = -1;
	private (int Resolved, int Head, int HeadBone, bool PerView, bool OneViewed, bool TwoViewed) _planKey = (-1, 0, -1, false, false, false);
	private Engine_ViewHides.Plan _plan = Engine_ViewHides.Plan.None;
	private int[] _posed = Array.Empty<int>();
	private readonly Engine_ViewTwin?[] _twins = new Engine_ViewTwin?[2];
	private MeshInstance3D? _narrowed;

	private static ulong _mapFrame = ulong.MaxValue;
	private static readonly Dictionary<uint, uint> HandleByAddress = new();

	/// <summary>The skeleton's modifier, made when it has none, kept its last child.</summary>
	public static Engine_HiddenPartModifier Ensure(Skeleton3D skeleton)
	{
		var modifier = skeleton.GetNodeOrNull<Engine_HiddenPartModifier>(NodeName);
		if (modifier is null)
		{
			modifier = new Engine_HiddenPartModifier { Name = NodeName };
			skeleton.AddChild(modifier);
		}

		if (modifier.GetIndex() != skeleton.GetChildCount() - 1)
		{
			skeleton.MoveChild(modifier, -1);
		}

		return modifier;
	}

	/// <summary>
	/// Gives the skeleton of the entity at <paramref name="address"/> its modifier when a part of the
	/// entity is hidden, and states the entity's handle. Called by the entity walk for every rig; cheap
	/// while nothing is hidden.
	/// </summary>
	public static void EnsureFor(Skeleton3D skeleton, uint address)
	{
		uint handle = HandleOf(address);
		Engine_HiddenPartModifier? modifier = handle == 0
			? skeleton.GetNodeOrNull<Engine_HiddenPartModifier>(NodeName)
			: Ensure(skeleton);
		if (modifier is not null && modifier._handle != handle)
		{
			modifier._handle = handle;
			modifier._revision = -1;
		}
	}

	/// <summary>
	/// States this frame's first-person head: <paramref name="bone"/> hidden in
	/// <paramref name="player"/>'s view (1 or 2), or no head hidden (0). Stated by the head hider,
	/// which runs before this modifier.
	/// </summary>
	public void StateHead(int player, int bone)
	{
		_headPlayer = player is 1 or 2 ? player : 0;
		_headBone = bone;
	}

	/// <summary>The handle of the entity at <paramref name="address"/> when a part of it is hidden, else 0; the map is built once a frame.</summary>
	private static uint HandleOf(uint address)
	{
		if (Engine_HiddenParts.Count == 0)
		{
			return 0;
		}

		ulong frame = Godot.Engine.GetProcessFrames();
		if (frame != _mapFrame)
		{
			_mapFrame = frame;
			HandleByAddress.Clear();
			foreach (uint handle in Engine_HiddenParts.Handles())
			{
				uint at = Refii.Host.HostBridge.ModHandleAddress(handle);
				if (at != 0)
				{
					HandleByAddress[at] = handle;
				}
			}
		}

		return HandleByAddress.TryGetValue(address, out uint found) ? found : 0;
	}

	public override void _EnterTree() => Refii.Core.Engine_Views.ViewsChanged += OnViewsChanged;

	public override void _ExitTree()
	{
		Refii.Core.Engine_Views.ViewsChanged -= OnViewsChanged;
		DropTwins();
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		Skeleton3D? skeleton = GetSkeleton();
		if (skeleton is null)
		{
			return;
		}

		int revision = Engine_HiddenParts.Revision;
		if (revision != _revision)
		{
			_revision = revision;
			++_resolved;
			Resolve(skeleton, _every, 0);
			Resolve(skeleton, _one, 1);
			Resolve(skeleton, _two, 2);
		}

		int bones = skeleton.GetBoneCount();
		int head = _headPlayer != 0 && _headBone >= 0 && _headBone < bones ? _headPlayer : 0;
		if (head == 0 && _every.Count == 0 && _one.Count == 0 && _two.Count == 0 &&
			_posed.Length == 0 && _twins[0] is null && _twins[1] is null && _narrowed is null)
		{
			return;
		}

		var body = skeleton.GetParent() as MeshInstance3D;
		bool perView = Refii.Core.Engine_Views.Split && body is { Skin: not null };
		Refii.Core.Engine_TitleGame title = Refii.Core.Engine_TitleGame.Active;
		bool oneViewed = Engine_ViewHides.Viewed(1, title.LocalCoop, title.OnlineClient);
		bool twoViewed = Engine_ViewHides.Viewed(2, title.LocalCoop, title.OnlineClient);
		var key = (_resolved, head, _headBone, perView, oneViewed, twoViewed);
		if (key != _planKey)
		{
			_planKey = key;
			_plan = Plan(skeleton, head, perView, oneViewed, twoViewed);
		}

		Pose(skeleton, _plan.Pose);
		Twins(skeleton, body, _plan);
	}

	private Engine_ViewHides.Plan Plan(Skeleton3D skeleton, int head, bool perView, bool oneViewed, bool twoViewed)
	{
		var one = new List<int>(_one);
		var two = new List<int>(_two);
		if (head != 0)
		{
			(head == 1 ? one : two).Add(_headBone);
		}

		var parents = new int[skeleton.GetBoneCount()];
		for (int b = 0; b < parents.Length; b++)
		{
			parents[b] = skeleton.GetBoneParent(b);
		}

		return Engine_ViewHides.Compose(_every, one, two, perView, oneViewed, twoViewed, parents);
	}

	/// <summary>The pose bones shrunk to nothing; a bone that left the pose is put back.</summary>
	private void Pose(Skeleton3D skeleton, int[] pose)
	{
		int bones = skeleton.GetBoneCount();
		if (!ReferenceEquals(pose, _posed))
		{
			foreach (int bone in _posed)
			{
				if (Array.IndexOf(pose, bone) < 0 && bone < bones)
				{
					skeleton.SetBonePoseScale(bone, Vector3.One);
				}
			}

			_posed = pose;
		}

		foreach (int bone in pose)
		{
			if (bone < bones)
			{
				skeleton.SetBonePoseScale(bone, Vector3.One * Engine_ViewTwin.Shrunk);
			}
		}
	}

	/// <summary>A twin in each view that hides bones of its own, and the body in the views without one.</summary>
	private void Twins(Skeleton3D skeleton, MeshInstance3D? body, Engine_ViewHides.Plan plan)
	{
		for (int view = 0; view < _twins.Length; view++)
		{
			int[] roots = plan.View(view);
			Engine_ViewTwin? twin = _twins[view];
			if (twin is not null && (roots.Length == 0 || body is null || !twin.Collapses(roots)))
			{
				twin.Release();
				twin = null;
			}

			if (roots.Length > 0 && body is not null)
			{
				twin ??= Engine_ViewTwin.Create(body, skeleton, roots);
				twin?.Update(view);
			}

			_twins[view] = twin;
		}

		bool first = _twins[0] is not null, second = _twins[1] is not null;
		if (body is not null && (first || second))
		{
			if (_narrowed is not null && !ReferenceEquals(_narrowed, body))
			{
				Engine_ViewTwin.DrawBody(_narrowed, false, false);
			}

			Engine_ViewTwin.DrawBody(body, first, second);
			_narrowed = body;
		}
		else if (_narrowed is not null)
		{
			Engine_ViewTwin.DrawBody(_narrowed, false, false);
			_narrowed = null;
		}
	}

	/// <summary>A rejoined screen has one view: the twins go, and the next pass plans for it.</summary>
	private void OnViewsChanged()
	{
		if (!Refii.Core.Engine_Views.Split)
		{
			DropTwins();
		}
	}

	private void DropTwins()
	{
		for (int view = 0; view < _twins.Length; view++)
		{
			_twins[view]?.Release();
			_twins[view] = null;
		}

		if (_narrowed is not null)
		{
			Engine_ViewTwin.DrawBody(_narrowed, false, false);
			_narrowed = null;
		}

		_planKey = (-1, 0, -1, false, false, false);
	}

	/// <summary><paramref name="into"/> the bones of the parts hidden on the handle for <paramref name="player"/> (0: every view).</summary>
	private void Resolve(Skeleton3D skeleton, List<int> into, int player)
	{
		into.Clear();
		if (_handle == 0)
		{
			return;
		}

		foreach (string part in Engine_HiddenParts.Of(_handle, player))
		{
			int bone = Bone(skeleton, part);
			if (bone >= 0 && !into.Contains(bone))
			{
				into.Add(bone);
			}
		}
	}

	/// <summary>
	/// The bone named <paramref name="part"/>: exact, else ignoring case, else the skeleton's name
	/// for a rig name (<see cref="Refii.Animations.Engine_TitleRig.BoneName"/>, a title whose bones
	/// are named by hash); -1 for none.
	/// </summary>
	private static int Bone(Skeleton3D skeleton, string part)
	{
		int exact = Named(skeleton, part);
		if (exact >= 0)
		{
			return exact;
		}

		string bone = Refii.Core.Engine_TitleGame.Active.Rig.BoneName(part);
		return bone == part ? -1 : Named(skeleton, bone);
	}

	private static int Named(Skeleton3D skeleton, string part)
	{
		int exact = skeleton.FindBone(part);
		if (exact >= 0)
		{
			return exact;
		}

		for (int b = 0; b < skeleton.GetBoneCount(); b++)
		{
			if (string.Equals(skeleton.GetBoneName(b), part, StringComparison.OrdinalIgnoreCase))
			{
				return b;
			}
		}

		return -1;
	}
}
