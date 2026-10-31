using System.Collections.Generic;
using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ouranos.Interaction
{
    /// <summary>
    /// Translates the local player's input into selection changes and movement orders.
    /// Select resolves on release: a click (pointer moved ≤ DragThreshold) picks a ship (Shift toggles it) or clears the
    /// selection (Shift: no change); a drag selects the ships whose center projects inside the rectangle.
    /// Command moves one selected ship through <see cref="MovementOrders"/>, or two or more through one
    /// <see cref="GroupMoveDispatcher.Move"/>; Stop stops them. Selection changes go only through <see cref="ShipSelection"/>.
    /// </summary>
    public class PlayerCommands : MonoBehaviour
    {
        [Tooltip("Camera whose view the pointer is in. Required; there is no fallback.")]
        public Camera Camera;
        public ShipSelection Selection;

        [Tooltip("Layers hit-tested for ship collision proxies (provisional).")]
        public LayerMask ShipLayers = 1 << 6;
        [Tooltip("Layers hit-tested for the ground (provisional).")]
        public LayerMask GroundLayers = 1 << 0;
        [Tooltip("Pointer travel from the press beyond which Select is a drag rectangle, not a click (px, provisional).")]
        [Range(1f, 50f)] public float DragThreshold = 5f;

        [Header("Bindings")]
        public InputAction Select = new InputAction("Select", InputActionType.Button, "<Mouse>/leftButton");
        public InputAction Command = new InputAction("Command", InputActionType.Button, "<Mouse>/rightButton");
        public InputAction Stop = new InputAction("Stop", InputActionType.Button, "<Keyboard>/c");
        [Tooltip("Held during a click: toggle the clicked ship instead of replacing the selection.")]
        public InputAction AddModifier = new InputAction("AddModifier", InputActionType.Button, "<Keyboard>/shift");

        /// <summary>True while Select is held and the pointer has moved beyond DragThreshold.</summary>
        public bool IsDragging { get; private set; }
        /// <summary>Screen rectangle (pixels, origin bottom-left) of the gesture in progress.</summary>
        public Rect DragRect { get; private set; }

        bool pressing;
        Vector2 pressPosition, currentPosition;
        Pointer pressPointer;
        readonly List<ShipMovement> boxed = new List<ShipMovement>();
        readonly List<ShipMovement> commanded = new List<ShipMovement>();

        void OnEnable()
        {
            if (Camera == null || Selection == null)
            {
                Debug.LogError($"[PlayerCommands] {name}: {(Camera == null ? "Camera" : "Selection")} is not assigned; input disabled.", this);
                enabled = false;
                return;
            }
            Select.started += OnSelectPressed;
            Select.canceled += OnSelectReleased;
            Command.performed += OnCommand;
            Stop.performed += OnStop;
            Select.Enable();
            Command.Enable();
            Stop.Enable();
            AddModifier.Enable();
        }

        void OnDisable()
        {
            Select.started -= OnSelectPressed;
            Select.canceled -= OnSelectReleased;
            Command.performed -= OnCommand;
            Stop.performed -= OnStop;
            Select.Disable();
            Command.Disable();
            Stop.Disable();
            AddModifier.Disable();
            pressing = IsDragging = false;
        }

        // Keeps the gesture state current while Select is held, so the rectangle view can draw it.
        void Update()
        {
            if (pressing) UpdateGesture(PointerPosition(pressPointer));
        }

        void OnSelectPressed(InputAction.CallbackContext context)
        {
            pressPointer = context.control.device as Pointer ?? Pointer.current;
            pressPosition = PointerPosition(pressPointer);
            pressing = true;
            UpdateGesture(pressPosition);
        }

        void OnSelectReleased(InputAction.CallbackContext context)
        {
            if (!pressing) return;
            UpdateGesture(PointerPosition(pressPointer));
            bool drag = IsDragging;
            pressing = IsDragging = false;

            if (drag) SelectBox(DragRect);
            else SelectClick(currentPosition);
        }

        void UpdateGesture(Vector2 current)
        {
            currentPosition = current;
            DragRect = Rect.MinMaxRect(Mathf.Min(pressPosition.x, current.x), Mathf.Min(pressPosition.y, current.y),
                                       Mathf.Max(pressPosition.x, current.x), Mathf.Max(pressPosition.y, current.y));
            IsDragging = Vector2.Distance(pressPosition, current) > DragThreshold;
        }

        void SelectClick(Vector2 screen)
        {
            ShipMovement ship = null;
            if (Physics.Raycast(Camera.ScreenPointToRay(screen), out RaycastHit hit, Mathf.Infinity, ShipLayers, QueryTriggerInteraction.Ignore))
                ship = hit.collider.GetComponentInParent<ShipMovement>();

            bool add = AddModifier.IsPressed();
            if (ShipSelection.IsSelectable(ship))
            {
                if (add) Selection.Toggle(ship);
                else Selection.SetSingle(ship);
            }
            else if (!add)
                Selection.Clear();
        }

        // Provisional membership rule: the ship's authoritative center on Y = 0 projects inside the rectangle.
        void SelectBox(Rect rect)
        {
            boxed.Clear();
            foreach (ShipMovement ship in FindObjectsByType<ShipMovement>())
            {
                if (!ShipSelection.IsSelectable(ship) || (ShipLayers.value & (1 << ship.gameObject.layer)) == 0) continue;
                Vector3 screen = Camera.WorldToScreenPoint(ShipMovementState.ToWorld(ship.Motor.State.Position));
                if (screen.z > 0f && rect.Contains(new Vector2(screen.x, screen.y)))
                    boxed.Add(ship);
            }
            Selection.SetMany(boxed);
        }

        void OnCommand(InputAction.CallbackContext context)
        {
            commanded.Clear();
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                    commanded.Add(ship);
            if (commanded.Count == 0) return;

            if (!Physics.Raycast(PointerRay(context), out RaycastHit hit, Mathf.Infinity, GroundLayers, QueryTriggerInteraction.Ignore))
            {
                Debug.Log("[PlayerCommands] pointer is not over the ground; no order issued.", this);
                return;
            }

            Vector2 destination = ShipMovementState.ToPlanar(hit.point);
            if (commanded.Count == 1)
                commanded[0].Orders.Move(destination);
            else
                GroupMoveDispatcher.Move(commanded, destination);
        }

        void OnStop(InputAction.CallbackContext context)
        {
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                    ship.Orders.Stop();
        }

        // The pointer that pressed the button supplies the position; a non-pointer binding falls back to the current pointer.
        Ray PointerRay(InputAction.CallbackContext context) =>
            Camera.ScreenPointToRay(PointerPosition(context.control.device as Pointer ?? Pointer.current));

        static Vector2 PointerPosition(Pointer pointer) => pointer != null ? pointer.position.ReadValue() : Vector2.zero;
    }
}
