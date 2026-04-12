using Godot;
using System.Collections.Generic;

/// <summary>Case d’air (ancre déplacement) depuis la même visée que le terrain : coupe V, masques, Q/Alt.</summary>
public delegate bool TryPickMoveAnchorDelegate(out Vector3I anchorAirCell);

public class UnitController
{
    const int MoveMenuId = 1;

    Simulation sim;
    LockstepManager lockstep;
    Camera3D camera;
    Node uiRoot;
    readonly TryPickMoveAnchorDelegate tryPickMoveAnchor;
    PopupMenu interactionMenu;
    Vector3I pendingTarget = Vector3I.Zero;
    bool pendingMoveAnchorValid;
    List<Colonist> pendingSelection = new();

    public UnitController(
        Simulation sim,
        LockstepManager lockstep,
        Camera3D camera,
        Node uiRoot,
        TryPickMoveAnchorDelegate tryPickMoveAnchor)
    {
        this.sim = sim;
        this.lockstep = lockstep;
        this.camera = camera;
        this.uiRoot = uiRoot;
        this.tryPickMoveAnchor = tryPickMoveAnchor;
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

            pendingMoveAnchorValid = tryPickMoveAnchor(out Vector3I a);
            pendingTarget = pendingMoveAnchorValid ? a : Vector3I.Zero;
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
        if (!pendingMoveAnchorValid)
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

}