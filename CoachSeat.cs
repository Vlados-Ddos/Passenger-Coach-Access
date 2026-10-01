using UnityEngine;

namespace PassengerCoachAccess
{
    internal sealed class CoachSeat
    {
        internal CoachGeometry Owner;
        internal PassengerSeatDefinition Definition;
        internal bool Occupied;
        // Query token used by CoachGeometry's spatial seat index. It prevents
        // duplicate evaluation when a ray crosses adjacent bins without any
        // per-frame allocations.
        internal int LastQueryToken;
        internal Transform Frame { get { return Owner == null ? null : Owner.Frame; } }
        internal bool IsToilet { get { return Definition.Kind == 1; } }

        internal bool IsAimed(Ray ray, Vector3 body, float reach, out float distance)
        {
            distance = 0f;
            if (Frame == null || Definition == null) return false;
            Ray local = new Ray(Frame.InverseTransformPoint(ray.origin), Frame.InverseTransformDirection(ray.direction));
            Vector3 size = new Vector3(Definition.Size.x + .12f, .26f, Definition.Size.z + .12f);
            Bounds bounds = new Bounds(Definition.Cushion, size);
            if (!bounds.IntersectRay(local, out distance) || distance < 0f) return false;
            Vector3 hit = Frame.TransformPoint(local.GetPoint(distance));
            distance = (hit - ray.origin).magnitude;
            return (hit - body).sqrMagnitude <= reach * reach;
        }
    }
}
