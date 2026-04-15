using System.Collections.Generic;
using Godot;

public enum DesignationType
{
    Build,
    Mine,
    CutTree,
}

public sealed class WorkDesignation
{
    public DesignationType Type;
    public Vector3I Target;
    public JobPriority Priority = JobPriority.Normal;
    public string BuildTileType = "stone";
    public bool Planned;
}

/// <summary>
/// Couche "designation" façon RimWorld: le joueur marque le terrain,
/// puis le planner convertit ensuite en jobs exécutables.
/// </summary>
public sealed class DesignationBoard
{
    readonly Dictionary<(DesignationType Type, Vector3I Target), WorkDesignation> _items = new();

    public bool Has(DesignationType type, Vector3I target) => _items.ContainsKey((type, target));

    public void AddOrUpdate(WorkDesignation d)
    {
        if (d == null)
            return;
        _items[(d.Type, d.Target)] = d;
    }

    public void MarkPlanned(DesignationType type, Vector3I target)
    {
        if (_items.TryGetValue((type, target), out var d))
            d.Planned = true;
    }

    public void MarkCompleted(DesignationType type, Vector3I target)
    {
        _items.Remove((type, target));
    }

    public void Clear()
    {
        _items.Clear();
    }

    public void ClearAllPlannedFlags()
    {
        foreach (var kv in _items)
            kv.Value.Planned = false;
    }

    public void CopyByType(DesignationType type, List<WorkDesignation> buffer, bool onlyUnplanned = false)
    {
        buffer.Clear();
        foreach (var d in _items.Values)
        {
            if (d.Type != type)
                continue;
            if (onlyUnplanned && d.Planned)
                continue;
            buffer.Add(d);
        }
    }
}
