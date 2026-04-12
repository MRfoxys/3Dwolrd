using Godot;
using System.Collections.Generic;

public partial class PlayerCommands : Node
{
    private Simulation _simulation;
    private PlayerController _playerController;
    private SelectionManager _selectionManager;

    public PlayerCommands(Simulation simulation, PlayerController playerController, SelectionManager selectionManager)
    {
        _simulation = simulation;
        _playerController = playerController;
        _selectionManager = selectionManager;
    }

    // Exécuter une commande en fonction de l'action sélectionnée
    public void ExecuteCommand(string command)
    {
        switch (command)
        {
            case "CutSelectedTrees":
                CutSelectedTrees();
                break;

            case "GoTo":
                // Logique existante pour "Aller à"
                break;

            case "Build":
                // Logique pour construire
                break;
        }
    }

    private void CutSelectedTrees()
    {
        // Récupérer les arbres sélectionnés via une méthode publique
        var selectedTreePositions = _playerController.GetSelectedTreePositions();
        if (selectedTreePositions.Count == 0)
        {
            GD.Print("[PlayerCommands] Aucun arbre sélectionné !");
            return;
        }

        var selectedColonists = _selectionManager.SelectedColonists;
        if (selectedColonists.Count == 0)
        {
            GD.Print("[PlayerCommands] Aucun colon sélectionné !");
            return;
        }

        foreach (var colon in selectedColonists)
        {
            Vector3I? closestTree = FindClosestTree(colon.Position, selectedTreePositions);

            if (closestTree == null)
            {
                GD.Print($"[PlayerCommands] Aucun arbre trouvé pour le colon {colon.OwnerId}.");
                continue;
            }

            var tile = _simulation.World.CurrentMap.GetTile(closestTree.Value);
            if (tile == null || tile.Type != "tree")
            {
                GD.Print($"[PlayerCommands] L'arbre à {closestTree.Value} n'existe plus ou n'est plus un arbre.");
                continue;
            }

            // Créer un SimJob avec les bonnes propriétés
            var job = new SimJob
            {
                Type = JobType.CutTree,
                Priority = JobPriority.Normal, // Priorité par défaut
                Target = closestTree.Value,    // Position de l'arbre
                WorkPosition = closestTree.Value // Position où le colon doit travailler
            };

            // Ajouter le job au JobBoard
            _simulation.jobBoard.AddJob(job);

            // Assigner le job au colon
            colon.ActiveJob = job;
            colon.WorkTicksRemaining = 10; // Temps pour couper un arbre

            GD.Print($"[PlayerCommands] Job assigné : Colon {colon.OwnerId} coupe l'arbre à {closestTree.Value}");
        }

        _playerController.ClearTreeSelection();
    }

    private Vector3I? FindClosestTree(Vector3 colonPosition, HashSet<Vector3I> treePositions)
    {
        Vector3I? closest = null;
        float minDistance = float.MaxValue;

        foreach (var treePos in treePositions)
        {
            var tile = _simulation.World.CurrentMap.GetTile(treePos);
            if (tile == null || tile.Type != "tree")
                continue;

            float distance = colonPosition.DistanceTo(new Vector3(treePos.X, treePos.Y, treePos.Z));
            if (distance < minDistance)
            {
                minDistance = distance;
                closest = treePos;
            }
        }

        return closest;
    }

    
}