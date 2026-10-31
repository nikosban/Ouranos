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
    /// Issue #11 acceptance scenario. Observes selection and Ship A/B orders while the player (or, with
    /// <see cref="ScriptedInput"/>, virtual Input System devices) performs five steps: select A, move to marker 1,
    /// replace with marker 2 while moving, stop, clear. Expected destinations come from the marker transforms,
    /// never from <see cref="PlayerCommands"/>. Results show in the Inspector, on screen and as one log summary.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class PlayerControlScenario : MonoBehaviour
    {
        enum Step { Select, Move, Replace, Stop, Clear, Done }

        [Header("Setup")]
        public ShipSelection Selection;
        [Tooltip("Supplies the camera for the scripted driver only.")]
        public PlayerCommands Commands;
        public ShipMovement ShipA;
        public ShipMovement ShipB;
        public Transform Marker1;
        public Transform Marker2;
        [Tooltip("Acceptance verification only: drive the real bindings with virtual mouse/keyboard events. Keep off in the committed scene.")]
        public bool ScriptedInput;
        [Tooltip("Scripted driver: empty ground point clicked for the clear step (XZ).")]
        public Vector2 ScriptedEmptyPoint = new Vector2(0f, 28f);

        [Header("Criteria")]
        [Tooltip("Authoritative destination tolerance for scripted input (u).")]
        public float ScriptedTolerance = 0.1f;
        [Tooltip("Supplementary destination tolerance for manual clicks: the marker radius (u).")]
        public float ManualTolerance = 1f;
        public float MovingSpeed = 0.1f;
        public float StopSpeed = 0.1f;
        public float StopTimeLimit = 4f;
        [Tooltip("Scripted runs only: fail any step not reached within this time (s).")]
        public float ScriptedTimeLimit = 30f;

        [Header("Results (recorded at runtime)")]
        public float MoveError = -1f;
        public float ReplaceError = -1f;
        public float ReplaceSpeed = -1f;
        public float StopSpeedAtCommand = -1f;
        public float StopTime = -1f;
        public int ShipBOrders;
        public bool PassSelect, PassMove, PassReplace, PassStop, PassClear, PassShipB, Pass;
        public bool Finished;

        Step step;
        OrderKind lastKind;
        Vector2 lastDestination;
        float stopIssuedAt, startTime;
        string selectNote = "", clearNote = "";
        string summary = "";
        Mouse scriptedMouse;
        Keyboard scriptedKeyboard;

        float Tolerance => ScriptedInput ? ScriptedTolerance : ManualTolerance;
        string Mode => ScriptedInput ? "scripted" : "manual";

        void Start()
        {
            lastKind = ShipA.Orders.Kind;
            lastDestination = ShipA.Orders.Destination;
            startTime = Time.time;
            if (ScriptedInput)
                StartCoroutine(Drive());
        }

        void OnDestroy()
        {
            if (scriptedMouse != null) InputSystem.RemoveDevice(scriptedMouse);
            if (scriptedKeyboard != null) InputSystem.RemoveDevice(scriptedKeyboard);
        }

        // Runs after input callbacks of the same frame, so the speed read here is the speed when the order arrived.
        void Update()
        {
            if (Finished) return;

            if (ShipB.Orders.Kind != OrderKind.None) ShipBOrders++;

            MovementOrders orders = ShipA.Orders;
            bool orderChanged = orders.Kind != lastKind || orders.Destination != lastDestination;
            lastKind = orders.Kind;
            lastDestination = orders.Destination;
            float speed = ShipA.Motor.State.Speed;

            switch (step)
            {
                case Step.Select:
                    if (orderChanged) Fail("an order reached A before selection");
                    else if (Selection.Selected.Count > 0)
                    {
                        PassSelect = Selection.Selected.Count == 1 && Selection.Selected[0] == ShipA;
                        selectNote = $"{Selection.Selected.Count} selected ({Selection.Selected[0].name})";
                        step = Step.Move;
                    }
                    break;

                case Step.Move:
                    if (orderChanged)
                    {
                        MoveError = Vector2.Distance(orders.Destination, Planar(Marker1));
                        PassMove = orders.Kind == OrderKind.Move && MoveError <= Tolerance;
                        step = Step.Replace;
                    }
                    break;

                case Step.Replace:
                    if (orderChanged)
                    {
                        ReplaceError = Vector2.Distance(orders.Destination, Planar(Marker2));
                        ReplaceSpeed = speed;
                        PassReplace = orders.Kind == OrderKind.Move && ReplaceError <= Tolerance
                            && Vector2.Distance(orders.Destination, Planar(Marker1)) > 1f && speed > MovingSpeed;
                        step = Step.Stop;
                    }
                    break;

                case Step.Stop:
                    if (orderChanged && stopIssuedAt <= 0f)
                    {
                        // The marker-2 order must hold until S; arrival or any other order here fails the step.
                        StopSpeedAtCommand = speed;
                        stopIssuedAt = Time.time;
                        PassStop = orders.Kind == OrderKind.Stop && speed > MovingSpeed;
                    }
                    else if (stopIssuedAt > 0f)
                    {
                        if (orderChanged) PassStop = false;
                        float elapsed = Time.time - stopIssuedAt;
                        if (speed <= StopSpeed)
                        {
                            StopTime = elapsed;
                            step = Step.Clear;
                        }
                        else if (elapsed > StopTimeLimit)
                        {
                            PassStop = false;
                            step = Step.Clear;
                        }
                    }
                    break;

                case Step.Clear:
                    if (orderChanged) PassClear = false;
                    if (Selection.Selected.Count != 1 || Selection.Selected[0] != ShipA)
                    {
                        PassClear = !orderChanged && Selection.Selected.Count == 0 && ShipA.Orders.Kind == OrderKind.Stop;
                        clearNote = $"{Selection.Selected.Count} selected, A order {ShipA.Orders.Kind}";
                        step = Step.Done;
                    }
                    break;

                case Step.Done:
                    Finish();
                    break;
            }

            if (ScriptedInput && Time.time - startTime > ScriptedTimeLimit && !Finished)
                Fail($"timed out in step {step}");
        }

        void Fail(string reason)
        {
            Debug.LogWarning($"[Acceptance] {reason}", this);
            Finish();
        }

        void Finish()
        {
            Finished = true;
            PassStop &= StopTime >= 0f && StopTime <= StopTimeLimit;
            PassShipB = ShipBOrders == 0 && ShipB.Orders.Kind == OrderKind.None;
            Pass = PassSelect && PassMove && PassReplace && PassStop && PassClear && PassShipB;

            var text = new StringBuilder();
            text.AppendLine($"Mode: {Mode}, destination tolerance {Tolerance} u" +
                            (ScriptedInput ? " (authoritative)" : " (supplementary manual check)"));
            text.AppendLine($"(1) Select:  {Result(PassSelect)} {selectNote} (= 1, Ship A)");
            text.AppendLine($"(2) Move:    {Result(PassMove)} destination {MoveError:F4} u from marker 1 (≤ {Tolerance})");
            text.AppendLine($"(3) Replace: {Result(PassReplace)} destination {ReplaceError:F4} u from marker 2 (≤ {Tolerance}), " +
                            $"issued at {ReplaceSpeed:F2} u/s (> {MovingSpeed})");
            text.AppendLine($"(4) Stop:    {Result(PassStop)} issued at {StopSpeedAtCommand:F2} u/s, " +
                            $"≤ {StopSpeed} u/s after {StopTime:F2} s (≤ {StopTimeLimit})");
            text.AppendLine($"(5) Clear:   {Result(PassClear)} {clearNote}");
            text.AppendLine($"(6) Ship B:  {Result(PassShipB)} frames with an order {ShipBOrders}, final {ShipB.Orders.Kind}");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static Vector2 Planar(Transform marker) => ShipMovementState.ToPlanar(marker.position);

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        // Acceptance-only driver: queues events on virtual devices so the real bindings and PlayerCommands run unchanged.
        IEnumerator Drive()
        {
            scriptedMouse = InputSystem.AddDevice<Mouse>("ScriptedMouse");
            scriptedKeyboard = InputSystem.AddDevice<Keyboard>("ScriptedKeyboard");
            yield return new WaitForSeconds(1f);

            yield return Click(MouseButton.Left, ShipA.transform.position);
            yield return new WaitForSeconds(0.5f);
            yield return Click(MouseButton.Right, Marker1.position);
            while (ShipA.Motor.State.Speed < 2f) yield return null;
            yield return Click(MouseButton.Right, Marker2.position);
            yield return new WaitForSeconds(1.5f);
            yield return Press(Key.C);
            while (step == Step.Stop) yield return null;
            yield return Click(MouseButton.Left, ShipMovementState.ToWorld(ScriptedEmptyPoint));
        }

        IEnumerator Click(MouseButton button, Vector3 world)
        {
            Vector2 screen = Commands.Camera.WorldToScreenPoint(world);
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen }.WithButton(button, true));
            yield return null;
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen });
            yield return null;
        }

        IEnumerator Press(Key key)
        {
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(key));
            yield return null;
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
        }

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1200f, 300f), summary);
                return;
            }
            string prompt = step switch
            {
                Step.Select => "1. Left-click Ship A",
                Step.Move => "2. Right-click marker 1",
                Step.Replace => "3. While A moves, right-click marker 2",
                Step.Stop => "4. While A moves, press C",
                _ => "5. Left-click empty ground",
            };
            GUI.Label(new Rect(10f, 10f, 1200f, 60f),
                $"{Mode} — {prompt}   A {ShipA.Motor.State.Speed:F2} u/s {ShipA.Orders.Kind}   selected {Selection.Selected.Count}");
        }
    }
}
