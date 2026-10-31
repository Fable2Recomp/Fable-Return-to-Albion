using System;
using System.Threading;

namespace Refii.Lighting;

/// <summary>Publishes detached renderer measurements across render and main threads without retaining native buffers.</summary>
internal sealed class Engine_RenderBufferTelemetry
{
    private Sample? _latest;
    private long _sequence;

    /// <summary>One actual renderer buffer configuration, captured inside the compositor callback.</summary>
    /// <param name="Sequence">Monotonic publication number for this effect instance.</param>
    /// <param name="Utc">Capture time for rejecting stale observations.</param>
    /// <param name="InternalWidth">Pre-upscale 3D buffer width.</param>
    /// <param name="InternalHeight">Pre-upscale 3D buffer height.</param>
    /// <param name="TargetWidth">Renderer's post-upscale target width.</param>
    /// <param name="TargetHeight">Renderer's post-upscale target height.</param>
    /// <param name="Views">Renderer's view count.</param>
    internal sealed record Sample(long Sequence, DateTime Utc, int InternalWidth,
        int InternalHeight, int TargetWidth, int TargetHeight, uint Views);

    /// <summary>The latest coherent immutable measurement, or null before rendering/after retirement.</summary>
    internal Sample? Latest => Volatile.Read(ref _latest);

    /// <summary>Copies scalar values from an actual callback; rejects invalid buffers rather than inventing dimensions.</summary>
    /// <param name="width">Actual internal width.</param>
    /// <param name="height">Actual internal height.</param>
    /// <param name="targetWidth">Actual target width.</param>
    /// <param name="targetHeight">Actual target height.</param>
    /// <param name="views">Actual view count.</param>
    /// <param name="utc">UTC capture time.</param>
    /// <returns>Whether a valid measurement was published.</returns>
    internal bool Publish(int width, int height, int targetWidth, int targetHeight, uint views, DateTime utc)
    {
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0 ||
            views == 0 || utc.Kind != DateTimeKind.Utc)
        {
            Clear();
            return false;
        }
        Volatile.Write(ref _latest, new Sample(Interlocked.Increment(ref _sequence), utc,
            width, height, targetWidth, targetHeight, views));
        return true;
    }

    /// <summary>Retires the observation so title transitions cannot masquerade as a rendered world.</summary>
    internal void Clear() => Volatile.Write(ref _latest, null);
}
