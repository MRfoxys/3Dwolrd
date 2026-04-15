using Godot;

public sealed class GameInputModule
{
    public bool IsShortcut(InputEventKey key, Key wanted)
    {
        return key.PhysicalKeycode == wanted || key.Keycode == wanted;
    }
}
