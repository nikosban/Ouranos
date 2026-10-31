using System.Collections.Generic;
using Ouranos.Movement;
using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Owns the player's selected ships: a set of unique selectable ships. Every change first drops entries that are
    /// no longer selectable. Selection never touches orders.
    /// </summary>
    public class ShipSelection : MonoBehaviour
    {
        public IReadOnlyList<ShipMovement> Selected => selected;

        readonly List<ShipMovement> selected = new List<ShipMovement>();

        /// <summary>Replaces the selection with one ship. Null, destroyed or inactive ships are ignored.</summary>
        public void SetSingle(ShipMovement ship)
        {
            if (!IsSelectable(ship)) return;
            Prune();
            if (selected.Count == 1 && selected[0] == ship) return;
            selected.Clear();
            selected.Add(ship);
        }

        /// <summary>Adds a selectable ship that is not selected, removes one that is. Other ships are ignored.</summary>
        public void Toggle(ShipMovement ship)
        {
            if (!IsSelectable(ship)) return;
            Prune();
            if (!selected.Remove(ship))
                selected.Add(ship);
        }

        /// <summary>Replaces the selection with the unique selectable ships in ships; none clears it.</summary>
        public void SetMany(IEnumerable<ShipMovement> ships)
        {
            selected.Clear();
            if (ships == null) return;
            foreach (ShipMovement ship in ships)
                if (IsSelectable(ship) && !selected.Contains(ship))
                    selected.Add(ship);
        }

        public void Clear() => selected.Clear();

        /// <summary>True for a live ship whose movement component is active and enabled.</summary>
        public static bool IsSelectable(ShipMovement ship) => ship != null && ship.isActiveAndEnabled;

        void Prune() => selected.RemoveAll(ship => !IsSelectable(ship));
    }
}
