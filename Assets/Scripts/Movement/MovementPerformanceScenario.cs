using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Issue #7 movement performance benchmark. Spawns Count ships in two mirrored grids in opposing lanes LaneOffset apart,
    /// sends them past each other and the obstacles with <see cref="GroupMoveDispatcher"/> (re-commanded every
    /// CommandInterval) at a capped frame rate, and records workload
    /// counters and per-frame Profiler values (ProfilerRecorder) for a capture window after a warmup.
    /// Results show on screen and as [Perf] log lines. Never writes ship transforms or state; orders go through
    /// the order seam only. Command line: -perfCount N, -perfRun K, -perfLane L, -perfValidate, -perfQuit.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class MovementPerformanceScenario : MonoBehaviour
    {
        // Upper bound on recorded rendered frames per run; a run that exceeds it is flagged invalid.
        const int MaxFrames = 400000;

        [Header("Setup")]
        public ShipMovement ShipPrefab;
        [Tooltip("Static obstacles whose clearance and engagement are recorded.")]
        public Transform[] Obstacles;
        [Tooltip("Ships spawned (split West/East; an odd extra ship goes West).")]
        [Range(1, 200)] public int Count = 100;
        [Tooltip("Seed for the per-ship placement jitter.")]
        public int Seed = 1;
        [Tooltip("Run label for the log (repeats of the same configuration).")]
        [Min(1)] public int Run = 1;

        [Header("Traffic")]
        [Tooltip("West group grid center and destination anchor of the East group (u).")]
        public Vector2 WestAnchor = new Vector2(-150f, 0f);
        [Tooltip("East group grid center and destination anchor of the West group (u).")]
        public Vector2 EastAnchor = new Vector2(150f, 0f);
        [Tooltip("Lateral lane separation: the West group travels at z = +LaneOffset and the East group at z = −LaneOffset (u).")]
        [Range(0f, 100f)] public float LaneOffset = 60f;
        [Tooltip("Grid columns per group, along X.")]
        [Range(1, 10)] public int Columns = 5;
        [Tooltip("Grid pitch between ship centers (u).")]
        [Range(5f, 20f)] public float Spacing = 10f;
        [Tooltip("Maximum seeded placement jitter per axis (u).")]
        [Range(0f, 2f)] public float Jitter = 1f;
        [Tooltip("Time between group commands; the groups swap sides each time (s).")]
        [Range(10f, 300f)] public float CommandInterval = 50f;

        [Header("Capture")]
        [Tooltip("Player frame-rate cap with VSync off; identical for every compared run (frames/s).")]
        [Range(10, 240)] public int TargetFrameRate = 60;
        [Tooltip("Time from the first command to the start of the capture window (s).")]
        [Range(0f, 120f)] public float Warmup = 10f;
        [Tooltip("Length of the capture window (s).")]
        [Range(1f, 600f)] public float Duration = 30f;
        [Tooltip("Validation run: record the O(n²) minimum ship separation every fixed step. Off for timed runs.")]
        public bool TrackSeparation;

        [Header("Criteria")]
        [Tooltip("Minimum average fraction of ships holding a move order during capture for a valid run.")]
        [Range(0f, 1f)] public float MinActiveFraction = 0.7f;
        [Tooltip("Speed above which a ship counts as moving (u/s).")]
        public float MovingSpeed = 0.1f;
        [Tooltip("Continuous blocking at or above this counts the ship as a sustained blocker (s).")]
        public float SustainedBlock = 2f;
        [Tooltip("Ship-to-obstacle-center distance that counts as engaging an obstacle (u).")]
        public float EngagementDistance = 16f;
        public float MinShipDistance = 4f;
        public float MinObstacleDistance = 12f;

        [Header("Results (runtime)")]
        public bool Finished;
        public bool Valid;
        public float ActiveOrderPercent;
        public float MovingPercent;

        static readonly ProfilerMarker ScenarioMarker = new ProfilerMarker("Perf.Scenario");

        // Per-frame recorder for one Profiler marker or counter, looked up by name.
        class Series
        {
            public readonly string Name;
            public ProfilerRecorder Recorder;
            public bool Valid;
            public string Unit = "unavailable";
            public readonly int[] Values = new int[MaxFrames];
            public readonly int[] Counts = new int[MaxFrames];

            public Series(string name) => Name = name;
        }

        class CommandRecord
        {
            public float Time;
            public int Frame;
            public double Realtime;
            public int Orders;
            public int QueueDepth;
            public int MaxQueueDepth;
            public int Pending;
            public int LastReadyFrame = -1;
            public readonly List<float> LatencyMs = new List<float>();
            public readonly List<int> LatencyFrames = new List<int>();
        }

        readonly List<ShipMovement> west = new List<ShipMovement>();
        readonly List<ShipMovement> east = new List<ShipMovement>();
        readonly List<ShipMovement> ships = new List<ShipMovement>();
        readonly List<CommandRecord> commands = new List<CommandRecord>();

        Series mainThread, mainThreadActive, waitForTarget, gcAlloc, fixedStep, steering, avoidance, motorPropose, guard, motorCommit, routeRequest,
            physicsFixed, scriptsFixed, scenario, neighborQuery, neighborScan, sideClearance;
        Series[] series;
        bool recording;

        // Per recorded frame (the frame before the Update that read it).
        readonly float[] frameTime = new float[MaxFrames];
        readonly int[] frameOwnSteps = new int[MaxFrames];
        readonly int[] frameQueue = new int[MaxFrames];
        int frames;
        bool framesOverflowed;
        float previousTime;
        int previousOwnSteps;
        int previousQueue;
        int stepsThisFrame;

        // Per ship.
        RouteStatus[] lastStatus;
        OrderKind[] lastKind;
        bool[] wasAvoiding;
        bool[] wasStopped;
        float[] blockedFor;
        bool[] everBlocked;
        bool[,] engaged;
        int[] commandOfShip;

        float startTime;
        int nextCommand;
        bool captureStarted;
        int overflowAtCaptureStart;
        bool quitWhenDone;

        // Workload counters.
        int ordersIssued, routesReady, routesFailed, unexpectedStops, arrivals;
        int avoidanceActivations, avoidanceActivationsInCapture;
        int captureSteps;
        double activeSum, movingSum;
        int blockedShipSteps;
        float maxBlockedSeconds;
        float[] longestBlock;
        float minPredictedApproach = float.PositiveInfinity;
        float minShipDistance = float.PositiveInfinity;
        string closestPair = "";
        float[] minObstacleDistance;
        int overflowEvents;

        string summary = "Spawning…";
        string liveText = "";
        float nextLiveUpdate;

        void Awake()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-perfCount" && i + 1 < args.Length && int.TryParse(args[i + 1], out int count)) Count = Mathf.Clamp(count, 1, 200);
                if (args[i] == "-perfRun" && i + 1 < args.Length && int.TryParse(args[i + 1], out int run)) Run = run;
                if (args[i] == "-perfLane" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float lane)) LaneOffset = lane;
                if (args[i] == "-perfValidate") TrackSeparation = true;
                if (args[i] == "-perfQuit") quitWhenDone = true;
            }
            useGUILayout = false;
            // A benchmark player must not pause when its window loses focus.
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFrameRate;
        }

        void Start()
        {
            Spawn();
            int n = ships.Count;
            lastStatus = new RouteStatus[n];
            lastKind = new OrderKind[n];
            wasAvoiding = new bool[n];
            wasStopped = new bool[n];
            blockedFor = new float[n];
            everBlocked = new bool[n];
            longestBlock = new float[n];
            commandOfShip = new int[n];
            int obstacleCount = Obstacles != null ? Obstacles.Length : 0;
            engaged = new bool[n, obstacleCount];
            minObstacleDistance = new float[obstacleCount];
            for (int i = 0; i < obstacleCount; i++) minObstacleDistance[i] = float.PositiveInfinity;

            Debug.Log($"[Perf] config Count {Count} spawned {n} (West {west.Count}, East {east.Count}) Run {Run} Seed {Seed} " +
                      $"Warmup {Warmup} s Duration {Duration} s CommandInterval {CommandInterval} s Spacing {Spacing} u Columns {Columns} " +
                      $"Jitter {Jitter} u LaneOffset {LaneOffset} u TargetFrameRate {TargetFrameRate} TrackSeparation {TrackSeparation}");
            LogEnvironment();

            startTime = Time.time;
            IssueCommand();
        }

        void Spawn()
        {
            var random = new System.Random(Seed);
            int westCount = (Count + 1) / 2;
            SpawnGroup(west, westCount, WestAnchor + WestLane, 1f, 90f, "W", random);
            SpawnGroup(east, Count - westCount, EastAnchor + EastLane, -1f, 270f, "E", random);
            ships.AddRange(west);
            ships.AddRange(east);
        }

        // Grid of Columns along X (mirrored by side) and as many rows as needed along Z, centered on the anchor.
        void SpawnGroup(List<ShipMovement> group, int n, Vector2 anchor, float side, float heading, string prefix, System.Random random)
        {
            int rows = Mathf.CeilToInt(n / (float)Columns);
            for (int k = 0; k < n; k++)
            {
                float x = (k % Columns - (Columns - 1) * 0.5f) * Spacing * side;
                float z = (k / Columns - (rows - 1) * 0.5f) * Spacing;
                var jitter = new Vector2((float)(random.NextDouble() * 2.0 - 1.0), (float)(random.NextDouble() * 2.0 - 1.0)) * Jitter;
                Vector2 position = anchor + new Vector2(x, z) + jitter;
                ShipMovement ship = Instantiate(ShipPrefab, ShipMovementState.ToWorld(position), Quaternion.Euler(0f, heading, 0f));
                ship.name = $"{prefix}{k:D2}";
                group.Add(ship);
            }
        }

        Vector2 WestLane => new Vector2(0f, LaneOffset);
        Vector2 EastLane => new Vector2(0f, -LaneOffset);

        // Even commands send each group to the other side; odd commands bring them back.
        void IssueCommand()
        {
            bool outbound = nextCommand % 2 == 0;
            var record = new CommandRecord { Time = Time.time - startTime, Frame = Time.frameCount, Realtime = Time.realtimeSinceStartupAsDouble };
            record.Orders = GroupMoveDispatcher.Move(west, (outbound ? EastAnchor : WestAnchor) + WestLane).Count
                + GroupMoveDispatcher.Move(east, (outbound ? WestAnchor : EastAnchor) + EastLane).Count;
            record.Pending = record.Orders;
            record.QueueDepth = RouteRequestQueue.Instance != null ? RouteRequestQueue.Instance.PendingCount : 0;
            record.MaxQueueDepth = record.QueueDepth;
            for (int i = 0; i < ships.Count; i++)
            {
                commandOfShip[i] = commands.Count;
                lastStatus[i] = ships[i].Planner.Status;
                lastKind[i] = ships[i].Orders.Kind;
            }
            commands.Add(record);
            ordersIssued += record.Orders;
            Debug.Log($"[Perf] command {nextCommand} at {record.Time:F2} s ({(outbound ? "outbound" : "return")}): " +
                      $"{record.Orders} orders, queue depth {record.QueueDepth}");
            nextCommand++;
        }

        void FixedUpdate()
        {
            if (Finished) return;
            using (ScenarioMarker.Auto())
            {
                stepsThisFrame++;
                float t = Time.fixedTime - startTime;
                bool capture = t >= Warmup && t < Warmup + Duration;
                if (capture && !captureStarted)
                {
                    captureStarted = true;
                    overflowAtCaptureStart = TotalOverflow();
                }

                int active = 0, moving = 0;
                for (int i = 0; i < ships.Count; i++)
                {
                    ShipMovement ship = ships[i];
                    ShipMovementState state = ship.Motor.State;
                    if (ship.Orders.Kind == OrderKind.Move) active++;
                    if (state.Speed > MovingSpeed) moving++;

                    // A failed route stops the ship; a Stop is never issued otherwise in this scenario.
                    bool stopped = ship.Orders.Kind == OrderKind.Stop;
                    if (stopped && !wasStopped[i]) unexpectedStops++;
                    wasStopped[i] = stopped;

                    if (lastKind[i] == OrderKind.Move && ship.Orders.Kind == OrderKind.None) arrivals++;
                    lastKind[i] = ship.Orders.Kind;

                    bool avoiding = ship.Avoidance.Active;
                    if (avoiding && !wasAvoiding[i])
                    {
                        avoidanceActivations++;
                        if (capture) avoidanceActivationsInCapture++;
                    }
                    wasAvoiding[i] = avoiding;
                    if (avoiding && ship.Avoidance.Decision.Threat != null)
                        minPredictedApproach = Mathf.Min(minPredictedApproach, ship.Avoidance.Decision.ClosestDistance);

                    if (ship.Motor.Blocked)
                    {
                        everBlocked[i] = true;
                        blockedFor[i] += Time.fixedDeltaTime;
                        maxBlockedSeconds = Mathf.Max(maxBlockedSeconds, blockedFor[i]);
                        longestBlock[i] = Mathf.Max(longestBlock[i], blockedFor[i]);
                        if (capture) blockedShipSteps++;
                    }
                    else blockedFor[i] = 0f;

                    for (int o = 0; o < minObstacleDistance.Length; o++)
                    {
                        float distance = Vector2.Distance(state.Position, ShipMovementState.ToPlanar(Obstacles[o].position));
                        if (distance < minObstacleDistance[o]) minObstacleDistance[o] = distance;
                        if (distance < EngagementDistance) engaged[i, o] = true;
                    }

                    if (TrackSeparation)
                    {
                        for (int j = i + 1; j < ships.Count; j++)
                        {
                            float separation = Vector2.Distance(state.Position, ships[j].Motor.State.Position);
                            if (separation < minShipDistance)
                            {
                                minShipDistance = separation;
                                closestPair = $"{ship.name}–{ships[j].name} at {t:F2} s";
                            }
                        }
                    }
                }

                if (capture)
                {
                    captureSteps++;
                    activeSum += active / (double)ships.Count;
                    movingSum += moving / (double)ships.Count;
                }
            }
        }

        void Update()
        {
            if (Finished) return;
            using (ScenarioMarker.Auto())
            {
                float t = Time.time - startTime;
                TrackRoutes();

                // Markers register on first use, so recorders start once ships have stepped.
                if (!recording && stepsThisFrame > 0)
                    StartRecorders();
                else if (recording)
                    RecordFrame();

                previousTime = t;
                previousOwnSteps = stepsThisFrame;
                previousQueue = RouteRequestQueue.Instance != null ? RouteRequestQueue.Instance.PendingCount : 0;
                stepsThisFrame = 0;

                if (t >= nextCommand * CommandInterval && t < Warmup + Duration)
                    IssueCommand();

                if (t >= Warmup + Duration)
                    Finish();
                else if (Time.unscaledTime >= nextLiveUpdate && t < Warmup)
                {
                    // Live text only during warmup, so the capture window carries no display allocations.
                    nextLiveUpdate = Time.unscaledTime + 0.5f;
                    liveText = $"Movement performance — Count {ships.Count} Run {Run}  t {t:F1} s  warmup";
                }
                else if (t >= Warmup)
                    liveText = "Capturing…";
            }
        }

        // Route status transitions since the last frame; RouteRequestQueue runs earlier in the frame.
        void TrackRoutes()
        {
            CommandRecord command = commands.Count > 0 ? commands[commands.Count - 1] : null;
            if (command != null && RouteRequestQueue.Instance != null)
                command.MaxQueueDepth = Mathf.Max(command.MaxQueueDepth, RouteRequestQueue.Instance.PendingCount);

            for (int i = 0; i < ships.Count; i++)
            {
                RouteStatus status = ships[i].Planner.Status;
                if (status == lastStatus[i]) continue;
                if (lastStatus[i] == RouteStatus.Pending && status == RouteStatus.Ready)
                {
                    routesReady++;
                    CommandRecord owner = commands[commandOfShip[i]];
                    owner.LatencyMs.Add((float)((Time.realtimeSinceStartupAsDouble - owner.Realtime) * 1000.0));
                    owner.LatencyFrames.Add(Time.frameCount - owner.Frame);
                    owner.Pending--;
                    owner.LastReadyFrame = Time.frameCount;
                }
                else if (lastStatus[i] == RouteStatus.Pending && status == RouteStatus.Failed)
                {
                    routesFailed++;
                    commands[commandOfShip[i]].Pending--;
                }
                lastStatus[i] = status;
            }
        }

        void StartRecorders()
        {
            mainThread = new Series("Main Thread");
            mainThreadActive = new Series("CPU Main Thread Active Time");
            waitForTarget = new Series("WaitForTargetFPS");
            gcAlloc = new Series("GC Allocated In Frame");
            fixedStep = new Series("Movement.FixedStep");
            steering = new Series("Movement.Steering");
            avoidance = new Series("Movement.Avoidance");
            motorPropose = new Series("Movement.MotorPropose");
            guard = new Series("Movement.Guard");
            motorCommit = new Series("Movement.MotorCommit");
            routeRequest = new Series("Movement.RouteRequest");
            physicsFixed = new Series("FixedUpdate.PhysicsFixedUpdate");
            scriptsFixed = new Series("FixedUpdate.ScriptRunBehaviourFixedUpdate");
            scenario = new Series("Perf.Scenario");
            neighborQuery = new Series("Avoidance.NeighborQuery");
            neighborScan = new Series("Avoidance.NeighborScan");
            sideClearance = new Series("Avoidance.SideClearance");
            series = new[] { mainThread, mainThreadActive, waitForTarget, gcAlloc, fixedStep, steering, avoidance, motorPropose, guard, motorCommit, routeRequest, physicsFixed, scriptsFixed, scenario,
                neighborQuery, neighborScan, sideClearance };

            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (Series s in series)
            {
                foreach (ProfilerRecorderHandle handle in handles)
                {
                    ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                    if (description.Name != s.Name) continue;
                    s.Recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
                    s.Recorder.Start();
                    s.Valid = s.Recorder.Valid;
                    s.Unit = $"{description.UnitType} ({description.DataType}, {description.Category.Name})";
                    break;
                }
                Debug.Log($"[Perf] recorder '{s.Name}': {(s.Valid ? s.Unit : "unavailable")}");
            }
            recording = true;
        }

        void RecordFrame()
        {
            if (frames >= MaxFrames)
            {
                framesOverflowed = true;
                return;
            }
            foreach (Series s in series)
            {
                if (!s.Valid || s.Recorder.Count == 0) continue;
                ProfilerRecorderSample sample = s.Recorder.GetSample(s.Recorder.Count - 1);
                s.Values[frames] = (int)System.Math.Min(sample.Value, int.MaxValue);
                s.Counts[frames] = (int)sample.Count;
            }
            frameTime[frames] = previousTime;
            frameOwnSteps[frames] = previousOwnSteps;
            frameQueue[frames] = previousQueue;
            frames++;
        }

        int TotalOverflow()
        {
            int total = 0;
            foreach (ShipMovement ship in ships) total += ship.Avoidance.OverflowCount;
            return total;
        }

        void Finish()
        {
            Finished = true;
            overflowEvents = TotalOverflow() - overflowAtCaptureStart;
            foreach (Series s in series ?? new Series[0])
                if (s.Valid) s.Recorder.Dispose();

            ActiveOrderPercent = captureSteps > 0 ? (float)(activeSum / captureSteps * 100.0) : 0f;
            MovingPercent = captureSteps > 0 ? (float)(movingSum / captureSteps * 100.0) : 0f;
            Valid = ships.Count == Count && ActiveOrderPercent >= MinActiveFraction * 100f && !framesOverflowed && recording;

            var text = new StringBuilder();
            text.AppendLine($"Count {Count} Run {Run} Seed {Seed}: {(Valid ? "VALID" : "INVALID")} " +
                            $"(spawned {ships.Count}, active orders {ActiveOrderPercent:F1}% ≥ {MinActiveFraction * 100f:F0}%, frames {frames}{(framesOverflowed ? " OVERFLOWED" : "")})");
            if (MovingPercent < MinActiveFraction * 100f)
                text.AppendLine($"WORKLOAD WARNING: only {MovingPercent:F1}% of ships moving; the capture measures congestion or idle ships, not moving traffic.");
            text.AppendLine($"workload: moving {MovingPercent:F1}%, orders issued {ordersIssued}, routes ready {routesReady}, failed {routesFailed}, " +
                            $"unexpected stops {unexpectedStops}, arrivals {arrivals}, avoidance activations {avoidanceActivations} " +
                            $"({avoidanceActivationsInCapture} in capture), neighbor overflow events {overflowEvents} (capture), " +
                            $"blocked ship-steps {blockedShipSteps} (capture), ships ever blocked {CountTrue(everBlocked)}, longest continuous block {maxBlockedSeconds:F2} s, " +
                            $"sustained blockers (≥ {SustainedBlock} s) {CountAtLeast(longestBlock, SustainedBlock)}, " +
                            $"min predicted closest approach while avoiding {minPredictedApproach:F2} u");
            text.AppendLine($"obstacles: {DescribeObstacles()}");
            text.AppendLine($"sustained blockers: {DescribeBlockers()}");
            if (TrackSeparation)
            {
                bool pass = minShipDistance >= MinShipDistance && routesFailed == 0 && unexpectedStops == 0;
                for (int o = 0; o < minObstacleDistance.Length; o++) pass &= minObstacleDistance[o] >= MinObstacleDistance;
                text.AppendLine($"VALIDATION (not a profiling number): {(pass ? "PASS" : "FAIL")} min ship–ship {minShipDistance:F3} u ({closestPair}) (≥ {MinShipDistance}), " +
                                $"min obstacle-center ≥ {MinObstacleDistance} u, failed routes {routesFailed}, unexpected stops {unexpectedStops}");
            }
            AppendTiming(text);
            AppendBursts(text);
            summary = text.ToString();

            foreach (string line in summary.Split('\n'))
                if (line.Length > 0) Debug.Log($"[Perf] {line}");

            if (quitWhenDone)
                Application.Quit();
        }

        // Steady frames: inside the capture window with no route request in that frame and an empty queue.
        void AppendTiming(StringBuilder text)
        {
            if (!recording)
            {
                text.AppendLine("timing: recorders never started");
                return;
            }
            var steady = new List<int>();
            int stepMismatch = 0;
            for (int f = 1; f < frames; f++)
            {
                if (fixedStep.Valid && fixedStep.Counts[f] != frameOwnSteps[f] * ships.Count) stepMismatch++;
                bool inCapture = frameTime[f] >= Warmup && frameTime[f] < Warmup + Duration;
                bool burst = (routeRequest.Valid && routeRequest.Counts[f] > 0) || frameQueue[f] > 0;
                if (inCapture && !burst) steady.Add(f);
            }

            // Uncapped frame rates leave most frames without a fixed step; step frames carry the movement cost.
            var stepFrames = steady.FindAll(f => frameOwnSteps[f] > 0);
            text.AppendLine($"effective {(frames > 1 ? (frames - 1) / (frameTime[frames - 1] - frameTime[1]) : 0f):F1} frames/s over the recorded run; " +
                            $"steady frames {steady.Count} ({steady.Count / Duration:F0}/s), of which with ≥ 1 fixed step {stepFrames.Count}; " +
                            $"fixed steps/frame mean {Mean(steady, frameOwnSteps):F3} max {Max(steady, frameOwnSteps)}; " +
                            $"Movement.FixedStep sample count ≠ steps × ships in {stepMismatch} of {frames - 1} frames");
            // Main Thread includes the frame-rate cap's wait; the active time and Main Thread − WaitForTargetFPS exclude it.
            AppendSeries(text, "main thread frame incl. cap wait (all steady frames)", mainThread, steady, 1e-6, "ms");
            AppendSeries(text, "CPU main thread active (all steady frames)", mainThreadActive, steady, 1e-6, "ms");
            AppendSeries(text, "CPU main thread active (step frames)", mainThreadActive, stepFrames, 1e-6, "ms");
            AppendBusy(text, "main thread − WaitForTargetFPS (all steady frames)", steady);
            AppendBusy(text, "main thread − WaitForTargetFPS (step frames)", stepFrames);
            AppendSeries(text, "movement (FixedStep, inclusive) per frame (all steady frames)", fixedStep, steady, 1e-6, "ms");
            AppendSeries(text, "movement (FixedStep, inclusive) per frame (step frames)", fixedStep, stepFrames, 1e-6, "ms");
            foreach (Series s in new[] { steering, avoidance, motorPropose, guard, motorCommit })
                AppendSeries(text, $"  {s.Name} (step frames)", s, stepFrames, 1e-6, "ms", share: fixedStep);
            AppendExclusive(text, stepFrames);
            foreach (Series s in new[] { neighborQuery, neighborScan, sideClearance })
            {
                AppendSeries(text, $"    {s.Name} (step frames)", s, stepFrames, 1e-6, "ms", share: avoidance);
                AppendCalls(text, $"    {s.Name} calls", s, steady);
            }
            AppendAvoidanceExclusive(text, stepFrames);
            AppendSeries(text, "FixedUpdate.PhysicsFixedUpdate (step frames)", physicsFixed, stepFrames, 1e-6, "ms");
            AppendSeries(text, "FixedUpdate.ScriptRunBehaviourFixedUpdate (step frames)", scriptsFixed, stepFrames, 1e-6, "ms");
            AppendSeries(text, "Perf.Scenario (benchmark overhead, step frames)", scenario, stepFrames, 1e-6, "ms");
            AppendSeries(text, "GC allocated (all steady frames)", gcAlloc, steady, 1.0, "B", total: true);
            AppendSeries(text, "GC allocated (step frames)", gcAlloc, stepFrames, 1.0, "B", total: true);

            // Per fixed step, so frames with 0, 1 or several steps compare.
            if (fixedStep.Valid)
            {
                var perStep = new List<double>();
                foreach (int f in steady)
                    if (frameOwnSteps[f] > 0) perStep.Add(fixedStep.Values[f] * 1e-6 / frameOwnSteps[f]);
                text.AppendLine($"movement per fixed step (all ships): median {Percentile(perStep, 0.5):F4} ms p95 {Percentile(perStep, 0.95):F4} ms (n {perStep.Count})");
            }
        }

        void AppendSeries(StringBuilder text, string label, Series s, List<int> frameSet, double scale, string unit, bool total = false, Series share = null)
        {
            if (!s.Valid)
            {
                text.AppendLine($"{label}: unavailable");
                return;
            }
            var values = new List<double>(frameSet.Count);
            double sum = 0.0;
            foreach (int f in frameSet)
            {
                values.Add(s.Values[f] * scale);
                sum += s.Values[f] * scale;
            }
            string format = unit == "ms" ? "F4" : "F0";
            text.AppendLine($"{label}: median {Percentile(values, 0.5).ToString(format)} {unit} p95 {Percentile(values, 0.95).ToString(format)} {unit} " +
                            $"max {Percentile(values, 1.0).ToString(format)} {unit}" +
                            (total ? $" total {sum:F0} {unit} over {values.Count} frames, nonzero frames {values.FindAll(v => v > 0).Count}" : "") +
                            (share != null && share.Valid ? $", {ShareOf(s, share, frameSet):F1}% of {share.Name}" : ""));
        }

        void AppendBusy(StringBuilder text, string label, List<int> frameSet)
        {
            if (!mainThread.Valid || !waitForTarget.Valid)
            {
                text.AppendLine($"{label}: unavailable");
                return;
            }
            var values = new List<double>(frameSet.Count);
            foreach (int f in frameSet) values.Add((mainThread.Values[f] - (long)waitForTarget.Values[f]) * 1e-6);
            text.AppendLine($"{label}: median {Percentile(values, 0.5):F4} ms p95 {Percentile(values, 0.95):F4} ms max {Percentile(values, 1.0):F4} ms");
        }

        // Summed time of one series as a percentage of another over the same frames.
        static double ShareOf(Series part, Series whole, List<int> frameSet)
        {
            double partSum = 0.0, wholeSum = 0.0;
            foreach (int f in frameSet)
            {
                partSum += part.Values[f];
                wholeSum += whole.Values[f];
            }
            return wholeSum > 0.0 ? partSum / wholeSum * 100.0 : double.NaN;
        }

        // FixedStep time not inside any of its five submarkers (call overhead and the step's own logic).
        void AppendExclusive(StringBuilder text, List<int> steady)
        {
            Series[] children = { steering, avoidance, motorPropose, guard, motorCommit };
            if (!fixedStep.Valid || System.Array.Exists(children, c => !c.Valid))
            {
                text.AppendLine("  FixedStep exclusive: unavailable");
                return;
            }
            var values = new List<double>(steady.Count);
            foreach (int f in steady)
            {
                long rest = fixedStep.Values[f];
                foreach (Series c in children) rest -= c.Values[f];
                values.Add(rest * 1e-6);
            }
            text.AppendLine($"  FixedStep exclusive (step frames): median {Percentile(values, 0.5):F4} ms p95 {Percentile(values, 0.95):F4} ms");
        }

        // Sample counts of one marker over the frames: calls per capture and per step frame.
        void AppendCalls(StringBuilder text, string label, Series s, List<int> frameSet)
        {
            if (!s.Valid) return;
            long calls = 0;
            int stepFrames = 0;
            foreach (int f in frameSet)
            {
                calls += s.Counts[f];
                if (frameOwnSteps[f] > 0) stepFrames++;
            }
            text.AppendLine($"{label}: {calls} over {frameSet.Count} steady frames, {(stepFrames > 0 ? (double)calls / stepFrames : 0.0):F2} per step frame");
        }

        // Movement.Avoidance time not inside its three sibling submarkers (route direction, side application, readout).
        void AppendAvoidanceExclusive(StringBuilder text, List<int> frameSet)
        {
            Series[] children = { neighborQuery, neighborScan, sideClearance };
            if (!avoidance.Valid || System.Array.Exists(children, c => !c.Valid))
            {
                text.AppendLine("    Avoidance exclusive: unavailable");
                return;
            }
            var values = new List<double>(frameSet.Count);
            foreach (int f in frameSet)
            {
                long rest = avoidance.Values[f];
                foreach (Series c in children) rest -= c.Values[f];
                values.Add(rest * 1e-6);
            }
            text.AppendLine($"    Avoidance exclusive (step frames): median {Percentile(values, 0.5):F4} ms p95 {Percentile(values, 0.95):F4} ms");
        }

        void AppendBursts(StringBuilder text)
        {
            for (int c = 0; c < commands.Count; c++)
            {
                CommandRecord command = commands[c];
                var burst = new List<int>();
                int maxPerFrame = 0;
                if (recording && routeRequest.Valid)
                {
                    for (int f = 1; f < frames; f++)
                    {
                        if (routeRequest.Counts[f] == 0 || frameTime[f] < command.Time) continue;
                        if (c + 1 < commands.Count && frameTime[f] >= commands[c + 1].Time) continue;
                        burst.Add(f);
                        maxPerFrame = Mathf.Max(maxPerFrame, routeRequest.Counts[f]);
                    }
                }
                var latency = new List<double>();
                foreach (float ms in command.LatencyMs) latency.Add(ms);
                var latencyFrames = new List<double>();
                foreach (int n in command.LatencyFrames) latencyFrames.Add(n);
                text.AppendLine($"burst {c} at {command.Time:F2} s: {command.Orders} orders, queue depth {command.QueueDepth} (max {command.MaxQueueDepth}), " +
                                $"unresolved {command.Pending}, frames with requests {burst.Count}, requests/frame max {maxPerFrame}, " +
                                $"command→ready median {Percentile(latency, 0.5):F1} ms / {Percentile(latencyFrames, 0.5):F0} frames, " +
                                $"max {Percentile(latency, 1.0):F1} ms / {Percentile(latencyFrames, 1.0):F0} frames");
                if (burst.Count > 0)
                {
                    AppendSeries(text, "  burst RouteRequest per frame", routeRequest, burst, 1e-6, "ms");
                    AppendBusy(text, "  burst main thread − WaitForTargetFPS", burst);
                    AppendSeries(text, "  burst GC allocated", gcAlloc, burst, 1.0, "B", total: true);
                }
            }
        }

        // Ships blocked continuously for at least SustainedBlock, with their longest block and final position.
        string DescribeBlockers()
        {
            var parts = new List<string>();
            for (int i = 0; i < ships.Count; i++)
            {
                if (longestBlock[i] < SustainedBlock) continue;
                Vector2 p = ships[i].Motor.State.Position;
                parts.Add($"{ships[i].name} {longestBlock[i]:F1} s at ({p.x:F0}, {p.y:F0})");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "none";
        }

        string DescribeObstacles()
        {
            var parts = new List<string>();
            for (int o = 0; o < minObstacleDistance.Length; o++)
            {
                int inside = 0;
                for (int i = 0; i < engaged.GetLength(0); i++) if (engaged[i, o]) inside++;
                Vector3 p = Obstacles[o].position;
                parts.Add($"({p.x:F0}, {p.z:F0}) min center distance {minObstacleDistance[o]:F2} u, ships within {EngagementDistance} u {inside}");
            }
            return string.Join("; ", parts);
        }

        void LogEnvironment()
        {
            Debug.Log($"[Perf] environment: {SystemInfo.processorType} ({SystemInfo.processorCount} threads), {SystemInfo.graphicsDeviceName}, " +
                      $"{SystemInfo.systemMemorySize} MB, {SystemInfo.operatingSystem}; Unity {Application.unityVersion}, " +
                      $"development {Debug.isDebugBuild}, editor {Application.isEditor}, profiler enabled {UnityEngine.Profiling.Profiler.enabled} " +
                      $"log '{UnityEngine.Profiling.Profiler.logFile}'; {Screen.width}×{Screen.height} {Screen.fullScreenMode}, " +
                      $"quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', vSyncCount {QualitySettings.vSyncCount}, " +
                      $"targetFrameRate {Application.targetFrameRate}, fixedDeltaTime {Time.fixedDeltaTime}, maximumDeltaTime {Time.maximumDeltaTime}");
            ShipMovementProfile p = ShipPrefab.Profile;
            Debug.Log($"[Perf] profile {p.name}: MaxSpeed {p.MaxSpeed} FootprintRadius {p.FootprintRadius} LookAhead {p.LookAheadDistance} " +
                      $"AvoidanceOffset {p.AvoidanceOffset} NeighborSampleInterval {p.NeighborSampleInterval} NeighborSearchRadius {p.NeighborSearchRadius} " +
                      $"NeighborBufferCapacity {p.NeighborBufferCapacity}; MaxRequestsPerFrame " +
                      $"{(RouteRequestQueue.Instance != null ? RouteRequestQueue.Instance.MaxRequestsPerFrame : -1)}");
        }

        static int CountAtLeast(float[] values, float threshold)
        {
            int n = 0;
            foreach (float value in values) if (value >= threshold) n++;
            return n;
        }

        static int CountTrue(bool[] flags)
        {
            int n = 0;
            foreach (bool flag in flags) if (flag) n++;
            return n;
        }

        static double Mean(List<int> frameSet, int[] values)
        {
            if (frameSet.Count == 0) return 0.0;
            double sum = 0.0;
            foreach (int f in frameSet) sum += values[f];
            return sum / frameSet.Count;
        }

        static int Max(List<int> frameSet, int[] values)
        {
            int max = 0;
            foreach (int f in frameSet) max = Mathf.Max(max, values[f]);
            return max;
        }

        // Nearest-rank percentile; NaN when empty.
        static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            int rank = Mathf.Clamp(Mathf.CeilToInt((float)(p * sorted.Count)) - 1, 0, sorted.Count - 1);
            return sorted[rank];
        }

        void OnGUI()
        {
            // IMGUI text generation allocates every frame; draw nothing during the capture window.
            if (!Finished && Time.time - startTime >= Warmup) return;
            GUI.Label(new Rect(10f, 10f, 1400f, 600f), Finished ? summary : liveText);
        }
    }
}
