# HANDOFF

## Project state

- Unity 6000.6.4f1, URP 17.6.0, Input System 1.20.0.
- Unity AI Navigation `com.unity.ai.navigation` 2.0.16.
- One gameplay system: 2.5D ship movement with physical blocking (Issue #1) and predictive avoidance between moving ships (Issue #3).

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
| `ShipMotor` | Authoritative XZ position, heading, forward speed. Turn ≤ `TurnRate·dt`; accel/decel limits; arrival cap `sqrt(2·Dec·max(0, remaining − ArrivalRadius))`; pure-pursuit turn-rate cap; optional `speedCap`. Writes the pose to a kinematic Rigidbody. |
| `CollisionGuard` | Swept vertical-capsule query (footprint + skin) against `Ship` and `Obstacle` layers; returns permitted displacement and a blocked flag. Blocked motion keeps only the speed it actually moved. |
| `ShipVisual` | Applies `VisualAltitudeOffset` to the `Visual` child only. |
| `ShipMovementDebug` | Gizmos: footprint (green, red when blocked), route corners (cyan), progress point (white), look-ahead target (yellow), avoidance target (magenta), threat line and predicted closest-approach positions (orange). Visible in the Scene view and in the Game view with Gizmos on. |

Arrival completes the order when the remaining route distance is ≤ `ArrivalRadius`. Avoidance keeps the route's remaining distance, so arrival is unaffected.

## Editable values

- `Assets/Settings/Movement/DefaultShipProfile.asset`: MaxSpeed 8, Acceleration 2, Deceleration 3, TurnRate 90, FootprintRadius 2, ArrivalRadius 2, CollisionSkin 0.05, LookAheadDistance 6; Avoidance: PredictionHorizon 4, NeighborSearchRadius 60, NeighborSampleInterval 0.1, SafetyMargin 1, AvoidanceOffset 4, ThreatReleaseDelay 0.5, AvoidanceRecoveryTime 2, NeighborBufferCapacity 16 (read at ship initialization; changing it in Play needs re-entering Play). Shared by every ship that references it; a different ship type needs its own profile. The ship's collider radius is synced from `FootprintRadius`.
- `Assets/Prefabs/Ship.prefab` › `ShipVisual.VisualAltitudeOffset` (0); `MovingShipAvoidance.NeighborLayers` (`Ship`).
- `MovementTest` scene › `Route Request Queue` › `MaxRequestsPerFrame` (2).
- Project Settings › Navigation › agent type `Ship`: radius 2.5 (footprint 2 + 0.5 clearance, provisional), cell size 0.25. Changing it requires rebaking the `Navigation` NavMeshSurface.
- Project Settings › Time › Fixed Timestep 0.02.
- Layers: `Ship` (6), `Obstacle` (7). The floor stays on Default and is ignored by movement queries.

All values are provisional. The starboard head-on tie-break is a provisional prototype value, not a passing convention.

## Test scenes

`Assets/Scenes/MovementTest.unity`: floor, static obstacle (radius 10 at origin), Ship A (-40, 0) facing +X, Ship B (30, 0) facing −X, baked `Navigation` NavMeshSurface (ships excluded), `Movement Acceptance Scenario`.

Press Play: the scenario orders A to (15, 0), then to (40, 0), and shows PASS/FAIL per criterion in its Inspector fields, on screen, and as `[Acceptance]` log lines. Expected result: A stops against B (route around ships is out of scope). B never moves, so avoidance stays inactive.

`Assets/Scenes/AvoidanceTest.unity`: floor, Ship A (-30, 0) facing +X, Ship B (30, 0) facing −X, baked `Navigation` NavMeshSurface (ships excluded), `Head-On Avoidance Scenario`. Press Play: A is ordered to (30, 0) and B to (-30, 0); criteria (a)–(e) of Issue #3 plus (f) coherent pass (per ship: 1 activation, 1 release, ≤ 0.5 u to the other side, ≤ 1 lateral reversal at 0.5 u hysteresis, ≤ 0.5 u lateral deviation at arrival) show PASS/FAIL in the Inspector, on screen, and as `[Acceptance]` log lines. Trails are logged as `[Trail]` lines every 0.5 s and drawn as gizmos (A cyan, B yellow). Last result (AvoidanceOffset 4): both arrive in 10.68 s at 1.97 u, min separation 7.8 u, first decision at 46 u, both pass starboard with 3.9 u peak deviation, one reversal each, 0.17 u lateral at arrival, no guard blocking. At AvoidanceOffset 8 the separation was 15.5 u.

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
- Performance not measured, including avoidance query cost at scale.
- `Assets/TutorialInfo/Icons/URP.png` is damaged in the working tree (not part of any change).

## Next

Not defined; needs a ticket. Deferred owner decisions: permanent passing/yield convention, final forward-only vs lateral-thrust handling, avoidance feel.
