using Godot;
using System.Collections.Generic;

/// <summary>Parcours grille DDA des cellules solides le long d&apos;un rayon (occlusion Q/Alt, repli picking).</summary>
public static class TerrainRayDda
{
    /// <param name="rayLength">≤0 utilise <paramref name="defaultRayMax"/>.</param>
    public static void CollectOrderedSolidCellsAlongRay(
        Map map,
        float defaultRayMax,
        Vector3 from,
        Vector3 dirNormalized,
        List<Vector3I> output,
        float startAdvance = 0f,
        float rayLength = -1f)
    {
        output.Clear();
        if (map == null)
            return;

        float rayMax = rayLength > 0f ? rayLength : defaultRayMax;

        Vector3 dir = dirNormalized;
        if (dir.LengthSquared() < 1e-12f)
            return;
        dir = dir.Normalized();

        Vector3 rayOrigin = from + dir * startAdvance + dir * 1e-4f;

        int cx = Mathf.FloorToInt(rayOrigin.X);
        int cy = Mathf.FloorToInt(rayOrigin.Y);
        int cz = Mathf.FloorToInt(rayOrigin.Z);

        float dx = dir.X, dy = dir.Y, dz = dir.Z;
        const float Eps = 1e-8f;
        int stepX = dx > Eps ? 1 : (dx < -Eps ? -1 : 0);
        int stepY = dy > Eps ? 1 : (dy < -Eps ? -1 : 0);
        int stepZ = dz > Eps ? 1 : (dz < -Eps ? -1 : 0);

        float tDeltaX = stepX != 0 ? Mathf.Abs(1f / dx) : float.MaxValue;
        float tDeltaY = stepY != 0 ? Mathf.Abs(1f / dy) : float.MaxValue;
        float tDeltaZ = stepZ != 0 ? Mathf.Abs(1f / dz) : float.MaxValue;

        float tMaxX = stepX > 0 ? ((cx + 1) - rayOrigin.X) / dx
            : stepX < 0 ? (rayOrigin.X - cx) / (-dx) : float.MaxValue;
        float tMaxY = stepY > 0 ? ((cy + 1) - rayOrigin.Y) / dy
            : stepY < 0 ? (rayOrigin.Y - cy) / (-dy) : float.MaxValue;
        float tMaxZ = stepZ > 0 ? ((cz + 1) - rayOrigin.Z) / dz
            : stepZ < 0 ? (rayOrigin.Z - cz) / (-dz) : float.MaxValue;

        float maxTravel = Mathf.Max(0f, rayMax - startAdvance);
        var seen = new HashSet<Vector3I>();
        const int maxSteps = 8192;
        float traveled = 0f;

        for (int step = 0; step < maxSteps; step++)
        {
            if (traveled > maxTravel)
                break;

            var cell = new Vector3I(cx, cy, cz);
            if ((new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f) - from).Length() > rayMax + 1f)
                break;

            var tile = map.GetTile(cell);
            if (tile != null && tile.Solid && seen.Add(cell))
                output.Add(cell);

            float tStep = Mathf.Min(tMaxX, Mathf.Min(tMaxY, tMaxZ));
            if (float.IsNaN(tStep) || tStep >= float.MaxValue * 0.5f)
                break;
            if (traveled + tStep > maxTravel)
                break;
            traveled += tStep;

            float tieTol = 1e-5f * (1f + Mathf.Abs(tStep));
            if (tMaxX <= tStep + tieTol)
            {
                tMaxX += tDeltaX;
                cx += stepX;
            }
            if (tMaxY <= tStep + tieTol)
            {
                tMaxY += tDeltaY;
                cy += stepY;
            }
            if (tMaxZ <= tStep + tieTol)
            {
                tMaxZ += tDeltaZ;
                cz += stepZ;
            }
        }
    }
}
