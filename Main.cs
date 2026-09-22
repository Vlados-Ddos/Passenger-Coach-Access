using System;
using System.Reflection;
using HarmonyLib;
using I2.Loc;
using UnityEngine;
using UnityModManagerNet;

[assembly: AssemblyVersion("1.34.0.0")]
[assembly: AssemblyFileVersion("1.34.0.0")]

namespace PassengerCoachAccess
{
    public sealed class CoachAccessSettings : UnityModManager.ModSettings
    {
        public KeyBinding AccessKey = new KeyBinding();
        public float ActivationDistance = 2.25f;

        public CoachAccessSettings()
        {
            // UMM's key enum differs between loader releases; keep its own binding UI.
            foreach (MethodInfo method in typeof(KeyBinding).GetMethods())
            {
                if (method.Name != "Change" || method.GetParameters().Length != 4) continue;
                Type keyType = method.GetParameters()[0].ParameterType;
                method.Invoke(AccessKey, new object[] { Enum.Parse(keyType, "E"), false, false, false });
                break;
            }
        }

        public override void Save(UnityModManager.ModEntry entry) { Save(this, entry); }
    }

    public static class Main
    {
        internal static UnityModManager.ModEntry Entry;
        internal static CoachAccessSettings Settings;
        internal static readonly CoachRegistry Registry = new CoachRegistry();
        private static Transform player;
        private static CustomFirstPersonController controller;
        private static CharacterController capsule;
        private static AccessAction action;
        private static float nextInput;
        private static float nextVisibility;
        private static CoachDoor visibleDoor;
        private static bool visible;
        private static string noticeRu;
        private static string noticeEn;
        private static float noticeUntil;
        private static readonly RaycastHit[] sightHits = new RaycastHit[32];
        private static readonly AccessPlacement placement = new AccessPlacement();

        internal static float Distance { get { return MultiplayerSync.Distance; } }
        internal static string Text(string ru, string en)
        {
            // Read the game's current UI language, including changes made during play.
            string code = LocalizationManager.CurrentLanguageCode;
            bool russian = string.Equals(code, "ru", StringComparison.OrdinalIgnoreCase) ||
                (code != null && (code.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) ||
                                  code.StartsWith("ru_", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrEmpty(code))
                russian = string.Equals(LocalizationManager.CurrentLanguage, "Russian", StringComparison.OrdinalIgnoreCase);
            return russian ? ru : en;
        }

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry;
            Settings = UnityModManager.ModSettings.Load<CoachAccessSettings>(entry);
            Settings.ActivationDistance = SanitizeDistance(Settings.ActivationDistance);
            if (Settings.AccessKey == null) Settings.AccessKey = new CoachAccessSettings().AccessKey;
            entry.OnGUI = DrawGui;
            entry.OnSaveGUI = SaveGui;
            entry.OnUpdate = Update;
            entry.OnToggle = Toggle;
            new Harmony(entry.Info.Id).PatchAll(Assembly.GetExecutingAssembly());
            GameObject overlay = new GameObject("PassengerCoachAccessPrompt");
            UnityEngine.Object.DontDestroyOnLoad(overlay);
            overlay.AddComponent<PromptOverlay>();
            MultiplayerSync.Initialize();
            if (WorldStreamingInit.IsLoaded) Registry.Attach(CarSpawner.Instance);
            entry.Logger.Log("Passenger Coach Access 1.34 loaded; geometry and native interior access enabled.");
            return true;
        }

        public static float SanitizeDistance(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 2.25f : Mathf.Clamp(value, 1f, 4f);
        }

        public static float GetActivationDistanceForMultiplayer()
        {
            return SanitizeDistance(Settings == null ? 2.25f : Settings.ActivationDistance);
        }

        public static void LogOptionalMultiplayerFailure(Exception exception)
        {
            if (Entry != null && Entry.Logger != null)
                Entry.Logger.Warning("Optional Multiplayer integration is unavailable: " + exception.GetBaseException().Message);
        }

        private static bool Toggle(UnityModManager.ModEntry entry, bool active)
        {
            if (!active && MultiplayerSync.InSession)
            {
                entry.Logger.Warning("Passenger Coach Access must remain enabled during a multiplayer session.");
                return false;
            }
            action = default(AccessAction);
            visibleDoor = null;
            // Native floor/reparenting continues to work when the access UI is disabled.
            return true;
        }

        private static void DrawGui(UnityModManager.ModEntry entry)
        {
            GUILayout.Label(Text("Наведите прицел на нужную дверь. Торцевые двери ведут только в сцепленный пассажирский вагон.",
                "Aim at a door. End doors only lead to the coupled passenger coach."));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Text("Кнопка входа/выхода", "Access key"), GUILayout.Width(260));
            UnityModManager.UI.DrawKeybindingSmart(Settings.AccessKey, string.Empty,
                key => Settings.AccessKey = key, GUI.skin.button, GUILayout.Width(180));
            GUILayout.EndHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !MultiplayerSync.IsClient;
            GUILayout.BeginHorizontal();
            float distance = Distance;
            GUILayout.Label(Text("Дистанция активации: ", "Activation distance: ") + distance.ToString("0.00") + " m", GUILayout.Width(260));
            float edited = GUILayout.HorizontalSlider(distance, 1f, 4f, GUILayout.Width(260));
            if (!MultiplayerSync.IsClient) Settings.ActivationDistance = edited;
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
        }

        private static void SaveGui(UnityModManager.ModEntry entry)
        {
            Settings.ActivationDistance = SanitizeDistance(Settings.ActivationDistance);
            // Received host values never enter the local settings object.
            Settings.Save(entry);
            MultiplayerSync.Broadcast();
        }

        private static void Update(UnityModManager.ModEntry entry, float deltaTime)
        {
            MultiplayerSync.Tick(Time.unscaledTime);
            Registry.Tick();
            if (!entry.Active || !WorldStreamingInit.IsLoaded || LoadingScreenManager.IsLoading ||
                UnloadWatcher.isUnloading || !MultiplayerSync.Ready || Time.timeScale == 0f ||
                Cursor.lockState != CursorLockMode.Locked || !RefreshPlayer())
            {
                action = default(AccessAction);
                visibleDoor = null;
                return;
            }
            bool pressed = Settings.AccessKey.Down() && Time.unscaledTime >= nextInput;
            Registry.RefreshNearby(player.position, pressed);
            action = SelectAction(pressed);
            if (!pressed || action.Door == null) return;
            nextInput = Time.unscaledTime + 0.35f;
            try { Execute(action); }
            catch (Exception exception)
            {
                Entry.Logger.LogException(exception);
                Notify("Не удалось выполнить переход. Подробности в журнале.", "Access failed; see the log for details.");
            }
            action = default(AccessAction);
            visibleDoor = null;
            Registry.RefreshNearby(player.position, true);
        }

        private static bool RefreshPlayer()
        {
            Transform current = PlayerManager.PlayerTransform;
            if (current != player)
            {
                player = current;
                controller = player == null ? null : player.GetComponent<CustomFirstPersonController>();
                capsule = player == null ? null : player.GetComponent<CharacterController>();
                action = default(AccessAction);
                visibleDoor = null;
            }
            return player != null && controller != null && capsule != null &&
                controller.enabled && !controller.isRepositioning && PlayerManager.PlayerCamera != null &&
                PlayerManager.ActiveCamera == PlayerManager.PlayerCamera;
        }

        private static AccessAction SelectAction(bool forceVisibility)
        {
            Vector3 body = player.TransformPoint(capsule.center);
            CoachGeometry occupied = Registry.FindContaining(body);
            Ray ray = PlayerManager.PlayerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            AccessAction best = default(AccessAction);
            float distance = float.MaxValue;
            for (int i = 0; i < Registry.Nearby.Count; i++)
            {
                CoachGeometry coach = Registry.Nearby[i];
                if (!coach.EnsureReady() || (occupied != null && occupied != coach)) continue;
                bool inside = occupied == coach;
                for (int j = 0; j < coach.Doors.Count; j++)
                {
                    CoachDoor door = coach.Doors[j];
                    if (!inside && door.Kind == CoachDoorKind.End) continue;
                    float hitDistance;
                    if (!door.IsAimed(ray, body, Distance, inside, out hitDistance) || hitDistance >= distance) continue;
                    CoachDoor target = null;
                    if (door.Kind == CoachDoorKind.End && !Registry.TryGetConnectedDoor(door, out target)) continue;
                    best = new AccessAction { Door = door, Target = target, IsInside = inside };
                    distance = hitDistance;
                }
            }
            if (best.Door == null) { visibleDoor = null; return best; }
            if (forceVisibility || visibleDoor != best.Door || Time.unscaledTime >= nextVisibility)
            {
                visibleDoor = best.Door;
                nextVisibility = Time.unscaledTime + 0.1f;
                visible = HasLineOfSight(ray, distance);
            }
            return visible ? best : default(AccessAction);
        }

        private static bool HasLineOfSight(Ray ray, float distance)
        {
            // Door panels can lie slightly in front of the measured access plane.
            float range = Mathf.Max(0f, distance - 0.12f);
            int count = Physics.RaycastNonAlloc(ray, sightHits, range, controller.GetTraversableLayers(), QueryTriggerInteraction.Ignore);
            try
            {
                if (count == sightHits.Length) return false;
                for (int i = 0; i < count; i++) if (!IsPlayerCollider(sightHits[i].collider)) return false;
                return true;
            }
            finally { Array.Clear(sightHits, 0, count); }
        }

        private static void Execute(AccessAction selected)
        {
            CoachDoor destinationDoor = selected.Target ?? selected.Door;
            if (selected.Target != null)
            {
                CoachDoor currentTarget;
                if (!Registry.TryGetConnectedDoor(selected.Door, out currentTarget) || currentTarget != selected.Target) return;
            }
            bool goingInside = !selected.IsInside || selected.Target != null;
            TrainCar destination = goingInside ? destinationDoor.Owner.Car : null;
            RaycastHit floor;
            Vector3 position;
            if (goingInside)
            {
                if (destination == null || destination.carLivery == null) return;
                if (destination.carLivery.interiorPrefab != null && !destination.IsInteriorLoaded) destination.LoadInterior();
                if (!destinationDoor.Owner.EnsureReady()) return;
                destinationDoor = destinationDoor.Owner.RefreshDoor(destinationDoor);
                if (destinationDoor == null) return;
                Physics.SyncTransforms();
                Vector3 point = destinationDoor.WorldThreshold - destinationDoor.WorldNormal * (capsule.radius + 0.22f);
                if (!destinationDoor.Owner.TryFloor(point, out floor) ||
                    Mathf.Abs(Vector3.Dot(floor.point - destinationDoor.WorldThreshold, destinationDoor.Owner.Frame.up)) > 0.3f)
                {
                    Notify("За этой дверью не найден пол вагона.", "No coach floor was found behind this door.");
                    return;
                }
                // The native capsule center already includes skinWidth. Preserve
                // the measured interior floor position and native teleport target.
                position = floor.point;
                if (!placement.HasClearance(position, capsule, controller.GetTraversableLayers()))
                {
                    Notify("Место за дверью занято или слишком тесное.", "The destination is obstructed or too narrow.");
                    return;
                }
            }
            else
            {
                Physics.SyncTransforms();
                if (!placement.TryExterior(destinationDoor, capsule, controller.GetTraversableLayers(), out floor, out position))
                {
                    Notify("Рядом с этой дверью нет безопасного места для выхода.", "No safe ground was found beside this door.");
                    return;
                }
            }
            if (APlayerTeleport.Instance == null) return;
            PlayerManager.TeleportPlayer(position, player.rotation, goingInside ? destination.interior : floor.transform, false, false);
        }

        private static bool IsPlayerCollider(Collider collider)
        {
            return collider == null || collider.transform == player || collider.transform.IsChildOf(player);
        }

        private static void Notify(string ru, string en)
        {
            noticeRu = ru; noticeEn = en; noticeUntil = Time.unscaledTime + 3f;
        }

        private struct AccessAction
        {
            public CoachDoor Door;
            public CoachDoor Target;
            public bool IsInside;
        }

        private sealed class PromptOverlay : MonoBehaviour
        {
            private GUIStyle style;
            private void OnGUI()
            {
                if (Entry == null || !Entry.Active || Cursor.lockState != CursorLockMode.Locked) return;
                if (action.Door == null && Time.unscaledTime >= noticeUntil) return;
                if (style == null) style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter,
                    fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                string text = Text(noticeRu, noticeEn);
                if (action.Door != null)
                {
                    string verb = action.Target != null ? Text("перейти в соседний вагон", "move to the next coach") :
                        action.IsInside ? Text("выйти из вагона", "exit the coach") : Text("войти в вагон", "enter the coach");
                    text = Text("Нажмите ", "Press ") + Settings.AccessKey.keyCode + Text(", чтобы ", " to ") + verb;
                }
                GUI.Label(new Rect(0f, Screen.height * 0.84f, Screen.width, 34f), text, style);
            }
        }
    }
}
