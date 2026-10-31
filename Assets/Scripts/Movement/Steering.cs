using UnityEngine;

namespace Ouranos.Movement
{
    public struct SteeringTarget
    {
        public Vector2 Point;
        public float RemainingDistance;
    }

    /// <summary>Look-ahead path following along a route polyline.</summary>
    public static class Steering
    {
        // Candidate points tried, from the full look-ahead back toward the ship's progress.
        const int BackOffSamples = 12;

        /// <summary>
        /// Advances route progress and returns a point up to LookAheadDistance ahead along the route,
        /// possibly across several corners, backed off until the straight shortcut to it is clear.
        /// </summary>
        public static SteeringTarget EvaluateSteering(ShipMovementState state, Route route, ShipMovementProfile profile, CollisionGuard clearance)
        {
            route.AdvanceProgress(state.Position);
            float reach = Mathf.Min(profile.LookAheadDistance, route.RemainingDistance);

            float clearanceRadius = profile.FootprintRadius + profile.CollisionSkin;
            Vector2 point = route.PointAt(route.Progress + reach);
            for (int i = BackOffSamples - 1; i >= 1 && !clearance.HasClearance(state.Position, point, clearanceRadius, profile.FootprintHalfSegment); i--)
                point = route.PointAt(route.Progress + reach * i / BackOffSamples);

            return new SteeringTarget { Point = point, RemainingDistance = route.RemainingDistance };
        }
    }
}
