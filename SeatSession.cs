using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PassengerCoachAccess
{
    internal static class SeatSession
    {
        internal static CoachSeat Current;
        private static CustomFirstPersonController controller;
        private static CharacterController capsule;
        private static float previousHeight;
        private static bool previousCanBob;
        private static bool cameraBobLocked;
        private static bool ownTeleport;
        private static bool transitioning;
        private const float TransitionDuration = .24f;
        private static readonly Collider[] overlaps = new Collider[32];
        private static readonly RaycastHit[] supports = new RaycastHit[32];
        private static readonly FieldInfo moveDirectionField = AccessTools.Field(typeof(CustomFirstPersonController), "m_MoveDir");
        private static readonly FieldInfo desiredMoveField = AccessTools.Field(typeof(CustomFirstPersonController), "desiredMove");
        private static readonly FieldInfo bobTimeField = AccessTools.Field(typeof(CameraSmoothing), "bobTime");
        private static readonly FieldInfo bobDistanceField = AccessTools.Field(typeof(CameraSmoothing), "bobDistance");
        private static readonly FieldInfo ySmoothVelocityField = AccessTools.Field(typeof(CameraSmoothing), "ySmoothVelo");

        internal static bool IsTransitioning { get { return transitioning; } }

        internal static bool Enter(CoachSeat seat)
        {
            if (Current != null || transitioning || seat == null || seat.Occupied || seat.Owner == null ||
                !seat.Owner.EnsureReady() || APlayerTeleport.Instance == null) return false;
            Transform player = PlayerManager.PlayerTransform;
            if (player == null) return false;
            var fps = player.GetComponent<CustomFirstPersonController>();
            var cc = player.GetComponent<CharacterController>();
            if (fps == null || cc == null || fps.provider == null || fps.provider.IsVR || fps.isRepositioning) return false;
            Vector3 target = seat.Owner.Frame.TransformPoint(seat.Definition.Feet);
            Vector3 forward = Vector3.ProjectOnPlane(seat.Owner.Frame.TransformDirection(seat.Definition.Forward), Vector3.up).normalized;
            if (forward.sqrMagnitude < .1f || (target - player.position).sqrMagnitude > (Main.Distance + .45f) * (Main.Distance + .45f)) return false;
            if (!Main.HasSeatClearance(target, cc, fps.GetTraversableLayers(), seat.Owner)) return false;

            previousHeight = fps.provider.PlayerSittingHeight;
            controller = fps; capsule = cc; Current = seat; seat.Occupied = true; transitioning = true;
            try
            {
                StopNativeMovement(fps);
                ownTeleport = true;
                fps.IsClimbingLadders = false;
                // Keep native reparenting and PlayerManager state, but leave the
                // player at the current pose.  Movement to the seat is animated
                // in local coach space below so the camera never snaps.
                Vector3 start = player.position;
                Quaternion startRotation = player.rotation;
                PlayerManager.TeleportPlayer(start, startRotation, seat.Owner.Frame, false, false);
                var provider = fps.provider as CharacterControllerProvider;
                if (provider == null) throw new InvalidOperationException("Native character provider is unavailable.");
                Transform frame = seat.Owner.Frame;
                Vector3 startLocal = frame.InverseTransformPoint(start);
                Quaternion startLocalRotation = Quaternion.Inverse(frame.rotation) * startRotation;
                Vector3 targetLocal = frame.InverseTransformPoint(target);
                Quaternion targetLocalRotation = Quaternion.Inverse(frame.rotation) * Quaternion.LookRotation(forward, Vector3.up);
                if (player.parent != frame) player.SetParent(frame, true);
                provider.IsSitting = true;
                provider.SetSittingHeight(previousHeight);
                cc.enabled = false;
                PlayerManager.PlayerTeleportStarted += TeleportStarted;
                controller.StartCoroutine(SmoothSit(frame, startLocal, targetLocal,
                    startLocalRotation, targetLocalRotation, previousHeight));
                return true;
            }
            catch
            {
                ForceRelease();
                throw;
            }
            finally { ownTeleport = false; }
        }

        private static IEnumerator SmoothSit(Transform frame, Vector3 start, Vector3 target,
            Quaternion startRotation, Quaternion targetRotation, float startHeight)
        {
            float elapsed = 0f;
            while (elapsed < TransitionDuration && Current != null && transitioning && frame != null && controller != null)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / TransitionDuration);
                t = t * t * (3f - 2f * t);
                Transform player = controller.transform;
                if (player.parent != frame) player.SetParent(frame, true);
                player.localPosition = Vector3.Lerp(start, target, t);
                player.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);
                var native = controller.provider as CharacterControllerProvider;
                if (native != null)
                    native.SetSittingHeight(Mathf.Lerp(startHeight, 1.3f, t));
                yield return null;
            }
            if (Current == null || controller == null || frame == null)
            {
                if (Current != null) ForceRelease();
                yield break;
            }
            Transform finalPlayer = controller.transform;
            if (finalPlayer.parent != frame) finalPlayer.SetParent(frame, true);
            finalPlayer.localPosition = target;
            finalPlayer.localRotation = targetRotation;
            var provider = controller.provider;
            if (provider == null)
            {
                ForceRelease();
                yield break;
            }
            var finalNative = provider as CharacterControllerProvider;
            if (finalNative != null)
            {
                finalNative.SetSittingHeight(1.3f);
                provider.IsSitting = true;
            }
            StopNativeMovement(controller);
            transitioning = false;
            capsule.enabled = true;
        }

        private static void StopNativeMovement(CustomFirstPersonController fps)
        {
            if (fps == null) return;
            SeatMovementSnapshot snapshot = new SeatMovementSnapshot
            {
                MoveDirection = moveDirectionField == null ? Vector3.zero :
                    (Vector3)moveDirectionField.GetValue(fps),
                DesiredMove = desiredMoveField == null ? Vector3.zero :
                    (Vector3)desiredMoveField.GetValue(fps),
                Input = fps.m_Input,
                Jumping = fps.m_Jumping,
                Walking = fps.m_IsWalking
            };
            snapshot.Stop();
            if (moveDirectionField != null) moveDirectionField.SetValue(fps, snapshot.MoveDirection);
            if (desiredMoveField != null) desiredMoveField.SetValue(fps, snapshot.DesiredMove);
            fps.m_Input = snapshot.Input;
            fps.m_Jumping = snapshot.Jumping;
            fps.m_IsWalking = snapshot.Walking;
            CameraSmoothing smoothing = fps.GetComponent<CameraSmoothing>();
            if (smoothing != null)
            {
                if (!cameraBobLocked)
                {
                    previousCanBob = smoothing.canBob;
                    cameraBobLocked = true;
                }
                // CameraSmoothing samples CharacterController.velocity directly,
                // outside CustomFirstPersonController.CharacterMovement. A stale
                // velocity can therefore restart bobbing after the seat patch has
                // already stopped native movement. Keep smoothing active, but let
                // the Sitting state suppress only the movement-bob branch.
                smoothing.canBob = false;
                if (bobTimeField != null) bobTimeField.SetValue(smoothing, 0f);
                if (bobDistanceField != null) bobDistanceField.SetValue(smoothing, 0.2f);
                if (ySmoothVelocityField != null) ySmoothVelocityField.SetValue(smoothing, 0f);
                smoothing.ForceUpdateHeadPosition();
            }
        }

        internal static bool TryStand()
        {
            if (Current == null) return true;
            if (transitioning) return false;
            if (controller == null || capsule == null || !Current.Owner.Alive)
            {
                ForceRelease(); return true;
            }
            Vector3 world;
            if (!StandingPosition(Current, capsule, controller.GetTraversableLayers(), out world))
            {
                Main.Notify("Рядом с местом недостаточно пространства, чтобы встать.", "There is no clear standing space beside this seat.");
                if (controller.provider != null) controller.provider.IsSitting = true;
                return false;
            }
            BeginSmoothStand(world);
            return true;
        }

        private static void BeginSmoothStand(Vector3 world)
        {
            if (controller == null || capsule == null || Current == null) return;
            Transform player = controller.transform;
            Transform frame = Current.Owner.Frame;
            Vector3 start = frame.InverseTransformPoint(player.position);
            Quaternion startRotation = Quaternion.Inverse(frame.rotation) * player.rotation;
            Vector3 target = frame.InverseTransformPoint(world);
            Quaternion targetRotation = startRotation;
            transitioning = true;
            controller.provider.IsSitting = false;
            capsule.enabled = false;
            controller.StartCoroutine(SmoothStand(frame, start, target, startRotation, targetRotation));
        }

        private static IEnumerator SmoothStand(Transform frame, Vector3 start, Vector3 target,
            Quaternion startRotation, Quaternion targetRotation)
        {
            float elapsed = 0f;
            while (elapsed < TransitionDuration && Current != null && frame != null && controller != null)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / TransitionDuration);
                t = t * t * (3f - 2f * t);
                Transform player = controller.transform;
                if (player.parent != frame) player.SetParent(frame, true);
                player.localPosition = Vector3.Lerp(start, target, t);
                player.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);
                yield return null;
            }
            if (controller != null && frame != null)
            {
                Transform player = controller.transform;
                if (player.parent != frame) player.SetParent(frame, true);
                player.localPosition = target;
                player.localRotation = targetRotation;
            }
            FinishStand();
        }

        internal static bool StandingPosition(CoachSeat seat, CharacterController cc, int mask, out Vector3 result)
        {
            result = default(Vector3);
            if (seat == null || seat.Owner == null || seat.Owner.Frame == null) return false;
            PassengerSeatDefinition d = seat.Definition;
            float x = d.Kind == 1 ? -.12f : 0f;
            for (int step = 0; step < 5; step++)
            {
                float z = d.Cushion.z + (step == 0 ? 0f : (step % 2 == 0 ? 1 : -1) * ((step + 1) / 2) * .32f);
                Vector3 origin = seat.Owner.Frame.TransformPoint(new Vector3(x, 2f, z));
                int count = Physics.RaycastNonAlloc(origin, Vector3.down, supports, 1.4f, mask, QueryTriggerInteraction.Ignore);
                try
                {
                    if (count == supports.Length) continue;
                    float nearest = float.MaxValue; RaycastHit hit = default(RaycastHit);
                    for (int i = 0; i < count; i++)
                    {
                        RaycastHit candidate = supports[i];
                        if (candidate.collider == null || candidate.collider == cc || candidate.distance >= nearest) continue;
                        nearest = candidate.distance; hit = candidate;
                    }
                    if (hit.collider == null || (TrainCar.Resolve(hit.transform) != seat.Owner.Car && !hit.transform.IsChildOf(seat.Owner.Frame)) || hit.normal.y < .7f) continue;
                    if (Vector3.Distance(hit.point, seat.Owner.Frame.TransformPoint(new Vector3(x, 1.203f, z))) > .15f) continue;
                    if (NearAnySeat(hit.point, seat.Owner, seat)) continue;
                    if (!ClearStanding(hit.point, cc, mask, seat.Owner)) continue;
                    result = hit.point; return true;
                }
                finally { Array.Clear(supports, 0, count); }
            }
            return false;
        }

        private static bool ClearStanding(Vector3 position, CharacterController cc, int mask, CoachGeometry owner)
        {
            float radius = cc.radius, skin = cc.skinWidth;
            Vector3 a = position + Vector3.up * (radius + skin);
            Vector3 b = position + Vector3.up * (1.62f - radius - skin);
            int count = Physics.OverlapCapsuleNonAlloc(a, b, radius, overlaps, mask, QueryTriggerInteraction.Ignore);
            try
            {
                if (count == overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    Collider value = overlaps[i];
                    if (value == null || value == cc || value.transform.IsChildOf(cc.transform)) continue;
                    if (TrainCar.Resolve(value.transform) == owner.Car) continue;
                    return false;
                }
                return true;
            }
            finally { Array.Clear(overlaps, 0, count); }
        }

        private static bool NearAnySeat(Vector3 position, CoachGeometry owner, CoachSeat selected)
        {
            Vector3 local = owner.Frame.InverseTransformPoint(position);
            for (int i = 0; i < owner.Seats.Count; i++)
            {
                CoachSeat other = owner.Seats[i];
                if (other == null || other == selected) continue;
                Vector3 point = other.Definition.Cushion;
                if (new Vector2(local.x - point.x, local.z - point.z).sqrMagnitude < .36f) return true;
            }
            return false;
        }

        internal static void Tick()
        {
            if (Current == null) return;
            if (transitioning) return;
            if (controller == null || capsule == null || controller.provider == null || !Current.Owner.Alive || UnloadWatcher.isUnloading ||
                PlayerManager.PlayerTransform != controller.transform)
            { ForceRelease(); return; }
            if (controller.transform.parent != Current.Owner.Frame) { ForceRelease(); return; }
            // Ctrl/X must not cancel a seat. Only the configured access key is
            // handled by Main while Current is active. If another native update
            // briefly clears the flag, restore it instead of treating that input
            // as a stand request.
            if (!controller.provider.IsSitting) controller.provider.IsSitting = true;
        }

        private static void TeleportStarted() { if (!ownTeleport) ForceRelease(); }

        internal static void Removing(CoachGeometry coach)
        {
            if (Current == null || Current.Owner != coach) return;
            ForceRelease();
        }

        private static void FinishStand()
        {
            CoachSeat old = Current;
            Current = null; transitioning = false;
            PlayerManager.PlayerTeleportStarted -= TeleportStarted;
            if (controller != null && controller.provider != null)
            {
                controller.provider.IsSitting = false;
                var native = controller.provider as CharacterControllerProvider;
                if (native != null) native.SetSittingHeight(previousHeight);
            }
            if (capsule != null) capsule.enabled = true;
            RestoreCameraBob();
            if (old != null) old.Occupied = false;
            controller = null; capsule = null;
        }

        private static void ForceRelease()
        {
            CoachSeat old = Current; Current = null; transitioning = false;
            PlayerManager.PlayerTeleportStarted -= TeleportStarted;
            if (controller != null && controller.provider != null)
            {
                controller.provider.IsSitting = false;
                var native = controller.provider as CharacterControllerProvider;
                if (native != null) native.SetSittingHeight(previousHeight);
            }
            if (capsule != null) capsule.enabled = true;
            RestoreCameraBob();
            if (old != null) old.Occupied = false;
            controller = null; capsule = null;
        }

        private static void RestoreCameraBob()
        {
            if (!cameraBobLocked) return;
            if (controller != null)
            {
                CameraSmoothing smoothing = controller.GetComponent<CameraSmoothing>();
                if (smoothing != null) smoothing.canBob = previousCanBob;
            }
            cameraBobLocked = false;
        }

        [HarmonyPatch(typeof(CustomFirstPersonController), "CharacterMovement")]
        private static class SeatedMovement
        {
            private static bool Prefix(CustomFirstPersonController __instance)
            { return Current == null || controller != __instance; }
        }

        [HarmonyPatch(typeof(CustomFirstPersonController), "get_IsCrouching")]
        private static class SeatedCrouching
        {
            private static bool Prefix(CustomFirstPersonController __instance, ref bool __result)
            {
                if (Current == null || controller != __instance) return true;
                __result = false; return false;
            }
        }

        [HarmonyPatch(typeof(CharacterControllerProvider), "CheckSitting")]
        private static class NativeSittingInput
        {
            private static bool Prefix(CharacterControllerProvider __instance)
            {
                // The native routine clears IsSitting on the crouch action when
                // crouch-toggle is enabled. Suppress it only for the provider
                // owned by this active seat; outside seating the game is untouched.
                return Current == null || controller == null || controller.provider != __instance;
            }
        }
    }
}
