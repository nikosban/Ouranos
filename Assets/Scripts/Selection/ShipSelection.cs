using System.Collections.Generic;
using Ouranos.Movement;
using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Owns the player's selected ships. Holds at most one ship for now; the read-only list keeps the seam
    /// ready for group selection. Selection never touches orders.
    /// </summary>
    public class ShipSelection : MonoBehaviour
    {
        public IReadOnlyList<ShipMovement> Selected => selected;

        readonly List<ShipMovement> selected = new List<ShipMovement>(1);

        /// <summary>Replaces the selection with one ship. Null, destroyed or inactive ships are ignored.</summary>
        public void SetSingle(ShipMovement ship)
        {
            if (!IsSelectable(ship)) return;
            if (selected.Count == 1 && selected[0] == ship) return;
            selected.Clear();
            selected.Add(ship);
        }

        public void Clear() => selected.Clear();

        /// <summary>True for a live ship whose movement component is active and enabled.</summary>
        public static bool IsSelectable(ShipMovement ship) => ship != null && ship.isActiveAndEnabled;
    }
}
