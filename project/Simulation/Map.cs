using System.Collections.Generic;
using System;
using Godot;

public class Map
{
    public Dictionary<Vector3I, Chunk> Chunks = new();
    public List<Colonist> Colonists = new();
    readonly HashSet<Vector3I> _modifiedTiles = new();

    public const int CHUNK_SIZE = 16;

    /// <summary>Couche sol (plateforme / tronc d'arbre). Le monde « bas » reste y=0 (bedrock + grottes).</summary>
    public const int WorldFloorY = 11;

    /// <summary>Couche d'air où les colons se déplacent (au-dessus du sol solide en y=11).</summary>
    public const int ColonistWalkY = 12;

    public int WorldSeed { get; }

    // =========================
    // 🌍 TILE ACCESS
    // =========================
    public Tile GetTile(Vector3I worldPos)
    {
        var chunkPos = new Vector3I(
            Mathf.FloorToInt((float)worldPos.X / CHUNK_SIZE),
            Mathf.FloorToInt((float)worldPos.Y / CHUNK_SIZE),
            Mathf.FloorToInt((float)worldPos.Z / CHUNK_SIZE)
        );

        var chunk = GetOrCreateChunk(chunkPos);
        if (chunk == null)
        {
            GD.PrintErr($"[Map] Chunk introuvable à {chunkPos}");
            return null;
        }

        int localX = worldPos.X % CHUNK_SIZE;
        int localY = worldPos.Y % CHUNK_SIZE;
        int localZ = worldPos.Z % CHUNK_SIZE;

        // Gérer les indices négatifs
        if (localX < 0) localX += CHUNK_SIZE;
        if (localY < 0) localY += CHUNK_SIZE;
        if (localZ < 0) localZ += CHUNK_SIZE;

        if (localX >= CHUNK_SIZE || localY >= CHUNK_SIZE || localZ >= CHUNK_SIZE)
        {
            GD.PrintErr($"[Map] Indices locaux invalides pour {worldPos} (chunk {chunkPos})");
            return null;
        }

        return chunk.Tiles[localX, localY, localZ];
    }

    public Map(int seed = 42) // Seed par défaut = 42 (peut être changé)
    {
        WorldSeed = seed;
    }

    public void SetTile(Vector3I worldPos, Tile tile)
    {
        var chunk = GetChunk(worldPos);
        var local = WorldToLocal(worldPos);
        var nextTile = tile ?? new Tile();
        chunk.Tiles[local.X, local.Y, local.Z] = nextTile;

        var generated = GetGeneratedTile(worldPos);
        if (TilesEqual(nextTile, generated))
            _modifiedTiles.Remove(worldPos);
        else
            _modifiedTiles.Add(worldPos);
    }

    public bool HasTile(Vector3I pos)
    {
        return GetTile(pos) != null;
    }

    public IReadOnlyCollection<Vector3I> GetModifiedTiles() => _modifiedTiles;

    public void ResetGeneratedWorldState()
    {
        Chunks.Clear();
        _modifiedTiles.Clear();
    }

    // =========================
    // 📦 CHUNK MANAGEMENT
    // =========================
    public Chunk GetChunk(Vector3I worldPos)
    {
        var chunkPos = WorldToChunk(worldPos);

        if (!Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk = GenerateChunk(chunkPos);
            Chunks[chunkPos] = chunk;
        }

        return chunk;
    }

    // =========================
    // 🔄 COORD CONVERSIONS
    // =========================
    public Vector3I WorldToChunk(Vector3I pos)
    {
        return new Vector3I(
            Mathf.FloorToInt((float)pos.X / CHUNK_SIZE),
            Mathf.FloorToInt((float)pos.Y / CHUNK_SIZE),
            Mathf.FloorToInt((float)pos.Z / CHUNK_SIZE)
        );
    }

    public Vector3I WorldToLocal(Vector3I pos)
    {
        return new Vector3I(
            Mod(pos.X, CHUNK_SIZE),
            Mod(pos.Y, CHUNK_SIZE),
            Mod(pos.Z, CHUNK_SIZE)
        );
    }

    public int Mod(int a, int b)
    {
        return (a % b + b) % b;
    }

    // =========================
    // 🌍 GENERATION
    // =========================
    Chunk GenerateChunk(Vector3I chunkPos)
    {
        var chunk = new Chunk();
        chunk.ChunkPos = chunkPos;
        chunk.Tiles = new Tile[CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE];

        for (int x = 0; x < CHUNK_SIZE; x++)
        for (int y = 0; y < CHUNK_SIZE; y++)
        for (int z = 0; z < CHUNK_SIZE; z++)
        {
            int worldX = chunkPos.X * CHUNK_SIZE + x;
            int worldY = chunkPos.Y * CHUNK_SIZE + y;
            int worldZ = chunkPos.Z * CHUNK_SIZE + z;
            chunk.Tiles[x, y, z] = GenerateTileAtWorldPosition(worldX, worldY, worldZ);
        }

        return chunk;
    }

    public Tile GetGeneratedTile(Vector3I worldPos) =>
        GenerateTileAtWorldPosition(worldPos.X, worldPos.Y, worldPos.Z);

    Tile GenerateTileAtWorldPosition(int worldX, int worldY, int worldZ)
    {
        var tile = new Tile();

        // 🌍 SEULEMENT y=0 est du sol (ground)
        if (worldY == 0)
        {
            tile.Type = "ground";
            tile.Solid = true;
        }
        // 🏔️ Entre y=1 et y=10 : masse de pierre + cavités (bruit 3D cohérent)
        else if (worldY > 0 && worldY <= 10)
        {
            float cave = TerrainNoise.Sample3D(
                worldX * 0.11f,
                worldY * 0.10f,
                worldZ * 0.11f,
                WorldSeed);

            // Seuil plus haut = plus de grottes / tunnels reliés
            if (cave > 0.56f)
            {
                tile.Type = "air";
                tile.Solid = false;
            }
            else
            {
                tile.Type = "stone";
                tile.Solid = true;
            }
        }
        // 🛠️ Sol de jeu : plateforme (les colons marchent dans la couche d'air au-dessus)
        else if (worldY == WorldFloorY)
        {
            tile.Type = "platform";
            tile.Solid = true;
        }
        // 🌌 Au-dessus du sol de jeu : air
        else
        {
            tile.Type = "air";
            tile.Solid = false;
        }

        // 🌳 Arbres en surface avec bruit + hash déterministe (indépendant de l'ordre de génération des chunks)
        const int treeSalt = 913_517;
        if (worldY == WorldFloorY && tile.Type == "platform")
        {
            float scatter = TerrainNoise.Sample3D(worldX * 0.27f, WorldFloorY * 0.07f, worldZ * 0.27f, WorldSeed + treeSalt);
            float chance = Deterministic01(worldX, worldZ, WorldSeed + treeSalt * 3);
            if (scatter > 0.82f && chance < 0.4f)
            {
                tile.Type = "tree";
                tile.Solid = true;
            }
        }

        return tile;
    }

    static float Deterministic01(int x, int z, int seed)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h = (h ^ (uint)seed) * 16777619u;
            return (h & 0x00FFFFFFu) / 16777216f;
        }
    }

    static bool TilesEqual(Tile a, Tile b)
    {
        if (a == null || b == null)
            return a == b;
        return a.Solid == b.Solid && string.Equals(a.Type, b.Type, StringComparison.Ordinal);
    }

    public Chunk GenerateFlatChunk(Vector3I chunkPos)
    {
        var chunk = new Chunk();

        for (int x = 0; x < chunk.GetChunkSize(); x++)
        for (int y = 0; y < chunk.GetChunkSize(); y++)
        for (int z = 0; z < chunk.GetChunkSize(); z++)
        {
            int worldY = chunkPos.Y * chunk.GetChunkSize() + y;

            if (worldY == 0)
            {
                chunk.Tiles[x,y,z] = new Tile
                {
                    Solid = true,
                    Type = "ground"
                };
            }
            else
            {
                chunk.Tiles[x,y,z] = new Tile
                {
                    Solid = false,
                    Type = "air"
                };
            }
        }

        return chunk;
    }

    // =========================
    // 📏 DEBUG SIZE
    // =========================
    public Vector3I GetSize()
    {
        return new Vector3I(1000, 1000, 1000); // virtuel (monde infini)
    }

    public Chunk GetOrCreateChunk(Vector3I chunkPos)
    {
        if (!Chunks.TryGetValue(chunkPos, out var chunk))
        {
            chunk = GenerateChunk(chunkPos);
            Chunks[chunkPos] = chunk;
        }
        return chunk;
    }
}