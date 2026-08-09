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
		ResourceLoader.AddResourceFormatLoader(_registered);
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

		byte[]? bytes = _resolver?.Invoke(path[PathPrefix.Length..]);
		if (bytes is null)
		{
			GD.PushWarning($"[bnk] texture not found: {path}");
			return Variant.CreateFrom((int)Error.FileNotFound);
		}

		ImageTexture? texture = Engine_TexImage.TryCreateTexture(bytes, out string error);
		if (texture is null)
		{
			GD.PushWarning($"[bnk] {path}: {error}");
			return Variant.CreateFrom((int)Error.FileCorrupt);
		}

		texture.ResourcePath = path;
		return texture;
	}
}
