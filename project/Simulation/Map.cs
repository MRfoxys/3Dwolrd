using System.Collections.Generic;
using Godot;

public class Map
{
    public Dictionary<Vector3I, Chunk> Chunks = new();
    public List<Colonist> Colonists = new();

    public const int CHUNK_SIZE = 16;

    // =========================
    // 🌍 TILE ACCESS
    // =========================
    public Tile GetTile(Vector3I worldPos)
    {
        var chunk = GetChunk(worldPos);
        var local = WorldToLocal(worldPos);

        var tile = chunk.Tiles[local.X, local.Y, local.Z];

        // 🔥 IMPORTANT : jamais null
        if (tile == null)
            return new Tile { Solid = false }; // air

        return tile;
    }

    public void SetTile(Vector3I worldPos, Tile tile)
    {
        var chunk = GetChunk(worldPos);
        var local = WorldToLocal(worldPos);
        chunk.Tiles[local.X, local.Y, local.Z] = tile;
    }

    public bool HasTile(Vector3I pos)
    {
        return GetTile(pos) != null;
    }

    // =========================
    // 📦 CHUNK MANAGEMENT
    // =========================
    public Chunk GetChunk(Vector3I worldPos)
    {
        var chunkPos = WorldToChunk(worldPos);

        if (!Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk = GenerateChunk(chunkPos);
            Chunks[chunkPos] = chunk;
        }

        return chunk;
    }

    // =========================
    // 🔄 COORD CONVERSIONS
    // =========================
    public Vector3I WorldToChunk(Vector3I pos)
    {
        return new Vector3I(
            Mathf.FloorToInt((float)pos.X / CHUNK_SIZE),
            Mathf.FloorToInt((float)pos.Y / CHUNK_SIZE),
            Mathf.FloorToInt((float)pos.Z / CHUNK_SIZE)
        );
    }

    public Vector3I WorldToLocal(Vector3I pos)
    {
        return new Vector3I(
            Mod(pos.X, CHUNK_SIZE),
            Mod(pos.Y, CHUNK_SIZE),
            Mod(pos.Z, CHUNK_SIZE)
        );
    }

    public int Mod(int a, int b)
    {
        return (a % b + b) % b;
    }

    // =========================
    // 🌍 GENERATION
    // =========================
    Chunk GenerateChunk(Vector3I chunkPos)
    {
        var chunk = new Chunk();
        chunk.ChunkPos = chunkPos;
        chunk.Tiles = new Tile[CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE];

        for (int x = 0; x < CHUNK_SIZE; x++)
        for (int y = 0; y < CHUNK_SIZE; y++)
        for (int z = 0; z < CHUNK_SIZE; z++)
        {
            int worldY = chunkPos.Y * CHUNK_SIZE + y;

            var tile = new Tile();

            // 🌱 sol de base
            if (worldY == 0)
            {
                tile.Solid = true;
                tile.Type = "ground";
            }

            // 🟧 plateforme test
            if (worldY == 3 && x > 4 && x < 10 && z > 4 && z < 10)
            {
                tile.Solid = true;
                tile.Type = "platform";
            }

            // 🟨 escalier simple
            if (worldY == 1 && x == 6 && z == 6)
            {
                tile.Solid = true;
                tile.Type = "stairs";
            }

            chunk.Tiles[x, y, z] = tile;
        }

        return chunk;
    }

    public Chunk GenerateFlatChunk(Vector3I chunkPos)
    {
        var chunk = new Chunk();

        for (int x = 0; x < chunk.GetChunkSize(); x++)
        for (int y = 0; y < chunk.GetChunkSize(); y++)
        for (int z = 0; z < chunk.GetChunkSize(); z++)
        {
            int worldY = chunkPos.Y * chunk.GetChunkSize() + y;

            if (worldY == 0)
            {
                chunk.Tiles[x,y,z] = new Tile
                {
                    Solid = true,
                    Type = "ground"
                };
            }
            else
            {
                chunk.Tiles[x,y,z] = new Tile
                {
                    Solid = false,
                    Type = "air"
                };
            }
        }

        return chunk;
    }

    // =========================
    // 📏 DEBUG SIZE
    // =========================
    public Vector3I GetSize()
    {
        return new Vector3I(1000, 1000, 1000); // virtuel (monde infini)
    }

    public Chunk GetOrCreateChunk(Vector3I chunkPos)
{
    if (!Chunks.TryGetValue(chunkPos, out var chunk))
    {
        chunk = GenerateFlatChunk(chunkPos);
        Chunks[chunkPos] = chunk;
    }

    return chunk;
}
}