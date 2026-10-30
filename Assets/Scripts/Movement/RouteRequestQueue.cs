using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Ouranos.Movement
{
    /// <summary>Scene-wide budget for synchronous NavMesh path requests per rendered frame.</summary>
    public class RouteRequestQueue : MonoBehaviour
    {
        [Tooltip("Maximum synchronous path requests processed per rendered frame (provisional).")]
        [Min(1)] public int MaxRequestsPerFrame = 2;

        public static RouteRequestQueue Instance { get; private set; }

        /// <summary>Requests waiting for a later frame's budget.</summary>
        public int PendingCount => pending.Count;

        static readonly ProfilerMarker RouteRequestMarker = new ProfilerMarker("Movement.RouteRequest");

        readonly Queue<RoutePlanner> pending = new Queue<RoutePlanner>();
        readonly HashSet<RoutePlanner> queued = new HashSet<RoutePlanner>();

        void OnEnable() => Instance = this;

        void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        public void Enqueue(RoutePlanner planner)
        {
            if (queued.Add(planner))
                pending.Enqueue(planner);
        }

        void Update()
        {
            for (int processed = 0; processed < MaxRequestsPerFrame && pending.Count > 0; processed++)
            {
                RoutePlanner planner = pending.Dequeue();
                queued.Remove(planner);
                if (planner != null)
                    using (RouteRequestMarker.Auto())
                        planner.ExecuteRequest();
            }
        }
    }
}
