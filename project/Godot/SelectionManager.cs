using Godot;
using System.Collections.Generic;

public class SelectionManager
{
    const float DRAG_THRESHOLD_PIXELS = 12f;
    const float CLICK_SELECT_PIXELS = 26f;
    /// <summary>Demi-largeur monde autour du rayon pour un clic colon (évite de prendre le voisin projeté à côté après rotation caméra).</summary>
    const float COLONIST_RAY_PICK_RADIUS = 1.15f;
    const float COLONIST_RAY_MIN_T = 0.12f;

    Camera3D camera;
    Dictionary<Colonist, Node3D> visuals;
    int localPlayerId;

    public List<Colonist> SelectedColonists = new();
    public WorldSelectionTargetKind TargetKind { get; set; } = WorldSelectionTargetKind.Colonists;

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
        if (TargetKind != WorldSelectionTargetKind.Colonists)
            return;

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
            // Clic simple : d'abord distance au rayon 3D (précis après orbite), puis repli pixels si rien.
            var mouse = camera.GetViewport().GetMousePosition();
            var from = camera.ProjectRayOrigin(mouse);
            var dir = camera.ProjectRayNormal(mouse).Normalized();

            Colonist closestRay = null;
            float bestRayScore = float.MaxValue;
            foreach (var pair in visuals)
            {
                var colon = pair.Key;
                var node = pair.Value;
                if (colon.OwnerId != localPlayerId)
                    continue;

                Vector3 p = node.GlobalPosition;
                float t = (p - from).Dot(dir);
                if (t < COLONIST_RAY_MIN_T)
                    continue;
                Vector3 onRay = from + dir * t;
                float perp = p.DistanceTo(onRay);
                if (perp > COLONIST_RAY_PICK_RADIUS)
                    continue;
                float pix = camera.UnprojectPosition(p).DistanceTo(mouse);
                float score = perp * 120f + pix;
                if (score < bestRayScore)
                {
                    bestRayScore = score;
                    closestRay = colon;
                }
            }

            if (closestRay != null)
            {
                SelectedColonists.Add(closestRay);
            }
            else
            {
                Colonist closest = null;
                float bestPx = CLICK_SELECT_PIXELS;
                foreach (var pair in visuals)
                {
                    var colon = pair.Key;
                    var node = pair.Value;
                    if (colon.OwnerId != localPlayerId)
                        continue;
                    float d = camera.UnprojectPosition(node.GlobalPosition).DistanceTo(mouse);
                    if (d < bestPx)
                    {
                        bestPx = d;
                        closest = colon;
                    }
                }
                if (closest != null)
                    SelectedColonists.Add(closest);
            }
        }

        GD.Print("Selected count: ", SelectedColonists.Count);
    }

    public Rect2 GetScreenRect()
    {
        return new Rect2(start, end - start).Abs();
    }
}