using System.Collections.Generic;
using Godot;

public class PlayerVision
{
    public HashSet<Vector3I> Visible = new();
    public HashSet<Vector3I> Discovered = new();

    public void ClearVisible()
    {
        Visible.Clear();
    }

    public void AddVisible(Vector3I pos)
    {
        Visible.Add(pos);
        Discovered.Add(pos);
    }

    public bool IsVisible(Vector3I pos)
    {
        return Visible.Contains(pos);
    }

    public bool IsDiscovered(Vector3I pos)
    {
        return Discovered.Contains(pos);
    }
}