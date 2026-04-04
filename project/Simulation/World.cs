using System.Collections.Generic;

public class World
{
    public List<Map> Maps = new();
    public Map CurrentMap;

    public void Update()
    {
        CurrentMap?.Update();
    }
}