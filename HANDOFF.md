# HANDOFF

## Project state

- Unity 6000.6.4f1, URP 17.6.0, Input System 1.20.0.
- Unity AI Navigation `com.unity.ai.navigation` 2.0.16.
- One gameplay system: 2.5D ship movement with physical blocking (Issue #1), predictive avoidance between moving ships (Issue #3), and Group Move by translated destinations (Issue #5).
- Movement performance profiled at 10/50/100 ships with a dedicated benchmark scene and profiler markers (Issue #7); one measured allocation removed.

## Decisions

From the Issue #1 decision log:

- DECIDED: Ouranos uses 2.5D navigation on XZ with cosmetic altitude variation. Navigation stays planar and visuals stay independent.
- DECIDED: Ships and obstacles physically block movement; ships avoid or stop, never pass through occupied space.
- DECIDED: Intermediate path waypoints guide continuous look-ahead steering and are not stop destinations.
- DECIDED: The movement system is built in independently verifiable vertical slices, starting with single-ship movement and blocking.
- DECIDED: Acceptance for movement slices is an in-game scenario with recorded metrics, not automated PlayMode tests (Issue #1 review).

From the Issue #3 decision log:

- DECIDED: Predictive moving-ship avoidance is a temporary steering/speed adjustment before ShipMotor, while CollisionGuard remains the final blocking authority.
- DECIDED: Avoidance begins with bounded local nearest-approach prediction, not a global multi-agent solver.

From the Issue #5 decision log:

- DECIDED: Group Move assigns each selected ship a destination obtained by translating its current XZ position by the group's centroid-to-target displacement, with no rotation. — Preserve the initial arrangement at the commanded destination, not during travel.
- DECIDED: Group Move issues independent per-ship orders with no speed synchronization, waiting or persistent formation constraints. — Group movement and formations are separate components.
- DECIDED: Persistent coordinated formations, including wedge layouts and in-transit relative-position maintenance, are a later independent system. — Keep the group command dispatcher minimal.

From the Issue #7 decision log:

- DECIDED: Stage 4 profiles movement at scale before optimizing, and changes only measured bottlenecks while preserving gameplay. — Avoid speculative rewrites and keep movement behavior stable.
- DECIDED: Performance profiling precedes ship selection UX and persistent formations. — Establish the scaling baseline before adding more gameplay systems.
- DECIDED: Movement benchmarks run in a Windows Development Player capped at 60 fps (VSync off), reporting per-rendered-frame and per-step-frame costs (Issue #7 review).
- DECIDED: Benchmark traffic uses opposing lanes offset by `LaneOffset` 60 u; overlapping head-on lanes deadlock and were rejected as the baseline (Issue #7 owner decision).

## Movement system (`Assets/Scripts/Movement/`)

Each ship runs one fixed step (`ShipMovement`, 0.02 s fixed timestep):
route refresh → look-ahead steering → moving-ship avoidance → motor proposal → collision guard → motor commit; the visual reads the result.

| Part | Responsibility |
| --- | --- |
| `MovementOrders` | The only order seam: `Move(destinationXZ)`, `Stop()`. A new order supersedes the old route. |
| `RoutePlanner` / `Route` | NavMesh path request for agent type `Ship`, the retained corner polyline, and monotonic progress. Missing or partial routes log a warning and stop the ship. `IsNavigable(from, to)` is a query-only NavMesh raycast for the same agent type. |
| `RouteRequestQueue` | Scene component; at most `MaxRequestsPerFrame` (2) synchronous path requests per rendered frame. |
| `Steering` | Look-ahead target `LookAheadDistance` along the route, crossing corners; backs off toward the ship if the straight shortcut lacks clearance (capsule cast at footprint + skin against the `Obstacle` layer). |
| `MovingShipAvoidance` | Every `NeighborSampleInterval` (phases staggered across ships): bounded `OverlapSphereNonAlloc` on the `Ship` layer, skipping ships at ≤ 0.1 u/s; constant-velocity closest approach per neighbor; the earliest imminent conflict (`0 < t ≤ PredictionHorizon`, distance < both footprints + `SafetyMargin`) becomes the threat. Offsets the route target by `AvoidanceOffset` along the route's starboard/port normal (route direction = progress point → route target, independent of the ship's sideways motion), so the ship follows a corridor parallel to the route, on the side away from the threat (exact head-on: starboard for both, provisional); opposite side only if the preferred one lacks static clearance or NavMesh; neither → speed cap 0. Holds threat and side until the threat is behind along the route by both footprints, or it disappears, stops, leaves the search radius, or keeps separating without danger for `ThreatReleaseDelay`; a strictly earlier threat from another ship replaces it. After release the offset ramps linearly to 0 over `AvoidanceRecoveryTime`. Resets immediately when the route object changes (new order, stop, arrival). Never writes Transform, state, route or orders. Inspector readout: threat, recovering, time/distance of closest approach, side, temporary target, speed cap, guard blocked, overflow. |
| `ShipMotor` | Authoritative XZ position, heading, forward speed. Turn ≤ `TurnRate·dt`; accel/decel limits; arrival cap `sqrt(2·Dec·max(0, remaining − ArrivalRadius))`; pure-pursuit turn-rate cap; optional `speedCap`. The desired speed is the minimum of these caps via two-argument `Mathf.Min` calls (the `params` overload allocated 48 B per ship per step). Writes the pose to a kinematic Rigidbody. |
| `CollisionGuard` | Swept vertical-capsule query (footprint + skin) against `Ship` and `Obstacle` layers; returns permitted displacement and a blocked flag. Blocked motion keeps only the speed it actually moved. |
| `ShipVisual` | Applies `VisualAltitudeOffset` to the `Visual` child only. |
| `ShipMovementDebug` | Gizmos: footprint (green, red when blocked), route corners (cyan), progress point (white), look-ahead target (yellow), avoidance target (magenta), threat line and predicted closest-approach positions (orange). Visible in the Scene view and in the Game view with Gizmos on. |

**Group Move** (`GroupMoveDispatcher.Move(ships, targetAnchor)`, static, stateless): skips null, destroyed, inactive and duplicate ships, snapshots each remaining ship's `Motor.State.Position`, computes `delta = targetAnchor − centroid`, and calls `Orders.Move(position + delta)` once per ship. Returns the `GroupMoveAssignment` list (ship, destination); zero valid ships logs a warning and issues nothing. The target anchor is the destination centroid. No rotation, slotting, leader, speed sync, waiting or persistent group state; the caller owns the ship set, and each ship's `MovementOrders.Destination` stays authoritative. A new group move takes a fresh snapshot and supersedes the ships' orders. Path requests go through `RouteRequestQueue` as usual; a failed route stops only that ship. No selection UI exists; the acceptance scenario is the only caller.

Arrival completes the order when the remaining route distance is ≤ `ArrivalRadius`. Avoidance keeps the route's remaining distance, so arrival is unaffected.

**Profiler markers** (Issue #7): `ShipMovement` wraps each fixed step in `Movement.FixedStep` (inclusive) with children `Movement.Steering`, `Movement.Avoidance`, `Movement.MotorPropose`, `Movement.Guard`, `Movement.MotorCommit`; `RouteRequestQueue` wraps each request in `Movement.RouteRequest` and exposes `PendingCount`. Markers only; the pipeline order is unchanged.

## Editable values

- `Assets/Settings/Movement/DefaultShipProfile.asset`: MaxSpeed 8, Acceleration 2, Deceleration 3, TurnRate 90, FootprintRadius 2, ArrivalRadius 2, CollisionSkin 0.05, LookAheadDistance 6; Avoidance: PredictionHorizon 4, NeighborSearchRadius 60, NeighborSampleInterval 0.1, SafetyMargin 1, AvoidanceOffset 4, ThreatReleaseDelay 0.5, AvoidanceRecoveryTime 2, NeighborBufferCapacity 16 (read at ship initialization; changing it in Play needs re-entering Play). Shared by every ship that references it; a different ship type needs its own profile. The ship's collider radius is synced from `FootprintRadius`.
- `Assets/Prefabs/Ship.prefab` › `ShipVisual.VisualAltitudeOffset` (0); `MovingShipAvoidance.NeighborLayers` (`Ship`).
- `MovementTest` scene › `Route Request Queue` › `MaxRequestsPerFrame` (2).
- Project Settings › Navigation › agent type `Ship`: radius 2.5 (footprint 2 + 0.5 clearance, provisional), cell size 0.25. Changing it requires rebaking the `Navigation` NavMeshSurface.
- Project Settings › Time › Fixed Timestep 0.02.
- Layers: `Ship` (6), `Obstacle` (7). The floor stays on Default and is ignored by movement queries.
- `MovementPerformanceTest` scene › `Movement Performance Scenario` (benchmark configuration, not gameplay): Count 100 (1–200), Seed 1, Run 1, WestAnchor (−150, 0), EastAnchor (150, 0), LaneOffset 60 u, Columns 5, Spacing 10 u, Jitter 1 u, CommandInterval 50 s, TargetFrameRate 60, Warmup 10 s, Duration 30 s, TrackSeparation off; criteria MinActiveFraction 0.7, MovingSpeed 0.1 u/s, SustainedBlock 2 s, EngagementDistance 16 u, MinShipDistance 4 u, MinObstacleDistance 12 u. Player command-line overrides: `-perfCount N`, `-perfRun K`, `-perfLane L`, `-perfValidate`, `-perfQuit`.

All values are provisional. The starboard head-on tie-break is a provisional prototype value, not a passing convention.

## Test scenes

`Assets/Scenes/MovementTest.unity`: floor, static obstacle (radius 10 at origin), Ship A (-40, 0) facing +X, Ship B (30, 0) facing −X, baked `Navigation` NavMeshSurface (ships excluded), `Movement Acceptance Scenario`.

Press Play: the scenario orders A to (15, 0), then to (40, 0), and shows PASS/FAIL per criterion in its Inspector fields, on screen, and as `[Acceptance]` log lines. Expected result: A stops against B (route around ships is out of scope). B never moves, so avoidance stays inactive.

`Assets/Scenes/AvoidanceTest.unity`: floor, Ship A (-30, 0) facing +X, Ship B (30, 0) facing −X, baked `Navigation` NavMeshSurface (ships excluded), `Head-On Avoidance Scenario`. Press Play: A is ordered to (30, 0) and B to (-30, 0); criteria (a)–(e) of Issue #3 plus (f) coherent pass (per ship: 1 activation, 1 release, ≤ 0.5 u to the other side, ≤ 1 lateral reversal at 0.5 u hysteresis, ≤ 0.5 u lateral deviation at arrival) show PASS/FAIL in the Inspector, on screen, and as `[Acceptance]` log lines. Trails are logged as `[Trail]` lines every 0.5 s and drawn as gizmos (A cyan, B yellow). Last result (AvoidanceOffset 4): both arrive in 10.68 s at 1.97 u, min separation 7.8 u, first decision at 46 u, both pass starboard with 3.9 u peak deviation, one reversal each, 0.17 u lateral at arrival, no guard blocking. At AvoidanceOffset 8 the separation was 15.5 u.

`Assets/Scenes/GroupMoveTest.unity`: the `MovementTest` layout (floor, radius-10 obstacle at origin, baked `Navigation` NavMeshSurface, `Route Request Queue`) with Ship A (-38, -18), B (-31, 4), C (-21, 14), all facing +X, and `Group Move Scenario`. Press Play: one group move to anchor (30, 0); criteria (1) command (3 orders, assigned destinations and centroid within 0.01 u), (2) independent arrival per ship, (3) ship–ship ≥ 4 u and obstacle-center ≥ 12 u, (4) no order replaced show PASS/FAIL in the Inspector, on screen, and as `[Acceptance]` log lines; an unexpected Stop counts as that ship's failure. Assigned destinations are drawn as magenta gizmos. Last result: assigned (22, -18), (29, 4), (39, 14) exactly; arrivals A 10.56 s, B 10.86 s, C 10.58 s, each 1.92 u from its target; min ship–ship 11.1 u (B–C), min obstacle B 12.05 u.

`Assets/Scenes/MovementPerformanceTest.unity`: 400 × 400 u floor, radius-10 obstacles at (0, 0), (0, 60), (0, −60), its own baked `Navigation` NavMeshSurface (ships excluded), `Route Request Queue`, top-down camera, `Movement Performance Scenario`. Press Play, or run a Development Player with `-profiler-enable -profiler-log-file <file>.raw -perfCount N -perfQuit`. Count ships spawn in two 5-column grids (10 u pitch, seeded jitter): West centered at (−150, 60) facing +X, East at (150, −60) facing −X; one group move per side to the opposite end, re-commanded every 50 s. After a 10 s warmup a 30 s capture records the workload (active/moving %, routes, avoidance activations, neighbor overflow, blocking and sustained blockers, obstacle engagement) and per-frame `ProfilerRecorder` values (main thread minus `WaitForTargetFPS`, `Movement.*` markers, physics, GC), split into steady frames and the route-request burst; results appear as `[Perf]` log lines and on screen after capture. `-perfValidate` adds the O(n²) separation check (not for timed runs).

Last result (Windows Development Player, Ryzen 5 7600X / RTX 4070 SUPER, 60 fps cap, baseline and optimized players interleaved, 3 runs each at 100 ships): movement per rendered frame median 0.57–0.59 ms, p95 0.68–0.74 ms; main thread minus cap wait p95 1.56–1.64 ms; `Movement.Avoidance` about 40% of movement (p95 0.28–0.30 ms); steady GC 588 B per step frame (all from avoidance); 96.5% of ships moving, about 110 avoidance activations, 18 sustained blockers; route burst 49 frames at 2 requests/frame. At 50 ships movement p95 is 0.38–0.39 ms; at 10 ships 0.085 ms (no avoidance encounters). Validation at 100 ships: min ship–ship 4.05 u, min obstacle-center 12.27 u, no failed routes or unexpected stops.

## Known limitations and debt

- Circular footprints approximate hulls; routes ignore turn radius.
- One NavMesh agent type means one baked clearance; other ship radii need their own agent types and bakes.
- Ships are not in the NavMesh; a ship blocked by a stopped ship waits in place with no recovery (avoidance ignores ships at ≤ 0.1 u/s).
- Avoidance predicts at constant current velocity (no anticipated turns or acceleration) and handles one threat at a time. Only the head-on case is verified; crossing, near-parallel and multi-ship traffic are untested.
- The neighbor buffer is bounded; when it fills, `Overflowed` is set, `OverflowCount` increments, and some neighbors may be missed. Hard blocking is the fallback.
- If the held side loses static clearance mid-encounter, the ship brakes instead of switching sides.
- The corridor offset is a fixed `AvoidanceOffset`, not sized to the threat. At 4 u the head-on pass keeps 7.8 u between centers; the final clearance is an owner feel choice.
- Ships finish recovery with a small lateral residual (0.17 u in the head-on test). A pass that ends near the destination can arrive further off the route.
- Neighbor states may be up to one fixed step old, because ships' FixedUpdate order is undefined.
- Look-ahead shortcuts are checked at footprint + skin, so ships can ride the exact collision clearance (12.05 u from the test obstacle) rather than the NavMesh's 0.5 u margin.
- Forward-only thrust with capped turning is a reversible prototype assumption; final handling/strafe behavior is UNDECIDED (owner).
- Pose writes teleport the kinematic body each step; no render interpolation.
- Gizmos debug view exists only in the Editor.
- Group Move preserves the arrangement only in the assigned destinations. Final positions differ by the arrival radius (about 1.9 u short along each route) or NavMesh snapping; a failed ship is reported, not re-slotted. Congested arrivals and overlapping starts are not handled.
- Performance is measured on one desktop machine in a Development Player only. Target hardware, frame-rate budget and supported fleet size are UNDECIDED (owner); the numbers are not a shipping claim.
- `Movement.Avoidance` (`MovingShipAvoidance::Step/Evaluate`) is the largest movement cost at 50–100 ships (about 40%), and total movement still crosses the Issue #7 gate (p95 ≥ 0.5 ms per rendered frame at 100 ships). Not optimized: it needs sub-attribution (neighbor query, neighbor loop, `SideIsClear` NavMesh queries) and a separate review.
- `MovingShipAvoidance.UpdateReadout` reads `Threat.name` every fixed step while a threat is held; the profiler attributes about 0.9 MB per 33 s at 100 ships (about 28 B per call) to `Movement.Avoidance`. Not fixed (not approved in Issue #7).
- At 50 and 100 ships the 16-slot neighbor buffer overflows on nearly every avoidance evaluation in the benchmark, so some neighbors are missed; hard blocking is the fallback.
- Dense head-on traffic deadlocks: opposing 5-column streams on one line, or lanes overlapping (`LaneOffset` ≤ 50 u), leave ships blocked for 20 s or more at every Count, because a ship blocked by a stopped ship has no recovery. This is movement-behavior debt, separate from performance. The benchmark avoids it with 60 u lanes, so it measures same-lane encounters, not head-on ones.
- Route-request latency follows the 2-per-frame budget: a 100-ship command needs 49 rendered frames (about 0.7 s at 60 fps after startup) until the last route is ready.
- `Assets/TutorialInfo/Icons/URP.png` is damaged in the working tree (not part of any change).

## Next

Not defined; needs a ticket. Candidate from Issue #7: sub-attribute and review the `MovingShipAvoidance` cost and its per-step `name` allocation. Deferred owner decisions: target hardware, frame-rate budget and supported fleet size; selection UX, formation layouts and persistent formation behavior, permanent passing/yield convention, final forward-only vs lateral-thrust handling, avoidance feel.
