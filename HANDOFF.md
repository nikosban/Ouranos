# HANDOFF

## Project state

- Unity 6000.6.4f1, URP 17.6.0, Input System 1.20.0.
- Unity AI Navigation `com.unity.ai.navigation` 2.0.16.
- One gameplay system: single-ship 2.5D movement with physical blocking (Issue #1).

## Decisions

From the Issue #1 decision log:

- DECIDED: Ouranos uses 2.5D navigation on XZ with cosmetic altitude variation. Navigation stays planar and visuals stay independent.
- DECIDED: Ships and obstacles physically block movement; ships avoid or stop, never pass through occupied space.
- DECIDED: Intermediate path waypoints guide continuous look-ahead steering and are not stop destinations.
- DECIDED: The movement system is built in independently verifiable vertical slices, starting with single-ship movement and blocking.
- DECIDED: Acceptance for movement slices is an in-game scenario with recorded metrics, not automated PlayMode tests (Issue #1 review).

## Movement system (`Assets/Scripts/Movement/`)

Each ship runs one fixed step (`ShipMovement`, 0.02 s fixed timestep):
route refresh → look-ahead steering → motor proposal → collision guard → motor commit; the visual reads the result.

| Part | Responsibility |
| --- | --- |
| `MovementOrders` | The only order seam: `Move(destinationXZ)`, `Stop()`. A new order supersedes the old route. |
| `RoutePlanner` / `Route` | NavMesh path request for agent type `Ship`, the retained corner polyline, and monotonic progress. Missing or partial routes log a warning and stop the ship. |
| `RouteRequestQueue` | Scene component; at most `MaxRequestsPerFrame` (2) synchronous path requests per rendered frame. |
| `Steering` | Look-ahead target `LookAheadDistance` along the route, crossing corners; backs off toward the ship if the straight shortcut lacks clearance (capsule cast at footprint + skin against the `Obstacle` layer). |
| `ShipMotor` | Authoritative XZ position, heading, forward speed. Turn ≤ `TurnRate·dt`; accel/decel limits; arrival cap `sqrt(2·Dec·max(0, remaining − ArrivalRadius))`; pure-pursuit turn-rate cap. Writes the pose to a kinematic Rigidbody. |
| `CollisionGuard` | Swept vertical-capsule query (footprint + skin) against `Ship` and `Obstacle` layers; returns permitted displacement and a blocked flag. Blocked motion keeps only the speed it actually moved. |
| `ShipVisual` | Applies `VisualAltitudeOffset` to the `Visual` child only. |
| `ShipMovementDebug` | Gizmos: footprint (green, red when blocked), route corners (cyan), progress point (white), look-ahead target (yellow). Visible in the Scene view and in the Game view with Gizmos on. |

Arrival completes the order when the remaining route distance is ≤ `ArrivalRadius`.

## Editable values

- `Assets/Settings/Movement/DefaultShipProfile.asset`: MaxSpeed 8, Acceleration 2, Deceleration 3, TurnRate 90, FootprintRadius 2, ArrivalRadius 2, CollisionSkin 0.05, LookAheadDistance 6. Shared by every ship that references it; a different ship type needs its own profile. The ship's collider radius is synced from `FootprintRadius`.
- `Assets/Prefabs/Ship.prefab` › `ShipVisual.VisualAltitudeOffset` (0).
- `MovementTest` scene › `Route Request Queue` › `MaxRequestsPerFrame` (2).
- Project Settings › Navigation › agent type `Ship`: radius 2.5 (footprint 2 + 0.5 clearance, provisional), cell size 0.25. Changing it requires rebaking the `Navigation` NavMeshSurface.
- Project Settings › Time › Fixed Timestep 0.02.
- Layers: `Ship` (6), `Obstacle` (7). The floor stays on Default and is ignored by movement queries.

All values are provisional.

## Test scene

`Assets/Scenes/MovementTest.unity`: floor, static obstacle (radius 10 at origin), Ship A (-40, 0) facing +X, Ship B (30, 0) facing −X, baked `Navigation` NavMeshSurface (ships excluded), `Movement Acceptance Scenario`.

Press Play: the scenario orders A to (15, 0), then to (40, 0), and shows PASS/FAIL per criterion in its Inspector fields, on screen, and as `[Acceptance]` log lines. Expected result: A stops against B (route around ships is out of scope).

## Known limitations and debt

- Circular footprints approximate hulls; routes ignore turn radius.
- One NavMesh agent type means one baked clearance; other ship radii need their own agent types and bakes.
- Ships are not in the NavMesh; a ship blocked by another ship waits in place with no recovery.
- Look-ahead shortcuts are checked at footprint + skin, so ships can ride the exact collision clearance (12.05 u from the test obstacle) rather than the NavMesh's 0.5 u margin.
- Forward-only thrust with capped turning is a reversible prototype assumption; final handling/strafe behavior is UNDECIDED (owner).
- Pose writes teleport the kinematic body each step; no render interpolation.
- Gizmos debug view exists only in the Editor.
- Performance not measured.
- `Assets/TutorialInfo/Icons/URP.png` is damaged in the working tree (not part of any change).

## Next

Stage 2: predictive avoidance between moving ships (not started; needs a ticket).
