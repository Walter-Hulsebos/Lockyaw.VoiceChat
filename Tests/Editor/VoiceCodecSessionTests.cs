using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lockyaw.VoiceChat.Tests {

    internal sealed class VoiceCodecSessionTests {

        [Test]
        public void EncodeAndDecodeUseBundledManagedCodecsWithoutChangingGlobalFactorySettings() {
            Type factoryType = Type.GetType("Concentus.OpusCodecFactory, Concentus", true);
            PropertyInfo nativeLibraryPreference = factoryType.GetProperty("AttemptToUseNativeLibrary");
            FieldInfo nativeLibraryInitialized = factoryType.GetField("_nativeLibInitialized", BindingFlags.Static | BindingFlags.NonPublic);
            object originalPreference = nativeLibraryPreference.GetValue(null);
            object originalInitialization = nativeLibraryInitialized.GetValue(null);
            nativeLibraryPreference.SetValue(null, true);
            try {
                VoiceCodecSettings settings = new(VoiceFrameLength.TwentyMilliseconds, 32000, 8, 10, true, true, false);
                using (VoiceEncoderSession encoder = new(settings)) {
                    using (VoiceDecoderSession decoder = new()) {
                        FieldInfo encoderField = typeof(VoiceEncoderSession).GetField("encoder", BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo decoderField = typeof(VoiceDecoderSession).GetField("decoder", BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.That(encoderField.GetValue(encoder).GetType().FullName, Is.EqualTo("Concentus.Structs.OpusEncoder"));
                        Assert.That(decoderField.GetValue(decoder).GetType().FullName, Is.EqualTo("Concentus.Structs.OpusDecoder"));

                        float[] samples = new float[settings.SamplesPerFrame];
                        for (int i = 0; i < samples.Length; i++) {
                            samples[i] = (float)Math.Sin(i * Math.PI * 2d * 440d / VoiceEncoderSession.SAMPLE_RATE) * .25f;
                        }
                        Assert.That(encoder.TryEncode(samples, 0, 100, out VoiceFrame frame), Is.True);
                        ReadOnlySpan<float> decodedSamples = decoder.Decode(frame, false);
                        Assert.That(decodedSamples.Length, Is.EqualTo(samples.Length));
                        float maximumMagnitude = 0f;
                        for (int i = 0; i < decodedSamples.Length; i++) {
                            Assert.That(float.IsFinite(decodedSamples[i]), Is.True);
                            maximumMagnitude = Math.Max(maximumMagnitude, Math.Abs(decodedSamples[i]));
                        }
                        Assert.That(maximumMagnitude, Is.GreaterThan(.01f));
                        Assert.That(nativeLibraryPreference.GetValue(null), Is.True);
                        Assert.That(nativeLibraryInitialized.GetValue(null), Is.EqualTo(originalInitialization));
                    }
                }
                LogAssert.NoUnexpectedReceived();
            }
            finally {
                nativeLibraryPreference.SetValue(null, originalPreference);
            }
        }

    }

}
