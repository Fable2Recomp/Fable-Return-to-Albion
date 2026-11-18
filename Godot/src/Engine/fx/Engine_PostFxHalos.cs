using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// Every live light that states a HALO — the content of the guest's glow-sprite layer.
/// </summary>
/// <remarks>
/// The guest renders these as camera-facing quads into a half-res [0,1] additive layer that
/// reaches the frame ONLY through bloom (off_8331A9CC feeds BRIGHTNESSCUTOFF); they are halo
/// sources, never scene draws. The identity is DATA: an FX point/spot light entity whose
/// <c>HaloTextureHash</c> is non-zero. This registry is the native stand-in for the guest's
/// scene-registry families 8/9.
///
/// Two halves, because the consumer runs on the render thread: <see cref="Register"/> collects
/// the lights as effects build; the node (one per tree, <see cref="Ensure"/>d) samples their
/// world position, colour and energy each frame on the MAIN thread and publishes plain values;
/// <see cref="Snapshot"/> hands those to the post-FX, which rasterises them into its glow layer
/// with the scene depth in hand, so a halo behind a wall dies per pixel the way the guest's
/// z-test killed it. Registered entries self-clean: the sample pass drops any light Godot has
/// freed.
///
/// The AUTHORED SPRITE: HaloTextureHash is the FNV-1 the bank keys resources by, and the
/// candidate space is the FX texture folder, so the node reverses the hash by hashing every
/// file there under the spellings the data uses, stacks the resolved sprites into one
/// <see cref="Texture2DArray"/>, and publishes each halo's layer. A hash no file answers draws
/// the procedural soft disc instead (layer −1).
/// </remarks>
[Tool]
public partial class Engine_PostFxHalos : Node
{
	public const string NodeName = "PostFxHalos";

	/// <summary>All sprites are resampled to this edge; halos are soft, small quads.</summary>
	public const int SpriteSize = 128;

	/// <summary>
	/// v1 sizing: the glow's world half-extent, taken as a fraction of the light's own range so
	/// a brazier's halo outgrows a candle's. The authored per-halo scale (alongside
	/// HaloTextureHash in the FX bank) replaces this once decoded.
	/// </summary>
	public const float HalfPerRange = 0.15f;

	private const int GuestHaloFloats = 12;
	private readonly float[] _guestHalos = new float[GuestHaloFloats * 1024];
	public const float HalfMin = 0.25f;
	public const float HalfMax = 1.5f;

	public readonly record struct Halo(Light3D Light, uint TextureHash);

	/// <summary>A halo as the render thread consumes it: world values only, no scene objects.
	/// Layer indexes the published sprite array; −1 draws the procedural disc.</summary>
	public readonly record struct WorldHalo(Vector3 Position, Vector3 Colour, float WorldHalf, float Layer);

	/// <summary>
	/// A halo pinned to a SKY DIRECTION rather than a light. The guest's glow-layer pass
	/// (`sub_82277380`, Z on, ZWrite off) draws, after the light halos, the MOON — theme texture
	/// slot 6 at <c>moonDir × 4000</c>, half-size <c>MOON_SIZE × 400</c>, alpha-blended, PS
	/// binding 144 (MOONBLOOM, <c>texel × 0.2 × alpha</c>), `0x82279410` — and the SUN DISC —
	/// slot 3 at <c>sunDir × 1900</c>, half-size <c>SUN_DISC_SIZE × 300</c>, constant 245 =
	/// <c>sunColour × SUN_DISC_COLOUR × SUN_DISC_INTENSITY</c>, `0x821D8658`. That layer is the
	/// bright pass's post-threshold input and nothing else, so these are the sun's bloom and the
	/// moon's faint one. Each draw is its own occlusion query, so neither carries a visibility
	/// term; the per-pixel depth test here is the query. <paramref name="SpriteU0"/>..
	/// <paramref name="SpriteU1"/> is the sprite's horizontal window (the moon texture holds its
	/// eight phases side by side). The MOON GLARE is a scene draw, not a halo.
	/// </summary>
	public readonly record struct SkyHalo(
		Vector3 Direction, float Distance, float WorldHalf, Vector3 Colour, Texture2D? Sprite, bool Active,
		float SpriteU0 = 0f, float SpriteU1 = 1f);

	private static readonly Refii.Core.Engine_TracedLock Gate = new("post-FX halos");
	private static readonly List<Halo> Entries = new();
	private static readonly Dictionary<string, SkyHalo> Sky = new();
	private static readonly Dictionary<string, float> SkyVisibility = new();
	private static readonly List<WorldHalo> Published = new();
	private static Rid _publishedArray;
	private static Engine_PostFxHalos? _current;

	/// <summary>
	/// Hash 0 is a VALID registration: the guest's drawer falls back to a runtime-built
	/// default texture set when a light's key resolves nothing (holder slots copied from
	/// dword_834A4340), and the procedural disc is that provider's native analogue. The
	/// caller decides which lights join — the shipped data states no keys at all, so the
	/// modern model registers every FX light and fable mode registers none.
	/// </summary>
	public static void Register(Light3D light, uint textureHash)
	{
		if (light is null)
		{
			return;
		}

		using (Gate.Hold())
		{
			Entries.Add(new Halo(light, textureHash));
		}
	}

	/// <summary>Publishes (or replaces) a sky halo under <paramref name="key"/>.</summary>
	public static void PublishSky(string key, SkyHalo halo)
	{
		using (Gate.Hold())
		{
			Sky[key] = halo;
		}
	}

	/// <summary>The cloud visibility of a sky halo, 0..1; squared on the way in like the
	/// guest's fade² (0x82279a94..0x82279ac4).</summary>
	public static void SetSkyVisibility(string key, float visibility)
	{
		using (Gate.Hold())
		{
			SkyVisibility[key] = Math.Clamp(visibility, 0f, 1f);
		}
	}

	/// <summary>Copies the published world halos and the sprite array. Render-thread safe.</summary>
	public static void Snapshot(List<WorldHalo> into, out Rid spriteArray)
	{
		into.Clear();
		using (Gate.Hold())
		{
			into.AddRange(Published);
			spriteArray = _publishedArray;
		}
	}

	/// <summary>
	/// Ensures the tree's main-thread halo publisher exists once the owning context is attached.
	/// </summary>
	/// <param name="context">An effect or celestial owner; detached region-build nodes are valid.</param>
	/// <remarks>
	/// Region construction publishes sky glare from a worker before its nodes enter the tree.
	/// GetTree on that detached owner emits a Godot error. Leave the pending registry intact:
	/// the owner's next attached celestial update retries this method and samples those values.
	/// </remarks>
	public static void Ensure(Node context)
	{
		if (!context.IsInsideTree())
		{
			return;
		}

		if (RenderingServer.GetRenderingDevice() is null)
		{
			return;
		}

		if (_current is not null && GodotObject.IsInstanceValid(_current))
		{
			return;
		}

		if (!context.IsInsideTree())
		{
			return;
		}
		SceneTree tree = context.GetTree();

		var node = new Engine_PostFxHalos { Name = NodeName };
		_current = node;
		tree.Root.CallDeferred(Node.MethodName.AddChild, node);
	}

	private readonly List<Halo> _live = new();
	private readonly List<WorldHalo> _build = new();
	private readonly List<(SkyHalo Halo, float Visibility)> _sky = new();
	private readonly Dictionary<(ulong Id, float U0, float U1), float> _spriteLayers = new();

	/// <summary>Every view's eye this frame (Engine_Views.Cameras), the window's first.</summary>
	private readonly Vector3[] _eyes = new Vector3[2];

	private readonly Dictionary<uint, float> _layers = new();
	private readonly List<Image> _sprites = new();
	private readonly List<Texture2DArray> _retained = new();
	private readonly HashSet<uint> _reported = new();
	private Dictionary<uint, string>? _hashToPath;
	private Rid _arrayRd;
	private bool _arrayDirty;

	public override void _ExitTree()
	{
		if (_current == this)
		{
			_current = null;
		}

		using (Gate.Hold())
		{
			Published.Clear();
			_publishedArray = default;
		}
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("postfxhalos");
		if (SkipUnseen()) return; // no chain to draw them: no sprites decoded, no array built
		_live.Clear();
		_sky.Clear();
		using (Gate.Hold())
		{
			for (int i = Entries.Count - 1; i >= 0; i--)
			{
				if (!GodotObject.IsInstanceValid(Entries[i].Light))
				{
					Entries.RemoveAt(i);
					continue;
				}

				_live.Add(Entries[i]);
			}

			foreach ((string key, SkyHalo halo) in Sky)
			{
				if (halo.Active)
				{
					_sky.Add((halo, SkyVisibility.TryGetValue(key, out float vis) ? vis : 1f));
				}
			}
		}

		// Sampled OUTSIDE the gate: GlobalPosition and IsVisibleInTree walk the scene tree.
		// ONE LIST FOR EVERY VIEW. The post-FX projects each halo through the camera it is
		// rendering, but what is published here is shared: the distance fade is taken from the
		// nearest view's eye, and a sky halo hangs off the window camera's.
		Camera3D? camera = GetViewport()?.GetCamera3D();
		Vector3 eye = camera is not null && GodotObject.IsInstanceValid(camera) && camera.IsInsideTree()
			? camera.GlobalPosition
			: Vector3.Zero;
		int eyes = 0;
		foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(camera))
		{
			if (eyes == _eyes.Length)
			{
				break;
			}

			if (!GodotObject.IsInstanceValid(view) || !view.IsInsideTree())
			{
				continue;
			}

			_eyes[eyes++] = view.GlobalPosition;
		}

		_build.Clear();
		foreach (Halo halo in _live)
		{
			Light3D light = halo.Light;
			if (!GodotObject.IsInstanceValid(light) || !light.IsInsideTree() || !light.IsVisibleInTree())
			{
				continue;
			}

			float energy = light.LightEnergy;
			if (energy <= 1e-4f)
			{
				continue;
			}

			Vector3 at = light.GlobalPosition;

			// The guest's glow rides colour × intensity × (flicker × DISTANCE FADE) — the
			// +0x1C0 product — so the sprite dies with its light instead of outliving the
			// renderer's cull. Godot's distance fade happens inside the renderer where this
			// feed cannot see it, so the same window is applied here.
			if (eyes > 0 && light.DistanceFadeEnabled)
			{
				float fadeBegin = light.DistanceFadeBegin;
				float fadeLength = light.DistanceFadeLength;
				float end = fadeBegin + fadeLength;
				float minDistSq = float.MaxValue;
				for (int e = 0; e < eyes; e++)
				{
					minDistSq = MathF.Min(minDistSq, _eyes[e].DistanceSquaredTo(at));
				}

				if (minDistSq >= end * end)
				{
					continue;
				}

				if (minDistSq > fadeBegin * fadeBegin)
				{
					float dist = MathF.Sqrt(minDistSq);
					energy *= Math.Clamp(
						(end - dist) / MathF.Max(fadeLength, 0.01f), 0f, 1f);
				}
			}

			Color c = Refii.Lighting.Engine_LightColour.Linear(light);
			var colour = new Vector3(c.R * energy, c.G * energy, c.B * energy);
			if (MathF.Max(MathF.Max(colour.X, colour.Y), colour.Z) <= 1e-3f)
			{
				continue;
			}

			float half = light is OmniLight3D omni
				? Math.Clamp(omni.OmniRange * HalfPerRange, HalfMin, HalfMax)
				: HalfMin;
			_build.Add(new WorldHalo(
				at, colour, half, LayerFor(halo.TextureHash)));
		}

		// Under classic, the guest's own halo list: the lights `sub_82274370` draws LIGHTHALO for
		// this frame, with the quad's half-extent and the c123 colour as it states them. The
		// placed Godot lights are hidden there and the modern FX do not run.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			uint halos = Refii.Host.HostBridge.GetLightHalos(_guestHalos);
			for (uint i = 0; i < halos; i++)
			{
				int o = (int)i * GuestHaloFloats;
				var colour = new Vector3(_guestHalos[o + 4], _guestHalos[o + 5], _guestHalos[o + 6]);
				if (MathF.Max(MathF.Max(colour.X, colour.Y), colour.Z) <= 1e-4f)
				{
					continue;
				}
				// Guest axes to the classic world's: (-x, z, y), as every classic feed places.
				var position = new Vector3(-_guestHalos[o], _guestHalos[o + 2], _guestHalos[o + 1]);
				uint hash = System.BitConverter.SingleToUInt32Bits(_guestHalos[o + 10]);
				_build.Add(new WorldHalo(position, colour, _guestHalos[o + 3], LayerFor(hash)));
			}
		}

		foreach ((SkyHalo halo, float visibility) in _sky)
		{
			Vector3 direction = halo.Direction;
			if (direction.LengthSquared() <= 1e-12f || !(halo.Distance > 0f))
			{
				continue;
			}

			float fade = visibility * visibility;
			Vector3 colour = halo.Colour * fade;
			if (MathF.Max(MathF.Max(colour.X, colour.Y), colour.Z) <= 1e-4f)
			{
				continue;
			}

			_build.Add(new WorldHalo(
				eye + (direction.Normalized() * halo.Distance), colour, halo.WorldHalf,
				LayerForSprite(halo.Sprite, halo.SpriteU0, halo.SpriteU1)));
		}

		if (_arrayDirty)
		{
			_arrayDirty = false;
			RebuildArray();
		}

		using (Gate.Hold())
		{
			Published.Clear();
			Published.AddRange(_build);
			_publishedArray = _arrayRd;
		}
	}

	/// <summary>The array layer for an already-loaded sprite (the celestial textures), cropped
	/// to its horizontal window <paramref name="u0"/>..<paramref name="u1"/> first; −1 for none
	/// or one that will not decode.</summary>
	private float LayerForSprite(Texture2D? sprite, float u0, float u1)
	{
		if (sprite is null || !GodotObject.IsInstanceValid(sprite))
		{
			return -1f;
		}

		(ulong, float, float) id = (sprite.GetInstanceId(), u0, u1);
		if (_spriteLayers.TryGetValue(id, out float known))
		{
			return known;
		}

		float layer = -1f;
		Image? image = Refii.Core.Engine_ServerSyncs.Image(sprite);
		if (image is not null && !image.IsEmpty())
		{
			if (image.IsCompressed())
			{
				image.Decompress();
			}

			image.Convert(Image.Format.Rgba8);
			if (u0 > 0f || u1 < 1f)
			{
				int x0 = Math.Clamp((int)MathF.Floor(u0 * image.GetWidth()), 0, image.GetWidth() - 1);
				int x1 = Math.Clamp((int)MathF.Ceiling(u1 * image.GetWidth()), x0 + 1, image.GetWidth());
				image = image.GetRegion(new Rect2I(x0, 0, x1 - x0, image.GetHeight()));
			}

			image.Resize(SpriteSize, SpriteSize, Image.Interpolation.Lanczos);
			if (image.HasMipmaps())
			{
				image.ClearMipmaps();
			}

			_sprites.Add(image);
			layer = _sprites.Count - 1;
			_arrayDirty = true;
		}

		_spriteLayers[id] = layer;
		return layer;
	}

	private float LayerFor(uint hash)
	{
		if (_layers.TryGetValue(hash, out float known))
		{
			return known;
		}

		float layer = -1f;
		if (ResolvePath(hash) is string path &&
			Refii.Assets.Engine_GameAssets.Instance?.ReadAllBytes(path) is byte[] bytes &&
			Refii.Textures.Engine_TexImage.TryCreateImage(bytes, out string error) is Image image)
		{
			if (image.IsCompressed())
			{
				image.Decompress();
			}

			image.Convert(Image.Format.Rgba8);
			image.Resize(SpriteSize, SpriteSize, Image.Interpolation.Lanczos);
			if (image.HasMipmaps())
			{
				image.ClearMipmaps();
			}

			_sprites.Add(image);
			layer = _sprites.Count - 1;
			_arrayDirty = true;
		}
		else if (_reported.Add(hash))
		{
			GD.Print($"[postfx] halo texture 0x{hash:X8} has no file in {FableFx.TextureFolder}; " +
				"drawing the procedural disc");
		}

		_layers[hash] = layer;
		return layer;
	}

	private void RebuildArray()
	{
		var images = new Godot.Collections.Array<Image>();
		foreach (Image sprite in _sprites)
		{
			images.Add(sprite);
		}

		var array = new Texture2DArray();
		if (Refii.Textures.Engine_TextureUpload.CreateLayered(array, images) != Error.Ok)
		{
			GD.PrintErr("[postfx] halo sprite array creation failed");
			return;
		}

		// Resolve the RD backing texture on the render thread so `--render-thread separate`
		// never stalls the main thread with a synchronous `push_and_ret` round-trip.
		_retained.Add(array);
		Rid texRid = array.GetRid();
		RenderingServer.CallOnRenderThread(Callable.From(() =>
		{
			Rid resolved = RenderingServer.TextureGetRdTexture(texRid);
			if (resolved.IsValid)
			{
				_arrayRd = resolved;
				using (Gate.Hold())
				{
					_publishedArray = resolved;
				}
			}
		}));
	}

	private string? ResolvePath(uint hash)
	{
		if (_hashToPath is null)
		{
			Refii.Assets.Engine_GameAssets? assets = Refii.Assets.Engine_GameAssets.Instance;
			if (assets is null || !assets.IsMounted)
			{
				return null;
			}

			// The hash keys a RESOURCE NAME (rex_lhResourceName_ComputeHash — FNV-1 of the
			// lowercased string), and the candidate space is the FX texture folder. Which
			// spelling the bank hashed is not stated, so every plausible one maps to the file.
			string folder = FableFx.TextureFolder.TrimEnd('/');
			var index = new Dictionary<uint, string>();
			foreach (string file in assets.GetFiles(folder))
			{
				string path = folder + "/" + file;
				int dot = file.LastIndexOf('.');
				string stem = dot < 0 ? file : file[..dot];
				index.TryAdd(FxBank.Fnv1Lower(stem), path);
				index.TryAdd(FxBank.Fnv1Lower(file), path);
				index.TryAdd(FxBank.Fnv1Lower(path), path);
			}

			_hashToPath = index;
		}

		return _hashToPath.TryGetValue(hash, out string? found) ? found : null;
	}
}
