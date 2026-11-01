using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// `stable_light_counts`: every mesh in the world paired with at least two omni and two spot
/// lights, so Godot Mobile's per-draw pipeline never changes with the lamps in reach.
/// </summary>
/// <remarks>
/// WHAT IT STOPS. Mobile specialises each draw's pipeline on how many omni and spot lights the
/// instance is paired with (`ShaderSpecialization::omni_lights` / `spot_lights`, filled from
/// `inst->omni_light_count` in render_forward_mobile.cpp), folded to none, one or several
/// (`shader_count_for`). Where ubershaders are unavailable (the Adreno 640) each new (material,
/// counts) pair is a synchronous compile, and placed lights come and go with the player — the
/// shadow budget shows and hides them, which pairs and unpairs them — so walking through
/// Bowerstone Market moves materials between nine count combinations and compiles each one it
/// reaches: 31-39 compiles in 40 s, 0.4-2.9 s apiece. With two of each type always paired, every
/// instance sits at "several" for both, and a material has one combination instead of nine.
///
/// WHY THE FILLERS COST NO SHADING. A filler is a zero-energy light reaching the whole world, so
/// the scene cull pairs it with every instance (renderer_scene_cull.cpp scores it worst, distance
/// over max(0.01, range x energy), so it is the first a real light displaces past Godot's eight
/// per mesh and never takes a real light's slot). It is "parked": distance fade on, ending a
/// centimetre from the light, so `LightStorage::update_light_buffers` drops it before it is
/// mapped this frame, and `fill_push_constant_instance_indices` writes only lights mapped this
/// frame. The pairing count sees it; the shader's index list never does. A lit instance shades
/// exactly the lights it shaded before, now through the loop of the "several" pipeline rather
/// than an unrolled single.
///
/// THE ONE READ IT ADDS. The "several" loop reads slot 0 before testing for the 0xFF end marker,
/// so an instance with no light mapped this frame shades buffer entry 255 once — the read stock
/// Godot already makes for a "single" instance whose one paired light is out of the buffer.
/// Entry 255 is never written below 256 lights in view; if it ever shades as a stray light,
/// <see cref="Sentinel"/> (`stable_light_counts_sentinel`) makes one filler of each type a real
/// zero-energy light in the buffer, so slot 0 is always a defined light that adds nothing, at the
/// price of one cheap light evaluation of each type on every pixel (no light-table record and no
/// shadow: Godot's window, the fallback inversion of `fable_attenuation`, a BRDF times zero).
///
/// Mobile only: Forward+ culls by cluster and has no per-instance count. The fillers are server
/// objects, not nodes, so no tree walk over Light3D (the shadow budget, the light table, the
/// debug listings) ever sees them. A change re-pairs every instance once, so each material on
/// screen compiles its new combination once.
/// </remarks>
public partial class Engine_StableLightCounts : Node
{
	public const string Key = "stable_light_counts";

	public const string SentinelKey = "stable_light_counts_sentinel";

	public const string NodeName = "StableLightCounts";

	/// <summary>Reaches every instance of any level: the fillers' range, and their AABB's half size.</summary>
	public const float Reach = 100000f;

	/// <summary>Where the fillers stand: below any level, never within a centimetre of a camera.</summary>
	public static readonly Vector3 Origin = new(0f, -10000f, 0f);

	/// <summary>Fillers per light type: two puts every instance at Godot's "several".</summary>
	public const int PerType = 2;

	private static readonly List<Engine_StableLightCounts> Mounted = new();

	private static bool _enabled;
	private static bool _sentinel;

	private readonly List<Rid> _lights = new();
	private readonly List<Rid> _instances = new();

	/// <summary>Whether the fillers stand in every mounted world. Main thread; applies at once.</summary>
	public static bool Enabled
	{
		get => _enabled;
		set
		{
			if (_enabled == value) return;
			_enabled = value;
			RefreshAll();
		}
	}

	/// <summary>One filler of each type lit (in the light buffer) so slot 0 is always defined.</summary>
	public static bool Sentinel
	{
		get => _sentinel;
		set
		{
			if (_sentinel == value) return;
			_sentinel = value;
			RefreshAll();
		}
	}

	/// <summary>Whether this renderer specialises on per-instance light counts at all.</summary>
	public static bool Applies => RenderingServer.GetCurrentRenderingMethod() == "mobile";

	/// <summary>Applies the settings at launch; the debug channel may change them afterwards.</summary>
	public static void Configure(IReadOnlyDictionary<string, string> config)
	{
		_enabled = Refii.Core.Engine_RefiiConfig.Bool(config, Key, false);
		_sentinel = Refii.Core.Engine_RefiiConfig.Bool(config, SentinelKey, false);
		RefreshAll();
	}

	/// <summary>Mounts the fillers' owner under <paramref name="parent"/>, for its world.</summary>
	public static void Mount(Node parent) => parent.AddChild(new Engine_StableLightCounts { Name = NodeName });

	/// <summary>Validates the optional levers before the debug endpoint mutates rendering state.</summary>
	/// <returns>The requested flags, each null when absent, without changing process state.</returns>
	internal static (bool? Enabled, bool? Sentinel) ReadRequest(JsonElement request) =>
		(ReadBool(request, "stableLightCounts"), ReadBool(request, "stableLightSentinel"));

	private static bool? ReadBool(JsonElement request, string name)
	{
		if (!request.TryGetProperty(name, out JsonElement value)) return null;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new ArgumentException($"{name} must be a boolean.");
		return value.GetBoolean();
	}

	/// <summary>Fillers standing across every mounted world.</summary>
	public static int Standing
	{
		get
		{
			int n = 0;
			foreach (Engine_StableLightCounts node in Mounted) n += node._instances.Count;
			return n;
		}
	}

	private static void RefreshAll()
	{
		foreach (Engine_StableLightCounts node in Mounted) node.Refresh();
	}

	public override void _EnterTree()
	{
		Mounted.Add(this);
		Refresh();
	}

	public override void _ExitTree()
	{
		Mounted.Remove(this);
		Clear();
	}

	private void Refresh()
	{
		Clear();
		if (!_enabled || !Applies) return;

		World3D? world = GetViewport()?.FindWorld3D();
		if (world is null) return;

		Rid scenario = world.Scenario;
		var at = new Transform3D(Basis.Identity, Origin);
		for (int i = 0; i < PerType; i++)
		{
			bool lit = _sentinel && i == 0;
			Add(RenderingServer.OmniLightCreate(), scenario, at, lit);
			Add(RenderingServer.SpotLightCreate(), scenario, at, lit);
		}
	}

	private void Add(Rid light, Rid scenario, Transform3D at, bool lit)
	{
		RenderingServer.LightSetColor(light, Colors.Black);
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.Energy, 0f);
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.IndirectEnergy, 0f);
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.VolumetricFogEnergy, 0f);
		// Below 2 x 2: no light-table record (fable_lighting's slot is SPECULAR_AMOUNT / 2 - 2).
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.Specular, 0f);
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.Range, Reach);
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.Attenuation, 0f);
		// Past 90 degrees a spot's AABB is the whole cube, as an omni's.
		RenderingServer.LightSetParam(light, RenderingServer.LightParam.SpotAngle, 179f);
		RenderingServer.LightSetShadow(light, false);
		RenderingServer.LightSetBakeMode(light, RenderingServer.LightBakeMode.Disabled);
		// Parked: out of the light buffer beyond a centimetre from the camera, so never shaded.
		RenderingServer.LightSetDistanceFade(light, !lit, 0f, 0f, 0.01f);

		Rid instance = RenderingServer.InstanceCreate2(light, scenario);
		RenderingServer.InstanceSetTransform(instance, at);
		_lights.Add(light);
		_instances.Add(instance);
	}

	private void Clear()
	{
		foreach (Rid instance in _instances) RenderingServer.FreeRid(instance);
		foreach (Rid light in _lights) RenderingServer.FreeRid(light);
		_instances.Clear();
		_lights.Clear();
	}
}
