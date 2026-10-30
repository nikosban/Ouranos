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
    }
}
