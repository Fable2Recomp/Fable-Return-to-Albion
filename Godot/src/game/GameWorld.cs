using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The world, rendered by GODOT through the guest's camera.
///
/// This is the plan's phase 1 in its smallest form: the guest simulates and tells us where
/// it is looking; Godot draws. Proving the camera first is deliberate — it is nearly free,
/// it needs no entity sync, and nothing else in the world feed is visible until something
/// is looking at it.
/// </summary>
public partial class GameWorld : Node3D
{
	private Camera3D _camera = null!;
	private bool _reported;

	public override void _Ready()
	{
		_camera = new Camera3D
		{
			Name = "GuestCamera",
			Near = 0.05f,
			Far = 20_000f,

			// The guest auto-exposes; a constant cannot follow a scene it was not measured on.
			Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),
		};

		AddChild(_camera);

		// After the add, and explicitly. Godot renders a viewport through exactly one
		// current camera, and this is the window's — the front end renders into a viewport
		// of its own precisely so that it cannot take this one away, which it used to do
		// every time a widget changed state.
		_camera.MakeCurrent();
	}

	public override void _Process(double delta)
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		FollowGuestIntoTheWorld();
		TraceEntities();

		if (HostBridge.GetCamera() is not { } camera || camera.IsDefault)
		{
			return;
		}

		Vector3 position = ToGodot(camera.Position);
		Vector3 forward = ToGodot(camera.Forward).Normalized();
		Vector3 up = ToGodot(camera.Up);

		if (forward.IsZeroApprox())
		{
			return;
		}

		// Re-orthonormalise up against forward. This matches the guest rather than fighting
		// it — it does the same Gram-Schmidt itself in sub_822B52F8, correcting UP and
		// holding FORWARD fixed.
		Vector3 basisZ = -forward;
		Vector3 basisY = (up - basisZ * up.Dot(basisZ)).Normalized();
		if (basisY.IsZeroApprox())
		{
			return;
		}

		_camera.GlobalTransform = new Transform3D(
			new Basis(basisY.Cross(basisZ), basisY, basisZ), position);

		// The guest stores a HORIZONTAL field of view; Godot wants the vertical one.
		_camera.Fov = camera.VerticalFovDegrees;
		_camera.KeepAspect = Camera3D.KeepAspectEnum.Height;

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[game-world] driving Godot's camera from the guest: " +
				$"pos={position} fov={_camera.Fov:F1}deg");
		}
	}

	/// <summary>
	/// Builds the region the guest named. `sub_82379628` writes the destination before it
	/// starts the game, so Godot is told which region to build rather than inferring one.
	/// </summary>
	private void FollowGuestIntoTheWorld()
	{
		// Wait for in-game: the startup script leaves "TestWorld/TestLevel1" in these globals
		// until sub_82379628 overwrites them, so an earlier read latches the placeholder.
		if (_loaded || !HostBridge.IsInGame ||
			HostBridge.GetStartLevel() is not { } start || start.Level.Length == 0)
		{
			return;
		}

		if (!_announced)
		{
			_announced = true;
			GD.Print($"[game-world] the guest is going to {start.World}/{start.Level} " +
				$"(entrance '{start.Entrance}')");
		}

		// The editor's own construction path, then mount it under the live camera.
		//
		// NOTHING BELOW LATCHES _loaded UNTIL IT HAS ACTUALLY BUILT. The guest can cross into
		// the game before Engine_GameAssets has finished mounting — the mount is asynchronous
		// and TryBuildLevel answers "banks are not mounted" until it completes — so latching on
		// the attempt turns a few frames of bad luck into a permanently empty world that no
		// later frame ever retries.
		if (Refii.Assets.Engine_GameAssets.Instance is not { } assets)
		{
			return;
		}

		// THE SCENARIO, BEFORE THE BUILD. It is what selects the heightfield — a level ships one
		// per story state, and picking by name order put a childhood entrance in chapter 2's
		// Bowerstone. Taking it from the guest rather than inferring it is the whole point: the
		// guest states which map it is standing in.
		// The LIVE scenario wins; the start value is only the fallback before a level is up.
		if ((HostBridge.GetActiveScenario() ?? HostBridge.GetStartScenario())
				is { Length: > 0 } scenario)
		{
			assets.CurrentScenario = scenario.ToLowerInvariant();
			if (!_scenarioReported)
			{
				_scenarioReported = true;
				uint[] layers = HostBridge.GetActiveLayers();
				GD.Print($"[game-world] the guest's scenario is '{assets.CurrentScenario}', " +
					$"{layers.Length} layer(s) active");
			}
		}


		// STRAIGHT TO A NODE, not through a `.tscn` on disk.
		//
		// Building the region, saving it to `res://levels/...` and loading that back gets the
		// same construction through a round trip that cannot survive an export: `res://` is a
		// read-only pack in an exported build, so the save fails and the game gets no world at
		// all. The editor still saves, because a scene you can open IS the editor's point.
		if (assets.BuildLevelNode(start.World, start.Level, out string error) is not { } region)
		{
			// Named, not silent: an empty world under a live camera is flat grey, which reads
			// as "rendering is broken". Once only — this is retried every frame.
			if (!_buildFailureLogged)
			{
				_buildFailureLogged = true;
				GD.PrintErr($"[game-world] could not build {start.World}/{start.Level}: {error}");
			}

			return;
		}

		_loaded = true;
		AddChild(region);

		// THE REGION'S OWN COPY OF EVERY ENTITY THE GUEST PLACES, indexed so it can be retired
		// one record at a time as the guest claims it.
		//
		// Both halves of the region are built from the level's `.save` map and so is the guest,
		// so a door is drawn twice — a ghost you cannot interact with standing inside the one
		// you can. The obvious cure is to delete the static half outright and it is wrong twice
		// over: the props container also holds foliage, engine_level blocks and GMD children
		// that no `.save` record names and the guest never states, and of the guest's 1437 live
		// entities only 1030 name a model at all, so the rest have no live copy to replace the
		// static one with. Freeing the whole container emptied the street.
		//
		// The container is handed to GameEntities alive. Freeing it here and passing the freed
		// node on the next statement leaves nothing for the one-at-a-time retirement to do —
		// `IsInstanceValid` is already false when the first entity arrives.
		var twins = Refii.Entities.Engine_StaticTwins.Build(region);
		GD.Print($"[game-world] the guest owns entities here; {twins.Instances} static " +
			$"drawable(s) across {twins.Records} `.save` record(s) are waiting to be retired");

		// Alongside the region, not inside it: the live entities carry the whole guest-to-Godot
		// map themselves, exactly as the camera does.
		AddChild(new GameEntities
		{
			Name = "GameEntities",
			Twins = twins,
		});
		GD.Print($"[game-world] built and mounted {start.World}/{start.Level}");
	}

	/// <summary>
	/// Reports the guest's live entities — the plan's phase 1, proved before anything is drawn
	/// from it.
	///
	/// Three numbers decide whether the feed is usable, and none of them needs a mesh:
	/// how many entities came back, how many were SKIPPED for an unrecognised placement vtable
	/// (the set of physics delegate classes could not be proven closed, so those are counted
	/// rather than guessed), and how many model hashes resolve through the prop index.
	///
	/// That last one settles an open question by measurement instead of argument. The guest
	/// stores a resource-name hash for ModelFile and our index keys on FNV-1 of the lowercased
	/// path; whether those are the same family was NOT established from the binary, so the
	/// honest test is to hash-match real entities and count the hits.
	/// </summary>
	private void TraceEntities()
	{
		if (!_loaded || ++_entityTicks % 120 != 0 || _entityReports >= 10)
		{
			return;
		}

		System.Collections.Generic.List<HostBridge.GuestEntity> entities =
			HostBridge.GetEntities(out uint unknown, out HostBridge.UnknownPlacement[] unknownVtables);
		if (entities.Count == 0 && unknown == 0)
		{
			return;
		}

		++_entityReports;

		// Three buckets, not two. "685 of 1049 resolved" reads as a 35% index miss and is
		// not one: an entity carrying NO model hash is a trigger or a marker and was never
		// going to draw. The miss rate that matters is against the entities that do name a
		// model, and conflating the two is the same wrong-denominator mistake that sent a
		// whole turn after a parser defect that did not exist.
		int withModel = 0;
		int resolved = 0;
		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (entity.ModelHash == 0)
			{
				continue;
			}

			++withModel;
			if (Refii.Level.Engine_PropModelIndex.Resolve(
					entity.ModelHash, System.Array.Empty<string>()) is not null)
			{
				++resolved;
			}
		}

		// Descending by tally: the first address on the line is the one worth decompiling.
		System.Array.Sort(unknownVtables, (a, b) => b.Entities.CompareTo(a.Entities));
		string vtables = unknownVtables.Length == 0
			? string.Empty
			: "  unknown vtables: " +
			  string.Join(", ", System.Array.ConvertAll(
				  unknownVtables, v => $"0x{v.Vtable:X8}x{v.Entities}"));

		GD.Print($"[guest-entities] live={entities.Count} unknownPlacement={unknown} " +
			$"withModel={withModel} (noModel={entities.Count - withModel}) " +
			$"resolved={resolved}/{withModel}{vtables}");

		TracePoses(entities);

		// Concrete rows, because a count alone cannot say whether the transforms are sane —
		// an entity at the origin with an identity rotation is what a wrong offset looks like.
		//
		// UNRESOLVED ones by preference. The first few entities in list order were all
		// resolving already, so the rows confirmed what was working and said nothing about
		// the 364 that were not; these are the hashes to go and look up.
		int shown = 0;
		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (shown >= 4 || entity.ModelHash == 0 ||
				Refii.Level.Engine_PropModelIndex.Resolve(
					entity.ModelHash, System.Array.Empty<string>()) is not null)
			{
				continue;
			}

			++shown;
			GD.Print($"    unresolved 0x{entity.Address:X8} pos={entity.Position} " +
				$"rot={entity.Rotation} model=0x{entity.ModelHash:X8} " +
				$"record=0x{entity.RecordHash:X8}");
		}
	}

	/// <summary>
	/// Reports which live entities are CHARACTERS, and whether their bones are readable.
	///
	/// A character is an entity carrying an animated mesh, and the pose the guest's animation
	/// system evaluated is left in that component. Proving the feed before drawing anything
	/// from it is the same discipline the placement read got: a rig posed from a wrong offset
	/// does not look like a bad offset, it looks like a broken character.
	///
	/// The space is reported because the guest states it. The bones are written LOCAL and
	/// converted to model space in place, so a pose caught mid-update is local — and only the
	/// guest can say which one we are holding.
	/// </summary>
	private void TracePoses(System.Collections.Generic.List<HostBridge.GuestEntity> entities)
	{
		int characters = 0;
		int modelSpace = 0;
		int localSpace = 0;
		int unposed = 0;
		int bones = 0;
		int scaled = 0;
		float scaleLow = float.PositiveInfinity;
		float scaleHigh = float.NegativeInfinity;
		string sample = string.Empty;
		string chainSample = string.Empty;

		// Characters the draw path cannot reach: a live skeleton and no model hash.
		int modelless = 0;
		int modellessWithMorph = 0;
		string modellessSample = string.Empty;

		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (HostBridge.GetEntityPose(entity.Address) is not { } pose)
			{
				continue;
			}

			++characters;
			bones += pose.BoneCount;

			// THREE buckets. "modelSpace=12 of 138" invited the reading that the other 126 were
			// local, and a local pose is usable — you compose it. A pose the animation system
			// has never written is not, and it was being composed anyway.
			switch (pose.Space)
			{
				case 2: ++modelSpace; break;
				case 1: ++localSpace; break;
				default: ++unposed; break;
			}

			// THE SCALE LANES, PROVEN RATHER THAN ASSUMED. The 48-byte element is read as
			// quaternion / translation / scale, and the third of those is the only field
			// nothing has ever printed — it went straight into the bone basis on the strength
			// of the layout alone. If these are the guest's scales they are 1 almost
			// everywhere; if the lanes are actually something else the extremes say so
			// immediately, and a wrong triple multiplied into every bone is another way to
			// tear a rig apart that looks nothing like a lane mix-up.
			for (int b = 0; b < pose.BoneCount; ++b)
			{
				Vector3 bone = pose.Scale(b);
				float low = Mathf.Min(bone.X, Mathf.Min(bone.Y, bone.Z));
				float high = Mathf.Max(bone.X, Mathf.Max(bone.Y, bone.Z));
				scaleLow = Mathf.Min(scaleLow, low);
				scaleHigh = Mathf.Max(scaleHigh, high);
				if (Mathf.Abs(bone.X - 1f) > 0.01f || Mathf.Abs(bone.Y - 1f) > 0.01f ||
					Mathf.Abs(bone.Z - 1f) > 0.01f)
				{
					++scaled;
				}
			}

			// The characters with no model hash, counted against the route that can still
			// draw them.
			//
			// An entity with a live skeleton and no model hash is one component 6 cannot
			// reach: `sub_8257E0C0` returns 0 for a zero hash with no fallback. Their geometry
			// is the part LIST on CECGraphicAppearanceMorph, component id 17, so whether they
			// carry id 17 at all decides whether anything can draw them — and that is a
			// live-memory fact no amount of reading the binary answers.
			//
			// Only the ids are read here; the parts themselves come from
			// RefiiHostGetEntityMorphParts, which knows the component's layout.
			if (entity.ModelHash == 0)
			{
				++modelless;

				byte[] ids = HostBridge.GetEntityComponents(entity.Address);
				bool hasMorph = System.Array.IndexOf(ids, (byte)17) >= 0;
				if (hasMorph)
				{
					++modellessWithMorph;
				}

				if (modelless <= 3)
				{
					modellessSample += $"\n    modelless character 0x{entity.Address:X8} " +
						$"bones={pose.BoneCount} components=[{string.Join(",", ids)}] " +
						$"id17={(hasMorph ? "PRESENT" : "absent")}";
				}
			}

			// IS A "LOCAL" POSE ACTUALLY LOCAL? The one measurement that decides the explosion.
			//
			// Every rig that explodes does so by compounding down the chain, and each link adds
			// roughly the bone's REST offset rather than a bone-length delta — which is what
			// composing already-absolute transforms looks like. But that is an inference from
			// bounds, and the two possibilities need opposite fixes, so here is the direct test:
			// walk a parent chain and print each bone's stated translation.
			//
			// If they are LOCAL they are bone lengths and stay small and roughly constant with
			// depth. If they are already MODEL space they grow monotonically along the chain,
			// because each one is an absolute position further up the body. No arithmetic, no
			// theory — the numbers say which.
			if (chainSample.Length == 0 && pose.Space == 1 && pose.BoneCount > 24)
			{
				var chain = new System.Collections.Generic.List<int>();
				for (int b = pose.BoneCount - 1; b >= 0 && chain.Count < 6; b = pose.Parents[b])
				{
					chain.Add(b);
					if (pose.Parents[b] < 0 || pose.Parents[b] >= b)
					{
						break;
					}
				}

				chain.Reverse();
				var text = new System.Text.StringBuilder();
				foreach (int b in chain)
				{
					Vector3 t = pose.Translation(b);
					text.Append($"\n        bone[{b}] parent={pose.Parents[b]} " +
						$"t={t} |t|={t.Length():0.###}");
				}

				chainSample = $"\n    LOCAL-space chain on 0x{entity.Address:X8} " +
					"(|t| roughly constant with depth = genuinely local; growing = already " +
					$"model space and we are double-composing):{text}";
			}

			if (sample.Length == 0)
			{
				// Bone 0 and one mid-rig bone: a rig whose every bone sits at the origin is
				// what an array that was sized but never filled looks like, and it counts the
				// same as a good one until someone prints a position.
				int mid = pose.BoneCount / 2;
				sample = $"\n    0x{entity.Address:X8} bones={pose.BoneCount} " +
					$"space={pose.Space} root={pose.Translation(0)} " +
					$"bone{mid}={pose.Translation(mid)} rot{mid}={pose.Rotation(mid)} " +
					$"scale{mid}={pose.Scale(mid)} parent{mid}={pose.Parents[mid]}";
			}
		}

		// IS THE FACING EVEN COMING THROUGH? The measurement behind "everyone faces one way".
		//
		// A character's orientation reaches Godot through exactly one value — the quaternion in
		// the placement read — and several of the EC-2 layouts do not load one directly: the
		// hybrids delegate and fall back to IDENTITY when the chain yields nothing. If that is
		// what is happening, every affected character gets the same rotation forever, which is
		// precisely the symptom. Counted against NON-characters as the control: if props are
		// mostly oriented and characters are mostly identity, the fault is specific to the
		// class characters use and not to the read as a whole.
		int characterIdentity = 0;
		int characterOriented = 0;
		int otherIdentity = 0;
		int otherOriented = 0;

		foreach (HostBridge.GuestEntity entity in entities)
		{
			bool isCharacter = HostBridge.GetEntityPose(entity.Address) is not null;
			bool identity = Mathf.Abs(entity.Rotation.X) < 1e-5f &&
				Mathf.Abs(entity.Rotation.Y) < 1e-5f &&
				Mathf.Abs(entity.Rotation.Z) < 1e-5f;

			if (isCharacter)
			{
				if (identity) { ++characterIdentity; } else { ++characterOriented; }
			}
			else
			{
				if (identity) { ++otherIdentity; } else { ++otherOriented; }
			}
		}

		GD.Print($"[guest-facing] characters: {characterIdentity} IDENTITY rotation, " +
			$"{characterOriented} oriented | everything else: {otherIdentity} identity, " +
			$"{otherOriented} oriented — characters nearly all identity means the facing never " +
			"reaches us and no amount of bone work will turn them round");

		GD.Print($"[guest-poses] characters={characters} model={modelSpace} local={localSpace} " +
			$"NEVER-POSED={unposed} bones={bones} " +
			$"| scale lanes: {scaled} of {bones} bones are not unit, range " +
			$"{(bones == 0 ? 0f : scaleLow)}..{(bones == 0 ? 0f : scaleHigh)} " +
			$"| left in bind: " +
			$"noPose={Refii.Entities.Engine_EntitySkeleton.NoPose} " +
			$"countMismatch={Refii.Entities.Engine_EntitySkeleton.CountMismatch}" +
			$"{Refii.Entities.Engine_EntitySkeleton.LastMismatch} " +
			$"neverPosed={Refii.Entities.Engine_EntitySkeleton.NeverPosed}{sample}{chainSample}");

		// Reported separately from the pose counts above, because it says where a character
		// with no model hash gets its geometry: component 17 is the composite route, and a
		// modelless character without it is unreachable by either path.
		GD.Print($"[guest-modelless] {modelless} of {characters} character(s) have a live " +
			$"skeleton and NO model hash — nothing can draw them; {modellessWithMorph} of " +
			$"those carry component 17 (CECGraphicAppearanceMorph){modellessSample}");
	}

	private int _entityTicks;
	private int _entityReports;

	private bool _loaded;

	/// <summary>The destination has been reported. Separate from <see cref="_loaded"/>, which
	/// only becomes true once the region is actually standing.</summary>
	private bool _announced;

	private bool _buildFailureLogged;

	private bool _scenarioReported;

	/// <summary>
	/// Fable world space to Godot world space.
	///
	/// THE NET MAP THE WORLD IS BUILT WITH, which takes TWO steps to read off and is wrong
	/// if you stop after the first.
	///
	/// Step one is per placement: <see cref="Refii.Level.Engine_LevelProps.BuildTransform"/>
	/// puts a prop at <c>(X, Z, Y)</c> and <c>Engine_GmdLayout</c> puts a GMD child at the
	/// same, which on its own is a MIRROR — determinant -1. Step two is the level root:
	/// <c>Engine_LevelScene.TryBuild</c> gives it <c>Scale = (-1, 1, 1)</c>, a second mirror.
	/// The two compose to <c>(x, y, z) -> (-x, z, y)</c>, determinant +1 — a proper
	/// rotation. THE WORLD IS NOT MIRRORED; it just gets there in two moves.
	///
	/// This camera is a SIBLING of the mounted region, not a child of it, so it does not
	/// inherit that root scale and has to carry the whole net map itself. That is the trap:
	/// matching only the per-placement half puts the camera in the region's INTERNAL space,
	/// which is the level reflected through X, and it is off by a mirror even though the
	/// props and the camera now "agree".
	///
	/// The guest side is settled and verified from the binary: Fable's world is right-handed
	/// with X right, Y FORWARD, Z UP — sub_821B0068 builds [cross(fwd,up), fwd, up] as a
	/// proper rotation, and sub_821DD3B0's no-camera defaults are forward = (0,1,0) at
	/// snapshot+0x20 and up = (0,0,1) at +0x10, masked out of the lane constants at
	/// 0x82099140 = (0,ffffffff,0,0) and 0x82099150 = (0,0,ffffffff,0).
	///
	/// Because the net map is a rotation and not a mirror, rebuilding the basis below as
	/// X = Y x Z lands on the correct right-handed camera rather than a reflected one.
	/// </summary>
	private static Vector3 ToGodot(Vector3 fable) =>
		new(-fable.X, fable.Z, fable.Y);
}
