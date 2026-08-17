using System;
using Godot;

namespace Refii.Textures;

/// <summary>
/// A super-resolution backend. One implementation, registered once, used by every semantic.
/// </summary>
/// <remarks>
/// The plan's §8 asks for an EMBEDDED implementation that keeps the model and the Vulkan
/// context alive, not a process per texture. That is a property of the implementation, so it
/// is not in this interface — what the interface enforces instead is that the boundary is an
/// <see cref="Image"/> in and an <see cref="Image"/> out, with no file path anywhere. A
/// backend that wants to shell out has to encode and decode a PNG itself, in plain sight,
/// rather than the pipeline doing it on its behalf.
///
/// <see cref="Id"/> is an INPUT TO THE CACHE KEY, so it must change whenever the model or its
/// weights change. "realesr-general-v3" is a good id; "esrgan" is not.
/// </remarks>
public interface ITexUpscaler
{
	/// <summary>Identifies the model and its weights. Rides in the cache key.</summary>
	string Id { get; }

	/// <summary>
	/// Resamples <paramref name="source"/> by <paramref name="scale"/>.
	/// </summary>
	/// <remarks>
	/// <paramref name="source"/> arrives DECOMPRESSED, in <see cref="Image.Format.Rgba8"/>,
	/// with no mip chain — the caller strips all three, because every backend would otherwise
	/// have to. The result is expected in the same form and at exactly
	/// <c>source.GetWidth() * scale</c>; the caller re-encodes and rebuilds the chain.
	///
	/// Return false to decline. Declining is not a failure — a backend that cannot honour
	/// <paramref name="filter"/> should decline rather than substitute a filter it can do,
	/// and the pipeline keeps the original texture.
	/// </remarks>
	bool TryUpscale(Image source, TexFilterClass filter, int scale, out Image? result);
}

/// <summary>
/// Holds the registered upscaler, and the settings that decide whether it is consulted.
/// </summary>
/// <remarks>
/// NOTHING IS ENHANCED UNTIL A BACKEND IS REGISTERED. With none, <see cref="Backend"/> is null,
/// <see cref="BackendId"/> is "none", and the whole enhancement path short circuits — the
/// pipeline still decodes and still caches, which is the part that pays off today. That is
/// deliberate: the plan's phases 4 and 5 land behind this seam without touching a call site.
/// </remarks>
public static class Engine_TexEnhance
{
	private static ITexUpscaler? _backend;

	private static readonly object Gate = new();

	/// <summary>The registered backend, or null when enhancement is off.</summary>
	public static ITexUpscaler? Backend
	{
		get
		{
			lock (Gate)
			{
				return _backend;
			}
		}
	}

	/// <summary>
	/// Registers the backend, or clears it with null.
	/// </summary>
	/// <remarks>
	/// Call it BEFORE the first texture request. Registering later is allowed and safe, but
	/// everything already resolved keeps the texture it was given: the pipeline does not walk
	/// the live scene re-requesting, because a swap of every material's albedo mid frame is a
	/// far bigger hammer than a setting change deserves.
	/// </remarks>
	public static void Register(ITexUpscaler? backend)
	{
		lock (Gate)
		{
			_backend = backend;
		}

		GD.Print($"[tex] upscaler backend: {backend?.Id ?? "none"}");
	}

	/// <summary>The backend's id, or "none". An input to the cache key.</summary>
	public static string BackendId => Backend?.Id ?? "none";

	/// <summary>
	/// Whether enhancement runs at all.
	/// </summary>
	/// <remarks>
	/// THE EDITOR NEVER ENHANCES, and that is the first condition rather than an afterthought.
	/// The asset browser, the model viewer and the screen viewer exist to show what is actually
	/// in the player's game data — an upscaled albedo in the model viewer is a picture of our
	/// pipeline, not of the asset, and someone checking whether a texture decoded correctly would
	/// be looking at a resample of it. <see cref="Godot.Engine.IsEditorHint"/> is false in an
	/// exported build and in a headless tool run, so only the editor is excluded.
	///
	/// The other two conditions are the player's setting and a registered backend, in that order.
	/// </remarks>
	public static bool Enabled =>
		!Godot.Engine.IsEditorHint() &&
		(_runtimeOverride ?? Engine_TexSettings.UpscaleEnabled) &&
		Backend is not null;

	/// <summary>
	/// A runtime answer that outranks the file, or null to follow it.
	/// </summary>
	/// <remarks>
	/// Needed because the in-game switch does NOT write refii.toml. Without an override,
	/// <see cref="SetEnabled"/> would flip the backend and then every request would still be
	/// refused by a settings read that says what the file said at startup — the toggle would
	/// appear to do nothing until the next launch.
	/// </remarks>
	private static bool? _runtimeOverride;

	/// <summary>
	/// Turns enhancement on or off DURING PLAY, reverting what is already on screen.
	/// </summary>
	/// <remarks>
	/// This is the switch a player flips, so "off" has to mean the world goes back to looking
	/// like the original — registering null alone would only stop FUTURE work and leave every
	/// texture already swapped in still showing its enhanced form until a level change.
	/// <see cref="Engine_TexLibrary.ApplyEnhancementSetting"/> does the reverting.
	///
	/// Turning it back on is cheap after the first time: the results are on disk, so the workers
	/// re-apply from the cache rather than re-running the backend.
	/// </remarks>
	public static void SetEnabled(bool enabled, bool persist = false)
	{
		if (Godot.Engine.IsEditorHint())
		{
			return;
		}

		_runtimeOverride = enabled;

		if (persist)
		{
			Engine_TexSettings.PersistUpscaleEnabled(enabled);
		}

		if (enabled && Backend is null)
		{
			Register(Create(Engine_TexSettings.Model));
		}

		// The backend can still be null here — an unknown model name, for instance — and then
		// "on" is a promise the pipeline cannot keep. Report what actually happened rather than
		// what was asked for.
		Engine_TexLibrary.ApplyEnhancementSetting(enabled && Backend is not null);
	}

	/// <summary>
	/// Registers the backend the settings ask for. OFF unless they say otherwise.
	/// </summary>
	/// <remarks>
	///     texture_upscaling_enabled = true   in refii.toml, or REFII_TEX_UPSCALE=1
	///     texture_upscaling_model = "..."    which backend; "resample" is the only one built
	///
	/// An unrecognised model registers nothing and SAYS SO. Treating a typo as "on" would mean a
	/// misremembered model name silently produced the built-in resampler's output and got
	/// attributed to the model.
	/// </remarks>
	public static void RegisterDefault()
	{
		_registered = true;
		Engine_TexSettings.Report();
		_runtimeOverride = null;

		if (Godot.Engine.IsEditorHint() || !Engine_TexSettings.UpscaleEnabled)
		{
			Register(null);
			return;
		}

		Register(Create(Engine_TexSettings.Model));
	}

	private static bool _registered;

	/// <summary>
	/// <see cref="RegisterDefault"/>, but only if nothing has configured the backend yet.
	/// </summary>
	/// <remarks>
	/// FOR ENTRY POINTS THAT ARE NOT THE GAME. <see cref="Refii.Game.GameSession"/> registers on
	/// behalf of a normal session, but the pub games are their own scene and are also run on their
	/// own as a harness — started that way there is no backend, and the upscaling silently does
	/// nothing with no line saying why.
	///
	/// GUARDED RATHER THAN JUST CALLED, because <see cref="RegisterDefault"/> clears the runtime
	/// override. Calling it unconditionally from a scene that loads during play would throw away a
	/// switch the player had just flipped and re-read the setting from the file.
	/// </remarks>
	public static void EnsureRegistered()
	{
		if (_registered)
		{
			return;
		}

		RegisterDefault();
	}

	private static ITexUpscaler? Create(string model)
	{
		switch (model.Trim().ToLowerInvariant())
		{
			case "" or "0" or "off" or "false" or "no":
				return null;

			case "1" or "on" or "true" or "yes" or "resample":
				return new Engine_TexResample();

			case "realesrgan" or "real-esrgan" or "esrgan":
			{
				// FALLS BACK RATHER THAN FAILING. The native library is not built yet, and even
				// once it is a player may have no suitable Vulkan device — asking for the neural
				// model and getting nothing at all would be worse than getting the classical one,
				// so the log says which arrived.
				// BESIDE THE BUILD, which is where the native library is staged and where the
				// weights are documented to live. res:// is the project, not the build output.
				// Per-architecture, matching where the native build stages this arch's
				// closure — a flat build/bin cannot hold an x86_64 and an ARM64 copy of
				// the same DLL at once.
				string weightsDirectory = Refii.Core.Engine_NativeLibraries.StagedBinDirectory(
					ProjectSettings.GlobalizePath("res://"));

				ITexUpscaler? neural = Engine_TexRealEsrgan.TryCreate(
					weightsDirectory, Engine_TexSettings.Weights);
				if (neural is not null)
				{
					return neural;
				}

				GD.Print("[tex] falling back to the classical resampler");
				return new Engine_TexResample();
			}

			default:
				GD.PushWarning(
					$"[tex] texture_upscaling_model={model} names no known backend; " +
					"enhancement stays off");
				return null;
		}
	}
}
