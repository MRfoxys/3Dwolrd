using Godot;

public enum VoxelWorkState
{
    Intact,
    Ghost,
    InProgress,
    Destroyed,
    Built
}

public sealed class VoxelWorkProgress
{
    public Vector3I Position;
    public VoxelWorkState State = VoxelWorkState.Intact;
    public float Current;
    public float Max = 1f;
    public bool IsConstruction;
    public string TargetTileType = "air";

    public float Ratio => Max <= 0.0001f ? 1f : Mathf.Clamp(Current / Max, 0f, 1f);
}

public static class VoxelWorkCatalog
{
    public static float GetMaxHp(string tileType)
    {
        return tileType switch
        {
            "scaffold" => 55f,
            "build_black" => 105f,
            "stone" => 120f,
            "platform" => 95f,
            "ground" => 140f,
            "tree" => 65f,
            "dirt" => 75f,
            _ => 90f
        };
    }

    public static float GetMinePowerPerTick(string tileType)
    {
        return tileType switch
        {
            "scaffold" => 9f,
            "build_black" => 6.5f,
            "stone" => 5f,
            "platform" => 6f,
            "ground" => 4f,
            "tree" => 8f,
            "dirt" => 7f,
            _ => 6f
        };
    }

    public static float GetBuildPowerPerTick(string tileType)
    {
        return tileType switch
        {
            "scaffold" => 9.5f,
            "build_black" => 7f,
            "stone" => 6f,
            "platform" => 7f,
            "ground" => 5f,
            "tree" => 0f,
            "dirt" => 7.5f,
            _ => 6f
        };
    }

    public static Tile GetDestroyedResult(string sourceType)
    {
        if (sourceType == "tree")
            return new Tile { Solid = true, Type = "dirt" };

        return new Tile { Solid = false, Type = "air" };
    }

    public static Tile BuildTile(string type)
    {
        return new Tile
        {
            Type = type,
            Solid = type != "air"
        };
    }
}
