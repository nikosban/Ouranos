using System.Collections.Generic;
using Ouranos.Movement;
using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Provisional placeholder: a flat ring around every selected ship. This object's LineRenderer draws the first ring
    /// and is the single configuration source; further rings are runtime child objects carrying only a LineRenderer with
    /// the same material and settings. Reads <see cref="ShipSelection"/> only; never writes the selection and never
    /// touches the ships. Final indicator art is an owner decision.
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
            line.useWorldSpace = true;
            line.loop = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            rings.Add(line);
        }

        void LateUpdate()
        {
            int used = 0;
            if (Selection != null)
                foreach (ShipMovement ship in Selection.Selected)
                    if (ShipSelection.IsSelectable(ship))
                        Draw(Ring(used++), ship);
            for (int i = used; i < rings.Count; i++)
                rings[i].enabled = false;
        }

        LineRenderer Ring(int index)
        {
            if (index < rings.Count) return rings[index];
            LineRenderer source = rings[0];
            var ring = new GameObject($"Ring {index}").AddComponent<LineRenderer>();
            ring.transform.SetParent(transform, false);
            ring.sharedMaterial = source.sharedMaterial;
            ring.useWorldSpace = true;
            ring.loop = true;
            ring.shadowCastingMode = source.shadowCastingMode;
            ring.receiveShadows = false;
            ring.alignment = source.alignment;
            ring.textureMode = source.textureMode;
            ring.numCapVertices = source.numCapVertices;
            ring.numCornerVertices = source.numCornerVertices;
            ring.enabled = false;
            rings.Add(ring);
            return ring;
        }

        void Draw(LineRenderer line, ShipMovement ship)
        {
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
