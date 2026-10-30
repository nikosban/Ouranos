using System.Text;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #1 acceptance scenario: issues two orders to Ship A and records the acceptance metrics
    /// every fixed step. Results show in the Inspector, on screen, and as one log summary.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class MovementAcceptanceScenario : MonoBehaviour
    {
        enum Phase { NotStarted, FirstOrder, SecondOrder, Done }

        [Header("Setup")]
        public ShipMovement ShipA;
        public ShipMovement ShipB;
        public Transform Obstacle;
        public float ObstacleRadius = 10f;
        public Vector2 FirstDestination = new Vector2(15f, 0f);
        public Vector2 SecondDestination = new Vector2(40f, 0f);

        [Header("Criteria")]
        public float ArrivalTimeLimit = 30f;
        public float ArrivalDistance = 2f;
        public float StopSpeed = 0.1f;
        public float ContinuityStartTime = 3f;
        public float ContinuityEndDistance = 5f;
        public float MinContinuitySpeed = 0.5f;
        public float MinObstacleDistance = 12f;
        public float MinShipDistance = 4f;
        public float BlockedStopTimeLimit = 10f;
        public float Tolerance = 0.05f;
        [Tooltip("Time Ship A is observed after stopping on the second order.")]
        public float SettleTime = 2f;

        [Header("Results (recorded at runtime)")]
        public float FirstArrivalTime = -1f;
        public float FirstArrivalDistance = -1f;
        public float FirstEndpointSpeed = -1f;
        public float ObservedMinObstacleDistance = float.PositiveInfinity;
        public float ObservedMinContinuitySpeed = float.PositiveInfinity;
        public float ObservedMinShipDistance = float.PositiveInfinity;
        public float SecondStopTime = -1f;
        public float SecondMaxSpeedAfterStop = -1f;
        public float ShipBMaxDisplacement;
        public bool PassArrival, PassObstacle, PassContinuity, PassShipDistance, PassSecondStop, PassShipBStationary, Pass;
        public bool Finished;

        Phase phase;
        float phaseStart;
        Vector2 shipBStart;
        string summary = "Running…";

        void Start()
        {
            shipBStart = ShipB.Motor.State.Position;
            ShipA.Orders.Move(FirstDestination);
            BeginPhase(Phase.FirstOrder);
        }

        void FixedUpdate()
        {
            if (phase == Phase.NotStarted || phase == Phase.Done) return;

            float t = Time.fixedTime - phaseStart;
            ShipMovementState a = ShipA.Motor.State;
            Vector2 b = ShipB.Motor.State.Position;
            Vector2 obstacle = ShipMovementState.ToPlanar(Obstacle.position);

            ObservedMinObstacleDistance =Mathf.Min(ObservedMinObstacleDistance, Vector2.Distance(a.Position, obstacle));
            ObservedMinShipDistance = Mathf.Min(ObservedMinShipDistance, Vector2.Distance(a.Position, b));
            ShipBMaxDisplacement = Mathf.Max(ShipBMaxDisplacement, Vector2.Distance(b, shipBStart));

            if (phase == Phase.FirstOrder)
            {
                float toDestination = Vector2.Distance(a.Position, FirstDestination);
                if (t >= ContinuityStartTime && toDestination > ContinuityEndDistance)
                    ObservedMinContinuitySpeed = Mathf.Min(ObservedMinContinuitySpeed, a.Speed);

                if (ShipA.Orders.Kind == OrderKind.None && a.Speed <= StopSpeed)
                {
                    FirstArrivalTime = t;
                    FirstArrivalDistance = toDestination;
                    FirstEndpointSpeed = a.Speed;
                    StartSecondOrder();
                }
                else if (t > ArrivalTimeLimit)
                {
                    StartSecondOrder();
                }
            }
            else if (phase == Phase.SecondOrder)
            {
                bool stopped = (ShipA.Motor.Blocked || ShipA.Orders.Kind != OrderKind.Move) && a.Speed <= StopSpeed;
                if (SecondStopTime < 0f && stopped)
                    SecondStopTime = t;

                if (SecondStopTime >= 0f)
                {
                    SecondMaxSpeedAfterStop = Mathf.Max(SecondMaxSpeedAfterStop, a.Speed);
                    if (t >= SecondStopTime + SettleTime) Finish();
                }
                else if (t > BlockedStopTimeLimit)
                {
                    Finish();
                }
            }
        }

        void StartSecondOrder()
        {
            ShipA.Orders.Move(SecondDestination);
            BeginPhase(Phase.SecondOrder);
        }

        void BeginPhase(Phase next)
        {
            phase = next;
            phaseStart = Time.fixedTime;
        }

        void Finish()
        {
            phase = Phase.Done;
            Finished = true;

            PassArrival = FirstArrivalTime >= 0f && FirstArrivalTime <= ArrivalTimeLimit
                && FirstArrivalDistance <= ArrivalDistance + Tolerance && FirstEndpointSpeed <= StopSpeed;
            PassObstacle = ObservedMinObstacleDistance >= MinObstacleDistance;
            PassContinuity = ObservedMinContinuitySpeed >= MinContinuitySpeed;
            PassShipDistance = ObservedMinShipDistance >= MinShipDistance - Tolerance;
            PassSecondStop = SecondStopTime >= 0f && SecondStopTime <= BlockedStopTimeLimit && SecondMaxSpeedAfterStop <= StopSpeed;
            PassShipBStationary = ShipBMaxDisplacement <= 1e-4f;
            Pass = PassArrival && PassObstacle && PassContinuity && PassShipDistance && PassSecondStop && PassShipBStationary;

            var text = new StringBuilder();
            text.AppendLine($"Order 1 arrival: {Result(PassArrival)} time {FirstArrivalTime:F2} s (≤ {ArrivalTimeLimit}), distance {FirstArrivalDistance:F3} u (≤ {ArrivalDistance} + {Tolerance}), speed {FirstEndpointSpeed:F3} u/s (≤ {StopSpeed})");
            text.AppendLine($"Obstacle clearance: {Result(PassObstacle)} min {ObservedMinObstacleDistance:F3} u (≥ {MinObstacleDistance})");
            text.AppendLine($"Continuity: {Result(PassContinuity)} min speed {ObservedMinContinuitySpeed:F3} u/s (≥ {MinContinuitySpeed}) from {ContinuityStartTime} s until {ContinuityEndDistance} u from destination");
            text.AppendLine($"Ship clearance: {Result(PassShipDistance)} min A–B {ObservedMinShipDistance:F3} u (≥ {MinShipDistance} − {Tolerance})");
            text.AppendLine($"Order 2 stop: {Result(PassSecondStop)} stopped at {SecondStopTime:F2} s (≤ {BlockedStopTimeLimit}), max speed after {SecondMaxSpeedAfterStop:F3} u/s (≤ {StopSpeed}), final A {ShipA.Motor.State.Position}");
            text.AppendLine($"Ship B stationary: {Result(PassShipBStationary)} max displacement {ShipBMaxDisplacement:F5} u");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        void OnGUI()
        {
            string live = Finished ? summary
                : $"Phase {phase}  t {Time.fixedTime - phaseStart:F1} s  speed {ShipA.Motor.State.Speed:F2} u/s  " +
                  $"min obstacle {ObservedMinObstacleDistance:F2} u  min A–B {ObservedMinShipDistance:F2} u";
            GUI.Label(new Rect(10f, 10f, 900f, 160f), live);
        }
    }
}
