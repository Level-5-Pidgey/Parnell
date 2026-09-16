using System;
using System.Collections.Generic;

namespace RetainerRepricer.Services;

internal enum PluginLogLevel
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
}

internal readonly record struct PluginLogEntry(DateTime TimestampUtc, PluginLogLevel Level, string Message);

internal sealed class PluginLogBuffer
{
    private readonly object _sync = new();
    private readonly List<PluginLogEntry> _entries = new();
    private readonly int _maxEntries;

    public PluginLogBuffer(int maxEntries = 1000)
    {
        _maxEntries = Math.Max(100, maxEntries);
    }

    public void Add(PluginLogEntry entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
            var overflow = _entries.Count - _maxEntries;
            if (overflow > 0)
                _entries.RemoveRange(0, overflow);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    public IReadOnlyList<PluginLogEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }
}
