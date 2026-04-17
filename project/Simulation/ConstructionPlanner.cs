using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Planner build voxel orienté "frontière":
/// - priorise les cellules supportées par le monde (ou déjà planifiées dans la vague),
/// - garde un ordre déterministe,
/// - privilégie les cellules avec un poste de travail immédiatement walkable.
/// </summary>
public static class ConstructionPlanner
{
    static readonly Vector3I[] SupportNeighbors =
    {
        new(0, -1, 0), new(0, 1, 0),
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    static readonly Vector3I[] WorkOffsets =
    {
        new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
        new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
        new(1, -1, 1), new(1, -1, -1), new(-1, -1, 1), new(-1, -1, -1),
        new(1, 1, 1), new(1, 1, -1), new(-1, 1, 1), new(-1, 1, -1),
    };

    public static void SortBuildCellsForPlanning(
        List<Vector3I> cells,
        Vector3I anchor,
        Func<Vector3I, bool> isSolid,
        Func<Vector3I, bool> isWalkable)
    {
        if (cells == null || cells.Count <= 1)
            return;
        if (isSolid == null || isWalkable == null)
            return;

        var pending = new HashSet<Vector3I>(cells);
        var planned = new HashSet<Vector3I>();
        var queuedFrontier = new HashSet<Vector3I>();
        var frontier = new List<Vector3I>();
        var ordered = new List<Vector3I>(cells.Count);

        SeedFrontierFromPending(pending, planned, queuedFrontier, frontier, isSolid);

        while (pending.Count > 0)
        {
            if (frontier.Count == 0)
                SeedFallbackFrontier(pending, queuedFrontier, frontier, anchor, isWalkable);

            frontier.Sort((a, b) => CompareFrontier(a, b, anchor, isWalkable));
            var next = frontier[0];
            frontier.RemoveAt(0);
            queuedFrontier.Remove(next);
            if (!pending.Remove(next))
                continue;

            ordered.Add(next);
            planned.Add(next);

            foreach (var n in SupportNeighbors)
            {
                var candidate = next + n;
                if (!pending.Contains(candidate) || queuedFrontier.Contains(candidate))
                    continue;
                if (!HasSupport(candidate, planned, isSolid))
                    continue;
                frontier.Add(candidate);
                queuedFrontier.Add(candidate);
            }
        }

        cells.Clear();
        cells.AddRange(ordered);
    }

    static void SeedFrontierFromPending(
        HashSet<Vector3I> pending,
        HashSet<Vector3I> planned,
        HashSet<Vector3I> queuedFrontier,
        List<Vector3I> frontier,
        Func<Vector3I, bool> isSolid)
    {
        foreach (var c in pending)
        {
            if (!HasSupport(c, planned, isSolid))
                continue;
            frontier.Add(c);
            queuedFrontier.Add(c);
        }
    }

    static void SeedFallbackFrontier(
        HashSet<Vector3I> pending,
        HashSet<Vector3I> queuedFrontier,
        List<Vector3I> frontier,
        Vector3I anchor,
        Func<Vector3I, bool> isWalkable)
    {
        Vector3I best = default;
        bool found = false;
        int bestScore = int.MaxValue;

        foreach (var c in pending)
        {
            int score = DistanceScore(c, anchor);
            if (!HasImmediateWalkableWorkSpot(c, isWalkable))
                score += 50_000;
            if (score >= bestScore)
                continue;
            bestScore = score;
            best = c;
            found = true;
        }

        if (!found)
            return;
        if (queuedFrontier.Add(best))
            frontier.Add(best);
    }

    static bool HasSupport(Vector3I cell, HashSet<Vector3I> planned, Func<Vector3I, bool> isSolid)
    {
        foreach (var d in SupportNeighbors)
        {
            var n = cell + d;
            if (planned.Contains(n) || isSolid(n))
                return true;
        }

        return false;
    }

    public static bool HasSupportForDebug(Vector3I cell, HashSet<Vector3I> virtualSupports, Func<Vector3I, bool> isSolid)
    {
        if (isSolid == null)
            return false;
        if (virtualSupports == null)
            virtualSupports = new HashSet<Vector3I>();
        return HasSupport(cell, virtualSupports, isSolid);
    }

    static int CompareFrontier(Vector3I a, Vector3I b, Vector3I anchor, Func<Vector3I, bool> isWalkable)
    {
        bool wa = HasImmediateWalkableWorkSpot(a, isWalkable);
        bool wb = HasImmediateWalkableWorkSpot(b, isWalkable);
        int c = wb.CompareTo(wa);
        if (c != 0)
            return c;

        c = DistanceScore(a, anchor).CompareTo(DistanceScore(b, anchor));
        if (c != 0)
            return c;

        c = a.Y.CompareTo(b.Y);
        if (c != 0)
            return c;
        c = a.X.CompareTo(b.X);
        if (c != 0)
            return c;
        return a.Z.CompareTo(b.Z);
    }

    static int DistanceScore(Vector3I cell, Vector3I anchor)
    {
        int xz = Mathf.Abs(cell.X - anchor.X) + Mathf.Abs(cell.Z - anchor.Z);
        int y = Mathf.Abs(cell.Y - anchor.Y);
        return xz * 4 + y * 2;
    }

    static bool HasImmediateWalkableWorkSpot(Vector3I target, Func<Vector3I, bool> isWalkable)
    {
        foreach (var d in WorkOffsets)
        {
            if (isWalkable(target + d))
                return true;
        }

        return false;
    }

    public static bool HasImmediateWalkableWorkSpotForDebug(Vector3I target, Func<Vector3I, bool> isWalkable)
    {
        if (isWalkable == null)
            return false;
        return HasImmediateWalkableWorkSpot(target, isWalkable);
    }
}
