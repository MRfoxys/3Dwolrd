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
        SpawnTiles();

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

        UpdateSelectionRect();

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
            var targetPos = new Vector3(colon.X, colon.Y, colon.Z);

            var mesh = node.GetNode<MeshInstance3D>("MeshInstance3D");

                mesh.MaterialOverride =
                selectionManager.SelectedColonists.Contains(colon)
                ? selectedMat
                : defaultMat;

            node.Position = node.Position.Lerp(targetPos, 0.2f);
        }
    }

    void SpawnVisuals()
    {
        foreach (var colon in sim.World.CurrentMap.Colonists)
        {
            var instance = colonScene.Instantiate<Node3D>();
            AddChild(instance);
            
            instance.Position = new Vector3(colon.X, colon.Y, colon.Z);

            colonVisuals[colon] = instance;
        }
    }

    void InitSimulation()
    {
        sim.World = new World();

        var map = new Map();
        var chunk = new Chunk(16);


        // sol
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            chunk.Tiles[x, 0, z] = new Tile { Solid = true, Type = "ground" };
        }

        // plateforme
        for (int x = 4; x < 8; x++)
        for (int z = 4; z < 8; z++)
        {
            chunk.Tiles[x, 2, z] = new Tile { Solid = true, Type = "platform" };
        }

        // escalier simple
        chunk.Tiles[4, 1, 4] = new Tile { Solid = true, Type = "stairs" };

        map.Chunks[new Vector3I(0, 0, 0)] = chunk;
        
        // colons
        for (int i = 0; i < 5; i++)
        {
            var colon = new Colonist
            {
                X = 5 + i,
                Y = 1,
                Z = 2,
                OwnerId = 0
            };

            map.Colonists.Add(colon);
        }

        sim.World.Maps.Add(map);
        sim.World.CurrentMap = map;

        sim.Init();
    }

    void SpawnTiles()
    {
        var map = sim.World.CurrentMap;

        foreach (var chunkPair in map.Chunks)
        {
            var chunkPos = chunkPair.Key;
            var chunk = chunkPair.Value;

            int size = map.ChunkSize;

            for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            {
                var tile = chunk.Tiles[x, y, z];

                if (tile == null || !tile.Solid)
                    continue;

                var worldPos = new Vector3I(
                    chunkPos.X * size + x,
                    chunkPos.Y * size + y,
                    chunkPos.Z * size + z
                );

                SpawnTile(worldPos, tile);
            }
        }
    }

        void SpawnTile(Vector3I pos, Tile tile)
    {
        var body = new StaticBody3D();

        var mesh = new MeshInstance3D();
        mesh.Mesh = new BoxMesh();

        var collision = new CollisionShape3D();
        collision.Shape = new BoxShape3D();

        body.AddChild(mesh);
        body.AddChild(collision);

        body.Position = new Vector3(pos.X, pos.Y, pos.Z);

        var mat = new StandardMaterial3D();

        switch (tile.Type)
        {
            case "ground":
                mat.AlbedoColor = new Color(0.4f, 0.25f, 0.1f);
                break;

            case "platform":
                mat.AlbedoColor = new Color(1.0f, 0.5f, 0.0f);
                break;

            case "stairs":
                mat.AlbedoColor = new Color(0.8f, 0.8f, 0.2f);
                break;

            default:
                mat.AlbedoColor = new Color(1, 1, 1);
                break;
        }

        mesh.MaterialOverride = mat;

        AddChild(body);
    }

    void UpdateSelectionRect()
    {
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
    }
    
}