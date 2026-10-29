using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Fx;

/// <summary>
/// AUTHORED PIECES a recipe may hang on a dressed effect — looks the shipped banks never had,
/// drawn by Godot with the game's own effect textures and meshes:
/// <list type="bullet">
/// <item><c>embers</c>: particles climbing a spiral cone (a fire tornado with the flame texture,
/// an electric one with the spark texture, chaos with the silk);</item>
/// <item><c>arcs</c>: lightning arcs re-struck many times a second between random points on the
/// cone, as ribbons of the lightning texture;</item>
/// <item><c>ring</c>: a shockwave disc expanding and fading on a period;</item>
/// <item><c>blades</c>: the Blades spell's own cutlass mesh, several, orbiting tilted, each with
/// its aura.</item>
/// </list>
/// A piece is a part kind of an EFFECT (<see cref="FxEffect.Pieces"/>, fx/effects/*.json
/// <c>pieces</c>; a recipe's <c>pieces</c> are stated as such an effect): built by
/// <see cref="Build"/> as a child of the <see cref="FableFx"/> drawing the effect, so it follows it
/// and dies with it. Textures are the bank's <c>art/fx/pfx_textures/*.tex</c> by leaf name.
/// </summary>
/// <remarks>
/// A PIECE IS GODOT'S DRAWING; THE GUEST HAS NO SUCH KIND, so the guest's bank never carries one and
/// the classic look never shows one. Every kind is expressible with the bank's own entities, which
/// the guest simulates and both looks draw — the form an effect that must play everywhere takes
/// (Fable2/mods-src/spell_weaving/fx/effects/*.json, each pinned to its piece by SpellWeavingLookTests):
/// <list type="bullet">
/// <item><c>embers</c> — a standard emitter on a disc (<c>subType</c> 2, <c>spawnRadius</c> = radius)
/// shooting each particle out from the EDGE (<c>shapeFlagA</c>, <c>shapeFlagB</c>) at the cone's
/// widening, (0.8 radiusTop − radius) ÷ period with 0.4 radiusTop ÷ period of <c>speedSpread</c> (the
/// piece's ±20% jitter); lifted by a reversed <c>gravityScale</c> against <c>vec1</c> (1, 1, 0.5),
/// which holds the climb at height ÷ (period × 30) a step one frame after the spawn; <c>rate</c> =
/// max(8, count) ÷ (period × 30) a frame (the bank's rate is particles per frame), living the
/// period (the last colour/size key). A true cone, every particle turning about its axis at the
/// piece's <c>speed</c>: the emitter's <c>spin</c> (<see cref="FxEmitter.Spin"/>, which the guest
/// takes from the dress table).</item>
/// <item><c>arcs</c> — a spline entity (typeId 5) round the cone over the band the arcs crawled
/// (0.125..0.825 of the height), <c>splineRandomGenerator</c> putting its point anywhere on it each
/// step, carrying a standard emitter of bolt sprites (the lightning texture, width × the mean
/// chord) at count × rate × 0.65 a second (the piece skipped 35% of its strikes), each living one
/// strike, 1 ÷ rate, and lit at the piece's mean flash (E[f²], f in 0.55..1).</item>
/// <item><c>ring</c> — a standard emitter at height emitting one particle per period with an aligned
/// visual (<c>hasAlignQuat</c>, lying flat) whose keys, at (k ÷ 6)² of the period, follow the
/// piece's lerp(radius ÷ 10, radius, √t) and its colour and alpha × (1 − t).</item>
/// <item><c>blades</c> — one attractor (typeId 4) holding a persistent particle per blade, and one
/// per aura, in orbit: each emitter (<c>positionalInteractable</c>, <c>initialNum</c> 1, rate 0, a
/// system looping forever) starts at the blade's place with the velocity of the exact discrete
/// orbit at the piece's turn rate, <c>vec1.z</c> 0 freezing its height; the blades stand on one
/// sphere about the attractor, so each blade's height moves up to 0.1 m. The blade is Fable II's
/// own Blades sword — SpellShotDamoclesSword's model (globals.gdb ModelFile,
/// npc_sword_damocles.mdl) as the mesh visual at the piece's size — laid along its orbit, tip first,
/// climbing by the piece's tilt, the flat facing out (its <c>meshAlign</c> under the emitter's
/// rotation, <see cref="FxVisual.MeshAlign"/>), turning about the vertical at the orbit's rate
/// (<c>orAxis</c> (0, 0, −1) in the root's frame). The sword's own effects — FX_Damocles_Trail's
/// fxspl_damocles_trail and SwordContinueFX's fxspl_damocles_sword_continue, the bank's own
/// emitters and systems, referenced — ride an orbital (typeId 3) in step with it.</item>
/// </list>
/// The light is the bank's point light (typeId 7) with its own flicker walk: <c>p21</c> =
/// 1 − e^(−23/30), the corner of the old wave's fast 23 rad/s, and <c>p20</c> = flicker × the old
/// wave's spread ÷ the walk's.
/// </remarks>
public static class Engine_FxRecipePieces
{
	public sealed record Piece(
		string Kind,
		string Texture,
		Color Colour,
		Color ColourEnd,
		float Height,
		float Radius,
		float RadiusTop,
		float Rate,
		float Speed,
		float Size,
		float SizeEnd,
		int Count,
		float Period,
		float Width,
		string Mesh,
		float Tilt,
		float Intensity,
		string Blend = "add");

	private const string TextureFolder = "art/fx/pfx_textures/";
	private static readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, ArrayMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>A bank effect texture by leaf name (<c>fxt_flames_double_03</c>), cached; null when missing.</summary>
	public static Texture2D? LoadTexture(string leaf)
	{
		string name = leaf.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? leaf : leaf + ".tex";
		if (_textures.TryGetValue(name, out Texture2D? cached))
		{
			return cached;
		}

		Texture2D? texture = null;
		if (FableFx.ReferenceTextureDirectory.Length != 0)
		{
			string loose = System.IO.Path.Combine(FableFx.ReferenceTextureDirectory, name);
			if (System.IO.File.Exists(loose))
			{
				texture = Engine_TexImage.TryCreateTexture(System.IO.File.ReadAllBytes(loose), out string looseError);
				if (texture is null)
				{
					GD.Print($"[fx-recipes] reference texture {loose} did not decode: {looseError}");
				}
			}
		}

		foreach (string candidate in new[] { TextureFolder + name, "data/" + TextureFolder + name })
		{
			if (texture is not null)
			{
				break;
			}

			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(candidate);
			if (bytes is not null && Engine_TexImage.TryCreateTexture(bytes, out _) is { } made)
			{
				texture = made;
				break;
			}
		}

		if (texture is null)
		{
			GD.Print($"[fx-recipes] piece texture '{name}' unresolved");
		}

		_textures[name] = texture;
		return texture;
	}

	/// <summary>A game mesh by its virtual path, cached; null when missing.</summary>
	public static ArrayMesh? LoadMesh(string path)
	{
		string virtualPath = path.Replace('\\', '/');
		if (_meshes.TryGetValue(virtualPath, out ArrayMesh? cached))
		{
			return cached;
		}

		ArrayMesh? mesh = null;
		byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath) ?? Engine_BnkMeshLoader.ReadBytes("data/" + virtualPath);
		if (bytes is not null)
		{
			mesh = Engine_MdlMesh.TryCreateMesh(bytes, virtualPath, out string error, out _);
			if (mesh is null)
			{
				GD.Print($"[fx-recipes] piece mesh '{virtualPath}' failed: {error}");
			}
		}
		else
		{
			GD.Print($"[fx-recipes] piece mesh '{virtualPath}' unresolved");
		}

		_meshes[virtualPath] = mesh;
		return mesh;
	}

	/// <summary>Unshaded additive billboard material with a bank texture and vertex colour.</summary>
	public static StandardMaterial3D Additive(Texture2D? texture, bool billboard) => Billboard(texture, billboard, "add");

	/// <summary>
	/// Unshaded billboard material in the vertex colour; <paramref name="blend"/> "add" lights
	/// the scene up (fire, sparks, bolts), "mix" covers it (smoke, dust, a dark twister).
	/// </summary>
	public static StandardMaterial3D Billboard(Texture2D? texture, bool billboard, string blend)
	{
		var material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = blend == "mix" ? BaseMaterial3D.BlendModeEnum.Mix : BaseMaterial3D.BlendModeEnum.Add,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			VertexColorUseAsAlbedo = true,
			DisableReceiveShadows = true,
			NoDepthTest = false,
			AlbedoTexture = texture,
		};
		if (billboard)
		{
			material.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
			material.BillboardKeepScale = true;
		}

		return material;
	}

	private const string GlowShader = """
		shader_type spatial;
		render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;
		uniform sampler2D tex : source_color, filter_linear_mipmap;
		uniform vec4 tint : source_color = vec4(1.0);
		uniform bool billboard = false;

		void vertex() {
			if (billboard) {
				MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
				MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0), vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0), vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0), vec4(0.0, 0.0, 0.0, 1.0));
			}
		}

		void fragment() {
			vec4 t = texture(tex, UV);
			float coverage = max(t.a, max(t.r, max(t.g, t.b)));
			ALBEDO = COLOR.rgb * tint.rgb * coverage;
			ALPHA = coverage * COLOR.a * tint.a;
		}
		""";

	private static Shader? _glow;

	/// <summary>Additive glow in the vertex colour times <paramref name="tint"/>, shaped by the texture's coverage.</summary>
	public static ShaderMaterial Glow(Texture2D? texture, Color tint, bool billboard)
	{
		_glow ??= new Shader { Code = GlowShader };
		var material = new ShaderMaterial { Shader = _glow };
		if (texture is not null)
		{
			material.SetShaderParameter("tex", texture);
		}
		material.SetShaderParameter("tint", tint);
		material.SetShaderParameter("billboard", billboard);
		return material;
	}

	public static Node3D? Build(Piece piece)
	{
		switch (piece.Kind)
		{
			case "embers":
				return Engine_FxEmbers.Make(piece);
			case "arcs":
				return new Engine_FxArcs(piece);
			case "ring":
				return new Engine_FxShockRing(piece);
			case "blades":
				return Engine_FxOrbitingBlades.Make(piece);
			default:
				GD.PushWarning($"[fx-recipes] unknown piece kind '{piece.Kind}'");
				return null;
		}
	}
}

/// <summary>Particles up a spiral cone: the tornado. The motion is analytic in the process shader.</summary>
public static class Engine_FxEmbers
{
	private const string ProcessShader = """
		shader_type particles;
		uniform float height = 3.0;
		uniform float radius0 = 0.3;
		uniform float radius1 = 1.4;
		uniform float spin = 4.0;
		uniform float size0 = 0.5;
		uniform float size1 = 0.15;
		uniform vec4 color0 : source_color = vec4(1.0, 0.6, 0.15, 1.0);
		uniform vec4 color1 : source_color = vec4(0.6, 0.05, 0.0, 0.0);
		uniform float jitter = 0.15;

		uint pcg(uint v) {
			uint state = v * 747796405u + 2891336453u;
			uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
			return (word >> 22u) ^ word;
		}

		float rnd(uint seed) {
			return float(pcg(seed)) / 4294967295.0;
		}

		void start() {
			uint s = RANDOM_SEED + NUMBER * 7919u;
			CUSTOM = vec4(rnd(s) * 6.2831853, 0.0, rnd(s + 1u), rnd(s + 2u));
			TRANSFORM[3].xyz = vec3(0.0);
		}

		void process() {
			CUSTOM.y += DELTA / LIFETIME;
			float t = CUSTOM.y;
			if (t >= 1.0) {
				ACTIVE = false;
			}
			float rr = mix(radius0, radius1, t) * (0.8 + 0.4 * CUSTOM.z);
			float a = CUSTOM.x + spin * t * LIFETIME * (0.8 + 0.4 * CUSTOM.w);
			vec3 p = vec3(cos(a) * rr, t * height + jitter * sin(a * 3.0 + CUSTOM.z * 6.0), sin(a) * rr);
			TRANSFORM[3].xyz = p;
			COLOR = mix(color0, color1, t);
			float sz = mix(size0, size1, t);
			TRANSFORM[0].xyz = vec3(sz, 0.0, 0.0);
			TRANSFORM[1].xyz = vec3(0.0, sz, 0.0);
			TRANSFORM[2].xyz = vec3(0.0, 0.0, sz);
		}
		""";

	private static Shader? _shader;

	private static StandardMaterial3D ParticleMaterial(Engine_FxRecipePieces.Piece piece)
	{
		StandardMaterial3D material = Engine_FxRecipePieces.Billboard(Engine_FxRecipePieces.LoadTexture(piece.Texture), true, piece.Blend);
		material.BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles;
		return material;
	}

	public static Node3D Make(Engine_FxRecipePieces.Piece piece)
	{
		_shader ??= new Shader { Code = ProcessShader };
		var process = new ShaderMaterial { Shader = _shader };
		process.SetShaderParameter("height", piece.Height);
		process.SetShaderParameter("radius0", piece.Radius);
		process.SetShaderParameter("radius1", piece.RadiusTop);
		process.SetShaderParameter("spin", piece.Speed);
		process.SetShaderParameter("size0", piece.Size);
		process.SetShaderParameter("size1", piece.SizeEnd);
		process.SetShaderParameter("color0", (piece.Colour * piece.Intensity) with { A = piece.Colour.A });
		process.SetShaderParameter("color1", piece.ColourEnd);

		var particles = new GpuParticles3D
		{
			Name = "Embers",
			Amount = Math.Max(8, piece.Count),
			Lifetime = Math.Max(0.2f, piece.Period),
			LocalCoords = true,
			ProcessMaterial = process,
			DrawPass1 = new QuadMesh { Size = Vector2.One, Material = ParticleMaterial(piece) },
			Explosiveness = 0f,
			Randomness = 0.3f,
			VisibilityAabb = new Aabb(new Vector3(-piece.RadiusTop - 1f, -1f, -piece.RadiusTop - 1f), new Vector3(2f * piece.RadiusTop + 2f, piece.Height + 2f, 2f * piece.RadiusTop + 2f)),
			Emitting = true,
		};
		return particles;
	}
}

/// <summary>Lightning arcs between random points on the cone, re-struck <c>rate</c> times a second.</summary>
public partial class Engine_FxArcs : MeshInstance3D
{
	private readonly Engine_FxRecipePieces.Piece _piece;
	private readonly ImmediateMesh _mesh = new();
	private readonly Random _random = new();
	private double _until;

	public Engine_FxArcs(Engine_FxRecipePieces.Piece piece)
	{
		_piece = piece;
		Name = "Arcs";
		Mesh = _mesh;
		MaterialOverride = Engine_FxRecipePieces.Glow(Engine_FxRecipePieces.LoadTexture(piece.Texture), Colors.White, false);
		CastShadow = ShadowCastingSetting.Off;
	}

	public override void _Process(double delta)
	{
		_until -= delta;
		if (_until > 0.0)
		{
			return;
		}

		_until = 1.0 / Math.Max(1f, _piece.Rate);
		Strike();
	}

	private Vector3 OnCone(float t, float angle)
	{
		float radius = Mathf.Lerp(_piece.Radius, _piece.RadiusTop, t);
		return new Vector3(Mathf.Cos(angle) * radius, t * _piece.Height, Mathf.Sin(angle) * radius);
	}

	private void Strike()
	{
		_mesh.ClearSurfaces();
		// The ribbons are one mesh for every view, so they face the nearest view's eye: the view
		// that sees them largest.
		Vector3 eye = new(0f, 1f, 10f);
		float nearest = float.MaxValue;
		foreach (Camera3D camera in Refii.Core.Engine_Views.Cameras(GetViewport()?.GetCamera3D()))
		{
			float d = camera.GlobalPosition.DistanceSquaredTo(GlobalPosition);
			if (d < nearest)
			{
				nearest = d;
				eye = ToLocal(camera.GlobalPosition);
			}
		}

		int arcs = Math.Max(1, _piece.Count);
		for (int n = 0; n < arcs; n++)
		{
			// Some strikes skip: lightning is never steady.
			if (_random.NextDouble() < 0.35)
			{
				continue;
			}

			// Each arc crawls a stretch of the cone's surface: up a third of the height and round
			// up to a quarter turn, so the lightning climbs the funnel rather than tangling across it.
			float t0 = (float)_random.NextDouble() * 0.7f;
			float angle0 = (float)(_random.NextDouble() * Math.Tau);
			Vector3 a = OnCone(t0, angle0);
			Vector3 b = OnCone(t0 + 0.15f + (float)_random.NextDouble() * 0.2f, angle0 + (float)(_random.NextDouble() - 0.5) * Mathf.Pi * 0.5f);
			const int segments = 8;
			var points = new Vector3[segments + 1];
			// The kinks stay a fraction of a segment, so the ribbon never doubles back on itself.
			float segment = a.DistanceTo(b) / segments;
			for (int i = 0; i <= segments; i++)
			{
				float t = (float)i / segments;
				Vector3 along = a.Lerp(b, t);
				float wobble = Mathf.Sin(t * Mathf.Pi) * segment * 0.8f;
				points[i] = along + new Vector3(
					(float)(_random.NextDouble() - 0.5) * wobble, (float)(_random.NextDouble() - 0.5) * wobble,
					(float)(_random.NextDouble() - 0.5) * wobble);
			}

			float flash = 0.55f + (float)_random.NextDouble() * 0.45f;
			Color colour = _piece.Colour * (_piece.Intensity * flash);
			colour.A = flash;
			_mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.TriangleStrip);
			for (int i = 0; i <= segments; i++)
			{
				// One facing for the whole ribbon, from its chord, so the strip never twists.
				Vector3 side = (b - a).Normalized().Cross((eye - points[i]).Normalized()).Normalized() * (_piece.Width * 0.5f);
				float u = (float)i / segments;
				_mesh.SurfaceSetColor(colour);
				_mesh.SurfaceSetUV(new Vector2(0f, u));
				_mesh.SurfaceAddVertex(points[i] - side);
				_mesh.SurfaceSetColor(colour);
				_mesh.SurfaceSetUV(new Vector2(1f, u));
				_mesh.SurfaceAddVertex(points[i] + side);
			}

			_mesh.SurfaceEnd();
		}
	}
}

/// <summary>A shockwave: a flat ring that grows to <c>radius</c> and fades over <c>period</c>, again and again.</summary>
public partial class Engine_FxShockRing : MeshInstance3D
{
	private readonly Engine_FxRecipePieces.Piece _piece;
	private readonly StandardMaterial3D _material;
	private double _age;

	public Engine_FxShockRing(Engine_FxRecipePieces.Piece piece)
	{
		_piece = piece;
		Name = "ShockRing";
		Mesh = new PlaneMesh { Size = Vector2.One * 2f };
		_material = Engine_FxRecipePieces.Billboard(Engine_FxRecipePieces.LoadTexture(piece.Texture), false, piece.Blend);
		_material.VertexColorUseAsAlbedo = false;
		MaterialOverride = _material;
		Position = new Vector3(0f, piece.Height, 0f);
		CastShadow = ShadowCastingSetting.Off;
	}

	public override void _Process(double delta)
	{
		_age += delta;
		float period = Math.Max(0.2f, _piece.Period);
		float t = (float)(_age % period) / period;
		float radius = Mathf.Lerp(Math.Max(0.05f, _piece.Radius * 0.1f), _piece.Radius, Mathf.Sqrt(t));
		Scale = new Vector3(radius, 1f, radius);
		float fade = 1f - t;
		Color colour = _piece.Colour * (_piece.Intensity * fade);
		colour.A = fade;
		_material.AlbedoColor = colour;
	}
}

/// <summary>The Blades spell's own cutlasses orbiting the base, tilted, each with its aura.</summary>
public static class Engine_FxOrbitingBlades
{
	public static Node3D? Make(Engine_FxRecipePieces.Piece piece)
	{
		ArrayMesh? mesh = Engine_FxRecipePieces.LoadMesh(piece.Mesh.Length != 0 ? piece.Mesh : "art/inventory/weapons/dotxsi/cutlass_master/cutlass_master.mdl");
		var orbit = new Engine_FxOrbit { Name = "Blades", TurnsPerSecond = piece.Speed };
		int count = Math.Max(1, piece.Count);
		Texture2D? aura = Engine_FxRecipePieces.LoadTexture(piece.Texture.Length != 0 ? piece.Texture : "fxt_democles_aura");
		var steel = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.75f, 0.78f, 0.85f),
			Metallic = 0.9f,
			Roughness = 0.25f,
			EmissionEnabled = true,
			Emission = piece.Colour * 0.35f,
		};
		for (int i = 0; i < count; i++)
		{
			float angle = Mathf.Tau * i / count;
			// The funnel widens as it rises: each blade sits a step higher and a step further out.
			float rise = piece.Height + (i * piece.Period);
			float reach = piece.Radius + (i * piece.Width * 0.25f);
			var pivot = new Node3D { Name = $"Blade_{i}", Position = new Vector3(Mathf.Cos(angle) * reach, rise, Mathf.Sin(angle) * reach) };
			// The pivot's local X points outward and its local Z along the orbit; the cutlass is
			// modelled along Y, so a turn about X lays it along the orbit, less Tilt degrees of climb.
			pivot.RotationDegrees = new Vector3(0f, -Mathf.RadToDeg(angle), 0f);
			var blade = new Node3D { RotationDegrees = new Vector3(90f - piece.Tilt, 0f, 0f), Scale = Vector3.One * Math.Max(0.1f, piece.Size) };
			if (mesh is not null)
			{
				blade.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = steel, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
			}

			pivot.AddChild(blade);
			if (aura is not null)
			{
				// The aura rides each blade as an additive billboard in the overlay colour.
				pivot.AddChild(new MeshInstance3D
				{
					Mesh = new QuadMesh { Size = Vector2.One * Math.Max(0.1f, piece.Width) },
					MaterialOverride = Engine_FxRecipePieces.Glow(aura, (piece.Colour * piece.Intensity) with { A = 0.8f }, true),
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				});
			}

			orbit.AddChild(pivot);
		}

		return orbit;
	}
}
