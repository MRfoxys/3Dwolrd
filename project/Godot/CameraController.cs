using Godot;

public class CameraController
{
    Node3D pivot;
    Camera3D camera;

    bool rotating = false;
    float speed = 10f;

    public CameraController(Node3D pivot , Camera3D camera)
    {
        this.pivot = pivot;
        this.camera = camera;
    }

    public void Update(double delta)
    {
        Vector3 move = Vector3.Zero;

        Vector3 forward = -pivot.GlobalTransform.Basis.Z;
        forward.Y = 0;
        forward = forward.Normalized();

        Vector3 right = pivot.GlobalTransform.Basis.X;
        right.Y = 0;
        right = right.Normalized();

        if (Input.IsActionPressed("ui_up"))
            move += forward;

        if (Input.IsActionPressed("ui_down"))
            move -= forward;

        if (Input.IsActionPressed("ui_left"))
            move -= right;

        if (Input.IsActionPressed("ui_right"))
            move += right;

        if (Input.IsActionPressed("camera_up"))
            move += Vector3.Up;

        if (Input.IsActionPressed("camera_down"))
            move += Vector3.Down;

        pivot.Position += move * speed * (float)delta;
    }

    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Middle)
                rotating = mouse.Pressed;
            if (mouse.ButtonIndex == MouseButton.WheelUp)
                camera.Position += -camera.GlobalTransform.Basis.Z * 2;

            if (mouse.ButtonIndex == MouseButton.WheelDown)
                camera.Position += camera.GlobalTransform.Basis.Z * 2;
        }

        if (@event is InputEventMouseMotion motion && rotating)
        {
            pivot.RotateY(-motion.Relative.X * 0.01f);
        }
    }
}