using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using UnityEngine;
using UnityModManagerNet;

namespace PassengerCoachAccess
{
    public class CoachAccessSettings : UnityModManager.ModSettings
    {
        public KeyBinding AccessKey = new KeyBinding();

        public float ActivationDistance = 2.25f;

        public float MinimumDoorDistanceFromCentre = 2.75f;

        public float FallbackInteriorHeight = 1.65f;

        public int Language;

        public CoachAccessSettings()
        {
            SetDefaultAccessKey(AccessKey);
        }

        private static void SetDefaultAccessKey(KeyBinding keyBinding)
        {
            MethodInfo changeMethod = null;
            MethodInfo[] methods = keyBinding.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo candidate = methods[i];
                if (candidate.Name == "Change" && candidate.GetParameters().Length == 4)
                {
                    changeMethod = candidate;
                    break;
                }
            }

            if (changeMethod == null)
            {
                return;
            }

            ParameterInfo[] parameters = changeMethod.GetParameters();
            object eKey = Enum.Parse(parameters[0].ParameterType, "E");
            changeMethod.Invoke(keyBinding, new object[] { eKey, false, false, false });
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public sealed class CoachAccessSettingsPacket : IPacket
    {
        public float ActivationDistance { get; set; }
        public float MinimumDoorDistanceFromCentre { get; set; }
        public float FallbackInteriorHeight { get; set; }
    }

    public static class Main
    {
        // 0.001 cm below the actual support surface so the controller resolves
        // contact immediately through the game's normal teleport path.
        private const float TeleportSurfaceOffset = 0.00001f;
        private const float MinimumExteriorDoorDistance = 1.00f;

        private struct DoorLocation
        {
            public int Side;
            public int End;
            public float LocalZ;
        }

        private static UnityModManager.ModEntry _modEntry;
        private static CoachAccessSettings _settings;
        private static Component _localPlayer;
        private static Component _characterReparenting;
        private static MethodInfo _reparentTo;
        private static FieldInfo _collisionRootField;
        private static TrainCar _occupiedCoach;
        private static DoorLocation _enteredThrough;
        private static TrainCar _aimedCoach;
        private static TrainCar _aimedNextCoach;
        private static DoorLocation _aimedDoor;
        private static object _passengerCargo;
        private static MethodInfo _isLoadableOnCarType;
        private static bool _passengerLookupFailed;
        private static bool _multiplayerCompatibilityRegistered;
        private static IServer _multiplayerServer;
        private static IClient _multiplayerClient;
        private static bool _multiplayerEventsRegistered;
        private static bool _usingHostSettings;
        private static CoachAccessSettingsPacket _localSettingsSnapshot;
        private static float _nextMultiplayerEventCheck;
        private static float _nextMultiplayerCheck;
        private static TrainCar[] _cachedPassengerCoaches = new TrainCar[0];
        private static readonly Dictionary<TrainCar, Bounds> _cachedCoachBounds = new Dictionary<TrainCar, Bounds>();
        private static readonly HashSet<TrainCar> _pendingCoachRegistrations = new HashSet<TrainCar>();
        private static TrainCar[] _nearbyPassengerCoaches = new TrainCar[0];
        private static bool _initialCoachScanComplete;
        private static bool _coachCacheDirty;
        private static float _nextCoachCacheCleanup;
        private static float _nextNearbyRefresh;
        private static Vector3 _lastNearbyOrigin;
        private static float _nextAimCheck;
        private static float _nextExternalStateScan;
        private static MonoBehaviour _coroutineRunner;
        private static int _teleportToken;
        private static float _stateLockUntil;
        private static float _nextInputTime;
        private static TrainCar _stableAimedCoach;
        private static DoorLocation _stableAimedDoor;
        private static float _stableAimUntil;
        private static bool _stableAimValid;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            _modEntry = modEntry;
            _settings = UnityModManager.ModSettings.Load<CoachAccessSettings>(modEntry);
            _modEntry.OnGUI = DrawGui;
            _modEntry.OnSaveGUI = SaveGui;
            _modEntry.OnUpdate = Update;
            _modEntry.OnToggle = Toggle;

            GameObject overlayObject = new GameObject("PassengerCoachAccessPrompt");
            UnityEngine.Object.DontDestroyOnLoad(overlayObject);
            _coroutineRunner = overlayObject.AddComponent<PromptOverlay>();

            Type collidersType = AccessTools.TypeByName("TrainCarColliders");
            _collisionRootField = collidersType == null
                ? null
                : AccessTools.Field(collidersType, "collisionRoot");

            new Harmony(modEntry.Info.Id).PatchAll(Assembly.GetExecutingAssembly());
            FindLocalPlayer();
            RegisterMultiplayerEvents();

            _modEntry.Logger.Log("Passenger Coach Access loaded. Approach and aim at a passenger-coach door.");
            return true;
        }

        private static bool Toggle(UnityModManager.ModEntry modEntry, bool active)
        {
            if (!active && _occupiedCoach != null)
            {
                if (_enteredThrough.Side == 0)
                {
                    ReparentPlayer(null, false, null);
                    _occupiedCoach = null;
                }
                else
                {
                    LeaveCoach();
                }
            }

            return true;
        }

        private static void DrawGui(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label(T("Язык / Language", "Language / Язык"));
            _settings.Language = GUILayout.SelectionGrid(_settings.Language,
                new[] { "Русский", "English" }, 2, GUILayout.Width(280.0f));
            GUILayout.Space(8.0f);
            GUILayout.Label(T(
                "Наведитесь на боковую или торцевую дверь пассажирского вагона и нажмите назначенную кнопку.",
                "Aim at a side or end door of a passenger coach and press the configured key."));
            GUILayout.Label(T(
                "Боковая дверь служит для входа/выхода, торцевая — для перехода в соседний вагон.",
                "Side doors enter or exit; end doors pass to an adjacent passenger coach."));

            bool sharedSettingsEditable = !_usingHostSettings;
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Кнопка входа/выхода", "Enter/exit key"), GUILayout.Width(310.0f));
            UnityModManager.UI.DrawKeybindingSmart(_settings.AccessKey, string.Empty,
                updated => _settings.AccessKey = updated, GUI.skin.button, GUILayout.Width(180.0f));
            GUILayout.EndHorizontal();
            GUI.enabled = sharedSettingsEditable;
            DrawSlider(T("Дистанция активации двери", "Door activation distance"),
                ref _settings.ActivationDistance, 1.0f, 4.0f);
            DrawSlider(T("Мин. расстояние от центра вагона", "Minimum distance from coach centre"),
                ref _settings.MinimumDoorDistanceFromCentre, 1.5f, 6.0f);
            DrawSlider(T("Резервная высота телепортации", "Fallback teleport height"),
                ref _settings.FallbackInteriorHeight, 1.0f, 3.0f);
            GUI.enabled = true;

            if (_usingHostSettings)
            {
                GUILayout.Label(T(
                    "Настройки игрового процесса получены от хоста и доступны только для чтения.",
                    "Gameplay settings are supplied by the host and are read-only."));
            }

            if (_multiplayerCompatibilityRegistered)
            {
                GUILayout.Label(T(
                    "Мультиплеер: мод зарегистрирован и настройки хоста синхронизированы.",
                    "Multiplayer: mod registered; host settings are synchronized."));
            }
            else
            {
                GUILayout.Label(T(
                    "Мультиплеер: регистрация произойдёт автоматически после запуска Multiplayer.",
                    "Multiplayer: registration occurs automatically when Multiplayer starts."));
            }
        }

        private static void DrawSlider(string label, ref float value, float minimum, float maximum)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": " + value.ToString("0.00") + " m", GUILayout.Width(310.0f));
            value = GUILayout.HorizontalSlider(value, minimum, maximum, GUILayout.Width(260.0f));
            GUILayout.EndHorizontal();
        }

        private static string T(string russian, string english)
        {
            return _settings != null && _settings.Language == 1 ? english : russian;
        }

        private static void SaveGui(UnityModManager.ModEntry modEntry)
        {
            _settings.Save(modEntry);
            if (_multiplayerServer != null)
            {
                SendHostSettings(null);
            }
        }

        private static void Update(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            RegisterMultiplayerCompatibilityIfAvailable();
            if (Time.unscaledTime >= _nextMultiplayerEventCheck)
            {
                _nextMultiplayerEventCheck = Time.unscaledTime + 1.0f;
                RegisterMultiplayerEvents();
            }

            if (!EnsureLocalPlayer())
            {
                return;
            }

            if (Time.unscaledTime >= _nextAimCheck)
            {
                _nextAimCheck = Time.unscaledTime + 0.25f;
                SynchronizeOccupiedCoachState();
                UpdateAimedDoorPrompt();
            }

            if (_occupiedCoach != null && !_occupiedCoach)
            {
                _occupiedCoach = null;
            }

            if (!_settings.AccessKey.Down() || Time.unscaledTime < _nextInputTime)
            {
                return;
            }

            _nextInputTime = Time.unscaledTime + 0.35f;

            DoorLocation door = _aimedDoor;
            TrainCar coach = _aimedCoach;
            if (coach == null)
            {
                coach = FindAimedPassengerCoach(out door);
            }
            if (coach != null)
            {
                bool actuallyInside = IsPlayerActuallyInsideCoach(coach);
                if (actuallyInside)
                {
                    _occupiedCoach = coach;
                }
                else if (_occupiedCoach == coach)
                {
                    _occupiedCoach = null;
                }
            }
            if (_occupiedCoach != null)
            {
                if (coach == _occupiedCoach)
                {
                    if (door.Side == 0)
                    {
                        TraverseEndDoor(_occupiedCoach, door);
                    }
                    else
                    {
                        LeaveCoach(door);
                    }
                }

                return;
            }

            if (coach == null)
            {
                _modEntry.Logger.Log("No passenger-coach door is close enough.");
                return;
            }

            EnterCoach(coach, door);
        }

        private static void UpdateAimedDoorPrompt()
        {
            DoorLocation door;
            TrainCar detectedCoach = FindAimedPassengerCoach(out door);
            if (detectedCoach != null)
            {
                _stableAimedCoach = detectedCoach;
                _stableAimedDoor = door;
                _stableAimUntil = Time.unscaledTime + 0.45f;
                _stableAimValid = true;
            }
            else if (_stableAimValid && Time.unscaledTime <= _stableAimUntil &&
                IsStableDoorStillUsable(_stableAimedCoach, _stableAimedDoor))
            {
                detectedCoach = _stableAimedCoach;
                door = _stableAimedDoor;
            }
            else
            {
                _stableAimValid = false;
            }

            _aimedCoach = detectedCoach;
            _aimedDoor = door;
            _aimedNextCoach = _occupiedCoach != null && _aimedCoach == _occupiedCoach && door.Side == 0
                ? FindCoachBeyondEndDoor(_occupiedCoach, door)
                : null;
        }

        private static bool IsStableDoorStillUsable(TrainCar coach, DoorLocation door)
        {
            if (coach == null || _localPlayer == null)
            {
                return false;
            }

            Bounds bounds;
            if (!_cachedCoachBounds.TryGetValue(coach, out bounds))
            {
                return false;
            }

            Vector3 local = coach.transform.InverseTransformPoint(_localPlayer.transform.position);
            float edgeDistance = door.Side == 0
                ? Mathf.Abs((door.End > 0 ? bounds.max.z : bounds.min.z) - local.z)
                : Mathf.Abs((door.Side > 0 ? bounds.max.x : bounds.min.x) - local.x);
            float alignmentDistance = door.Side == 0
                ? Mathf.Abs(local.x - bounds.center.x)
                : Mathf.Abs(local.z - door.LocalZ);
            float alignmentLimit = door.Side == 0 ? 1.25f : 1.35f;
            return edgeDistance <= _settings.ActivationDistance + 1.2f &&
                alignmentDistance <= alignmentLimit;
        }

        private static void SynchronizeOccupiedCoachState()
        {
            if (_localPlayer == null || Time.unscaledTime < _stateLockUntil)
            {
                return;
            }

            if (_occupiedCoach != null)
            {
                if (IsPlayerActuallyInsideCoach(_occupiedCoach))
                {
                    return;
                }

                _occupiedCoach = null;
                _aimedCoach = null;
                _aimedNextCoach = null;
            }

            if (Time.unscaledTime < _nextExternalStateScan)
            {
                return;
            }

            _nextExternalStateScan = Time.unscaledTime + 1.0f;
            TrainCar[] cars = _cachedPassengerCoaches;
            for (int i = 0; i < cars.Length; i++)
            {
                TrainCar coach = cars[i];
                Bounds bounds;
                if (coach != null && _cachedCoachBounds.TryGetValue(coach, out bounds) &&
                    IsCoachWithinInteractionRange(coach, bounds, _localPlayer.transform.position) &&
                    IsPlayerActuallyInsideCoach(coach))
                {
                    _occupiedCoach = coach;
                    return;
                }
            }
        }

        private static bool IsPlayerInsideCoachVolume(TrainCar coach)
        {
            Bounds bounds;
            if (coach == null || !_cachedCoachBounds.TryGetValue(coach, out bounds))
            {
                return false;
            }

            Vector3 local = coach.transform.InverseTransformPoint(_localPlayer.transform.position);
            float lowerY = coach.PivotToTrackHeightOffset + 0.35f;
            return local.x > bounds.min.x + 0.18f && local.x < bounds.max.x - 0.18f &&
                local.z > bounds.min.z + 0.75f && local.z < bounds.max.z - 0.75f &&
                local.y > lowerY && local.y < bounds.max.y - 0.35f;
        }

        private static bool IsPlayerActuallyInsideCoach(TrainCar coach)
        {
            if (coach == null || _localPlayer == null)
            {
                return false;
            }

            if (IsPlayerInsideCoachVolume(coach))
            {
                return true;
            }

            return false;
        }

        private static TrainCar FindAimedPassengerCoach(out DoorLocation aimedDoor)
        {
            aimedDoor = new DoorLocation();
            if (!ResolvePassengerCargo())
            {
                return null;
            }

            RefreshPassengerCoachCache();

            if (_occupiedCoach != null)
            {
                return FindAimedDoorOnCoach(_occupiedCoach, out aimedDoor);
            }

            RefreshNearbyCoachCandidates();

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = _localPlayer.GetComponentInChildren<Camera>();
            }

            if (camera == null)
            {
                return null;
            }

            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0.0f));
            TrainCar closestCoach = null;
            DoorLocation closestDoor = new DoorLocation();
            float closestScore = float.MaxValue;
            TrainCar[] cars = _nearbyPassengerCoaches;
            for (int i = 0; i < cars.Length; i++)
            {
                TrainCar coach = cars[i];
                if (coach == null)
                {
                    continue;
                }

                Bounds bounds;
                if (!_cachedCoachBounds.TryGetValue(coach, out bounds))
                {
                    continue;
                }

                if (!IsCoachWithinInteractionRange(coach, bounds, ray.origin))
                {
                    continue;
                }

                for (int sideIndex = -1; sideIndex <= 1; sideIndex += 2)
                {
                    for (int endIndex = -1; endIndex <= 1; endIndex += 2)
                    {
                        float score;
                        DoorLocation sideDoor = new DoorLocation
                        {
                            Side = sideIndex,
                            End = endIndex,
                            LocalZ = GetSideDoorZ(bounds, endIndex)
                        };
                        if (IsAimedDoor(coach, bounds, sideDoor, ray, out score) && score < closestScore)
                        {
                            closestCoach = coach;
                            closestDoor = sideDoor;
                            closestScore = score;
                        }

                        // Free end doors are not entry/exit points. End doors
                        // are offered only from inside a coach when a coupled
                        // passenger coach is available (see below).
                    }
                }
            }

            aimedDoor = closestDoor;
            return closestCoach;
        }

        private static TrainCar FindAimedDoorOnCoach(TrainCar coach, out DoorLocation aimedDoor)
        {
            aimedDoor = new DoorLocation();
            if (coach == null)
            {
                return null;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = _localPlayer.GetComponentInChildren<Camera>();
            }

            Bounds bounds;
            if (camera == null || !_cachedCoachBounds.TryGetValue(coach, out bounds))
            {
                return null;
            }

            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0.0f));
            DoorLocation closestDoor = new DoorLocation();
            float closestScore = float.MaxValue;
            for (int sideIndex = -1; sideIndex <= 1; sideIndex += 2)
            {
                for (int endIndex = -1; endIndex <= 1; endIndex += 2)
                {
                    float score;
                    DoorLocation sideDoor = new DoorLocation
                    {
                        Side = sideIndex,
                        End = endIndex,
                        LocalZ = GetSideDoorZ(bounds, endIndex)
                    };
                    if (IsAimedDoor(coach, bounds, sideDoor, ray, out score) && score < closestScore)
                    {
                        closestDoor = sideDoor;
                        closestScore = score;
                    }

                    DoorLocation endDoor = new DoorLocation
                    {
                        Side = 0,
                        End = endIndex,
                        LocalZ = GetDoorEdgeZ(bounds, endIndex, 0.55f)
                    };
                    if (IsAimedDoor(coach, bounds, endDoor, ray, out score))
                    {
                        TrainCar coupledCoach = FindCoupledCoachAtEnd(coach, endDoor);
                        if (coupledCoach != null && IsPassengerCoach(coupledCoach) && score < closestScore)
                        {
                            closestDoor = endDoor;
                            closestScore = score;
                        }
                    }
                }
            }

            if (closestScore == float.MaxValue)
            {
                return null;
            }

            aimedDoor = closestDoor;
            return coach;
        }

        private static void RefreshPassengerCoachCache()
        {
            if (!_initialCoachScanComplete)
            {
                _initialCoachScanComplete = true;
                TrainCar[] existingCars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
                for (int i = 0; i < existingCars.Length; i++)
                {
                    RegisterPassengerCoach(existingCars[i]);
                }
            }

            if (_pendingCoachRegistrations.Count > 0 && ResolvePassengerCargo())
            {
                TrainCar[] pending = new TrainCar[_pendingCoachRegistrations.Count];
                _pendingCoachRegistrations.CopyTo(pending);
                _pendingCoachRegistrations.Clear();
                for (int i = 0; i < pending.Length; i++)
                {
                    RegisterPassengerCoach(pending[i]);
                }
            }

            if (Time.unscaledTime >= _nextCoachCacheCleanup)
            {
                _nextCoachCacheCleanup = Time.unscaledTime + 30.0f;
                TrainCar[] cars = _cachedPassengerCoaches;
                for (int i = 0; i < cars.Length; i++)
                {
                    if (cars[i] == null)
                    {
                        _coachCacheDirty = true;
                    }
                }
            }

            if (_coachCacheDirty)
            {
                RebuildPassengerCoachArray();
            }
        }

        private static void RegisterPassengerCoach(TrainCar car)
        {
            if (car == null || _cachedCoachBounds.ContainsKey(car))
            {
                return;
            }

            if (!ResolvePassengerCargo())
            {
                _pendingCoachRegistrations.Add(car);
                return;
            }

            // A spawned car can reach Awake before its livery/type is assigned.
            // Keep it pending so the next event/cache refresh can register it
            // after Passenger Jobs has finished initializing the car.
            if (car.carLivery == null || car.carLivery.parentType == null)
            {
                _pendingCoachRegistrations.Add(car);
                return;
            }

            if (!IsPassengerCoach(car))
            {
                _pendingCoachRegistrations.Remove(car);
                return;
            }

            Bounds bounds;
            if (!TryGetLocalBounds(car, out bounds))
            {
                return;
            }

            _cachedCoachBounds[car] = bounds;
            _coachCacheDirty = true;
        }

        private static void UnregisterPassengerCoach(TrainCar car)
        {
            _pendingCoachRegistrations.Remove(car);
            if (car != null && _cachedCoachBounds.Remove(car))
            {
                _coachCacheDirty = true;
            }
        }

        private static void RebuildPassengerCoachArray()
        {
            List<TrainCar> passengerCoaches = new List<TrainCar>();
            List<TrainCar> staleCars = new List<TrainCar>();
            foreach (KeyValuePair<TrainCar, Bounds> pair in _cachedCoachBounds)
            {
                if (pair.Key == null)
                {
                    staleCars.Add(pair.Key);
                }
                else
                {
                    passengerCoaches.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleCars.Count; i++)
            {
                _cachedCoachBounds.Remove(staleCars[i]);
            }

            _cachedPassengerCoaches = passengerCoaches.ToArray();
            _coachCacheDirty = false;
            _nearbyPassengerCoaches = new TrainCar[0];
            _nextNearbyRefresh = 0.0f;
        }

        private static void RefreshNearbyCoachCandidates()
        {
            if (_localPlayer == null)
            {
                return;
            }

            Vector3 origin = _localPlayer.transform.position;
            if (Time.unscaledTime < _nextNearbyRefresh &&
                Vector3.SqrMagnitude(origin - _lastNearbyOrigin) < 64.0f)
            {
                return;
            }

            _nextNearbyRefresh = Time.unscaledTime + 1.0f;
            _lastNearbyOrigin = origin;
            List<TrainCar> nearby = new List<TrainCar>();
            TrainCar[] cars = _cachedPassengerCoaches;
            for (int i = 0; i < cars.Length; i++)
            {
                TrainCar coach = cars[i];
                Bounds bounds;
                if (coach == null || !_cachedCoachBounds.TryGetValue(coach, out bounds))
                {
                    continue;
                }

                if (IsCoachWithinInteractionRange(coach, bounds, origin))
                {
                    nearby.Add(coach);
                }
            }

            _nearbyPassengerCoaches = nearby.ToArray();
        }

        private static bool IsCachedPassengerCoach(TrainCar coach)
        {
            TrainCar[] cars = _cachedPassengerCoaches;
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == coach)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAimedDoor(TrainCar coach, Bounds bounds, DoorLocation door, Ray ray, out float score)
        {
            score = float.MaxValue;
            float x = door.Side == 0
                ? 0.0f
                : (door.Side > 0 ? bounds.max.x + 0.08f : bounds.min.x - 0.08f);
            float y = Mathf.Clamp(bounds.min.y + 1.65f, bounds.min.y + 0.8f, bounds.max.y - 0.35f);
            Vector3 worldDoor = coach.transform.TransformPoint(new Vector3(x, y, door.LocalZ));
            Vector3 toDoor = worldDoor - _localPlayer.transform.position;
            float playerDistance = toDoor.magnitude;
            if (playerDistance > _settings.ActivationDistance + 0.35f)
            {
                return false;
            }

            float alongRay = Vector3.Dot(worldDoor - ray.origin, ray.direction);
            if (alongRay < 0.0f)
            {
                return false;
            }

            // Use a vertical interaction capsule around the door rather than a
            // single low point: normal eye-level aiming should hit the whole door.
            Vector3 closestOnRay = ray.origin + ray.direction * alongRay;
            float horizontalDistance = Vector3.Distance(
                Vector3.Scale(worldDoor, new Vector3(1.0f, 0.0f, 1.0f)),
                Vector3.Scale(closestOnRay, new Vector3(1.0f, 0.0f, 1.0f)));
            float verticalDistance = Mathf.Abs(worldDoor.y - closestOnRay.y);
            if (horizontalDistance > 1.15f || verticalDistance > 1.35f)
            {
                return false;
            }

            score = playerDistance + horizontalDistance * 1.5f + verticalDistance * 0.25f;
            return true;
        }

        private static bool IsCoachWithinInteractionRange(TrainCar coach, Bounds bounds, Vector3 origin)
        {
            float radius = bounds.extents.magnitude;
            float maxDistance = _settings.ActivationDistance + radius + 1.0f;
            return (coach.transform.position - origin).sqrMagnitude <= maxDistance * maxDistance;
        }

        private static bool TryGetLocalBounds(TrainCar coach, out Bounds localBounds)
        {
            localBounds = new Bounds();
            if (TryGetBodyColliderBounds(coach, out localBounds))
            {
                return true;
            }

            if (localBounds.size.sqrMagnitude <= 0.01f)
            {
                try
                {
                    localBounds = coach.Bounds;
                }
                catch
                {
                    localBounds = new Bounds();
                }
            }
            return localBounds.size.sqrMagnitude > 0.01f;
        }

        private static bool TryGetBodyColliderBounds(TrainCar coach, out Bounds localBounds)
        {
            localBounds = new Bounds();
            if (coach == null)
            {
                return false;
            }

            Transform collisionRoot = null;
            if (coach.carColliders != null && _collisionRootField != null)
            {
                collisionRoot = _collisionRootField.GetValue(coach.carColliders) as Transform;
            }

            if (collisionRoot == null)
            {
                Transform[] transforms = coach.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (string.Equals(transforms[i].name, "[collision]", StringComparison.OrdinalIgnoreCase))
                    {
                        collisionRoot = transforms[i];
                        break;
                    }
                }
            }

            if (collisionRoot == null)
            {
                return false;
            }

            BoxCollider[] colliders = collisionRoot.GetComponentsInChildren<BoxCollider>(true);
            return TryBuildColliderBounds(coach, colliders, out localBounds);
        }

        private static bool TryBuildColliderBounds(
            TrainCar coach, BoxCollider[] colliders, out Bounds localBounds)
        {
            localBounds = new Bounds();
            bool found = false;
            for (int i = 0; i < colliders.Length; i++)
            {
                BoxCollider collider = colliders[i];
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    !IsBodyCollider(collider, coach))
                {
                    continue;
                }

                Vector3 halfSize = collider.size * 0.5f;
                Vector3 center = collider.center;
                Vector3[] corners =
                {
                    center + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z),
                    center + new Vector3(-halfSize.x, -halfSize.y, halfSize.z),
                    center + new Vector3(-halfSize.x, halfSize.y, -halfSize.z),
                    center + new Vector3(-halfSize.x, halfSize.y, halfSize.z),
                    center + new Vector3(halfSize.x, -halfSize.y, -halfSize.z),
                    center + new Vector3(halfSize.x, -halfSize.y, halfSize.z),
                    center + new Vector3(halfSize.x, halfSize.y, -halfSize.z),
                    center + new Vector3(halfSize.x, halfSize.y, halfSize.z)
                };
                for (int c = 0; c < corners.Length; c++)
                {
                    Vector3 worldPoint = collider.transform.TransformPoint(corners[c]);
                    Vector3 localPoint = coach.transform.InverseTransformPoint(worldPoint);
                    if (!found)
                    {
                        localBounds = new Bounds(localPoint, Vector3.zero);
                        found = true;
                    }
                    else
                    {
                        localBounds.Encapsulate(localPoint);
                    }
                }
            }

            return found && localBounds.size.sqrMagnitude > 0.01f;
        }

        private static bool IsBodyCollider(BoxCollider collider, TrainCar coach)
        {
            bool rejectedBranch = false;
            Transform current = collider.transform;
            while (current != null)
            {
                string name = current.name.ToLowerInvariant();
                if (name.Contains("coupler") || name.Contains("bogie") || name.Contains("wheel") ||
                    name.Contains("buffer") || name.Contains("chain"))
                {
                    rejectedBranch = true;
                }
                if (current == coach.transform)
                {
                    break;
                }
                current = current.parent;
            }

            return !rejectedBranch;
        }

        private static bool EnsureLocalPlayer()
        {
            if (_localPlayer)
            {
                return true;
            }

            FindLocalPlayer();
            return _localPlayer != null;
        }

        private sealed class PromptOverlay : MonoBehaviour
        {
            private GUIStyle _style;

            private void OnGUI()
            {
                if (Main._modEntry == null || !Main._modEntry.Active || Main._aimedCoach == null)
                {
                    return;
                }

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 17,
                        fontStyle = FontStyle.Bold,
                        normal = { textColor = Color.white }
                    };
                }

                string keyName = Main._settings.AccessKey.keyCode.ToString();
                bool isInside = Main._occupiedCoach != null &&
                    Main.IsPlayerActuallyInsideCoach(Main._occupiedCoach);
                bool canTraverse = isInside && Main._aimedDoor.Side == 0 &&
                    Main._aimedNextCoach != null;
                string text;
                if (!isInside)
                {
                    text = Main.T("Нажмите " + keyName + ", чтобы войти в вагон",
                        "Press " + keyName + " to enter the coach");
                }
                else if (Main._aimedDoor.Side == 0 && canTraverse)
                {
                    text = Main.T("Нажмите " + keyName + ", чтобы перейти в другой вагон",
                        "Press " + keyName + " to move to the next coach");
                }
                else
                {
                    text = Main.T("Нажмите " + keyName + ", чтобы выйти из вагона",
                        "Press " + keyName + " to exit the coach");
                }
                GUI.Label(new Rect(0.0f, Screen.height * 0.84f, Screen.width, 30.0f), text, _style);
            }
        }

        private static void FindLocalPlayer()
        {
            Type playerControllerType = AccessTools.TypeByName("CustomFirstPersonController");
            if (playerControllerType == null)
            {
                return;
            }

            Component candidate = UnityEngine.Object.FindObjectOfType(playerControllerType) as Component;
            if (candidate != null)
            {
                SetLocalPlayer(candidate);
            }
        }

        private static void SetLocalPlayer(Component player)
        {
            _localPlayer = player;
            _characterReparenting = _localPlayer.GetComponent("CharacterReparenting");
            _reparentTo = _characterReparenting == null
                ? null
                : AccessTools.Method(_characterReparenting.GetType(), "ReparentTo");
        }

        private static bool ResolvePassengerCargo()
        {
            if (_passengerCargo != null && _isLoadableOnCarType != null)
            {
                return true;
            }

            if (_passengerLookupFailed)
            {
                return false;
            }

            try
            {
                Type injectorType = AccessTools.TypeByName("PassengerJobs.Injectors.CargoInjector");
                PropertyInfo cargoProperty = injectorType == null
                    ? null
                    : AccessTools.Property(injectorType, "PassengerCargo");

                _passengerCargo = cargoProperty == null ? null : cargoProperty.GetValue(null, null);
                _isLoadableOnCarType = _passengerCargo == null
                    ? null
                    : AccessTools.Method(_passengerCargo.GetType(), "IsLoadableOnCarType");

                if (_passengerCargo == null || _isLoadableOnCarType == null)
                {
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                _passengerLookupFailed = true;
                _modEntry.Logger.LogException(exception);
                _modEntry.Logger.Error("Passenger Jobs could not be queried for passenger-coach types.");
                return false;
            }
        }

        private static bool IsPassengerCoach(TrainCar car)
        {
            try
            {
                if (car == null || car.carLivery == null || car.carLivery.parentType == null)
                {
                    return false;
                }

                object result = _isLoadableOnCarType.Invoke(
                    _passengerCargo,
                    new object[] { car.carLivery.parentType });
                return result is bool && (bool)result;
            }
            catch (Exception exception)
            {
                _modEntry.Logger.LogException(exception);
                return false;
            }
        }

        private static void EnterCoach(TrainCar coach, DoorLocation door)
        {
            try
            {
                if (coach.carLivery != null && coach.carLivery.interiorPrefab != null && !coach.IsInteriorLoaded)
                {
                    coach.LoadInterior();
                }

                _occupiedCoach = coach;
                _enteredThrough = door;
                _stateLockUntil = Time.unscaledTime + 1.0f;
                QueuePlayerPlacement(coach, door, true);
                _modEntry.Logger.Log("Entered passenger coach " + coach.ID + ".");
            }
            catch (Exception exception)
            {
                _modEntry.Logger.LogException(exception);
                _modEntry.Logger.Error("Unable to enter this passenger coach.");
            }
        }

        private static void LeaveCoach()
        {
            LeaveCoach(_enteredThrough);
        }

        private static void LeaveCoach(DoorLocation exitDoor)
        {
            TrainCar coach = _occupiedCoach;
            _occupiedCoach = null;
            _stateLockUntil = Time.unscaledTime + 1.0f;

            // End doors are reserved exclusively for traversing into an
            // attached passenger coach. They never provide an outside exit.
            if (exitDoor.Side == 0)
            {
                return;
            }

            if (coach == null || !EnsureLocalPlayer())
            {
                return;
            }

            try
            {
                Vector3 outsidePosition = GetFallbackOutsidePosition(coach, exitDoor);
                ReparentPlayer(null, false, null);
                QueuePlayerPlacement(outsidePosition);
                _modEntry.Logger.Log("Left passenger coach " + coach.ID + ".");
            }
            catch (Exception exception)
            {
                _modEntry.Logger.LogException(exception);
                _modEntry.Logger.Error("Unable to leave the passenger coach cleanly.");
            }
        }

        private static bool ReparentPlayer(Transform parent, bool isTrain, Component reparentTarget)
        {
            if (_characterReparenting == null || _reparentTo == null)
            {
                SetLocalPlayer(_localPlayer);
            }

            if (_characterReparenting == null || _reparentTo == null)
            {
                _modEntry.Logger.Error("The game character controller is not ready yet.");
                return false;
            }

            _reparentTo.Invoke(_characterReparenting, new object[] { parent, isTrain, reparentTarget });
            return true;
        }

        private static void QueuePlayerPlacement(TrainCar coach, DoorLocation door, bool requireOccupiedCoach)
        {
            int token = ++_teleportToken;
            if (_coroutineRunner != null)
            {
                _coroutineRunner.StartCoroutine(PlacePlayerInCoach(token, coach, door, requireOccupiedCoach));
                return;
            }

            TeleportPlayerUsingGame(GetFallbackInteriorPosition(coach, door), coach);
        }

        private static void QueuePlayerPlacement(Vector3 worldPosition)
        {
            int token = ++_teleportToken;
            if (_coroutineRunner != null)
            {
                _coroutineRunner.StartCoroutine(PlacePlayerOutside(token, worldPosition));
                return;
            }

            TeleportPlayerUsingGame(worldPosition, null, true);
        }

        private static IEnumerator PlacePlayerInCoach(
            int token,
            TrainCar coach,
            DoorLocation door,
            bool requireOccupiedCoach)
        {
            // Let the loaded interior and its colliders finish their frame before
            // handing the exact floor point to the game's teleport system.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (token != _teleportToken || coach == null ||
                (requireOccupiedCoach && _occupiedCoach != coach))
            {
                yield break;
            }

            TeleportPlayerUsingGame(GetFallbackInteriorPosition(coach, door), coach);
        }

        private static IEnumerator PlacePlayerOutside(int token, Vector3 worldPosition)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            if (token != _teleportToken || _occupiedCoach != null)
            {
                yield break;
            }

            TeleportPlayerUsingGame(worldPosition, null, true);
        }

        private static void TeleportPlayerUsingGame(
            Vector3 worldPosition, TrainCar targetCoach, bool exterior = false)
        {
            if (_localPlayer == null)
            {
                return;
            }

            Transform teleportSurface = exterior || targetCoach == null
                ? null
                : (targetCoach.interior != null ? targetCoach.interior : targetCoach.transform);
            PlayerManager.TeleportPlayer(
                worldPosition,
                _localPlayer.transform.rotation,
                teleportSurface,
                false,
                false);

        }

        private static Transform FindTeleportAnchor(TrainCar coach, Vector3 fromPosition)
        {
            Transform[] transforms = coach.GetComponentsInChildren<Transform>(true);
            Transform closestAnchor = null;
            float closestDistance = float.MaxValue;

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate.name.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                float distance = Vector3.SqrMagnitude(candidate.position - fromPosition);
                if (distance < closestDistance)
                {
                    closestAnchor = candidate;
                    closestDistance = distance;
                }
            }

            return closestAnchor;
        }

        private static Vector3 GetFallbackInteriorPosition(TrainCar coach, DoorLocation door)
        {
            Bounds bounds;
            if (!TryGetLocalBounds(coach, out bounds))
            {
                bounds = new Bounds(Vector3.zero, new Vector3(2.5f, 3.0f, 18.0f));
            }

            // Side doors open into the vestibule.  Keep the spawn on the aisle
            // centreline so the character cannot be embedded in the side wall.
            float insideX = bounds.center.x;
            float edgeZ = door.End > 0 ? bounds.max.z : bounds.min.z;
            float insideZ = door.Side == 0
                ? edgeZ - door.End * 1.15f
                : door.LocalZ;
            float floorY = FindInteriorFloorY(coach, bounds, insideX, insideZ);
            return coach.transform.TransformPoint(new Vector3(insideX, floorY, insideZ));
        }

        private static Vector3 GetFallbackOutsidePosition(TrainCar coach, DoorLocation door)
        {
            Bounds bounds;
            if (!TryGetBodyColliderBounds(coach, out bounds) &&
                !_cachedCoachBounds.TryGetValue(coach, out bounds))
            {
                bounds = new Bounds(Vector3.zero, new Vector3(2.5f, 3.0f, 18.0f));
            }

            float localX = door.Side > 0
                ? bounds.max.x + MinimumExteriorDoorDistance
                : bounds.min.x - MinimumExteriorDoorDistance;
            float localZ = door.LocalZ;
            Vector3 probe = coach.transform.TransformPoint(new Vector3(
                localX, bounds.max.y + 2.0f, localZ));
            RaycastHit[] hits = Physics.RaycastAll(probe, Vector3.down, 8.0f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float floorY = float.MinValue;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider == null || hits[i].normal.y < 0.45f)
                {
                    continue;
                }

                Vector3 hitLocal = coach.transform.InverseTransformPoint(hits[i].point);
                if (Mathf.Abs(hitLocal.z - localZ) <= 1.2f &&
                    hitLocal.y < bounds.max.y - 0.35f)
                {
                    floorY = Mathf.Max(floorY, hitLocal.y);
                }
            }

            if (floorY == float.MinValue)
            {
                floorY = coach.PivotToTrackHeightOffset + 1.12f;
            }

            return coach.transform.TransformPoint(new Vector3(localX, floorY, localZ));
        }

        private static float FindInteriorFloorY(TrainCar coach, Bounds bounds, float localX, float localZ)
        {
            Transform interiorRoot = coach.interior;
            if (interiorRoot == null && coach.loadedInterior != null)
            {
                interiorRoot = coach.loadedInterior.transform;
            }

            Transform walkableRoot = FindWalkableRoot(coach);
            Vector3 localProbe = new Vector3(localX, bounds.max.y + 2.0f, localZ);
            Vector3 worldProbe = coach.transform.TransformPoint(localProbe);
            RaycastHit[] hits = Physics.RaycastAll(worldProbe, Vector3.down, 8.0f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float bestWalkableY = float.MinValue;
            float bestNamedFloorY = float.MinValue;
            float bestInteriorSupportY = float.MinValue;
            float bestCoachSupportY = float.MinValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hitTransform = hits[i].collider == null ? null : hits[i].collider.transform;
                if (hitTransform == null ||
                    (hitTransform != coach.transform && !hitTransform.IsChildOf(coach.transform)) ||
                    hits[i].normal.y <= 0.45f)
                {
                    continue;
                }

                float localY = coach.transform.InverseTransformPoint(hits[i].point).y;
                if (localY >= bounds.max.y - 0.35f)
                {
                    continue;
                }

                bool belongsToWalkable = walkableRoot != null &&
                    (hitTransform == walkableRoot || hitTransform.IsChildOf(walkableRoot));
                bool belongsToInterior = interiorRoot != null && hitTransform.IsChildOf(interiorRoot);
                bestCoachSupportY = Mathf.Max(bestCoachSupportY, localY);
                if (belongsToInterior)
                {
                    bestInteriorSupportY = Mathf.Max(bestInteriorSupportY, localY);
                }
                if (belongsToWalkable)
                {
                    bestWalkableY = Mathf.Max(bestWalkableY, localY);
                }
                if (LooksLikeFloorSurface(hitTransform, coach))
                {
                    bestNamedFloorY = Mathf.Max(bestNamedFloorY, localY);
                }
            }

            float bestY = bestWalkableY != float.MinValue
                ? bestWalkableY
                : (bestNamedFloorY != float.MinValue
                    ? bestNamedFloorY
                    : (bestInteriorSupportY != float.MinValue
                        ? bestInteriorSupportY
                        : bestCoachSupportY));
            if (bestY != float.MinValue)
            {
                return bestY - TeleportSurfaceOffset;
            }

            // This runs only while a streamed asset has no available floor
            // collider. Keep the emergency point model-relative, not global.
            return bounds.min.y + bounds.size.y * 0.18f - TeleportSurfaceOffset;
        }

        private static bool LooksLikeFloorSurface(Transform transform, TrainCar coach)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name.ToLowerInvariant();
                if (name.Contains("walkable") || name.Contains("floor") ||
                    name.Contains("ground") || name.Contains("rubber"))
                {
                    return true;
                }
                if (current == coach.transform)
                {
                    break;
                }
            }

            return false;
        }

        private static void TraverseEndDoor(TrainCar currentCoach, DoorLocation door)
        {
            int nextEnd;
            TrainCar nextCoach = FindCoachBeyondEndDoor(currentCoach, door, out nextEnd);
            if (nextCoach == null)
            {
                // A free end door is intentionally not an exit. Only a
                // genuinely coupled passenger coach can be traversed here.
                return;
            }

            DoorLocation nextDoor = new DoorLocation
            {
                Side = 0,
                End = nextEnd,
                LocalZ = GetDoorEndZ(nextCoach, nextEnd)
            };

            try
            {
                if (nextCoach.carLivery != null && nextCoach.carLivery.interiorPrefab != null && !nextCoach.IsInteriorLoaded)
                {
                    nextCoach.LoadInterior();
                }

                _occupiedCoach = nextCoach;
                _enteredThrough = nextDoor;
                _stateLockUntil = Time.unscaledTime + 1.0f;
                QueuePlayerPlacement(nextCoach, nextDoor, true);
                _modEntry.Logger.Log("Moved to adjacent passenger coach " + nextCoach.ID + ".");
            }
            catch (Exception exception)
            {
                _modEntry.Logger.LogException(exception);
                _modEntry.Logger.Error("Unable to pass through this end door.");
            }
        }

        private static TrainCar FindCoachBeyondEndDoor(TrainCar currentCoach, DoorLocation door)
        {
            int ignoredEnd;
            return FindCoachBeyondEndDoor(currentCoach, door, out ignoredEnd);
        }

        private static TrainCar FindCoachBeyondEndDoor(TrainCar currentCoach, DoorLocation door, out int nextEnd)
        {
            nextEnd = 0;
            Coupler selectedCoupler = door.End > 0 ? currentCoach.frontCoupler : currentCoach.rearCoupler;
            if (selectedCoupler == null)
            {
                return null;
            }

            Coupler coupled = selectedCoupler.GetCoupled();
            if (coupled == null || coupled.train == null || !IsPassengerCoach(coupled.train))
            {
                return null;
            }

            nextEnd = coupled.isFrontCoupler ? 1 : -1;
            return coupled.train;
        }

        private static TrainCar FindCoupledCoachAtEnd(TrainCar coach, DoorLocation door)
        {
            if (coach == null || door.Side != 0)
            {
                return null;
            }

            Coupler selectedCoupler = door.End > 0 ? coach.frontCoupler : coach.rearCoupler;
            if (selectedCoupler == null)
            {
                return null;
            }

            Coupler coupled = selectedCoupler.GetCoupled();
            return coupled == null ? null : coupled.train;
        }

        private static float GetDoorEndZ(TrainCar coach, int end)
        {
            Bounds bounds;
            if (!_cachedCoachBounds.TryGetValue(coach, out bounds))
            {
                TryGetLocalBounds(coach, out bounds);
            }

            return GetDoorEdgeZ(bounds, end, 0.55f);
        }

        private static float GetSideDoorZ(Bounds bounds, int end)
        {
            return GetDoorEdgeZ(bounds, end, 1.25f);
        }

        private static float GetDoorEdgeZ(Bounds bounds, int end, float inset)
        {
            float edge = end > 0 ? bounds.max.z : bounds.min.z;
            return edge - end * inset;
        }

        private static Transform FindWalkableRoot(TrainCar coach)
        {
            if (coach == null)
            {
                return null;
            }

            Transform[] transforms = coach.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (string.Equals(transforms[i].name, "[walkable]", StringComparison.OrdinalIgnoreCase))
                {
                    return transforms[i];
                }
            }

            Component reparentTarget = FindReparentTarget(coach);
            return reparentTarget == null ? null : reparentTarget.transform;
        }

        private static Component FindReparentTarget(TrainCar coach)
        {
            Type reparentTargetType = AccessTools.TypeByName("DV.CharacterReparentTarget");
            if (reparentTargetType == null || coach == null)
            {
                return null;
            }

            Transform[] transforms = coach.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Component target = transforms[i].GetComponent(reparentTargetType);
                if (target != null)
                {
                    return target;
                }
            }

            return null;
        }

        private static void RegisterMultiplayerEvents()
        {
            if (!_multiplayerEventsRegistered)
            {
                MPAPI.MultiplayerAPI.ServerStarted += OnMultiplayerServerStarted;
                MPAPI.MultiplayerAPI.ServerStopped += OnMultiplayerServerStopped;
                MPAPI.MultiplayerAPI.ClientStarted += OnMultiplayerClientStarted;
                MPAPI.MultiplayerAPI.ClientStopped += OnMultiplayerClientStopped;
                _multiplayerEventsRegistered = true;
            }

            if (_multiplayerServer == null && MPAPI.MultiplayerAPI.Server != null)
            {
                OnMultiplayerServerStarted(MPAPI.MultiplayerAPI.Server);
            }

            if (_multiplayerClient == null && MPAPI.MultiplayerAPI.Client != null)
            {
                OnMultiplayerClientStarted(MPAPI.MultiplayerAPI.Client);
            }
        }

        private static void OnMultiplayerServerStarted(IServer server)
        {
            if (server == null || _multiplayerServer == server)
            {
                return;
            }

            _multiplayerServer = server;
            _multiplayerServer.OnPlayerConnected += OnMultiplayerPlayerConnected;
            _usingHostSettings = false;
            SendHostSettings(null);
            _modEntry.Logger.Log("Multiplayer host settings synchronization started.");
        }

        private static void OnMultiplayerServerStopped()
        {
            if (_multiplayerServer != null)
            {
                _multiplayerServer.OnPlayerConnected -= OnMultiplayerPlayerConnected;
            }
            _multiplayerServer = null;
        }

        private static void OnMultiplayerClientStarted(IClient client)
        {
            if (client == null || _multiplayerClient == client)
            {
                return;
            }

            if (MPAPI.MultiplayerAPI.Instance != null && MPAPI.MultiplayerAPI.Instance.IsHost)
            {
                return;
            }

            _multiplayerClient = client;
            _localSettingsSnapshot = CreateSettingsPacket();
            _usingHostSettings = true;
            _multiplayerClient.RegisterPacket<CoachAccessSettingsPacket>(OnHostSettingsReceived);
            _modEntry.Logger.Log("Waiting for authoritative settings from the multiplayer host.");
        }

        private static void OnMultiplayerClientStopped()
        {
            _multiplayerClient = null;
            _usingHostSettings = false;
            if (_localSettingsSnapshot != null)
            {
                ApplySettingsPacket(_localSettingsSnapshot);
                _localSettingsSnapshot = null;
            }
        }

        private static void OnMultiplayerPlayerConnected(IPlayer player)
        {
            if (player != null && !player.IsHost)
            {
                SendHostSettings(player);
            }
        }

        private static void SendHostSettings(IPlayer player)
        {
            if (_multiplayerServer == null)
            {
                return;
            }

            CoachAccessSettingsPacket packet = CreateSettingsPacket();
            if (player == null)
            {
                _multiplayerServer.SendPacketToAll(packet, true, true, null);
            }
            else
            {
                _multiplayerServer.SendPacketToPlayer(packet, player, true);
            }
        }

        private static CoachAccessSettingsPacket CreateSettingsPacket()
        {
            return new CoachAccessSettingsPacket
            {
                ActivationDistance = _settings.ActivationDistance,
                MinimumDoorDistanceFromCentre = _settings.MinimumDoorDistanceFromCentre,
                FallbackInteriorHeight = _settings.FallbackInteriorHeight
            };
        }

        private static void OnHostSettingsReceived(CoachAccessSettingsPacket packet)
        {
            if (packet == null || !_usingHostSettings)
            {
                return;
            }

            ApplySettingsPacket(packet);
            _modEntry.Logger.Log("Applied authoritative Passenger Coach Access settings from host.");
        }

        private static void ApplySettingsPacket(CoachAccessSettingsPacket packet)
        {
            _settings.ActivationDistance = Mathf.Clamp(packet.ActivationDistance, 1.0f, 4.0f);
            _settings.MinimumDoorDistanceFromCentre = Mathf.Clamp(
                packet.MinimumDoorDistanceFromCentre, 1.5f, 6.0f);
            _settings.FallbackInteriorHeight = Mathf.Clamp(packet.FallbackInteriorHeight, 1.0f, 3.0f);
            _nextNearbyRefresh = 0.0f;
        }

        private static void RegisterMultiplayerCompatibilityIfAvailable()
        {
            if (_multiplayerCompatibilityRegistered || Time.realtimeSinceStartup < _nextMultiplayerCheck)
            {
                return;
            }

            _nextMultiplayerCheck = Time.realtimeSinceStartup + 2.0f;

            try
            {
                Type multiplayerApiType = AccessTools.TypeByName("MPAPI.MultiplayerAPI");
                if (multiplayerApiType == null)
                {
                    return;
                }

                PropertyInfo loadedProperty = AccessTools.Property(multiplayerApiType, "IsMultiplayerLoaded");
                if (loadedProperty == null || !(bool)loadedProperty.GetValue(null, null))
                {
                    return;
                }

                PropertyInfo instanceProperty = AccessTools.Property(multiplayerApiType, "Instance");
                object apiInstance = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
                Type compatibilityType = AccessTools.TypeByName("MPAPI.Types.MultiplayerCompatibility");
                MethodInfo setCompatibility = apiInstance == null
                    ? null
                    : AccessTools.Method(apiInstance.GetType(), "SetModCompatibility");

                if (apiInstance == null || compatibilityType == null || setCompatibility == null)
                {
                    return;
                }

                object allPlayers = Enum.Parse(compatibilityType, "All");
                setCompatibility.Invoke(apiInstance, new object[] { _modEntry.Info.Id, allPlayers });
                _multiplayerCompatibilityRegistered = true;
                _modEntry.Logger.Log("Registered Multiplayer compatibility: every participant must use Passenger Coach Access.");
            }
            catch (Exception exception)
            {
                _modEntry.Logger.LogException(exception);
            }
        }

        [HarmonyPatch]
        private static class LocalPlayerAwakePatch
        {
            private static MethodBase TargetMethod()
            {
                Type playerControllerType = AccessTools.TypeByName("CustomFirstPersonController");
                return playerControllerType == null ? null : AccessTools.Method(playerControllerType, "Awake");
            }

            private static void Postfix(object __instance)
            {
                Component player = __instance as Component;
                if (player != null)
                {
                    SetLocalPlayer(player);
                }
            }
        }

        [HarmonyPatch(typeof(TrainCar), "AwakeForPooledCar")]
        private static class TrainCarAwakePatch
        {
            private static void Postfix(TrainCar __instance)
            {
                RegisterPassengerCoach(__instance);
            }
        }

        [HarmonyPatch(typeof(TrainCar), "Awake")]
        private static class TrainCarCreatedPatch
        {
            private static void Postfix(TrainCar __instance)
            {
                RegisterPassengerCoach(__instance);
            }
        }

        [HarmonyPatch(typeof(TrainCar), "Start")]
        private static class TrainCarStartedPatch
        {
            private static void Postfix(TrainCar __instance)
            {
                RegisterPassengerCoach(__instance);
            }
        }

        [HarmonyPatch(typeof(TrainCar), "OnDestroy")]
        private static class TrainCarDestroyPatch
        {
            private static void Prefix(TrainCar __instance)
            {
                UnregisterPassengerCoach(__instance);
            }
        }
    }
}
