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

            HashSet<Vector3I> reserved = new();

            foreach (var colon in selected)
            {
                var finalTarget = sim.FindNearestFreeWithReservation(pos, reserved);

                reserved.Add(finalTarget);

                var index = sim.World.CurrentMap.Colonists.IndexOf(colon);

                var cmd = new PlayerCommand
                {
                    Tick = sim.Tick + 1,
                    Type = "MOVE",
                    EntityId = index,
                    X = finalTarget.X,
                    Y = finalTarget.Y,
                    Z = finalTarget.Z
                };

                lockstep.AddCommand(cmd);
                GD.Print("CLICK TARGET: ", finalTarget);
                GD.Print("CLICK GRID: ", pos);
            }
        }
    }

    Vector3I GetMouseWorldPosition()
    {
        var mousePos = camera.GetViewport().GetMousePosition();

        var from = camera.ProjectRayOrigin(mousePos);
        var to = from + camera.ProjectRayNormal(mousePos) * 1000;

        var space = camera.GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);

        var result = space.IntersectRay(query);

        if (result.Count > 0)
        {
            var pos = (Vector3)result["position"];

            int x = Mathf.FloorToInt(pos.X);
            int y = Mathf.FloorToInt(pos.Y);
            int z = Mathf.FloorToInt(pos.Z);

            // 🔥 TOUJOURS viser au-dessus du bloc
            return new Vector3I(x, y + 1, z);
        }

        return Vector3I.Zero;
    }
}