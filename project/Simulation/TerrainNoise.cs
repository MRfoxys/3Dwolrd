using Godot;

/// <summary>
/// Value noise with smooth interpolation for deterministic procedural terrain (caves, tree scatter).
/// </summary>
public static class TerrainNoise
{
    static int Hash(int n)
    {
        unchecked
        {
            n = (n << 13) ^ n;
            return n * (n * n * 15731 + 789221) + 1376312589;
        }
    }

    static float Unit(int h) => (h & 0x7fffff) / (float)0x7fffff;

    public static float Raw(int x, int y, int z, int seed)
    {
        int h = Hash(x + Hash(y + Hash(z + seed)));
        return Unit(h);
    }

    static float SmoothT(float t) => t * t * (3f - 2f * t);

    public static float Sample3D(float worldX, float worldY, float worldZ, int seed)
    {
        int x0 = Mathf.FloorToInt(worldX);
        int y0 = Mathf.FloorToInt(worldY);
        int z0 = Mathf.FloorToInt(worldZ);
        float tx = worldX - x0;
        float ty = worldY - y0;
        float tz = worldZ - z0;

        float ux = SmoothT(tx);
        float uy = SmoothT(ty);
        float uz = SmoothT(tz);

        float v000 = Raw(x0, y0, z0, seed);
        float v100 = Raw(x0 + 1, y0, z0, seed);
        float v010 = Raw(x0, y0 + 1, z0, seed);
        float v110 = Raw(x0 + 1, y0 + 1, z0, seed);
        float v001 = Raw(x0, y0, z0 + 1, seed);
        float v101 = Raw(x0 + 1, y0, z0 + 1, seed);
        float v011 = Raw(x0, y0 + 1, z0 + 1, seed);
        float v111 = Raw(x0 + 1, y0 + 1, z0 + 1, seed);

        float x00 = Mathf.Lerp(v000, v100, ux);
        float x10 = Mathf.Lerp(v010, v110, ux);
        float x01 = Mathf.Lerp(v001, v101, ux);
        float x11 = Mathf.Lerp(v011, v111, ux);

        float y0z = Mathf.Lerp(x00, x10, uy);
        float y1z = Mathf.Lerp(x01, x11, uy);

        return Mathf.Lerp(y0z, y1z, uz);
    }
}
