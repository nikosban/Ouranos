using UnityEngine;

namespace Ouranos.Movement
{
    public enum OrderKind { None, Move, Stop }

    /// <summary>The single seam through which player and AI issue movement orders to one ship.</summary>
    [RequireComponent(typeof(RoutePlanner))]
    public class MovementOrders : MonoBehaviour
    {
        public OrderKind Kind { get; private set; }
        public Vector2 Destination { get; private set; }

        RoutePlanner planner;

        void Awake() => planner = GetComponent<RoutePlanner>();

        /// <summary>Move to an XZ destination. Supersedes any previous order and route.</summary>
        public void Move(Vector2 destination)
        {
            Kind = OrderKind.Move;
            Destination = destination;
            planner.RequestPath(destination);
        }

        /// <summary>Brake to a stop and drop the current route.</summary>
        public void Stop()
        {
            Kind = OrderKind.Stop;
            planner.Clear();
        }

        /// <summary>Called by the movement pipeline when a move order arrives.</summary>
        public void Complete()
        {
            Kind = OrderKind.None;
            planner.Clear();
        }
    }
}
