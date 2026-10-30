using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>Lateral side of an avoidance maneuver relative to the route direction.</summary>
    public enum AvoidanceSide { None = 0, Starboard = 1, Port = -1 }

    /// <summary>Temporary steering adjustment against one predicted moving-ship conflict.</summary>
    public struct AvoidanceDecision
    {
        public bool Active;
        public Vector2 SteeringPoint;
        public float SpeedCap;
        public ShipMovement Threat;
        public float TimeToNearestApproach;
        public float ClosestDistance;
        public AvoidanceSide Side;
        public Vector2 PredictedSelfPosition;
        public Vector2 PredictedThreatPosition;
    }

    /// <summary>
    /// Predictive avoidance between moving ships (Issue #3). Finds moving neighbors, predicts the earliest
    /// closest-approach conflict at constant current velocity, and offsets the route steering target to one
    /// side or caps speed. Never writes the Transform, movement state, route or orders; the motor stays the
    /// sole state writer and <see cref="CollisionGuard"/> the final blocking authority.
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

        [Tooltip("Layers holding ship collision proxies.")]
        public LayerMask NeighborLayers;

        [Header("Readout (runtime)")]
        public bool Active;
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
        float sampleTimer;
        float sinceEvaluation;
        float releaseTimer;
        bool braking;

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
                Release();
            }

            if (normalTarget.HasValue)
            {
                sinceEvaluation += deltaTime;
                sampleTimer -= deltaTime;
                if (sampleTimer <= 1e-5f)
                {
                    sampleTimer += profile.NeighborSampleInterval;
                    Evaluate(normalTarget.Value, profile);
                    sinceEvaluation = 0f;
                }
                if (decision.Active)
                    ApplySide(normalTarget.Value, profile);
            }

            UpdateReadout();
            return decision;
        }

        void Evaluate(SteeringTarget normalTarget, ShipMovementProfile profile)
        {
            ShipMovementState self = motor.State;
            int count = Physics.OverlapSphereNonAlloc(ShipMovementState.ToWorld(self.Position), profile.NeighborSearchRadius,
                neighbors, NeighborLayers, QueryTriggerInteraction.Ignore);
            Overflowed = count == neighbors.Length;
            if (Overflowed) OverflowCount++;

            ShipMovement earliest = null;
            float earliestTime = float.PositiveInfinity, earliestDistance = 0f;
            Vector2 earliestSelf = default, earliestOther = default;
            bool retainedImminent = false;
            float retainedTime = float.PositiveInfinity, retainedDistance = 0f;
            Vector2 retainedSelf = default, retainedOther = default;
            NeighborsExamined = 0;

            for (int i = 0; i < count; i++)
            {
                Collider candidate = neighbors[i];
                if (System.Array.IndexOf(ownColliders, candidate) >= 0) continue;
                if (!candidate.TryGetComponent(out ShipMovement other) || other == movement) continue;
                if (other.Profile == null || other.Motor == null || other.Motor.State.Speed <= MovingSpeed) continue;

                NeighborsExamined++;
                if (!Predict(self, profile, other, out float time, out float distance, out Vector2 selfAt, out Vector2 otherAt))
                    continue;

                if (other == decision.Threat)
                {
                    retainedImminent = true;
                    retainedTime = time;
                    retainedDistance = distance;
                    retainedSelf = selfAt;
                    retainedOther = otherAt;
                }
                if (time < earliestTime)
                {
                    earliest = other;
                    earliestTime = time;
                    earliestDistance = distance;
                    earliestSelf = selfAt;
                    earliestOther = otherAt;
                }
            }

            if (decision.Threat != null && retainedImminent && earliest == decision.Threat)
            {
                // Hold the threat and its side through the encounter.
                releaseTimer = 0f;
                SetPrediction(retainedTime, retainedDistance, retainedSelf, retainedOther);
                braking = !SideIsClear(self.Position, normalTarget, decision.Side, profile);
                return;
            }

            if (decision.Threat != null && earliest == null)
            {
                // Release only after the threat stays non-imminent for the release delay.
                releaseTimer += sinceEvaluation;
                if (releaseTimer >= profile.ThreatReleaseDelay)
                    Release();
                return;
            }

            if (earliest != null)
            {
                // No threat yet, or a different neighbor with a strictly earlier conflict.
                Adopt(earliest, self.Position, normalTarget, profile, earliestSelf, earliestOther);
                SetPrediction(earliestTime, earliestDistance, earliestSelf, earliestOther);
            }
        }

        // Constant-current-velocity nearest approach (Issue #3 formula).
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

            distance = (r + v * time).magnitude;
            selfAt = self.Position + selfVelocity * time;
            otherAt = state.Position + otherVelocity * time;
            return distance < profile.FootprintRadius + other.Profile.FootprintRadius + profile.SafetyMargin;
        }

        void Adopt(ShipMovement threat, Vector2 position, SteeringTarget normalTarget, ShipMovementProfile profile, Vector2 selfAt, Vector2 otherAt)
        {
            // Steer away from the side the threat is predicted to pass on; symmetric head-on passes starboard.
            float lateral = Vector2.Dot(otherAt - selfAt, Right(position, normalTarget.Point));
            AvoidanceSide preferred = lateral > HeadOnTolerance ? AvoidanceSide.Port : AvoidanceSide.Starboard;
            AvoidanceSide opposite = preferred == AvoidanceSide.Starboard ? AvoidanceSide.Port : AvoidanceSide.Starboard;

            decision.Active = true;
            decision.Threat = threat;
            releaseTimer = 0f;
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
            }
            else
            {
                decision.SteeringPoint = SidePoint(motor.State.Position, normalTarget, decision.Side, profile);
                decision.SpeedCap = float.MaxValue;
            }
        }

        bool SideIsClear(Vector2 position, SteeringTarget normalTarget, AvoidanceSide side, ShipMovementProfile profile)
        {
            Vector2 point = SidePoint(position, normalTarget, side, profile);
            return guard.HasClearance(position, point, profile.FootprintRadius + profile.CollisionSkin)
                && planner.IsNavigable(position, point);
        }

        Vector2 SidePoint(Vector2 position, SteeringTarget normalTarget, AvoidanceSide side, ShipMovementProfile profile)
        {
            return normalTarget.Point + Right(position, normalTarget.Point) * ((int)side * profile.AvoidanceOffset);
        }

        // Starboard unit vector of the direction from the ship to the route target (heading 0 = +Z, clockwise).
        Vector2 Right(Vector2 position, Vector2 target)
        {
            Vector2 direction = target - position;
            direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : motor.State.Forward;
            return new Vector2(direction.y, -direction.x);
        }

        void Release()
        {
            decision = default;
            releaseTimer = 0f;
            braking = false;
        }

        void UpdateReadout()
        {
            Active = decision.Active;
            ThreatName = decision.Threat != null ? decision.Threat.name : "";
            TimeToNearestApproach = decision.TimeToNearestApproach;
            ClosestApproach = decision.ClosestDistance;
            Side = decision.Side;
            TemporaryTarget = decision.SteeringPoint;
            SpeedCap = decision.Active ? decision.SpeedCap : float.PositiveInfinity;
            GuardBlocked = motor.Blocked;
        }
    }
}
