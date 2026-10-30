using Unity.Profiling;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Runs one ship's movement step in a fixed order:
    /// route refresh → look-ahead steering → moving-ship avoidance → motor proposal → collision guard → motor commit.
    /// </summary>
    [RequireComponent(typeof(MovementOrders), typeof(RoutePlanner), typeof(ShipMotor))]
    [RequireComponent(typeof(CollisionGuard), typeof(CapsuleCollider), typeof(MovingShipAvoidance))]
    public class ShipMovement : MonoBehaviour
    {
        public ShipMovementProfile Profile;

        public SteeringTarget? LastTarget { get; private set; }
        public MovementOrders Orders { get; private set; }
        public RoutePlanner Planner { get; private set; }
        public ShipMotor Motor { get; private set; }
        public MovingShipAvoidance Avoidance { get; private set; }

        CollisionGuard guard;

        // Module boundary markers (Issue #7). FixedStep is inclusive of the five pipeline markers.
        static readonly ProfilerMarker FixedStepMarker = new ProfilerMarker("Movement.FixedStep");
        static readonly ProfilerMarker SteeringMarker = new ProfilerMarker("Movement.Steering");
        static readonly ProfilerMarker AvoidanceMarker = new ProfilerMarker("Movement.Avoidance");
        static readonly ProfilerMarker MotorProposeMarker = new ProfilerMarker("Movement.MotorPropose");
        static readonly ProfilerMarker GuardMarker = new ProfilerMarker("Movement.Guard");
        static readonly ProfilerMarker MotorCommitMarker = new ProfilerMarker("Movement.MotorCommit");

        void Awake()
        {
            Orders = GetComponent<MovementOrders>();
            Planner = GetComponent<RoutePlanner>();
            Motor = GetComponent<ShipMotor>();
            guard = GetComponent<CollisionGuard>();
            Avoidance = GetComponent<MovingShipAvoidance>();
            SyncFootprint();
        }

        void OnValidate() => SyncFootprint();

        // The collision proxy radius always comes from the profile.
        void SyncFootprint()
        {
            if (Profile != null && TryGetComponent(out CapsuleCollider proxy))
                proxy.radius = Profile.FootprintRadius;
        }

        void FixedUpdate()
        {
            using (FixedStepMarker.Auto())
                Step(Time.fixedDeltaTime);
        }

        void Step(float deltaTime)
        {
            LastTarget = null;

            if (Orders.Kind == OrderKind.Move)
            {
                if (Planner.Status == RouteStatus.Failed)
                {
                    Orders.Stop();
                }
                else if (Planner.Status == RouteStatus.Ready)
                {
                    SteeringTarget target;
                    using (SteeringMarker.Auto())
                        target = Steering.EvaluateSteering(Motor.State, Planner.Route, Profile, guard);
                    if (target.RemainingDistance <= Profile.ArrivalRadius)
                        Orders.Complete();
                    else
                        LastTarget = target;
                }
            }

            // Avoidance only adjusts the target point or caps speed; RemainingDistance stays the route's.
            SteeringTarget? steered = LastTarget;
            float speedCap = float.MaxValue;
            AvoidanceDecision avoidance;
            using (AvoidanceMarker.Auto())
                avoidance = Avoidance.Step(LastTarget, Profile, deltaTime);
            if (avoidance.Active && steered.HasValue)
            {
                steered = new SteeringTarget { Point = avoidance.SteeringPoint, RemainingDistance = steered.Value.RemainingDistance };
                speedCap = avoidance.SpeedCap;
            }

            MotionProposal proposal;
            using (MotorProposeMarker.Auto())
                proposal = Motor.ProposeMotion(steered, Profile, deltaTime, speedCap);
            PermittedMotion permitted;
            using (GuardMarker.Auto())
                permitted = guard.ConstrainMotion(Motor.State.Position, proposal.Displacement, Profile.FootprintRadius, Profile.CollisionSkin);
            using (MotorCommitMarker.Auto())
                Motor.CommitMotion(proposal, permitted, deltaTime);
        }
    }
}
