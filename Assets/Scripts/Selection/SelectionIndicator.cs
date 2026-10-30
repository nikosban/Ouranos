using Ouranos.Movement;
using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Provisional placeholder: a flat ring around the selected ship, drawn by a LineRenderer on this object.
    /// Reads <see cref="ShipSelection"/> only; never writes the selection and never touches the ship.
    /// Final indicator art is an owner decision.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class SelectionIndicator : MonoBehaviour
    {
        public ShipSelection Selection;

        [Tooltip("Ring colour (provisional placeholder).")]
        public Color Color = new Color(0.9f, 0.9f, 0.9f, 1f);
        [Tooltip("Ring line width (u).")]
        [Range(0.05f, 2f)] public float LineWidth = 0.3f;
        [Tooltip("Ring radius beyond the ship's footprint radius (u).")]
        [Range(0f, 10f)] public float RadiusMargin = 1f;
        [Tooltip("Ring height above the movement plane Y = 0 (u).")]
        [Range(0.05f, 5f)] public float Height = 0.2f;
        [Tooltip("Number of line segments in the ring.")]
        [Range(12, 128)] public int Segments = 48;

        LineRenderer line;

        void Awake()
        {
            line = GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
        }

        void LateUpdate()
        {
            ShipMovement ship = Selection != null && Selection.Selected.Count > 0 ? Selection.Selected[0] : null;
            if (!ShipSelection.IsSelectable(ship))
            {
                line.enabled = false;
                return;
            }

            float radius = (ship.Profile != null ? ship.Profile.FootprintRadius : 0f) + RadiusMargin;
            Vector2 center = ship.Motor.State.Position;
            line.startColor = line.endColor = Color;
            line.startWidth = line.endWidth = LineWidth;
            line.positionCount = Segments;
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * 2f * Mathf.PI / Segments;
                line.SetPosition(i, new Vector3(center.x + radius * Mathf.Cos(angle), Height, center.y + radius * Mathf.Sin(angle)));
            }
            line.enabled = true;
        }
    }
}
