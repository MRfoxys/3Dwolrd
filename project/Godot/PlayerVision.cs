using System.Collections.Generic;
using Godot;

public class PlayerVision
{
    public HashSet<Vector3I> Visible = new();
    public HashSet<Vector3I> Discovered = new();
    /// <summary>Chunks contenant au moins une tuile découverte ou vue — le fog ne dépend plus seulement d’échantillons aux coins du chunk.</summary>
    public HashSet<Vector3I> ChunksWithDiscoveredTile = new();

    public void ClearVisible()
    {
        Visible.Clear();
    }

    public void ResetAll()
    {
        Visible.Clear();
        Discovered.Clear();
        ChunksWithDiscoveredTile.Clear();
    }

    static Vector3I ChunkOfWorldTile(Vector3I worldPos)
    {
        const int S = Map.CHUNK_SIZE;
        return new Vector3I(
            Mathf.FloorToInt((float)worldPos.X / S),
            Mathf.FloorToInt((float)worldPos.Y / S),
            Mathf.FloorToInt((float)worldPos.Z / S));
    }

    void TouchChunkForTile(Vector3I worldPos)
    {
        ChunksWithDiscoveredTile.Add(ChunkOfWorldTile(worldPos));
    }

    public void AddVisible(Vector3I pos)
    {
        Visible.Add(pos);
        Discovered.Add(pos);
        TouchChunkForTile(pos);
    }

    /// <summary>Découverte sans être « en ligne de vue » ce tick (sol, décor proche).</summary>
    public void AddDiscovered(Vector3I pos)
    {
        Discovered.Add(pos);
        TouchChunkForTile(pos);
    }

    public bool IsVisible(Vector3I pos)
    {
        return Visible.Contains(pos);
    }

    public bool IsDiscovered(Vector3I pos)
    {
        return Discovered.Contains(pos);
    }
}