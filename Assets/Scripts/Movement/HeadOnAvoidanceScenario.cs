using System.Text;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #3 acceptance scenario: two ships ordered head-on through each other's start points. Records the
    /// acceptance metrics every fixed step. Results show in the Inspector, on screen, and as one log summary.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class HeadOnAvoidanceScenario : MonoBehaviour
    {
        [Header("Setup")]
        public ShipMovement ShipA;
        public ShipMovement ShipB;
        public Vector2 DestinationA = new Vector2(30f, 0f);
        public Vector2 DestinationB = new Vector2(-30f, 0f);

        [Header("Criteria")]
        public float TimeLimit = 30f;
        public float ArrivalDistance = 2.05f;
        public float StopSpeed = 0.1f;
        public float MinShipDistance = 4f;
        public float MinDetectionDistance = 10f;
        public float MaxBlockedTime = 0.5f;

        [Header("Results (recorded at runtime)")]
        public float ArrivalTimeA = -1f;
        public float ArrivalTimeB = -1f;
        public float ArrivalDistanceA = -1f;
        public float ArrivalDistanceB = -1f;
        public float ObservedMinShipDistance = float.PositiveInfinity;
        public float FirstAvoidanceTime = -1f;
        public float FirstAvoidanceSeparation = -1f;
        public string FirstAvoidanceShip = "";
        public string SideA = "";
        public string SideB = "";
        public float LongestBlockedA;
        public float LongestBlockedB;
        public bool OrderReplacedA, OrderReplacedB;
        public bool PassArrival, PassSeparation, PassPrediction, PassBlocked, PassOrders, Pass;
        public bool Finished;

        float startTime;
        float blockedRunA, blockedRunB;
        string summary = "Running…";

        void Start()
        {
            ShipA.Orders.Move(DestinationA);
            ShipB.Orders.Move(DestinationB);
            startTime = Time.fixedTime;
        }

        void FixedUpdate()
        {
            if (Finished) return;

            float t = Time.fixedTime - startTime;
            ShipMovementState a = ShipA.Motor.State;
            ShipMovementState b = ShipB.Motor.State;
            float separation = Vector2.Distance(a.Position, b.Position);
            ObservedMinShipDistance = Mathf.Min(ObservedMinShipDistance, separation);

            RecordAvoidance(ShipA, ref SideA, t, separation);
            RecordAvoidance(ShipB, ref SideB, t, separation);
            RecordBlocked(ShipA, ref blockedRunA, ref LongestBlockedA);
            RecordBlocked(ShipB, ref blockedRunB, ref LongestBlockedB);
            RecordOrder(ShipA, DestinationA, ref OrderReplacedA);
            RecordOrder(ShipB, DestinationB, ref OrderReplacedB);
            RecordArrival(ShipA, DestinationA, t, ref ArrivalTimeA, ref ArrivalDistanceA);
            RecordArrival(ShipB, DestinationB, t, ref ArrivalTimeB, ref ArrivalDistanceB);

            if ((ArrivalTimeA >= 0f && ArrivalTimeB >= 0f) || t > TimeLimit)
                Finish();
        }

        void RecordAvoidance(ShipMovement ship, ref string side, float t, float separation)
        {
            AvoidanceDecision decision = ship.Avoidance.Decision;
            if (!decision.Active) return;
            if (side.Length == 0) side = decision.Side.ToString();
            if (FirstAvoidanceTime < 0f)
            {
                FirstAvoidanceTime = t;
                FirstAvoidanceSeparation = separation;
                FirstAvoidanceShip = ship.name;
            }
        }

        static void RecordBlocked(ShipMovement ship, ref float run, ref float longest)
        {
            run = ship.Motor.Blocked ? run + Time.fixedDeltaTime : 0f;
            longest = Mathf.Max(longest, run);
        }

        // The order must stay the original move until it completes (Move → None only).
        static void RecordOrder(ShipMovement ship, Vector2 destination, ref bool replaced)
        {
            OrderKind kind = ship.Orders.Kind;
            if (kind == OrderKind.Stop || (kind == OrderKind.Move && ship.Orders.Destination != destination))
                replaced = true;
        }

        void RecordArrival(ShipMovement ship, Vector2 destination, float t, ref float time, ref float distance)
        {
            if (time >= 0f || ship.Orders.Kind != OrderKind.None) return;
            ShipMovementState state = ship.Motor.State;
            if (state.Speed > StopSpeed) return;
            time = t;
            distance = Vector2.Distance(state.Position, destination);
        }

        void Finish()
        {
            Finished = true;

            PassArrival = ArrivalTimeA >= 0f && ArrivalTimeA <= TimeLimit && ArrivalDistanceA <= ArrivalDistance
                && ArrivalTimeB >= 0f && ArrivalTimeB <= TimeLimit && ArrivalDistanceB <= ArrivalDistance;
            PassSeparation = ObservedMinShipDistance >= MinShipDistance;
            PassPrediction = FirstAvoidanceTime >= 0f && FirstAvoidanceSeparation >= MinDetectionDistance;
            PassBlocked = LongestBlockedA <= MaxBlockedTime && LongestBlockedB <= MaxBlockedTime;
            PassOrders = !OrderReplacedA && !OrderReplacedB;
            Pass = PassArrival && PassSeparation && PassPrediction && PassBlocked && PassOrders;

            var text = new StringBuilder();
            text.AppendLine($"(a) Arrival: {Result(PassArrival)} A {ArrivalTimeA:F2} s, {ArrivalDistanceA:F3} u; B {ArrivalTimeB:F2} s, {ArrivalDistanceB:F3} u (≤ {TimeLimit} s, ≤ {ArrivalDistance} u, ≤ {StopSpeed} u/s)");
            text.AppendLine($"(b) Separation: {Result(PassSeparation)} min A–B {ObservedMinShipDistance:F3} u (≥ {MinShipDistance})");
            text.AppendLine($"(c) Prediction: {Result(PassPrediction)} first decision by {FirstAvoidanceShip} at {FirstAvoidanceTime:F2} s, separation {FirstAvoidanceSeparation:F2} u (≥ {MinDetectionDistance}); sides A {SideA}, B {SideB}");
            text.AppendLine($"(d) Blocking: {Result(PassBlocked)} longest blocked run A {LongestBlockedA:F2} s, B {LongestBlockedB:F2} s (≤ {MaxBlockedTime})");
            text.AppendLine($"(e) Orders: {Result(PassOrders)} replaced A {OrderReplacedA}, B {OrderReplacedB}");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        void OnGUI()
        {
            string live = Finished ? summary
                : $"t {Time.fixedTime - startTime:F1} s  min A–B {ObservedMinShipDistance:F2} u  " +
                  $"A {ShipA.Motor.State.Speed:F2} u/s avoid {ShipA.Avoidance.Active}  B {ShipB.Motor.State.Speed:F2} u/s avoid {ShipB.Avoidance.Active}";
            GUI.Label(new Rect(10f, 10f, 900f, 160f), live);
        }
    }
}
