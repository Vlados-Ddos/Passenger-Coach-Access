using System;
using System.Collections.Generic;
using UnityEngine;

namespace PassengerCoachAccess
{
    // Queried only when access is requested. No cached world positions or scene scans.
    internal sealed class AccessPlacement
    {
        private const float ProbeAboveDoor = 0.6f;
        private const float GroundRange = 4f;
        private const float MaxSlopeLift = 0.25f;
        private const float ContactGap = 0.003f;
        private readonly RaycastHit[] hits = new RaycastHit[64];
        private readonly Collider[] overlaps = new Collider[64];
        private static readonly Vector2[] offsets = CreateOffsets();

        private struct Body
        {
            internal Vector3 Lower, Upper;
            internal float Radius;
            internal Transform Player;

            internal Body(CharacterController capsule)
            {
                Player = capsule.transform;
                Vector3 scale = Player.lossyScale;
                Radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float halfLine = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - Radius);
                Vector3 center = Player.TransformVector(capsule.center);
                Lower = center - Player.up * halfLine;
                Upper = center + Player.up * halfLine;
            }

            internal bool IsPlayer(Collider value)
            {
                return value == null || value.transform == Player || value.transform.IsChildOf(Player);
            }
        }

        private static Vector2[] CreateOffsets()
        {
            var values = new List<Vector2>();
            // At most 1.5 m from the original exit, always on the chosen side.
            for (int outward = 0; outward <= 4; outward++)
                for (int along = -3; along <= 3; along++)
                {
                    Vector2 value = new Vector2(outward * 0.35f, along * 0.35f);
                    if (value.sqrMagnitude <= 2.25f) values.Add(value);
                }
            values.Sort((a, b) =>
            {
                int order = a.sqrMagnitude.CompareTo(b.sqrMagnitude);
                if (order == 0) order = a.x.CompareTo(b.x);
                return order == 0 ? a.y.CompareTo(b.y) : order;
            });
            return values.ToArray();
        }

        internal bool HasClearance(Vector3 position, CharacterController capsule, int mask)
        {
            return capsule != null && Clear(position, new Body(capsule), mask);
        }

        private bool Clear(Vector3 position, Body body, int mask)
        {
            int count = Physics.OverlapCapsuleNonAlloc(position + body.Lower, position + body.Upper,
                body.Radius, overlaps, mask, QueryTriggerInteraction.Ignore);
            try
            {
                if (count == overlaps.Length) return false;
                // Never exempt an entire support mesh: it can also contain walls.
                // Other cars, the source coach and couplers remain physical obstacles.
                for (int i = 0; i < count; i++) if (!body.IsPlayer(overlaps[i])) return false;
                return true;
            }
            finally { Array.Clear(overlaps, 0, count); }
        }

        internal bool TryExterior(CoachDoor door, CharacterController capsule, int mask,
            out RaycastHit support, out Vector3 position)
        {
            support = default(RaycastHit);
            position = default(Vector3);
            if (capsule == null || door == null || door.Owner.Frame == null) return false;
            Body body = new Body(capsule);
            Vector3 threshold = door.WorldThreshold;
            Vector3 normal = door.WorldNormal;
            Vector3 outward = Vector3.ProjectOnPlane(normal, Vector3.up).normalized;
            if (outward.sqrMagnitude < 0.9f) return false;
            Vector3 along = Vector3.Cross(Vector3.up, outward);
            // Preserve the existing first probe, including bank/grade of the actual door.
            Vector3 first = threshold + normal * Mathf.Max(1f, body.Radius + 0.35f);
            float minNormal = Mathf.Max(0.65f, Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad));
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 probe = first + outward * offsets[i].x + along * offsets[i].y;
                RaycastHit ground;
                if (!Ground(probe + Vector3.up * ProbeAboveDoor, GroundRange, body, mask, minNormal, out ground)) continue;
                Vector3 candidate = ground.point;
                if (TrainAbove(candidate, body, mask)) continue;
                if (!HasFooting(ground, body, mask, minNormal)) continue;
                if (!Clear(candidate, body, mask) && !Settle(ground, body, mask, minNormal, out candidate)) continue;
                // Full capsule stays outside the door plane even on a banked coach.
                if (Mathf.Min(Vector3.Dot(candidate + body.Lower - threshold, normal),
                    Vector3.Dot(candidate + body.Upper - threshold, normal)) < body.Radius + 0.05f) continue;
                if (!Reachable(threshold, outward, candidate, body, mask)) continue;
                support = ground;
                position = candidate;
                return true;
            }
            return false;
        }

        private bool Nearest(int count, Body body, out RaycastHit result)
        {
            result = default(RaycastHit);
            try
            {
                if (count == hits.Length) return false;
                float distance = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = hits[i];
                    if (body.IsPlayer(hit.collider) || hit.distance >= distance) continue;
                    result = hit;
                    distance = hit.distance;
                }
                return result.collider != null;
            }
            finally { Array.Clear(hits, 0, count); }
        }

        private bool Ground(Vector3 origin, float range, Body body, int mask, float minNormal, out RaycastHit result)
        {
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, range, mask, QueryTriggerInteraction.Ignore);
            // Test the nearest solid hit, not the ground underneath a rejected car/roof.
            return Nearest(count, body, out result) && IsSupport(result, minNormal);
        }

        private static bool IsSupport(RaycastHit hit, float minNormal)
        {
            return hit.normal.y >= minNormal && hit.point.y > LevelInfo.WaterLevel &&
                TrainCar.Resolve(hit.transform) == null;
        }

        private bool TrainAbove(Vector3 ground, Body body, int mask)
        {
            // A low ground ray can start below a neighbouring car's underframe.
            // Even with headroom, the space underneath a train is not an exit.
            int count = Physics.RaycastNonAlloc(ground + Vector3.up * 0.05f, Vector3.up,
                hits, 6f, mask, QueryTriggerInteraction.Ignore);
            try
            {
                if (count == hits.Length) return true;
                for (int i = 0; i < count; i++)
                    if (!body.IsPlayer(hits[i].collider) && TrainCar.Resolve(hits[i].transform) != null) return true;
                return false;
            }
            finally { Array.Clear(hits, 0, count); }
        }

        private bool HasFooting(RaycastHit ground, Body body, int mask, float minNormal)
        {
            // Require a footprint, not just one ray onto a rail, rock tip or platform edge.
            float radius = body.Radius * 0.8f;
            float span = radius * Mathf.Sqrt(1f - minNormal * minNormal) / minNormal + 0.06f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 offset = (i < 2 ? Vector3.right : Vector3.forward) * (i % 2 == 0 ? radius : -radius);
                RaycastHit foot;
                if (!Ground(ground.point + offset + Vector3.up * span, span * 2f, body, mask, minNormal, out foot)) return false;
                float planeY = ground.point.y - Vector3.Dot(ground.normal, offset) / ground.normal.y;
                if (Mathf.Abs(foot.point.y - planeY) > 0.12f) return false;
                // Do not straddle different moving supports.
                if (foot.rigidbody != ground.rigidbody) return false;
            }
            return true;
        }

        private bool Settle(RaycastHit ground, Body body, int mask, float minNormal, out Vector3 position)
        {
            position = ground.point;
            // The lower hemisphere must touch the slope at its side, not intersect it.
            // A downward shape sweep gives actual support for the lift; there is no
            // arbitrary upward search that could place the player in mid-air.
            Vector3 origin = ground.point + body.Lower + Vector3.up * MaxSlopeLift;
            int count = Physics.SphereCastNonAlloc(origin, body.Radius, Vector3.down, hits,
                MaxSlopeLift, mask, QueryTriggerInteraction.Ignore);
            RaycastHit contact;
            if (!Nearest(count, body, out contact) || contact.distance <= 0f || !IsSupport(contact, minNormal) ||
                contact.rigidbody != ground.rigidbody) return false;
            position += Vector3.up * (MaxSlopeLift - contact.distance + ContactGap);
            return Clear(position, body, mask);
        }

        private bool Reachable(Vector3 threshold, Vector3 outward, Vector3 position, Body body, int mask)
        {
            // Check a body-width route from just outside the actual doorway. This
            // prevents fallback from selecting a clear spot through a wall/other car.
            float radius = Mathf.Min(body.Radius, 0.25f);
            Vector3 center = (body.Lower + body.Upper) * 0.5f;
            Vector3 start = threshold + outward * (body.Radius + 0.15f) + center;
            Vector3 delta = position + center - start;
            int count = Physics.OverlapSphereNonAlloc(start, radius, overlaps, mask, QueryTriggerInteraction.Ignore);
            try
            {
                if (count == overlaps.Length) return false;
                for (int i = 0; i < count; i++) if (!body.IsPlayer(overlaps[i])) return false;
            }
            finally { Array.Clear(overlaps, 0, count); }
            count = Physics.SphereCastNonAlloc(start, radius, delta.normalized, hits,
                delta.magnitude, mask, QueryTriggerInteraction.Ignore);
            // Nearest(false) also means a saturated query: fail closed in that case.
            bool saturated = count == hits.Length;
            RaycastHit obstruction;
            return !Nearest(count, body, out obstruction) && !saturated;
        }
    }
}
