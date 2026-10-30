using System.Collections.Generic;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>
    /// Planar corner polyline from a NavMesh query, with monotonic progress along it.
    /// Intermediate corners are guidance only, never stop destinations.
    /// </summary>
    public class Route
    {
        readonly List<Vector2> corners = new List<Vector2>();
        readonly List<float> cumulative = new List<float>();
        int segment;

        public IReadOnlyList<Vector2> Corners => corners;
        public float TotalLength { get; private set; }
        public float Progress { get; private set; }
        public float RemainingDistance => Mathf.Max(0f, TotalLength - Progress);
        public Vector2 Destination => corners[corners.Count - 1];

        public Route(IEnumerable<Vector2> points)
        {
            foreach (Vector2 point in points)
            {
                if (corners.Count > 0 && (point - corners[corners.Count - 1]).sqrMagnitude < 1e-8f)
                    continue;
                cumulative.Add(corners.Count == 0 ? 0f : cumulative[cumulative.Count - 1] + Vector2.Distance(corners[corners.Count - 1], point));
                corners.Add(point);
            }
            TotalLength = cumulative[cumulative.Count - 1];
        }

        /// <summary>Projects the position onto the current and next segment and advances progress (never backwards).</summary>
        public void AdvanceProgress(Vector2 position)
        {
            int lastSegment = corners.Count - 2;
            if (lastSegment < 0)
            {
                Progress = TotalLength;
                return;
            }

            float best = Progress;
            float bestSqr = float.MaxValue;
            int bestSegment = segment;
            for (int i = segment; i <= Mathf.Min(segment + 1, lastSegment); i++)
            {
                Vector2 a = corners[i];
                Vector2 ab = corners[i + 1] - a;
                float t = Mathf.Clamp01(Vector2.Dot(position - a, ab) / ab.sqrMagnitude);
                float sqr = (a + ab * t - position).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = cumulative[i] + ab.magnitude * t;
                    bestSegment = i;
                }
            }

            if (best > Progress)
            {
                Progress = best;
                segment = bestSegment;
            }
            // Leave a finished segment once progress reaches its end.
            while (segment < lastSegment && Progress >= cumulative[segment + 1])
                segment++;
        }

        /// <summary>Point on the polyline at a distance from the route start (clamped to the route).</summary>
        public Vector2 PointAt(float distance)
        {
            if (distance <= 0f) return corners[0];
            if (distance >= TotalLength) return Destination;
            int i = segment;
            while (i > 0 && cumulative[i] > distance) i--;
            while (i < corners.Count - 2 && cumulative[i + 1] < distance) i++;
            float length = cumulative[i + 1] - cumulative[i];
            float t = length > 0f ? (distance - cumulative[i]) / length : 0f;
            return Vector2.Lerp(corners[i], corners[i + 1], t);
        }
    }
}
