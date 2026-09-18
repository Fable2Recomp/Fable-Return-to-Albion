using Godot;

namespace Refii.Models;

/// <summary>
/// A model's own skeleton, as the mesh carries it — names, parents and the local rest pose.
///
/// THIS IS THE INDEX SPACE A SKINNED MESH'S BONES ARRAY ADDRESSES, and that is the whole
/// reason it exists as a type rather than three loose metadata lookups. The guest's runtime
/// rig for the same character is numbered differently: 143 bones here against the guest's 161
/// for the child hero, and 39 against 39 with only 11 of the parent links agreeing for a
/// chicken. Building a Skeleton3D in the guest's numbering and binding this mesh to it
/// attaches every vertex to the wrong bone, which on screen is a fan of spikes rather than
/// anything that reads as a numbering problem.
///
/// So the skeleton is built from THIS, and the guest's pose is mapped onto it by name.
/// </summary>
public readonly record struct Engine_MdlRig(string[] Names, int[] Parents, float[] Rest)
{
	/// <summary>How many bones the model declares.</summary>
	public int BoneCount => Names.Length;

	/// <summary>Bone <paramref name="index"/>'s local rotation.</summary>
	public Quaternion Rotation(int index)
	{
		int at = index * Engine_MdlMesh.BoneRestStride;
		return new Quaternion(Rest[at], Rest[at + 1], Rest[at + 2], Rest[at + 3]);
	}

	/// <summary>Bone <paramref name="index"/>'s local translation.</summary>
	public Vector3 Translation(int index)
	{
		int at = index * Engine_MdlMesh.BoneRestStride;
		return new Vector3(Rest[at + 4], Rest[at + 5], Rest[at + 6]);
	}

	/// <summary>Bone <paramref name="index"/>'s local scale.</summary>
	public Vector3 Scale(int index)
	{
		int at = index * Engine_MdlMesh.BoneRestStride;
		return new Vector3(Rest[at + 7], Rest[at + 8], Rest[at + 9]);
	}

	/// <summary>A box, because a weak table cannot hold a null value and "no rig" is the answer worth keeping.</summary>
	private sealed class Answer
	{
		public Engine_MdlRig? Rig;
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Answer> Cache = new();

	/// <summary>
	/// The rig a mesh carries, or null when it has none or ships an incomplete one.
	///
	/// All three parts or nothing. A bone table with no rest transforms cannot stand a
	/// character up, and half a rig is worse than none: it builds a skeleton that looks
	/// plausible in every count we print and collapses every bone onto the origin.
	/// </summary>
	/// <remarks>
	/// Answered once per mesh and cached, including "no rig": callers retry unrigged drawables
	/// on a cadence, and re-reading the metadata arrays each time churns Godot wrappers.
	/// </remarks>
	public static Engine_MdlRig? TryFrom(Mesh? mesh)
	{
		if (mesh is null)
		{
			return null;
		}

		if (Cache.TryGetValue(mesh, out Answer? cached))
		{
			return cached.Rig;
		}

		var answer = new Answer { Rig = Build(mesh) };
		Cache.Add(mesh, answer);
		return answer.Rig;
	}

	private static Engine_MdlRig? Build(Mesh? mesh)
	{
		if (mesh is not ArrayMesh array ||
			!array.HasMeta(Engine_MdlMesh.BoneNamesMeta) ||
			!array.HasMeta(Engine_MdlMesh.BoneParentsMeta) ||
			!array.HasMeta(Engine_MdlMesh.BoneRestMeta))
		{
			return null;
		}

		string[] names = array.GetMeta(Engine_MdlMesh.BoneNamesMeta).AsStringArray();
		int[] parents = array.GetMeta(Engine_MdlMesh.BoneParentsMeta).AsInt32Array();
		float[] rest = array.GetMeta(Engine_MdlMesh.BoneRestMeta).AsFloat32Array();

		return names.Length != 0 &&
			parents.Length == names.Length &&
			rest.Length == names.Length * Engine_MdlMesh.BoneRestStride
				? new Engine_MdlRig(names, parents, rest)
				: null;
	}
}
