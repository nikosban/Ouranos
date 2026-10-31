using System.Collections.Generic;
using Ouranos.Movement;
using Ouranos.Selection;
using UnityEngine;

namespace Ouranos.Interaction
{
    /// <summary>
    /// Provisional placeholder: a flat circle at the active Move destination of every selected ship. Stop and None
    /// orders show nothing. Reads <see cref="ShipSelection"/> and <see cref="MovementOrders.Kind"/>/<see cref="MovementOrders.Destination"/>
    /// only; holds no command state and never writes orders, selection or the camera. This object's LineRenderer is the
    /// configuration source for every circle (<see cref="RingLines"/>). Final appearance is an owner decision.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class OrderFeedback : MonoBehaviour
    {
        public ShipSelection Selection;

        [Tooltip("Destination circle colour (provisional placeholder).")]
        public Color Color = new Color(0.2f, 0.9f, 0.3f, 1f);
        [Tooltip("Destination circle radius (u).")]
        [Range(0.1f, 10f)] public float Radius = 1.5f;
        [Tooltip("Destination circle line width (u).")]
        [Range(0.05f, 2f)] public float LineWidth = 0.2f;
        [Tooltip("Destination circle height above the movement plane Y = 0 (u).")]
        [Range(0.05f, 5f)] public float Height = 0.15f;
        [Tooltip("Number of line segments in the circle.")]
        [Range(12, 128)] public int Segments = 32;

        readonly List<LineRenderer> circles = new List<LineRenderer>();

        void Awake()
        {
            LineRenderer line = GetComponent<LineRenderer>();
            RingLines.Prepare(line);
            circles.Add(line);
        }

        void LateUpdate()
        {
            int used = 0;
            if (Selection != null)
                foreach (ShipMovement ship in Selection.Selected)
                    if (ShipSelection.IsSelectable(ship) && ship.Orders.Kind == OrderKind.Move)
                        RingLines.Draw(Circle(used++), ship.Orders.Destination, Radius, Height, Segments, Color, LineWidth);
            for (int i = used; i < circles.Count; i++)
                circles[i].enabled = false;
        }

        LineRenderer Circle(int index)
        {
            if (index == circles.Count)
                circles.Add(RingLines.CreateLike(circles[0], transform, $"Destination {index}"));
            return circles[index];
        }
    }
}
