using Godot;

public class CameraController
{
    Node3D pivot;
    Camera3D camera;

    bool rotating = false;
    float speed = 16f;
    float panSmooth = 11f;
    float followSmooth = 7.5f;
    float zoomStep = 2.5f;
    float zoomSmooth = 12f;
    float minZoom = 6f;
    float maxZoom = 45f;
    float targetZoom;
    Vector3 zoomDirection;
    Vector3 targetPivotPos;
    Vector3 focusPoint = Vector3.Zero;
    bool hasFocusPoint;
    /// <summary>False par défaut : sinon la caméra « suit » le focus (désormais stable, sans survol terrain).</summary>
    bool followFocusEnabled = false;

    public CameraController(Node3D pivot , Camera3D camera)
    {
        this.pivot = pivot;
        this.camera = camera;

        zoomDirection = camera.Position.Normalized();
        if (zoomDirection == Vector3.Zero)
            zoomDirection = new Vector3(0, 0.6f, 0.8f).Normalized();
        targetZoom = Mathf.Clamp(camera.Position.Length(), minZoom, maxZoom);
        targetPivotPos = pivot.Position;
    }

    public void SetFocusPoint(Vector3 worldPoint, bool valid)
    {
        focusPoint = worldPoint;
        hasFocusPoint = valid;
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

        float dt = (float)delta;
        if (move != Vector3.Zero)
        {
            move = move.Normalized();
            float zoom01 = Mathf.Clamp((targetZoom - minZoom) / Mathf.Max(0.001f, maxZoom - minZoom), 0f, 1f);
            float zoomSpeedFactor = Mathf.Lerp(0.65f, 1.45f, zoom01);
            targetPivotPos += move * speed * zoomSpeedFactor * dt;
        }

        if (followFocusEnabled && hasFocusPoint)
        {
            float tf = Mathf.Clamp(dt * followSmooth, 0f, 1f);
            targetPivotPos = targetPivotPos.Lerp(focusPoint, tf);
        }

        float tp = Mathf.Clamp(dt * panSmooth, 0f, 1f);
        pivot.Position = pivot.Position.Lerp(targetPivotPos, tp);

        float currentZoom = camera.Position.Length();
        float t = Mathf.Clamp(dt * zoomSmooth, 0f, 1f);
        float smoothedZoom = Mathf.Lerp(currentZoom, targetZoom, t);
        camera.Position = zoomDirection * smoothedZoom;
    }

    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Middle)
                rotating = mouse.Pressed;
            if (mouse.ButtonIndex == MouseButton.WheelUp)
                targetZoom = Mathf.Clamp(targetZoom - zoomStep, minZoom, maxZoom);

            if (mouse.ButtonIndex == MouseButton.WheelDown)
                targetZoom = Mathf.Clamp(targetZoom + zoomStep, minZoom, maxZoom);
        }

        if (@event is InputEventMouseMotion motion && rotating)
        {
            pivot.RotateY(-motion.Relative.X * 0.01f);
        }

        // F8 = souvent « arrêter le jeu » dans l’éditeur Godot — utiliser une autre touche.
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.PhysicalKeycode == Key.J || key.Keycode == Key.J)
            {
                followFocusEnabled = !followFocusEnabled;
                if (!followFocusEnabled)
                    targetPivotPos = pivot.Position;
                GD.Print(followFocusEnabled
                    ? "[Camera] Suivi de la cible ON (touche J)"
                    : "[Camera] Suivi de la cible OFF (touche J)");
            }
        }
    }
}