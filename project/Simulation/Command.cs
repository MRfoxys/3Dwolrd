using System.Collections.Generic;
using Godot;

public static class PlayerCommandType
{
    public const string Move = "MOVE";
    public const string DesignateBuild = "DESIGNATE_BUILD";
    public const string DesignateMine = "DESIGNATE_MINE";
    public const string DesignateCutTree = "DESIGNATE_CUT_TREE";
}

public sealed class PlayerCommandCell
{
    public int X;
    public int Y;
    public int Z;

    public PlayerCommandCell() { }

    public PlayerCommandCell(Vector3I worldCell)
    {
        X = worldCell.X;
        Y = worldCell.Y;
        Z = worldCell.Z;
    }

    public Vector3I ToVector3I() => new(X, Y, Z);
}

public class PlayerCommand
{
    public long Tick;
    public string Type = string.Empty;
    public int PlayerId;
    public int Sequence;
    public int EntityId;
    public int X;
    public int Y;
    public int Z;
    public int Priority = (int)JobPriority.Normal;
    public string BuildTileType = "stone";
    public List<PlayerCommandCell> Cells = new();
}