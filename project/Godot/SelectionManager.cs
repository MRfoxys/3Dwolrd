using Godot;
using System.Collections.Generic;

public class SelectionManager
{
    Camera3D camera;
    Dictionary<Colonist, Node3D> visuals;
    int localPlayerId;

    public List<Colonist> SelectedColonists = new();
    bool dragging = false;
    public bool IsDragging => dragging;
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
                    dragging = true;
                    start = mouse.Position;
                }
                else
                {
                    dragging = false;
                    end = mouse.Position;

                    Select();
                }
            }
        }

        if (@event is InputEventMouseMotion motion && dragging)
        {
            end = motion.Position;
        }
    }

    void Select()
    {
        SelectedColonists.Clear();

        var rect = new Rect2(start, end - start).Abs();

        // 🟦 DRAG
        if (rect.Size.Length() > 10)
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
            Vector3 worldPos = GetMouseWorldPosition();

            Colonist closest = null;
            float bestDist = 2f;

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

    public Rect2 GetScreenRect()
    {
        return new Rect2(start, end - start).Abs();
    }
}