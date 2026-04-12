using Godot;
using System;

public partial class WorldBootstrap : Node
{
    private Random random = new Random(42); // Seed fixe

    public World CreateDefaultWorld(int chunkRadius = 2, int localPlayerId = 0)
    {
        GD.Print("=== Création du monde ===");

        var world = new World();
        if (world == null)
        {
            GD.PrintErr("Erreur : Impossible de créer le monde.");
            return null;
        }

        var map = new Map();
        if (map == null)
        {
            GD.PrintErr("Erreur : Impossible de créer la map.");
            return null;
        }

        world.CurrentMap = map;

        // 🔹 Générer TOUS les chunks autour de (0, 0, 0) (rayon 2 par défaut)
        for (int x = -chunkRadius; x <= chunkRadius; x++)
        for (int z = -chunkRadius; z <= chunkRadius; z++)
        {
            var chunkPos = new Vector3I(x, 0, z);
            GD.Print($"Génération du chunk à {chunkPos}...");

            var chunk = map.GetOrCreateChunk(chunkPos);
            if (chunk == null)
            {
                GD.PrintErr($"Erreur : Impossible de créer le chunk à {chunkPos}.");
                continue;
            }
        }

        // 🔹 Placer EXACTEMENT 5 colons dans le chunk central (0, 0, 0)
        var startChunkPos = new Vector3I(0, 0, 0);
        var startChunk = map.GetOrCreateChunk(startChunkPos);
        if (startChunk == null)
        {
            GD.PrintErr("Erreur : Impossible de récupérer le chunk central.");
            return world;
        }

        for (int i = 0; i < 5; i++)
        {
            Vector3I spawnPos = new Vector3I(
                random.Next(0, Map.CHUNK_SIZE),
                Map.ColonistWalkY, // air au-dessus du sol y=11 (case marchable pour le pathfinder)
                random.Next(0, Map.CHUNK_SIZE)
            );

            GD.Print($"Colon placé à {spawnPos} (sol en dessous : {map.GetTile(new Vector3I(spawnPos.X, Map.WorldFloorY, spawnPos.Z))?.Type})");

            var colon = new Colonist(spawnPos.X, spawnPos.Y, spawnPos.Z, localPlayerId);
            if (colon == null)
            {
                GD.PrintErr("Erreur : Impossible de créer un colon.");
                continue;
            }

            map.Colonists.Add(colon);
        }

        GD.Print("=== Monde créé avec succès ===");
        return world;
    }
}