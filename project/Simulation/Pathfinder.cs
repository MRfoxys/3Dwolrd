using System;
using System.Collections.Generic;
using Godot;

public class Pathfinder
{
    Tile[,,] world;
    int sizeX, sizeY, sizeZ;

    Vector3I[] directions = new Vector3I[]
    {
        new(1,0,0), new(-1,0,0),
        new(0,0,1), new(0,0,-1),
        new(0,1,0), new(0,-1,0)
    };

    public Pathfinder(Tile[,,] world)
    {
        this.world = world;
        sizeX = world.GetLength(0);
        sizeY = world.GetLength(1);
        sizeZ = world.GetLength(2);
    }

    class Node
    {
        public Vector3I Pos;
        public int G;
        public int H;
        public int F => G + H;
        public Node Parent;
    }

    int Heuristic(Vector3I a, Vector3I b)
    {
        return Mathf.Abs(a.X - b.X)
            + Mathf.Abs(a.Y - b.Y)
            + Mathf.Abs(a.Z - b.Z);
    }

    bool InBounds(Vector3I p)
    {
        return p.X >= 0 && p.X < sizeX &&
            p.Y >= 0 && p.Y < sizeY &&
            p.Z >= 0 && p.Z < sizeZ;
    }

    bool Walkable(Vector3I p)
    {
        return InBounds(p) && !world[p.X, p.Y, p.Z].Solid;
    }

    public List<Vector3I> FindPath(Vector3I start, Vector3I end)
    {
        var open = new PriorityQueue<Node, int>();
        var visited = new Dictionary<Vector3I, Node>();

        var startNode = new Node
        {
            Pos = start,
            G = 0,
            H = Heuristic(start, end)
        };

        open.Enqueue(startNode, startNode.F);
        visited[start] = startNode;

        while (open.Count > 0)
        {
            var current = open.Dequeue();

            if (current.Pos == end)
                return Reconstruct(current);

            foreach (var dir in directions)
            {
                var nextPos = current.Pos + dir;

                if (!Walkable(nextPos))
                    continue;

                int newG = current.G + 1;

                if (visited.TryGetValue(nextPos, out var existing))
                {
                    if (newG >= existing.G)
                        continue;
                }

                var node = new Node
                {
                    Pos = nextPos,
                    G = newG,
                    H = Heuristic(nextPos, end),
                    Parent = current
                };

                visited[nextPos] = node;
                open.Enqueue(node, node.F);
            }
        }

        return null;
    }

    List<Vector3I> Reconstruct(Node node)
    {
        var path = new List<Vector3I>();

        while (node != null)
        {
            path.Add(node.Pos);
            node = node.Parent;
        }

        path.Reverse();
        return path;
    }
}