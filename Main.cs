using System;
using System.Reflection;
using HarmonyLib;
using I2.Loc;
using UnityEngine;
using UnityModManagerNet;

[assembly: AssemblyVersion("1.35.0.0")]
[assembly: AssemblyFileVersion("1.35.0.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PassengerCoachAccess.SeatAudit")]

namespace PassengerCoachAccess
{
    public sealed class CoachAccessSettings : UnityModManager.ModSettings
    {
        public KeyBinding AccessKey = new KeyBinding();
        public float ActivationDistance = 2.25f;
        public bool ShowInteractionPrompts = true;

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
        private static float nextVisibility;
        private static CoachDoor visibleDoor;
        private static bool visible;
        private static CoachDoor executableDoor;
        private static bool executable;
        private static float nextExecutableCheck;
        private static string noticeRu;
        private static string noticeEn;
        private static float noticeUntil;
        private static readonly RaycastHit[] sightHits = new RaycastHit[32];
        private static readonly AccessPlacement placement = new AccessPlacement();
        private static PropertyInfo keyBindingKeyControl;
        private static PropertyInfo keyControlWasPressedThisFrame;
        private static Type keyControlRuntimeType;
        private static float nextInputWarning;

        internal static float Distance { get { return MultiplayerSync.Distance; } }
        internal static bool HasPlayerClearance(Vector3 position, CharacterController value, int mask)
        {
            return placement.HasClearance(position, value, mask);
        }
        internal static bool HasSeatClearance(Vector3 position, CharacterController value, int mask, CoachGeometry owner)
        {
            return owner != null && placement.HasSeatClearance(position, value, mask, owner.Car);
        }
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
            keyBindingKeyControl = typeof(KeyBinding).GetProperty("KeyControl",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // Validate the measured seat profile off the Unity main thread. The
            // profile hashes the 190 MB resources.assets file; doing that lazily
            // from the first interaction caused a multi-second hitch.
            SeatData.BeginLoad(entry.Path);
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
            entry.Logger.Log("Passenger Coach Access 1.35 loaded; stable door access with passenger seats.");
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
        public static bool GetInteractionPromptsForMultiplayer()
        {
            return Settings == null || Settings.ShowInteractionPrompts;
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
            executableDoor = null;
            if (!active && !SeatSession.TryStand()) return false;
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
            bool promptGuiEnabled = GUI.enabled;
            GUI.enabled = promptGuiEnabled && !MultiplayerSync.IsClient;
            Settings.ShowInteractionPrompts = GUILayout.Toggle(Settings.ShowInteractionPrompts,
                Text("Показывать подсказки взаимодействия", "Show interaction prompts"));
            GUI.enabled = promptGuiEnabled;
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
            if (!entry.Active)
            {
                action = default(AccessAction);
                visibleDoor = null;
                executableDoor = null;
                return;
            }
            SeatSession.Tick();
            MultiplayerSync.Tick(Time.unscaledTime);
            Registry.Tick();
            // Once the background profile completes, publish at most one coach's
            // managed seat cache per frame. This keeps a large consist from
            // turning completion of the shared profile into a second frame spike.
            if (SeatData.IsReady) Registry.RefreshSeats();
            if (!WorldStreamingInit.IsLoaded || LoadingScreenManager.IsLoading ||
                UnloadWatcher.isUnloading || Time.timeScale == 0f ||
                Cursor.lockState != CursorLockMode.Locked || !RefreshPlayer())
            {
                action = default(AccessAction);
                visibleDoor = null;
                executableDoor = null;
                return;
            }
            // KeyBinding.Down() is already an edge query (Input System
            // wasPressedThisFrame with the legacy GetKeyDown fallback).  A second
            // time gate here used to discard a legitimate first press during the
            // short interval after entering or traversing a coach.
            bool pressed = AccessKeyDown();
            if (SeatSession.Current != null)
            {
                action = default(AccessAction);
                visibleDoor = null;
                executableDoor = null;
                if (pressed)
                {
                    SeatSession.TryStand();
                }
                return;
            }
            // A client may lose transport readiness while already seated. Keep
            // the local stand action available so a network fault cannot trap
            // the player in the seat; new coach/seat interactions remain gated
            // below until the host state is ready again.
            if (!MultiplayerSync.Ready)
            {
                action = default(AccessAction);
                visibleDoor = null;
                return;
            }
            Registry.RefreshNearby(player.position,
                ShouldForceNearbyRefresh(pressed, action.Door != null || action.Seat != null));
            action = SelectAction(pressed);
            if (!pressed || (action.Door == null && action.Seat == null)) return;
            try
            {
                if (action.Seat != null)
                {
                    if (!SeatSession.Enter(action.Seat)) Notify("Не удалось занять это место.", "This seat is unavailable.");
                }
                else Execute(action);
            }
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
                PlayerManager.ActiveCamera == PlayerManager.PlayerCamera && APlayerTeleport.Instance != null;
        }

        private static bool AccessKeyDown()
        {
            KeyBinding binding = Settings.AccessKey;
            if (binding == null) return false;
            try
            {
                if (binding.Down()) return true;
            }
            catch (Exception error)
            {
                LogInputWarning("key edge fallback", error);
            }

            // KeyBinding.Down intentionally requires an exact modifier set. That
            // makes a plain T binding disappear while Shift is held for sprint,
            // even though the key itself was pressed this frame. Preserve any
            // modifiers explicitly configured for the binding, while allowing
            // movement modifiers that are unrelated to this action.
            byte modifiers = binding.modifiers;
            if (!ModifiersSatisfied(modifiers, KeyBinding.Ctrl(), KeyBinding.Shift(), KeyBinding.Alt())) return false;

            // In legacy-input mode the raw edge is sufficient and avoids a
            // reflection call on every movement frame. The Input System path is
            // handled below through the key control's wasPressedThisFrame value.
            if (!KeyBinding.LegacyInputDisabled)
                return LegacyKeyDown(binding.keyCode);

            object control;
            try { control = keyBindingKeyControl == null ? null : keyBindingKeyControl.GetValue(binding, null); }
            catch (Exception error) { control = null; LogInputWarning("Input System key lookup", error); }
            if (control != null)
            {
                try
                {
                    if (keyControlWasPressedThisFrame == null || keyControlRuntimeType != control.GetType())
                    {
                        keyControlWasPressedThisFrame = control.GetType().GetProperty("wasPressedThisFrame");
                        keyControlRuntimeType = control.GetType();
                    }
                    if (keyControlWasPressedThisFrame != null &&
                        (bool)keyControlWasPressedThisFrame.GetValue(control, null)) return true;
                }
                catch (Exception error) { LogInputWarning("Input System key edge", error); }
            }
            return LegacyKeyDown(binding.keyCode);
        }

        private static bool LegacyKeyDown(KeyCode key)
        {
            try { return key != KeyCode.None && Input.GetKeyDown(key); }
            catch (Exception error)
            {
                LogInputWarning("legacy key edge", error);
                return false;
            }
        }

        private static void LogInputWarning(string stage, Exception error)
        {
            if (Entry == null || Entry.Logger == null || Time.unscaledTime < nextInputWarning) return;
            nextInputWarning = Time.unscaledTime + 5f;
            Entry.Logger.Warning("Passenger Coach Access " + stage + ": " + error.GetBaseException().Message);
        }

        private static bool ModifiersSatisfied(byte configured, bool ctrl, bool shift, bool alt)
        {
            // A configured modifier is mandatory. Unconfigured modifiers remain
            // allowed so sprinting with Shift does not suppress a plain keybind.
            return ((configured & 1) == 0 || ctrl) &&
                ((configured & 2) == 0 || shift) &&
                ((configured & 4) == 0 || alt);
        }

        private static bool ShouldForceNearbyRefresh(bool pressed, bool hasVisibleAction)
        {
            // A prompt already represents a live cached context. Replacing the
            // nearby list in the same key edge can transiently lose a detached
            // interior collider at a doorway, leaving a visible prompt with no
            // action. Re-scan when discovering a new action; preserve the context
            // that produced the current prompt until the action is validated.
            return pressed && !hasVisibleAction;
        }

        private static AccessAction SelectAction(bool forceVisibility)
        {
            Vector3 body = player.TransformPoint(capsule.center);
            CoachGeometry occupied = Registry.FindContaining(body);
            Ray ray = PlayerManager.PlayerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            AccessAction best = default(AccessAction);
            float distance = float.MaxValue;
            TrainCar attachedCar = PlayerManager.Car;
            for (int i = 0; i < Registry.Nearby.Count; i++)
            {
                CoachGeometry coach = Registry.Nearby[i];
                bool attachedToCoach = attachedCar != null && attachedCar == coach.Car;
                if (!coach.EnsureReady() || (occupied != null && occupied != coach && !attachedToCoach)) continue;
                for (int j = 0; j < coach.Doors.Count; j++)
                {
                    CoachDoor door = coach.Doors[j];
                    // The native reparent target is only a discovery hint while
                    // the controller crosses a doorway.  The action direction is
                    // always derived from the body position relative to the
                    // selected doorway, preventing a stale parent from turning
                    // an outside player into an Exit action.
                    // The native interior region is the primary inside test.
                    // During a doorway crossing it can be absent for one frame;
                    // only then may the attached coach and the selected door
                    // plane extend the state across that bounded boundary.
                    bool inside = occupied == coach ||
                        (attachedToCoach && door.IsBodyInside(body));
                    bool movingAcrossBoundary = attachedToCoach && occupied != coach;
                    if (!inside && door.Kind == CoachDoorKind.End) continue;
                    float hitDistance;
                    bool aimed = movingAcrossBoundary ? door.IsAimedWhileMoving(ray, body, Distance, inside, out hitDistance) :
                        door.IsAimed(ray, body, Distance, inside, out hitDistance);
                    if (!aimed ||
                        hitDistance >= distance) continue;
                    CoachDoor target = null;
                    if (door.Kind == CoachDoorKind.End && !Registry.TryGetConnectedDoor(door, out target)) continue;
                    best = new AccessAction { Door = door, Target = target, IsInside = inside };
                    distance = hitDistance;
                }
            }
            if (best.Door == null && occupied != null)
            {
                CoachSeat seat;
                float seatDistance;
                // Seat anchors are measured data rather than Unity colliders, so
                // the same first-obstacle test used by doors must run before a
                // seat or toilet prompt can be shown.
                if (occupied.FindSeat(ray, body, Distance, out seat, out seatDistance) &&
                    HasLineOfSight(ray, seatDistance) &&
                    HasSeatClearance(seat.Owner.Frame.TransformPoint(seat.Definition.Feet), capsule,
                        controller.GetTraversableLayers(), seat.Owner))
                    best = new AccessAction { Seat = seat, IsInside = true };
            }
            if (best.Door == null)
            {
                visibleDoor = null;
                return best;
            }
            if (forceVisibility || visibleDoor != best.Door || Time.unscaledTime >= nextVisibility)
            {
                visibleDoor = best.Door;
                nextVisibility = Time.unscaledTime + 0.1f;
                visible = HasLineOfSight(ray, distance);
            }
            if (forceVisibility || executableDoor != best.Door || Time.unscaledTime >= nextExecutableCheck)
            {
                executableDoor = best.Door;
                nextExecutableCheck = Time.unscaledTime + 0.1f;
                executable = CanExecute(best);
            }
            return visible && executable ? best : default(AccessAction);
        }

        private static bool CanExecute(AccessAction selected)
        {
            if (APlayerTeleport.Instance == null || selected.Door == null ||
                selected.Door.Owner == null || !selected.Door.Owner.Alive) return false;
            CoachDoor destinationDoor = selected.Target ?? selected.Door;
            if (selected.Target != null)
            {
                CoachDoor currentTarget;
                if (!Registry.TryGetConnectedDoor(selected.Door, out currentTarget) || currentTarget != selected.Target)
                    return false;
            }
            bool goingInside = !selected.IsInside || selected.Target != null;
            if (goingInside)
            {
                TrainCar destination = destinationDoor.Owner.Car;
                if (destination == null || destination.carLivery == null || !destinationDoor.Owner.EnsureReady()) return false;
                destinationDoor = destinationDoor.Owner.RefreshDoor(destinationDoor);
                if (destinationDoor == null) return false;
                Physics.SyncTransforms();
                RaycastHit floor;
                Vector3 point = destinationDoor.WorldThreshold - destinationDoor.WorldNormal * (capsule.radius + 0.22f);
                return destinationDoor.Owner.TryFloor(point, out floor) &&
                    Mathf.Abs(Vector3.Dot(floor.point - destinationDoor.WorldThreshold, destinationDoor.Owner.Frame.up)) <= 0.3f &&
                    HasPlayerClearance(floor.point, capsule, controller.GetTraversableLayers());
            }
            RaycastHit support;
            Vector3 position;
            Physics.SyncTransforms();
            return placement.TryExterior(destinationDoor, capsule, controller.GetTraversableLayers(), out support, out position);
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
            if (selected.Door == null || selected.Door.Owner == null || !selected.Door.Owner.Alive)
            {
                Notify("Дверь больше недоступна.", "The selected door is no longer available.");
                return;
            }
            CoachDoor destinationDoor = selected.Target ?? selected.Door;
            if (selected.Target != null)
            {
                CoachDoor currentTarget;
                if (!Registry.TryGetConnectedDoor(selected.Door, out currentTarget) || currentTarget != selected.Target)
                {
                    Notify("Сцепка или соседний вагон изменились.", "The coupling or neighboring coach changed.");
                    return;
                }
            }
            bool goingInside = !selected.IsInside || selected.Target != null;
            TrainCar destination = goingInside ? destinationDoor.Owner.Car : null;
            RaycastHit floor;
            Vector3 position;
            if (goingInside)
            {
                if (destination == null || destination.carLivery == null)
                {
                    Notify("Вагон больше недоступен.", "The destination coach is no longer available.");
                    return;
                }
                if (destination.carLivery.interiorPrefab != null && !destination.IsInteriorLoaded) destination.LoadInterior();
                if (!destinationDoor.Owner.EnsureReady())
                {
                    Notify("Интерьер вагона ещё не готов.", "The coach interior is not ready yet.");
                    return;
                }
                destinationDoor = destinationDoor.Owner.RefreshDoor(destinationDoor);
                if (destinationDoor == null)
                {
                    Notify("Цель двери изменилась.", "The selected door changed; aim again.");
                    return;
                }
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
            if (APlayerTeleport.Instance == null)
            {
                Notify("Штатная система перемещения ещё не готова.", "The native teleport system is not ready yet.");
                return;
            }
            PlayerManager.TeleportPlayer(position, player.rotation, goingInside ? destination.interior : floor.transform, false, false);
        }

        private static bool IsPlayerCollider(Collider collider)
        {
            return collider == null || collider.transform == player || collider.transform.IsChildOf(player);
        }

        internal static void Notify(string ru, string en)
        {
            noticeRu = ru; noticeEn = en; noticeUntil = Time.unscaledTime + 3f;
        }

        private struct AccessAction
        {
            public CoachDoor Door;
            public CoachDoor Target;
            public CoachSeat Seat;
            public bool IsInside;
        }

        private sealed class PromptOverlay : MonoBehaviour
        {
            private GUIStyle style;
            private void OnGUI()
            {
                if (Entry == null || !Entry.Active || Cursor.lockState != CursorLockMode.Locked) return;
                bool notice = Time.unscaledTime < noticeUntil;
                bool interaction = action.Door != null || action.Seat != null ||
                    (SeatSession.Current != null && !SeatSession.IsTransitioning);
                if (!notice && (!interaction || !MultiplayerSync.ShowInteractionPrompts)) return;
                if (style == null) style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter,
                    fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                string text = notice ? Text(noticeRu, noticeEn) : string.Empty;
                if (!notice && MultiplayerSync.ShowInteractionPrompts)
                {
                    string verb;
                    if (SeatSession.Current != null && !SeatSession.IsTransitioning) verb = Text("встать", "stand up");
                    else if (action.Seat != null) verb = action.Seat.IsToilet ? Text("сесть в туалете", "sit on the toilet") : Text("сесть", "sit down");
                    else verb = action.Target != null ? Text("перейти в соседний вагон", "move to the next coach") :
                        action.IsInside ? Text("выйти из вагона", "exit the coach") : Text("войти в вагон", "enter the coach");
                    text = Text("Нажмите ", "Press ") + Settings.AccessKey.keyCode + Text(", чтобы ", " to ") + verb;
                }
                GUI.Label(new Rect(0f, Screen.height * 0.84f, Screen.width, 34f), text, style);
            }
        }
    }
}
