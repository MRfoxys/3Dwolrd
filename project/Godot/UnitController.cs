using Godot;
using System.Collections.Generic;

public class UnitController
{
    Simulation sim;
    LockstepManager lockstep;
    Camera3D camera;

    public UnitController(Simulation sim, LockstepManager lockstep, Camera3D camera)
    {
        this.sim = sim;
        this.lockstep = lockstep;
        this.camera = camera;
    }

    public void HandleInput(InputEvent @event, List<Colonist> selected)
    {
        if (@event is InputEventMouseButton mouse &&
            mouse.ButtonIndex == MouseButton.Right &&
            mouse.Pressed)
        {
            var pos = GetMouseWorldPosition();

            int i = 0;

            foreach (var colon in selected)
            {
                int offsetX = i % 3;
                int offsetY = i / 3;

                var cmd = new PlayerCommand
                {
                    Tick = sim.Tick + 1,
                    Type = "MOVE",
                    EntityId = sim.World.CurrentMap.Colonists.IndexOf(colon),
                    X = (int)pos.X,
                    Y = 0, // sol
                    Z = (int)pos.Z
                };

                lockstep.AddCommand(cmd);
                i++;
            }
        }
    }

    Vector3 GetMouseWorldPosition()
    {
        var mousePos = camera.GetViewport().GetMousePosition();

        var from = camera.ProjectRayOrigin(mousePos);
        var to = from + camera.ProjectRayNormal(mousePos) * 1000;

        var space = camera.GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);

        var result = space.IntersectRay(query);

        if (result.Count > 0)
            return (Vector3)result["position"];

        return Vector3.Zero;
    }
}