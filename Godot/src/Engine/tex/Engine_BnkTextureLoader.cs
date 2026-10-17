using System;
using Godot;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

[Tool]
public partial class Engine_BnkTextureLoader : ResourceFormatLoader
{
	public const string PathPrefix = "game://";

	private static Func<string, byte[]?>? _resolver;

	public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

	public static byte[]? ReadBytes(string virtualPath) => _resolver?.Invoke(virtualPath);

	private static Engine_BnkTextureLoader? _registered;

	/// <summary>
	/// Registers this loader with Godot's ResourceLoader, once per process.
	///
	/// Without it <c>game://…</c> resolves to nothing, and the failure is silent — a
	/// screen simply comes up untextured. Callers that build a UI outside the running
	/// game (the screen viewer, the scene exporter) reach it through
	/// <see cref="Engine_UiTextureCache"/> rather than knowing to do this themselves.
	/// </summary>
	public static void EnsureRegistered()
	{
		if (_registered is not null)
		{
			return;
		}

		_registered = new Engine_BnkTextureLoader();

		// AT THE FRONT. Godot tries format loaders in registration order and moves on to the
		// next one whenever a loader returns null, so appending put ResourceFormatLoaderBinary
		// ahead of this: it ran FileAccess::open on every `game://…` path, failed, and logged
		// `Cannot open file '…'` (resource_format_binary.cpp:1156) before this loader was even
		// consulted. The texture still arrived, which is why it read as a missing asset and was
		// not one — 70 distinct paths in one run, and 28 per frame from the loading screen,
		// each costing a failed open first.
		ResourceLoader.AddResourceFormatLoader(_registered, atFront: true);
	}

	public static bool Recognises(string path) =>
		path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
		path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);

	public override string[] _GetRecognizedExtensions() => new[] { "tex" };

	public override bool _RecognizePath(string path, StringName type) => Recognises(path);

	public override bool _Exists(string path) => Recognises(path);

	public override bool _HandlesType(StringName type)
	{
		string name = type.ToString();
		return name is "Texture2D" or "Texture" or "Resource" or "ImageTexture";
	}

	public override string _GetResourceType(string path) =>
		Recognises(path) ? "ImageTexture" : string.Empty;

	public override Variant _Load(
		string path, string originalPath, bool useSubThreads, int cacheMode)
	{
		if (!Recognises(path))
		{
			return Variant.CreateFrom((int)Error.FileUnrecognized);
		}

		Func<string, byte[]?> read = _resolver ?? (_ => null);

		// THROUGH THE LIBRARY, not straight to the decoder. This route and the material route
		// decode the same files — a bank texture is a bank texture whether a prop's albedo or a
		// menu frame names it — so they share the persistent cache under user:// rather than each
		// paying the untile and re-encode once per process.
		//
		// Ui is the semantic because that is what this path is FOR: the front end, the HUD and
		// the editor's viewers all arrive here. It is the slot the caller bound, not a guess from
		// the file name, which is the rule the whole semantic enum exists to keep.
		//
		// Decode rather than Get: Godot's ResourceLoader is this route's memory tier and it also
		// FREES what it caches, so the texture must not also be pinned in the library's live map.
		Image? image = Engine_TexLibrary.Decode(
			path[PathPrefix.Length..], TexSemantic.Ui, read, out _);
		if (image is null || image.IsEmpty())
		{
			// The library has already said why, in its own warning.
			return Variant.CreateFrom((int)Error.FileNotFound);
		}

		ImageTexture texture = ImageTexture.CreateFromImage(image);
		texture.ResourcePath = path;

		// §7 PUTS THIS PATH FIRST. The HUD, the menus and the front end are priority 0, and they
		// were the one route that could never be enhanced at all — the loader handed its texture
		// to Godot and forgot it, so there was nothing left to swap. Tracked weakly, so Godot
		// still owns the lifetime. A no-op when the player has upscaling off, and in the editor.
		Engine_TexLibrary.TrackUi(path[PathPrefix.Length..], texture, read);

		return texture;
	}
}
