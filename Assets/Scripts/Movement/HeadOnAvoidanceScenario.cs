using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #3 acceptance scenario: two ships ordered head-on through each other's start points. Records the
    /// acceptance metrics and each ship's trail every fixed step. Results show in the Inspector, on screen, and as
    /// one log summary; trails are drawn as gizmos and logged every TrailLogInterval.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class HeadOnAvoidanceScenario : MonoBehaviour
    {
        /// <summary>One ship's pass shape relative to the straight line from its start to its destination.</summary>
        [System.Serializable]
        public class PassTrack
        {
            public int Activations;
            public int Releases;
            [Tooltip("Largest signed lateral deviation to starboard (+) and port (−) of the original route (u).")]
            public float MaxStarboardDeviation;
            public float MaxPortDeviation;
            [Tooltip("Sustained changes of lateral direction, ignoring movement within the hysteresis.")]
            public int Reversals;
            public float ArrivalDeviation = -1f;

            internal Vector2 Start, Right;
            internal bool HadThreat;
            internal int Direction;
            internal float Extreme;
            internal readonly List<Vector2> Trail = new List<Vector2>(2048);
        }

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
        [Tooltip("Lateral movement smaller than this is jitter, not a direction change (u).")]
        public float LateralHysteresis = 0.5f;
        [Tooltip("Largest allowed deviation to the side not chosen, and from the route at arrival (u).")]
        public float LateralTolerance = 0.5f;
        [Tooltip("Interval between logged trail samples (s).")]
        public float TrailLogInterval = 0.5f;

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
        public PassTrack TrackA = new PassTrack();
        public PassTrack TrackB = new PassTrack();
        public bool PassArrival, PassSeparation, PassPrediction, PassBlocked, PassOrders, PassCoherent, Pass;
        public bool Finished;

        float startTime;
        float blockedRunA, blockedRunB;
        float nextTrailLog;
        string summary = "Running…";

        void Start()
        {
            BeginTrack(TrackA, ShipA, DestinationA);
            BeginTrack(TrackB, ShipB, DestinationB);
            ShipA.Orders.Move(DestinationA);
            ShipB.Orders.Move(DestinationB);
            startTime = Time.fixedTime;
        }

        static void BeginTrack(PassTrack track, ShipMovement ship, Vector2 destination)
        {
            track.Start = ship.Motor.State.Position;
            Vector2 direction = (destination - track.Start).normalized;
            track.Right = new Vector2(direction.y, -direction.x);
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
            RecordTrack(TrackA, ShipA, ArrivalTimeA >= 0f);
            RecordTrack(TrackB, ShipB, ArrivalTimeB >= 0f);

            if (t >= nextTrailLog)
            {
                nextTrailLog += TrailLogInterval;
                Debug.Log($"[Trail] t {t:F2} A {a.Position.x:F2} {a.Position.y:F2} B {b.Position.x:F2} {b.Position.y:F2} " +
                          $"threatA {ShipA.Avoidance.Decision.Threat != null} threatB {ShipB.Avoidance.Decision.Threat != null}");
            }

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

        // Signed lateral deviation (starboard +) from the straight original route, with hysteresis on direction changes.
        void RecordTrack(PassTrack track, ShipMovement ship, bool arrived)
        {
            Vector2 position = ship.Motor.State.Position;
            track.Trail.Add(position);

            bool hasThreat = ship.Avoidance.Decision.Threat != null;
            if (hasThreat && !track.HadThreat) track.Activations++;
            if (!hasThreat && track.HadThreat) track.Releases++;
            track.HadThreat = hasThreat;

            float deviation = Vector2.Dot(position - track.Start, track.Right);
            track.MaxStarboardDeviation = Mathf.Max(track.MaxStarboardDeviation, deviation);
            track.MaxPortDeviation = Mathf.Min(track.MaxPortDeviation, deviation);
            if (arrived && track.ArrivalDeviation < 0f) track.ArrivalDeviation = Mathf.Abs(deviation);

            if (track.Direction == 0)
            {
                if (Mathf.Abs(deviation - track.Extreme) > LateralHysteresis)
                {
                    track.Direction = deviation > track.Extreme ? 1 : -1;
                    track.Extreme = deviation;
                }
            }
            else if ((deviation - track.Extreme) * track.Direction > 0f)
            {
                track.Extreme = deviation;
            }
            else if (Mathf.Abs(deviation - track.Extreme) > LateralHysteresis)
            {
                track.Reversals++;
                track.Direction = -track.Direction;
                track.Extreme = deviation;
            }
        }

        // One coherent pass: one hold, one release, no excursion to the other side, at most one return, back on route.
        bool Coherent(PassTrack track, string side)
        {
            float otherSide = side == nameof(AvoidanceSide.Port) ? track.MaxStarboardDeviation : -track.MaxPortDeviation;
            return track.Activations == 1 && track.Releases == 1 && otherSide <= LateralTolerance
                && track.Reversals <= 1 && track.ArrivalDeviation >= 0f && track.ArrivalDeviation <= LateralTolerance;
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
            PassCoherent = Coherent(TrackA, SideA) && Coherent(TrackB, SideB);
            Pass = PassArrival && PassSeparation && PassPrediction && PassBlocked && PassOrders && PassCoherent;

            var text = new StringBuilder();
            text.AppendLine($"(a) Arrival: {Result(PassArrival)} A {ArrivalTimeA:F2} s, {ArrivalDistanceA:F3} u; B {ArrivalTimeB:F2} s, {ArrivalDistanceB:F3} u (≤ {TimeLimit} s, ≤ {ArrivalDistance} u, ≤ {StopSpeed} u/s)");
            text.AppendLine($"(b) Separation: {Result(PassSeparation)} min A–B {ObservedMinShipDistance:F3} u (≥ {MinShipDistance})");
            text.AppendLine($"(c) Prediction: {Result(PassPrediction)} first decision by {FirstAvoidanceShip} at {FirstAvoidanceTime:F2} s, separation {FirstAvoidanceSeparation:F2} u (≥ {MinDetectionDistance}); sides A {SideA}, B {SideB}");
            text.AppendLine($"(d) Blocking: {Result(PassBlocked)} longest blocked run A {LongestBlockedA:F2} s, B {LongestBlockedB:F2} s (≤ {MaxBlockedTime})");
            text.AppendLine($"(e) Orders: {Result(PassOrders)} replaced A {OrderReplacedA}, B {OrderReplacedB}");
            text.AppendLine($"(f) Coherent pass: {Result(PassCoherent)} {Describe("A", TrackA)}; {Describe("B", TrackB)} " +
                            $"(1 activation, 1 release, other side ≤ {LateralTolerance} u, ≤ 1 reversal at {LateralHysteresis} u hysteresis, arrival ≤ {LateralTolerance} u)");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        static string Describe(string label, PassTrack track) =>
            $"{label} activations {track.Activations}, releases {track.Releases}, deviation +{track.MaxStarboardDeviation:F2}/{track.MaxPortDeviation:F2} u, " +
            $"reversals {track.Reversals}, arrival {track.ArrivalDeviation:F2} u";

        // Editor replay of the recorded trails (A cyan, B yellow).
        void OnDrawGizmos()
        {
            DrawTrail(TrackA, Color.cyan);
            DrawTrail(TrackB, Color.yellow);
        }

        static void DrawTrail(PassTrack track, Color color)
        {
            if (track == null || track.Trail.Count < 2) return;
            Gizmos.color = color;
            for (int i = 1; i < track.Trail.Count; i++)
                Gizmos.DrawLine(ShipMovementState.ToWorld(track.Trail[i - 1]), ShipMovementState.ToWorld(track.Trail[i]));
        }

        void OnGUI()
        {
            string live = Finished ? summary
                : $"t {Time.fixedTime - startTime:F1} s  min A–B {ObservedMinShipDistance:F2} u  " +
                  $"A {ShipA.Motor.State.Speed:F2} u/s avoid {ShipA.Avoidance.Active} act {TrackA.Activations} rev {TrackA.Reversals}  " +
                  $"B {ShipB.Motor.State.Speed:F2} u/s avoid {ShipB.Avoidance.Active} act {TrackB.Activations} rev {TrackB.Reversals}";
            GUI.Label(new Rect(10f, 10f, 1200f, 200f), live);
        }
    }
}
