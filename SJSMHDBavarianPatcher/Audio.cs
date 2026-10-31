using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;
using NVorbis;

namespace SJSMHDBavarianPatcher;

/// <summary>Turns a 16-bit PCM WAV or Ogg Vorbis (.ogg) file into an FSB5 container (the format Unity's AudioClip streams).</summary>
public static class AudioConverter
{
    /// <summary>
    /// Auto-detects whether the audio bytes are Ogg Vorbis ("OggS") or WAV ("RIFF") and decodes to 16-bit PCM.
    /// </summary>
    public static bool TryParseAudio(byte[] data, out int channels, out int sampleRate, out byte[] pcm, out string error)
    {
        if (data.Length >= 4 && BitConverter.ToInt32(data, 0) == 0x5367674F /* "OggS" */)
        {
            return TryParseOgg(data, out channels, out sampleRate, out pcm, out error);
        }
        return TryParseWav(data, out channels, out sampleRate, out pcm, out error);
    }

    public static bool TryParseOgg(byte[] ogg, out int channels, out int sampleRate, out byte[] pcm, out string error)
    {
        channels = 0; sampleRate = 0; pcm = Array.Empty<byte>(); error = "";

        if (ogg.Length < 4 || BitConverter.ToInt32(ogg, 0) != 0x5367674F /* "OggS" */)
        {
            error = "not an Ogg Vorbis file";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(ogg);
            using var reader = new VorbisReader(ms, closeOnDispose: false);

            channels = reader.Channels;
            sampleRate = reader.SampleRate;

            if (channels < 1 || channels > 2)
            {
                error = $"the OGG has {channels} channels. Only mono and stereo are supported.";
                return false;
            }

            using var pcmStream = new MemoryStream();
            float[] buffer = new float[4096 * channels];
            byte[] byteBuf = new byte[buffer.Length * 2];

            int samplesRead;
            while ((samplesRead = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < samplesRead; i++)
                {
                    // Convert float sample (-1.0f..1.0f) to 16-bit signed PCM (-32768..32767)
                    float f = Math.Clamp(buffer[i], -1.0f, 1.0f);
                    short s = (short)Math.Round(f * 32767f);
                    byteBuf[i * 2] = (byte)(s & 0xFF);
                    byteBuf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                pcmStream.Write(byteBuf, 0, samplesRead * 2);
            }

            pcm = pcmStream.ToArray();
            if (pcm.Length == 0)
            {
                error = "no audio data found in the OGG";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "could not decode OGG file: " + ex.Message;
            return false;
        }
    }

    public static bool TryParseWav(byte[] wav, out int channels, out int sampleRate, out byte[] pcm, out string error)
    {
        channels = 0; sampleRate = 0; pcm = Array.Empty<byte>(); error = "";

        if (wav.Length < 12 || BitConverter.ToInt32(wav, 0) != 0x46464952 /* RIFF */)
        {
            error = "not a RIFF/WAV or OGG file";
            return false;
        }

        int pos = 12;
        bool haveFmt = false;
        while (pos + 8 <= wav.Length)
        {
            int id = BitConverter.ToInt32(wav, pos);
            uint size = BitConverter.ToUInt32(wav, pos + 4);
            int body = pos + 8;

            if (id == 0x20746D66) // "fmt "
            {
                short format = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                short bits = BitConverter.ToInt16(wav, body + 14);

                // 1 = PCM, 0xFFFE = extensible
                if (format != 1 && format != unchecked((short)0xFFFE))
                {
                    error = "the WAV is not PCM (maybe float or compressed). Export it as 'WAV signed 16-bit PCM'.";
                    return false;
                }
                if (bits != 16)
                {
                    error = $"the WAV is {bits}-bit. Export it as 'WAV signed 16-bit PCM'.";
                    return false;
                }
                if (channels < 1 || channels > 2)
                {
                    error = $"the WAV has {channels} channels. Only mono and stereo are supported.";
                    return false;
                }
                haveFmt = true;
            }
            else if (id == 0x61746164 && haveFmt) // "data"
            {
                // a data size of 0xFFFFFFFF (streamed WAVs) or a truncated file is clamped to what exists
                int length = (int)Math.Min(size, (uint)(wav.Length - body));
                length -= length % (2 * channels); // whole frames only
                pcm = new byte[length];
                Array.Copy(wav, body, pcm, 0, length);
                return true;
            }

            long next = (long)body + size + (size & 1); // chunks are word-aligned
            if (next > wav.Length) break;
            pos = (int)next;
        }

        error = "no audio data found in the WAV";
        return false;
    }

    public static byte[] BuildPcm16Fsb5(byte[] pcm, int channels, int sampleRate)
    {
        int freqIndex = sampleRate switch
        {
            8000 => 1,
            11000 => 2,
            11025 => 3,
            16000 => 4,
            22050 => 5,
            24000 => 6,
            32000 => 7,
            44100 => 8,
            48000 => 9,
            _ => throw new ModException(
                $"unsupported sample rate {sampleRate} Hz. Resample the audio to 44100 or 48000 Hz " +
                "(supported: 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000)")
        };

        int sampleCount = pcm.Length / (2 * channels);

        // 64-bit sample header: bit0 extraParams, bits1-4 freq, bit5 channels-1,
        // bits6-33 dataOffset/16, bits34-63 sample count
        ulong sampleHeader = 0;
        sampleHeader |= (ulong)freqIndex << 1;
        sampleHeader |= (ulong)(channels - 1) << 5;
        sampleHeader |= (ulong)sampleCount << 34;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new byte[] { (byte)'F', (byte)'S', (byte)'B', (byte)'5' });
        w.Write(1);            // version
        w.Write(1);            // number of samples
        w.Write(8);            // sample headers size
        w.Write(0);            // name table size
        w.Write(pcm.Length);   // data size
        w.Write(2);            // mode: PCM16
        w.Write(new byte[8]);  // zero
        w.Write(new byte[16]); // hash
        w.Write(new byte[8]);  // dummy
        w.Write(sampleHeader);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reads the generated FSB back with Fmod5Sharp so a broken file is never written to the game.</summary>
    public static void Validate(byte[] fsb)
    {
        try
        {
            if (!FsbLoader.TryLoadFsbFromByteArray(fsb, out FmodSoundBank? bank) || bank == null || bank.Samples.Count != 1)
                throw new ModException("the generated FSB could not be read back");

            bank.Samples[0].RebuildAsStandardFileFormat(out byte[]? _, out string? _);
        }
        catch (ModException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModException("the generated FSB failed validation: " + ex.Message);
        }
    }
}