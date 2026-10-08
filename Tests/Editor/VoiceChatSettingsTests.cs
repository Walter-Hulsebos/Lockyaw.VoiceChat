using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

using Object = UnityEngine.Object;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class VoiceChatSettingsTests {

        private NetworkManager networkManager;
        private GameObject participantObject;
        private GameObject channelSpeakerObject;
        private GameObject areaMicrophoneObject;

        [UnityTearDown]
        public IEnumerator TearDown() {
            if (networkManager != null) {
                networkManager.Shutdown();
                yield return null;
            }
            if (participantObject != null) {
                Object.Destroy(participantObject);
            }
            if (channelSpeakerObject != null) {
                Object.Destroy(channelSpeakerObject);
            }
            if (areaMicrophoneObject != null) {
                Object.Destroy(areaMicrophoneObject);
            }
            if (networkManager != null) {
                Object.Destroy(networkManager.gameObject);
            }
            if (Application.isPlaying) {
                yield return null;
                yield return new ExitPlayMode();
            }
            VoiceChatSettings.SetAudioEnabled(true);
        }

        [Test]
        public void SettingsWorkBeforeJoiningAndNotifyOnlyOnChanges() {
            VoiceChatSettings.SetAudioEnabled(true);
            int sendingChanges = 0;
            int receivingChanges = 0;
            VoiceChatSettings.OnSendingEnabled.Listen(HandleSendingEnabledChanged);
            VoiceChatSettings.OnReceivingEnabled.Listen(HandleReceivingEnabledChanged);
            try {
                VoiceChatSettings.SetSendingEnabled(false);
                VoiceChatSettings.SetSendingEnabled(false);
                Assert.That(VoiceChatSettings.IsReceivingEnabled, Is.True);
                Assert.That(VoiceChatSettings.IsSendingEnabled, Is.False);
                Assert.That(sendingChanges, Is.EqualTo(1));
                Assert.That(receivingChanges, Is.Zero);

                VoiceChatSettings.SetAudioEnabled(false);
                VoiceChatSettings.SetAudioEnabled(false);
                Assert.That(VoiceChatSettings.IsReceivingEnabled, Is.False);
                Assert.That(VoiceChatSettings.IsSendingEnabled, Is.False);
                Assert.That(sendingChanges, Is.EqualTo(1));
                Assert.That(receivingChanges, Is.EqualTo(1));

                VoiceChatSettings.SetReceivingEnabled(true);
                Assert.That(VoiceChatSettings.IsSendingEnabled, Is.False);
                Assert.That(receivingChanges, Is.EqualTo(2));
            }
            finally {
                VoiceChatSettings.OnSendingEnabled.Unlisten(HandleSendingEnabledChanged);
                VoiceChatSettings.OnReceivingEnabled.Unlisten(HandleReceivingEnabledChanged);
            }

            void HandleSendingEnabledChanged(bool isEnabled) {
                Assert.That(VoiceChatSettings.IsSendingEnabled, Is.EqualTo(isEnabled));
                sendingChanges++;
            }

            void HandleReceivingEnabledChanged(bool isEnabled) {
                Assert.That(VoiceChatSettings.IsReceivingEnabled, Is.EqualTo(isEnabled));
                receivingChanges++;
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void NetworkPreferencesPreserveMuteAndDeafen(bool sendingEnabled, bool receivingEnabled) {
            ParticipantState initialState = default;
            Assert.That(initialState.IsSendingEnabled, Is.True);
            Assert.That(initialState.IsReceivingEnabled, Is.True);

            ParticipantState state = initialState.WithInputMuted(true).WithDeafened(true)
                .WithSendingEnabled(sendingEnabled).WithReceivingEnabled(receivingEnabled);
            Assert.That(state.IsSendingEnabled, Is.EqualTo(sendingEnabled));
            Assert.That(state.IsReceivingEnabled, Is.EqualTo(receivingEnabled));
            Assert.That(state.IsInputMuted, Is.True);
            Assert.That(state.IsDeafened, Is.True);
            Assert.That(state.WithSendingEnabled(sendingEnabled).WithReceivingEnabled(receivingEnabled), Is.EqualTo(state));
        }

        [UnityTest]
        public IEnumerator AudioSwitchesApplyBeforeSpawnAndReleaseActiveResources() {
            yield return new EnterPlayMode();
            VoiceChatSettings.SetAudioEnabled(false);
            GameObject managerObject = new("Voice settings test network");
            networkManager = managerObject.AddComponent<NetworkManager>();
            UnityTransport transport = managerObject.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 0, "127.0.0.1");
            networkManager.NetworkConfig = new NetworkConfig {
                NetworkTransport = transport,
                EnableSceneManagement = false
            };
            Assert.That(networkManager.StartHost(), Is.True);

            participantObject = new("Voice settings test participant");
            VoiceParticipant participant = participantObject.AddComponent<VoiceParticipant>();
            MicrophoneInput microphone = participantObject.GetComponent<MicrophoneInput>();
            SpeakerOutput speaker = participantObject.GetComponent<SpeakerOutput>();
            microphone.SetDevice($"Unavailable test microphone {Guid.NewGuid()}");
            participantObject.GetComponent<NetworkObject>().SpawnAsPlayerObject(networkManager.LocalClientId);
            participant.SetLocalLoopback(true);

            channelSpeakerObject = new("Voice settings test radio");
            ChannelSpeaker channelSpeaker = channelSpeakerObject.AddComponent<ChannelSpeaker>();
            channelSpeakerObject.GetComponent<NetworkObject>().Spawn();
            Assert.That(participant.IsSendingEnabled, Is.False);
            Assert.That(participant.IsReceivingEnabled, Is.False);
            Assert.That(microphone.IsCapturing, Is.False);
            Assert.That(speaker.AudioSource == null || speaker.AudioSource.clip == null, Is.True);
            Assert.That(channelSpeaker.AudioSource == null || channelSpeaker.AudioSource.clip == null, Is.True);

            VoiceChatSettings.SetReceivingEnabled(true);
            Assert.That(participant.IsReceivingEnabled, Is.True);
            Assert.That(participant.IsSendingEnabled, Is.False);
            Assert.That(speaker.AudioSource.clip != null, Is.True);
            Assert.That(channelSpeaker.AudioSource.clip != null, Is.True);
            VoiceFrame frame = CreateFrame();
            participant.SendFrame(frame);
            Assert.That(speaker.GetDiagnostics().ReceivedFrames, Is.Zero);

            VoiceChatSettings.SetSendingEnabled(true);
            participant.SendFrame(frame);
            Assert.That(speaker.GetDiagnostics().ReceivedFrames, Is.EqualTo(1));
            Assert.That(speaker.GetDiagnostics().PendingEncodedFrames, Is.EqualTo(1));
            BroadcastFrame broadcastFrame = new(new(channelSpeaker.Channel, participant.NetworkObjectId, participant.NetworkObjectId), frame);
            ChannelSpeaker.Dispatch(networkManager, broadcastFrame);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.That(speaker.BufferedSamples, Is.GreaterThan(0));
            Assert.That(channelSpeaker.ActiveStreams, Is.EqualTo(1));

            VoiceChatSettings.SetReceivingEnabled(false);
            Assert.That(participant.IsSendingEnabled, Is.True);
            Assert.That(participant.IsReceivingEnabled, Is.False);
            Assert.That(speaker.AudioSource.clip == null, Is.True);
            Assert.That(channelSpeaker.AudioSource.clip == null, Is.True);
            Assert.That(speaker.BufferedSamples, Is.Zero);
            Assert.That(channelSpeaker.ActiveStreams, Is.Zero);
            speaker.ResetDiagnostics();
            speaker.ReceiveFrame(frame);
            ChannelSpeaker.Dispatch(networkManager, broadcastFrame);
            Assert.That(speaker.GetDiagnostics().ReceivedFrames, Is.Zero);
            Assert.That(channelSpeaker.ActiveStreams, Is.Zero);

            channelSpeaker.enabled = false;
            channelSpeaker.enabled = true;
            Assert.That(channelSpeaker.AudioSource.clip == null, Is.True);
            VoiceChatSettings.SetReceivingEnabled(true);
            Assert.That(speaker.AudioSource.clip != null, Is.True);
            Assert.That(speaker.BufferedSamples, Is.Zero);
            Assert.That(channelSpeaker.AudioSource.clip != null, Is.True);
            Assert.That(channelSpeaker.ActiveStreams, Is.Zero);

            speaker.OnFrameLevel.Listen(HandleFrameLevelChanged);
            speaker.ReceiveFrame(frame);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.That(VoiceChatSettings.IsReceivingEnabled, Is.False);
            Assert.That(speaker.AudioSource.clip == null, Is.True);
            speaker.OnFrameLevel.Unlisten(HandleFrameLevelChanged);
            VoiceChatSettings.SetReceivingEnabled(true);

            areaMicrophoneObject = new("Voice settings test area microphone");
            areaMicrophoneObject.transform.position = new Vector3(1000f, 0f, 0f);
            AreaMicrophone areaMicrophone = areaMicrophoneObject.AddComponent<AreaMicrophone>();
            areaMicrophoneObject.GetComponent<NetworkObject>().Spawn();
            yield return null;
            Assert.That(GetPrivateField(areaMicrophone, "encoder"), Is.Not.Null);

            FakeMicrophoneApi microphoneApi = new() {
                Devices = new[] { "Simulated microphone" },
                CaptureClip = AudioClip.Create("Voice settings microphone", 48000, 1, 48000, false)
            };
            MicrophoneReader reader = new("Simulated microphone", 960, microphoneApi);
            Assert.That(reader.Start(), Is.True);
            SetPrivateField(microphone, "microphoneReader", reader);
            SetPrivateField(microphone, "isCaptureActive", true);
            VoiceChatSettings.SetSendingEnabled(false);
            Assert.That(microphoneApi.EndCount, Is.EqualTo(1));
            Assert.That(microphone.IsCapturing, Is.False);
            Assert.That(GetPrivateField(areaMicrophone, "encoder"), Is.Null);
            Assert.That(GetPrivateField(microphone, "isCaptureRequested"), Is.True);
            Assert.That(participant.IsReceivingEnabled, Is.True);
            Assert.That(participant.IsSpeaking, Is.False);

            microphone.StopCapture();
            VoiceChatSettings.SetSendingEnabled(true);
            Assert.That(GetPrivateField(microphone, "isCaptureRequested"), Is.False);
            participant.SetInputMuted(true);
            participant.SetDeafened(true);
            VoiceChatSettings.SetAudioEnabled(false);
            VoiceChatSettings.SetAudioEnabled(true);
            Assert.That(participant.IsInputMuted, Is.True);
            Assert.That(participant.IsDeafened, Is.True);
            LogAssert.NoUnexpectedReceived();

            void HandleFrameLevelChanged(float level) {
                VoiceChatSettings.SetReceivingEnabled(false);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value) {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static VoiceFrame CreateFrame() {
            VoiceCodecSettings settings = new(VoiceFrameLength.TwentyMilliseconds, 32000, 8, 10, true, true, false);
            using (VoiceEncoderSession encoder = new(settings)) {
                float[] samples = new float[settings.SamplesPerFrame];
                for (int i = 0; i < samples.Length; i++) {
                    samples[i] = (float)Math.Sin(i * Math.PI * 2d * 440d / VoiceEncoderSession.SAMPLE_RATE) * .25f;
                }
                Assert.That(encoder.TryEncode(samples, 0, 100, out VoiceFrame frame), Is.True);
                return frame;
            }
        }

        private static object GetPrivateField(object target, string fieldName) {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(target);
        }

    }

}
