using Godot;
using System.Collections.Generic;

public class Colonist
{
    public int OwnerId;
    public int X;
    public int Y;
    public int Z;

    public Vector3I Position => new Vector3I(X, Y, Z);
    public int WaitTimer = 0;

    public Vector3I Target;
    public int RepathTimer = 0;

    public float MoveProgress = 0f;
    public float MoveSpeed = 3f;

    public bool HasPathFailed = false;
    public List<Vector3I> Path = new();

    public Colonist()
    {
        X= 0;
        Y = 0;
        Z = 0;
        Target = new Vector3I(0, 0, 0);
    }

    public Colonist(int spawnX ,int spawnY, int spawnZ)
    {
        X= spawnX;
        Y = spawnY;
        Z = spawnZ;
        Target = new Vector3I(0, 0, 0);
    }
}