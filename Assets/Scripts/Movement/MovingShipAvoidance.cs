using Unity.Profiling;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>Lateral side of an avoidance maneuver relative to the route direction.</summary>
    public enum AvoidanceSide { None = 0, Starboard = 1, Port = -1 }

    /// <summary>Temporary steering adjustment against one moving-ship encounter.</summary>
    public struct AvoidanceDecision
    {
        /// <summary>True while the steering target is adjusted: holding a threat, or recovering after one.</summary>
        public bool Active;
        public Vector2 SteeringPoint;
        public float SpeedCap;
        /// <summary>The held threat; null while recovering.</summary>
        public ShipMovement Threat;
        public float TimeToNearestApproach;
        public float ClosestDistance;
        public AvoidanceSide Side;
        public Vector2 PredictedSelfPosition;
        public Vector2 PredictedThreatPosition;
    }

    /// <summary>
    /// Predictive avoidance between moving ships (Issue #3). Finds moving neighbors, predicts the earliest
    /// closest-approach conflict at constant current velocity, and offsets the route steering target sideways,
    /// parallel to the route, until the threat has passed; then ramps the offset back to the route.
    /// Never writes the Transform, movement state, route or orders; the motor stays the sole state writer and
    /// <see cref="CollisionGuard"/> the final blocking authority.
    /// </summary>
    [RequireComponent(typeof(ShipMotor), typeof(CollisionGuard), typeof(RoutePlanner))]
    public class MovingShipAvoidance : MonoBehaviour
    {
        // Neighbors at or below this speed are left to hard collision blocking.
        const float MovingSpeed = 0.1f;
        // Predicted lateral offsets within this distance count as an exactly symmetric head-on.
        const float HeadOnTolerance = 0.01f;

        // Spreads the first evaluation of each ship across the sample interval's fixed steps.
        static int nextPhase;

        // Sibling scopes inside Movement.Avoidance (Issue #9); they never nest, so their sum is at most the parent.
        static readonly ProfilerMarker NeighborQueryMarker = new ProfilerMarker("Avoidance.NeighborQuery");
        static readonly ProfilerMarker NeighborScanMarker = new ProfilerMarker("Avoidance.NeighborScan");
        static readonly ProfilerMarker SideClearanceMarker = new ProfilerMarker("Avoidance.SideClearance");

        [Tooltip("Layers holding ship collision proxies.")]
        public LayerMask NeighborLayers;

        [Header("Readout (runtime)")]
        public bool Active;
        public bool Recovering;
        public string ThreatName;
        public float TimeToNearestApproach;
        public float ClosestApproach;
        public AvoidanceSide Side;
        public Vector2 TemporaryTarget;
        public float SpeedCap;
        public bool GuardBlocked;
        public int NeighborsExamined;
        public bool Overflowed;
        public int OverflowCount;

        public AvoidanceDecision Decision => decision;

        ShipMovement movement;
        ShipMotor motor;
        CollisionGuard guard;
        RoutePlanner planner;
        Collider[] neighbors;
        Collider[] ownColliders;

        AvoidanceDecision decision;
        Route lastRoute;
        Vector2 routeDirection;
        float sampleTimer;
        float sinceEvaluation;
        bool holding;
        bool braking;
        float separatingTimer;
        float recoveryTimer;

        void Awake()
        {
            movement = GetComponent<ShipMovement>();
            motor = GetComponent<ShipMotor>();
            guard = GetComponent<CollisionGuard>();
            planner = GetComponent<RoutePlanner>();
            ownColliders = GetComponentsInChildren<Collider>();

            ShipMovementProfile profile = movement.Profile;
            neighbors = new Collider[profile.NeighborBufferCapacity];
            int steps = Mathf.Max(1, Mathf.RoundToInt(profile.NeighborSampleInterval / Time.fixedDeltaTime));
            sampleTimer = (nextPhase++ % steps) * Time.fixedDeltaTime;
        }

        /// <summary>
        /// Runs once per movement step. Re-evaluates neighbors every NeighborSampleInterval and otherwise
        /// re-applies the last decision to the current route target. Inactive without a route target.
        /// </summary>
        public AvoidanceDecision Step(SteeringTarget? normalTarget, ShipMovementProfile profile, float deltaTime)
        {
            // A new order replaces or clears the route; discard decisions made for the old one.
            if (planner.Route != lastRoute || !normalTarget.HasValue)
            {
                lastRoute = planner.Route;
                routeDirection = Vector2.zero;
                ClearDecision();
            }

            if (normalTarget.HasValue)
            {
                UpdateRouteDirection(normalTarget.Value);
                sinceEvaluation += deltaTime;
                sampleTimer -= deltaTime;
                if (sampleTimer <= 1e-5f)
                {
                    sampleTimer += profile.NeighborSampleInterval;
                    Evaluate(normalTarget.Value, profile);
                    sinceEvaluation = 0f;
                }

                if (Recovering)
                {
                    recoveryTimer += deltaTime;
                    if (recoveryTimer >= profile.AvoidanceRecoveryTime)
                        ClearDecision();
                }
                if (decision.Active)
                    ApplySide(normalTarget.Value, profile);
            }

            UpdateReadout();
            return decision;
        }

        // Lateral basis: the route direction from the progress point to the route target. Both lie on the route,
        // so the basis does not follow the ship's own sideways motion. Kept when the two points coincide.
        void UpdateRouteDirection(SteeringTarget normalTarget)
        {
            Route route = planner.Route;
            Vector2 along = normalTarget.Point - route.PointAt(route.Progress);
            if (along.sqrMagnitude > 1e-6f)
                routeDirection = along.normalized;
            else if (routeDirection == Vector2.zero)
                routeDirection = motor.State.Forward;
        }

        void Evaluate(SteeringTarget normalTarget, ShipMovementProfile profile)
        {
            ShipMovementState self = motor.State;
            int count;
            using (NeighborQueryMarker.Auto())
                count = Physics.OverlapSphereNonAlloc(ShipMovementState.ToWorld(self.Position), profile.NeighborSearchRadius,
                    neighbors, NeighborLayers, QueryTriggerInteraction.Ignore);
            Overflowed = count == neighbors.Length;
            if (Overflowed) OverflowCount++;

            ShipMovement earliest = null;
            float earliestTime = float.PositiveInfinity, earliestDistance = 0f;
            Vector2 earliestSelf = default, earliestOther = default;
            bool heldSeen = false, heldImminent = false;
            float heldTime = float.PositiveInfinity, heldDistance = 0f;
            Vector2 heldSelf = default, heldOther = default;
            NeighborsExamined = 0;

            using (NeighborScanMarker.Auto())
            {
                for (int i = 0; i < count; i++)
                {
                    Collider candidate = neighbors[i];
                    if (System.Array.IndexOf(ownColliders, candidate) >= 0) continue;
                    if (!candidate.TryGetComponent(out ShipMovement other) || other == movement) continue;
                    if (other.Profile == null || other.Motor == null || other.Motor.State.Speed <= MovingSpeed) continue;

                    NeighborsExamined++;
                    bool imminent = Predict(self, profile, other, out float time, out float distance, out Vector2 selfAt, out Vector2 otherAt);
                    if (holding && other == decision.Threat)
                    {
                        heldSeen = true;
                        heldImminent = imminent;
                        heldTime = imminent ? time : float.PositiveInfinity;
                        heldDistance = distance;
                        heldSelf = selfAt;
                        heldOther = otherAt;
                    }
                    if (imminent && time < earliestTime)
                    {
                        earliest = other;
                        earliestTime = time;
                        earliestDistance = distance;
                        earliestSelf = selfAt;
                        earliestOther = otherAt;
                    }
                }
            }

            if (holding)
            {
                ShipMovement held = decision.Threat;
                // A held threat that disappeared, stopped or left the search radius ends the encounter.
                if (held == null || !heldSeen)
                {
                    BeginRecovery(profile);
                    return;
                }

                ShipMovementState other = held.Motor.State;
                Vector2 r = other.Position - self.Position;
                if (Vector2.Dot(r, routeDirection) <= -(profile.FootprintHalfLength + held.Profile.FootprintHalfLength))
                {
                    // Passed: the threat is behind along the route by at least both footprint half lengths.
                    BeginRecovery(profile);
                    return;
                }

                if (earliest != null && earliest != held && earliestTime < heldTime)
                {
                    // A different neighbor with a strictly earlier conflict replaces the held one.
                    Adopt(earliest, self.Position, normalTarget, profile, earliestSelf, earliestOther);
                    SetPrediction(earliestTime, earliestDistance, earliestSelf, earliestOther);
                    return;
                }

                // Not passed yet: hold the side while closing. A threat that is separating without danger
                // (for example one that turned away) is released after ThreatReleaseDelay.
                Vector2 v = other.Forward * other.Speed - self.Forward * self.Speed;
                bool separating = Vector2.Dot(r, v) >= 0f;
                separatingTimer = separating && !heldImminent ? separatingTimer + sinceEvaluation : 0f;
                if (separatingTimer >= profile.ThreatReleaseDelay)
                {
                    BeginRecovery(profile);
                    return;
                }

                SetPrediction(heldImminent ? heldTime : 0f, heldDistance, heldSelf, heldOther);
                braking = !SideIsClear(self.Position, normalTarget, decision.Side, profile);
                return;
            }

            if (earliest != null)
            {
                // A new threat, also during recovery from a previous one.
                Adopt(earliest, self.Position, normalTarget, profile, earliestSelf, earliestOther);
                SetPrediction(earliestTime, earliestDistance, earliestSelf, earliestOther);
            }
        }

        // Constant-current-velocity nearest approach of the centers (Issue #3 formula). At that time the two capsule
        // center segments, at their current headings, are compared (Issue #24). True when the conflict is imminent.
        static bool Predict(ShipMovementState self, ShipMovementProfile profile, ShipMovement other,
            out float time, out float distance, out Vector2 selfAt, out Vector2 otherAt)
        {
            ShipMovementState state = other.Motor.State;
            Vector2 selfVelocity = self.Forward * self.Speed;
            Vector2 otherVelocity = state.Forward * state.Speed;
            Vector2 r = state.Position - self.Position;
            Vector2 v = otherVelocity - selfVelocity;
            float vv = Vector2.Dot(v, v);

            time = 0f;
            distance = r.magnitude;
            selfAt = self.Position;
            otherAt = state.Position;
            if (vv <= 1e-6f) return false;

            time = Mathf.Clamp(-Vector2.Dot(r, v) / vv, 0f, profile.PredictionHorizon);
            if (time <= 0f) return false;

            selfAt = self.Position + selfVelocity * time;
            otherAt = state.Position + otherVelocity * time;
            Vector2 selfAxis = self.Forward * profile.FootprintHalfSegment;
            Vector2 otherAxis = state.Forward * other.Profile.FootprintHalfSegment;
            distance = SegmentDistance(selfAt - selfAxis, selfAt + selfAxis, otherAt - otherAxis, otherAt + otherAxis);
            return distance < profile.FootprintRadius + other.Profile.FootprintRadius + profile.SafetyMargin;
        }

        // Closest distance between two planar segments: zero if they cross, otherwise the nearest endpoint-to-segment distance.
        static float SegmentDistance(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
        {
            Vector2 a = a1 - a0, b = b1 - b0;
            float d0 = Cross(b, a0 - b0), d1 = Cross(b, a1 - b0), d2 = Cross(a, b0 - a0), d3 = Cross(a, b1 - a0);
            if (d0 * d1 < 0f && d2 * d3 < 0f) return 0f;
            return Mathf.Min(Mathf.Min(PointToSegment(a0, b0, b1), PointToSegment(a1, b0, b1)),
                Mathf.Min(PointToSegment(b0, a0, a1), PointToSegment(b1, a0, a1)));
        }

        static float Cross(Vector2 u, Vector2 w) => u.x * w.y - u.y * w.x;

        static float PointToSegment(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 s = s1 - s0;
            float lengthSquared = Vector2.Dot(s, s);
            float t = lengthSquared < 1e-12f ? 0f : Mathf.Clamp01(Vector2.Dot(p - s0, s) / lengthSquared);
            return Vector2.Distance(p, s0 + s * t);
        }

        void Adopt(ShipMovement threat, Vector2 position, SteeringTarget normalTarget, ShipMovementProfile profile, Vector2 selfAt, Vector2 otherAt)
        {
            // Steer away from the side the threat is predicted to pass on; symmetric head-on passes starboard.
            float lateral = Vector2.Dot(otherAt - selfAt, RouteRight());
            AvoidanceSide preferred = lateral > HeadOnTolerance ? AvoidanceSide.Port : AvoidanceSide.Starboard;
            AvoidanceSide opposite = preferred == AvoidanceSide.Starboard ? AvoidanceSide.Port : AvoidanceSide.Starboard;

            decision.Active = true;
            decision.Threat = threat;
            // Readout only, on acquire or replace; Object.name allocates, so it is never read per step.
            ThreatName = threat.name;
            holding = true;
            Recovering = false;
            separatingTimer = 0f;
            if (SideIsClear(position, normalTarget, preferred, profile))
            {
                decision.Side = preferred;
                braking = false;
            }
            else if (SideIsClear(position, normalTarget, opposite, profile))
            {
                decision.Side = opposite;
                braking = false;
            }
            else
            {
                decision.Side = preferred;
                braking = true;
            }
        }

        // Ends the hold and ramps the lateral offset back to the route over AvoidanceRecoveryTime.
        void BeginRecovery(ShipMovementProfile profile)
        {
            if (braking || profile.AvoidanceRecoveryTime <= 0f)
            {
                ClearDecision();
                return;
            }
            holding = false;
            Recovering = true;
            recoveryTimer = 0f;
            decision.Threat = null;
            ThreatName = "";
            decision.TimeToNearestApproach = 0f;
        }

        void SetPrediction(float time, float distance, Vector2 selfAt, Vector2 otherAt)
        {
            decision.TimeToNearestApproach = time;
            decision.ClosestDistance = distance;
            decision.PredictedSelfPosition = selfAt;
            decision.PredictedThreatPosition = otherAt;
        }

        void ApplySide(SteeringTarget normalTarget, ShipMovementProfile profile)
        {
            if (braking)
            {
                decision.SteeringPoint = normalTarget.Point;
                decision.SpeedCap = 0f;
                return;
            }

            float offset = profile.AvoidanceOffset;
            if (Recovering)
                offset *= 1f - Mathf.Clamp01(recoveryTimer / profile.AvoidanceRecoveryTime);
            decision.SteeringPoint = normalTarget.Point + RouteRight() * ((int)decision.Side * offset);
            decision.SpeedCap = float.MaxValue;
        }

        bool SideIsClear(Vector2 position, SteeringTarget normalTarget, AvoidanceSide side, ShipMovementProfile profile)
        {
            using (SideClearanceMarker.Auto())
            {
                Vector2 point = normalTarget.Point + RouteRight() * ((int)side * profile.AvoidanceOffset);
                return guard.HasClearance(position, point, profile.FootprintRadius + profile.CollisionSkin, profile.FootprintHalfSegment)
                    && planner.IsNavigable(position, point);
            }
        }

        // Starboard unit vector of the route direction (heading 0 = +Z, clockwise).
        Vector2 RouteRight() => new Vector2(routeDirection.y, -routeDirection.x);

        void ClearDecision()
        {
            decision = default;
            ThreatName = "";
            holding = false;
            braking = false;
            Recovering = false;
            separatingTimer = 0f;
            recoveryTimer = 0f;
        }

        void UpdateReadout()
        {
            Active = decision.Active;
            TimeToNearestApproach = decision.TimeToNearestApproach;
            ClosestApproach = decision.ClosestDistance;
            Side = decision.Side;
            TemporaryTarget = decision.SteeringPoint;
            SpeedCap = decision.Active ? decision.SpeedCap : float.PositiveInfinity;
            GuardBlocked = motor.Blocked;
        }
    }
}
