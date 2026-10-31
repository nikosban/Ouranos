using Ouranos.Movement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ouranos.Cameras
{
    /// <summary>
    /// Perspective battlefield camera. Exclusively owns the ground focal point (XZ), yaw and distance, and derives
    /// the Transform from them every LateUpdate: it looks at the focal point on Y = 0 at a fixed 45° pitch.
    /// WASD pans on XZ relative to yaw, Q/E and a horizontal middle-mouse drag orbit around the focal point,
    /// wheel and +/- change the distance. Because the camera always looks at the focal point, the viewport-center
    /// ray meets the ground there, so zoom changes only the distance. Other systems use <see cref="Focus"/>,
    /// never the Transform.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class TacticalCameraController : MonoBehaviour
    {
        public const float PitchDegrees = 45f;

        [Header("State (initial values)")]
        [Tooltip("Ground focal point on XZ (u).")]
        [SerializeField] Vector2 focalXZ;
        [Tooltip("Yaw around the focal point; 0 looks toward +Z, positive turns clockwise seen from above (°).")]
        [SerializeField] float yawDegrees;

        [Header("Tuning (provisional)")]
        [Tooltip("Focal point pan speed on XZ (u/s).")]
        [Min(0f)] public float PanSpeed = 25f;
        [Tooltip("Q/E yaw speed (°/s).")]
        [Min(0f)] public float KeyboardYawSpeed = 90f;
        [Tooltip("Middle-mouse horizontal drag yaw (°/pixel).")]
        [Min(0f)] public float MMBYawSensitivity = 0.25f;
        [Tooltip("Distance from the focal point at start, clamped to Min/Max (u).")]
        [Min(0.01f)] public float InitialDistance = 70f;
        [Min(0.01f)] public float MinDistance = 15f;
        [Min(0.01f)] public float MaxDistance = 180f;
        [Tooltip("Fraction of the distance removed per wheel notch inward; outward divides by the same factor.")]
        [Range(0.01f, 0.9f)] public float WheelZoomFraction = 0.12f;
        [Tooltip("+/- zoom speed (u/s).")]
        [Min(0f)] public float KeyZoomSpeed = 35f;

        [Header("Bindings")]
        // Composite defaults are added in Reset: building them in a field initializer starts the Input System from
        // a MonoBehaviour constructor, which the player rejects.
        public InputAction Pan = new InputAction("Pan", InputActionType.Value);
        public InputAction KeyYaw = new InputAction("KeyYaw", InputActionType.Value);
        public InputAction YawDrag = new InputAction("YawDrag", InputActionType.Button, "<Mouse>/middleButton");
        public InputAction YawDragDelta = new InputAction("YawDragDelta", InputActionType.Value, "<Mouse>/delta/x");
        public InputAction WheelZoom = new InputAction("WheelZoom", InputActionType.Value, "<Mouse>/scroll/y");
        [Tooltip("Positive zooms in. Keyboard paths are physical key positions: equals is the main-row + key.")]
        public InputAction KeyZoom = new InputAction("KeyZoom", InputActionType.Value);

        float distance;

        public Vector2 FocalPoint => focalXZ;
        public float Yaw => yawDegrees;
        public float Distance => distance;

        /// <summary>Moves the focal point to a ground position (XZ); yaw and distance are kept.</summary>
        public void Focus(Vector2 focal) => focalXZ = focal;

        void Reset()
        {
            Pan = PanAction();
            KeyYaw = AxisAction("KeyYaw", ("<Keyboard>/q", "<Keyboard>/e"));
            KeyZoom = AxisAction("KeyZoom", ("<Keyboard>/minus", "<Keyboard>/equals"), ("<Keyboard>/numpadMinus", "<Keyboard>/numpadPlus"));
        }

        static InputAction PanAction()
        {
            var action = new InputAction("Pan", InputActionType.Value);
            action.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            return action;
        }

        static InputAction AxisAction(string name, params (string negative, string positive)[] axes)
        {
            var action = new InputAction(name, InputActionType.Value);
            foreach (var (negative, positive) in axes)
                action.AddCompositeBinding("1DAxis").With("Negative", negative).With("Positive", positive);
            return action;
        }

        void Awake()
        {
            distance = Mathf.Clamp(InitialDistance, MinDistance, MaxDistance);
            ApplyTransform();
        }

        void OnEnable()
        {
            Pan.Enable();
            KeyYaw.Enable();
            YawDrag.Enable();
            YawDragDelta.Enable();
            WheelZoom.Enable();
            KeyZoom.Enable();
        }

        void OnDisable()
        {
            Pan.Disable();
            KeyYaw.Disable();
            YawDrag.Disable();
            YawDragDelta.Disable();
            WheelZoom.Disable();
            KeyZoom.Disable();
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;

            float yawDelta = KeyYaw.ReadValue<float>() * KeyboardYawSpeed * dt;
            if (YawDrag.IsPressed())
                yawDelta += YawDragDelta.ReadValue<float>() * MMBYawSensitivity;
            yawDegrees = Mathf.Repeat(yawDegrees + yawDelta, 360f);

            Vector2 pan = Pan.ReadValue<Vector2>();
            if (pan != Vector2.zero)
            {
                Vector3 world = Quaternion.Euler(0f, yawDegrees, 0f) * new Vector3(pan.x, 0f, pan.y);
                focalXZ += ShipMovementState.ToPlanar(world) * (PanSpeed * dt);
            }

            // Uniform scroll reports one unit per notch; the platform range reports 120 per notch on Windows.
            float notches = WheelZoom.ReadValue<float>();
            if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange)
                notches /= 120f;
            distance *= Mathf.Pow(1f - WheelZoomFraction, notches);
            distance -= KeyZoom.ReadValue<float>() * KeyZoomSpeed * dt;
            distance = Mathf.Clamp(distance, MinDistance, MaxDistance);

            ApplyTransform();
        }

        void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(PitchDegrees, yawDegrees, 0f);
            transform.SetPositionAndRotation(ShipMovementState.ToWorld(focalXZ) - rotation * Vector3.forward * distance, rotation);
        }
    }
}
