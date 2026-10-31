using System.Collections;
using System.Collections.Generic;
using System.Text;
using Ouranos.Interaction;
using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Ouranos.Cameras
{
    /// <summary>
    /// Issue #14 acceptance scenario. Runs after the camera and the order feedback each frame and measures them
    /// independently: framing through <see cref="Camera.WorldToViewportPoint(Vector3)"/>, follow through the
    /// viewport-center ground point against a centroid computed here, destination circles from their LineRenderer
    /// points, and every ship's orders. The player (or, with <see cref="ScriptedInput"/>, virtual devices on the real
    /// bindings) box-selects A/B/C, navigates to the marker and right-clicks it, zooms in, presses F, yaws and zooms
    /// while following, presses W, presses F again and clicks empty ground. Results show in the Inspector, on screen
    /// and as one log summary.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class TacticalCameraSelectionScenario : MonoBehaviour
    {
        enum Step { Select, Command, Focus, Follow, Cancel, Reacquire, Deselect, Done }

        [Header("Setup")]
        public TacticalCameraController Controller;
        public ShipSelection Selection;
        [Tooltip("Supplies the camera for the scripted driver only.")]
        public PlayerCommands Commands;
        public OrderFeedback Feedback;
        public ShipMovement ShipA;
        public ShipMovement ShipB;
        public ShipMovement ShipC;
        public Transform Marker;
        [Tooltip("Acceptance verification only: drive the real bindings with virtual mouse/keyboard events. Keep off in the committed scene.")]
        public bool ScriptedInput;
        [Tooltip("Scripted driver: zoom in to at most this distance before pressing F (u).")]
        public float PreFocusDistance = 30f;

        [Header("Criteria")]
        public float PitchTolerance = 0.1f;
        [Tooltip("Follow: viewport-center ground point to selected centroid (u).")]
        public float FollowTolerance = 1f;
        public float FollowWindow = 10f;
        [Tooltip("Destination circle center to Orders.Destination (u).")]
        public float MarkerTolerance = 0.1f;
        [Tooltip("Allowed frame-to-frame focal change on deselection beyond the previous frame's change (u).")]
        public float JumpTolerance = 0.01f;
        public float MovingSpeed = 0.1f;
        [Tooltip("Scripted runs only: fail when not finished within this time (s).")]
        public float ScriptedTimeLimit = 90f;

        [Header("Results (recorded at runtime)")]
        public float FocusDistanceBefore = -1f, FocusDistanceAfter = -1f;
        public float FocusMaxExtent = -1f;
        public float FocusPitchError = -1f;
        public float MinSpeedAtFocus = -1f;
        public float MaxFollowError = -1f;
        public float FollowTime;
        public float MaxPitchError;
        public float MaxMarkerError = -1f;
        public int MarkerChecks;
        public float CancelPanDistance;
        public float ReacquireError = -1f;
        public float DeselectJump = -1f, DeselectPreviousStep = -1f;
        public int MarkersAfterDeselect = -1;
        public string UnexpectedOrders = "";
        public bool PassSelect, PassCommand, PassFocus, PassFollow, PassControls, PassCancel, PassReacquire,
                    PassMarkers, PassDeselect, PassNoOrders, Pass;
        public bool Finished;

        Step step;
        ShipMovement[] ships;
        OrderKind[] lastKind;
        Vector2[] lastDestination;
        Camera view;
        bool lastFollowing, zoomSeen, keyYawSeen, dragYawSeen, followBroken;
        float lastDistance, lastYaw, focusTime, startTime;
        Vector2 lastFocal, previousFocalStep, cancelStartFocal;
        readonly List<LineRenderer> circles = new List<LineRenderer>();
        string summary = "";
        Mouse scriptedMouse;
        Keyboard scriptedKeyboard;

        string Mode => ScriptedInput ? "scripted" : "manual";

        void Start()
        {
            ships = new[] { ShipA, ShipB, ShipC };
            lastKind = new OrderKind[3];
            lastDestination = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                lastKind[i] = ships[i].Orders.Kind;
                lastDestination[i] = ships[i].Orders.Destination;
            }
            view = Controller.GetComponent<Camera>();
            lastFocal = Controller.FocalPoint;
            lastDistance = Controller.Distance;
            lastYaw = Controller.Yaw;
            PassNoOrders = true;
            startTime = Time.time;
            if (ScriptedInput)
                StartCoroutine(Drive());
        }

        void OnDestroy()
        {
            if (scriptedMouse != null) InputSystem.RemoveDevice(scriptedMouse);
            if (scriptedKeyboard != null) InputSystem.RemoveDevice(scriptedKeyboard);
        }

        // Execution order 100: the camera, selection rings and order feedback have all updated this frame.
        void LateUpdate()
        {
            if (Finished || ships == null) return;

            bool following = Controller.Following;
            Vector2 focal = GroundCenter();
            float distance = Controller.Distance, yaw = Controller.Yaw;
            MaxPitchError = Mathf.Max(MaxPitchError, Mathf.Abs(Mathf.DeltaAngle(Controller.transform.eulerAngles.x, TacticalCameraController.PitchDegrees)));
            CheckOrders();
            if (step >= Step.Focus && step <= Step.Deselect) CheckMarkers();

            switch (step)
            {
                case Step.Select:
                    if (Selection.Selected.Count == 3 && IsSelected(ShipA) && IsSelected(ShipB) && IsSelected(ShipC))
                    {
                        PassSelect = true;
                        step = Step.Command;
                    }
                    break;

                case Step.Command:
                    if (ships[0].Orders.Kind == OrderKind.Move && ships[1].Orders.Kind == OrderKind.Move && ships[2].Orders.Kind == OrderKind.Move)
                    {
                        PassCommand = true;
                        step = Step.Focus;
                    }
                    break;

                case Step.Focus:
                    if (following && !lastFollowing)
                    {
                        EvaluateFocus(lastDistance, distance);
                        focusTime = Time.time;
                        MaxFollowError = 0f;
                        step = Step.Follow;
                    }
                    break;

                case Step.Follow:
                    if (!following) followBroken = true;
                    MaxFollowError = Mathf.Max(MaxFollowError, Vector2.Distance(focal, Centroid()));
                    FollowTime = Time.time - focusTime;
                    TrackControls(distance, yaw);
                    if (FollowTime >= FollowWindow)
                    {
                        PassFollow = !followBroken && MaxFollowError <= FollowTolerance;
                        PassControls = zoomSeen && keyYawSeen && dragYawSeen;
                        step = Step.Cancel;
                    }
                    break;

                case Step.Cancel:
                    if (lastFollowing && !following)
                    {
                        PassCancel = Controller.Pan.ReadValue<Vector2>() != Vector2.zero;
                        cancelStartFocal = lastFocal;
                    }
                    else if (!following && PassCancel)
                    {
                        CancelPanDistance = Vector2.Distance(focal, cancelStartFocal);
                        if (Controller.Pan.ReadValue<Vector2>() == Vector2.zero)
                        {
                            PassCancel &= CancelPanDistance > 0.1f;
                            step = Step.Reacquire;
                        }
                    }
                    break;

                case Step.Reacquire:
                    if (following)
                    {
                        ReacquireError = Vector2.Distance(focal, Centroid());
                        PassReacquire = ReacquireError <= FollowTolerance;
                        step = Step.Deselect;
                    }
                    break;

                case Step.Deselect:
                    if (Selection.Selected.Count == 0)
                    {
                        DeselectJump = Vector2.Distance(focal, lastFocal);
                        DeselectPreviousStep = previousFocalStep.magnitude;
                        MarkersAfterDeselect = ActiveCircles();
                        PassDeselect = lastFollowing && !following && DeselectJump <= DeselectPreviousStep + JumpTolerance
                                       && MarkersAfterDeselect == 0;
                        step = Step.Done;
                    }
                    break;

                case Step.Done:
                    Finish();
                    break;
            }

            previousFocalStep = focal - lastFocal;
            lastFocal = focal;
            lastFollowing = following;
            lastDistance = distance;
            lastYaw = yaw;

            if (ScriptedInput && Time.time - startTime > ScriptedTimeLimit && !Finished)
                Fail($"timed out in step {step}");
        }

        void EvaluateFocus(float before, float after)
        {
            FocusDistanceBefore = before;
            FocusDistanceAfter = after;
            FocusPitchError = Mathf.Abs(Mathf.DeltaAngle(Controller.transform.eulerAngles.x, TacticalCameraController.PitchDegrees));
            float margin = Controller.FrameMargin;
            FocusMaxExtent = 0f;
            MinSpeedAtFocus = float.MaxValue;
            bool inside = true;
            foreach (ShipMovement ship in ships)
            {
                Vector3 v = view.WorldToViewportPoint(ShipMovementState.ToWorld(ship.Motor.State.Position));
                inside &= v.z > 0f && v.x >= margin - 1e-4f && v.x <= 1f - margin + 1e-4f && v.y >= margin - 1e-4f && v.y <= 1f - margin + 1e-4f;
                FocusMaxExtent = Mathf.Max(FocusMaxExtent, 2f * Mathf.Abs(v.x - 0.5f), 2f * Mathf.Abs(v.y - 0.5f));
                MinSpeedAtFocus = Mathf.Min(MinSpeedAtFocus, ship.Motor.State.Speed);
            }
            bool zoomedOut = after > before + 1e-3f;
            // Zoom out only: never closer; when it zoomed out, the outermost center sits on the 80 % edge (not looser).
            bool tight = !zoomedOut || after >= Controller.MaxDistance - 1e-3f || FocusMaxExtent >= 1f - 2f * margin - 0.005f;
            PassFocus = inside && tight && after >= before - 1e-3f && FocusPitchError <= PitchTolerance && MinSpeedAtFocus > MovingSpeed;
        }

        void TrackControls(float distance, float yaw)
        {
            bool yawChanged = Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw)) > 1e-4f;
            if ((Controller.WheelZoom.ReadValue<float>() != 0f || Controller.KeyZoom.ReadValue<float>() != 0f) && Mathf.Abs(distance - lastDistance) > 1e-4f)
                zoomSeen = true;
            if (Controller.KeyYaw.ReadValue<float>() != 0f && yawChanged) keyYawSeen = true;
            if (Controller.YawDrag.IsPressed() && Controller.YawDragDelta.ReadValue<float>() != 0f && yawChanged) dragYawSeen = true;
        }

        // Each enabled destination circle must sit on one selected Move ship's destination, one circle per such ship.
        void CheckMarkers()
        {
            int expected = 0;
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship) && ship.Orders.Kind == OrderKind.Move) expected++;
            int active = ActiveCircles();
            if (expected == 0) return;
            MarkerChecks++;
            float worst = active == expected ? 0f : float.PositiveInfinity;
            foreach (ShipMovement ship in Selection.Selected)
            {
                if (!ShipSelection.IsSelectable(ship) || ship.Orders.Kind != OrderKind.Move) continue;
                float best = float.PositiveInfinity;
                foreach (LineRenderer circle in circles)
                    best = Mathf.Min(best, Vector2.Distance(CircleCenter(circle), ship.Orders.Destination));
                worst = Mathf.Max(worst, best);
            }
            MaxMarkerError = Mathf.Max(MaxMarkerError, worst);
        }

        int ActiveCircles()
        {
            circles.Clear();
            foreach (LineRenderer line in Feedback.GetComponentsInChildren<LineRenderer>())
                if (line.enabled && line.positionCount > 0) circles.Add(line);
            return circles.Count;
        }

        static Vector2 CircleCenter(LineRenderer line)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < line.positionCount; i++) sum += line.GetPosition(i);
            return ShipMovementState.ToPlanar(sum / line.positionCount);
        }

        // Only the group command may change orders; a natural completion (Move → None, same destination) is allowed.
        void CheckOrders()
        {
            bool commandFrame = step == Step.Command;
            for (int i = 0; i < 3; i++)
            {
                MovementOrders orders = ships[i].Orders;
                bool changed = orders.Kind != lastKind[i] || orders.Destination != lastDestination[i];
                bool completion = orders.Kind == OrderKind.None && lastKind[i] == OrderKind.Move && orders.Destination == lastDestination[i];
                if (changed && !(commandFrame && orders.Kind == OrderKind.Move) && !completion)
                {
                    PassNoOrders = false;
                    UnexpectedOrders += $"{ships[i].name} {orders.Kind} in step {step}; ";
                }
                lastKind[i] = orders.Kind;
                lastDestination[i] = orders.Destination;
            }
        }

        Vector2 GroundCenter()
        {
            Ray center = view.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            return new Plane(Vector3.up, Vector3.zero).Raycast(center, out float enter)
                ? ShipMovementState.ToPlanar(center.GetPoint(enter))
                : Controller.FocalPoint;
        }

        Vector2 Centroid()
        {
            Vector2 sum = Vector2.zero;
            int count = 0;
            foreach (ShipMovement ship in Selection.Selected)
                if (ShipSelection.IsSelectable(ship))
                {
                    sum += ship.Motor.State.Position;
                    count++;
                }
            return count > 0 ? sum / count : Vector2.zero;
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
            PassMarkers = MarkerChecks > 0 && MaxMarkerError <= MarkerTolerance;
            Pass = PassSelect && PassCommand && PassFocus && PassFollow && PassControls && PassCancel && PassReacquire
                   && PassMarkers && PassDeselect && PassNoOrders && MaxPitchError <= PitchTolerance;

            var text = new StringBuilder();
            text.AppendLine($"Mode: {Mode}");
            text.AppendLine($"(1) Box select:  {Result(PassSelect)} A, B, C selected");
            text.AppendLine($"(2) Command:     {Result(PassCommand)} A, B, C ordered to the marker (navigated with the real camera bindings)");
            text.AppendLine($"(3) F focus:     {Result(PassFocus)} distance {FocusDistanceBefore:F2} → {FocusDistanceAfter:F2} u (zoom out only), " +
                            $"outermost center at {FocusMaxExtent:P1} of the half-viewport (≤ {1f - 2f * Controller.FrameMargin:P0}), " +
                            $"pitch error {FocusPitchError:F4}°, slowest ship {MinSpeedAtFocus:F2} u/s (> {MovingSpeed})");
            text.AppendLine($"(4) Follow:      {Result(PassFollow)} max center-to-centroid {MaxFollowError:F4} u (≤ {FollowTolerance}) over {FollowTime:F2} s, " +
                            $"follow interrupted {followBroken}");
            text.AppendLine($"(5) Controls:    {Result(PassControls)} while following: zoom {zoomSeen}, Q/E yaw {keyYawSeen}, MMB yaw {dragYawSeen}");
            text.AppendLine($"(6) W cancels:   {Result(PassCancel)} follow ended on pan input, focal panned {CancelPanDistance:F2} u (> 0.1)");
            text.AppendLine($"(7) F again:     {Result(PassReacquire)} center-to-centroid {ReacquireError:F4} u (≤ {FollowTolerance})");
            text.AppendLine($"(8) Markers:     {Result(PassMarkers)} max circle-to-destination {MaxMarkerError:F4} u (≤ {MarkerTolerance}) over {MarkerChecks} frames");
            text.AppendLine($"(9) Deselect:    {Result(PassDeselect)} follow ended, focal step {DeselectJump:F4} u (previous {DeselectPreviousStep:F4} + {JumpTolerance}), " +
                            $"circles left {MarkersAfterDeselect}");
            text.AppendLine($"(10) No orders from camera: {Result(PassNoOrders)} {UnexpectedOrders}");
            text.AppendLine($"     Pitch:      max error {MaxPitchError:F4}° (≤ {PitchTolerance})");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        // Acceptance-only driver: queues events on virtual devices so the real bindings, PlayerCommands and the
        // camera controller run unchanged. Every pointer action targets a point the driver has confirmed is on screen.
        IEnumerator Drive()
        {
            scriptedMouse = InputSystem.AddDevice<Mouse>("ScriptedMouse");
            scriptedKeyboard = InputSystem.AddDevice<Keyboard>("ScriptedKeyboard");
            yield return new WaitForSeconds(1f);

            // Zoom out with the wheel until all three ships are on screen, then box-select them.
            for (int i = 0; i < 30 && !AllOnScreen(0.1f); i++)
                yield return Wheel(-1f, 1);
            Debug.Log($"[Drive] ships on screen at distance {Controller.Distance:F2} u");
            yield return Drag(ScreenBounds(30f), 10);
            yield return new WaitForSeconds(0.3f);

            // Zoom out fully, then pan with WASD until the marker is on screen, and right-click its ground point.
            yield return Wheel(-1f, 20);
            Vector3 markerGround = ShipMovementState.ToWorld(ShipMovementState.ToPlanar(Marker.position));
            for (int i = 0; i < 1200; i++)
            {
                Vector3 v = view.WorldToViewportPoint(markerGround);
                var keys = new List<Key>();
                if (v.z <= 0f || v.y > 0.8f) keys.Add(Key.W);
                else if (v.y < 0.2f) keys.Add(Key.S);
                if (v.z > 0f && v.x > 0.8f) keys.Add(Key.D);
                else if (v.z > 0f && v.x < 0.2f) keys.Add(Key.A);
                if (keys.Count == 0) break;
                InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(keys.ToArray()));
                yield return null;
            }
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
            Vector3 markerViewport = view.WorldToViewportPoint(markerGround);
            Debug.Log($"[Drive] marker on screen at viewport ({markerViewport.x:F3}, {markerViewport.y:F3}), distance {Controller.Distance:F2} u, focal {Controller.FocalPoint}");
            yield return Click(MouseButton.Right, markerGround);
            while (ShipA.Motor.State.Speed < 2f || ShipB.Motor.State.Speed < 2f || ShipC.Motor.State.Speed < 2f) yield return null;

            // Zoom back in so F has to zoom out to frame the ships.
            for (int i = 0; i < 40 && Controller.Distance > PreFocusDistance; i++)
                yield return Wheel(+1f, 1);
            Debug.Log($"[Drive] distance before F {Controller.Distance:F2} u");
            yield return Press(Key.F);

            // Yaw and zoom while following.
            yield return new WaitForSeconds(1f);
            yield return Wheel(+1f, 1);
            yield return new WaitForSeconds(0.5f);
            yield return Hold(Key.E, 0.4f);
            yield return new WaitForSeconds(0.5f);
            yield return MiddleDrag(10, 15f);
            while (step == Step.Follow) yield return null;

            yield return Hold(Key.W, 0.5f);
            yield return new WaitForSeconds(0.5f);
            yield return Press(Key.F);
            yield return new WaitForSeconds(1f);

            yield return Click(MouseButton.Left, EmptyGround());
        }

        bool AllOnScreen(float margin)
        {
            foreach (ShipMovement ship in new[] { ShipA, ShipB, ShipC })
            {
                Vector3 v = view.WorldToViewportPoint(ShipMovementState.ToWorld(ship.Motor.State.Position));
                if (v.z <= 0f || v.x < margin || v.x > 1f - margin || v.y < margin || v.y > 1f - margin) return false;
            }
            return true;
        }

        Rect ScreenBounds(float margin)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (ShipMovement ship in new[] { ShipA, ShipB, ShipC })
            {
                Vector2 screen = view.WorldToScreenPoint(ShipMovementState.ToWorld(ship.Motor.State.Position));
                min = Vector2.Min(min, screen);
                max = Vector2.Max(max, screen);
            }
            return Rect.MinMaxRect(min.x - margin, min.y - margin, max.x + margin, max.y + margin);
        }

        // A ground point near the bottom of the screen that no ship covers.
        Vector3 EmptyGround()
        {
            for (float x = 0.1f; x < 0.95f; x += 0.1f)
            {
                Ray ray = view.ViewportPointToRay(new Vector3(x, 0.12f, 0f));
                if (Physics.Raycast(ray, Mathf.Infinity, Commands.ShipLayers, QueryTriggerInteraction.Ignore)) continue;
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) return ray.GetPoint(enter);
            }
            return ShipMovementState.ToWorld(Controller.FocalPoint + new Vector2(0f, -40f));
        }

        static float ScrollNotch(float direction) =>
            direction * (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange ? 120f : 1f);

        IEnumerator Wheel(float direction, int notches)
        {
            for (int i = 0; i < notches; i++)
            {
                InputSystem.QueueStateEvent(scriptedMouse, new MouseState { scroll = new Vector2(0f, ScrollNotch(direction)) });
                yield return null;
            }
            yield return null;
        }

        IEnumerator Press(Key key)
        {
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(key));
            yield return null;
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
        }

        IEnumerator Hold(Key key, float seconds)
        {
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(key));
            yield return new WaitForSeconds(seconds);
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
        }

        IEnumerator Drag(Rect rect, int frames)
        {
            Vector2 from = new Vector2(rect.xMax, rect.yMax), to = new Vector2(rect.xMin, rect.yMin);
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

        IEnumerator MiddleDrag(int frames, float dx)
        {
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState().WithButton(MouseButton.Middle, true));
            yield return null;
            for (int i = 0; i < frames; i++)
            {
                InputSystem.QueueStateEvent(scriptedMouse, new MouseState { delta = new Vector2(dx, 0f) }.WithButton(MouseButton.Middle, true));
                yield return null;
            }
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState());
            yield return null;
        }

        IEnumerator Click(MouseButton button, Vector3 world)
        {
            Vector2 screen = view.WorldToScreenPoint(world);
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen }.WithButton(button, true));
            yield return null;
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState { position = screen });
            yield return null;
        }

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1400f, 320f), summary);
                return;
            }
            string prompt = step switch
            {
                Step.Select => "1. Zoom out until A, B, C are visible and drag a box around them",
                Step.Command => "2. Pan/zoom until the marker is visible, right-click it",
                Step.Focus => "3. Zoom in close, then press F",
                Step.Follow => $"4. Following: zoom, use Q/E and a middle-mouse drag ({FollowTime:F1}/{FollowWindow:F0} s)",
                Step.Cancel => "5. Press W once",
                Step.Reacquire => "6. Press F again",
                _ => "7. Left-click empty ground",
            };
            GUI.Label(new Rect(10f, 10f, 1400f, 60f),
                $"{Mode} — {prompt}   following {Controller.Following}   distance {Controller.Distance:F1} u   selected {Selection.Selected.Count}");
        }
    }
}
