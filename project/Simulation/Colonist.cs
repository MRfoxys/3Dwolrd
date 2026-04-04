using Godot;
using System.Collections.Generic;

public class Colonist
{
    public int OwnerId;
    public int X;
    public int Y;
    public int Z;

    public int TargetX;
    public int TargetY;
    public int TargetZ;
    public List<Vector3I> Path = new();

    public Colonist()
    {
        X= 0;
        Y = 0;
        Z = 0;
        TargetX = 0;
        TargetY = 0;
        TargetZ = 0;
    }

        public Colonist(int spawnX ,int spawnY, int spawnZ)
    {
        X= spawnX;
        Y = spawnY;
        Z = spawnZ;
        TargetX = 0;
        TargetY = 0;
        TargetZ = 0;
    }



    public void Update(Map map)
    {
        if (X < TargetX) X++;
        else if (X > TargetX) X--;

        if (Y < TargetY) Y++;
        else if (Y > TargetY) Y--;

        if (Z < TargetZ) Z++;
        else if (Z > TargetZ) Z--;
    }
}