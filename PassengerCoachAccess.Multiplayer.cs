using System;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;

[assembly: System.Reflection.AssemblyVersion("1.33.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.33.0.0")]

namespace PassengerCoachAccess
{
    public sealed class CoachAccessSettingsPacket : IPacket
    {
        public int Protocol { get; set; }
        public int Revision { get; set; }
        public float ActivationDistance { get; set; }
    }

    public sealed class CoachAccessSettingsRequest : IPacket
    {
        public int Protocol { get; set; }
    }

}

namespace PassengerCoachAccess.Multiplayer
{
    public static class MultiplayerBridge
    {
        private const int Protocol = 133;
        private static IServer server;
        private static IClient client;
        private static IMultiplayerAPI registeredApi;
        private static bool received;
        private static bool wrongProtocol;
        private static float hostDistance = 2.25f;
        private static float requestAt;
        private static float checkAt;
        private static int revision;
        private static int receivedRevision = -1;
        private static bool initialized;
        private static bool broadcastPending;
        private static float broadcastRetryAt;
        private static float clock;

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            MultiplayerAPI.ServerStarted += ServerStarted;
            MultiplayerAPI.ServerStopped += ServerStopped;
            MultiplayerAPI.ClientStarted += ClientStarted;
            MultiplayerAPI.ClientStopped += ClientStopped;
        }

        // The facade supplies Unity's unscaled clock. All network callbacks remain on
        // Multiplayer's main-thread PollEvents loop; no background Unity calls are used.
        public static void Tick(float now)
        {
            clock = now;
            PushState();
            if (server != MultiplayerAPI.Server) { StopServer(); StartServer(MultiplayerAPI.Server); }
            if (client != MultiplayerAPI.Client) { StopClient(); StartClient(MultiplayerAPI.Client); }
            if (now >= checkAt)
            {
                checkAt = now + 1f;
                IMultiplayerAPI api = MultiplayerAPI.Instance;
                if (api != null && api != registeredApi)
                {
                    api.SetModCompatibility("PassengerCoachAccess", MultiplayerCompatibility.All);
                    registeredApi = api;
                }
                if (api == null) registeredApi = null;
            }
            if (broadcastPending && server != null && now >= broadcastRetryAt) FlushBroadcast();
            if (IsClient() && client != null && !received && !wrongProtocol && client.IsConnected && now >= requestAt)
            {
                requestAt = now + 2f;
                client.SendPacketToServer(new CoachAccessSettingsRequest { Protocol = Protocol }, true);
            }
            PushState();
        }

        public static void Broadcast()
        {
            if (server == null) return;
            revision++;
            broadcastPending = true;
            PushState();
            FlushBroadcast();
        }

        private static void FlushBroadcast()
        {
            // A persistent transport failure must not trigger a send/exception every frame.
            broadcastRetryAt = clock + 2f;
            Send(null);
            broadcastPending = false;
            PushState();
        }

        private static void Guard(Action operation)
        {
            try { operation(); }
            catch (Exception exception) { MultiplayerSync.ReportFailure(exception); }
        }

        private static void ServerStarted(IServer value) { Guard(() => StartServer(value)); }
        private static void ServerStopped() { Guard(StopServer); }
        private static void ClientStarted(IClient value) { Guard(() => StartClient(value)); }
        private static void ClientStopped() { Guard(StopClient); }

        private static bool Session()
        {
            return (MultiplayerAPI.Server != null || MultiplayerAPI.Client != null || server != null || client != null) &&
                (MultiplayerAPI.Instance == null || !MultiplayerAPI.Instance.IsSinglePlayer);
        }

        private static bool IsClient()
        {
            return (MultiplayerAPI.Client != null || client != null) &&
                (MultiplayerAPI.Instance == null ? server == null && MultiplayerAPI.Server == null : !MultiplayerAPI.Instance.IsHost);
        }

        private static void PushState()
        {
            bool isClient = IsClient();
            PassengerCoachAccess.MultiplayerSync.SetState(Session(), isClient,
                isClient ? received : !broadcastPending, isClient && wrongProtocol, hostDistance);
        }

        private static void StartServer(IServer value)
        {
            if (value == null || server == value) return;
            StopServer();
            server = value;
            revision = 0;
            PushState();
            try
            {
                server.OnPlayerConnected += PlayerJoined;
                server.OnPlayerReady += PlayerJoined;
                server.RegisterPacket<CoachAccessSettingsRequest>((packet, player) => Guard(() =>
                {
                    // Reply with our protocol even to a mismatched request so the client
                    // can report incompatible versions instead of waiting forever.
                    if (server == value && packet != null && player != null) Send(player);
                }));
            }
            catch { StopServer(); throw; }
            Broadcast();
            PushState();
        }

        private static void StopServer()
        {
            if (server != null)
            {
                server.OnPlayerConnected -= PlayerJoined;
                server.OnPlayerReady -= PlayerJoined;
            }
            server = null;
            broadcastPending = false;
            PushState();
        }

        private static void StartClient(IClient value)
        {
            if (value == null || client == value) return;
            StopClient();
            client = value;
            requestAt = 0f;
            received = false;
            wrongProtocol = false;
            receivedRevision = -1;
            PushState();
            try
            {
                value.RegisterPacket<CoachAccessSettingsPacket>(packet => Guard(() =>
                {
                    if (client != value || !IsClient() || packet == null) return;
                    // Check ordering before mutating protocol/readiness state.
                    if (packet.Revision < receivedRevision) return;
                    receivedRevision = packet.Revision;
                    bool incompatible = packet.Protocol != Protocol;
                    if (incompatible && !wrongProtocol)
                        Main.LogOptionalMultiplayerFailure(new InvalidOperationException(
                            "Host Passenger Coach Access protocol " + packet.Protocol + " does not match " + Protocol + "."));
                    wrongProtocol = incompatible;
                    if (wrongProtocol) { received = false; PushState(); return; }
                    hostDistance = Main.SanitizeDistance(packet.ActivationDistance);
                    received = true;
                    PushState();
                }));
            }
            catch { StopClient(); throw; }
            PushState();
        }

        private static void StopClient()
        {
            client = null;
            received = false;
            wrongProtocol = false;
            receivedRevision = -1;
            hostDistance = 2.25f;
            PushState();
        }

        private static void PlayerJoined(IPlayer player)
        {
            Guard(() => { if (player != null && !player.IsHost) Send(player); });
        }

        private static void Send(IPlayer player)
        {
            if (server == null) return;
            CoachAccessSettingsPacket packet = new CoachAccessSettingsPacket
            {
                Protocol = Protocol,
                Revision = revision,
                ActivationDistance = Main.GetActivationDistanceForMultiplayer()
            };
            if (player == null) server.SendPacketToAll(packet, true, true, null);
            else server.SendPacketToPlayer(packet, player, true);
        }
    }
}
