using System.Collections.Generic;

namespace Refii.Animations;

public sealed class AnimEvent
{
    public float Time { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Param { get; set; } = string.Empty;
}

public sealed class AnimTrackBone
{
    public string Name { get; set; } = string.Empty;

    public int Parent { get; set; } = -1;
}

public sealed class AnimClip
{
    public uint Key0 { get; set; }

    public uint Key1 { get; set; }

    public uint DataOffset { get; set; }

    public uint TocFrameCount { get; set; }

    public uint DataSizeBytes { get; set; }

    public float Fps { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<AnimEvent> Events { get; } = new();

    public IReadOnlyList<AnimTrackBone>? TrackMap { get; set; }

    public float DurationSeconds
    {
        get
        {
            if (Fps <= 0.0f || TocFrameCount == 0)
            {
                return 0.0f;
            }

            return TocFrameCount / Fps;
        }
    }
}
