using System;
using System.Collections.Generic;
using System.Reflection;
using DV.ThingTypes;
using HarmonyLib;
using UnityEngine;

namespace PassengerCoachAccess
{
    internal sealed class CoachRegistry
    {
        private readonly Dictionary<TrainCar, CoachGeometry> coaches = new Dictionary<TrainCar, CoachGeometry>();
        private readonly HashSet<TrainCar> pending = new HashSet<TrainCar>();
        private readonly List<TrainCar> pendingWork = new List<TrainCar>();
        internal readonly List<CoachGeometry> Nearby = new List<CoachGeometry>();
        private Collider[] overlaps = new Collider[64];
        private CarSpawner spawner;
        private PropertyInfo passengerCargoProperty;
        private CargoType_v2 cargo;
        private float nextTick;
        private float nextNearby;
        private Vector3 lastOrigin;
        private int walkableMask;

        internal void Attach(CarSpawner value)
        {
            if (value == null || value == spawner) return;
            Clear();
            spawner = value;
            walkableMask = LayerMask.GetMask("Train_Walkable");
            spawner.CarSpawned += Register;
            spawner.CarAboutToBeDeleted += Unregister;
            if (spawner.AllCars != null)
                for (int i = 0; i < spawner.AllCars.Count; i++) Register(spawner.AllCars[i]);
        }

        internal void Detach(CarSpawner value) { if (ReferenceEquals(value, spawner)) Clear(); }

        private void Clear()
        {
            if (!ReferenceEquals(spawner, null))
            {
                spawner.CarSpawned -= Register;
                spawner.CarAboutToBeDeleted -= Unregister;
            }
            foreach (KeyValuePair<TrainCar, CoachGeometry> pair in coaches)
            {
                if (!ReferenceEquals(pair.Key, null)) pair.Key.OnDestroyCar -= Unregister;
                pair.Value.Dispose();
            }
            coaches.Clear(); pending.Clear(); pendingWork.Clear(); Nearby.Clear();
            spawner = null;
            cargo = null;
            Array.Clear(overlaps, 0, overlaps.Length);
            nextNearby = 0f;
        }

        internal void Tick()
        {
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            if (UnloadWatcher.isUnloading) { Clear(); return; }
            if (WorldStreamingInit.IsLoaded) Attach(CarSpawner.Instance);
            if (!ResolveCargo() || pending.Count == 0) return;
            pendingWork.Clear();
            pendingWork.AddRange(pending);
            pending.Clear();
            for (int i = 0; i < pendingWork.Count; i++) Register(pendingWork[i]);
            pendingWork.Clear();
        }

        internal void Recheck(TrainCar car)
        {
            if (car == null) return;
            pending.Remove(car);
            CoachGeometry existing;
            if (coaches.TryGetValue(car, out existing)) existing.Invalidate();
            else Register(car);
        }

        private bool ResolveCargo()
        {
            if (cargo != null) return true;
            if (passengerCargoProperty == null)
            {
                Type type = AccessTools.TypeByName("PassengerJobs.Injectors.CargoInjector");
                if (type != null) passengerCargoProperty = AccessTools.Property(type, "PassengerCargo");
            }
            if (passengerCargoProperty != null) cargo = passengerCargoProperty.GetValue(null, null) as CargoType_v2;
            return cargo != null;
        }

        private void Register(TrainCar car)
        {
            if (car == null || !car.gameObject.activeInHierarchy || coaches.ContainsKey(car)) return;
            if (!ResolveCargo() || car.carLivery == null || car.carLivery.parentType == null)
            {
                pending.Add(car);
                return;
            }
            pending.Remove(car);
            if (!cargo.IsLoadableOnCarType(car.carLivery.parentType)) return;
            if (car.interior == null || car.couplers == null || car.couplers.Length < 2 ||
                car.couplers[0] == null || car.couplers[1] == null) { pending.Add(car); return; }
            coaches.Add(car, new CoachGeometry(car));
            car.OnDestroyCar += Unregister;
            nextNearby = 0f;
        }

        private void Unregister(TrainCar car)
        {
            if (ReferenceEquals(car, null)) return;
            pending.Remove(car);
            CoachGeometry geometry;
            if (!coaches.TryGetValue(car, out geometry)) return;
            car.OnDestroyCar -= Unregister;
            geometry.Dispose();
            coaches.Remove(car);
            Nearby.Remove(geometry);
            nextNearby = 0f;
        }

        internal void RefreshNearby(Vector3 origin, bool force)
        {
            if (!force && Time.unscaledTime < nextNearby && (origin - lastOrigin).sqrMagnitude < 1f) return;
            nextNearby = Time.unscaledTime + 0.15f;
            lastOrigin = origin;
            Nearby.Clear();
            int count;
            while (true)
            {
                count = Physics.OverlapSphereNonAlloc(origin, Main.Distance + 2f, overlaps, walkableMask, QueryTriggerInteraction.Collide);
                if (count < overlaps.Length) break;
                // Dense yards: grow only on saturation, never silently omit the selected car.
                Array.Resize(ref overlaps, overlaps.Length * 2);
            }
            for (int i = 0; i < count; i++)
            {
                Collider collider = overlaps[i];
                if (collider != null) AddNearby(TrainCar.Resolve(collider.transform));
                overlaps[i] = null;
            }
            AddNearby(PlayerManager.Car); // Hint only; Contains still checks the actual body position.
        }

        private void AddNearby(TrainCar car)
        {
            if (car == null || !car.gameObject.activeInHierarchy) return;
            CoachGeometry coach;
            if (!coaches.TryGetValue(car, out coach))
            {
                // Local discovery also covers custom spawners that bypass CarSpawner events.
                Register(car);
                if (!coaches.TryGetValue(car, out coach)) return;
            }
            if (!Nearby.Contains(coach)) Nearby.Add(coach);
        }

        internal CoachGeometry FindContaining(Vector3 body)
        {
            for (int i = 0; i < Nearby.Count; i++) if (Nearby[i].Contains(body)) return Nearby[i];
            return null;
        }

        internal bool TryGetConnectedDoor(CoachDoor source, out CoachDoor destination)
        {
            destination = null;
            if (source.Kind != CoachDoorKind.End || source.Owner.Car == null) return false;
            // DV's frontCoupler/rearCoupler getters index this array without a null check.
            Coupler[] couplers = source.Owner.Car.couplers;
            if (couplers == null || couplers.Length < 2) return false;
            Coupler own = couplers[source.Front ? 0 : 1];
            Coupler next = own == null ? null : own.GetCoupled();
            if (next == null || next.GetCoupled() != own || next.train == null || next.train == source.Owner.Car) return false;
            CoachGeometry coach;
            if (!coaches.TryGetValue(next.train, out coach))
            {
                Register(next.train);
                if (!coaches.TryGetValue(next.train, out coach)) return false;
            }
            if (!coach.EnsureReady()) return false;
            for (int i = 0; i < coach.Doors.Count; i++)
            {
                CoachDoor door = coach.Doors[i];
                if (door.Kind == CoachDoorKind.End && door.Front == next.isFrontCoupler)
                {
                    destination = door;
                    return true;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(CarSpawner), "Awake")]
        private static class SpawnerAwake
        {
            private static void Postfix(CarSpawner __instance) { Main.Registry.Attach(__instance); }
        }

        [HarmonyPatch(typeof(CarSpawner), "OnDestroy")]
        private static class SpawnerDestroyed
        {
            private static void Prefix(CarSpawner __instance) { Main.Registry.Detach(__instance); }
        }

        [HarmonyPatch(typeof(TrainCar), "AwakeForPooledCar")]
        private static class PooledCarAwake
        {
            private static void Postfix(TrainCar __instance) { Main.Registry.Recheck(__instance); }
        }
    }
}
