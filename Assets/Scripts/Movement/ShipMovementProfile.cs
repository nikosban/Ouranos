using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Shared handling values for one ship type. Values are provisional (Issue #1).
    /// Editing this asset changes every ship that references it.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipMovementProfile", menuName = "Ouranos/Ship Movement Profile")]
    public class ShipMovementProfile : ScriptableObject
    {
        [Tooltip("Top forward speed (u/s).")]
        [Min(0f)] public float MaxSpeed = 8f;

        [Tooltip("Forward acceleration (u/s²).")]
        [Min(0.01f)] public float Acceleration = 2f;

        [Tooltip("Forward deceleration used for braking and arrival (u/s²).")]
        [Min(0.01f)] public float Deceleration = 3f;

        [Tooltip("Maximum heading change (degrees/s).")]
        [Min(0.01f)] public float TurnRate = 90f;

        [Tooltip("Radius of the circular planar collision footprint (u).")]
        [Min(0.01f)] public float FootprintRadius = 2f;

        [Tooltip("Remaining route distance at which a move order counts as arrived (u).")]
        [Min(0f)] public float ArrivalRadius = 2f;

        [Tooltip("Gap kept between the footprint and blocking geometry (u).")]
        [Min(0.001f)] public float CollisionSkin = 0.05f;

        [Tooltip("Distance along the route ahead of the ship's progress that steering aims at (u).")]
        [Min(0.01f)] public float LookAheadDistance = 6f;

        [Header("Avoidance (provisional, Issue #3)")]
        [Tooltip("How far ahead in time closest approaches to moving ships are predicted (s).")]
        [Min(0.1f)] public float PredictionHorizon = 4f;

        [Tooltip("Radius searched for moving ship neighbors (u).")]
        [Min(1f)] public float NeighborSearchRadius = 60f;

        [Tooltip("Interval between neighbor searches and predictions (s). Steps in between reuse the last decision.")]
        [Min(0.02f)] public float NeighborSampleInterval = 0.1f;

        [Tooltip("Clearance required beyond the two footprints at closest approach (u).")]
        [Min(0f)] public float SafetyMargin = 1f;

        [Tooltip("Lateral offset of the temporary steering target from the route target (u).")]
        [Min(0.1f)] public float AvoidanceOffset = 8f;

        [Tooltip("Time a held threat must keep separating without danger before avoidance releases it before passing (s).")]
        [Min(0f)] public float ThreatReleaseDelay = 0.5f;

        [Tooltip("Time over which the lateral offset ramps back to the route after the threat has passed (s).")]
        [Min(0f)] public float AvoidanceRecoveryTime = 2f;

        [Tooltip("Size of the fixed neighbor query buffer. Read when a ship initializes; changing it in Play needs re-entering Play.")]
        [Min(1)] public int NeighborBufferCapacity = 16;
    }
}
