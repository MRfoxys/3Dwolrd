using Godot;
using System.Collections.Generic;

public partial class Game : Node3D
{
    Simulation sim = new Simulation();
    LockstepManager lockstep = new LockstepManager();

    CameraController cameraController;
    SelectionManager selectionManager;
    UnitController unitController;

    public Dictionary<Colonist, Node3D> colonVisuals = new();

    PackedScene colonScene = GD.Load<PackedScene>("res://project/Godot/Colon.tscn");

    Node3D cameraPivot;
    Camera3D camera;

    StandardMaterial3D selectedMat;

    StandardMaterial3D defaultMat;

    ColorRect selectionRect;

    int localPlayerId = 0;

    double accumulator = 0;
    const double TICK_RATE = 0.05;

    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = GetNode<Camera3D>("CameraPivot/Camera3D");

        selectedMat = new StandardMaterial3D();
        selectedMat.AlbedoColor = new Color(0, 1, 0);

        defaultMat = new StandardMaterial3D();
        defaultMat.AlbedoColor = new Color(1, 0, 0);

        selectionRect = GetNode<ColorRect>("UI/SelectionRect");

        InitSimulation();
        SpawnVisuals();

        cameraController = new CameraController(cameraPivot, camera);
        selectionManager = new SelectionManager(camera, colonVisuals, localPlayerId);
        unitController = new UnitController(sim, lockstep, camera);
    }

    public override void _Process(double delta)
    {
        if (sim.World == null)
            return;

        accumulator += delta;

        while (accumulator >= TICK_RATE)
        {
            Step();
            accumulator -= TICK_RATE;
        }

        if (selectionManager.IsDragging)
        {
            selectionRect.Visible = true;

            var rect = selectionManager.GetScreenRect();

            selectionRect.Position = rect.Position;
            selectionRect.Size = rect.Size;
        }
        else
        {
            selectionRect.Visible = false;
        }



        Render();
        cameraController.Update(delta);
    }

    public override void _Input(InputEvent @event)
    {
        cameraController.HandleInput(@event);
        selectionManager.HandleInput(@event);
        unitController.HandleInput(@event, selectionManager.SelectedColonists);
    }

    void Step()
    {
        
        var cmds = lockstep.GetCommandsForTick(sim.Tick);

        foreach (var cmd in cmds)
            sim.CommandQueue.Enqueue(cmd);

        sim.Update();
    }

    void Render()
    {
        foreach (var pair in colonVisuals)
        {
            var colon = pair.Key;
            var node = pair.Value;

            var mesh = node.GetNode<MeshInstance3D>("MeshInstance3D");

            if (selectionManager.SelectedColonists.Contains(colon))
                mesh.MaterialOverride = selectedMat;
            else
                mesh.MaterialOverride = defaultMat;

            node.Position = new Vector3(colon.X, colon.Y + 0.5f, colon.Z);
        }
    }

    void SpawnVisuals()
    {
        foreach (var colon in sim.World.CurrentMap.Colonists)
        {
            var instance = colonScene.Instantiate<Node3D>();
            AddChild(instance);

            colonVisuals[colon] = instance;
        }
    }

    void InitSimulation()
    {
        sim.World = new World();

        var map = new Map(20, 20, 20);
        
        for (int i = 0; i < 5; i++)
        {
            var colon = new Colonist
            {
                X = 5 + i * 2,
                Y = 1,
                Z = 5,
                OwnerId = 0
            };

            map.Colonists.Add(colon);
        }

        sim.World.Maps.Add(map);
        sim.World.CurrentMap = map;
        sim.Init();
    }
}