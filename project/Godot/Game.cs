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
    Dictionary<Vector3I, Node3D> spawnedTiles = new();

    int chunkRadius = 2;
    bool debugChunks = false;

    HashSet<Vector3I> currentNeededChunks = new();

    Vector3I lastPlayerChunk = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
    Dictionary<Vector3I, MultiMeshInstance3D> chunkMeshes = new();

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
        spawnedTiles.Clear();
        //SpawnTiles();replace by updatechunks
        UpdateChunksAroundColonists();

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


        UpdateChunksAroundColonists();
        UnloadFarChunks();
        UpdateChunkVisibility();
        

        Render();
        cameraController.Update(delta);
    }

    public override void _Input(InputEvent @event)
    {
        cameraController.HandleInput(@event);
        selectionManager.HandleInput(@event);
        unitController.HandleInput(@event, selectionManager.SelectedColonists);


        // debug tools
        if (@event is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.F9)
            {
                debugChunks = !debugChunks;

                GD.Print("DEBUG CHUNKS: ", debugChunks);

                // 🔥 refresh visuel
                foreach (var pair in spawnedTiles)
                {
                    var pos = pair.Key;
                    var node = pair.Value;

                    var mesh = node.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");

                    if (mesh == null)
                        continue;

                    var mat = mesh.MaterialOverride as StandardMaterial3D;

                    var chunkPos = WorldToChunk(new Vector3(pos.X, pos.Y, pos.Z));

                    var baseColor = GetTileColor(pos);

                    if (debugChunks && chunkPos != Vector3I.Zero)
                        mat.AlbedoColor = baseColor * 0.8f;
                    else
                        mat.AlbedoColor = baseColor;
                }
            }
        }
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

            // 🎯 sélection
            mesh.MaterialOverride =
                selectionManager.SelectedColonists.Contains(colon)
                ? selectedMat
                : defaultMat;

            var tilePos = new Vector3I(colon.X, colon.Y, colon.Z);

            // 👁️ visibilité
            if (colon.OwnerId != 0) // ennemi
            {
                if (!sim.Vision.IsVisible(tilePos))
                {
                    node.Visible = false;
                    continue;
                }
            }

            node.Visible = true;

            // 🎥 interpolation fluide
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
        var chunk = new Chunk();


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

        int radius = 2; // nombre de chunks autour

        for (int cx = -radius; cx <= radius; cx++)
        for (int cz = -radius; cz <= radius; cz++)
        {
            var pos = new Vector3I(cx, 0, cz);

            // ⚠️ skip ton chunk custom
            if (pos == new Vector3I(0, 0, 0))
                continue;

            var generated = map.GenerateFlatChunk(pos);
            map.Chunks[pos] = generated;
        }
        
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

            for (int x = 0; x < Map.CHUNK_SIZE; x++)
            for (int y = 0; y < Map.CHUNK_SIZE; y++)
            for (int z = 0; z < Map.CHUNK_SIZE; z++)
            {
                var tile = chunk.Tiles[x, y, z];

                if (tile == null || !tile.Solid)
                    continue;

                var worldPos = new Vector3I(
                    chunkPos.X * Map.CHUNK_SIZE + x,
                    chunkPos.Y * Map.CHUNK_SIZE + y,
                    chunkPos.Z * Map.CHUNK_SIZE + z
                );

                // 🔥 évite double spawn
                if (spawnedTiles.ContainsKey(worldPos))
                    continue;

                var node = SpawnTile(worldPos, tile);
                spawnedTiles[worldPos] = node;
                
            }
        }
    }

    Node3D SpawnTile(Vector3I pos, Tile tile)
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

        return body;
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

    Vector3I WorldToChunk(Vector3 pos)
    {
        int size = Map.CHUNK_SIZE;

        return new Vector3I(
            Mathf.FloorToInt(pos.X / size),
            Mathf.FloorToInt(pos.Y / size),
            Mathf.FloorToInt(pos.Z / size)
        );
    }

    void UpdateChunksAround(Vector3I playerChunk)
    {
        var map = sim.World.CurrentMap;

        for (int x = -chunkRadius; x <= chunkRadius; x++)
        for (int z = -chunkRadius; z <= chunkRadius; z++)
        {
            var chunkPos = new Vector3I(
                playerChunk.X + x,
                0,
                playerChunk.Z + z
            );

            var chunk = map.GetOrCreateChunk(chunkPos);

            SpawnChunkMesh(chunkPos, chunk);

            // 🔥 spawn debug (optionnel)
            if (debugChunks)
                SpawnChunkDebug(chunkPos, chunk);
        }
    }

    void SpawnChunkMesh(Vector3I chunkPos, Chunk chunk)
    {
        if (chunkMeshes.ContainsKey(chunkPos))
            return;

        // 🧱 MultiMesh setup
        var mm = new MultiMesh();

        mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        mm.UseColors = true;
        mm.InstanceCount = 0; // ⚠️ important (reset clean)

        // 🧱 Mesh + material
        var mesh = new BoxMesh();

        var material = new StandardMaterial3D();
        material.VertexColorUseAsAlbedo = true; // 🔥 utilise SetInstanceColor
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; // perf + lisibilité RTS

        mesh.SurfaceSetMaterial(0, material);

        mm.Mesh = mesh;

        // 🧱 Instance
        var instance = new MultiMeshInstance3D();
        instance.Multimesh = mm;

        List<Transform3D> transforms = new();
        List<Color> colors = new();

        // 📦 Build instances
        for (int x = 0; x < Map.CHUNK_SIZE; x++)
        for (int y = 0; y < Map.CHUNK_SIZE; y++)
        for (int z = 0; z < Map.CHUNK_SIZE; z++)
        {
            var tile = chunk.Tiles[x, y, z];

            if (tile == null || !tile.Solid)
                continue;

            var worldPos = new Vector3(
                chunkPos.X * Map.CHUNK_SIZE + x,
                chunkPos.Y * Map.CHUNK_SIZE + y,
                chunkPos.Z * Map.CHUNK_SIZE + z
            );

            transforms.Add(new Transform3D(Basis.Identity, worldPos));

            // 🎨 couleur de base
            var color = GetTileColor(new Vector3I(
                (int)worldPos.X,
                (int)worldPos.Y,
                (int)worldPos.Z
            ));

            // 🧪 debug chunk
            if (debugChunks && chunkPos != Vector3I.Zero)
                color *= 0.8f;

            colors.Add(color);
        }

        // ⚠️ si chunk vide → skip
        if (transforms.Count == 0)
            return;

        // 📦 Apply instances
        mm.InstanceCount = transforms.Count;

        for (int i = 0; i < transforms.Count; i++)
        {
            mm.SetInstanceTransform(i, transforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }

        // 🧠 FOG OF WAR INIT (évite le "tout gris au start")
        var center = new Vector3I(
            chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
            1,
            chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
        );

        if (!sim.Vision.IsDiscovered(center))
        {
            instance.Visible = false;
        }
        else
        {
            instance.Visible = true;
        }

        AddChild(instance);
        chunkMeshes[chunkPos] = instance;
    }

    Color GetTileColor(Vector3I pos)
    {
        var tile = sim.World.CurrentMap.GetTile(pos);

        return tile.Type switch
        {
            "ground" => new Color(0.4f, 0.25f, 0.1f),
            "platform" => new Color(1.0f, 0.5f, 0.0f),
            "stairs" => new Color(0.8f, 0.8f, 0.2f),
            _ => new Color(1, 1, 1)
        };
    }

    void UnloadFarChunks()
    {
        List<Vector3I> toRemove = new();

        foreach (var pair in chunkMeshes)
        {
            var chunkPos = pair.Key;

            if (!currentNeededChunks.Contains(chunkPos))
            {
                pair.Value.QueueFree();
                toRemove.Add(chunkPos);
            }
        }

        foreach (var pos in toRemove)
            chunkMeshes.Remove(pos);
    }

    void SpawnChunkDebug(Vector3I chunkPos, Chunk chunk)
    {
        for (int x = 0; x < Map.CHUNK_SIZE; x++)
        for (int y = 0; y < Map.CHUNK_SIZE; y++)
        for (int z = 0; z < Map.CHUNK_SIZE; z++)
        {
            var tile = chunk.Tiles[x, y, z];

            if (tile == null || !tile.Solid)
                continue;

            var worldPos = new Vector3I(
                chunkPos.X * Map.CHUNK_SIZE + x,
                chunkPos.Y * Map.CHUNK_SIZE + y,
                chunkPos.Z * Map.CHUNK_SIZE + z
            );

            if (spawnedTiles.ContainsKey(worldPos))
                continue;

            var node = SpawnTile(worldPos, tile);
            spawnedTiles[worldPos] = node;
        }
    }

    void UpdateChunksAroundColonists()
    {
        var map = sim.World.CurrentMap;

        HashSet<Vector3I> neededChunks = new();

        bool useVision = sim.Vision.Visible.Count > 0;

        foreach (var colon in map.Colonists)
        {

            if (colon.OwnerId != localPlayerId)
                continue;

            var colonChunk = WorldToChunk(new Vector3(colon.X, colon.Y, colon.Z));

            for (int x = -chunkRadius; x <= chunkRadius; x++)
            for (int z = -chunkRadius; z <= chunkRadius; z++)
            {
                var chunkPos = new Vector3I
                (
                    colonChunk.X + x,
                    0,
                    colonChunk.Z + z
                );

                // 🔥 centre du chunk
                var chunkCenter = new Vector3I
                (
                    chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
                    1,
                    chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
                );

            if (useVision && !sim.Vision.IsVisible(chunkCenter))
                continue;

                neededChunks.Add(chunkPos);

                var chunk = map.GetOrCreateChunk(chunkPos);

                SpawnChunkMesh(chunkPos, chunk);
            }
        }

        // stock pour unload
        currentNeededChunks = neededChunks;
    }

    bool IsInCameraView(Vector3I pos)
    {
        var worldPos = new Vector3(pos.X, pos.Y, pos.Z);

        var screenPos = camera.UnprojectPosition(worldPos);

        var rect = GetViewport().GetVisibleRect();

        return screenPos.X >= 0 && screenPos.X <= rect.Size.X &&
            screenPos.Y >= 0 && screenPos.Y <= rect.Size.Y;
    }


    bool IsChunkVisible(Vector3I chunkPos)
    {
        var center = new Vector3I(
            chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
            1,
            chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
        );

        bool discovered = sim.Vision.IsDiscovered(center);
        bool visible = sim.Vision.IsVisible(center);
        bool inCamera = IsInCameraView(center);

        if (!discovered)
            return false;

        if (!visible || !inCamera)
            return true; // visible sombre

        return true;
    }

    void UpdateChunkVisibility()
    {
        foreach (var pair in chunkMeshes)
        {
            var chunkPos = pair.Key;
            var instance = pair.Value;

            var center = new Vector3I(
                chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
                1,
                chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
            );

            bool discovered = sim.Vision.IsDiscovered(center);
            bool visible = sim.Vision.IsVisible(center);
            bool inCamera = IsInCameraView(center);

            if (!discovered)
            {
                instance.Visible = false;
                continue;
            }

            instance.Visible = true;

            var mat = instance.MaterialOverride as StandardMaterial3D;

            if (mat == null)
                continue;

            if (!visible || !inCamera)
                mat.AlbedoColor = new Color(0.2f, 0.2f, 0.2f);
            else
                mat.AlbedoColor = new Color(1, 1, 1);
        }
    }

}