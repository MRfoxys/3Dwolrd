using System.Collections.Generic;
using Godot;

public partial class World : Node3D
{
    public List<Map> Maps = new();
    public Map CurrentMap;

    public PhysicsDirectSpaceState3D PhysicsSpaceState { get; private set; }

    public override void _Ready()
    {
        var root = GetTree().Root.GetChild<Node3D>(0);
        PhysicsSpaceState = root.GetWorld3D().DirectSpaceState;
    }

}