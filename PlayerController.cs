using Godot;
using System;
using System.Collections.Generic;

public partial class PlayerController : Node
{
    private Simulation _simulation;
    private SelectionManager _selectionManager;
    private Control _ui;
    private Camera3D _camera;

    // Sélection d'arbres (basée sur CurrentMap)
    private HashSet<Vector3I> _selectedTreePositions = new();
    private bool _isSelectingTreesMode = false;

    // Menu contextuel
    private PopupMenu _actionMenu;

    public void SetupReferences(Simulation simulation, SelectionManager selectionManager, Control ui, Camera3D camera)
    {
        _simulation = simulation;
        _selectionManager = selectionManager;
        _ui = ui;
        _camera = camera;
        _actionMenu = _ui.GetNode<PopupMenu>("ActionMenu");
        _actionMenu.IdPressed += OnActionMenuSelected;
    }

    // --- Gestion du mode sélection d'arbres ---
    public void ToggleTreeSelectionMode()
    {
        _isSelectingTreesMode = !_isSelectingTreesMode;
        GD.Print($"Mode sélection d'arbres : {_isSelectingTreesMode}");
    }

    public void HandleTreeSelection(Vector2 mousePosition)
    {
        if (!_isSelectingTreesMode || _simulation.World?.CurrentMap == null || _camera == null)
            return;

        // 1. Détecter l'arbre sous le curseur avec Godot natif
        var spaceState = _simulation.World.PhysicsSpaceState;
        var from = _camera.ProjectRayOrigin(mousePosition);
        var to = from + _camera.ProjectRayNormal(mousePosition) * 10000;

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollideWithAreas = true;

        var result = spaceState.IntersectRay(query);

        if (result.Count > 0)
        {
            // 2. Convertir la position 3D en coordonnées de CurrentMap
            var globalPos = (Vector3)result["position"];
            var tilePos = new Vector3I(
                Mathf.FloorToInt(globalPos.X),
                Mathf.FloorToInt(globalPos.Y),
                Mathf.FloorToInt(globalPos.Z)
            );

            // 3. Vérifier si c'est un arbre dans CurrentMap
            var tile = _simulation.World.CurrentMap.GetTile(tilePos);
            if (tile != null && tile.Type == "tree")
            {
                ToggleTreeSelection(tilePos);
            }
        }
    }

    private void ToggleTreeSelection(Vector3I position)
    {
        if (_selectedTreePositions.Contains(position))
        {
            _selectedTreePositions.Remove(position);
            GD.Print($"Arbre désélectionné à {position}");
        }
        else
        {
            _selectedTreePositions.Add(position);
            GD.Print($"Arbre sélectionné à {position}");
        }
    }

    // --- Gestion des commandes ---
    private void OnActionMenuSelected(long id)
    {
        var actionMenu = _ui.GetNode<PopupMenu>("ActionMenu");
        actionMenu.Visible = false;

        string selectedAction = id switch
        {
            0 => "GoTo",
            1 => "CutSelectedTrees",
            2 => "Build",
            _ => null
        };

        if (selectedAction == null)
            return;

        switch (selectedAction)
        {
            case "GoTo":
                AssignGoToJobs();
                break;

            case "CutSelectedTrees":
                AssignCutTreeJobs();
                break;

            case "Build":
                // Logique pour construire
                break;
        }
    }

    private void AssignGoToJobs()
    {
        var selectedColonists = _selectionManager.SelectedColonists;
        if (selectedColonists.Count == 0)
        {
            GD.Print("Aucun colon sélectionné !");
            return;
        }

        // Logique existante pour "Aller à" (à adapter)
        // ...
    }

    private void AssignCutTreeJobs()
    {
        if (_selectedTreePositions.Count == 0)
        {
            GD.Print("Aucun arbre sélectionné !");
            return;
        }

        var selectedColonists = _selectionManager.SelectedColonists;
        if (selectedColonists.Count == 0)
        {
            GD.Print("Aucun colon sélectionné !");
            return;
        }

        foreach (var colon in selectedColonists)
        {
            Vector3I? closestTree = FindClosestTree(colon.Position);
            if (closestTree != null)
            {
                var job = new SimJob
                {
                    Type = JobType.CutTree,       // Type de job
                    Priority = JobPriority.Normal, // Priorité normale
                    Target = closestTree.Value,    // Position de l'arbre
                    WorkPosition = closestTree.Value // Position où le colon doit travailler
                };

                _simulation.jobBoard.AddJob(job);

                // ✅ Assigner le job au colon
                colon.ActiveJob = job;
                colon.WorkTicksRemaining = 10;
                GD.Print($"[PlayerController] Job assigné : Colon {colon.OwnerId} coupe l'arbre à {closestTree.Value}");
            }
        }

        _selectedTreePositions.Clear();
    }

    private Vector3I? FindClosestTree(Vector3 colonPosition)
    {
        Vector3I? closestTree = null;
        float closestDistance = float.MaxValue;

        foreach (var treePos in _selectedTreePositions)
        {
            var distance = colonPosition.DistanceTo(new Vector3(treePos.X, treePos.Y, treePos.Z));
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestTree = treePos;
            }
        }

        return closestTree;
    }

    // --- Gestion des inputs ---
    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            if (keyEvent.Keycode == Key.T)
                ToggleTreeSelectionMode();
        }

        if (_isSelectingTreesMode && @event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
        {
            HandleTreeSelection(mouseEvent.Position);
        }
    }

    public HashSet<Vector3I> GetSelectedTreePositions()
    {
        return _selectedTreePositions;
    }

    // Méthode publique pour effacer la sélection
    public void ClearTreeSelection()
    {
        _selectedTreePositions.Clear();
        GD.Print("[PlayerController] Sélection d'arbres effacée.");
    }

    public void CreateCutTreeJob(Vector3I treePosition)
    {
        var job = new SimJob
        {
            Type = JobType.CutTree,
            Priority = JobPriority.Normal,
            Target = treePosition,
            WorkPosition = treePosition
        };

        _simulation.jobBoard.AddJob(job);
        GD.Print($"[PlayerController] Job d'abattage créé pour l'arbre à {treePosition}.");
    }
}