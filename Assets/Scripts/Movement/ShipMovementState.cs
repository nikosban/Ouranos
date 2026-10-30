using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>Authoritative planar movement state. Position is XZ; heading is yaw in degrees (0 = +Z).</summary>
    public struct ShipMovementState
    {
        public Vector2 Position;
        public float Heading;
        public float Speed;

        public Vector2 Forward => HeadingToDirection(Heading);

        public static Vector2 HeadingToDirection(float heading)
        {
            float radians = heading * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        }

        public static float DirectionToHeading(Vector2 direction)
        {
            return Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
        }

        public static Vector2 ToPlanar(Vector3 position) => new Vector2(position.x, position.z);

        public static Vector3 ToWorld(Vector2 position) => new Vector3(position.x, 0f, position.y);
    }
}
