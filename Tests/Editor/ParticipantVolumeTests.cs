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

    internal sealed class ParticipantVolumeTests {

        private sealed class FixedNameResolver : ISpeakerNameResolver {

            public bool TryResolveDisplayName(SpeakerIdentity identity, out string displayName) {
                displayName = "Custom account name";
                return true;
            }

        }

        private NetworkManager networkManager;
        private GameObject localParticipantObject;
        private GameObject remoteParticipantObject;
        private GameObject channelSpeakerObject;

        [UnityTearDown]
        public IEnumerator TearDown() {
            if (networkManager != null) {
                networkManager.Shutdown();
                yield return null;
            }
            if (localParticipantObject != null) {
                Object.Destroy(localParticipantObject);
            }
            if (remoteParticipantObject != null) {
                Object.Destroy(remoteParticipantObject);
            }
            if (channelSpeakerObject != null) {
                Object.Destroy(channelSpeakerObject);
            }
            if (networkManager != null) {
                Object.Destroy(networkManager.gameObject);
            }
            if (Application.isPlaying) {
                yield return null;
                yield return new ExitPlayMode();
            }
            SpeakerIdentity.DefaultResolver = null;
            VoiceChatSettings.SetAudioEnabled(true);
        }

        [Test]
        public void BroadcastVolumeAppliesToQueuedSamplesWithoutScalingOtherMixedAudio() {
            VoiceFrame[] frames = CreateFrames(4, 123);
            using (BroadcastPlaybackStream reference = new(48000, 0, 3, 0)) {
                using (BroadcastPlaybackStream quieter = new(48000, 0, 3, 0)) {
                    for (int i = 0; i < frames.Length; i++) {
                        reference.Receive(frames[i], 0d, 960);
                        quieter.Receive(frames[i], 0d, 960);
                    }
                    quieter.PlaybackVolume = .25f;
                    float[] originalSamples = new float[960];
                    float[] mixedSamples = new float[960];
                    Array.Fill(mixedSamples, .1f);
                    Assert.That(reference.MixInto(originalSamples), Is.EqualTo(960));
                    Assert.That(quieter.MixInto(mixedSamples), Is.EqualTo(960));
                    for (int i = 0; i < mixedSamples.Length; i++) {
                        Assert.That(mixedSamples[i], Is.EqualTo(.1f + originalSamples[i] * .25f).Within(.00001f));
                    }
                    quieter.PlaybackVolume = 0f;
                    Array.Clear(mixedSamples, 0, mixedSamples.Length);
                    Assert.That(quieter.MixInto(mixedSamples), Is.EqualTo(960));
                    Assert.That(mixedSamples, Is.All.Zero);
                }
            }
        }

        [UnityTest]
        public IEnumerator PerUserVolumeUsesClientIdentityAndPreservesCustomNames() {
            yield return new EnterPlayMode();
            VerifyPerUserVolume();
        }

        private void VerifyPerUserVolume() {
            VoiceChatSettings.SetSendingEnabled(false);
            SpeakerIdentity.DefaultResolver = new FixedNameResolver();
            GameObject managerObject = new("Participant volume test network");
            networkManager = managerObject.AddComponent<NetworkManager>();
            UnityTransport transport = managerObject.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 0, "127.0.0.1");
            networkManager.NetworkConfig = new NetworkConfig {
                NetworkTransport = transport,
                EnableSceneManagement = false
            };
            Assert.That(networkManager.StartHost(), Is.True);

            int joinedCount = 0;
            int leftCount = 0;
            VoiceParticipant.OnParticipantJoin.Listen(HandleParticipantJoined);
            VoiceParticipant.OnParticipantLeave.Listen(HandleParticipantLeft);
            localParticipantObject = new("Local volume test participant");
            SpeakerIdentity localIdentity = localParticipantObject.AddComponent<SpeakerIdentity>();
            VoiceParticipant localParticipant = localParticipantObject.GetComponent<VoiceParticipant>();
            localParticipantObject.GetComponent<NetworkObject>().SpawnAsPlayerObject(networkManager.LocalClientId);
            Assert.That(localParticipant.Identity, Is.SameAs(localIdentity));
            Assert.That(localIdentity.DisplayName, Is.EqualTo("Custom account name"));
            Assert.That(localIdentity.SetDisplayName("Shared display name"), Is.True);

            remoteParticipantObject = new("Remote volume test participant");
            SpeakerIdentity remoteIdentity = remoteParticipantObject.AddComponent<SpeakerIdentity>();
            VoiceParticipant remoteParticipant = remoteParticipantObject.GetComponent<VoiceParticipant>();
            NetworkObject remoteNetworkObject = remoteParticipantObject.GetComponent<NetworkObject>();
            remoteNetworkObject.SpawnWithOwnership(123);
            remoteIdentity.Profile.Value = new SpeakerProfile("Shared display name", 1);
            Assert.That(joinedCount, Is.EqualTo(2));
            Assert.That(VoiceParticipant.TryGet(networkManager, 123, out VoiceParticipant foundParticipant), Is.True);
            Assert.That(foundParticipant, Is.SameAs(remoteParticipant));
            Assert.That(VoiceParticipant.TryGet(networkManager, 999, out VoiceParticipant _), Is.False);
            Assert.That(VoiceParticipant.TryGet(null, 123, out VoiceParticipant _), Is.False);

            ParticipantState originalState = remoteParticipant.SyncedState;
            SpeakerOutput localSpeaker = localParticipantObject.GetComponent<SpeakerOutput>();
            SpeakerOutput remoteSpeaker = remoteParticipantObject.GetComponent<SpeakerOutput>();
            int volumeChanges = 0;
            remoteParticipant.OnPlaybackVolume.Listen(HandlePlaybackVolumeChanged);
            remoteParticipant.SetPlaybackVolume(.25f);
            remoteParticipant.SetPlaybackVolume(.25f);
            remoteParticipant.SetPlaybackVolume(float.NaN);
            Assert.That(volumeChanges, Is.EqualTo(1));
            Assert.That(remoteParticipant.PlaybackVolume, Is.EqualTo(.25f));
            Assert.That(remoteSpeaker.AudioSource.volume, Is.EqualTo(.25f));
            Assert.That(localSpeaker.AudioSource.volume, Is.EqualTo(1f));
            Assert.That(localParticipant.PlaybackVolume, Is.EqualTo(1f));
            Assert.That(remoteParticipant.SyncedState, Is.EqualTo(originalState));
            Assert.That(localIdentity.SetDisplayName("Renamed custom user"), Is.True);
            Assert.That(remoteParticipant.PlaybackVolume, Is.EqualTo(.25f));
            Assert.That(remoteIdentity.DisplayName, Is.EqualTo("Shared display name"));

            VoiceChatSettings.SetReceivingEnabled(false);
            VoiceChatSettings.SetReceivingEnabled(true);
            Assert.That(remoteSpeaker.AudioSource.volume, Is.EqualTo(.25f));

            channelSpeakerObject = new("Participant volume test radio");
            ChannelSpeaker channelSpeaker = channelSpeakerObject.AddComponent<ChannelSpeaker>();
            typeof(ChannelSpeaker).GetField("maximumReorderDelayMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(channelSpeaker, 0);
            channelSpeakerObject.GetComponent<NetworkObject>().Spawn();
            VoiceFrame[] frames = CreateFrames(20, 123);
            using (BroadcastPlaybackStream reference = new(16800, 0, 3, 0)) {
                for (int i = 0; i < frames.Length; i++) {
                    reference.Receive(frames[i], 0d, 960);
                    BroadcastRoute userRoute = new(channelSpeaker.Channel, remoteNetworkObject.NetworkObjectId, remoteNetworkObject.NetworkObjectId, 0, 123);
                    BroadcastRoute worldRoute = new(channelSpeaker.Channel, 10000, 10000);
                    ChannelSpeaker.Dispatch(networkManager, new BroadcastFrame(userRoute, frames[i]));
                    ChannelSpeaker.Dispatch(networkManager, new BroadcastFrame(worldRoute, frames[i]));
                }
                AssertRadioMix(channelSpeaker, reference, 1.25f);
                remoteParticipant.SetPlaybackMuted(true);
                AssertRadioMix(channelSpeaker, reference, 1f);
                remoteParticipant.SetPlaybackMuted(false);
                remoteParticipant.SetPlaybackVolume(1f);
                AssertRadioMix(channelSpeaker, reference, 2f);
            }

            remoteParticipant.SetPlaybackVolume(-1f);
            Assert.That(remoteParticipant.PlaybackVolume, Is.Zero);
            remoteParticipant.SetPlaybackVolume(2f);
            Assert.That(remoteParticipant.PlaybackVolume, Is.EqualTo(1f));
            remoteParticipant.SetPlaybackVolume(.25f);
            remoteParticipant.SetPlaybackMuted(true);
            localParticipant.NetworkObject.Despawn(false);
            Assert.That(leftCount, Is.EqualTo(1));
            remoteNetworkObject.ChangeOwnership(networkManager.LocalClientId);
            Assert.That(remoteParticipant.PlaybackVolume, Is.EqualTo(1f));
            Assert.That(remoteParticipant.IsPlaybackMuted, Is.False);
            Assert.That(VoiceParticipant.TryGet(networkManager, 123, out VoiceParticipant _), Is.False);
            Assert.That(VoiceParticipant.TryGet(networkManager, networkManager.LocalClientId, out VoiceParticipant reassignedParticipant), Is.True);
            Assert.That(reassignedParticipant, Is.SameAs(remoteParticipant));
            remoteNetworkObject.Despawn(false);
            Assert.That(leftCount, Is.EqualTo(2));
            Assert.That(VoiceParticipant.TryGet(networkManager, networkManager.LocalClientId, out VoiceParticipant _), Is.False);
            VoiceParticipant.OnParticipantJoin.Unlisten(HandleParticipantJoined);
            VoiceParticipant.OnParticipantLeave.Unlisten(HandleParticipantLeft);
            LogAssert.NoUnexpectedReceived();

            void HandleParticipantJoined(VoiceParticipant participant) {
                joinedCount++;
            }

            void HandleParticipantLeft(VoiceParticipant participant) {
                leftCount++;
            }

            void HandlePlaybackVolumeChanged(float volume) {
                Assert.That(remoteParticipant.PlaybackVolume, Is.EqualTo(volume));
                volumeChanges++;
            }
        }

        private static void AssertRadioMix(ChannelSpeaker speaker, BroadcastPlaybackStream reference, float expectedGain) {
            MethodInfo renderMethod = typeof(ChannelSpeaker).GetMethod("RenderSamples", BindingFlags.Instance | BindingFlags.NonPublic);
            CircularAudioClipRenderer.SampleProvider render = (CircularAudioClipRenderer.SampleProvider)renderMethod.CreateDelegate(typeof(CircularAudioClipRenderer.SampleProvider), speaker);
            float[] expectedSamples = new float[960];
            float[] actualSamples = new float[960];
            Assert.That(reference.MixInto(expectedSamples), Is.EqualTo(960));
            Assert.That(render(actualSamples), Is.EqualTo(960));
            float maximumMagnitude = 0f;
            for (int i = 0; i < actualSamples.Length; i++) {
                Assert.That(actualSamples[i], Is.EqualTo(expectedSamples[i] * expectedGain).Within(.00001f));
                maximumMagnitude = Math.Max(maximumMagnitude, Math.Abs(actualSamples[i]));
            }
            Assert.That(maximumMagnitude, Is.GreaterThan(.01f));
        }

        private static VoiceFrame[] CreateFrames(int count, ulong sourceClientId) {
            VoiceCodecSettings settings = new(VoiceFrameLength.TwentyMilliseconds, 32000, 8, 10, true, true, false);
            using (VoiceEncoderSession encoder = new(settings)) {
                float[] samples = new float[settings.SamplesPerFrame];
                VoiceFrame[] frames = new VoiceFrame[count];
                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++) {
                    for (int i = 0; i < samples.Length; i++) {
                        samples[i] = (float)Math.Sin((frameIndex * samples.Length + i) * Math.PI * 2d * 440d / VoiceEncoderSession.SAMPLE_RATE) * .25f;
                    }
                    Assert.That(encoder.TryEncode(samples, (uint)frameIndex, 100, out frames[frameIndex]), Is.True);
                    frames[frameIndex].SourceClientId = sourceClientId;
                }
                return frames;
            }
        }

    }

}
