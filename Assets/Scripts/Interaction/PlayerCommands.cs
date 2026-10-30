using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ouranos.Interaction
{
    /// <summary>
    /// Translates the local player's input into selection changes and movement orders:
    /// Select picks a ship or clears the selection, Command moves the selection to the ground point,
    /// Stop stops it. Orders go only through <see cref="MovementOrders"/>; selection only through <see cref="ShipSelection"/>.
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

        [Header("Bindings")]
        public InputAction Select = new InputAction("Select", InputActionType.Button, "<Mouse>/leftButton");
        public InputAction Command = new InputAction("Command", InputActionType.Button, "<Mouse>/rightButton");
        public InputAction Stop = new InputAction("Stop", InputActionType.Button, "<Keyboard>/s");

        void OnEnable()
        {
            if (Camera == null || Selection == null)
            {
                Debug.LogError($"[PlayerCommands] {name}: {(Camera == null ? "Camera" : "Selection")} is not assigned; input disabled.", this);
                enabled = false;
                return;
            }
            Select.performed += OnSelect;
            Command.performed += OnCommand;
            Stop.performed += OnStop;
            Select.Enable();
            Command.Enable();
            Stop.Enable();
        }

        void OnDisable()
        {
            Select.performed -= OnSelect;
            Command.performed -= OnCommand;
            Stop.performed -= OnStop;
            Select.Disable();
            Command.Disable();
            Stop.Disable();
        }

        void OnSelect(InputAction.CallbackContext context)
        {
            ShipMovement ship = null;
            if (Physics.Raycast(PointerRay(context), out RaycastHit hit, Mathf.Infinity, ShipLayers, QueryTriggerInteraction.Ignore))
                ship = hit.collider.GetComponentInParent<ShipMovement>();

            if (ShipSelection.IsSelectable(ship))
                Selection.SetSingle(ship);
            else
                Selection.Clear();
        }

        void OnCommand(InputAction.CallbackContext context)
        {
            if (!HasSelectableShip()) return;
            if (!Physics.Raycast(PointerRay(context), out RaycastHit hit, Mathf.Infinity, GroundLayers, QueryTriggerInteraction.Ignore))
            {
                Debug.Log("[PlayerCommands] pointer is not over the ground; no order issued.", this);
                return;
            }

            Vector2 destination = ShipMovementState.ToPlanar(hit.point);
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                    ship.Orders.Move(destination);
        }

        void OnStop(InputAction.CallbackContext context)
        {
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                    ship.Orders.Stop();
        }

        bool HasSelectableShip()
        {
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                    return true;
            return false;
        }

        // The pointer that pressed the button supplies the position; a non-pointer binding falls back to the current pointer.
        Ray PointerRay(InputAction.CallbackContext context)
        {
            Pointer pointer = context.control.device as Pointer ?? Pointer.current;
            Vector2 screen = pointer != null ? pointer.position.ReadValue() : Vector2.zero;
            return Camera.ScreenPointToRay(screen);
        }
    }
}
