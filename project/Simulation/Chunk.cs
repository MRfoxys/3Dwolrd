using Godot;
using System.Collections.Generic;

public class Chunk
{
    public Tile[,,] Tiles { get; set; } = new Tile[SIZE, SIZE, SIZE];
    public const int SIZE = 16;

    public Vector3I ChunkPos { get; set; }

    Vector3I WorldToChunk(Vector3I pos)
    {
        return new Vector3I
        (
            Mod(pos.X, Chunk.SIZE),
            Mod(pos.Y, Chunk.SIZE),
            Mod(pos.Z, Chunk.SIZE)
        );
    }

    int Mod(int a, int b) => (a % b + b) % b;

    public int GetChunkSize() => Chunk.SIZE;
    

}