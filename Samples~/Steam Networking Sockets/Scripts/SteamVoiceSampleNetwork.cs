using System;
using Netcode.Transports;
using Steamworks;
using Unity.Netcode;
using UnityEngine;

namespace Lockyaw.VoiceChat.SteamNetworkingSocketsSample {

    [AddComponentMenu("Lockyaw Voice Chat/Steam Networking Sockets Network")]
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager), typeof(SteamNetworkingSocketsTransport))]
    public sealed class SteamVoiceSampleNetwork : MonoBehaviour {

        [SerializeField, HideInInspector] private NetworkManager networkManager;
        [SerializeField, HideInInspector] private SteamNetworkingSocketsTransport transport;

        [Header("Startup")]
        [SerializeField] private StartMode startMode = StartMode.Manual;
        [SerializeField] private ulong serverSteamId;
        [SerializeField] private bool initializeSteamIfNeeded = true;
        [SerializeField] private bool runSteamCallbacks = true;

#if UNITY_SERVER
        [Header("Dedicated Server")]
        [SerializeField] private ushort gamePort = 27015;
        [SerializeField] private ushort queryPort = 27016;
        [SerializeField] private EServerMode serverMode = EServerMode.eServerModeAuthenticationAndSecure;
        [SerializeField] private string productName = "Lockyaw Voice Chat";
        [SerializeField] private string gameDescription = "Lockyaw Voice Chat";
        [SerializeField] private string modDirectory = "LockyawVoiceChat";
        [SerializeField] private string gameVersion = "1.0.0.0";
#endif

        public enum StartMode {
            Manual = 0,
            Host = 1,
            Server = 2,
            Client = 3
        }

        public NetworkManager Manager => networkManager;
        public SteamNetworkingSocketsTransport Transport => transport;

        private const string HOST_ARGUMENT = "-voice-host";
        private const string SERVER_ARGUMENT = "-voice-server";
        private const string CLIENT_ARGUMENT = "-voice-client";
        private const string STEAM_ID_ARGUMENT = "-voice-steam-id";

        private bool isSteamReady;
        private bool ownsSteamInitialization;

        private void Awake() {
            Application.runInBackground = true;
            CacheComponentReferences();
            ConfigureNetworkManager();
        }

        private void Start() {
            if (networkManager == null || networkManager.IsListening) { return; }

            string[] arguments = Environment.GetCommandLineArgs();
            StartMode resolvedStartMode = GetStartMode(arguments);
            if (resolvedStartMode == StartMode.Manual) { return; }

            if (resolvedStartMode == StartMode.Client) {
                StartClient(GetServerSteamId(arguments));
                return;
            }

            StartSession(resolvedStartMode);
        }

        private void OnDestroy() {
            if (!ownsSteamInitialization) { return; }
            if (networkManager != null && networkManager.IsListening) { return; }

#if UNITY_SERVER
            GameServer.Shutdown();
#else
            SteamAPI.Shutdown();
#endif
        }

        private void Update() {
            if (!isSteamReady || !runSteamCallbacks) { return; }

#if UNITY_SERVER
            GameServer.RunCallbacks();
#else
            SteamAPI.RunCallbacks();
#endif
        }

        private void Reset() {
            CacheComponentReferences();
            ConfigureNetworkManager();
        }

        private void OnValidate() {
            CacheComponentReferences();
            ConfigureNetworkManager();
        }

        public bool StartHost() {
            return StartSession(StartMode.Host);
        }

        public bool StartServer() {
            return StartSession(StartMode.Server);
        }

        public bool StartClient() {
            return StartClient(serverSteamId);
        }

        public bool StartClient(ulong targetServerSteamId) {
            if (targetServerSteamId == 0) {
                Debug.LogError("Enter the server Steam ID before starting a client.", this);
                return false;
            }

            serverSteamId = targetServerSteamId;
            CacheComponentReferences();
            if (transport != null) {
                transport.ConnectToSteamID = serverSteamId;
            }

            return StartSession(StartMode.Client);
        }

        private bool StartSession(StartMode requestedStartMode) {
            CacheComponentReferences();
            if (networkManager == null || transport == null) {
                Debug.LogError("A NetworkManager and SteamNetworkingSocketsTransport are required.", this);
                return false;
            }
            if (networkManager.IsListening) {
                Debug.LogError("The network session is already running.", this);
                return false;
            }
#if UNITY_SERVER
            if (requestedStartMode == StartMode.Host) {
                Debug.LogError("Dedicated server builds cannot start as a host.", this);
                return false;
            }
#endif
            if (!EnsureSteamInitialized()) { return false; }

            ConfigureNetworkManager();
            bool started = requestedStartMode switch {
                StartMode.Host => networkManager.StartHost(),
                StartMode.Server => networkManager.StartServer(),
                StartMode.Client => networkManager.StartClient(),
                _ => false
            };

            if (!started) {
                Debug.LogError($"Unable to start the Steam Networking Sockets sample as {requestedStartMode}.", this);
            }

            return started;
        }

        private bool EnsureSteamInitialized() {
            if (transport.IsSupported) {
                isSteamReady = true;
                return true;
            }
            if (!initializeSteamIfNeeded) {
                Debug.LogError("Initialize Steamworks.NET before starting the network session.", this);
                return false;
            }

            try {
                ownsSteamInitialization = InitializeSteam();
            }
            catch (Exception exception) {
                Debug.LogException(exception, this);
                return false;
            }

            isSteamReady = ownsSteamInitialization && transport.IsSupported;
            if (!isSteamReady) {
                Debug.LogError("Steamworks.NET could not initialize. Run through Steam or provide a steam_appid.txt file for development.", this);
            }

            return isSteamReady;
        }

        private bool InitializeSteam() {
#if UNITY_SERVER
            bool initialized = GameServer.Init(0, gamePort, queryPort, serverMode, gameVersion);
            if (!initialized) { return false; }

            SteamGameServer.SetProduct(productName);
            SteamGameServer.SetGameDescription(gameDescription);
            SteamGameServer.SetModDir(modDirectory);
            SteamGameServer.SetDedicatedServer(true);
            SteamGameServer.LogOnAnonymous();
            return true;
#else
            return SteamAPI.Init();
#endif
        }

        private void ConfigureNetworkManager() {
            if (networkManager == null || networkManager.NetworkConfig == null || transport == null) { return; }
            networkManager.NetworkConfig.NetworkTransport = transport;
        }

        private void CacheComponentReferences() {
            if (networkManager == null) {
                TryGetComponent(out networkManager);
            }
            if (transport == null) {
                TryGetComponent(out transport);
            }
        }

        private StartMode GetStartMode(string[] arguments) {
            if (HasArgument(arguments, HOST_ARGUMENT)) { return StartMode.Host; }
            if (HasArgument(arguments, SERVER_ARGUMENT)) { return StartMode.Server; }
            if (HasArgument(arguments, CLIENT_ARGUMENT)) { return StartMode.Client; }
            if (Application.isBatchMode && startMode == StartMode.Host) { return StartMode.Server; }
            return startMode;
        }

        private ulong GetServerSteamId(string[] arguments) {
            string steamIdText = GetArgumentValue(arguments, STEAM_ID_ARGUMENT, serverSteamId.ToString());
            return ulong.TryParse(steamIdText, out ulong parsedSteamId) ? parsedSteamId : serverSteamId;
        }

        private static string GetArgumentValue(string[] arguments, string argumentName, string fallbackValue) {
            for (int i = 0; i < arguments.Length - 1; i++) {
                if (string.Equals(arguments[i], argumentName, StringComparison.OrdinalIgnoreCase)) {
                    return arguments[i + 1];
                }
            }

            return fallbackValue;
        }

        private static bool HasArgument(string[] arguments, string argumentName) {
            for (int i = 0; i < arguments.Length; i++) {
                if (string.Equals(arguments[i], argumentName, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

    }

}
