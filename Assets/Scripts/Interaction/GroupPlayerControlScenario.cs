using System.Collections;
using System.Text;
using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Ouranos.Interaction
{
    /// <summary>
    /// Issue #13 acceptance scenario. Observes selection and the orders of Ships A, B and C while the player (or, with
    /// <see cref="ScriptedInput"/>, virtual Input System devices) drags a box around all three, right-clicks the marker,
    /// Shift-clicks B while moving and presses C. Expected destinations are computed here from the ships' positions
    /// before the command and the marker transform, never by calling <see cref="GroupMoveDispatcher"/>.
    /// Results show in the Inspector, on screen and as one log summary.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class GroupPlayerControlScenario : MonoBehaviour
    {
        enum Step { Select, Move, Deselect, Stop, ObserveB, Done }

        [Header("Setup")]
        public ShipSelection Selection;
        [Tooltip("Supplies the camera for the scripted driver only.")]
        public PlayerCommands Commands;
        public ShipMovement ShipA;
        public ShipMovement ShipB;
        public ShipMovement ShipC;
        public Transform Marker;
        [Tooltip("Acceptance verification only: drive the real bindings with virtual mouse/keyboard events. Keep off in the committed scene.")]
        public bool ScriptedInput;

        [Header("Criteria")]
        [Tooltip("Authoritative anchor tolerance for scripted input (u).")]
        public float ScriptedTolerance = 0.01f;
        [Tooltip("Supplementary anchor tolerance for manual clicks: the marker radius (u).")]
        public float ManualTolerance = 1f;
        [Tooltip("Rigid-translation tolerance of each destination, both modes (u).")]
        public float TranslationTolerance = 0.01f;
        public float MovingSpeed = 0.1f;
        public float StopSpeed = 0.1f;
        public float StopTimeLimit = 4f;
        [Tooltip("B may complete its move naturally within this distance beyond its ArrivalRadius (u).")]
        public float ArrivalSlack = 0.05f;
        [Tooltip("After the stop, B is observed until its order completes or this time passes (s).")]
        public float ObserveBTime = 20f;
        [Tooltip("Scripted runs only: fail when not finished within this time (s).")]
        public float ScriptedTimeLimit = 60f;

        [Header("Results (recorded at runtime)")]
        public int SelectedAfterBox;
        public Vector2 AssignedCentroid;
        public float CentroidError = -1f;
        public float MaxTranslationError = -1f;
        public float DeselectSpeed = -1f;
        public float StopSpeedA = -1f, StopSpeedC = -1f;
        public float StopTimeA = -1f, StopTimeC = -1f;
        public Vector2 BDestination;
        public string BOutcome = "";
        public string UnexpectedOrders = "";
        public bool PassSelect, PassMove, PassDeselect, PassStop, PassShipB, PassNoUnexpected, Pass;
        public bool Finished;

        Step step;
        ShipMovement[] ships;
        OrderKind[] lastKind;
        Vector2[] lastDestination;
        Vector2[] previousPosition;
        float stopIssuedAt = -1f, startTime;
        bool bFailed;
        string summary = "";
        Mouse scriptedMouse;
        Keyboard scriptedKeyboard;

        float Tolerance => ScriptedInput ? ScriptedTolerance : ManualTolerance;
        string Mode => ScriptedInput ? "scripted" : "manual";

        void Start()
        {
            ships = new[] { ShipA, ShipB, ShipC };
            lastKind = new OrderKind[3];
            lastDestination = new Vector2[3];
            previousPosition = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                lastKind[i] = ships[i].Orders.Kind;
                lastDestination[i] = ships[i].Orders.Destination;
                previousPosition[i] = ships[i].Motor.State.Position;
            }
            PassNoUnexpected = true;
            startTime = Time.time;
            if (ScriptedInput)
                StartCoroutine(Drive());
        }

        void OnDestroy()
        {
            if (scriptedMouse != null) InputSystem.RemoveDevice(scriptedMouse);
            if (scriptedKeyboard != null) InputSystem.RemoveDevice(scriptedKeyboard);
        }

        // Runs after input callbacks of the same frame, so order changes are seen in the frame they were issued.
        void Update()
        {
            if (Finished) return;

            bool[] changed = new bool[3];
            for (int i = 0; i < 3; i++)
            {
                MovementOrders orders = ships[i].Orders;
                changed[i] = orders.Kind != lastKind[i] || orders.Destination != lastDestination[i];
            }
            bool any = changed[0] || changed[1] || changed[2];

            if (step >= Step.Deselect) CheckShipB(changed[1]);

            switch (step)
            {
                case Step.Select:
                    if (any) Unexpected("an order arrived before the group command", changed);
                    if (Selection.Selected.Count == 3 && IsSelected(ShipA) && IsSelected(ShipB) && IsSelected(ShipC))
                    {
                        SelectedAfterBox = Selection.Selected.Count;
                        PassSelect = true;
                        step = Step.Move;
                    }
                    break;

                case Step.Move:
                    if (any) EvaluateMove(changed);
                    break;

                case Step.Deselect:
                    if (changed[0] || changed[2]) Unexpected("an order arrived while deselecting B", changed);
                    if (Selection.Selected.Count != 3)
                    {
                        DeselectSpeed = ShipB.Motor.State.Speed;
                        PassDeselect = Selection.Selected.Count == 2 && IsSelected(ShipA) && IsSelected(ShipC) && !IsSelected(ShipB)
                                       && DeselectSpeed > MovingSpeed;
                        step = Step.Stop;
                    }
                    break;

                case Step.Stop:
                    if (stopIssuedAt < 0f)
                    {
                        if (changed[0] || changed[2])
                        {
                            stopIssuedAt = Time.time;
                            StopSpeedA = ShipA.Motor.State.Speed;
                            StopSpeedC = ShipC.Motor.State.Speed;
                            PassStop = ShipA.Orders.Kind == OrderKind.Stop && ShipC.Orders.Kind == OrderKind.Stop;
                        }
                    }
                    else
                    {
                        if (changed[0] || changed[2]) Unexpected("an order arrived after the stop", changed);
                        float elapsed = Time.time - stopIssuedAt;
                        if (StopTimeA < 0f && ShipA.Motor.State.Speed <= StopSpeed) StopTimeA = elapsed;
                        if (StopTimeC < 0f && ShipC.Motor.State.Speed <= StopSpeed) StopTimeC = elapsed;
                        if ((StopTimeA >= 0f && StopTimeC >= 0f) || elapsed > StopTimeLimit)
                            step = Step.ObserveB;
                    }
                    break;

                case Step.ObserveB:
                    if (changed[0] || changed[2]) Unexpected("an order arrived after the stop", changed);
                    if (ShipB.Orders.Kind != OrderKind.Move || Time.time - stopIssuedAt > ObserveBTime)
                        step = Step.Done;
                    break;

                case Step.Done:
                    Finish();
                    break;
            }

            for (int i = 0; i < 3; i++)
            {
                lastKind[i] = ships[i].Orders.Kind;
                lastDestination[i] = ships[i].Orders.Destination;
            }

            if (ScriptedInput && Time.time - startTime > ScriptedTimeLimit && !Finished)
                Fail($"timed out in step {step}");
        }

        // Positions are captured at the end of every frame; the ships are idle until the command, so the previous
        // frame's positions are the positions the command translated.
        void LateUpdate()
        {
            if (Finished || ships == null) return;
            if (step <= Step.Move)
                for (int i = 0; i < 3; i++)
                    previousPosition[i] = ships[i].Motor.State.Position;
        }

        void EvaluateMove(bool[] changed)
        {
            bool allMove = true;
            Vector2 startCentroid = Vector2.zero, destinationCentroid = Vector2.zero;
            for (int i = 0; i < 3; i++)
            {
                allMove &= changed[i] && ships[i].Orders.Kind == OrderKind.Move;
                startCentroid += previousPosition[i] / 3f;
                destinationCentroid += ships[i].Orders.Destination / 3f;
            }
            AssignedCentroid = destinationCentroid;
            CentroidError = Vector2.Distance(destinationCentroid, Planar(Marker));
            Vector2 delta = destinationCentroid - startCentroid;
            MaxTranslationError = 0f;
            for (int i = 0; i < 3; i++)
                MaxTranslationError = Mathf.Max(MaxTranslationError,
                    Vector2.Distance(ships[i].Orders.Destination, previousPosition[i] + delta));

            PassMove = allMove && CentroidError <= Tolerance && MaxTranslationError <= TranslationTolerance;
            BDestination = ShipB.Orders.Destination;
            step = Step.Deselect;
        }

        // After B leaves the selection it may only keep its Move or complete it naturally at its destination.
        void CheckShipB(bool changed)
        {
            if (!changed || bFailed) return;
            MovementOrders orders = ShipB.Orders;
            string violation = null;
            if (orders.Kind == OrderKind.Stop) violation = "received Stop";
            else if (orders.Destination != BDestination) violation = $"destination changed to {orders.Destination}";
            else if (orders.Kind == OrderKind.Move && lastKind[1] == OrderKind.None) violation = "Move restarted after completion";
            else if (orders.Kind == OrderKind.None)
            {
                float arrival = (ShipB.Profile != null ? ShipB.Profile.ArrivalRadius : 0f) + ArrivalSlack;
                float distance = Vector2.Distance(ShipB.Motor.State.Position, BDestination);
                if (distance <= arrival) BOutcome = $"completed naturally {distance:F3} u from its destination (≤ {arrival:F2})";
                else violation = $"order cleared {distance:F3} u from its destination (> {arrival:F2})";
            }

            if (violation != null)
            {
                BOutcome = violation;
                bFailed = true;
            }
        }

        void Unexpected(string reason, bool[] changed)
        {
            PassNoUnexpected = false;
            for (int i = 0; i < 3; i++)
                if (changed[i])
                    UnexpectedOrders += $"{ships[i].name}: {reason} ({ships[i].Orders.Kind}); ";
        }

        bool IsSelected(ShipMovement ship)
        {
            foreach (ShipMovement selected in Selection.Selected)
                if (selected == ship) return true;
            return false;
        }

        void Fail(string reason)
        {
            Debug.LogWarning($"[Acceptance] {reason}", this);
            Finish();
        }

        void Finish()
        {
            Finished = true;
            PassStop &= StopTimeA >= 0f && StopTimeA <= StopTimeLimit && StopTimeC >= 0f && StopTimeC <= StopTimeLimit
                        && StopSpeedA > MovingSpeed && StopSpeedC > MovingSpeed;
            PassShipB = PassMove && PassDeselect && !bFailed;
            if (BOutcome.Length == 0) BOutcome = $"still Move to its assigned destination ({ShipB.Orders.Kind})";
            Pass = PassSelect && PassMove && PassDeselect && PassStop && PassShipB && PassNoUnexpected;

            var text = new StringBuilder();
            text.AppendLine($"Mode: {Mode}, anchor tolerance {Tolerance} u" +
                            (ScriptedInput ? " (authoritative)" : " (supplementary manual check)") +
                            $", translation tolerance {TranslationTolerance} u");
            text.AppendLine($"(1) Box select: {Result(PassSelect)} {SelectedAfterBox} selected (= 3: A, B, C)");
            text.AppendLine($"(2) Group move: {Result(PassMove)} assigned centroid {AssignedCentroid} {CentroidError:F4} u from marker (≤ {Tolerance}), " +
                            $"max translation error {MaxTranslationError:F4} u (≤ {TranslationTolerance})");
            foreach (ShipMovement ship in ships)
                text.AppendLine($"    {ship.name}: destination {ship.Orders.Destination}");
            text.AppendLine($"(3) Shift-click B: {Result(PassDeselect)} {Selection.Selected.Count} selected (A, C), B at {DeselectSpeed:F2} u/s (> {MovingSpeed})");
            text.AppendLine($"(4) Stop (C):   {Result(PassStop)} issued at A {StopSpeedA:F2} / C {StopSpeedC:F2} u/s, " +
                            $"≤ {StopSpeed} u/s after A {StopTimeA:F2} s / C {StopTimeC:F2} s (≤ {StopTimeLimit})");
            text.AppendLine($"(5) Ship B:     {Result(PassShipB)} assigned {BDestination}; {BOutcome}");
            text.AppendLine($"(6) No unintended orders: {Result(PassNoUnexpected)} {UnexpectedOrders}");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static Vector2 Planar(Transform marker) => ShipMovementState.ToPlanar(marker.position);

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        // Acceptance-only driver: queues events on virtual devices so the real bindings, PlayerCommands and the
        // camera controller run unchanged. It never presses S.
        IEnumerator Drive()
        {
            scriptedMouse = InputSystem.AddDevice<Mouse>("ScriptedMouse");
            scriptedKeyboard = InputSystem.AddDevice<Keyboard>("ScriptedKeyboard");
            yield return new WaitForSeconds(1f);

            // Change camera yaw and zoom before selecting.
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(Key.E));
            yield return new WaitForSeconds(0.2f);
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { scroll = new Vector2(0f, ScrollNotch(-1f)) });
            yield return new WaitForSeconds(0.5f);

            // Drag backwards (from the upper right to the lower left of the ships' screen bounds).
            Rect bounds = ScreenBounds(40f);
            yield return Drag(new Vector2(bounds.xMax, bounds.yMax), new Vector2(bounds.xMin, bounds.yMin), 10);
            yield return new WaitForSeconds(0.5f);

            yield return Click(MouseButton.Right, ShipMovementState.ToWorld(Planar(Marker)));
            while (ShipA.Motor.State.Speed < 2f || ShipB.Motor.State.Speed < 2f) yield return null;

            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(Key.LeftShift));
            yield return null;
            yield return Click(MouseButton.Left, ShipMovementState.ToWorld(ShipB.Motor.State.Position));
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return new WaitForSeconds(0.5f);

            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(Key.C));
            yield return null;
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
        }

        static float ScrollNotch(float direction) =>
            direction * (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange ? 120f : 1f);

        Rect ScreenBounds(float margin)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (ShipMovement ship in new[] { ShipA, ShipB, ShipC })
            {
                Vector2 screen = Commands.Camera.WorldToScreenPoint(ShipMovementState.ToWorld(ship.Motor.State.Position));
                min = Vector2.Min(min, screen);
                max = Vector2.Max(max, screen);
            }
            return Rect.MinMaxRect(min.x - margin, min.y - margin, max.x + margin, max.y + margin);
        }

        IEnumerator Drag(Vector2 from, Vector2 to, int frames)
        {
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = from }.WithButton(MouseButton.Left, true));
            yield return null;
            for (int i = 1; i <= frames; i++)
            {
                InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = Vector2.Lerp(from, to, i / (float)frames) }.WithButton(MouseButton.Left, true));
                yield return null;
            }
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = to });
            yield return null;
        }

        IEnumerator Click(MouseButton button, Vector3 world)
        {
            Vector2 screen = Commands.Camera.WorldToScreenPoint(world);
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen }.WithButton(button, true));
            yield return null;
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen });
            yield return null;
        }

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1400f, 300f), summary);
                return;
            }
            string prompt = step switch
            {
                Step.Select => "1. Drag a box around Ships A, B and C (yaw/zoom the camera first if you like)",
                Step.Move => "2. Right-click the marker",
                Step.Deselect => "3. While the ships move, Shift-click Ship B",
                Step.Stop => "4. While A and C move, press C",
                _ => "5. Wait while Ship B is observed",
            };
            GUI.Label(new Rect(10f, 10f, 1400f, 60f),
                $"{Mode} — {prompt}   selected {Selection.Selected.Count}   A {ShipA.Orders.Kind}  B {ShipB.Orders.Kind}  C {ShipC.Orders.Kind}");
        }
    }
}
