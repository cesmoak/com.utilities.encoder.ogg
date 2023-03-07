using UnityEngine;
using UnityEngine.Assertions;

namespace Utilities.Encoding.OggVorbis
{
    public static class AudioClipExtensions
    {
        /// <summary>
        /// Encodes the <see cref="AudioClip"/> to OggVorbis
        /// </summary>
        /// <param name="audioClip">The <see cref="AudioClip"/> to encode.</param>
        /// <param name="trim">Optional, trim silence at beginning and end of clip.</param>
        /// <returns><see cref="AudioClip"/> encoded to OggVorbis as byte array.</returns>
        public static byte[] EncodeAudioClipToOggVorbis(this AudioClip audioClip, bool trim = false)
        {
            var samples = new float[audioClip.samples * audioClip.channels];
            var sampleCount = samples.Length;
            audioClip.GetData(samples, 0);

            // trim data
            var start = 0;
            var end = sampleCount;

            if (trim)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    if (samples[i] * Constants.RescaleFactor == 0)
                    {
                        continue;
                    }

                    start = i;
                    break;
                }

                for (var i = sampleCount - 1; i >= 0; i--)
                {
                    if (samples[i] * Constants.RescaleFactor == 0)
                    {
                        continue;
                    }

                    end = i + 1;
                    break;
                }
            }

            var trimmedLength = end - start;
            Assert.IsTrue(trimmedLength > 0);
            var sampleIndex = 0;
            var pcmData = new byte[trimmedLength * sizeof(float)];

            // convert and write data
            for (var i = start; i < end; i++)
            {
                var sample = (short)(samples[i] * Constants.RescaleFactor);
                pcmData[sampleIndex++] = (byte)(sample >> 0);
                pcmData[sampleIndex++] = (byte)(sample >> 8);
            }

            var rawSamples = Encoder.ConvertPcmData(audioClip.frequency, audioClip.channels, pcmData, audioClip.frequency, audioClip.channels);
            var rawOggBytes = Encoder.ConvertToBytes(rawSamples, audioClip.frequency, audioClip.channels);
            return rawOggBytes;
        }
    }
}
