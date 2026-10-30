using UnityEngine;

namespace Ouranos.Movement
{
    public struct MotionProposal
    {
        public float Heading;
        public float Speed;
        public Vector2 Displacement;
    }

    /// <summary>
    /// Owns the authoritative planar pose and forward speed. Forward-only thrust with capped turning
    /// is a reversible prototype assumption (Issue #1), not a final handling decision.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class ShipMotor : MonoBehaviour
    {
        public ShipMovementState State => state;
        public bool Blocked { get; private set; }

        ShipMovementState state;
        Rigidbody body;

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            body.isKinematic = true;
            state.Position = ShipMovementState.ToPlanar(transform.position);
            state.Heading = transform.eulerAngles.y;
        }

        /// <summary>Turns toward the target and integrates speed; with no target, brakes. Desired speed never exceeds speedCap.</summary>
        public MotionProposal ProposeMotion(SteeringTarget? target, ShipMovementProfile profile, float deltaTime, float speedCap = float.MaxValue)
        {
            float heading = state.Heading;
            float desiredSpeed = 0f;

            if (target.HasValue)
            {
                Vector2 toTarget = target.Value.Point - state.Position;
                float distance = toTarget.magnitude;
                if (distance > 1e-4f)
                {
                    float desiredHeading = ShipMovementState.DirectionToHeading(toTarget);
                    heading = Mathf.MoveTowardsAngle(state.Heading, desiredHeading, profile.TurnRate * deltaTime);
                    float headingError = Mathf.Abs(Mathf.DeltaAngle(heading, desiredHeading));
                    // Two-argument Mathf.Min only: the params overload allocates an array every call (Issue #7).
                    desiredSpeed = Mathf.Min(Mathf.Min(Mathf.Min(profile.MaxSpeed,
                        ArrivalSpeedCap(target.Value.RemainingDistance, profile)),
                        TurnSpeedCap(headingError, distance, profile)),
                        speedCap);
                }
            }

            float rate = desiredSpeed > state.Speed ? profile.Acceleration : profile.Deceleration;
            float speed = Mathf.MoveTowards(state.Speed, desiredSpeed, rate * deltaTime);
            return new MotionProposal
            {
                Heading = heading,
                Speed = speed,
                Displacement = ShipMovementState.HeadingToDirection(heading) * speed * deltaTime,
            };
        }

        /// <summary>Applies the permitted displacement. A blocked ship keeps only the speed it could actually move.</summary>
        public void CommitMotion(MotionProposal proposal, PermittedMotion permitted, float deltaTime)
        {
            Blocked = permitted.Blocked;
            state.Heading = proposal.Heading;
            state.Position += permitted.Displacement;
            state.Speed = permitted.Blocked ? permitted.Displacement.magnitude / deltaTime : proposal.Speed;

            // Teleport the kinematic proxy so other ships' queries this step see the committed pose.
            body.position = ShipMovementState.ToWorld(state.Position);
            body.rotation = Quaternion.Euler(0f, state.Heading, 0f);
        }

        static float ArrivalSpeedCap(float remainingRouteDistance, ShipMovementProfile profile)
        {
            return Mathf.Sqrt(2f * profile.Deceleration * Mathf.Max(0f, remainingRouteDistance - profile.ArrivalRadius));
        }

        // Pure-pursuit curvature to the target is 2·sin(error)/distance; the turn rate limits speed to TurnRate/curvature.
        static float TurnSpeedCap(float headingErrorDegrees, float distance, ShipMovementProfile profile)
        {
            float sin = headingErrorDegrees >= 90f ? 1f : Mathf.Sin(headingErrorDegrees * Mathf.Deg2Rad);
            if (sin < 1e-3f) return float.MaxValue;
            return profile.TurnRate * Mathf.Deg2Rad * distance / (2f * sin);
        }
    }
}
