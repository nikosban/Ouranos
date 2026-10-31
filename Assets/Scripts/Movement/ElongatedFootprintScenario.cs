using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #24 acceptance scenario for the elongated capsule footprint. Runs its cases one after another: each case
    /// root starts inactive, is activated, receives its orders through <see cref="MovementOrders"/>, runs until its
    /// orders are done or its time limit, and is deactivated again, so no other case's ships take part in physics or
    /// avoidance. Every fixed step it measures, with its own geometry, signed capsule separations, signed 5 × 15 hull
    /// rectangle separations, obstacle gaps, guard and turn blocks and avoidance activations, and logs them as
    /// [Footprint] lines. Results show in the Inspector, on screen and as [Acceptance] lines.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ElongatedFootprintScenario : MonoBehaviour
    {
        public enum CaseKind
        {
            /// <summary>All ships ordered at once; physical and visual separation are judged.</summary>
            Traffic,
            /// <summary>The first ship's first turn is known to penetrate the obstacle; the turn must be refused.</summary>
            ForcedTurn,
            /// <summary>The first ship is driven into the stationary second ship, then ordered away from the contact.</summary>
            RestingEscape,
        }

        [System.Serializable]
        public class ShipOrder
        {
            public ShipMovement Ship;
            [Tooltip("Move destination (world XZ). Ignored when Ordered is off.")]
            public Vector2 Destination;
            [Tooltip("Off for a ship that stays without orders (RestingEscape contact ship).")]
            public bool Ordered = true;
        }

        [System.Serializable]
        public class Case
        {
            public string Name;
            public CaseKind Kind;
            [Tooltip("Inactive in the scene; holds this case's ships.")]
            public GameObject Root;
            [Tooltip("Region center (world XZ); every footprint must stay within RegionHalfSize of it.")]
            public Vector2 Origin;
            public ShipOrder[] Orders;
            [Tooltip("Optional static obstacle measured by this case.")]
            public Transform Obstacle;
            public float ObstacleRadius = 10f;
            [Tooltip("RestingEscape only: the first ship's approach destination, through the stationary ship (world XZ).")]
            public Vector2 ApproachDestination;
            [Min(1f)] public float TimeLimit = 40f;
            [Tooltip("Owner-accepted limitation (Issue #24 F1): hull rectangle corner overlap reports KNOWN-LIMITATION instead of FAIL.")]
            public bool AcceptCornerOverlap;
            [Tooltip("Owner-accepted limitation (Issue #24 F2/F3): orders not completed within the time limit report KNOWN-LIMITATION instead of FAIL.")]
            public bool AcceptStall;

            [Header("Results (runtime)")]
            public float MinCapsuleSeparation = float.PositiveInfinity;
            public float MinHullSeparation = float.PositiveInfinity;
            public float MinCapsuleObstacle = float.PositiveInfinity;
            public float MinHullObstacle = float.PositiveInfinity;
            public int GuardBlockSteps;
            public float MaxContinuousBlock;
            public int TurnBlockSteps;
            public float MaxContinuousTurnBlock;
            public int AvoidanceActivations;
            public float MaxExtent;
            public bool IsolationViolated;
            public string Outcomes = "";
            public string Detail = "";
            public bool PassPhysical, PassVisual, PassRegion, PassOrders, PassKind, Pass;
            public bool UnexpectedOrder;
            public bool KnownLimitation;
        }

        [Header("Setup")]
        public Case[] Cases;

        [Header("Geometry (provisional placeholder hull, Issue #24)")]
        [Tooltip("Half width of the visual hull rectangle (u).")]
        public float HullHalfWidth = 2.5f;
        [Tooltip("Half length of the visual hull rectangle (u).")]
        public float HullHalfLength = 7.5f;

        [Header("Criteria")]
        public float PenetrationTolerance = 0.001f;
        public float RegionHalfSize = 50f;
        public float StopSpeed = 0.1f;
        [Tooltip("ForcedTurn: longest allowed continuous refused turn (s).")]
        public float MaxTurnBlockedTime = 2f;
        [Tooltip("ForcedTurn: largest allowed sideways slip per moving step (u). Float position rounding is about 1e-5 u.")]
        public float MaxLateralSlip = 1e-4f;
        [Tooltip("RestingEscape: time the approaching ship must stay blocked and stopped before the escape order (s).")]
        public float ContactHoldTime = 0.5f;
        [Tooltip("RestingEscape: separation that counts as moved away from the contact (u).")]
        public float EscapeSeparation = 1f;

        [Header("Results (runtime)")]
        public int CurrentCase = -1;
        public bool Pass;
        public bool Finished;

        // Per-case working state.
        float caseStart;
        float blockRun, turnRun;
        bool[] wasAvoiding;
        Vector2[] startPosition, previousPosition;
        float[] previousHeading;
        float[] arrivalTime;
        string[] orderEnd;
        // ForcedTurn.
        bool headingHeldWhileBlocked, sawTurnBlock, turnedAfterLastBlock;
        float lastBlockHeading, maxLateralSlip;
        // RestingEscape.
        int escapePhase;
        float contactTimer, contactSeparation = float.NaN, contactTurn, maxEscapeSeparation;
        float previousSeparation = float.PositiveInfinity;
        bool escapeMoved, escapeTurned, escapeTurnFromContact, escapeTurnRefused;
        float skin;
        string summary = "Running…";

        void Start()
        {
            foreach (Case c in Cases)
                if (c.Root != null) c.Root.SetActive(false);
            BeginCase(0);
        }

        void BeginCase(int index)
        {
            CurrentCase = index;
            if (index >= Cases.Length)
            {
                Finish();
                return;
            }

            Case c = Cases[index];
            c.Root.SetActive(true);
            int n = c.Orders.Length;
            wasAvoiding = new bool[n];
            startPosition = new Vector2[n];
            previousPosition = new Vector2[n];
            previousHeading = new float[n];
            arrivalTime = new float[n];
            orderEnd = new string[n];
            for (int i = 0; i < n; i++)
            {
                ShipMovement ship = c.Orders[i].Ship;
                startPosition[i] = previousPosition[i] = ship.Motor.State.Position;
                previousHeading[i] = ship.Motor.State.Heading;
                arrivalTime[i] = -1f;
            }
            blockRun = turnRun = 0f;
            headingHeldWhileBlocked = true;
            sawTurnBlock = turnedAfterLastBlock = false;
            maxLateralSlip = 0f;
            escapePhase = 0;
            contactTimer = contactTurn = maxEscapeSeparation = 0f;
            contactSeparation = float.NaN;
            previousSeparation = float.PositiveInfinity;
            escapeMoved = escapeTurned = escapeTurnFromContact = escapeTurnRefused = false;
            skin = c.Orders[0].Ship.Profile.CollisionSkin;

            if (c.Kind == CaseKind.RestingEscape)
                c.Orders[0].Ship.Orders.Move(c.ApproachDestination);
            else
                foreach (ShipOrder order in c.Orders)
                    if (order.Ordered) order.Ship.Orders.Move(order.Destination);

            caseStart = Time.fixedTime;
            Debug.Log($"[Footprint] case {c.Name} ({c.Kind}) started");
        }

        void FixedUpdate()
        {
            if (Finished || CurrentCase < 0 || CurrentCase >= Cases.Length) return;

            Case c = Cases[CurrentCase];
            float t = Time.fixedTime - caseStart;
            float dt = Time.fixedDeltaTime;

            CheckIsolation(c);

            // Separations this step.
            float capsule = float.PositiveInfinity, hull = float.PositiveInfinity;
            float capsuleObstacle = float.PositiveInfinity, hullObstacle = float.PositiveInfinity;
            bool anyBlocked = false, anyTurnBlocked = false;
            for (int i = 0; i < c.Orders.Length; i++)
            {
                ShipMovement a = c.Orders[i].Ship;
                for (int j = i + 1; j < c.Orders.Length; j++)
                {
                    ShipMovement b = c.Orders[j].Ship;
                    capsule = Mathf.Min(capsule, CapsuleSeparation(a, b));
                    hull = Mathf.Min(hull, HullSeparation(a.Motor.State, b.Motor.State));
                }
                if (c.Obstacle != null)
                {
                    Vector2 center = ShipMovementState.ToPlanar(c.Obstacle.position);
                    capsuleObstacle = Mathf.Min(capsuleObstacle, CapsuleToCircle(a, center, c.ObstacleRadius));
                    hullObstacle = Mathf.Min(hullObstacle, HullToCircle(a.Motor.State, center, c.ObstacleRadius));
                }
                anyBlocked |= a.Motor.Blocked;
                anyTurnBlocked |= a.Motor.TurnBlocked;
                if (a.Motor.Blocked) c.GuardBlockSteps++;
                if (a.Motor.TurnBlocked) c.TurnBlockSteps++;
                bool avoiding = a.Avoidance.Active;
                if (avoiding && !wasAvoiding[i]) c.AvoidanceActivations++;
                wasAvoiding[i] = avoiding;

                ShipMovementState state = a.Motor.State;
                Vector2 offset = state.Position - c.Origin;
                c.MaxExtent = Mathf.Max(c.MaxExtent, Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.y)) + a.Profile.FootprintHalfLength);
                RecordOrder(c, i, t);
            }
            c.MinCapsuleSeparation = Mathf.Min(c.MinCapsuleSeparation, capsule);
            c.MinHullSeparation = Mathf.Min(c.MinHullSeparation, hull);
            c.MinCapsuleObstacle = Mathf.Min(c.MinCapsuleObstacle, capsuleObstacle);
            c.MinHullObstacle = Mathf.Min(c.MinHullObstacle, hullObstacle);
            blockRun = anyBlocked ? blockRun + dt : 0f;
            turnRun = anyTurnBlocked ? turnRun + dt : 0f;
            c.MaxContinuousBlock = Mathf.Max(c.MaxContinuousBlock, blockRun);
            c.MaxContinuousTurnBlock = Mathf.Max(c.MaxContinuousTurnBlock, turnRun);

            if (c.Kind == CaseKind.ForcedTurn) StepForcedTurn(c);
            if (c.Kind == CaseKind.RestingEscape) StepRestingEscape(c, capsule, dt);

            Debug.Log($"[Footprint] {c.Name} t {t:F2} capsule {Format(capsule)} hull {Format(hull)} " +
                      $"capsuleObstacle {Format(capsuleObstacle)} hullObstacle {Format(hullObstacle)} " +
                      $"blocked {(anyBlocked ? 1 : 0)} turnBlocked {(anyTurnBlocked ? 1 : 0)}");

            for (int i = 0; i < c.Orders.Length; i++)
            {
                previousPosition[i] = c.Orders[i].Ship.Motor.State.Position;
                previousHeading[i] = c.Orders[i].Ship.Motor.State.Heading;
            }
            previousSeparation = capsule;

            if (CaseDone(c) || t > c.TimeLimit)
                EndCase(c);
        }

        // Every active ShipMovement must belong to the running case.
        void CheckIsolation(Case c)
        {
            foreach (ShipMovement ship in FindObjectsByType<ShipMovement>())
            {
                bool own = false;
                foreach (ShipOrder order in c.Orders)
                    own |= order.Ship == ship;
                if (!own) c.IsolationViolated = true;
            }
        }

        void RecordOrder(Case c, int i, float t)
        {
            ShipOrder order = c.Orders[i];
            if (orderEnd[i] != null) return;
            if (!order.Ordered && order.Ship.Orders.Kind != OrderKind.None)
                orderEnd[i] = $"unexpected {order.Ship.Orders.Kind}";
            else if (order.Ship.Planner.Status == RouteStatus.Failed)
                orderEnd[i] = "route failed";
            else if (order.Ship.Orders.Kind == OrderKind.Stop)
                orderEnd[i] = "stopped";
            else if (order.Ordered && order.Ship.Orders.Kind == OrderKind.None && EscapeOrderIssued(c, i))
            {
                arrivalTime[i] = t;
                orderEnd[i] = $"arrived {t:F2} s, {Vector2.Distance(order.Ship.Motor.State.Position, order.Destination):F2} u from target";
            }
        }

        // The RestingEscape ship's Destination is the escape order, issued only after contact.
        bool EscapeOrderIssued(Case c, int i) => c.Kind != CaseKind.RestingEscape || i != 0 || escapePhase == 1;

        // A failed route, an unexpected Stop or an order on the unordered ship is never an accepted limitation.
        bool Unexpected()
        {
            foreach (string end in orderEnd)
                if (end != null && !end.StartsWith("arrived")) return true;
            return false;
        }

        bool CaseDone(Case c)
        {
            for (int i = 0; i < c.Orders.Length; i++)
                if (c.Orders[i].Ordered && orderEnd[i] == null) return false;
            return true;
        }

        void StepForcedTurn(Case c)
        {
            ShipMovementState state = c.Orders[0].Ship.Motor.State;
            if (c.Orders[0].Ship.Motor.TurnBlocked)
            {
                sawTurnBlock = true;
                // The committed heading must be exactly the previous step's heading.
                if (state.Heading != previousHeading[0]) headingHeldWhileBlocked = false;
                lastBlockHeading = state.Heading;
                turnedAfterLastBlock = false;
            }
            else if (sawTurnBlock && Mathf.Abs(Mathf.DeltaAngle(state.Heading, lastBlockHeading)) > 1f)
            {
                turnedAfterLastBlock = true;
            }

            // Translation stays parallel to the committed heading.
            Vector2 displacement = state.Position - previousPosition[0];
            if (displacement.sqrMagnitude > 1e-8f)
            {
                Vector2 forward = state.Forward;
                maxLateralSlip = Mathf.Max(maxLateralSlip, Mathf.Abs(forward.x * displacement.y - forward.y * displacement.x));
            }
        }

        void StepRestingEscape(Case c, float separation, float dt)
        {
            ShipMovement ship = c.Orders[0].Ship;
            ShipMovementState state = ship.Motor.State;
            if (escapePhase == 0)
            {
                contactTimer = ship.Motor.Blocked && state.Speed <= StopSpeed ? contactTimer + dt : 0f;
                if (contactTimer >= ContactHoldTime)
                {
                    contactSeparation = separation;
                    escapePhase = 1;
                    ship.Orders.Move(c.Orders[0].Destination);
                    Debug.Log($"[Footprint] {c.Name} contact at separation {separation:F4} u; escape ordered");
                }
                return;
            }

            // Turning from contact (Issue #24 F6): the first heading change after the escape order starts within contact
            // distance, and no turn is refused while in contact.
            bool inContact = previousSeparation <= skin + 0.01f;
            float turned = Mathf.Abs(Mathf.DeltaAngle(state.Heading, previousHeading[0]));
            if (!escapeTurned && turned > 0f)
            {
                escapeTurned = true;
                escapeTurnFromContact = inContact;
            }
            if (inContact && ship.Motor.TurnBlocked) escapeTurnRefused = true;
            contactTurn += turned;
            if (state.Speed > StopSpeed && separation > EscapeSeparation)
                escapeMoved = true;
            if (!float.IsInfinity(separation))
                maxEscapeSeparation = Mathf.Max(maxEscapeSeparation, separation);
        }

        void EndCase(Case c)
        {
            var outcomes = new List<string>();
            for (int i = 0; i < c.Orders.Length; i++)
            {
                ShipOrder order = c.Orders[i];
                string end = orderEnd[i] ?? (order.Ordered ? $"not done ({order.Ship.Orders.Kind})" : "no orders");
                outcomes.Add($"{order.Ship.name} {end}");
            }
            c.Outcomes = string.Join("; ", outcomes);

            c.PassPhysical = c.MinCapsuleSeparation >= -PenetrationTolerance && c.MinCapsuleObstacle >= -PenetrationTolerance;
            c.PassVisual = c.MinHullSeparation >= 0f && c.MinHullObstacle >= 0f;
            c.PassRegion = c.MaxExtent <= RegionHalfSize && !c.IsolationViolated;
            c.PassKind = true;
            float elapsed = Time.fixedTime - caseStart;
            switch (c.Kind)
            {
                case CaseKind.ForcedTurn:
                    bool arrived = arrivalTime[0] >= 0f;
                    c.PassKind = sawTurnBlock && headingHeldWhileBlocked && maxLateralSlip <= MaxLateralSlip
                        && c.MaxContinuousTurnBlock <= MaxTurnBlockedTime && arrived && turnedAfterLastBlock;
                    c.Detail = $"(a) turn refused {Result(sawTurnBlock)} {c.TurnBlockSteps} steps; " +
                               $"(b) heading held {Result(headingHeldWhileBlocked)}; " +
                               $"(c) max lateral slip {maxLateralSlip:E2} u (≤ {MaxLateralSlip:E0}) {Result(maxLateralSlip <= MaxLateralSlip)}; " +
                               $"(e) longest refused turn {c.MaxContinuousTurnBlock:F2} s (≤ {MaxTurnBlockedTime}), turned after last block {turnedAfterLastBlock}, " +
                               $"arrived {arrived}";
                    break;
                case CaseKind.RestingEscape:
                    bool contact = !float.IsNaN(contactSeparation)
                        && contactSeparation >= -PenetrationTolerance && contactSeparation <= skin + 0.01f;
                    bool completed = arrivalTime[0] >= 0f;
                    bool stationary = Vector2.Distance(c.Orders[1].Ship.Motor.State.Position, startPosition[1]) <= 1e-4f
                        && orderEnd[1] == null;
                    bool turnFromContact = escapeTurnFromContact && !escapeTurnRefused;
                    c.PassKind = contact && turnFromContact && escapeMoved && completed && stationary;
                    c.Detail = $"(a) contact {Result(contact)} separation {contactSeparation:F4} u (0 … {skin + 0.01f:F2}); " +
                               $"(b) turn from contact {Result(turnFromContact)} first turn within contact {escapeTurnFromContact}, " +
                               $"refused in contact {escapeTurnRefused}, total turn {contactTurn:F1}°; " +
                               $"(c) moved away {Result(escapeMoved)} max separation {maxEscapeSeparation:F2} u; " +
                               $"(d) escape completed {Result(completed)}; (f) contact ship unmoved {Result(stationary)}";
                    break;
            }
            c.UnexpectedOrder = Unexpected();
            c.PassOrders = CaseDone(c) && !c.UnexpectedOrder;
            // Accepted limitations (Issue #24 F1–F3) report instead of failing; every other criterion stays required.
            bool visualOk = c.PassVisual || c.AcceptCornerOverlap;
            bool ordersOk = c.PassOrders || (c.AcceptStall && !c.UnexpectedOrder);
            c.KnownLimitation = (!c.PassVisual && c.AcceptCornerOverlap) || (!c.PassOrders && c.AcceptStall);
            c.Pass = c.PassPhysical && visualOk && c.PassRegion && ordersOk && c.PassKind;
            Debug.Log($"[Footprint] case {c.Name} ended after {elapsed:F2} s");

            c.Root.SetActive(false);
            BeginCase(CurrentCase + 1);
        }

        void Finish()
        {
            Finished = true;
            Pass = Cases.Length > 0;
            var text = new StringBuilder();
            for (int k = 0; k < Cases.Length; k++)
            {
                Case c = Cases[k];
                Pass &= c.Pass;
                text.AppendLine($"Case {k + 1} {c.Name}: {Result(c.Pass)}{(c.Pass && c.KnownLimitation ? " (KNOWN-LIMITATION)" : "")}");
                text.AppendLine($"    physical {Result(c.PassPhysical)} min capsule ship {Format(c.MinCapsuleSeparation)} u, obstacle {Format(c.MinCapsuleObstacle)} u (≥ −{PenetrationTolerance})");
                text.AppendLine($"    visual {Limited(c.PassVisual, c.AcceptCornerOverlap)} min hull ship {Format(c.MinHullSeparation)} u, obstacle {Format(c.MinHullObstacle)} u (≥ 0)");
                text.AppendLine($"    region {Result(c.PassRegion)} max extent {c.MaxExtent:F2} u (≤ {RegionHalfSize}), isolation violated {c.IsolationViolated}");
                text.AppendLine($"    guard blocks {c.GuardBlockSteps} steps (longest {c.MaxContinuousBlock:F2} s), turn blocks {c.TurnBlockSteps} steps " +
                                $"(longest {c.MaxContinuousTurnBlock:F2} s), avoidance activations {c.AvoidanceActivations}");
                text.AppendLine($"    orders {Limited(c.PassOrders, c.AcceptStall && !c.UnexpectedOrder)}: {c.Outcomes}");
                if (c.Kind != CaseKind.Traffic)
                    text.AppendLine($"    {c.Kind} {Result(c.PassKind)}: {c.Detail}");
            }
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();
            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        // ---- Independent planar geometry ----

        static float CapsuleSeparation(ShipMovement a, ShipMovement b)
        {
            Segment(a, out Vector2 a0, out Vector2 a1);
            Segment(b, out Vector2 b0, out Vector2 b1);
            return SegmentDistance(a0, a1, b0, b1) - a.Profile.FootprintRadius - b.Profile.FootprintRadius;
        }

        static float CapsuleToCircle(ShipMovement ship, Vector2 center, float radius)
        {
            Segment(ship, out Vector2 s0, out Vector2 s1);
            return PointToSegment(center, s0, s1) - ship.Profile.FootprintRadius - radius;
        }

        static void Segment(ShipMovement ship, out Vector2 front, out Vector2 back)
        {
            ShipMovementState state = ship.Motor.State;
            Vector2 axis = state.Forward * ship.Profile.FootprintHalfSegment;
            front = state.Position + axis;
            back = state.Position - axis;
        }

        static float SegmentDistance(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
        {
            Vector2 a = a1 - a0, b = b1 - b0;
            float d0 = Cross(b, a0 - b0), d1 = Cross(b, a1 - b0), d2 = Cross(a, b0 - a0), d3 = Cross(a, b1 - a0);
            if (d0 * d1 < 0f && d2 * d3 < 0f) return 0f;
            return Mathf.Min(Mathf.Min(PointToSegment(a0, b0, b1), PointToSegment(a1, b0, b1)),
                Mathf.Min(PointToSegment(b0, a0, a1), PointToSegment(b1, a0, a1)));
        }

        static float PointToSegment(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 s = s1 - s0;
            float lengthSquared = Vector2.Dot(s, s);
            float t = lengthSquared < 1e-12f ? 0f : Mathf.Clamp01(Vector2.Dot(p - s0, s) / lengthSquared);
            return Vector2.Distance(p, s0 + s * t);
        }

        static float Cross(Vector2 u, Vector2 w) => u.x * w.y - u.y * w.x;

        // Corners of the hull rectangle, counter-clockwise seen from above.
        void Corners(ShipMovementState state, Vector2[] corners)
        {
            Vector2 forward = state.Forward * HullHalfLength;
            Vector2 right = new Vector2(state.Forward.y, -state.Forward.x) * HullHalfWidth;
            corners[0] = state.Position + forward + right;
            corners[1] = state.Position + forward - right;
            corners[2] = state.Position - forward - right;
            corners[3] = state.Position - forward + right;
        }

        readonly Vector2[] cornersA = new Vector2[4], cornersB = new Vector2[4];

        // Signed distance between two hull rectangles: the gap when apart, minus the smallest axis overlap (SAT) when overlapping.
        float HullSeparation(ShipMovementState a, ShipMovementState b)
        {
            Corners(a, cornersA);
            Corners(b, cornersB);
            float penetration = float.PositiveInfinity;
            bool separated = false;
            Vector2[] axes = { a.Forward, new Vector2(a.Forward.y, -a.Forward.x), b.Forward, new Vector2(b.Forward.y, -b.Forward.x) };
            foreach (Vector2 axis in axes)
            {
                Project(cornersA, axis, out float minA, out float maxA);
                Project(cornersB, axis, out float minB, out float maxB);
                float overlap = Mathf.Min(maxA, maxB) - Mathf.Max(minA, minB);
                if (overlap <= 0f) separated = true;
                penetration = Mathf.Min(penetration, overlap);
            }
            if (!separated) return -penetration;

            float gap = float.PositiveInfinity;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    gap = Mathf.Min(gap, PointToSegment(cornersA[i], cornersB[j], cornersB[(j + 1) % 4]));
                    gap = Mathf.Min(gap, PointToSegment(cornersB[i], cornersA[j], cornersA[(j + 1) % 4]));
                }
            return gap;
        }

        static void Project(Vector2[] corners, Vector2 axis, out float min, out float max)
        {
            min = max = Vector2.Dot(corners[0], axis);
            for (int i = 1; i < corners.Length; i++)
            {
                float value = Vector2.Dot(corners[i], axis);
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }
        }

        // Signed distance from the hull rectangle to a circle.
        float HullToCircle(ShipMovementState state, Vector2 center, float radius)
        {
            Vector2 d = center - state.Position;
            Vector2 forward = state.Forward;
            float along = Mathf.Abs(Vector2.Dot(d, forward));
            float across = Mathf.Abs(forward.x * d.y - forward.y * d.x);
            float ox = across - HullHalfWidth, oz = along - HullHalfLength;
            float outside = new Vector2(Mathf.Max(ox, 0f), Mathf.Max(oz, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(ox, oz), 0f);
            return outside + inside - radius;
        }

        static string Format(float value) => float.IsInfinity(value) ? "-" : value.ToString("F4");

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        static string Limited(bool pass, bool accepted) => pass ? "PASS" : accepted ? "KNOWN-LIMITATION" : "FAIL";

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1400f, 900f), summary);
                return;
            }
            if (CurrentCase < 0 || CurrentCase >= Cases.Length) return;
            Case c = Cases[CurrentCase];
            GUI.Label(new Rect(10f, 10f, 1400f, 300f),
                $"Case {CurrentCase + 1}/{Cases.Length} {c.Name}  t {Time.fixedTime - caseStart:F1} s  " +
                $"min capsule {Format(c.MinCapsuleSeparation)} u  min hull {Format(c.MinHullSeparation)} u  " +
                $"turn blocks {c.TurnBlockSteps}  guard blocks {c.GuardBlockSteps}");
        }
    }
}
