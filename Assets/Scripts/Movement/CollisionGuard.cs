using UnityEngine;

namespace Ouranos.Movement
{
    public struct PermittedMotion
    {
        public Vector2 Displacement;
        public bool Blocked;
    }

    /// <summary>
    /// Planar swept footprint queries against ship and obstacle collision proxies.
    /// Never writes movement state.
    /// </summary>
    public class CollisionGuard : MonoBehaviour
    {
        [Tooltip("Layers that physically block this ship (ships and obstacles).")]
        public LayerMask BlockingLayers;

        [Tooltip("Static layers checked when steering looks across route corners.")]
        public LayerMask StaticClearanceLayers;

        // Half height of the vertical query capsule; the footprint is full radius at y = 0.
        const float QueryHalfHeight = 1f;

        readonly RaycastHit[] hits = new RaycastHit[16];
        Collider[] ownColliders;

        void Awake() => ownColliders = GetComponentsInChildren<Collider>();

        /// <summary>
        /// Limits a proposed displacement so the footprint, inflated by the skin, never overlaps blocking geometry.
        /// </summary>
        public PermittedMotion ConstrainMotion(Vector2 position, Vector2 displacement, float radius, float skin)
        {
            float distance = displacement.magnitude;
            if (distance < 1e-6f)
                return new PermittedMotion { Displacement = Vector2.zero, Blocked = false };

            Vector2 direction = displacement / distance;
            if (!Sweep(position, direction, distance, radius + skin, BlockingLayers, out float free))
                return new PermittedMotion { Displacement = displacement, Blocked = false };

            return new PermittedMotion { Displacement = direction * free, Blocked = true };
        }

        /// <summary>True if the footprint can sweep in a straight line between two points without touching static geometry.</summary>
        public bool HasClearance(Vector2 from, Vector2 to, float radius)
        {
            Vector2 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 1e-6f) return true;
            return !Sweep(from, delta / distance, distance, radius, StaticClearanceLayers, out _);
        }

        bool Sweep(Vector2 position, Vector2 direction, float maxDistance, float radius, LayerMask mask, out float free)
        {
            Vector3 center = ShipMovementState.ToWorld(position);
            Vector3 offset = Vector3.up * QueryHalfHeight;
            int count = Physics.CapsuleCastNonAlloc(center + offset, center - offset, radius,
                ShipMovementState.ToWorld(direction), hits, maxDistance, mask, QueryTriggerInteraction.Ignore);

            free = maxDistance;
            bool blocked = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (System.Array.IndexOf(ownColliders, hit.collider) >= 0) continue;

                // A query that starts touching (resting one skin away) only blocks motion toward the collider.
                if (hit.distance <= 0f)
                {
                    Vector2 toward = ShipMovementState.ToPlanar(hit.collider.ClosestPoint(center) - center);
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
    }
}
