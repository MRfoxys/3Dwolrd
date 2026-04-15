using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Gestion centralisée des échafaudages virtuels :
/// - cellules scaffold partagées entre colons,
/// - cache des cibles déjà traitées,
/// - génération légère en colonne (sans A*).
/// </summary>
public sealed class VirtualScaffoldSystem
{
    readonly HashSet<Vector3I> _cells = new();
    readonly HashSet<Vector3I> _seededTargets = new();
    readonly HashSet<int> _seededBuildSites = new();
    int _version;

    public IReadOnlyCollection<Vector3I> Cells => _cells;
    public int Version => _version;

    public void Reset()
    {
        _cells.Clear();
        _seededTargets.Clear();
        _seededBuildSites.Clear();
        _version = 0;
    }

    public bool ClearAllScaffolds()
    {
        if (_cells.Count == 0 && _seededTargets.Count == 0 && _seededBuildSites.Count == 0)
            return false;
        _cells.Clear();
        _seededTargets.Clear();
        _seededBuildSites.Clear();
        _version++;
        return true;
    }

    public bool HasAt(Vector3I p) => _cells.Contains(p);

    public bool IsTargetSeeded(Vector3I target) => _seededTargets.Contains(target);

    public void MarkTargetSeeded(Vector3I target) => _seededTargets.Add(target);

    public void ClearTargetSeeded(Vector3I target) => _seededTargets.Remove(target);

    public bool IsBuildSiteSeeded(int buildSiteId) => buildSiteId != 0 && _seededBuildSites.Contains(buildSiteId);

    public void MarkBuildSiteSeeded(int buildSiteId)
    {
        if (buildSiteId != 0)
            _seededBuildSites.Add(buildSiteId);
    }

    public bool HasNearby(Vector3I around, int radius)
    {
        foreach (var s in _cells)
        {
            int d = Mathf.Abs(s.X - around.X) + Mathf.Abs(s.Y - around.Y) + Mathf.Abs(s.Z - around.Z);
            if (d <= radius)
                return true;
        }

        return false;
    }

    public bool TryGenerateColumn(
        Map map,
        JobBoard jobBoard,
        Dictionary<int, BuildSite> buildSites,
        Vector3I target,
        int buildSiteId,
        Func<Vector3I, bool> hasPathProbe,
        out int generatedCount)
    {
        generatedCount = 0;
        const int maxHeight = 14;
        const int maxSupportScanDepth = 18;

        ReadOnlySpan<Vector3I> standOffsets = stackalloc Vector3I[]
        {
            new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
            new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
        };

        bool found = false;
        Vector3I bestStand = default;
        int bestGroundY = int.MinValue;
        int bestScore = int.MaxValue;

        foreach (var d in standOffsets)
        {
            var stand = target + d;
            var standTile = map.GetTile(stand);
            if (standTile == null || standTile.Solid)
                continue;
            if (jobBoard.HasActiveJobOnTarget(stand, JobType.BuildBlock))
                continue;
            if (buildSiteId != 0
                && buildSites.TryGetValue(buildSiteId, out var site)
                && site.PendingTargets.Contains(stand))
                continue;

            if (!TryFindSupportY(map, stand, maxSupportScanDepth, out int supportY))
                continue;

            int height = stand.Y - supportY;
            if (height <= 0 || height > maxHeight)
                continue;

            int score = Mathf.Abs(stand.X - target.X) + Mathf.Abs(stand.Z - target.Z) + height * 2;
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestStand = stand;
            bestGroundY = supportY;
            found = true;
        }

        if (!found)
            return false;

        var added = new List<Vector3I>();
        for (int y = bestGroundY + 1; y <= bestStand.Y; y++)
        {
            var p = new Vector3I(bestStand.X, y, bestStand.Z);
            if (p == target)
                continue;
            var tile = map.GetTile(p);
            if (tile == null || tile.Solid)
                continue;
            if (_cells.Add(p))
            {
                added.Add(p);
                generatedCount++;
                _version++;
            }
        }

        if (generatedCount <= 0)
            return false;

        // Si la colonne n'ouvre aucun accès réel, rollback immédiat.
        if (hasPathProbe != null)
        {
            var probeFrom = new Vector3I(bestStand.X, bestStand.Y, bestStand.Z);
            if (!hasPathProbe(probeFrom))
            {
                foreach (var p in added)
                {
                    if (_cells.Remove(p))
                        _version++;
                }
                generatedCount = 0;
                return false;
            }
        }

        return generatedCount > 0;
    }

    /// <summary>
    /// Génère une "échelle" verticale sur la colonne du colon et s'arrête
    /// dès qu'une hauteur débloque un chemin vers la destination.
    /// Si aucune hauteur ne marche, rollback complet pour éviter les scaffolds inutiles.
    /// </summary>
    public bool TryGenerateLiftWithPathProbe(
        Map map,
        Vector3I origin,
        int maxLiftHeight,
        Func<Vector3I, bool> hasPathFromHeight,
        out int generatedCount)
    {
        generatedCount = 0;
        if (maxLiftHeight <= 0 || hasPathFromHeight == null)
            return false;

        var added = new List<Vector3I>();
        int topY = origin.Y + maxLiftHeight;
        for (int y = origin.Y + 1; y <= topY; y++)
        {
            var p = new Vector3I(origin.X, y, origin.Z);
            var tile = map.GetTile(p);
            if (tile == null || tile.Solid)
                break;

            if (_cells.Add(p))
            {
                added.Add(p);
                generatedCount++;
                _version++;
            }

            if (hasPathFromHeight(p))
                return generatedCount > 0;
        }

        // Aucun chemin débloqué : on enlève la colonne provisoire.
        foreach (var p in added)
        {
            if (_cells.Remove(p))
                _version++;
        }
        generatedCount = 0;
        return false;
    }

    /// <summary>
    /// Génère un escalier scaffold orienté vers la cible (X/Z + montée progressive).
    /// Idéal pour les murs/pyramides: approche en diagonale plutôt qu'une colonne isolée.
    /// Rollback si aucun niveau ne débloque de chemin.
    /// </summary>
    public bool TryGenerateStairWithPathProbe(
        Map map,
        Vector3I origin,
        Vector3I target,
        int maxSteps,
        Func<Vector3I, bool> hasPathFromPoint,
        out int generatedCount)
    {
        generatedCount = 0;
        if (maxSteps <= 0 || hasPathFromPoint == null)
            return false;

        var added = new List<Vector3I>();
        var current = origin;

        for (int step = 0; step < maxSteps; step++)
        {
            int nextX = current.X;
            int nextZ = current.Z;

            int dx = target.X - current.X;
            int dz = target.Z - current.Z;
            if (Mathf.Abs(dx) >= Mathf.Abs(dz) && dx != 0)
                nextX += Math.Sign(dx);
            else if (dz != 0)
                nextZ += Math.Sign(dz);

            int nextY = current.Y + 1;
            var next = new Vector3I(nextX, nextY, nextZ);
            var tile = map.GetTile(next);
            if (tile == null || tile.Solid)
                break;

            if (_cells.Add(next))
            {
                added.Add(next);
                generatedCount++;
                _version++;
            }

            current = next;

            if (hasPathFromPoint(current))
                return generatedCount > 0;
        }

        foreach (var p in added)
        {
            if (_cells.Remove(p))
                _version++;
        }
        generatedCount = 0;
        return false;
    }

    bool TryFindSupportY(Map map, Vector3I stand, int maxDepth, out int supportY)
    {
        supportY = int.MinValue;
        int minY = Mathf.Max(0, stand.Y - maxDepth);
        for (int y = stand.Y - 1; y >= minY; y--)
        {
            var p = new Vector3I(stand.X, y, stand.Z);
            if (HasAt(p))
            {
                supportY = y;
                return true;
            }

            var tile = map.GetTile(p);
            if (tile != null && tile.Solid)
            {
                supportY = y;
                return true;
            }
        }

        return false;
    }
}
