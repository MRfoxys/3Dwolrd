using Godot;
using System;
using System.Collections.Generic;

public partial class MapRenderer : Node
{
    private Dictionary<string, PackedScene> _resourceScenes = new();

    public override void _Ready()
    {
        // Charger les scènes de ressources
        LoadResourceScene("tree", "res://Tree.tscn");
        LoadResourceScene("stone", "res://Stone.tscn");
        LoadResourceScene("grass", "res://Grass.tscn");
    }

    private void LoadResourceScene(string resourceType, string scenePath)
    {
        var scene = GD.Load<PackedScene>(scenePath);
        if (scene != null)
        {
            _resourceScenes[resourceType] = scene;
            GD.Print($"[MapRenderer] Scène chargée : {resourceType}");
        }
        else
        {
            GD.PrintErr($"[MapRenderer] Impossible de charger {scenePath}");
        }
    }

    public Node3D InstantiateResource(string resourceType, Vector3I worldPos)
    {
        if (_resourceScenes.TryGetValue(resourceType, out var scene))
        {
            var instance = scene.Instantiate<Node3D>();
            instance.Position = new Vector3(worldPos.X, worldPos.Y, worldPos.Z);
            return instance;
        }

        GD.PrintErr($"[MapRenderer] Ressource inconnue : {resourceType}");
        return null;
    }
}