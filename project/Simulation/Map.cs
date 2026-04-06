using System.Collections.Generic;
using Godot;

public class Map
{
    public Dictionary<Vector3I, Chunk> Chunks = new();

    public List<Colonist> Colonists = new();

    public int ChunkSize = 16;

    public Tile GetTile(Vector3I pos)
    {
        var chunkPos = new Vector3I
        (
            Mathf.FloorToInt((float)pos.X / ChunkSize),
            Mathf.FloorToInt((float)pos.Y / ChunkSize),
            Mathf.FloorToInt((float)pos.Z / ChunkSize)
        );

        if (!Chunks.TryGetValue(chunkPos, out var chunk))
            return new Tile { Solid = true };

        var local = new Vector3I
        (
            Mod(pos.X, ChunkSize),
            Mod(pos.Y, ChunkSize),
            Mod(pos.Z, ChunkSize)
        );

        var tile = chunk.Tiles[local.X, local.Y, local.Z];

        if (tile == null)
            return new Tile { Solid = false }; // air

        return tile;
    }

    public int Mod(int a, int b) => (a % b + b) % b;
}