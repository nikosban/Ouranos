using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>Applies cosmetic altitude to the visual child only; movement and collision stay on XZ.</summary>
    public class ShipVisual : MonoBehaviour
    {
        public Transform Visual;

        [Tooltip("Cosmetic height of the visual child above the movement plane (u).")]
        public float VisualAltitudeOffset = 0f;

        void LateUpdate()
        {
            if (Visual != null)
                Visual.localPosition = new Vector3(0f, VisualAltitudeOffset, 0f);
        }
    }
}
