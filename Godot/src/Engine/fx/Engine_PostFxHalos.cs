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
	public const float HalfMin = 0.25f;
	public const float HalfMax = 1.5f;

	public readonly record struct Halo(Light3D Light, uint TextureHash);

	/// <summary>A halo as the render thread consumes it: world values only, no scene objects.
	/// Layer indexes the published sprite array; −1 draws the procedural disc.</summary>
	public readonly record struct WorldHalo(Vector3 Position, Vector3 Colour, float WorldHalf, float Layer);

	private static readonly object Gate = new();
	private static readonly List<Halo> Entries = new();
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

		lock (Gate)
		{
			Entries.Add(new Halo(light, textureHash));
		}
	}

	/// <summary>Copies the published world halos and the sprite array. Render-thread safe.</summary>
	public static void Snapshot(List<WorldHalo> into, out Rid spriteArray)
	{
		into.Clear();
		lock (Gate)
		{
			into.AddRange(Published);
			spriteArray = _publishedArray;
		}
	}

	public static void Ensure(Node context)
	{
		if (RenderingServer.GetRenderingDevice() is null)
		{
			return;
		}

		if (_current is not null && GodotObject.IsInstanceValid(_current))
		{
			return;
		}

		SceneTree? tree = context.GetTree();
		if (tree is null)
		{
			return;
		}

		var node = new Engine_PostFxHalos { Name = NodeName };
		_current = node;
		tree.Root.CallDeferred(Node.MethodName.AddChild, node);
	}

	private readonly List<Halo> _live = new();
	private readonly List<WorldHalo> _build = new();

	private readonly Dictionary<uint, float> _layers = new();
	private readonly List<Image> _sprites = new();
	private readonly List<Texture2DArray> _retained = new();
	private readonly HashSet<uint> _reported = new();
	private Dictionary<uint, string>? _hashToPath;
	private Rid _arrayRd;

	public override void _ExitTree()
	{
		if (_current == this)
		{
			_current = null;
		}

		lock (Gate)
		{
			Published.Clear();
			_publishedArray = default;
		}
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("postfxhalos");
		_live.Clear();
		lock (Gate)
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
		}

		// Sampled OUTSIDE the gate: GlobalPosition and IsVisibleInTree walk the scene tree.
		Camera3D? camera = GetViewport()?.GetCamera3D();
		Vector3 eye = camera?.GlobalPosition ?? Vector3.Zero;
		_build.Clear();
		foreach (Halo halo in _live)
		{
			Light3D light = halo.Light;
			if (!light.IsVisibleInTree())
			{
				continue;
			}

			float energy = light.LightEnergy;

			// The guest's glow rides colour × intensity × (flicker × DISTANCE FADE) — the
			// +0x1C0 product — so the sprite dies with its light instead of outliving the
			// renderer's cull. Godot's distance fade happens inside the renderer where this
			// feed cannot see it, so the same window is applied here.
			if (camera is not null && light.DistanceFadeEnabled)
			{
				float end = light.DistanceFadeBegin + light.DistanceFadeLength;
				float dist = eye.DistanceTo(light.GlobalPosition);
				energy *= Math.Clamp(
					(end - dist) / MathF.Max(light.DistanceFadeLength, 0.01f), 0f, 1f);
			}

			Color c = light.LightColor;
			var colour = new Vector3(c.R * energy, c.G * energy, c.B * energy);
			if (MathF.Max(MathF.Max(colour.X, colour.Y), colour.Z) <= 1e-3f)
			{
				continue;
			}

			float half = light is OmniLight3D omni
				? Math.Clamp(omni.OmniRange * HalfPerRange, HalfMin, HalfMax)
				: HalfMin;
			_build.Add(new WorldHalo(
				light.GlobalPosition, colour, half, LayerFor(halo.TextureHash)));
		}

		lock (Gate)
		{
			Published.Clear();
			Published.AddRange(_build);
			_publishedArray = _arrayRd;
		}
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
			RebuildArray();
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
		if (array.CreateFromImages(images) != Error.Ok)
		{
			GD.PrintErr("[postfx] halo sprite array creation failed");
			return;
		}

		// The PREVIOUS array must outlive any in-flight frame that still binds its RD texture,
		// and rebuilds are rare (one per distinct sprite, once) — retaining them all is cheaper
		// than a handshake with the render thread.
		_retained.Add(array);
		_arrayRd = RenderingServer.TextureGetRdTexture(array.GetRid());
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
