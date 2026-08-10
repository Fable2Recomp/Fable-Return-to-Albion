using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Models;

namespace Refii.Entities;

/// <summary>
/// Builds a Godot <see cref="Skeleton3D"/> for a live character and drives it from the guest.
///
/// THE SKELETON IS THE MODEL'S, AND THE POSE IS MAPPED ONTO IT BY NAME.
///
/// That sentence is the whole class, and the earlier version got it backwards. It built the
/// skeleton from the GUEST's rest pose, which is the natural thing to do — the guest is the
/// simulation and states what exists — but a skinned mesh's BLENDINDICES do not address the
/// guest's rig. They address the bone table shipped inside the `.mdl`, and the two do not
/// agree: the child hero's model declares 143 bones while the guest's runtime rig has 161,
/// and a chicken declares 39 against the guest's 39 with only 11 of the parent links in
/// common. Same rig, different numbering. Binding by index attaches every vertex to the
/// wrong bone, and that does not present as an index problem — it presents as a character
/// exploding into a fan of triangles.
///
/// The only key the two sides share is the bone NAME, so the map is built from names and the
/// skeleton is built in the model's numbering. The mesh then binds correctly by construction,
/// and no cached, shared mesh resource has to be rewritten per entity.
///
/// TWO POSES, IN DIFFERENT SPACES. The model's rest is LOCAL: it is fed in as-is and Godot
/// composes it, which is also what Godot inverts to derive the skin's bind matrices. The
/// guest's live pose is usually already MODEL space, so it goes straight in with no parent
/// walk — except when it is not. The guest writes the array local and converts it in place,
/// and the component STATES which one you are holding. Measured in BWSSlums, only 12 of 138
/// characters read as model space at any instant, so the local branch is the common path and
/// not an edge case.
/// </summary>
public sealed class Engine_EntitySkeleton
{
	private readonly Skeleton3D _skeleton;

	/// <summary>The model's bone table — the numbering the skeleton and the mesh both use.</summary>
	private readonly Engine_MdlRig _rig;

	/// <summary>
	/// For each MODEL bone, the guest bone that drives it, or -1 for one the guest does not
	/// have.
	///
	/// This direction and not its inverse, because the pose is applied by walking the model's
	/// bones IN ORDER. <see cref="Skeleton3D.SetBoneGlobalPose"/> derives a bone's local pose
	/// from its parent's current one, so a child written before its parent is computed against
	/// a stale parent and drifts when the parent later moves. The model's table is ordered
	/// parent-before-child; the guest's is too, but the map between them is a permutation, so
	/// walking the guest's order would write the model's bones out of order.
	/// </summary>
	private readonly int[] _modelToGuest;

	/// <summary>How many of the guest's bones found a bone of the same name in the model.</summary>
	public int Mapped { get; }

	/// <summary>Bones the guest has that the model does not. Expected to be non-zero.</summary>
	public int GuestOnly { get; }

	/// <summary>Rigs left in bind this frame because the guest offered no pose at all.</summary>
	public static int NoPose;

	/// <summary>
	/// Rigs left in bind because the live pose's bone count differs from the guest skeleton's.
	///
	/// If it is non-zero the rig is fine and the FEED is being refused, which is what a T-pose
	/// looks like from the inside.
	/// </summary>
	public static int CountMismatch;

	/// <summary>The last (live, expected) pair that disagreed, so the shape of the gap shows.</summary>
	public static (int Live, int Expected) LastMismatch;

	/// <summary>Rigs whose pose vector the animation system has not written yet.</summary>
	public static int NeverPosed;

	/// <summary>
	/// Zeroes the per-frame refusal counters. Called once per tick by whoever drives the rigs.
	///
	/// Without this they are run totals read as frame counts: after five thousand frames
	/// "countMismatch=545000" means a hundred and nine rigs were refused, not half a million.
	/// A counter with no reset answers a different question than the one its name asks.
	/// </summary>
	public static void BeginFrame()
	{
		NoPose = 0;
		CountMismatch = 0;
		NeverPosed = 0;
	}

	/// <summary>Scratch for composing a local pose, allocated once.</summary>
	private Transform3D[]? _composed;

	/// <summary>How many bones the GUEST's rig has, which is not the skeleton's count.</summary>
	private readonly int _guestBones;

	/// <summary>Read once — the environment does not change mid-run.</summary>
	private static bool? _bindOnly;

	/// <summary>Read once — see REFII_RIG_ASSUME_MODEL in <see cref="Follow"/>.</summary>
	private static bool? _assumeModel;

	private Engine_EntitySkeleton(
		Skeleton3D skeleton, Engine_MdlRig rig, int[] modelToGuest, int guestBones, int mapped)
	{
		_skeleton = skeleton;
		_rig = rig;
		_modelToGuest = modelToGuest;
		_guestBones = guestBones;
		Mapped = mapped;
		GuestOnly = guestBones - mapped;
	}

	/// <summary>The node to parent a skinned mesh under.</summary>
	public Skeleton3D Node => _skeleton;

	/// <summary>How many bones the rig has, in the model's numbering.</summary>
	public int BoneCount => _rig.BoneCount;

	/// <summary>How many bones the guest's own rig has.</summary>
	public int GuestBoneCount => _guestBones;

	/// <summary>
	/// The axis-aligned bounds of the bones, in the space the mesh is drawn in.
	///
	/// This is the deterministic version of "does the rig look right", and it exists because
	/// the screenshot version was not usable: whether a character is in frame depends on where
	/// the guest happens to have pointed its camera, and four consecutive runs produced sky,
	/// cutscene and rooftops. Bounds need no camera.
	///
	/// Compared against the mesh's own AABB it answers the question directly. A rig that
	/// roughly fills the mesh is bound correctly whatever it looks like on screen; one whose
	/// bones collapse into a point, or sprawl orders of magnitude wider than the mesh, is not
	/// — and those two failures need opposite fixes.
	/// </summary>
	/// <param name="rest">
	/// True for the bind pose Godot composed from what we set, false for the live pose.
	/// </param>
	public Aabb Bounds(bool rest)
	{
		var bounds = new Aabb();
		for (int i = 0; i < _rig.BoneCount; ++i)
		{
			Vector3 origin = rest
				? _skeleton.GetBoneGlobalRest(i).Origin
				: _skeleton.GetBoneGlobalPose(i).Origin;

			bounds = i == 0 ? new Aabb(origin, Vector3.Zero) : bounds.Expand(origin);
		}

		return bounds;
	}

	/// <summary>
	/// Names the bone furthest from the mesh, and the chain that put it there.
	///
	/// "live bones size=(0.77, 1.75, 2.12)" against a mesh 0.43 across says a rig is exploding
	/// and says nothing about WHY, and the two candidate causes need opposite fixes. If the
	/// outlier's PARENT is also far out, the fault is upstream and the chain is being composed
	/// wrongly — a bad parent index, or a local pose composed as if it were model space. If the
	/// parent is where it should be and only the child has flown, the fault is that one bone's
	/// own transform, which is a read problem rather than a composition one.
	///
	/// Reported per rig rather than aggregated: one chicken of three explodes, deterministically
	/// and with the same numbers every run, so the interesting object is a specific one.
	/// </summary>
	public string DescribeOutlier()
	{
		if (_rig.BoneCount == 0)
		{
			return string.Empty;
		}

		Vector3 root = _skeleton.GetBoneGlobalPose(0).Origin;
		int worst = -1;
		float worstDistance = 0f;

		for (int i = 0; i < _rig.BoneCount; ++i)
		{
			float distance = _skeleton.GetBoneGlobalPose(i).Origin.DistanceTo(root);
			if (distance > worstDistance)
			{
				worstDistance = distance;
				worst = i;
			}
		}

		if (worst < 0)
		{
			return " — every bone sits on the root";
		}

		int parent = _rig.Parents[worst];
		string parentText = parent >= 0 && parent < _rig.BoneCount
			? $"'{_rig.Names[parent]}'[{parent}] at " +
			  $"{_skeleton.GetBoneGlobalPose(parent).Origin} " +
			  $"({_skeleton.GetBoneGlobalPose(parent).Origin.DistanceTo(root):0.###} out)"
			: "<root>";

		int guest = _modelToGuest[worst];
		return $"\n    furthest bone '{_rig.Names[worst]}'[{worst}] is {worstDistance:0.###} " +
			$"from the root at {_skeleton.GetBoneGlobalPose(worst).Origin}, " +
			$"rest {_skeleton.GetBoneGlobalRest(worst).Origin}; " +
			$"parent {parentText}; " +
			$"guest bone {(guest < 0 ? "NONE — holding rest" : guest.ToString())}";
	}

	/// <summary>
	/// Builds a rig for one entity, or null when it carries no animated mesh or the guest has
	/// not named its bones yet.
	///
	/// The MODEL supplies the skeleton; the guest supplies only the names, and only so the two
	/// can be related. That ordering is deliberate — it means the skeleton cannot disagree with
	/// the bind matrices Godot derives from it, whatever the guest is doing.
	/// </summary>
	public static Engine_EntitySkeleton? TryBuild(uint entity, Engine_MdlRig? rig) =>
		rig is { } model && HostBridge.GetEntityBoneNames(entity) is { Length: > 0 } guestNames
			? Build(model, guestNames)
			: null;

	/// <summary>
	/// Builds a rig from a model and a guest bone-name list, WHEREVER THEY CAME FROM.
	///
	/// Split from <see cref="TryBuild"/> so the editor can stand a character up without a
	/// running guest — pass the model's own names as the guest's and the map is the identity.
	/// The engine is shared between the editor and the game by rule, and a rig that only
	/// existed when the guest was live would be a second implementation waiting to happen.
	/// </summary>
	public static Engine_EntitySkeleton? Build(Engine_MdlRig rig, string[] guestNames)
	{
		var skeleton = new Skeleton3D { Name = "Skeleton" };

		// SYNTHETIC NAMES, NOT THE MODEL'S. Godot puts rules on a bone name that a Fable rig
		// has never heard of — it must be non-empty, must not contain ':' or '/', and since
		// 4.3 must be UNIQUE within the skeleton — and `AddBone` answers a name it dislikes by
		// returning -1 and adding nothing.
		//
		// That is silent, and it is not survivable. The skeleton ends up SHORTER than the
		// model's table while the mesh's BLENDINDICES still run to the model's highest bone, so
		// the renderer indexes a skin past its end. Naming them by index instead cannot fail,
		// and Godot never needs the real names anyway: the name is only the key that matches
		// the guest's rig to this one, and that map is built below, in our own dictionary.
		for (int i = 0; i < rig.BoneCount; ++i)
		{
			skeleton.AddBone($"bone_{i}");
		}

		// Believe nothing — the count is the whole safety property. If it ever disagrees the
		// right answer is no rig at all: an unskinned character draws in bind and looks wrong,
		// where a short skeleton reads out of bounds in the renderer.
		if (skeleton.GetBoneCount() != rig.BoneCount)
		{
			GD.PushError($"[rig] skeleton took {skeleton.GetBoneCount()} of the model's " +
				$"{rig.BoneCount} bones; refusing to bind a skin to it");
			skeleton.QueueFree();
			return null;
		}

		for (int i = 0; i < rig.BoneCount; ++i)
		{
			int parent = rig.Parents[i];
			if (parent >= 0 && parent < rig.BoneCount && parent != i)
			{
				skeleton.SetBoneParent(i, parent);
			}
		}

		// The rest is LOCAL, which is exactly what SetBoneRest wants — Godot composes it
		// itself. Composing it here first and handing over a global transform would apply the
		// parent chain twice.
		for (int i = 0; i < rig.BoneCount; ++i)
		{
			Transform3D local = ToGodot(rig.Rotation(i), rig.Translation(i), rig.Scale(i));
			skeleton.SetBoneRest(i, local);

			// Seed the pose from the rest too, so a character drawn before its first live pose
			// arrives stands in bind rather than collapsing to the origin.
			skeleton.SetBonePosePosition(i, local.Origin);
			skeleton.SetBonePoseRotation(i, local.Basis.GetRotationQuaternion());
			skeleton.SetBonePoseScale(i, local.Basis.Scale);
		}

		// THE MAP, BY NAME, because the name is the only key the two rigs share.
		//
		// First match wins. Names are interned in the guest and a rig may legitimately repeat
		// one, so "the guest bone called X" is not guaranteed unique — taking the first keeps
		// the map a function and keeps it stable between frames, which matters more here than
		// resolving a duplicate cleverly.
		var byName = new Dictionary<string, int>(guestNames.Length);
		for (int g = 0; g < guestNames.Length; ++g)
		{
			if (guestNames[g].Length != 0)
			{
				byName.TryAdd(guestNames[g], g);
			}
		}

		var modelToGuest = new int[rig.BoneCount];
		var unmapped = new List<string>();
		int mapped = 0;
		for (int m = 0; m < rig.BoneCount; ++m)
		{
			modelToGuest[m] = byName.TryGetValue(rig.Names[m], out int g) ? g : -1;
			if (modelToGuest[m] >= 0)
			{
				++mapped;
			}
			else
			{
				// NAME the miss, and say where it sits in the hierarchy.
				//
				// "142 of 143 mapped" is a number that cannot be acted on. WHICH bone decides
				// everything: an unmapped fingertip is cosmetic, an unmapped spine holds every
				// descendant at its rest while its parent moves — and a bone frozen mid-chain
				// does not look like a missing bone, it looks like the mesh tearing, which is
				// what sent this investigation at scale and at the composition maths first.
				//
				// The child count is the tell. A leaf costs one bone; a bone with children
				// costs its whole subtree.
				int children = 0;
				for (int c = 0; c < rig.BoneCount; ++c)
				{
					if (rig.Parents[c] == m)
					{
						++children;
					}
				}

				unmapped.Add($"'{rig.Names[m]}'[{m}] parent={rig.Parents[m]} children={children}");
			}
		}

		if (unmapped.Count != 0)
		{
			GD.Print($"[rig] {rig.BoneCount - mapped} model bone(s) the guest never names, so " +
				$"they hold their REST while their parents move: {string.Join(", ", unmapped)}");
		}

		return new Engine_EntitySkeleton(
			skeleton, rig, modelToGuest, guestNames.Length, mapped);
	}

	/// <summary>
	/// Pushes the guest's current pose into the skeleton. Cheap enough to call every frame —
	/// it is one memory read and one transform per bone.
	/// </summary>
	public void Follow(uint entity)
	{
		// REFII_RIG_BIND_ONLY=1 freezes every rig in its bind pose.
		//
		// A bisect, not a feature. When a character renders wrongly there are two halves that
		// could be at fault — the skeleton/skin/mesh binding, or the live pose being fed into
		// it — and the counts cannot tell them apart. With the live pose skipped, a character
		// that stands up correctly puts the fault entirely in the feed, and one that is still
		// wrong puts it entirely in the bind.
		if (_bindOnly ??= OS.GetEnvironment("REFII_RIG_BIND_ONLY") == "1")
		{
			return;
		}

		if (HostBridge.GetEntityPose(entity) is not { } pose)
		{
			++NoPose;
			return;
		}

		if (pose.BoneCount != _guestBones)
		{
			// A bone count that changed means the entity swapped rigs — a different skeleton,
			// not a pose for this one. Counted, because this is what a T-pose looks like from
			// the inside and silently returning made it indistinguishable from an animation
			// that had not started.
			++CountMismatch;
			LastMismatch = (pose.BoneCount, _guestBones);
			return;
		}

		// THE SPACE FLAG HAS THREE VALUES, NOT TWO. 1 is local, 2 is model, and 0 is neither —
		// the component allocated its pose vector but the animation system has not written it
		// yet. Treating "not 2" as "therefore local" fed an uninitialised array through the
		// parent-composition walk, and composing garbage does not look like garbage: it looks
		// like a character whose head and feet are right and whose middle explodes into spikes.
		if (pose.Space != 1 && pose.Space != 2)
		{
			++NeverPosed;
			return;  // never posed — leave it in bind rather than invent one
		}

		// REFII_RIG_ASSUME_MODEL=1 treats EVERY pose as already model space.
		//
		// The other half of the bisect that REFII_RIG_BIND_ONLY started, and it exists because
		// every exploding rig explodes the same way: the error compounds down the chain and each
		// link adds roughly the bone's REST offset rather than a bone-length delta, which is
		// what composing already-absolute transforms looks like. 126 of 138 characters report
		// space=1 and explode; the 12 that report space=2 skip the composition and do not.
		//
		// The flag itself is not in doubt — `sub_821F2C78` stamps comp+0x14 = 1 after copying
		// the evaluated pose and `sub_8257D8B8` stamps 2 after converting, so 1 IS local — but
		// what the guest calls local may not be parent-relative in the sense this composition
		// assumes. With this set, a street that stands up correctly puts the fault entirely in
		// the composition; one that is still wrong puts it somewhere else, and that is a
		// distinction no amount of staring at bounds can make.
		_assumeModel ??= OS.GetEnvironment("REFII_RIG_ASSUME_MODEL") == "1";

		if (!pose.IsModelSpace && _assumeModel != true)
		{
			_composed ??= new Transform3D[HostBridge.MaxBones];
			_rotation ??= new Quaternion[HostBridge.MaxBones];
			_translation ??= new Vector3[HostBridge.MaxBones];
			_scale ??= new Vector3[HostBridge.MaxBones];

			for (int g = 0; g < _guestBones; ++g)
			{
				Quaternion r = GuestRotation(pose.Rotation(g));
				Vector3 t = Swizzle(pose.Translation(g));

				// PERMUTED, NOT SWIZZLED — the same distinction ToGodot documents. A scale is
				// not a position: conjugating diag(s) through M permutes the axes and negates
				// nothing, and swizzling it would put a mirror on every bone.
				Vector3 guestScale = pose.Scale(g);
				var s = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);

				if (!r.IsFinite() || !t.IsFinite() || !s.IsFinite())
				{
					r = Quaternion.Identity;
					t = Vector3.Zero;
					s = Vector3.One;
				}

				// A bone whose parent comes later is a torn read, not a rig to fix up: the
				// guest's own conversion relies on parent-before-child ordering too.
				int parent = pose.Parents[g];
				if (parent >= 0 && parent < g)
				{
					// COMPOSE TRS, NOT MATRICES.
					//
					// This was `_composed[parent] * local`, a full affine multiply, and that is
					// not what the guest does. `sub_821F58D8` takes a quaternion PRODUCT for
					// rotation and `parent.translation + rotate(parent.rotation, child.translation)`
					// for position — scale never enters it; a separate pass (`sub_821E5AC0`)
					// applies parent scale to the child's translation alone.
					//
					// A Transform3D multiply folds scale into the BASIS, so a parent with any
					// non-unit scale shears its child's rotation, and the error compounds down
					// the chain. That is why the hero came out roughly right in the torso with
					// shards spiking off the hands and weapon attachments — the ends of the
					// longest chains, where the most parents had multiplied in.
					_translation[g] = _translation[parent] +
						(_rotation[parent] * (t * _scale[parent]));
					_rotation[g] = _rotation[parent] * r;
					_scale[g] = _scale[parent] * s;
				}
				else
				{
					_translation[g] = t;
					_rotation[g] = r;
					_scale[g] = s;
				}

				// ScaledLocal for the same reason ToGodot uses it: the element is quaternion
				// THEN scale, so the scale belongs on the bone's own axes.
				_composed[g] = new Transform3D(
					new Basis(_rotation[g]).ScaledLocal(_scale[g]), _translation[g]);
			}
		}

		// IN THE MODEL'S ORDER, parents before children — see _modelToGuest. Bones the guest
		// does not have keep their rest, which is right for the wrapper and the attachment
		// dummies: they carry no skinning weight and nothing animates them.
		for (int m = 0; m < _modelToGuest.Length; ++m)
		{
			int g = _modelToGuest[m];
			if (g < 0)
			{
				continue;
			}

			_skeleton.SetBoneGlobalPose(m, pose.IsModelSpace || _assumeModel == true
				? ToGodot(pose.Rotation(g), pose.Translation(g), pose.Scale(g))
				: _composed![g]);
		}
	}

	/// <summary>
	/// One bone's transform, in THE MESH'S space.
	///
	/// The map here is <c>M: (x, y, z) -&gt; (x, z, -y)</c>, which is
	/// <see cref="Refii.Models.Engine_MdlGeometry"/>'s <c>ConvertZUpToYUp</c> — NOT the
	/// <c>(x, y, z) -&gt; (-x, z, y)</c> that <see cref="Refii.Game.GameEntities"/> and the
	/// camera use.
	///
	/// That distinction is the whole correctness of this class, and getting it wrong does not
	/// look like an axis error. Skinning multiplies each vertex by its bones' matrices, so
	/// vertices and bones have to be in ONE space; the vertices were already baked into M by
	/// the mesh reader. Posing the bones in the node's map instead left the two disagreeing by
	/// a rotation, and the mesh came out crumpled — which reads as a bad rig or bad weights
	/// rather than as a mismatched basis.
	///
	/// The node's map still applies, but one level up: <see cref="Refii.Game.GameEntities"/>
	/// puts it on the entity's own transform, and the skeleton rides that. Applying it here as
	/// well would be applying it twice.
	///
	/// Conjugation rather than component-swizzling, because a quaternion under a change of
	/// basis is <c>M R M^-1</c> — negating components happens to work only for maps that are
	/// their own inverse, and M is not one (applying it twice gives <c>(x, -y, -z)</c>).
	/// </summary>
	private static Transform3D ToGodot(Quaternion rotation, Vector3 guestTranslation,
		Vector3 guestScale)
	{
		float lengthSquared = rotation.LengthSquared();
		Basis basis = float.IsFinite(lengthSquared) && lengthSquared > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		basis = MeshMap * basis * MeshMapInverse;

		// THE SCALE IS THE ONE PART THAT MUST NOT PICK UP THE MAP'S SIGN, and it did.
		//
		// M sends y to -z, so a POSITION's third component flips — that is what Swizzle is
		// for and it is right for translations. A scale is not a position: conjugating it
		// gives `M diag(s) M^-1 = diag(sx, sz, sy)`, the axes permuted and nothing negated.
		// Swizzling it instead turned a unit scale into (1, 1, -1), which is a mirror, and
		// left EVERY bone with a determinant of -1. Measured after the fix: every one of the
		// 5,619 bones in a region reports a scale of 1, so the swizzle was doing nothing but
		// the damage.
		var scale = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);

		// Finite or nothing. A single NaN or infinity in one bone propagates through the
		// parent chain and takes every descendant with it, which is indistinguishable from a
		// bad offset by eye and trivially distinguishable here.
		Vector3 translation = Swizzle(guestTranslation);
		if (!scale.IsFinite() || !translation.IsFinite())
		{
			return Transform3D.Identity;
		}

		// ScaledLocal, not Scaled. The element is quaternion THEN scale, so the scale belongs
		// inside the rotation — `R * diag(s)`, the bone's own axes. Godot's `Scaled`
		// premultiplies (`diag(s) * R`) and scales along the PARENT's axes instead, which is
		// silently identical for the uniform scales these rigs carry and wrong for the rest.
		basis = basis.ScaledLocal(scale);
		return new Transform3D(basis, translation);
	}

	/// <summary>The mesh's own map applied to a vector: <c>(x, y, z) -&gt; (x, z, -y)</c>.</summary>
	private static Vector3 Swizzle(Vector3 guest) => new(guest.X, guest.Z, -guest.Y);

	/// <summary>
	/// One bone's rotation, mapped into the mesh's space.
	///
	/// Same conjugation <see cref="ToGodot"/> performs — a quaternion under a change of basis
	/// is <c>M R M^-1</c>, not a component swizzle — but returned as a quaternion so the
	/// parent walk can compose rotations the way the guest does, with a quaternion PRODUCT,
	/// rather than by multiplying bases that carry scale.
	/// </summary>
	private static Quaternion GuestRotation(Quaternion rotation)
	{
		float lengthSquared = rotation.LengthSquared();
		Basis basis = float.IsFinite(lengthSquared) && lengthSquared > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		return (MeshMap * basis * MeshMapInverse).GetRotationQuaternion();
	}

	private Quaternion[]? _rotation;

	private Vector3[]? _translation;

	private Vector3[]? _scale;

	/// <summary>
	/// <c>ConvertZUpToYUp</c> as a basis — columns are the images of the guest's axes.
	///
	/// Determinant +1, so it is a rotation and does not flip the winding of a skinned mesh.
	/// </summary>
	private static readonly Basis MeshMap = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, -1f),
		new Vector3(0f, 1f, 0f));

	/// <summary>
	/// The inverse of <see cref="MeshMap"/>: <c>(x, y, z) -&gt; (x, -z, y)</c>.
	///
	/// Spelled out rather than computed, and spelled out rather than reusing MeshMap — an
	/// earlier version assumed the map was its own inverse, which the world's map is and this
	/// one is not.
	/// </summary>
	private static readonly Basis MeshMapInverse = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, 1f),
		new Vector3(0f, -1f, 0f));
}
