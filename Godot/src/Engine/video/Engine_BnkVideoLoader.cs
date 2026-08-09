using System;
using Godot;

namespace Refii.Video;

/// <summary>
/// Loads <c>game://…/name.bik</c> as an <see cref="Engine_BinkVideo"/>, so movies load
/// through the same virtual path as models and textures do.
///
/// Movies come from the <see cref="Engine_VideoLibrary"/> — its own small index of the
/// videos folder — because Fable 2 ships them loose. The bank resolver is still consulted
/// afterwards so a movie that IS packed still resolves.
/// </summary>
[Tool]
public partial class Engine_BnkVideoLoader : ResourceFormatLoader
{
	public const string PathPrefix = "game://";

	private static Func<string, byte[]?>? _resolver;
	private static Engine_VideoLibrary? _library;

	public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

	/// <summary>The indexed videos folder. Consulted before the banks.</summary>
	public static void SetLibrary(Engine_VideoLibrary? library) => _library = library;

	public static Engine_VideoLibrary? Library => _library;

	public static byte[]? ReadBytes(string virtualPath) =>
		_library?.Read(virtualPath) ?? _resolver?.Invoke(virtualPath);

	public static bool Recognises(string path) =>
		path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
		path.EndsWith(".bik", StringComparison.OrdinalIgnoreCase);

	public override string[] _GetRecognizedExtensions() => new[] { "bik" };

	public override bool _RecognizePath(string path, StringName type) => Recognises(path);

	public override bool _Exists(string path) => Recognises(path);

	public override bool _HandlesType(StringName type)
	{
		string name = type.ToString();
		return name is "Resource" or "Engine_BinkVideo";
	}

	public override string _GetResourceType(string path) =>
		Recognises(path) ? "Resource" : string.Empty;

	public override Variant _Load(
		string path, string originalPath, bool useSubThreads, int cacheMode)
	{
		if (!Recognises(path))
		{
			return Variant.CreateFrom((int)Error.FileUnrecognized);
		}

		string virtualPath = path[PathPrefix.Length..];
		byte[]? bytes = ReadBytes(virtualPath);
		if (bytes is null)
		{
			GD.PushWarning($"[bnk] video not found: {path}");
			return Variant.CreateFrom((int)Error.FileNotFound);
		}

		Engine_BinkVideo? video = Engine_BinkVideo.FromBytes(bytes, out string error);
		if (video is null)
		{
			GD.PushWarning($"[bnk] {path}: {error}");
			return Variant.CreateFrom((int)Error.FileCorrupt);
		}

		video.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
		video.ResourcePath = path;
		return video;
	}
}
