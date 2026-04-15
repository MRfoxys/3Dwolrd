using System.Collections.Generic;
using Godot;

/// <summary>
/// Chantier voxel : ordre de file, déblocage par support, point de ralliement pour sortir du chantier.
/// </summary>
public sealed class BuildSite
{
    public readonly int Id;
    public readonly Vector3I RallyCell;
    public readonly int MinY;
    public readonly int MaxY;
    /// <summary>
    /// Sommet par colonne (X,Z) : une case n’y est que si la colonne a au moins 2 hauteurs de prévues.
    /// Sert au phasage rally avant de poser ces « bouchons » pour limiter le risque de se retrouver coincé en hauteur.
    /// </summary>
    public readonly HashSet<Vector3I> RoofTargets;
    public readonly HashSet<Vector3I> PendingTargets;
    /// <summary>Ids des colons actuellement engagés (job actif) sur ce chantier.</summary>
    public readonly HashSet<int> WorkersCommitted = new();

    public BuildSite(int id, Vector3I rallyCell, int minY, IEnumerable<Vector3I> pendingTargets)
    {
        Id = id;
        RallyCell = rallyCell;
        MinY = minY;
        PendingTargets = new HashSet<Vector3I>(pendingTargets);

        int maxY = int.MinValue;
        foreach (var c in PendingTargets)
            maxY = Mathf.Max(maxY, c.Y);
        MaxY = maxY;

        var colMinY = new Dictionary<(int x, int z), int>();
        var colMaxY = new Dictionary<(int x, int z), int>();
        foreach (var c in PendingTargets)
        {
            var k = (c.X, c.Z);
            if (!colMaxY.ContainsKey(k))
            {
                colMinY[k] = c.Y;
                colMaxY[k] = c.Y;
            }
            else
            {
                colMinY[k] = Mathf.Min(colMinY[k], c.Y);
                colMaxY[k] = Mathf.Max(colMaxY[k], c.Y);
            }
        }

        RoofTargets = new HashSet<Vector3I>();
        foreach (var c in PendingTargets)
        {
            var k = (c.X, c.Z);
            if (colMaxY[k] > colMinY[k] && c.Y == colMaxY[k])
                RoofTargets.Add(c);
        }
    }

    /// <summary>Vrai tant qu’il reste des blocs autres que les sommets de colonne (avant rally / toits locaux).</summary>
    public bool LowerLayersStillPending()
    {
        if (RoofTargets.Count == 0)
            return false;
        foreach (var p in PendingTargets)
        {
            if (!RoofTargets.Contains(p))
                return true;
        }
        return false;
    }

    /// <summary>Tous les colons déjà engagés sur ce chantier sont au rally (Chebyshev ≤ 1).</summary>
    public bool AllCommittedWorkersAtRally(IReadOnlyList<Colonist> colonists)
    {
        if (WorkersCommitted.Count == 0)
            return true;
        foreach (var colonistId in WorkersCommitted)
        {
            if (colonistId < 0 || colonistId >= colonists.Count)
                continue;
            var c = colonists[colonistId];
            int cx = Mathf.Abs(c.Position.X - RallyCell.X);
            int cy = Mathf.Abs(c.Position.Y - RallyCell.Y);
            int cz = Mathf.Abs(c.Position.Z - RallyCell.Z);
            if (Mathf.Max(cx, Mathf.Max(cy, cz)) > 1)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Un bloc du chantier est constructible si le dessous est déjà solide dans le monde
    /// ou si la case en dessous n’est plus dans les jobs restants du même chantier.
    /// Les cibles « toit » (sommet par colonne, chantier multi-hauteur) restent verrouillées tant qu’il
    /// reste des blocs plus bas dans le volume et que les colons engagés ne sont pas au rally.
    /// </summary>
    public bool IsJobUnlocked(SimJob job, Map map, IReadOnlyList<Colonist> colonists)
    {
        if (job == null || job.Type != JobType.BuildBlock)
            return true;

        if (RoofTargets.Count > 0 && RoofTargets.Contains(job.Target))
        {
            if (LowerLayersStillPending())
                return false;
            if (!AllCommittedWorkersAtRally(colonists))
                return false;
        }

        var below = job.Target + new Vector3I(0, -1, 0);
        var tb = map.GetTile(below);
        if (tb != null && tb.Solid)
            return true;

        return !PendingTargets.Contains(below);
    }

    public void OnBuildJobCompleted(Vector3I builtCell) => PendingTargets.Remove(builtCell);

    public bool IsFinished => PendingTargets.Count == 0;
}
