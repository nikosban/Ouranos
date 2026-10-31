using System.Collections.Generic;
using Ouranos.Movement;
using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Provisional placeholder: a flat ring around every selected ship. This object's LineRenderer draws the first ring
    /// and is the single configuration source; further rings are runtime child objects carrying only a LineRenderer with
    /// the same material and settings (<see cref="RingLines"/>). Reads <see cref="ShipSelection"/> only; never writes the
    /// selection and never touches the ships. Final indicator art is an owner decision.
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

        readonly List<LineRenderer> rings = new List<LineRenderer>();

        void Awake()
        {
            LineRenderer line = GetComponent<LineRenderer>();
            RingLines.Prepare(line);
            rings.Add(line);
        }

        void LateUpdate()
        {
            int used = 0;
            if (Selection != null)
                foreach (ShipMovement ship in Selection.Selected)
                    if (ShipSelection.IsSelectable(ship))
                    {
                        float radius = (ship.Profile != null ? ship.Profile.FootprintRadius : 0f) + RadiusMargin;
                        RingLines.Draw(Ring(used++), ship.Motor.State.Position, radius, Height, Segments, Color, LineWidth);
                    }
            for (int i = used; i < rings.Count; i++)
                rings[i].enabled = false;
        }

        LineRenderer Ring(int index)
        {
            if (index == rings.Count)
                rings.Add(RingLines.CreateLike(rings[0], transform, $"Ring {index}"));
            return rings[index];
        }
    }
}
