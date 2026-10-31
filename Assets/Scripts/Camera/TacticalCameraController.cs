using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ouranos.Cameras
{
    /// <summary>
    /// Perspective battlefield camera. Exclusively owns the ground focal point (XZ), yaw, distance and whether it is
    /// following the selection, and derives the Transform from them every LateUpdate: it looks at the focal point on
    /// Y = 0 at a fixed 45° pitch. WASD pans on XZ relative to yaw and cancels follow, Q/E and a horizontal
    /// middle-mouse drag orbit around the focal point, wheel and +/- change the distance. F centers on the centroid of
    /// the selected ships, zooms out only as far as needed to frame them, and follows that centroid until WASD or an
    /// empty selection ends it. Because the camera always looks at the focal point, the viewport-center ray meets the
    /// ground there, so zoom changes only the distance. The selection is read, never written; other systems use
    /// <see cref="Focus"/>, never the Transform.
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
        [Tooltip("F framing keeps selected ship centers inside the viewport minus this fraction at each edge.")]
        [Range(0f, 0.45f)] public float FrameMargin = 0.1f;

        [Header("Selection (optional)")]
        [Tooltip("Read-only source for F focus and follow. Without it F does nothing.")]
        public ShipSelection Selection;

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
        [Tooltip("Focus, frame and follow the selection. Pressing again reframes; it does not stop follow.")]
        public InputAction FocusFollow = new InputAction("FocusFollow", InputActionType.Button, "<Keyboard>/f");

        float distance;
        Camera view;

        public Vector2 FocalPoint => focalXZ;
        public float Yaw => yawDegrees;
        public float Distance => distance;
        /// <summary>True while the focal point tracks the selected ships' centroid.</summary>
        public bool Following { get; private set; }

        /// <summary>Moves the focal point to a ground position (XZ); yaw and distance are kept. While following, the
        /// next frame moves it back to the selection.</summary>
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
            view = GetComponent<Camera>();
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
            FocusFollow.Enable();
        }

        void OnDisable()
        {
            Pan.Disable();
            KeyYaw.Disable();
            YawDrag.Disable();
            YawDragDelta.Disable();
            WheelZoom.Disable();
            KeyZoom.Disable();
            FocusFollow.Disable();
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;

            float yawDelta = KeyYaw.ReadValue<float>() * KeyboardYawSpeed * dt;
            if (YawDrag.IsPressed())
                yawDelta += YawDragDelta.ReadValue<float>() * MMBYawSensitivity;
            yawDegrees = Mathf.Repeat(yawDegrees + yawDelta, 360f);

            // Manual pan always wins over follow, in the same frame.
            Vector2 pan = Pan.ReadValue<Vector2>();
            if (pan != Vector2.zero)
            {
                Following = false;
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

            bool focus = FocusFollow.WasPressedThisFrame();
            if ((focus || Following) && TrySelectionCentroid(out Vector2 centroid))
            {
                focalXZ = centroid;
                if (focus)
                {
                    Following = true;
                    distance = Mathf.Clamp(Mathf.Max(distance, FrameDistance()), MinDistance, MaxDistance);
                }
            }
            else
                Following = false; // An empty selection ends follow; the focal point stays where it is.

            ApplyTransform();
        }

        bool TrySelectionCentroid(out Vector2 centroid)
        {
            centroid = Vector2.zero;
            int count = 0;
            if (Selection != null)
                foreach (ShipMovement ship in Selection.Selected)
                    if (ShipSelection.IsSelectable(ship))
                    {
                        centroid += ship.Motor.State.Position;
                        count++;
                    }
            if (count == 0) return false;
            centroid /= count;
            return true;
        }

        // Smallest distance at which every selected ship center projects inside the viewport shrunk by FrameMargin
        // per edge, for the current focal point, yaw, pitch, field of view and aspect. A point at camera-space
        // (x, y, z0 + d) is inside when |x| <= k·tanH·(z0 + d) and |y| <= k·tanV·(z0 + d), so d >= |x|/(k·tanH) − z0.
        float FrameDistance()
        {
            Quaternion rotation = Quaternion.Euler(PitchDegrees, yawDegrees, 0f);
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up, forward = rotation * Vector3.forward;
            float k = 1f - 2f * FrameMargin;
            float tanV = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad) * k;
            float tanH = tanV * view.aspect;
            Vector3 focal = ShipMovementState.ToWorld(focalXZ);
            float required = 0f;
            foreach (ShipMovement ship in Selection.Selected)
            {
                if (!ShipSelection.IsSelectable(ship)) continue;
                Vector3 offset = ShipMovementState.ToWorld(ship.Motor.State.Position) - focal;
                float depth = Vector3.Dot(offset, forward);
                required = Mathf.Max(required, Mathf.Abs(Vector3.Dot(offset, right)) / tanH - depth);
                required = Mathf.Max(required, Mathf.Abs(Vector3.Dot(offset, up)) / tanV - depth);
            }
            return required;
        }

        void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(PitchDegrees, yawDegrees, 0f);
            transform.SetPositionAndRotation(ShipMovementState.ToWorld(focalXZ) - rotation * Vector3.forward * distance, rotation);
        }
    }
}
