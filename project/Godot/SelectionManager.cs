using Godot;
using System.Collections.Generic;

public class SelectionManager
{
    const float DRAG_THRESHOLD_PIXELS = 12f;

    Camera3D camera;
    Dictionary<Colonist, Node3D> visuals;
    int localPlayerId;

    public List<Colonist> SelectedColonists = new();
    bool leftPressed = false;
    bool dragging = false;
    public bool IsDragging => dragging && leftPressed;
    Vector2 start;
    Vector2 end;

    public SelectionManager(Camera3D camera, Dictionary<Colonist, Node3D> visuals, int localPlayerId)
    {
        this.camera = camera;
        this.visuals = visuals;
        this.localPlayerId = localPlayerId;
    }

    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Left)
            {
                if (mouse.Pressed)
                {
                    leftPressed = true;
                    dragging = false;
                    start = mouse.Position;
                    end = mouse.Position;
                }
                else
                {
                    leftPressed = false;
                    end = mouse.Position;

                    Select();
                    dragging = false;
                }
            }
        }

        if (@event is InputEventMouseMotion motion && leftPressed)
        {
            end = motion.Position;
            if (!dragging && start.DistanceTo(end) >= DRAG_THRESHOLD_PIXELS)
                dragging = true;
        }
    }

    void Select()
    {
        SelectedColonists.Clear();

        var rect = new Rect2(start, end - start).Abs();

        // 🟦 DRAG
        if (dragging)
        {
            foreach (var pair in visuals)
            {
                var colon = pair.Key;
                var node = pair.Value;

                if (colon.OwnerId != localPlayerId)
                    continue;

                var screenPos = camera.UnprojectPosition(node.GlobalPosition);

                if (rect.HasPoint(screenPos))
                    SelectedColonists.Add(colon);
            }
        }
        else
        {
            // 🔥 CLICK → raycast monde
            Vector3 worldPos = GetMouseGridPosition();

            Colonist closest = null;
            float bestDist = 1.2f;

            foreach (var pair in visuals)
            {
                var colon = pair.Key;
                var node = pair.Value;

                if (colon.OwnerId != localPlayerId)
                    continue;

                float dist = node.GlobalPosition.DistanceTo(worldPos);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    closest = colon;
                }
            }

            if (closest != null)
                SelectedColonists.Add(closest);
        }

        GD.Print("Selected count: ", SelectedColonists.Count);
    }

    Vector3I GetMouseGridPosition()
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
            var normal = (Vector3)result["normal"];

            int x = Mathf.FloorToInt(pos.X);
            int y = Mathf.FloorToInt(pos.Y);
            int z = Mathf.FloorToInt(pos.Z);

            // clic sur surface → on va au dessus
            if (normal.Y > 0.5f)
                y += 1;

            return new Vector3I(x, y, z);
        }


        return new Vector3I(0, 0, 0);
    }

    public Rect2 GetScreenRect()
    {
        return new Rect2(start, end - start).Abs();
    }
}