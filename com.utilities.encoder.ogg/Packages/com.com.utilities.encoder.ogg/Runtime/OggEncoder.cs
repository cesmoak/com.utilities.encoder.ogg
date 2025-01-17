// Licensed under the MIT License. See LICENSE in the project root for license information.

using OggVorbisEncoder;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;
using Utilities.Audio;
using Random = System.Random;
using Microphone = Utilities.Audio.Microphone;

namespace Utilities.Encoding.OggVorbis
{
    public class OggEncoder : IEncoder
    {
        public static float[][] ConvertPcmData(int outputSampleRate, int outputChannels, byte[] pcmSamples, int pcmSampleRate, int pcmChannels)
        {
            var numPcmSamples = pcmSamples.Length / sizeof(short) / pcmChannels;
            var pcmDuration = numPcmSamples / (float)pcmSampleRate;
            var numOutputSamples = (int)(pcmDuration * outputSampleRate) / pcmChannels;
            var outSamples = new float[outputChannels][];

            for (var ch = 0; ch < outputChannels; ch++)
            {
                outSamples[ch] = new float[numOutputSamples];
            }

            for (var i = 0; i < numOutputSamples; i++)
            {
                for (var channel = 0; channel < outputChannels; channel++)
                {
                    var sampleIndex = i * pcmChannels * sizeof(short);

                    if (channel < pcmChannels)
                    {
                        sampleIndex += channel * sizeof(short);
                    }

                    var rawSample = (short)(pcmSamples[sampleIndex + 1] << 8 | pcmSamples[sampleIndex]) / (float)short.MaxValue;

                    outSamples[channel][i] = rawSample;
                }
            }

            return outSamples;
        }

        public static byte[] ConvertToBytes(float[][] samples, int sampleRate, int channels, float quality = 1f)
        {
            using MemoryStream outputData = new MemoryStream();

            // Stores all the static vorbis bit stream settings
            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);

            // set up our packet->stream encoder
            var serial = new Random().Next();
            var oggStream = new OggStream(serial);

            // =========================================================
            // HEADER
            // =========================================================
            // Vorbis streams begin with three headers; the initial header
            // (with most of the codec setup parameters) which is mandated
            // by the Ogg bitstream spec.  The second header holds any
            // comment fields.  The third header holds the bitstream codebook.
            var comments = new Comments();

            var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
            var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
            var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

            oggStream.PacketIn(infoPacket);
            oggStream.PacketIn(commentsPacket);
            oggStream.PacketIn(booksPacket);

            // Flush to force audio data onto its own page per the spec
            oggStream.FlushPages(outputData, true);

            // =========================================================
            // BODY (Audio Data)
            // =========================================================
            var processingState = ProcessingState.Create(info);
            var sampleLength = samples[0].Length;
            var writeBufferSize = 1024;

            for (var readIndex = 0; readIndex <= sampleLength;)
            {
                if (readIndex == sampleLength)
                {
                    processingState.WriteEndOfStream();
                    break;
                }

                processingState.WriteData(samples, writeBufferSize, readIndex);

                while (processingState.PacketOut(out var packet))
                {
                    oggStream.PacketIn(packet);
                    oggStream.FlushPages(outputData, false);
                }

                var nextIndex = readIndex + writeBufferSize;

                if (nextIndex >= sampleLength - writeBufferSize)
                {
                    writeBufferSize = (sampleLength - readIndex) - writeBufferSize;
                    readIndex = sampleLength;
                }
                else
                {
                    readIndex = nextIndex;
                }
            }

            oggStream.FlushPages(outputData, true);

            return outputData.ToArray();
        }

        public static async Task<byte[]> ConvertToBytesAsync(float[][] samples, int sampleRate, int channels, float quality = 1f, CancellationToken cancellationToken = default)
        {
            using MemoryStream outputData = new MemoryStream();

            // Stores all the static vorbis bit stream settings
            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);

            // set up our packet->stream encoder
            var serial = new Random().Next();
            var oggStream = new OggStream(serial);

            // =========================================================
            // HEADER
            // =========================================================
            // Vorbis streams begin with three headers; the initial header
            // (with most of the codec setup parameters) which is mandated
            // by the Ogg bitstream spec.  The second header holds any
            // comment fields.  The third header holds the bitstream codebook.
            var comments = new Comments();

            var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
            var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
            var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

            oggStream.PacketIn(infoPacket);
            oggStream.PacketIn(commentsPacket);
            oggStream.PacketIn(booksPacket);

            // Flush to force audio data onto its own page per the spec
            await oggStream.FlushPagesAsync(outputData, true, cancellationToken).ConfigureAwait(false);

            // =========================================================
            // BODY (Audio Data)
            // =========================================================
            var processingState = ProcessingState.Create(info);
            var sampleLength = samples[0].Length;
            var writeBufferSize = 1024;

            for (var readIndex = 0; readIndex <= sampleLength;)
            {
                if (readIndex == sampleLength)
                {
                    processingState.WriteEndOfStream();
                    break;
                }

                processingState.WriteData(samples, writeBufferSize, readIndex);

                while (processingState.PacketOut(out var packet))
                {
                    oggStream.PacketIn(packet);
                    await oggStream.FlushPagesAsync(outputData, false, cancellationToken).ConfigureAwait(false);
                }

                var nextIndex = readIndex + writeBufferSize;

                if (nextIndex >= sampleLength - writeBufferSize)
                {
                    writeBufferSize = (sampleLength - readIndex) - writeBufferSize;
                    readIndex = sampleLength;
                }
                else
                {
                    readIndex = nextIndex;
                }
            }

            await oggStream.FlushPagesAsync(outputData, true, cancellationToken).ConfigureAwait(false);
            var result = outputData.ToArray();
            await outputData.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        public async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(AudioClip clip, string saveDirectory, CancellationToken cancellationToken, Action<Tuple<string, AudioClip>> callback = null, [CallerMemberName] string callingMethodName = null)
        {
            if (callingMethodName != nameof(RecordingManager.StartRecordingAsync))
            {
                throw new InvalidOperationException($"{nameof(StreamSaveToDiskAsync)} can only be called from {nameof(RecordingManager.StartRecordingAsync)}");
            }

            if (!Microphone.IsRecording(null))
            {
                throw new InvalidOperationException("Microphone is not initialized!");
            }

            if (RecordingManager.IsProcessing)
            {
                throw new AccessViolationException("Recoding already in progress!");
            }

            RecordingManager.IsProcessing = true;

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Recording process started...");
            }

            var lastPosition = 0;
            var clipName = clip.name;
            var maxClipLength = clip.samples;
            var samples = new float[clip.samples];

            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(1).ConfigureAwait(false);

            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            var path = $"{saveDirectory}/{clipName}.ogg";

            if (File.Exists(path))
            {
                Debug.LogWarning($"[{nameof(RecordingManager)}] {path} already exists, attempting to delete");
                File.Delete(path);
            }

            var outStream = new FileStream(path, FileMode.Create, FileAccess.Write);

            try
            {
                var info = VorbisInfo.InitVariableBitRate(Constants.Channels, Constants.Frequency, 0.2f);

                // set up our packet->stream encoder
                var oggStream = new OggStream(new Random().Next());

                #region Header

                // =========================================================
                // HEADER
                // =========================================================
                var comments = new Comments();

                var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
                var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
                var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

                oggStream.PacketIn(infoPacket);
                oggStream.PacketIn(commentsPacket);
                oggStream.PacketIn(booksPacket);

                // Flush to force audio data onto its own page per the spec
                OggPage page;
                while (oggStream.PageOut(out page, true))
                {
                    // ReSharper disable once MethodSupportsCancellation
                    await outStream.WriteAsync(page.Header, 0, page.Header.Length).ConfigureAwait(false);
                    // ReSharper disable once MethodSupportsCancellation
                    await outStream.WriteAsync(page.Body, 0, page.Body.Length).ConfigureAwait(false);
                }

                // Flush to force audio data onto its own page per the spec
                while (oggStream.PageOut(out page, true))
                {
                    // ReSharper disable once MethodSupportsCancellation
                    await outStream.WriteAsync(page.Header, 0, page.Header.Length).ConfigureAwait(false);
                    // ReSharper disable once MethodSupportsCancellation
                    await outStream.WriteAsync(page.Body, 0, page.Body.Length).ConfigureAwait(false);
                }

                #endregion Header

                #region Body

                // =========================================================
                // BODY (Audio Data)
                // =========================================================
                var processingState = ProcessingState.Create(info);
                var modulatorData = new short[samples.Length * info.Channels];
                var readBuffer = new byte[samples.Length * sizeof(float)];
                var channelBuffer = new float[info.Channels][];

                for (var i = 0; i < info.Channels; i++)
                {
                    channelBuffer[i] = new float[samples.Length];
                }

                var shouldStop = false;

                while (!oggStream.Finished)
                {
                    await Awaiters.UnityMainThread;
                    var currentPosition = Microphone.GetPosition(null);

                    if (clip != null)
                    {
                        clip.GetData(samples, 0);
                    }

                    if (shouldStop)
                    {
                        Microphone.End(null);
                    }

                    // ReSharper disable once MethodSupportsCancellation
                    await Task.Delay(1).ConfigureAwait(false);

                    if (currentPosition != 0)
                    {
                        int sampleIndex = 0;

                        foreach (var pcm in samples)
                        {
                            var sample = (short)(pcm * short.MaxValue);
                            modulatorData[sampleIndex++] = sample;
                            modulatorData[sampleIndex++] = sample;
                        }

                        Buffer.BlockCopy(modulatorData, 0, readBuffer, 0, modulatorData.Length);

                        int length = currentPosition - lastPosition;

                        for (var i = 0; i < length; i++)
                        {
                            var leftReadBuffer1 = (lastPosition + i) * sizeof(float) + 1;
                            var leftReadBuffer2 = (lastPosition + i) * sizeof(float) + 0;
                            var leftRawValue = (readBuffer[leftReadBuffer1] << 8) | (0x00ff & readBuffer[leftReadBuffer2]);
                            channelBuffer[0][i] = leftRawValue / (float)short.MaxValue;
                            var rightReadBuffer1 = (lastPosition + i) * sizeof(float) + 3;
                            var rightReadBuffer2 = (lastPosition + i) * sizeof(float) + 2;
                            var rightRawValue = (readBuffer[rightReadBuffer1] << 8) | (0x00ff & readBuffer[rightReadBuffer2]);
                            channelBuffer[1][i] = rightRawValue / (float)short.MaxValue;
                        }

                        processingState.WriteData(channelBuffer, length);
                        lastPosition = currentPosition;
                    }

                    while (!oggStream.Finished &&
                           processingState.PacketOut(out var packet))
                    {
                        oggStream.PacketIn(packet);

                        while (!oggStream.Finished &&
                               oggStream.PageOut(out page, false))
                        {
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(page.Header, 0, page.Header.Length).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(page.Body, 0, page.Body.Length).ConfigureAwait(false);
                        }
                    }

                    await Awaiters.UnityMainThread;

                    if (RecordingManager.EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] State: {nameof(RecordingManager.IsRecording)}? {RecordingManager.IsRecording} | {currentPosition} | isCancelled? {cancellationToken.IsCancellationRequested}");
                    }

                    if (currentPosition == maxClipLength ||
                        cancellationToken.IsCancellationRequested)
                    {
                        if (RecordingManager.IsRecording)
                        {
                            RecordingManager.IsRecording = false;

                            if (RecordingManager.EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Finished recording...");
                            }
                        }

                        if (shouldStop)
                        {
                            if (RecordingManager.EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Writing end of stream...");
                            }

                            processingState.WriteEndOfStream();
                            break;
                        }

                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] Stop stream requested...");
                        }

                        // delays stopping to make sure we process the last bits of the clip
                        shouldStop = true;
                    }
                }

                #endregion Body

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Flush stream...");
                }

                // ReSharper disable once MethodSupportsCancellation
                await outStream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return null;
            }
            finally
            {
                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Dispose stream...");
                }

                await outStream.DisposeAsync().ConfigureAwait(false);
            }

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Copying recording data stream...");
            }

            var microphoneData = new float[lastPosition];
            Array.Copy(samples, microphoneData, microphoneData.Length - 1);

            await Awaiters.UnityMainThread;

            // Create a copy.
            var newClip = AudioClip.Create(clipName, microphoneData.Length, 1, Constants.Frequency, false);
            newClip.SetData(microphoneData, 0);
            var result = new Tuple<string, AudioClip>(path, newClip);

            RecordingManager.IsProcessing = false;

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Finished processing...");
            }

            callback?.Invoke(result);
            return result;
        }
    }
}
