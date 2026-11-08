using System.Collections.Generic;

namespace Refii.Bnk;

public sealed class BnkEntry
{
    private string _relativePath = string.Empty;

    public string RelativePath
    {
        get => _relativePath;
        init => _relativePath = value;
    }

    /// <summary>Takes a string with the same text, so the entries of one name share it.</summary>
    internal void SharePath(string path)
    {
        if (string.Equals(_relativePath, path, System.StringComparison.Ordinal))
        {
            _relativePath = path;
        }
    }

    public uint Offset { get; init; }

    public uint Size { get; init; }

    public uint StoredSize { get; init; }

    public bool IsCompressed { get; init; }

    public IReadOnlyList<uint> ChunkSizes { get; init; } = System.Array.Empty<uint>();

    public override string ToString() => RelativePath;
}
