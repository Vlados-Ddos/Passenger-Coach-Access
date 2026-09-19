using System;
using System.Collections.Generic;
using DV;
using DV.ThingTypes;
using UnityEngine;

namespace PassengerCoachAccess
{
    public enum CoachDoorKind { Side, End }

    // Optional integration contract for models without native ladder/door metadata.
    // Origin is the bottom centre of the actual doorway; normal points outside.
    public sealed class CoachDoorAnchor : MonoBehaviour
    {
        public CoachDoorKind Kind;
        public Vector3 OutwardNormal = Vector3.forward;
        public float OpeningWidth = 0.84f;
        public float OpeningHeight = 1.9f;
        public CameraTrigger InteriorRegion;
    }

    internal sealed class CoachDoor
    {
        internal CoachGeometry Owner;
        internal CoachDoorKind Kind;
        internal Vector3 Threshold;
        internal Vector3 Normal;
        internal float Width;
        internal float Height;
        internal bool Front;
        internal Vector3 WorldThreshold { get { return Owner.Frame.TransformPoint(Threshold); } }
        internal Vector3 WorldNormal { get { return Owner.Frame.TransformDirection(Normal).normalized; } }

        internal bool IsAimed(Ray ray, Vector3 body, float reach, bool inside, out float distance)
        {
            distance = 0f;
            if (Owner.Frame == null) return false;
            Vector3 origin = WorldThreshold;
            Vector3 normal = WorldNormal;
            Vector3 up = Owner.Frame.up;
            float side = Vector3.Dot(body - origin, normal);
            if (inside ? side > 0.12f : side < -0.12f) return false;
            Vector3 right = Vector3.Cross(up, normal).normalized;
            float width = Owner.Frame.TransformVector(Vector3.Cross(Vector3.up, Normal).normalized * Width).magnitude;
            float height = Owner.Frame.TransformVector(Vector3.up * Height).magnitude;
            if (!DoorMath.Intersect(ray, origin, normal, right, up, width, height, out distance)) return false;
            Vector3 hit = ray.GetPoint(distance);
            return (hit - body).sqrMagnitude <= reach * reach;
        }
    }

    // Pure managed geometry, also exercised by the regression harness.
    internal static class DoorMath
    {
        internal static bool Intersect(Ray ray, Vector3 bottom, Vector3 normal, Vector3 right,
            Vector3 up, float width, float height, out float distance)
        {
            distance = 0f;
            float denominator = Vector3.Dot(ray.direction, normal);
            if (Math.Abs(denominator) < 0.0001f) return false;
            distance = Vector3.Dot(bottom - ray.origin, normal) / denominator;
            if (distance < 0f) return false;
            Vector3 offset = ray.GetPoint(distance) - bottom;
            float horizontal = Vector3.Dot(offset, right);
            float vertical = Vector3.Dot(offset, up);
            return Math.Abs(horizontal) <= width * 0.5f && vertical >= 0f && vertical <= height;
        }
    }

    internal sealed class CoachGeometry : IDisposable
    {
        internal readonly TrainCar Car;
        internal Transform Frame { get { return Car == null ? null : Car.interior; } }
        internal readonly List<CoachDoor> Doors = new List<CoachDoor>(6);
        private readonly List<CameraTrigger> regions = new List<CameraTrigger>();
        private readonly List<Collider> walkables = new List<Collider>();
        private readonly List<CoachDoorAnchor> markers = new List<CoachDoorAnchor>();
        private bool dirty = true;
        private bool disposed;
        private int unloadFrame = -1;
        private float retryAt;
        private bool reportedUnsupported;
        private int walkableLayer;

        internal CoachGeometry(TrainCar car)
        {
            Car = car;
            car.InteriorLoaded += InteriorChanged;
            car.InteriorAboutToBeUnloaded += InteriorUnloading;
            car.ExternalInteractableLoaded += InteriorChanged;
            car.ExternalInteractableAboutToBeUnloaded += InteriorUnloading;
            walkableLayer = LayerMask.NameToLayer("Train_Walkable");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // These are managed event accessors and can be detached even from a
            // Unity object whose native instance has already been destroyed.
            if (!ReferenceEquals(Car, null))
            {
                Car.InteriorLoaded -= InteriorChanged;
                Car.InteriorAboutToBeUnloaded -= InteriorUnloading;
                Car.ExternalInteractableLoaded -= InteriorChanged;
                Car.ExternalInteractableAboutToBeUnloaded -= InteriorUnloading;
            }
            Doors.Clear();
            walkables.Clear();
            regions.Clear();
            markers.Clear();
        }

        private void InteriorChanged(GameObject value)
        {
            // DV also emits Loaded(null) after scheduling the old object for Destroy.
            if (value == null) InteriorUnloading(value);
            else Invalidate();
        }

        private void InteriorUnloading(GameObject unused)
        {
            Invalidate();
            // Unity Destroy is deferred until the end of this frame. Rebuilding now
            // would cache the soon-to-be-destroyed floor/regions until another event.
            unloadFrame = Time.frameCount;
        }

        internal void Invalidate()
        {
            dirty = true; retryAt = 0f;
            Doors.Clear(); regions.Clear(); walkables.Clear(); markers.Clear();
        }

        internal CoachDoor RefreshDoor(CoachDoor previous)
        {
            for (int i = 0; i < Doors.Count; i++)
            {
                CoachDoor door = Doors[i];
                if (door.Kind == previous.Kind && Vector3.Dot(door.Normal, previous.Normal) > 0.99f &&
                    (door.Threshold - previous.Threshold).sqrMagnitude < 0.04f) return door;
            }
            return null;
        }

        internal bool EnsureReady()
        {
            if (disposed || Car == null || !Car.gameObject.activeInHierarchy || Frame == null ||
                Time.frameCount <= unloadFrame) return false;
            if (!dirty) return Doors.Count != 0;
            if (Time.unscaledTime < retryAt) return false;
            retryAt = Time.unscaledTime + 1f;
            Doors.Clear(); regions.Clear(); walkables.Clear(); markers.Clear();
            Collect(Car.transform);
            if (!Frame.IsChildOf(Car.transform)) Collect(Frame);
            if (regions.Count == 0 || walkables.Count == 0) return Unsupported();

            // Explicit model metadata takes precedence over the inferred stock layout.
            if (markers.Count != 0)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    CoachDoorAnchor marker = markers[i];
                    if (marker.OpeningWidth <= 0f || marker.OpeningHeight <= 0f) continue;
                    Vector3 normal = Frame.InverseTransformDirection(marker.transform.TransformDirection(marker.OutwardNormal));
                    if (Mathf.Abs(normal.y) > 0.1f || normal.sqrMagnitude < 0.5f) continue;
                    AddDoor(marker.Kind, marker.transform.position, normal.normalized,
                        marker.OpeningWidth, marker.OpeningHeight);
                }
            }
            else
            {
                BuildSideDoors();
                if (HasVerifiedStockLayout()) BuildStockEndDoors();
            }
            dirty = Doors.Count == 0;
            if (dirty) return Unsupported();
            return true;
        }

        private bool Unsupported()
        {
            if (!reportedUnsupported)
            {
                reportedUnsupported = true;
                Main.Entry.Logger.Warning("No verified access geometry yet for " + (Car.carLivery == null ? Car.name : Car.carLivery.id) +
                    ". Requires a native interior region, walkable floor and door/ladder metadata. Will retry nearby.");
            }
            return false;
        }

        private void Collect(Transform root)
        {
            // Both roots are necessary: DV detaches the interior from TrainCar.
            InternalExternalSnapshotSwitcher[] switches = root.GetComponentsInChildren<InternalExternalSnapshotSwitcher>(true);
            for (int i = 0; i < switches.Length; i++) AddRegion(switches[i].trigger);
            CoachDoorAnchor[] anchors = root.GetComponentsInChildren<CoachDoorAnchor>(true);
            for (int i = 0; i < anchors.Length; i++)
            {
                if (!anchors[i].isActiveAndEnabled) continue;
                markers.Add(anchors[i]);
                AddRegion(anchors[i].InteriorRegion);
            }
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider.gameObject.layer == walkableLayer && collider.enabled && collider.gameObject.activeInHierarchy &&
                    TrainCar.Resolve(collider.transform) == Car) walkables.Add(collider);
            }
        }

        private void AddRegion(CameraTrigger region)
        {
            if (region != null && region.box != null && !regions.Contains(region)) regions.Add(region);
        }

        internal bool Contains(Vector3 body)
        {
            // This cache is only a list of regions; occupancy is never cached.
            if (disposed || Car == null || !Car.gameObject.activeInHierarchy || Frame == null ||
                Time.frameCount <= unloadFrame) return false;
            EnsureReady();
            for (int i = 0; i < regions.Count; i++)
                if (regions[i] != null && regions[i].box != null && regions[i].IsPointInside(body)) return true;
            return false;
        }

        private void BuildSideDoors()
        {
            for (int i = 0; i < walkables.Count; i++)
            {
                BoxCollider ladder = walkables[i] as BoxCollider;
                if (ladder == null || !ladder.isTrigger || !ladder.CompareTag("Ladders")) continue;
                Vector3 normal = Frame.InverseTransformDirection(-ladder.transform.forward).normalized;
                // A roof/end ladder is not a side door. Check its actual orientation and region.
                if (Mathf.Abs(normal.x) < 0.9f || Mathf.Abs(normal.y) > 0.1f) continue;
                Vector3 top = ladder.transform.TransformPoint(ladder.center + new Vector3(0f, ladder.size.y * 0.5f, ladder.size.z * 0.5f));
                float width = ladder.transform.TransformVector(Vector3.right * ladder.size.x).magnitude;
                Vector3 inside = top - Frame.TransformDirection(normal) * 0.55f;
                RaycastHit floor;
                if (!TryFloor(inside, out floor) || Mathf.Abs(Vector3.Dot(floor.point - top, Frame.up)) > 0.4f) continue;
                Vector3 threshold = top + Frame.up * Vector3.Dot(floor.point - top, Frame.up);
                // Conservative aperture within the measured stock door. Other layouts can supply markers.
                AddDoor(CoachDoorKind.Side, threshold, normal, width, 1.9f);
            }
        }

        private bool HasVerifiedStockLayout()
        {
            TrainCarType type = Car.carLivery.v1;
            if (type != TrainCarType.PassengerRed && type != TrainCarType.PassengerGreen && type != TrainCarType.PassengerBlue) return false;
            int sides = 0;
            for (int i = 0; i < Doors.Count; i++) if (Doors[i].Kind == CoachDoorKind.Side) sides++;
            if (sides != 4) return false;
            for (int i = 0; i < walkables.Count; i++)
            {
                MeshCollider collider = walkables[i] as MeshCollider;
                if (collider != null && collider.sharedMesh != null && collider.sharedMesh.name == "CarPassenger_Collider" &&
                    collider.sharedMesh.vertexCount == 162312) return true;
            }
            return false;
        }

        private void BuildStockEndDoors()
        {
            // Confirmed stock doorway centres coincide with the native cabin's end faces.
            for (int i = 0; i < regions.Count; i++)
            {
                CameraTrigger region = regions[i];
                BoxCollider box = region.box;
                if (box.size.z < 20f) continue;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 end = region.transform.TransformPoint(box.center + Vector3.forward * (box.size.z * 0.5f * sign));
                    Vector3 normal = Frame.InverseTransformDirection(region.transform.forward * sign).normalized;
                    RaycastHit floor;
                    if (!TryFloor(end - Frame.TransformDirection(normal) * 0.5f, out floor)) continue;
                    end += Frame.up * Vector3.Dot(floor.point - end, Frame.up);
                    AddDoor(CoachDoorKind.End, end, normal, 0.84f, 1.9f);
                }
                break;
            }
        }

        private void AddDoor(CoachDoorKind kind, Vector3 threshold, Vector3 normal, float width, float height)
        {
            Vector3 local = Frame.InverseTransformPoint(threshold);
            for (int i = 0; i < Doors.Count; i++)
                if (Doors[i].Kind == kind && (Doors[i].Threshold - local).sqrMagnitude < 0.04f) return;
            RaycastHit floor;
            if (!TryFloor(threshold - Frame.TransformDirection(normal) * 0.5f, out floor) ||
                Mathf.Abs(Vector3.Dot(floor.point - threshold, Frame.up)) > 0.4f) return;
            local.y = Frame.InverseTransformPoint(floor.point).y;
            Coupler[] couplers = Car.couplers;
            Coupler frontCoupler = couplers != null && couplers.Length > 0 ? couplers[0] : null;
            Coupler rearCoupler = couplers != null && couplers.Length > 1 ? couplers[1] : null;
            if (kind == CoachDoorKind.End && (frontCoupler == null || rearCoupler == null)) return;
            bool front = frontCoupler != null && (rearCoupler == null ||
                (frontCoupler.transform.position - threshold).sqrMagnitude < (rearCoupler.transform.position - threshold).sqrMagnitude);
            Doors.Add(new CoachDoor { Owner = this, Kind = kind, Threshold = local, Normal = normal,
                Width = width / Frame.TransformVector(Vector3.Cross(Vector3.up, normal).normalized).magnitude,
                Height = height / Frame.TransformVector(Vector3.up).magnitude, Front = front });
        }

        internal bool TryFloor(Vector3 location, out RaycastHit result)
        {
            result = default(RaycastHit);
            float nearest = float.MaxValue;
            for (int r = 0; r < regions.Count; r++)
            {
                CameraTrigger region = regions[r];
                if (region == null || region.box == null) continue;
                BoxCollider box = region.box;
                Vector3 local = region.transform.InverseTransformPoint(location);
                Vector3 half = box.size * 0.5f;
                if (Mathf.Abs(local.x - box.center.x) >= half.x || Mathf.Abs(local.z - box.center.z) >= half.z) continue;
                local.y = box.center.y;
                Vector3 origin = region.transform.TransformPoint(local);
                float length = region.transform.TransformVector(Vector3.up * box.size.y).magnitude;
                Ray ray = new Ray(origin, Vector3.down);
                for (int c = 0; c < walkables.Count; c++)
                {
                    Collider collider = walkables[c];
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger) continue;
                    RaycastHit hit;
                    if (!collider.Raycast(ray, out hit, length) || hit.normal.y < 0.65f) continue;
                    // Verify real support within the cabin; reject the underframe beneath it.
                    if (!region.IsPointInside(hit.point + Vector3.up * 0.06f)) continue;
                    float score = Mathf.Abs(Vector3.Dot(hit.point - location, Frame.up));
                    if (score >= nearest) continue;
                    result = hit;
                    nearest = score;
                }
            }
            return result.collider != null;
        }
    }
}
