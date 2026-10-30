using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Runs one ship's movement step in a fixed order:
    /// route refresh → look-ahead steering → motor proposal → collision guard → motor commit.
    /// </summary>
    [RequireComponent(typeof(MovementOrders), typeof(RoutePlanner), typeof(ShipMotor))]
    [RequireComponent(typeof(CollisionGuard), typeof(CapsuleCollider))]
    public class ShipMovement : MonoBehaviour
    {
        public ShipMovementProfile Profile;

        public SteeringTarget? LastTarget { get; private set; }
        public MovementOrders Orders { get; private set; }
        public RoutePlanner Planner { get; private set; }
        public ShipMotor Motor { get; private set; }

        CollisionGuard guard;

        void Awake()
        {
            Orders = GetComponent<MovementOrders>();
            Planner = GetComponent<RoutePlanner>();
            Motor = GetComponent<ShipMotor>();
            guard = GetComponent<CollisionGuard>();
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
            float deltaTime = Time.fixedDeltaTime;
            LastTarget = null;

            if (Orders.Kind == OrderKind.Move)
            {
                if (Planner.Status == RouteStatus.Failed)
                {
                    Orders.Stop();
                }
                else if (Planner.Status == RouteStatus.Ready)
                {
                    SteeringTarget target = Steering.EvaluateSteering(Motor.State, Planner.Route, Profile, guard);
                    if (target.RemainingDistance <= Profile.ArrivalRadius)
                        Orders.Complete();
                    else
                        LastTarget = target;
                }
            }

            MotionProposal proposal = Motor.ProposeMotion(LastTarget, Profile, deltaTime);
            PermittedMotion permitted = guard.ConstrainMotion(Motor.State.Position, proposal.Displacement, Profile.FootprintRadius, Profile.CollisionSkin);
            Motor.CommitMotion(proposal, permitted, deltaTime);
        }
    }
}
