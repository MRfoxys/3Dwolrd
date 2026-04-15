using System.Collections.Generic;
using Godot;

/// <summary>Ligne droite discrète entre deux cases monde (pour sélection / file de minage).</summary>
public static class VoxelLine
{
    public static void FillLine(Vector3I from, Vector3I to, List<Vector3I> output)
    {
        output.Clear();
        int n = Mathf.Max(
            Mathf.Abs(to.X - from.X),
            Mathf.Max(Mathf.Abs(to.Y - from.Y), Mathf.Abs(to.Z - from.Z)));
        if (n == 0)
        {
            output.Add(from);
            return;
        }

        for (int i = 0; i <= n; i++)
        {
            output.Add(new Vector3I(
                from.X + (to.X - from.X) * i / n,
                from.Y + (to.Y - from.Y) * i / n,
                from.Z + (to.Z - from.Z) * i / n));
        }
    }
}
