using Godot;
using System.Collections.Generic;

public class Colonist
{
    public int OwnerId;
    public Vector3I Position { get; set; }
    public int WaitTimer = 0;

    public Vector3I Target;
    public int RepathTimer = 0;

    public float MoveProgress = 0f;
    public float MoveSpeed = 6.0f;
    public int WorkTicksRemaining = 0;

    public bool HasPathFailed = false;
    public List<Vector3I> Path = new();
    public SimJob ActiveJob;
    /// <summary>Après un bloc de chantier : retour obligatoire vers cette case avant un nouveau job.</summary>
    public Vector3I? PostBuildRallyCell;

    public Colonist()
    {
        Position = new Vector3I(0, 0, 0);
        Target = new Vector3I(0, 0, 0);
    }

    public Colonist(int spawnX ,int spawnY, int spawnZ , int ownerId = 0)
    {
        Position = new Vector3I(spawnX, spawnY, spawnZ);
        Target = new Vector3I(0, 0, 0);
        OwnerId = ownerId;
        GD.Print($"Colon créé à {Position}");
    }

    public void MoveTo(Vector3I newPos, Map map)
    {
        // 🔹 Génère les chunks autour de la nouvelle position
        var chunkPos = new Vector3I(
            Mathf.FloorToInt((float)newPos.X / Map.CHUNK_SIZE),
            0,
            Mathf.FloorToInt((float)newPos.Z / Map.CHUNK_SIZE)
        );

        // Génère les chunks dans un rayon de 1 autour du colon
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            map.GetOrCreateChunk(new Vector3I(chunkPos.X + x, 0, chunkPos.Z + z));
        }

        Position = newPos;
        GD.Print($"Colon déplacé à {Position}");
    }
}