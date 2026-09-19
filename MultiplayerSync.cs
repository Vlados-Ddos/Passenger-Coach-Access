using System;
using System.IO;
using System.Reflection;

namespace PassengerCoachAccess
{
    // Optional integration facade. The main assembly deliberately contains no MPAPI reference,
    // so it remains loadable when Multiplayer is not installed.
    public static class MultiplayerSync
    {
        private static Action<float> tick;
        private static Action broadcast;
        private static bool bridgeFailed;
        private static bool integrationError;
        private static float nextCheck;
        private static volatile bool apiLoaded;
        private static bool watchingAssemblies;
        private static bool inSession;
        private static bool isClient;
        private static bool ready = true;
        private static bool wrongProtocol;
        private static float hostDistance;

        public static bool InSession { get { return inSession; } }
        public static bool IsClient { get { return isClient; } }
        public static bool Ready { get { return !(inSession && integrationError) && ready && !wrongProtocol; } }
        public static float Distance
        {
            get { return IsClient && ready ? Main.SanitizeDistance(hostDistance) : Main.GetActivationDistanceForMultiplayer(); }
        }

        public static void Initialize()
        {
            if (tick != null || bridgeFailed) return;
            // Do not try to resolve MPAPI before its owner has loaded it. A failed CLR
            // bind can be cached; checking again later also handles either UMM load order.
            if (!watchingAssemblies && !apiLoaded)
            {
                AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                watchingAssemblies = true;
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                    if (loaded.GetName().Name == "MultiplayerAPI") { apiLoaded = true; break; }
            }
            if (!apiLoaded) return;
            if (watchingAssemblies)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                watchingAssemblies = false;
            }
            try
            {
                string directory = Path.GetDirectoryName(typeof(MultiplayerSync).Assembly.Location);
                string path = Path.Combine(directory ?? string.Empty, "PassengerCoachAccess.Multiplayer.dll");
                if (!File.Exists(path)) throw new FileNotFoundException("Optional Multiplayer bridge not found.", path);
                Assembly assembly = Assembly.LoadFrom(path);
                Type bridge = assembly.GetType("PassengerCoachAccess.Multiplayer.MultiplayerBridge", true);
                MethodInfo tickMethod = bridge.GetMethod("Tick", BindingFlags.Public | BindingFlags.Static);
                MethodInfo broadcastMethod = bridge.GetMethod("Broadcast", BindingFlags.Public | BindingFlags.Static);
                MethodInfo initialize = bridge.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                if (initialize == null || tickMethod == null || broadcastMethod == null) throw new MissingMethodException(bridge.FullName);
                Action<float> boundTick = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), tickMethod);
                Action boundBroadcast = (Action)Delegate.CreateDelegate(typeof(Action), broadcastMethod);
                initialize.Invoke(null, null);
                tick = boundTick;
                broadcast = boundBroadcast;
            }
            catch (Exception exception)
            {
                bridgeFailed = true;
                ReportFailure(exception);
            }
        }

        private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            // Loading may happen on another thread. Bind the bridge in Tick, never here.
            if (args.LoadedAssembly.GetName().Name == "MultiplayerAPI") apiLoaded = true;
        }

        public static void Tick(float now)
        {
            if (tick == null && !bridgeFailed && now >= nextCheck)
            {
                nextCheck = now + 1f;
                Initialize();
            }
            if (tick == null) return;
            try { tick(now); integrationError = false; }
            catch (Exception exception) { ReportFailure(exception); }
        }

        public static void Broadcast()
        {
            if (broadcast == null) return;
            try { broadcast(); }
            catch (Exception exception) { ReportFailure(exception); }
        }

        public static void SetState(bool session, bool client, bool isReady, bool incompatible, float distance)
        {
            inSession = session;
            isClient = client;
            ready = isReady;
            wrongProtocol = incompatible;
            hostDistance = Main.SanitizeDistance(distance);
        }

        public static void ReportFailure(Exception exception)
        {
            // Preserve the session/host snapshot on failure; a client must never silently
            // fall back to local gameplay. Tick may recover, and disconnect resets state.
            if (!integrationError) Main.LogOptionalMultiplayerFailure(exception);
            integrationError = true;
        }
    }
}
