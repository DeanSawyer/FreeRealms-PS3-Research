using System;
using System.Collections.Generic;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// The values of the last <c>character/create</c> request (Name, Head, ...), kept so the game-server side can send the same
/// character. Shared between the web front and the gateway; replaces the module-level dictionary of the Python reference.
/// </summary>
public sealed class CharacterStore
{
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Forgets the previous character and keeps <paramref name="values"/> (the first value of each name wins).</summary>
    public void Replace(IEnumerable<(string Name, string Value)> values)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
            next.TryAdd(name, value);

        lock (_gate)
            _values = next;
    }

    /// <summary>A copy of the current values; empty before any character was created.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }
}
