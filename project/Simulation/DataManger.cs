using System.Text.Json;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Godot;

public class DataManager
{
    public Dictionary<string, BuildingDef> Buildings = new();

    public void Load(string path)
    {
        var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        var json = file.GetAsText();

        var list = JsonSerializer.Deserialize<List<BuildingDef>>(json);

        foreach (var b in list)
        {
            if (b.Id == null)
            {
                GD.Print("Erreur JSON : id null !");
                continue;
            }

            Buildings[b.Id] = b;
        }
    }
}

public class BuildingDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("hp")]
    public int Hp { get; set; }
}