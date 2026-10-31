using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Ouranos.Cameras
{
    /// <summary>
    /// Issue #12 acceptance scenario. Runs after <see cref="TacticalCameraController.LateUpdate"/> each frame and
    /// measures the camera independently of the controller's state: pitch from the Transform, the ground point from
    /// the viewport-center ray against Y = 0, height, distance. The player (or, with <see cref="ScriptedInput"/>,
    /// virtual Input System devices) pans, zooms and yaws at yaw stages 0°, 90° and 180°. Results show in the
    /// Inspector, on screen and as one log summary. Command line: -cameraFps N sets the target frame rate.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CameraAcceptanceScenario : MonoBehaviour
    {
        static readonly float[] StageYaws = { 0f, 90f, 180f };

        [Header("Setup")]
        public TacticalCameraController Controller;
        [Tooltip("Acceptance verification only: drive the real bindings with virtual mouse/keyboard events. Keep off in the committed scene.")]
        public bool ScriptedInput;
        [Tooltip("Target frame rate with VSync off; 0 leaves the player default. -cameraFps N overrides it.")]
        [Min(0)] public int TargetFrameRate;

        [Header("Criteria")]
        public float PitchTolerance = 0.1f;
        [Tooltip("Pre/post zoom ground point and center-ray-to-focal tolerance (u).")]
        public float GroundTolerance = 0.1f;
        [Tooltip("Pan displacement direction tolerance against the yaw-rotated input (°).")]
        public float DirectionTolerance = 1f;
        [Tooltip("Relative tolerance of measured pan, key yaw and key zoom rates.")]
        public float RateTolerance = 0.02f;
        [Tooltip("A frame belongs to a yaw stage when its yaw is within this of 0°, 90° or 180° (°).")]
        public float StageTolerance = 10f;
        [Tooltip("Scripted runs only: fail when not finished within this time (s).")]
        public float ScriptedTimeLimit = 60f;

        [Header("Results (recorded at runtime)")]
        public float MaxPitchError;
        public float MaxZoomGroundError;
        public float MaxFocalRayError;
        public float MaxPanHeightChange;
        public float MaxPanDirectionError;
        public float MinObservedDistance = float.MaxValue, MaxObservedDistance;
        public float PanRate, KeyYawRate, KeyZoomRate;
        public float ObservedFps, MedianFrameMs;
        public bool PassInitial, PassYawPitch, PassZoom, PassPan, PassBounds, PassRates, PassCoverage, Pass;
        public bool Finished;

        // Acceptance-only state; the controller owns no part of it.
        struct Coverage { public bool Pan, Wheel, KeyMain, KeyPad; }
        readonly Coverage[] stages = new Coverage[3];
        bool yawE, yawQ, dragVertical, rayMissed, initialDone, firstFrame = true;
        int yawFrames, zoomFrames, panFrames, aspectCount;
        float lastAspect;
        float panDistance, panTime, yawDistance, yawTime, zoomDistance, zoomTime;
        Vector3 prevGround;
        float prevYaw, prevHeight, prevDistance, startTime;
        readonly List<float> frameTimes = new List<float>();
        string summary = "";
        Mouse scriptedMouse;
        Keyboard scriptedKeyboard;

        string Mode => ScriptedInput ? "scripted" : "manual";

        void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-cameraFps" && int.TryParse(args[i + 1], out int fps))
                    TargetFrameRate = fps;
            if (TargetFrameRate > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = TargetFrameRate;
            }
        }

        void Start()
        {
            startTime = Time.time;
            if (ScriptedInput)
                StartCoroutine(Drive());
        }

        void OnDestroy()
        {
            if (scriptedMouse != null) InputSystem.RemoveDevice(scriptedMouse);
            if (scriptedKeyboard != null) InputSystem.RemoveDevice(scriptedKeyboard);
        }

        // Execution order 100: the controller has applied this frame's input and Transform.
        void LateUpdate()
        {
            if (Finished) return;
            Camera view = Controller.GetComponent<Camera>();
            Transform pose = Controller.transform;
            float pitch = pose.eulerAngles.x;
            float yaw = Controller.Yaw;
            float height = pose.position.y;
            float distance = Controller.Distance;
            Ray center = view.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 ground = prevGround;
            if (new Plane(Vector3.up, Vector3.zero).Raycast(center, out float enter))
                ground = center.GetPoint(enter);
            else
                rayMissed = true;

            float aspect = view.aspect;
            if (firstFrame || Mathf.Abs(aspect - lastAspect) > 0.01f) aspectCount++;
            lastAspect = aspect;

            MaxPitchError = Mathf.Max(MaxPitchError, Mathf.Abs(Mathf.DeltaAngle(pitch, TacticalCameraController.PitchDegrees)));
            MaxFocalRayError = Mathf.Max(MaxFocalRayError, Vector2.Distance(new Vector2(ground.x, ground.z), Controller.FocalPoint));
            MinObservedDistance = Mathf.Min(MinObservedDistance, distance);
            MaxObservedDistance = Mathf.Max(MaxObservedDistance, distance);

            if (firstFrame)
            {
                PassInitial = !view.orthographic && MaxPitchError <= PitchTolerance;
                initialDone = true;
            }
            else
                Classify(yaw, ground, height, distance);

            frameTimes.Add(Time.unscaledDeltaTime);
            firstFrame = false;
            prevGround = ground;
            prevYaw = yaw;
            prevHeight = height;
            prevDistance = distance;

            if (CoverageComplete() || (ScriptedInput && Time.time - startTime > ScriptedTimeLimit))
                Finish();
        }

        // Each frame is checked against the one input kind active in it; mixed frames are skipped.
        void Classify(float yaw, Vector3 ground, float height, float distance)
        {
            float dt = Time.deltaTime;
            Vector2 pan = Controller.Pan.ReadValue<Vector2>();
            float keyYaw = Controller.KeyYaw.ReadValue<float>();
            bool drag = Controller.YawDrag.IsPressed() && Controller.YawDragDelta.ReadValue<float>() != 0f;
            float wheel = Controller.WheelZoom.ReadValue<float>();
            float keyZoom = Controller.KeyZoom.ReadValue<float>();
            bool panning = pan != Vector2.zero, yawing = keyYaw != 0f || drag, zooming = wheel != 0f || keyZoom != 0f;
            int kinds = (panning ? 1 : 0) + (yawing ? 1 : 0) + (zooming ? 1 : 0);
            if (kinds != 1) return;

            int stage = Stage(prevYaw);
            if (yawing)
            {
                yawFrames++;
                float delta = Mathf.DeltaAngle(prevYaw, yaw);
                if (keyYaw > 0f && delta > 0f) yawE = true;
                if (keyYaw < 0f && delta < 0f) yawQ = true;
                if (drag && Controller.YawDrag.activeControl?.device is Mouse mouse && mouse.delta.y.ReadValue() != 0f)
                    dragVertical = true;
                if (keyYaw != 0f && !drag)
                {
                    yawDistance += Mathf.Abs(delta);
                    yawTime += dt * Mathf.Abs(keyYaw);
                }
            }
            else if (zooming)
            {
                zoomFrames++;
                MaxZoomGroundError = Mathf.Max(MaxZoomGroundError, Vector3.Distance(ground, prevGround));
                if (stage >= 0)
                {
                    if (wheel != 0f) stages[stage].Wheel = true;
                    else if (Controller.KeyZoom.activeControl is KeyControl key)
                    {
                        if (key.keyCode == Key.NumpadPlus || key.keyCode == Key.NumpadMinus) stages[stage].KeyPad = true;
                        else stages[stage].KeyMain = true;
                    }
                }
                bool clamped = distance <= Controller.MinDistance + 1e-4f || distance >= Controller.MaxDistance - 1e-4f
                               || prevDistance <= Controller.MinDistance + 1e-4f || prevDistance >= Controller.MaxDistance - 1e-4f;
                if (wheel == 0f && !clamped)
                {
                    zoomDistance += Mathf.Abs(distance - prevDistance);
                    zoomTime += dt * Mathf.Abs(keyZoom);
                }
            }
            else
            {
                panFrames++;
                if (stage >= 0) stages[stage].Pan = true;
                MaxPanHeightChange = Mathf.Max(MaxPanHeightChange, Mathf.Abs(height - prevHeight));
                Vector3 moved = ground - prevGround;
                Vector3 expected = Quaternion.Euler(0f, yaw, 0f) * new Vector3(pan.x, 0f, pan.y);
                MaxPanDirectionError = Mathf.Max(MaxPanDirectionError, Vector3.Angle(new Vector3(moved.x, 0f, moved.z), expected));
                panDistance += new Vector2(moved.x, moved.z).magnitude;
                panTime += dt * Mathf.Min(1f, pan.magnitude);
            }
        }

        int Stage(float yaw)
        {
            for (int i = 0; i < StageYaws.Length; i++)
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, StageYaws[i])) <= StageTolerance) return i;
            return -1;
        }

        bool CoverageComplete()
        {
            foreach (Coverage c in stages)
                if (!c.Pan || !c.Wheel || !c.KeyMain || !c.KeyPad) return false;
            return yawE && yawQ && dragVertical && aspectCount >= 2 && initialDone && BoundsReached();
        }

        bool BoundsReached() => MinObservedDistance <= Controller.MinDistance + 1e-3f && MaxObservedDistance >= Controller.MaxDistance - 1e-3f;

        static bool Within(float measured, float expected, float tolerance) => Mathf.Abs(measured - expected) <= expected * tolerance;

        void Finish()
        {
            Finished = true;
            PanRate = panTime > 0f ? panDistance / panTime : 0f;
            KeyYawRate = yawTime > 0f ? yawDistance / yawTime : 0f;
            KeyZoomRate = zoomTime > 0f ? zoomDistance / zoomTime : 0f;
            frameTimes.Sort();
            float total = 0f;
            foreach (float t in frameTimes) total += t;
            ObservedFps = total > 0f ? frameTimes.Count / total : 0f;
            MedianFrameMs = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] * 1000f : 0f;

            PassYawPitch = yawE && yawQ && dragVertical && MaxPitchError <= PitchTolerance;
            PassZoom = zoomFrames > 0 && !rayMissed && MaxZoomGroundError <= GroundTolerance && MaxFocalRayError <= GroundTolerance;
            PassPan = panFrames > 0 && MaxPanHeightChange <= 1e-4f && MaxPanDirectionError <= DirectionTolerance;
            PassBounds = BoundsReached() && MinObservedDistance >= Controller.MinDistance - 1e-4f
                         && MaxObservedDistance <= Controller.MaxDistance + 1e-4f;
            PassRates = Within(PanRate, Controller.PanSpeed, RateTolerance) && Within(KeyYawRate, Controller.KeyboardYawSpeed, RateTolerance)
                        && Within(KeyZoomRate, Controller.KeyZoomSpeed, RateTolerance);
            PassCoverage = CoverageComplete();
            Pass = PassInitial && PassYawPitch && PassZoom && PassPan && PassBounds && PassRates && PassCoverage;

            var text = new StringBuilder();
            text.AppendLine($"Mode: {Mode}, target {(TargetFrameRate > 0 ? TargetFrameRate + " fps" : "default")}, " +
                            $"observed {ObservedFps:F1} fps (median frame {MedianFrameMs:F2} ms, {frameTimes.Count} frames)");
            text.AppendLine($"(1) Initial:   {Result(PassInitial)} perspective, pitch 45° ±{PitchTolerance}");
            text.AppendLine($"(2) Yaw pitch: {Result(PassYawPitch)} max pitch error {MaxPitchError:F4}° over {yawFrames} yaw frames " +
                            $"(E {yawE}, Q {yawQ}, MMB drag with vertical motion {dragVertical})");
            text.AppendLine($"(3) Zoom:      {Result(PassZoom)} max pre/post ground {MaxZoomGroundError:F4} u over {zoomFrames} frames, " +
                            $"max center-ray to focal {MaxFocalRayError:F4} u (≤ {GroundTolerance}), ray missed {rayMissed}, aspects {aspectCount}");
            text.AppendLine($"(4) Pan:       {Result(PassPan)} max height change {MaxPanHeightChange:F6} u, " +
                            $"max direction error {MaxPanDirectionError:F3}° (≤ {DirectionTolerance}) over {panFrames} frames");
            text.AppendLine($"(5) Bounds:    {Result(PassBounds)} distance {MinObservedDistance:F3}–{MaxObservedDistance:F3} u " +
                            $"({Controller.MinDistance}–{Controller.MaxDistance})");
            text.AppendLine($"(6) Rates:     {Result(PassRates)} pan {PanRate:F3} u/s ({Controller.PanSpeed}), " +
                            $"key yaw {KeyYawRate:F3} °/s ({Controller.KeyboardYawSpeed}), key zoom {KeyZoomRate:F3} u/s ({Controller.KeyZoomSpeed}) ±{RateTolerance:P0}");
            text.AppendLine($"    Coverage:  {Result(PassCoverage)} {CoverageText()}");
            text.Append($"RESULT: {Result(Pass)}");
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                Debug.Log($"[Acceptance] {line}");
        }

        string CoverageText()
        {
            var text = new StringBuilder();
            for (int i = 0; i < stages.Length; i++)
                text.Append($"yaw {StageYaws[i]:F0}: pan {stages[i].Pan} wheel {stages[i].Wheel} " +
                            $"+/- {stages[i].KeyMain} numpad {stages[i].KeyPad}; ");
            return text.ToString();
        }

        static string Result(bool pass) => pass ? "PASS" : "FAIL";

        // Acceptance-only driver: queues events on virtual devices so the real bindings and controller run unchanged.
        IEnumerator Drive()
        {
            scriptedMouse = InputSystem.AddDevice<Mouse>("ScriptedMouse");
            scriptedKeyboard = InputSystem.AddDevice<Keyboard>("ScriptedKeyboard");
            yield return new WaitForSeconds(1f);

            yield return StageSteps();
            yield return Hold(Key.E, 1f);
            Screen.SetResolution(900, 900, FullScreenMode.Windowed);
            yield return new WaitForSeconds(0.5f);
            yield return StageSteps();
            yield return Drag(18, new Vector2(20f, 15f));
            Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            yield return new WaitForSeconds(0.5f);
            yield return StageSteps();
            yield return Wheel(+1f, 30);
            yield return Wheel(-1f, 40);
            yield return Hold(Key.Q, 1f);
        }

        IEnumerator StageSteps()
        {
            yield return Hold(Key.W, 1f);
            yield return Hold(Key.D, 1f);
            yield return Wheel(+1f, 3);
            yield return Wheel(-1f, 3);
            yield return Hold(Key.Equals, 0.5f);
            yield return Hold(Key.Minus, 0.5f);
            yield return Hold(Key.NumpadPlus, 0.5f);
            yield return Hold(Key.NumpadMinus, 0.5f);
        }

        IEnumerator Hold(Key key, float seconds)
        {
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState(key));
            yield return new WaitForSeconds(seconds);
            InputSystem.QueueStateEvent(scriptedKeyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        // One notch per frame; the uniform scroll range reports one unit per notch, the platform range 120.
        IEnumerator Wheel(float direction, int notches)
        {
            float unit = InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange ? 120f : 1f;
            for (int i = 0; i < notches; i++)
            {
                InputSystem.QueueStateEvent(scriptedMouse, new MouseState { scroll = new Vector2(0f, direction * unit) });
                yield return null;
            }
            yield return null;
        }

        // Middle button held; each frame moves right by delta.x and alternates up/down by delta.y.
        IEnumerator Drag(int frames, Vector2 delta)
        {
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState().WithButton(MouseButton.Middle, true));
            yield return null;
            for (int i = 0; i < frames; i++)
            {
                var step = new Vector2(delta.x, i % 2 == 0 ? delta.y : -delta.y);
                InputSystem.QueueStateEvent(scriptedMouse, new MouseState { delta = step }.WithButton(MouseButton.Middle, true));
                yield return null;
            }
            InputSystem.QueueStateEvent(scriptedMouse, new MouseState());
            yield return null;
            yield return null;
        }

        void OnGUI()
        {
            if (Finished)
            {
                GUI.Label(new Rect(10f, 10f, 1400f, 300f), summary);
                return;
            }
            GUI.Label(new Rect(10f, 10f, 1400f, 80f),
                $"{Mode} — at yaw 0°, 90° and 180°: pan W/A/S/D, zoom with wheel, +/- and numpad +/-. " +
                "Yaw with E, Q and a middle-mouse drag (move vertically too). Zoom fully in and out. Resize the window once.\n" +
                $"yaw {Controller.Yaw:F1}°  distance {Controller.Distance:F1} u  focal {Controller.FocalPoint}\n{CoverageText()}");
        }
    }
}
