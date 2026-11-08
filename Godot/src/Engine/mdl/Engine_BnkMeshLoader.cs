using System;
using Godot;

namespace Refii.Models;

[Tool]
public partial class Engine_BnkMeshLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    private static Func<string, byte[]?>? _resolver;
    private static Func<string, int, byte[]?>? _headResolver;
    private static Func<string, bool>? _exists;

    /// <summary>How many of each kind of empty mesh get a line before the count alone speaks.</summary>
    private const int EmptyMeshReportCap = 12;

    /// <summary>
    /// Models that loaded and DREW NOTHING, split by why, for the run.
    ///
    /// A stub is a header whose body lives in a bank that is not mounted — expected during a
    /// level change, a defect if it persists. Undecodable is a reader failure. They were
    /// indistinguishable while both returned an empty mesh in silence, which is the one way a
    /// missing model leaves no trace anywhere: the node exists, the material is applied, the
    /// prop is counted as streamed, and nothing draws.
    /// </summary>
    public static int EmptyStubs { get; private set; }

    public static int EmptyUndecodable { get; private set; }

    /// <summary>Zeroed per region, so the counts describe the level rather than the run.</summary>
    public static void ResetEmptyMeshCounts()
    {
        EmptyStubs = 0;
        EmptyUndecodable = 0;
    }

    /// <param name="headResolver">Reads a file's first bytes alone; without one,
    /// <see cref="ReadHead"/> reads the whole file through <paramref name="resolver"/>.</param>
    public static void SetResolver(
        Func<string, byte[]?>? resolver, Func<string, int, byte[]?>? headResolver = null)
    {
        _resolver = resolver;
        _headResolver = headResolver;
    }

    /// <summary>The resolver in place, for a caller that swaps one in and must put it back.</summary>
    public static Func<string, byte[]?>? Resolver => _resolver;

    /// <summary>The head reader in place, put back with <see cref="Resolver"/>.</summary>
    public static Func<string, int, byte[]?>? HeadResolver => _headResolver;

    public static void SetExistsProbe(Func<string, bool>? probe) => _exists = probe;

    public static bool FileExists(string virtualPath) => _exists?.Invoke(virtualPath) ?? false;

    public static byte[]? ReadBytes(string virtualPath) => _resolver?.Invoke(virtualPath);

    /// <summary>
    /// At least the first <paramref name="count"/> bytes of the file (fewer only when it is
    /// shorter), for a caller that wants a header and not the body it would have to inflate.
    /// </summary>
    public static byte[]? ReadHead(string virtualPath, int count) =>
        _headResolver is { } head ? head(virtualPath, count) : ReadBytes(virtualPath);

    /// <summary>A texture's first bytes, as far as the longest header <c>TexHeader</c> parses.</summary>
    public static byte[]? ReadTextureHeader(string virtualPath) =>
        ReadHead(virtualPath, Refii.Textures.TexHeader.Fable3Size);

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "mdl" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "Mesh" or "ArrayMesh" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "ArrayMesh" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        string virtualPath = path[PathPrefix.Length..];
        byte[]? bytes = _resolver?.Invoke(virtualPath);
        if (bytes is null)
        {
            GD.PushWarning($"[bnk] model not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out string error, out bool isStub);

        if (mesh is null)
        {
            // AN EMPTY MESH IS A DRAW THAT NEVER HAPPENS, so it is counted and said.
            //
            // A stub used to return this in silence, on the reasoning that a header-only entry in
            // a resident bank is expected and its body pages in from a streaming one. But the
            // caller cannot tell that apart from a model that arrived and decoded to nothing: both
            // hand back a live `ArrayMesh` with no surfaces, the node is created, and every
            // counter upstream records a success. A prop that is present, placed, lit and
            // invisible is the hardest kind of missing model to find, and it was the only failure
            // in this file that produced no line at all.
            //
            // Rate-limited per KIND rather than globally: a level whose streaming bank is not
            // mounted yet can produce hundreds of stubs, and burying the handful of real decode
            // failures under them is how the log stops being readable.
            mesh = new ArrayMesh();

            int seen = isStub ? ++EmptyStubs : ++EmptyUndecodable;
            if (seen <= EmptyMeshReportCap)
            {
                GD.Print($"[bnk] {path} DREW NOTHING — " +
                    (isStub
                        ? "stub: the header states a body that is in no mounted bank"
                        : error) +
                    (seen == EmptyMeshReportCap
                        ? "  (further ones of this kind are counted, not printed)"
                        : string.Empty));
            }
        }

        mesh.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        mesh.ResourcePath = path;
        return mesh;
    }
}
