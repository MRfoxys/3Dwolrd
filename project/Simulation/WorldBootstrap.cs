using Godot;

public class WorldBootstrap
{
    public World CreateDefaultWorld(int chunkRadius = 2, int localPlayerId = 0)
    {
        var world = new World();
        var map = new Map();
        var chunk = new Chunk();

        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
            chunk.Tiles[x, 0, z] = new Tile { Solid = true, Type = "ground" };

        for (int x = 4; x < 8; x++)
        for (int z = 4; z < 8; z++)
            chunk.Tiles[x, 2, z] = new Tile { Solid = true, Type = "platform" };

        chunk.Tiles[4, 1, 4] = new Tile { Solid = true, Type = "stairs" };
        chunk.Tiles[9, 1, 9] = new Tile { Solid = true, Type = "tree" };
        map.Chunks[new Vector3I(0, 0, 0)] = chunk;

        for (int cx = -chunkRadius; cx <= chunkRadius; cx++)
        for (int cz = -chunkRadius; cz <= chunkRadius; cz++)
        {
            var pos = new Vector3I(cx, 0, cz);
            if (pos == Vector3I.Zero)
                continue;

            map.Chunks[pos] = map.GenerateFlatChunk(pos);
        }

        for (int i = 0; i < 5; i++)
        {
            map.Colonists.Add(new Colonist
            {
                X = 5 + i,
                Y = 1,
                Z = 2,
                OwnerId = localPlayerId
            });
        }

        world.Maps.Add(map);
        world.CurrentMap = map;
        return world;
    }
}
