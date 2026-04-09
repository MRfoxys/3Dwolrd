using Godot;
using System.Collections.Generic;

public class UnitController
{
    const int MoveMenuId = 1;

    Simulation sim;
    LockstepManager lockstep;
    Camera3D camera;
    Node uiRoot;
    PopupMenu interactionMenu;
    Vector3I pendingTarget = Vector3I.Zero;
    List<Colonist> pendingSelection = new();

    public UnitController(Simulation sim, LockstepManager lockstep, Camera3D camera, Node uiRoot)
    {
        this.sim = sim;
        this.lockstep = lockstep;
        this.camera = camera;
        this.uiRoot = uiRoot;
        BuildInteractionMenu();
    }

    public void HandleInput(InputEvent @event, List<Colonist> selected)
    {
        if (@event is InputEventMouseButton left &&
            left.ButtonIndex == MouseButton.Left &&
            left.Pressed)
        {
            interactionMenu?.Hide();
        }

        if (@event is InputEventMouseButton mouse &&
            mouse.ButtonIndex == MouseButton.Right &&
            mouse.Pressed)
        {
            if (selected == null || selected.Count == 0)
                return;

            pendingTarget = GetMouseWorldPosition();
            pendingSelection = new List<Colonist>(selected);

            interactionMenu.Position = new Vector2I((int)mouse.Position.X, (int)mouse.Position.Y);
            interactionMenu.Popup();
        }
    }

    void BuildInteractionMenu()
    {
        interactionMenu = new PopupMenu();
        interactionMenu.Name = "InteractionMenu";
        interactionMenu.AddItem("Aller vers", MoveMenuId);
        interactionMenu.IdPressed += OnInteractionSelected;
        uiRoot.AddChild(interactionMenu);
    }

    void OnInteractionSelected(long id)
    {
        interactionMenu.Hide();
        if (id != MoveMenuId || pendingSelection == null || pendingSelection.Count == 0)
            return;

        HashSet<Vector3I> reserved = new();

        foreach (var colon in pendingSelection)
        {
            var finalTarget = sim.FindNearestFreeWithReservation(pendingTarget, reserved);

            if (finalTarget == colon.Position)
                continue;

            reserved.Add(finalTarget);

            var index = sim.GetColonistIndex(colon);

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
        }
    }

    Vector3I GetMouseWorldPosition()
    {
        var mousePos = camera.GetViewport().GetMousePosition();

        var from = camera.ProjectRayOrigin(mousePos);
        var dir = camera.ProjectRayNormal(mousePos).Normalized();
        var map = sim.World?.CurrentMap;
        if (map == null)
            return Vector3I.Zero;

        // Grid-raycast against simulation tiles so elevated platforms are always picked,
        // even when rendered with MultiMesh without physics colliders.
        const float maxDistance = 400f;
        const float step = 0.1f;
        Vector3I? previousCell = null;

        for (float t = 0f; t <= maxDistance; t += step)
        {
            var p = from + dir * t;
            var cell = new Vector3I(
                Mathf.FloorToInt(p.X),
                Mathf.FloorToInt(p.Y),
                Mathf.FloorToInt(p.Z)
            );

            if (previousCell.HasValue && previousCell.Value == cell)
                continue;

            var tile = map.GetTile(cell);
            if (tile != null && tile.Solid)
                return previousCell ?? (cell + Vector3I.Up);

            previousCell = cell;
        }

        return Vector3I.Zero;
    }
}