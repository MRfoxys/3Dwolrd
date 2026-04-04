using System.Collections.Generic;
public class Map
{    public Tile[,,] World;

    public int DefaultSizeX = 100;
    public int DefaultSizeY = 10; // hauteur
    public int DefaultSizeZ = 100;
    public List<Colonist> Colonists = new();

    public void Update()
    {
        foreach (var colon in Colonists)
            colon.Update(this);
    }

    public Map()
    {
        World = new Tile[DefaultSizeX, DefaultSizeY, DefaultSizeZ];

        for (int x = 0; x < DefaultSizeX; x++)
        for (int y = 0; y < DefaultSizeY; y++)
        for (int z = 0; z < DefaultSizeZ; z++)
        {
            World[x,y,z] = new Tile();

            // sol simple
            if (y == 0)
                World[x,y,z].Solid = false;
        }
    }

    public Map(int sizeX, int sizeY, int sizeZ)
    {
        World = new Tile[sizeX, sizeY, sizeZ];

        for (int x = 0; x < sizeX; x++)
        for (int y = 0; y < sizeY; y++)
        for (int z = 0; z < sizeZ; z++)
        {
            World[x,y,z] = new Tile();

            // sol simple
            if (y == 0)
                World[x,y,z].Solid = false;
        }
    }

}