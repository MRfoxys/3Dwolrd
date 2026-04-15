using System.Collections.Generic;
using Godot;

public sealed class Int3SaveData
{
    public int X;
    public int Y;
    public int Z;

    public Int3SaveData() { }

    public Int3SaveData(Vector3I v)
    {
        X = v.X;
        Y = v.Y;
        Z = v.Z;
    }

    public Vector3I ToVector3I() => new(X, Y, Z);
}

public sealed class ColonistSaveData
{
    public int OwnerId;
    public Int3SaveData Position = new();
    public Int3SaveData Target = new();
}

public sealed class TileSaveData
{
    public Int3SaveData Position = new();
    public string Type = "air";
    public bool Solid;
}

public sealed class JobSaveData
{
    public int Type;
    public int Priority;
    public int Status;
    public Int3SaveData Target = new();
    public Int3SaveData WorkPosition = new();
    public int RetryAfterTick;
    public int EnqueueOrder;
    public string BuildTileType = "stone";
    public int BuildSiteId;
    public string ResourceType = string.Empty;
    public int ResourceAmount;
    public Int3SaveData DropoffTarget = new();
}

public sealed class DesignationSaveData
{
    public int Type;
    public Int3SaveData Target = new();
    public int Priority;
    public string BuildTileType = "stone";
    public bool Planned;
}

public sealed class ResourceSaveData
{
    public Int3SaveData Position = new();
    public string ResourceType = string.Empty;
    public int Amount;
}

public sealed class SimulationSaveData
{
    public int Tick;
    public List<ColonistSaveData> Colonists = new();
    public List<TileSaveData> Tiles = new();
    public List<JobSaveData> Jobs = new();
    public List<DesignationSaveData> Designations = new();
    public List<ResourceSaveData> LooseResources = new();
    public Dictionary<string, int> StockpileInventory = new();
    public List<Int3SaveData> StockpileCells = new();
}
