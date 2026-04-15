public sealed class GameSelectionModule
{
    public WorldSelectionTargetKind Next(WorldSelectionTargetKind current)
    {
        return current switch
        {
            WorldSelectionTargetKind.Colonists => WorldSelectionTargetKind.Trees,
            WorldSelectionTargetKind.Trees => WorldSelectionTargetKind.TerrainTiles,
            WorldSelectionTargetKind.TerrainTiles => WorldSelectionTargetKind.BuildBlocks,
            _ => WorldSelectionTargetKind.Colonists
        };
    }

    public bool IsVoxelSelectionMode(WorldSelectionTargetKind kind)
    {
        return kind == WorldSelectionTargetKind.TerrainTiles
            || kind == WorldSelectionTargetKind.BuildBlocks;
    }
}
