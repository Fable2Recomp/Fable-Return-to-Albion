using System;
using System.Collections.Generic;
using System.Threading;

namespace Refii.Textures;

/// <summary>Releases exclusive image/upload resources unless ownership is explicitly published.</summary>
/// <remarks>Godot images and native image arrays retain pixel references until disposed or
/// finalized. Uploads retain/copy the native data they need; the caller's temporary references
/// can then be released without disposing the shared GPU texture. Never register borrowed
/// images or already published textures here. A newly created texture may be tracked until
/// publication transfers it; a racing loser is then released promptly. Tracking permits parallel terrain
/// slice builders; native arrays registered last are released before their image wrappers.</remarks>
internal sealed class Engine_TextureTemporaries : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<IDisposable> _owned = new();
    private bool _disposed;

    /// <summary>Registers a newly owned resource once by identity, preserving nullable results.</summary>
    /// <param name="resource">An exclusive temporary, never a borrowed image or shared texture.</param>
    /// <returns>The unchanged resource for convenient decode/factory assignment.</returns>
    internal T? Own<T>(T? resource) where T : class, IDisposable
    {
        if (resource is null) return null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_seen.Add(resource)) _owned.Add(resource);
        }
        return resource;
    }

    /// <summary>Transfers an exclusive resource to its published owner without disposing it.</summary>
    /// <param name="resource">A registered resource which has just been committed to its longer-lived owner.</param>
    /// <returns>The unchanged resource, now excluded from this operation's cleanup.</returns>
    internal T Transfer<T>(T resource) where T : class, IDisposable
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_seen.Remove(resource)) throw new InvalidOperationException("Resource is not owned by this operation");
            _owned.RemoveAll(candidate => ReferenceEquals(candidate, resource));
        }
        return resource;
    }

    /// <summary>Releases every registered resource in reverse order, even if one cleanup fails.</summary>
    public void Dispose()
    {
        IDisposable[] resources;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            resources = _owned.ToArray();
            _owned.Clear();
            _seen.Clear();
        }
        List<Exception>? errors = null;
        for (int i = resources.Length - 1; i >= 0; --i)
        {
            try { resources[i].Dispose(); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }

    /// <summary>Transfers one owned image to a one-shot completion, releasing it after apply or skip.</summary>
    /// <param name="resource">The produced resource, which must survive until the queued callback.</param>
    /// <param name="apply">The main-thread operation; it may return early for a dead target.</param>
    /// <returns>A callback that applies at most once and releases on success, skip or exception.</returns>
    internal static Action ReleaseAfter<T>(T resource, Action<T> apply) where T : class, IDisposable
    {
        T? pending = resource;
        return () =>
        {
            T? current = Interlocked.Exchange(ref pending, null);
            if (current is null) return;
            using (current) apply(current);
        };
    }
}
