using Godot;
using System.Collections.Generic;

public class Chunk
{
    public Tile[,,] Tiles;

    public Chunk(int size)
    {
        Tiles = new Tile[size, size, size];
    }
}