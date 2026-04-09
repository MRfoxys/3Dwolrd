using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

public class PathMetrics
{
    public int Requests;
    public int CacheHits;
    public int Failures;
    public long TotalMicroseconds;
}

public class PathRequestService
{
    readonly Dictionary<(Vector3I Start, Vector3I End), List<Vector3I>> cache = new();
    readonly Queue<(Vector3I Start, Vector3I End)> cacheOrder = new();
    readonly int maxCacheSize;
    Pathfinder pathfinder;

    public PathMetrics LastMetrics { get; } = new PathMetrics();

    public PathRequestService(int maxCacheSize = 128)
    {
        this.maxCacheSize = maxCacheSize;
    }

    public void Bind(Pathfinder pathfinder)
    {
        this.pathfinder = pathfinder;
        InvalidateAll();
    }

    public List<Vector3I> GetOrCreatePath(Vector3I start, Vector3I end)
    {
        LastMetrics.Requests++;
        var key = (start, end);

        if (cache.TryGetValue(key, out var cached))
        {
            LastMetrics.CacheHits++;
            return cached;
        }

        var sw = Stopwatch.StartNew();
        var path = pathfinder?.FindPath(start, end);
        sw.Stop();
        LastMetrics.TotalMicroseconds += (long)(sw.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency));

        if (path == null)
        {
            LastMetrics.Failures++;
            return null;
        }

        cache[key] = path;
        cacheOrder.Enqueue(key);
        TrimCacheIfNeeded();
        return path;
    }

    public void InvalidateAll()
    {
        cache.Clear();
        cacheOrder.Clear();
    }

    void TrimCacheIfNeeded()
    {
        while (cache.Count > maxCacheSize && cacheOrder.Count > 0)
        {
            var oldKey = cacheOrder.Dequeue();
            cache.Remove(oldKey);
        }
    }
}
