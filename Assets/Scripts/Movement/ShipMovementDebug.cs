using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Gizmos for route corners, progress, the look-ahead target, the collision footprint and moving-ship
    /// avoidance (temporary target magenta, threat line and predicted closest-approach positions orange).
    /// </summary>
    [RequireComponent(typeof(ShipMovement))]
    public class ShipMovementDebug : MonoBehaviour
    {
        const int CircleSegments = 48;

        void OnDrawGizmos()
        {
            var movement = GetComponent<ShipMovement>();
            if (movement.Profile == null) return;

            bool blocked = Application.isPlaying && movement.Motor != null && movement.Motor.Blocked;
            Gizmos.color = blocked ? Color.red : Color.green;
            DrawCircle(transform.position, movement.Profile.FootprintRadius);

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

        static void DrawCircle(Vector3 center, float radius)
        {
            Vector3 previous = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= CircleSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / CircleSegments;
                Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
