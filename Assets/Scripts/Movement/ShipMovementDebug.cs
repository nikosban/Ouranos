using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Gizmos for route corners, progress, the look-ahead target, the capsule collision footprint and moving-ship
    /// avoidance (temporary target magenta, threat line and predicted closest-approach positions orange).
    /// </summary>
    [RequireComponent(typeof(ShipMovement))]
    public class ShipMovementDebug : MonoBehaviour
    {
        const int ArcSegments = 24;

        void OnDrawGizmos()
        {
            var movement = GetComponent<ShipMovement>();
            if (movement.Profile == null) return;

            bool blocked = Application.isPlaying && movement.Motor != null && movement.Motor.Blocked;
            Gizmos.color = blocked ? Color.red : Color.green;
            float heading = Application.isPlaying && movement.Motor != null ? movement.Motor.State.Heading : transform.eulerAngles.y;
            DrawCapsule(transform.position, ShipMovementState.HeadingToDirection(heading),
                movement.Profile.FootprintRadius, movement.Profile.FootprintHalfSegment);

            if (!Application.isPlaying || movement.Planner == null) return;

            Route route = movement.Planner.Route;
            if (route != null)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < route.Corners.Count; i++)
                {
                    Vector3 corner = ShipMovementState.ToWorld(route.Corners[i]);
                    Gizmos.DrawSphere(corner, 0.4f);
                    if (i > 0) Gizmos.DrawLine(ShipMovementState.ToWorld(route.Corners[i - 1]), corner);
                }
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(ShipMovementState.ToWorld(route.PointAt(route.Progress)), 0.3f);
            }

            if (movement.LastTarget.HasValue)
            {
                Vector3 target = ShipMovementState.ToWorld(movement.LastTarget.Value.Point);
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, target);
                Gizmos.DrawSphere(target, 0.5f);
            }

            AvoidanceDecision avoidance = movement.Avoidance != null ? movement.Avoidance.Decision : default;
            if (avoidance.Active)
            {
                Vector3 temporary = ShipMovementState.ToWorld(avoidance.SteeringPoint);
                Gizmos.color = Color.magenta;
                Gizmos.DrawLine(transform.position, temporary);
                Gizmos.DrawSphere(temporary, 0.6f);

                Gizmos.color = new Color(1f, 0.5f, 0f);
                if (avoidance.Threat != null)
                    Gizmos.DrawLine(transform.position, avoidance.Threat.transform.position);
                Vector3 selfAt = ShipMovementState.ToWorld(avoidance.PredictedSelfPosition);
                Vector3 threatAt = ShipMovementState.ToWorld(avoidance.PredictedThreatPosition);
                Gizmos.DrawWireSphere(selfAt, 0.5f);
                Gizmos.DrawWireSphere(threatAt, 0.5f);
                Gizmos.DrawLine(selfAt, threatAt);
            }
        }

        // Planar capsule outline: two half circles joined by the side lines, aligned to the heading.
        static void DrawCapsule(Vector3 center, Vector2 forward, float radius, float halfSegment)
        {
            Vector3 axis = ShipMovementState.ToWorld(forward);
            Vector3 side = new Vector3(axis.z, 0f, -axis.x);
            Vector3 front = center + axis * halfSegment;
            Vector3 back = center - axis * halfSegment;
            Gizmos.DrawLine(front + side * radius, back + side * radius);
            Gizmos.DrawLine(front - side * radius, back - side * radius);
            DrawHalfCircle(front, axis, side, radius);
            DrawHalfCircle(back, -axis, -side, radius);
        }

        // Half circle from +side through the outward direction to −side.
        static void DrawHalfCircle(Vector3 center, Vector3 outward, Vector3 side, float radius)
        {
            Vector3 previous = center + side * radius;
            for (int i = 1; i <= ArcSegments; i++)
            {
                float angle = i * Mathf.PI / ArcSegments;
                Vector3 next = center + (side * Mathf.Cos(angle) + outward * Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
