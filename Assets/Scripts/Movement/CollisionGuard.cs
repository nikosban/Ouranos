using UnityEngine;

namespace Ouranos.Movement
{
    public struct PermittedMotion
    {
        public Vector2 Displacement;
        public bool Blocked;
        /// <summary>True when the proposed turn was refused and the previous heading kept.</summary>
        public bool TurnBlocked;
    }

    /// <summary>
    /// Planar heading-aligned capsule footprint queries against ship and obstacle collision proxies:
    /// turn overlap, swept translation and static clearance. Never writes movement state.
    /// </summary>
    public class CollisionGuard : MonoBehaviour
    {
        [Tooltip("Layers that physically block this ship (ships and obstacles).")]
        public LayerMask BlockingLayers;

        [Tooltip("Static layers checked when steering looks across route corners.")]
        public LayerMask StaticClearanceLayers;

        readonly RaycastHit[] hits = new RaycastHit[16];
        readonly Collider[] overlaps = new Collider[16];
        Collider[] ownColliders;

        void Awake() => ownColliders = GetComponentsInChildren<Collider>();

        /// <summary>
        /// True if the footprint may turn in place from heading to proposedHeading: the rotated footprint, inflated by
        /// half the skin, overlaps no blocking geometry. One step turns at most TurnRate · dt, so the end pose bounds the sweep.
        /// </summary>
        public bool ConstrainTurn(Vector2 position, float heading, float proposedHeading, float radius, float halfSegment, float skin)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(heading, proposedHeading)) < 1e-5f) return true;

            Segment(position, ShipMovementState.HeadingToDirection(proposedHeading), halfSegment, out Vector3 front, out Vector3 back);
            int count = Physics.OverlapCapsuleNonAlloc(front, back, radius + skin * 0.5f, overlaps, BlockingLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (System.Array.IndexOf(ownColliders, overlaps[i]) < 0)
                    return false;
            return true;
        }

        /// <summary>
        /// Limits a proposed displacement so the footprint at the given heading, inflated by the skin, never overlaps blocking geometry.
        /// </summary>
        public PermittedMotion ConstrainMotion(Vector2 position, float heading, Vector2 displacement, float radius, float halfSegment, float skin)
        {
            float distance = displacement.magnitude;
            if (distance < 1e-6f)
                return new PermittedMotion { Displacement = Vector2.zero, Blocked = false };

            Vector2 direction = displacement / distance;
            Vector2 axis = ShipMovementState.HeadingToDirection(heading);
            if (!Sweep(position, axis, direction, distance, radius + skin, halfSegment, BlockingLayers, out float free))
                return new PermittedMotion { Displacement = displacement, Blocked = false };

            return new PermittedMotion { Displacement = direction * free, Blocked = true };
        }

        /// <summary>
        /// True if the footprint, aligned with the travel direction, can sweep in a straight line between two points
        /// without touching static geometry.
        /// </summary>
        public bool HasClearance(Vector2 from, Vector2 to, float radius, float halfSegment)
        {
            Vector2 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 1e-6f) return true;
            Vector2 direction = delta / distance;
            return !Sweep(from, direction, direction, distance, radius, halfSegment, StaticClearanceLayers, out _);
        }

        bool Sweep(Vector2 position, Vector2 axis, Vector2 direction, float maxDistance, float radius, float halfSegment, LayerMask mask, out float free)
        {
            Segment(position, axis, halfSegment, out Vector3 front, out Vector3 back);
            int count = Physics.CapsuleCastNonAlloc(front, back, radius,
                ShipMovementState.ToWorld(direction), hits, maxDistance, mask, QueryTriggerInteraction.Ignore);

            free = maxDistance;
            bool blocked = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (System.Array.IndexOf(ownColliders, hit.collider) >= 0) continue;

                // A query that starts touching (resting one skin away) only blocks motion toward the collider,
                // measured from the nearest point of the footprint's own segment.
                if (hit.distance <= 0f)
                {
                    Vector3 self = ClosestOnSegment(front, back, hit.collider.ClosestPoint(ShipMovementState.ToWorld(position)));
                    Vector2 toward = ShipMovementState.ToPlanar(hit.collider.ClosestPoint(self) - self);
                    if (Vector2.Dot(toward, direction) <= 0f) continue;
                }

                if (hit.distance < free)
                {
                    free = hit.distance;
                    blocked = true;
                }
            }
            return blocked;
        }

        // End points of the footprint's center segment on the movement plane (y = 0).
        static void Segment(Vector2 position, Vector2 axis, float halfSegment, out Vector3 front, out Vector3 back)
        {
            Vector3 center = ShipMovementState.ToWorld(position);
            Vector3 offset = ShipMovementState.ToWorld(axis) * halfSegment;
            front = center + offset;
            back = center - offset;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared < 1e-12f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSquared);
        }
    }
}
