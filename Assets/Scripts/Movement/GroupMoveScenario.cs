using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #5 acceptance scenario: one group move for the listed ships, dispatched once in Start, then every fixed
    /// step records each ship's order, arrival, obstacle clearance and ship-to-ship separation. Results show in the
    /// Inspector, on screen, and as one log summary; assigned destinations are drawn as gizmos.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class GroupMoveScenario : MonoBehaviour
    {
        /// <summary>One ship's assigned destination and recorded outcome.</summary>
        [System.Serializable]
        public class ShipRecord
        {
            public ShipMovement Ship;
            public Vector2 Start;
            public Vector2 AssignedDestination;
            public Vector2 ExpectedDestination;
            public float CommandError = -1f;
            public float ArrivalTime = -1f;
            public float ArrivalDistance = -1f;
            public float ArrivalSpeed = -1f;
            public bool OrderReplaced;
            public bool Failed;
            public float ObservedMinObstacleDistance = float.PositiveInfinity;
        }

        [Header("Setup")]
        public ShipMovement[] Ships;
        public Vector2 TargetAnchor = new Vector2(30f, 0f);
        [Tooltip("Expected assigned destination per entry of Ships, in the same order.")]
        public Vector2[] ExpectedDestinations =
        {
            new Vector2(22f, -18f), new Vector2(29f, 4f), new Vector2(39f, 14f),
        };
        public Transform Obstacle;

        [Header("Criteria")]
        public int ExpectedOrders = 3;
        public float CommandTolerance = 0.01f;
        public float TimeLimit = 30f;
        public float ArrivalDistance = 2.05f;
        public float StopSpeed = 0.1f;
        public float MinObstacleDistance = 12f;
        public float MinShipDistance = 4f;

        [Header("Results (recorded at runtime)")]
        public int OrdersDispatched;
        public Vector2 AssignedCentroid;
        public float CentroidError = -1f;
        public float ObservedMinShipDistance = float.PositiveInfinity;
        public string ClosestPair = "";
        public List<ShipRecord> Records = new List<ShipRecord>();
        public bool PassCommand, PassArrival, PassClearance, PassOrders, Pass;
        public bool Finished;

        float startTime;
        string summary = "Running…";

        void Start()
        {
            IReadOnlyList<GroupMoveAssignment> assignments = GroupMoveDispatcher.Move(Ships, TargetAnchor);
            OrdersDispatched = assignments.Count;

            Vector2 sum = Vector2.zero;
            for (int i = 0; i < assignments.Count; i++)
            {
                GroupMoveAssignment assignment = assignments[i];
                var record = new ShipRecord
                {
                    Ship = assignment.Ship,
                    Start = assignment.Ship.Motor.State.Position,
                    AssignedDestination = assignment.Destination,
                };
                int index = System.Array.IndexOf(Ships, assignment.Ship);
                if (index >= 0 && index < ExpectedDestinations.Length)
                {
                    record.ExpectedDestination = ExpectedDestinations[index];
                    record.CommandError = Vector2.Distance(assignment.Destination, record.ExpectedDestination);
                }
                // The ship's own order is authoritative; it must hold exactly the assigned destination.
                if (assignment.Ship.Orders.Kind != OrderKind.Move || assignment.Ship.Orders.Destination != assignment.Destination)
                    record.OrderReplaced = true;
                Records.Add(record);
                sum += assignment.Destination;
            }
            if (assignments.Count > 0)
            {
                AssignedCentroid = sum / assignments.Count;
                CentroidError = Vector2.Distance(AssignedCentroid, TargetAnchor);
            }

            foreach (ShipRecord record in Records)
                Debug.Log($"[GroupMove] {record.Ship.name} start ({record.Start.x:F2}, {record.Start.y:F2}) → " +
                          $"assigned ({record.AssignedDestination.x:F2}, {record.AssignedDestination.y:F2})");
            startTime = Time.fixedTime;
        }

        void FixedUpdate()
        {
            if (Finished) return;

            float t = Time.fixedTime - startTime;
            bool allDone = Records.Count > 0;
            for (int i = 0; i < Records.Count; i++)
            {
                ShipRecord record = Records[i];
                ShipMovementState state = record.Ship.Motor.State;
                if (Obstacle != null)
                    record.ObservedMinObstacleDistance = Mathf.Min(record.ObservedMinObstacleDistance,
                        Vector2.Distance(state.Position, ShipMovementState.ToPlanar(Obstacle.position)));

                for (int j = i + 1; j < Records.Count; j++)
                {
                    float separation = Vector2.Distance(state.Position, Records[j].Ship.Motor.State.Position);
                    if (separation < ObservedMinShipDistance)
                    {
                        ObservedMinShipDistance = separation;
                        ClosestPair = $"{record.Ship.name}–{Records[j].Ship.name} at {t:F2} s";
                    }
                }

                RecordOrder(record);
                RecordArrival(record, t);
                if (record.ArrivalTime < 0f && !record.Failed) allDone = false;
            }

            if (allDone || t > TimeLimit)
                Finish();
        }

        // A failed route stops the ship and clears the planner within the same step, so an unexpected Stop is
        // the failure signal; a Failed status is recorded too when observed. Any other Move destination is a replacement.
        static void RecordOrder(ShipRecord record)
        {
            MovementOrders orders = record.Ship.Orders;
            if (record.Ship.Planner.Status == RouteStatus.Failed || (orders.Kind == OrderKind.Stop && record.ArrivalTime < 0f))
                record.Failed = true;
            if (orders.Kind == OrderKind.Move && orders.Destination != record.AssignedDestination)
                record.OrderReplaced = true;
        }

        void RecordArrival(ShipRecord record, float t)
        {
            if (record.ArrivalTime >= 0f || record.Failed || record.Ship.Orders.Kind != OrderKind.None) return;
            ShipMovementState state = record.Ship.Motor.State;
            if (state.Speed > StopSpeed) return;
            record.ArrivalTime = t;
            record.ArrivalDistance = Vector2.Distance(state.Position, record.AssignedDestination);
            record.ArrivalSpeed = state.Speed;
        }

        void Finish()
        {
            Finished = true;

            PassCommand = OrdersDispatched == ExpectedOrders && CentroidError >= 0f && CentroidError <= CommandTolerance;
            PassArrival = Records.Count > 0;
            PassClearance = ObservedMinShipDistance >= MinShipDistance;
            PassOrders = true;
            foreach (ShipRecord record in Records)
            {
                PassCommand &= record.CommandError >= 0f && record.CommandError <= CommandTolerance;
                PassArrival &= !record.Failed && record.ArrivalTime >= 0f && record.ArrivalTime <= TimeLimit
                    && record.ArrivalDistance <= ArrivalDistance && record.ArrivalSpeed <= StopSpeed;
                PassClearance &= record.ObservedMinObstacleDistance >= MinObstacleDistance;
                PassOrders &= !record.OrderReplaced;
            }
            Pass = PassCommand && PassArrival && PassClearance && PassOrders;

            var text = new StringBuilder();
            text.AppendLine($"(1) Command: {Result(PassCommand)} {OrdersDispatched} orders (= {ExpectedOrders}), " +
                            $"assigned centroid ({AssignedCentroid.x:F3}, {AssignedCentroid.y:F3}) error {CentroidError:F4} u (≤ {CommandTolerance})");
            foreach (ShipRecord record in Records)
                text.AppendLine($"    {record.Ship.name}: assigned ({record.AssignedDestination.x:F3}, {record.AssignedDestination.y:F3}) " +
                                $"expected ({record.ExpectedDestination.x:F0}, {record.ExpectedDestination.y:F0}) error {record.CommandError:F4} u");
            text.AppendLine($"(2) Arrival: {Result(PassArrival)} (≤ {TimeLimit} s, ≤ {ArrivalDistance} u, ≤ {StopSpeed} u/s)");
            foreach (ShipRecord record in Records)
                text.AppendLine(record.Failed
                    ? $"    {record.Ship.name}: FAILED (route failed or stopped)"
                    : $"    {record.Ship.name}: {(record.ArrivalTime >= 0f ? "completed" : "not arrived")} {record.ArrivalTime:F2} s, " +
                      $"{record.ArrivalDistance:F3} u from its target, {record.ArrivalSpeed:F3} u/s");
            text.AppendLine($"(3) Clearance: {Result(PassClearance)} min ship–ship {ObservedMinShipDistance:F3} u ({ClosestPair}) (≥ {MinShipDistance}); " +
                            $"min obstacle {Describe(record => record.ObservedMinObstacleDistance)} (≥ {MinObstacleDistance})");
            text.AppendLine($"(4) Orders: {Result(PassOrders)} replaced {Describe(record => record.OrderReplaced ? 1f : 0f, "F0")}");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        string Describe(System.Func<ShipRecord, float> value, string format = "F3")
        {
            var parts = new List<string>();
            foreach (ShipRecord record in Records)
                parts.Add($"{record.Ship.name} {value(record).ToString(format)}");
            return string.Join(", ", parts);
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        void OnDrawGizmos()
        {
            if (Records == null) return;
            Gizmos.color = Color.magenta;
            foreach (ShipRecord record in Records)
                if (record.Ship != null)
                    Gizmos.DrawWireSphere(ShipMovementState.ToWorld(record.AssignedDestination), 1f);
        }

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1200f, 300f), summary);
                return;
            }
            var live = new StringBuilder($"t {Time.fixedTime - startTime:F1} s  min ship–ship {ObservedMinShipDistance:F2} u");
            foreach (ShipRecord record in Records)
                live.Append($"  {record.Ship.name} {record.Ship.Motor.State.Speed:F2} u/s {record.Ship.Orders.Kind}");
            GUI.Label(new Rect(10f, 10f, 1200f, 300f), live.ToString());
        }
    }
}
