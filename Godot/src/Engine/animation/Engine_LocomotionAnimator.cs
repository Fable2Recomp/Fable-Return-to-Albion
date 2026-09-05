using Godot;
using Refii.Host;

namespace Refii.Animations;

/// <summary>Where the state being played comes from.</summary>
public enum LocomotionSource
{
	/// <summary>Play whatever the guest's own machine has selected. Cannot drift.</summary>
	FollowGuest,

	/// <summary>Run our translated machine and play its choice. Diverges if we got it wrong.</summary>
	HostMachine,
}

/// <summary>
/// Plays the locomotion clip for one live character.
///
/// THIS IS THE JOIN. Everything else in this area is either the state machine or the clip
/// library; this is the piece that turns "the guest is in HeroLocomotionStateSprint" into a
/// Godot AnimationPlayer running `Sprint` with the state's own blend time.
///
/// IT DOES NOT SHARE A SKELETON WITH THE POSE FEED. `Engine_EntitySkeleton.Follow` writes every
/// bone from the guest each frame, and an AnimationPlayer writing the same bones would simply
/// lose the argument — last writer per frame wins and the result would be neither one. So
/// enabling this DISABLES the pose feed for that character, which is the actual meaning of
/// "animation runs on the Godot side": the host stops copying the guest's bones and starts
/// playing the guest's CLIPS.
///
/// TWO SOURCES ON PURPOSE. <see cref="LocomotionSource.FollowGuest"/> reads the state the guest
/// itself picked, so the animation cannot disagree with the simulation. HostMachine runs our
/// translation of the same rules. Running one against the other is how the translation gets
/// checked — <see cref="Divergences"/> counts the frames where they disagree, which is a real
/// number about our correctness rather than an impression that something looks off.
/// </summary>
[Tool]
[GlobalClass]
public partial class Engine_LocomotionAnimator : Node3D
{
	public const string NodeName = "Locomotion";

	/// <summary>Meta the entity node carries its guest address in.</summary>
	public const string AddressMeta = "entity_address";

	/// <summary>The guest's own machine ticks at 15 Hz, so polling faster only costs reads.</summary>
	private const double PollSeconds = 1.0 / 15.0;

	private readonly Engine_LocomotionMachine _machine = new();

	private double _sincePoll = PollSeconds;

	private string _playing = string.Empty;

	private string _guestState = string.Empty;

	[Export]
	public LocomotionSource Source { get; set; } = LocomotionSource.FollowGuest;

	/// <summary>The guest entity this follows. Falls back to the parent's meta.</summary>
	[Export]
	public uint EntityAddress { get; set; }

	/// <summary>Frames on which our machine and the guest's disagreed. Only meaningful in HostMachine.</summary>
	public int Divergences { get; private set; }

	public int Samples { get; private set; }

	/// <summary>The state actually being played, whichever source chose it.</summary>
	public string Current => _playing;

	/// <summary>What the guest says, always read when available — the cross-check.</summary>
	public string GuestState => _guestState;

	/// <summary>
	/// The skeleton to drive, and the model hashes to resolve clips against.
	///
	/// Both are handed in rather than discovered, because a LIVE character's skeleton is the one
	/// <see cref="Refii.Entities.Engine_EntitySkeleton"/> already built and skinned the mesh to.
	/// Building a second one here would animate a rig nothing is bound to — the character would
	/// hold its bind pose while a perfectly correct animation played on an invisible skeleton.
	/// </summary>
	public void Drive(Skeleton3D skeleton, string skeletonName, uint[] modelHashes)
	{
		_skeleton = skeleton;
		_skeletonName = skeletonName;
		_modelHashes = modelHashes;
		_player = null;
		_playing = string.Empty;
	}

	private Skeleton3D? _skeleton;

	private string _skeletonName = string.Empty;

	private uint[] _modelHashes = System.Array.Empty<uint>();

	private AnimationPlayer? _player;

	private const string LibraryName = "locomotion";

	private AnimationPlayer? Player()
	{
		if (_player is { } existing)
		{
			return existing;
		}

		if (_skeleton is null)
		{
			return null;
		}

		_player = new AnimationPlayer { Name = "LocomotionPlayer" };
		AddChild(_player);
		_player.AddAnimationLibrary(LibraryName, new AnimationLibrary());

		// THE PLAYER'S ROOT MUST BE THE ENTITY, not this node. Track paths are built as
		// "<skeletonName>:<bone>" and an AnimationPlayer resolves them from its root, which
		// defaults to its own parent — leaving it there would look for the skeleton one level
		// too deep, resolve every track to nothing, and play a clip that moves no bones at all.
		if (GetParent() is { } entity)
		{
			_player.RootNode = _player.GetPathTo(entity);
		}

		return _player;
	}

	private uint Address
	{
		get
		{
			if (EntityAddress != 0)
			{
				return EntityAddress;
			}

			Node? owner = GetParent();
			return owner is not null && owner.HasMeta(AddressMeta)
				? (uint)owner.GetMeta(AddressMeta).AsInt64()
				: 0u;
		}
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("locomotionanimator");
		if (Engine.IsEditorHint())
		{
			return;
		}

		_sincePoll += delta;
		if (_sincePoll < PollSeconds)
		{
			return;
		}

		// Captured BEFORE the reset: this is the machine's tick length, and reading it after
		// zeroing would hand it a delta of zero — under which no dwell timer ever accumulates
		// and every state carrying a ValidTime becomes permanently unreachable.
		float step = (float)_sincePoll;
		_sincePoll = 0;

		uint address = Address;
		if (address == 0)
		{
			return;
		}

		HostBridge.GuestLocomotion live = HostBridge.GetEntityLocomotion(address);
		_guestState = live.State;

		LocomotionState? chosen = Source == LocomotionSource.FollowGuest
			? Engine_LocomotionStates.Find(live.State)
			: StepMachine(live, step);

		if (chosen is null)
		{
			return;
		}

		if (Source == LocomotionSource.HostMachine && live.State.Length != 0)
		{
			Samples++;
			if (chosen.Name != live.State)
			{
				Divergences++;
			}
		}

		Play(chosen);
	}

	/// <summary>
	/// Advances our own machine.
	///
	/// The velocity and control magnitudes it needs are NOT readable from the guest — it builds
	/// them into a stack temporary each tick — so a caller driving this source has to supply
	/// them, which the host can do because the host is what feeds the sticks in the first place.
	/// Until it does, the machine runs on the gates the guest DOES publish and holds whatever
	/// state those admit.
	/// </summary>
	private LocomotionState? StepMachine(HostBridge.GuestLocomotion live, float delta)
	{
		var input = new LocomotionInput(
			ControlMagnitude: ControlMagnitude,
			ControlAngle: ControlAngle,
			VelocityMagnitude: VelocityMagnitude,
			VelocityAngle: VelocityAngle,
			VerticalVelocity: 0.0f,
			MaxSpeed: MaxSpeed,
			Delta: delta,
			TargetFacingAngle: TargetFacingAngle,
			Stance: (LocomotionStance)live.Stance,
			Confinement: (LocomotionConfinement)live.Confinement,
			CombatState: (LocomotionCombatState)live.CombatState,
			IsChild: false,
			IsLooking: false);

		return _machine.Update(input);
	}

	/// <summary>Stick deflection, 0..1. Set by whatever owns this character's input.</summary>
	public float ControlMagnitude { get; set; }

	/// <summary>Radians from the character's forward, signed.</summary>
	public float ControlAngle { get; set; }

	public float VelocityMagnitude { get; set; }

	public float VelocityAngle { get; set; }

	public float TargetFacingAngle { get; set; }

	/// <summary>The base `HeroLocomotionData` top speed, the normaliser for the velocity test.</summary>
	public float MaxSpeed { get; set; } = 5.25f;

	private void Play(LocomotionState state)
	{
		if (state.Name == _playing)
		{
			return;
		}

		if (Player() is not { } player || _skeleton is null)
		{
			return;
		}

		AnimationLibrary library = player.GetAnimationLibrary(LibraryName);

		// The clip list is a preference order: the first the character actually has wins. A
		// state offering {Pose, PoseLF, PoseRF} on a rig with only `Pose` should play `Pose`
		// rather than nothing.
		foreach (string clip in state.Anim)
		{
			string? key = Engine_ClipLibrary.Ensure(
				library, _skeleton, _skeletonName, _modelHashes, clip);

			if (key is null)
			{
				continue;
			}

			player.Play($"{LibraryName}/{key}", state.BlendInTime);
			_playing = state.Name;
			Played++;
			return;
		}

		Unresolved++;
	}

	/// <summary>State changes that found a clip, and ones where the rig had none of the offered names.</summary>
	public int Played { get; private set; }

	public int Unresolved { get; private set; }
}
