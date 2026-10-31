using UnityEngine;

namespace Ouranos.Selection
{
    /// <summary>
    /// Shared flat-ring drawing on the movement plane for <see cref="SelectionIndicator"/> and
    /// <c>Interaction.OrderFeedback</c>: prepares a source LineRenderer, creates further ring objects that carry only
    /// a LineRenderer copied from it, and draws a ring. No marker logic; callers decide what to ring.
    /// </summary>
    public static class RingLines
    {
        /// <summary>Configures a LineRenderer for world-space closed rings without shadows, hidden until drawn.</summary>
        public static void Prepare(LineRenderer line)
        {
            line.useWorldSpace = true;
            line.loop = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
        }

        /// <summary>A new child of parent carrying only a LineRenderer with source's material and settings.</summary>
        public static LineRenderer CreateLike(LineRenderer source, Transform parent, string name)
        {
            var ring = new GameObject(name).AddComponent<LineRenderer>();
            ring.transform.SetParent(parent, false);
            ring.sharedMaterial = source.sharedMaterial;
            ring.alignment = source.alignment;
            ring.textureMode = source.textureMode;
            ring.numCapVertices = source.numCapVertices;
            ring.numCornerVertices = source.numCornerVertices;
            Prepare(ring);
            return ring;
        }

        /// <summary>Draws and shows a ring around a planar (XZ) center at the given height.</summary>
        public static void Draw(LineRenderer line, Vector2 center, float radius, float height, int segments, Color color, float width)
        {
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = width;
            line.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * 2f * Mathf.PI / segments;
                line.SetPosition(i, new Vector3(center.x + radius * Mathf.Cos(angle), height, center.y + radius * Mathf.Sin(angle)));
            }
            line.enabled = true;
        }
    }
}
