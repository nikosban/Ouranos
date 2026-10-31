using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;

namespace Ouranos.Sandbox
{
    /// <summary>
    /// Disposable Development Sandbox tooling: selects the listed ships once at Start through
    /// <see cref="ShipSelection.SetMany"/>. Holds no selection state and issues no orders; later selection changes go
    /// through <see cref="ShipSelection"/> as usual. Nothing outside the sandbox may reference this type.
    /// </summary>
    public class InitialSandboxSelection : MonoBehaviour
    {
        public ShipSelection Selection;
        [Tooltip("Ships selected once at Start.")]
        public ShipMovement[] Ships;

        void Start()
        {
            if (Selection == null)
            {
                Debug.LogError($"[InitialSandboxSelection] {name}: Selection is not assigned; nothing selected.", this);
                return;
            }
            Selection.SetMany(Ships);
            int listed = Ships == null ? 0 : Ships.Length;
            if (Selection.Selected.Count != listed)
                Debug.LogWarning($"[InitialSandboxSelection] {name}: selected {Selection.Selected.Count} of {listed} listed ships (missing reference or ship not selectable).", this);
        }
    }
}
