public static class TerrainMineRules
{
    public static bool IsMineableBlock(Tile t) =>
        t != null && t.Solid && (t.Type == "stone" || t.Type == "platform");
}
