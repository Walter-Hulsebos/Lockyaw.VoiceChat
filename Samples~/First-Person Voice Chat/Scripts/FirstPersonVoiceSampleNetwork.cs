#if LOCKYAW_VOICECHAT_UNITY_TRANSPORT

using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Lockyaw.VoiceChat.FirstPersonSample {

    [AddComponentMenu("Lockyaw Voice Chat/First-Person Voice Sample Network")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
    public sealed class FirstPersonVoiceSampleNetwork : MonoBehaviour {

        [SerializeField, HideInInspector] private NetworkManager networkManager;
        [SerializeField, HideInInspector] private UnityTransport transport;

        [Header("References")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Startup")]
        [SerializeField] private StartMode startMode = StartMode.Manual;
        [SerializeField] private string serverAddress = "127.0.0.1";
        [SerializeField, Min(1)] private int port = 7777;
        [SerializeField] private string listenAddress = "0.0.0.0";

        public enum StartMode {
            Manual = 0,
            Host = 1,
            Server = 2,
            Client = 3
        }

        private const string HOST_ARGUMENT = "-voice-host";
        private const string SERVER_ARGUMENT = "-voice-server";
        private const string CLIENT_ARGUMENT = "-voice-client";
        private const string ADDRESS_ARGUMENT = "-voice-address";
        private const string PORT_ARGUMENT = "-voice-port";

        private void Awake() {
            Application.runInBackground = true;
            CacheComponentReferences();

            networkManager.NetworkConfig.ConnectionApproval = true;
            networkManager.ConnectionApprovalCallback += HandleConnectionApproval;
        }

        private void Start() {
            if (networkManager.IsListening) { return; }

            string[] arguments = Environment.GetCommandLineArgs();
            StartMode resolvedStartMode = GetStartMode(arguments);
            if (resolvedStartMode == StartMode.Manual) { return; }

            ConfigureTransport(arguments, resolvedStartMode);
            bool started = resolvedStartMode switch {
                StartMode.Host => networkManager.StartHost(),
                StartMode.Server => networkManager.StartServer(),
                StartMode.Client => networkManager.StartClient(),
                _ => false
            };

            if (!started) {
                Debug.LogError($"Unable to start the first-person voice sample as {resolvedStartMode}.", this);
            }
        }

        private void OnDestroy() {
            if (networkManager != null) {
                networkManager.ConnectionApprovalCallback -= HandleConnectionApproval;
            }
        }

        private void Reset() {
            CacheComponentReferences();
        }

        private void OnValidate() {
            CacheComponentReferences();
        }

        private void HandleConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response) {
            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Pending = false;

            if (spawnPoints == null || spawnPoints.Length == 0) { return; }

            int spawnPointIndex = (int)(request.ClientNetworkId % (ulong)spawnPoints.Length);
            Transform spawnPoint = spawnPoints[spawnPointIndex];
            if (spawnPoint == null) { return; }

            response.Position = spawnPoint.position;
            response.Rotation = spawnPoint.rotation;
        }

        private void ConfigureTransport(string[] arguments, StartMode resolvedStartMode) {
            string resolvedAddress = GetArgumentValue(arguments, ADDRESS_ARGUMENT, serverAddress);
            int resolvedPort = GetPort(arguments);
            ushort transportPort = (ushort)Mathf.Clamp(resolvedPort, 1, ushort.MaxValue);

            transport.ConnectionData.Address = resolvedAddress;
            transport.ConnectionData.Port = transportPort;
            transport.ConnectionData.ServerListenAddress = resolvedStartMode == StartMode.Client ? resolvedAddress : listenAddress;
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

        private int GetPort(string[] arguments) {
            string portText = GetArgumentValue(arguments, PORT_ARGUMENT, port.ToString());
            return int.TryParse(portText, out int parsedPort) ? parsedPort : port;
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
#endif
