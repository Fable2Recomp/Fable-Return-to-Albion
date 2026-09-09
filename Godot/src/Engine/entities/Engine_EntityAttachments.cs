using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// The entities that hang off another entity's bone — weapons, carried props, a sheathed
/// blade — placed from the HOST's pose.
///
/// THE GUEST STATES THE ATTACHMENT, NOT THE PLACEMENT. `CECObjectAttachment` recomputes each
/// attached child's world transform every time the parent moves and writes it into the child's
/// own placement component, composing the parent's dummy with the GUEST's bone pose. Godot now
/// plays the animation itself, so that placement describes a body nobody is drawing: the sword
/// swims a frame behind the back it is strapped to and bounces whenever the two poses disagree.
/// Following the statement instead — this child, that parent, that dummy — puts the child on
/// the bone the host is actually drawing.
///
/// THE OFFSET IS RECOVERED, NOT RE-DERIVED. The guest composes three things: the dummy's local
/// transform out of the parent's `.mdl.gmd`, the live bone pose, and the child's own offset
/// matrix (`record+80`, the rigid inverse of the dummy the child presents). Two of those are
/// constants of the attachment, so their product is one constant transform from the bone to the
/// child — and the guest states that product every frame in the child's placement. Solving for
/// it once, from the guest's own pose and its own stated result, needs no guess about matrix
/// order, quaternion sign or handedness: it is whatever makes the guest's numbers agree.
///
/// The `.gmd` is still read, for one thing only: WHICH BONE. A dummy is a record in the parent
/// model's attachment table naming the node it hangs from, and that node is either a bone or a
/// dummy whose parent chain reaches one — the same walk `sub_8223D160` does when the gmd bone
/// has no live counterpart.
/// </summary>
public sealed class Engine_EntityAttachments
{
	/// <summary>One live attachment, and what the host needs to place it.</summary>
	public sealed class Record
	{
		public uint Child;
		public uint Parent;
		public uint Ticket;
		public string Dummy = string.Empty;
		public int Index;

		/// <summary>The parent's model bone the dummy hangs from, or -1 while unresolved.</summary>
		public int Bone = -1;

		/// <summary>The child's transform relative to that bone. Constant once solved.</summary>
		public Transform3D Offset = Transform3D.Identity;

		public bool Solved;

		/// <summary>Attempts to solve, so a hopeless one stops costing a pose read a frame.</summary>
		public int Attempts;

		/// <summary>Solves taken so far; the offset keeps being re-solved while young.</summary>
		public int Solves;

		/// <summary>How far the offset put the child from the guest's statement, last frame.</summary>
		public float LastResidual = float.NaN;

		/// <summary>Consecutive frames a fresh solve landed far from the held offset.</summary>
		public int Disagreements;

		/// <summary>
		/// The mount bone as the guest last stated it, and whether we have one — the re-solve
		/// gate. See the re-solve in <c>Place</c>.
		/// </summary>
		public Transform3D LastGuestBone = Transform3D.Identity;

		public bool HaveLastGuestBone;

	}


	/// <summary>How many solve attempts a record gets before it is left on its stated placement.</summary>
	private const int MaxAttempts = 240;

	/// <summary>Attachments placed from the host's bones this frame.</summary>
	public static int Placed;

	/// <summary>Attachments the host could not resolve to a bone.</summary>
	public static int Unresolved;

	private readonly Dictionary<uint, Record> _byChild = new();

	private readonly List<HostBridge.GuestAttachment> _stated = new();

	private static readonly Dictionary<string, Refii.Models.Engine_GmdSkeleton?> GmdCache = new();

	/// <summary>Drops the dummy-name skeleton lookups. Keyed by model path, so region-scoped.</summary>
	public static void ClearCaches()
	{
		lock (GmdCache)
		{
			GmdCache.Clear();
		}
	}

	/// <summary>The guest's current statement, folded into the table. Cheap; call on a cadence.</summary>
	public void Refresh()
	{
		if (HostBridge.GetAttachments(_stated) == 0 && _byChild.Count == 0)
		{
			return;
		}

		foreach (HostBridge.GuestAttachment stated in _stated)
		{
			if (_byChild.TryGetValue(stated.Child, out Record? record))
			{
				// A RE-ATTACH IS A NEW TICKET. Same child, same parent, different dummy — a
				// weapon drawn from the back into the hand — and the solved offset from the
				// old dummy would hold the sword out sideways for the rest of the run.
				if (record.Ticket == stated.Ticket && record.Parent == stated.Parent)
				{
					continue;
				}

				_byChild.Remove(stated.Child);
			}

			_byChild[stated.Child] = new Record
			{
				Child = stated.Child,
				Parent = stated.Parent,
				Ticket = stated.Ticket,
				Dummy = stated.Dummy,
				Index = stated.Index,
			};
		}

		if (_byChild.Count == _stated.Count)
		{
			return;
		}

		// Detached children go back to their own placement: the guest stops writing them from
		// the parent and starts moving them itself (the dropped weapon falls).
		var live = new HashSet<uint>();
		foreach (HostBridge.GuestAttachment stated in _stated)
		{
			live.Add(stated.Child);
		}

		var gone = new List<uint>();
		foreach (uint child in _byChild.Keys)
		{
			if (!live.Contains(child))
			{
				gone.Add(child);
			}
		}

		foreach (uint child in gone)
		{
			_byChild.Remove(child);
		}
	}

	/// <summary>Drops every record naming a retired entity, as child or as parent.</summary>
	public void Forget(uint entity)
	{
		_byChild.Remove(entity);
		var orphans = new List<uint>();
		foreach ((uint child, Record record) in _byChild)
		{
			if (record.Parent == entity)
			{
				orphans.Add(child);
			}
		}

		foreach (uint child in orphans)
		{
			_byChild.Remove(child);
		}
	}

	/// <summary>Re-solves taken because an offset missed the guest's statement.</summary>
	public static int Resolves;

	/// <summary>Every record hanging on one parent, for the debug channel.</summary>
	public List<Dictionary<string, object?>> Describe(uint parent, Engine_EntitySkeleton? rig)
	{
		var rows = new List<Dictionary<string, object?>>();
		foreach (Record record in _byChild.Values)
		{
			if (record.Parent != parent)
			{
				continue;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["child"] = $"{record.Child:X8}",
				["dummy"] = record.Dummy,
				["ticket"] = record.Ticket,
				["bone"] = record.Bone,
				["boneName"] = rig is not null && record.Bone >= 0 ? rig.BoneName(record.Bone) : null,
				["solved"] = record.Solved,
				["offset"] = new[] { record.Offset.Origin.X, record.Offset.Origin.Y, record.Offset.Origin.Z },
				["offsetMetres"] = record.Offset.Origin.Length(),
				["residual"] = record.LastResidual,
				["scored"] = record.Solves,
			});
		}

		return rows;
	}

	/// <summary>The parent this child hangs on, or 0.</summary>
	public uint ParentOf(uint child) =>
		_byChild.TryGetValue(child, out Record? record) ? record.Parent : 0u;

	public int Count => _byChild.Count;

	/// <summary>
	/// Where the child belongs given the parent as the host is drawing it. False leaves the
	/// caller on the guest's stated placement — the honest fallback while a record is
	/// unsolved, and the permanent answer for a dummy no `.gmd` names.
	/// </summary>
	/// <param name="childStated">The guest's own placement for the child, for the solve.</param>
	/// <param name="parentStated">
	/// The parent's own stated placement — NOT the blended one it is drawn at. The guest
	/// composed the child's placement against this transform, and solving against a parent that
	/// is a fraction of a tick behind bakes that fraction into the offset for good.
	/// </param>
	public bool TryPlace(uint child, Transform3D childStated, Transform3D parentStated,
		Transform3D parentPlacement, Engine_EntitySkeleton parentRig, string[] parentParts,
		out Transform3D placed)
	{
		placed = childStated;
		if (!_byChild.TryGetValue(child, out Record? record))
		{
			return false;
		}

		if (!record.Solved)
		{
			if (record.Attempts >= MaxAttempts)
			{
				return false;
			}

			++record.Attempts;
			if (!Solve(record, childStated, parentStated, parentRig, parentParts))
			{
				if (record.Attempts == MaxAttempts)
				{
					++Unresolved;
					GD.Print($"[attach] '{record.Dummy}'[{record.Index}] on " +
						$"{record.Parent:X8} named no bone this host has — child " +
						$"{record.Child:X8} stays on its stated placement");
				}

				return false;
			}
		}
		else if (record.Bone >= 0 &&
			parentRig.TryGuestBoneGlobal(record.Parent, record.Bone, out Transform3D guestBone))
		{
			// RE-SOLVE ON A CANDIDATE, NOT ON A RESIDUAL. The child's placement is written on the
			// guest's tick and the pose is read now, so while the body moves the two disagree by
			// a tick's travel — a steady 3 cm on a walking hero, which a residual threshold
			// re-solved every frame (630 re-solves in seconds, the sword flipping between two
			// offsets). A torn read, the thing this guards against, gives a candidate offset
			// metres away; phase gives centimetres. So the held offset is replaced only when a
			// fresh solve lands more than ten centimetres from it three frames running.
			Transform3D from = parentStated * guestBone;
			record.LastResidual = (from * record.Offset).Origin.DistanceTo(childStated.Origin);
			++record.Solves;

			// RE-SOLVE ONLY WHILE THE MOUNT IS NEARLY STILL. The two halves of a solve sit on
			// different clocks — the child's placement is stated on the guest's tick, the bone
			// is read from the 30 Hz pose array now — so the error between them is proportional
			// to how fast that bone is TRAVELLING. Walking gives the few centimetres the ten
			// centimetre threshold below was chosen for. Swinging a blacksmith's hammer gives
			// far more, and three frames of that in a row is all it takes to bake the phase
			// error in as the mount itself. Measured after one job: sheathed weapons solved to
			// 0.31 m and 0.41 m from their own dummy bones, against 0.13 m to 0.17 m for the
			// ones solved at rest — a sword hanging a third of a metre off the body.
			//
			// A mount is rigid, so a sample taken while the bone is still is the honest one and
			// costs nothing to wait for. The FIRST solve is left alone: this only stops a good
			// offset being corrupted, which is the failure — the weapons are right until a job
			// is played.
			float travelled = record.HaveLastGuestBone
				? guestBone.Origin.DistanceTo(record.LastGuestBone.Origin)
				: 0f;
            bool settled = record.HaveLastGuestBone && travelled < 0.02f;
			record.LastGuestBone = guestBone;
			record.HaveLastGuestBone = true;

			if (settled && Mathf.Abs(from.Basis.Determinant()) > 1e-6f)
			{
				Transform3D candidate = from.AffineInverse() * childStated;
				if (candidate.Origin.IsFinite() &&
					candidate.Origin.DistanceTo(record.Offset.Origin) > 0.10f)
				{
					if (++record.Disagreements >= 3)
					{
						record.Offset = candidate;
						record.Disagreements = 0;
						++Resolves;
					}
				}
				else
				{
					record.Disagreements = 0;
				}
			}
		}

		if (record.Bone < 0 || record.Bone >= parentRig.BoneCount)
		{
			return false;
		}

		placed = parentPlacement * parentRig.Node.GetBoneGlobalPose(record.Bone) * record.Offset;
		++Placed;
		return true;
	}

	private static bool Solve(Record record, Transform3D childStated,
		Transform3D parentStated, Engine_EntitySkeleton parentRig, string[] parentParts)
	{
		if (record.Bone < 0 && !TryBone(record, parentRig, parentParts))
		{
			return false;
		}

		// THE GUEST'S OWN POSE, not the one the host is playing. The stated child placement was
		// composed against the guest's bones, so the offset only comes out constant when both
		// sides of the solve are the guest's.
		if (!parentRig.TryGuestBoneGlobal(record.Parent, record.Bone, out Transform3D bone))
		{
			return false;
		}

		Transform3D from = parentStated * bone;
		if (!from.Origin.IsFinite() || Mathf.Abs(from.Basis.Determinant()) < 1e-6f)
		{
			return false;
		}

		Transform3D offset = from.AffineInverse() * childStated;
		if (!offset.Origin.IsFinite() || offset.Origin.Length() > 8f)
		{
			// A metre or two is a sheath; eight is a torn read of one of the three transforms.
			return false;
		}

		record.Offset = offset;
		record.Solved = true;

		if (Solves < SolvesLogged)
		{
			++Solves;
			GD.Print($"[attach] {record.Child:X8} on {record.Parent:X8} " +
				$"'{record.Dummy}'[{record.Index}] -> bone {record.Bone} " +
				$"'{parentRig.BoneName(record.Bone)}', offset {offset.Origin} " +
				$"({offset.Origin.Length():0.###} m from the bone)");
		}

		return true;
	}

	/// <summary>Solves reported, so the first few can be read back and checked against the model.</summary>
	private static int Solves;

	private const int SolvesLogged = 8;

	/// <summary>
	/// The parent's model bone a stated dummy hangs from. The dummy is a record in one of the
	/// parent model's `.mdl.gmd` attachment tables — a morph character carries one per part and
	/// the guest takes the first that has the name — and the node it names is either a bone or a
	/// dummy node whose parent chain reaches one.
	/// </summary>
	private static bool TryBone(Record record, Engine_EntitySkeleton rig, string[] parentParts)
	{
		foreach (string part in parentParts)
		{
			Refii.Models.Engine_GmdSkeleton? gmd = Gmd(part);
			if (gmd is null || !gmd.TryResolveNode(record.Dummy, record.Index, out int node))
			{
				continue;
			}

			for (int guard = 0; node >= 0 && node < gmd.Names.Length && guard < 64; ++guard)
			{
				if (rig.TryBone(gmd.Names[node], out int bone))
				{
					record.Bone = bone;
					return true;
				}

				node = gmd.Parents[node];
			}
		}

		return false;
	}

	private static Refii.Models.Engine_GmdSkeleton? Gmd(string modelPath)
	{
		if (GmdCache.TryGetValue(modelPath, out Refii.Models.Engine_GmdSkeleton? cached))
		{
			return cached;
		}

		Refii.Models.Engine_GmdSkeleton? gmd = Refii.Models.Engine_GmdSkeleton.TryParse(
			Refii.Models.Engine_BnkMeshLoader.ReadBytes(modelPath + ".gmd"));
		GmdCache[modelPath] = gmd;
		return gmd;
	}
}
