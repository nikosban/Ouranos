using System.Collections.Generic;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>One ship's destination from a group move.</summary>
    public readonly struct GroupMoveAssignment
    {
        public readonly ShipMovement Ship;
        public readonly Vector2 Destination;

        public GroupMoveAssignment(ShipMovement ship, Vector2 destination)
        {
            Ship = ship;
            Destination = destination;
        }
    }

    /// <summary>
    /// Group Move (Issue #5): translates every ship's current XZ position by the displacement from the group's
    /// centroid to the target anchor, with no rotation, and issues one independent move order per ship.
    /// Stateless; the caller owns the ship set and the ships own their orders.
    /// </summary>
    public static class GroupMoveDispatcher
    {
        /// <summary>
        /// Issues one move order to each unique, active ship so that the assigned destinations' centroid is targetAnchor.
        /// Null, destroyed, inactive and duplicate entries are skipped before the centroid is computed.
        /// </summary>
        public static IReadOnlyList<GroupMoveAssignment> Move(IReadOnlyList<ShipMovement> ships, Vector2 targetAnchor)
        {
            // Snapshot every valid ship's position before any order is issued.
            var group = new List<ShipMovement>();
            var positions = new List<Vector2>();
            Vector2 sum = Vector2.zero;
            if (ships != null)
            {
                foreach (ShipMovement ship in ships)
                {
                    if (ship == null || !ship.isActiveAndEnabled || group.Contains(ship)) continue;
                    Vector2 position = ship.Motor.State.Position;
                    group.Add(ship);
                    positions.Add(position);
                    sum += position;
                }
            }

            var assignments = new List<GroupMoveAssignment>(group.Count);
            if (group.Count == 0)
            {
                Debug.LogWarning("[GroupMove] no valid ships; no orders issued.");
                return assignments;
            }

            Vector2 delta = targetAnchor - sum / group.Count;
            for (int i = 0; i < group.Count; i++)
            {
                Vector2 destination = positions[i] + delta;
                group[i].Orders.Move(destination);
                assignments.Add(new GroupMoveAssignment(group[i], destination));
            }
            return assignments;
        }
    }
}
