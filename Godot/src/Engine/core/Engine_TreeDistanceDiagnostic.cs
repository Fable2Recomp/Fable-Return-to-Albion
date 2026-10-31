using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Refii.Core;

/// <summary>Temporarily hides distant tree LOD nodes to bound their rendering cost; never a shipping quality policy.</summary>
internal sealed class Engine_TreeDistanceDiagnostic
{
    /// <summary>A detached node observation; distance is to its nearest world bounds, not its centre.</summary>
    internal readonly record struct Entry(ulong Id, string Path, bool Visible, double Distance);

    private readonly Dictionary<ulong, bool> _saved = new();

    /// <summary>Zero is normal rendering; otherwise the diagnostic cutoff in metres.</summary>
    internal int Metres { get; private set; }

    /// <summary>Nodes hidden in the current diagnostic arm.</summary>
    internal int Hidden => _saved.Count;

    /// <summary>Validates a request before touching nodes, restores the old arm, then observes the current world.</summary>
    /// <param name="request">Optional treeDiagnosticDistance integer: zero or 100–1000 metres.</param>
    /// <param name="capture">Fresh scalar observations obtained after restoration.</param>
    /// <param name="publish">Restores/hides a node if it still exists; missing old-world nodes are ignored.</param>
    internal void ApplyRequest(JsonElement request, Func<IEnumerable<Entry>> capture, Action<ulong, bool> publish)
    {
        if (!request.TryGetProperty("treeDiagnosticDistance", out JsonElement value)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int metres) ||
            (metres != 0 && (metres < 100 || metres > 1000)))
            throw new JsonException("treeDiagnosticDistance must be zero or an integer from 100 through 1000.");
        foreach (var saved in _saved) publish(saved.Key, saved.Value);
        _saved.Clear();
        Metres = 0;
        if (metres == 0) return;
        foreach (Entry entry in capture())
        {
            string name = entry.Path.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (!name.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith("_LOD1.mdl", StringComparison.OrdinalIgnoreCase) ||
                !entry.Visible || !double.IsFinite(entry.Distance) || entry.Distance <= metres)
                continue;
            if (_saved.TryAdd(entry.Id, entry.Visible)) publish(entry.Id, false);
        }
        Metres = metres;
    }
}
