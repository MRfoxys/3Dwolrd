using Godot;
using System;

/// <summary>Raycast physique sur le calque collision des meshes de picking terrain (ConcavePolygon par chunk).</summary>
public static class TerrainPhysicsPicker
{
    public const uint TerrainPickCollisionMask = 1u;

    /// <summary>Si le hit tombe sur une cellule que <paramref name="acceptSolidCell"/> refuse (ex. masquée par la coupe V), le rayon avance.</summary>
    public static bool TryPickCell(
        PhysicsDirectSpaceState3D space,
        Map map,
        Vector3 from,
        Vector3 dirUnit,
        float maxDist,
        uint collisionMask,
        Predicate<Vector3I> acceptSolidCell,
        out Vector3I cell)
    {
        cell = default;
        if (space == null || map == null)
            return false;

        Vector3 rayEnd = from + dirUnit * maxDist;
        const float surfaceNudge = 0.055f;
        const float advance = 0.09f;
        const int maxSteps = 80;

        Vector3 rayStart = from;
        for (int step = 0; step < maxSteps; step++)
        {
            if ((rayStart - from).LengthSquared() > (maxDist + 0.05f) * (maxDist + 0.05f))
                return false;

            var q = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd);
            q.CollisionMask = collisionMask;
            q.CollideWithAreas = false;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0)
                return false;

            Vector3 pos = hit["position"].AsVector3();
            Vector3 n = hit["normal"].AsVector3();
            if (n.LengthSquared() < 1e-12f)
            {
                rayStart = pos + dirUnit * advance;
                continue;
            }

            n = n.Normalized();
            // Voxels centrés sur des entiers : volume [i-0.5, i+0.5]³. Floor(pos - n*nudge) donne la mauvaise
            // cellule sur les faces -X / -Y / -Z (côtés et dessous) ; on prend la cellule au plus proche.
            Vector3 inside = pos - n * surfaceNudge;
            cell = new Vector3I(
                Mathf.FloorToInt(inside.X + 0.5f),
                Mathf.FloorToInt(inside.Y + 0.5f),
                Mathf.FloorToInt(inside.Z + 0.5f));

            var tile = map.GetTile(cell);
            if (tile == null || !tile.Solid)
            {
                rayStart = pos + dirUnit * advance;
                continue;
            }

            if (acceptSolidCell(cell))
                return true;

            rayStart = pos + dirUnit * advance;
        }

        return false;
    }
}
