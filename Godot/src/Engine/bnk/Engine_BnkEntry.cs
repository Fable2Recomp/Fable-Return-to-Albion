using System.Collections.Generic;

namespace Refii.Bnk;

public sealed class BnkEntry
{
    public string RelativePath { get; init; } = string.Empty;

    public uint Offset { get; init; }

    public uint Size { get; init; }

    public uint StoredSize { get; init; }

    public bool IsCompressed { get; init; }

    public IReadOnlyList<uint> ChunkSizes { get; init; } = System.Array.Empty<uint>();

    public override string ToString() => RelativePath;
}
