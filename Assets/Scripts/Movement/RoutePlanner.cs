using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Ouranos.Movement
{
    public enum RouteStatus { None, Pending, Ready, Failed }

    /// <summary>Owns NavMesh route requests for one ship and the retained route.</summary>
    public class RoutePlanner : MonoBehaviour
    {
        [Tooltip("NavMesh agent type used for static route queries.")]
        public string AgentTypeName = "Ship";

        [Tooltip("How far a start or destination may be from the NavMesh and still be snapped onto it (u).")]
        [Min(0f)] public float SampleDistance = 4f;

        public RouteStatus Status { get; private set; }
        public Route Route { get; private set; }

        Vector2 requestedDestination;
        NavMeshPath path;

        void Awake() => path = new NavMeshPath();

        public void RequestPath(Vector2 destination)
        {
            requestedDestination = destination;
            Route = null;
            Status = RouteStatus.Pending;
            if (RouteRequestQueue.Instance == null)
            {
                Fail("no RouteRequestQueue in the scene");
                return;
            }
            RouteRequestQueue.Instance.Enqueue(this);
        }

        public void Clear()
        {
            Route = null;
            Status = RouteStatus.None;
        }

        /// <summary>Called by <see cref="RouteRequestQueue"/> within its per-frame budget.</summary>
        public void ExecuteRequest()
        {
            if (Status != RouteStatus.Pending) return;

            var filter = new NavMeshQueryFilter { agentTypeID = FindAgentTypeId(), areaMask = NavMesh.AllAreas };
            Vector3 start = transform.position;
            Vector3 end = ShipMovementState.ToWorld(requestedDestination);

            if (!NavMesh.SamplePosition(start, out NavMeshHit startHit, SampleDistance, filter))
            {
                Fail($"start {start} is not on the NavMesh");
                return;
            }
            if (!NavMesh.SamplePosition(end, out NavMeshHit endHit, SampleDistance, filter))
            {
                Fail($"destination {end} is not on the NavMesh");
                return;
            }
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, filter, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                Fail($"destination {end} is unreachable ({path.status})");
                return;
            }

            Route = new Route(path.corners.Select(ShipMovementState.ToPlanar));
            Status = RouteStatus.Ready;
        }

        void Fail(string reason)
        {
            Debug.LogWarning($"[RoutePlanner] {name}: {reason}; stopping.", this);
            Route = null;
            Status = RouteStatus.Failed;
        }

        int FindAgentTypeId()
        {
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(i);
                if (NavMesh.GetSettingsNameFromID(settings.agentTypeID) == AgentTypeName)
                    return settings.agentTypeID;
            }
            Debug.LogWarning($"[RoutePlanner] {name}: agent type '{AgentTypeName}' not found; using default.", this);
            return 0;
        }
    }
}
