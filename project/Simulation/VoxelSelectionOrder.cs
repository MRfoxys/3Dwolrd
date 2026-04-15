using System.Collections.Generic;
using Godot;

/// <summary>
/// Ordre stable pour enfiler les jobs voxel.
/// </summary>
public static class VoxelSelectionOrder
{
    public static void SortForMineEnqueue(List<Vector3I> cells)
    {
        cells.Sort(static (a, b) =>
        {
            int c = b.Y.CompareTo(a.Y);
            if (c != 0)
                return c;
            c = a.X.CompareTo(b.X);
            if (c != 0)
                return c;
            return a.Z.CompareTo(b.Z);
        });
    }

    /// <summary>
    /// Construction en "escalier" autour d'un ancrage (souvent rally) :
    /// - d'abord les colonnes (X,Z) proches de l'ancre,
    /// - puis, dans chaque colonne, du haut vers le bas.
    /// Les verrous de support du chantier gardent les jobs physiquement valides.
    /// </summary>
    public static void SortForBuildEnqueue(List<Vector3I> cells, Vector3I anchor)
    {
        cells.Sort((a, b) =>
        {
            int da = Mathf.Abs(a.X - anchor.X) + Mathf.Abs(a.Z - anchor.Z);
            int db = Mathf.Abs(b.X - anchor.X) + Mathf.Abs(b.Z - anchor.Z);
            int c = da.CompareTo(db);
            if (c != 0)
                return c;
            c = a.X.CompareTo(b.X);
            if (c != 0)
                return c;
            c = a.Z.CompareTo(b.Z);
            if (c != 0)
                return c;
            return b.Y.CompareTo(a.Y);
        });
    }
}
