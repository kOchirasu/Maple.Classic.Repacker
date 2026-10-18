using System.Buffers.Binary;
using Maple.Classic.File.IO.Values;
using Maple.Classic.Repacker.Engine.IO;

namespace Maple.Classic.Repacker.Engine.Audio;

public enum WzWaveFormatTag : ushort {
    Unknown = 0x0000,
    Pcm = 0x0001,
    MpegLayer3 = 0x0055,
}

public sealed record ExtractedSoundData(
    byte[] AudioBytes,
    string MimeType,
    string Extension,
    string FormatName,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int DurationMs);

/// <summary>
/// Extracts playable MP3/WAV streams from WZ <see cref="WzValue.Sound"/> properties and constructs
/// <see cref="WzValue.Sound"/> nodes from uploaded <c>.mp3</c> or <c>.wav</c> files.
/// </summary>
public static class WzSoundCodec {
    // Standard 52-byte DirectSound GUID header prefix in WZ Sound_DX8 before WAVEFORMATEX length byte
    private static ReadOnlySpan<byte> StandardDx8GuidHeader => [
        0x02,
        0x83, 0xEB, 0x36, 0xE4, 0x4F, 0x52, 0xCE, 0x11, 0x9F, 0x53, 0x00, 0x20, 0xAF, 0x0B, 0xA7, 0x70,
        0x8B, 0xEB, 0x36, 0xE4, 0x4F, 0x52, 0xCE, 0x11, 0x9F, 0x53, 0x00, 0x20, 0xAF, 0x0B, 0xA7, 0x70,
        0x00,
        0x01,
        0x81, 0x9F, 0x58, 0x05, 0x56, 0xC3, 0xCE, 0x11, 0xBF, 0x01, 0x00, 0xAA, 0x00, 0x55, 0x59, 0x5A,
        0x00, // 52nd byte (previously left implicit as the zero-initialized buffer byte before the length byte)
    ];

    public static ExtractedSoundData ExtractAudioStream(WzValue.Sound sound) {
        ArgumentNullException.ThrowIfNull(sound);
        ReadOnlySpan<byte> payload = sound.Payload.Span;
        ReadOnlySpan<byte> header = sound.Header.Span;

        // 1. Check if payload is already a complete RIFF WAVE container
        if (payload.Length >= 44 &&
            payload[..4].SequenceEqual("RIFF"u8) &&
            payload.Slice(8, 4).SequenceEqual("WAVE"u8)) {
            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(22, 2));
            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
            ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(34, 2));
            return new ExtractedSoundData(
                payload.ToArray(),
                "audio/wav",
                "wav",
                "WAV (RIFF PCM)",
                sampleRate,
                channels,
                bitsPerSample,
                sound.Duration);
        }

        // 2. Check if payload is MP3 (ID3 tag or MPEG frame sync)
        if (IsMp3Stream(payload)) {
            (int sampleRate, int channels, int bitrateKbps) = InspectMp3Header(payload);
            return new ExtractedSoundData(
                payload.ToArray(),
                "audio/mpeg",
                "mp3",
                bitrateKbps > 0 ? $"MP3 ({bitrateKbps} kbps)" : "MP3 (MPEG Layer-3)",
                sampleRate,
                channels,
                16,
                sound.Duration);
        }

        // 3. Check WAVEFORMATEX inside WZ Sound_DX8 Header
        if (TryParseWaveFormatEx(header, out WzWaveFormatTag formatTag, out ushort ch, out int sr, out int avgBytesPerSec, out ushort blockAlign, out ushort bps)) {
            if (formatTag == WzWaveFormatTag.Pcm && !payload.IsEmpty) {
                // Raw PCM samples — wrap in standard 44-byte RIFF WAVE container so HTML5 <audio> plays it directly
                byte[] wavBytes = WrapPcmInRiffWave(payload, ch, sr, avgBytesPerSec, blockAlign, bps);
                return new ExtractedSoundData(
                    wavBytes,
                    "audio/wav",
                    "wav",
                    $"PCM WAV ({sr} Hz, {bps}-bit, {(ch == 1 ? "Mono" : "Stereo")})",
                    sr,
                    ch,
                    bps,
                    sound.Duration);
            }
            if (formatTag == WzWaveFormatTag.MpegLayer3) {
                return new ExtractedSoundData(
                    payload.ToArray(),
                    "audio/mpeg",
                    "mp3",
                    "MP3 (Sound_DX8)",
                    sr,
                    ch,
                    bps,
                    sound.Duration);
            }
        }

        // 4. Fallback for raw/synthetic payloads
        return new ExtractedSoundData(
            payload.ToArray(),
            "application/octet-stream",
            "bin",
            "Raw Sound_DX8",
            44100,
            2,
            16,
            sound.Duration);
    }

    public static WzValue.Sound CreateFromAudioBytes(ReadOnlySpan<byte> audioBytes) {
        if (audioBytes.IsEmpty) {
            return new WzValue.Sound(0, 0);
        }

        // Check if input is RIFF WAVE
        if (audioBytes.Length >= 44 &&
            audioBytes[..4].SequenceEqual("RIFF"u8) &&
            audioBytes.Slice(8, 4).SequenceEqual("WAVE"u8)) {
            var formatTag = (WzWaveFormatTag) BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(20, 2));
            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(22, 2));
            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.Slice(24, 4));
            int byteRate = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.Slice(28, 4));
            ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(32, 2));
            ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(34, 2));

            // Find "data" chunk or store full RIFF WAVE with PCM WAVEFORMATEX header
            byte[] dx8Header = BuildDx8WaveHeader(formatTag == WzWaveFormatTag.Unknown ? WzWaveFormatTag.Pcm : formatTag, channels, sampleRate, byteRate, blockAlign, bitsPerSample);
            int computedDuration = byteRate > 0 ? (int) ((audioBytes.Length - 44) * 1000L / byteRate) : 1000;
            return new WzValue.Sound(audioBytes.Length, Math.Max(1, computedDuration), audioBytes.ToArray(), dx8Header);
        }

        // MP3 or generic audio stream
        (int mp3Sr, int mp3Ch, int bitrateKbps) = InspectMp3Header(audioBytes);
        int sr = mp3Sr > 0 ? mp3Sr : 44100;
        ushort ch = (ushort) (mp3Ch > 0 ? mp3Ch : 2);
        int byteRateMp3 = bitrateKbps > 0 ? (bitrateKbps * 1000) / 8 : 16000;
        int duration = byteRateMp3 > 0 ? (int) (audioBytes.Length * 1000L / byteRateMp3) : 1000;
        byte[] mp3Header = BuildDx8Mp3Header(ch, sr, byteRateMp3);
        return new WzValue.Sound(audioBytes.Length, Math.Max(1, duration), audioBytes.ToArray(), mp3Header);
    }

    private static bool IsMp3Stream(ReadOnlySpan<byte> data) {
        if (data.Length < 4) return false;
        if (data[..3].SequenceEqual("ID3"u8)) return true;
        return data[0] == 0xFF && (data[1] & 0xE0) == 0xE0;
    }

    private static (int SampleRate, int Channels, int BitrateKbps) InspectMp3Header(ReadOnlySpan<byte> data) {
        int offset = 0;
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8)) {
            int id3Size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
            offset = 10 + id3Size;
        }

        int limit = Math.Min(data.Length - 4, offset + 4096);
        ReadOnlySpan<int> bitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        ReadOnlySpan<int> sampleRatesV1 = [44100, 48000, 32000, 44100];

        for (int i = Math.Max(0, offset); i <= limit; i++) {
            if (data[i] == 0xFF && (data[i + 1] & 0xE0) == 0xE0) {
                int brIdx = (data[i + 2] >> 4) & 0x0F;
                int srIdx = (data[i + 2] >> 2) & 0x03;
                int chMode = (data[i + 3] >> 6) & 0x03;
                int bitrate = bitratesV1L3[brIdx];
                int sr = sampleRatesV1[srIdx];
                int channels = chMode == 3 ? 1 : 2;
                if (bitrate > 0) {
                    return (sr, channels, bitrate);
                }
            }
        }

        return (44100, 2, 128);
    }

    private static bool TryParseWaveFormatEx(
        ReadOnlySpan<byte> header,
        out WzWaveFormatTag formatTag,
        out ushort channels,
        out int sampleRate,
        out int avgBytesPerSec,
        out ushort blockAlign,
        out ushort bitsPerSample) {
        formatTag = WzWaveFormatTag.Unknown;
        channels = 2;
        sampleRate = 44100;
        avgBytesPerSec = 176400;
        blockAlign = 4;
        bitsPerSample = 16;

        // Standard WZ Sound_DX8 header: 52 bytes of GUIDs, 1 length byte (>= 16), then WAVEFORMATEX
        int lengthOffset = StandardDx8GuidHeader.Length;
        int formatOffset = lengthOffset + 1;
        int wfOffset = -1;
        if (header.Length >= formatOffset + WAVE_FORMAT_EX_SIZE && header[lengthOffset] >= 16 && formatOffset + header[lengthOffset] <= header.Length) {
            wfOffset = formatOffset;
        } else if (header.Length >= WAVE_FORMAT_EX_SIZE) {
            wfOffset = header.Length - WAVE_FORMAT_EX_SIZE;
        }

        if (wfOffset < 0 || wfOffset + 16 > header.Length) {
            return false;
        }

        ReadOnlySpan<byte> wf = header[wfOffset..];
        formatTag = (WzWaveFormatTag) BinaryPrimitives.ReadUInt16LittleEndian(wf[..2]);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(2, 2));
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wf.Slice(4, 4));
        avgBytesPerSec = BinaryPrimitives.ReadInt32LittleEndian(wf.Slice(8, 4));
        blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(12, 2));
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(14, 2));
        return channels is >= 1 and <= 8 && sampleRate is >= 4000 and <= 192000;
    }

    private const int RIFF_WAVE_HEADER_SIZE = 44;
    private const int WAVE_FORMAT_EX_SIZE = 18;      // WAVEFORMATEX with cbSize = 0
    private const int MPEG_LAYER3_WAVE_FORMAT_SIZE = 30; // MPEGLAYER3WAVEFORMAT (WAVEFORMATEX + 12 extra bytes)

    /// <summary>Wraps raw PCM samples in a minimal 44-byte RIFF/WAVE container so browsers can play them.</summary>
    private static byte[] WrapPcmInRiffWave(
        ReadOnlySpan<byte> pcm,
        ushort channels,
        int sampleRate,
        int avgBytesPerSec,
        ushort blockAlign,
        ushort bitsPerSample) {
        byte[] wav = new byte[RIFF_WAVE_HEADER_SIZE + pcm.Length];
        var w = new SpanWriter(wav);
        w.WriteBytes("RIFF"u8);
        w.WriteUInt32LittleEndian((uint) (RIFF_WAVE_HEADER_SIZE - 8 + pcm.Length)); // RIFF chunk size
        w.WriteBytes("WAVE"u8);
        w.WriteBytes("fmt "u8);
        w.WriteUInt32LittleEndian(16);                                          // fmt chunk size (PCM WAVEFORMAT)
        w.WriteUInt16LittleEndian((ushort) WzWaveFormatTag.Pcm);
        w.WriteUInt16LittleEndian(channels);
        w.WriteInt32LittleEndian(sampleRate);
        w.WriteInt32LittleEndian(avgBytesPerSec > 0 ? avgBytesPerSec : sampleRate * channels * (bitsPerSample / 8));
        w.WriteUInt16LittleEndian(blockAlign > 0 ? blockAlign : (ushort) (channels * (bitsPerSample / 8)));
        w.WriteUInt16LittleEndian(bitsPerSample > 0 ? bitsPerSample : (ushort) 16);
        w.WriteBytes("data"u8);
        w.WriteUInt32LittleEndian((uint) pcm.Length);
        w.WriteBytes(pcm);
        w.EnsureFilled();
        return wav;
    }

    /// <summary>Sound_DX8 header for PCM: DirectSound GUIDs, then a length-prefixed WAVEFORMATEX.</summary>
    private static byte[] BuildDx8WaveHeader(
        WzWaveFormatTag formatTag,
        ushort channels,
        int sampleRate,
        int avgBytesPerSec,
        ushort blockAlign,
        ushort bitsPerSample) {
        byte[] header = new byte[StandardDx8GuidHeader.Length + 1 + WAVE_FORMAT_EX_SIZE];
        var w = new SpanWriter(header);
        w.WriteBytes(StandardDx8GuidHeader);
        w.WriteByte(WAVE_FORMAT_EX_SIZE);
        WriteWaveFormatEx(ref w, formatTag, channels, sampleRate, avgBytesPerSec, blockAlign, bitsPerSample, extraSize: 0);
        w.EnsureFilled();
        return header;
    }

    /// <summary>Sound_DX8 header for MP3: DirectSound GUIDs, then a length-prefixed MPEGLAYER3WAVEFORMAT.</summary>
    private static byte[] BuildDx8Mp3Header(ushort channels, int sampleRate, int avgBytesPerSec) {
        byte[] header = new byte[StandardDx8GuidHeader.Length + 1 + MPEG_LAYER3_WAVE_FORMAT_SIZE];
        var w = new SpanWriter(header);
        w.WriteBytes(StandardDx8GuidHeader);
        w.WriteByte(MPEG_LAYER3_WAVE_FORMAT_SIZE);
        WriteWaveFormatEx(ref w, WzWaveFormatTag.MpegLayer3, channels, sampleRate, avgBytesPerSec, blockAlign: 1, bitsPerSample: 0,
            extraSize: MPEG_LAYER3_WAVE_FORMAT_SIZE - WAVE_FORMAT_EX_SIZE);
        w.WriteUInt16LittleEndian(1);    // wID = MPEGLAYER3_ID_MPEG
        w.WriteUInt32LittleEndian(2);    // fdwFlags = MPEGLAYER3_FLAG_PADDING_OFF
        w.WriteUInt16LittleEndian(417);  // nBlockSize
        w.WriteUInt16LittleEndian(1);    // nFramesPerBlock
        w.WriteUInt16LittleEndian(1393); // nCodecDelay
        w.EnsureFilled();
        return header;
    }

    /// <summary>Writes the 18-byte WAVEFORMATEX struct; <paramref name="extraSize"/> is its trailing cbSize field.</summary>
    private static void WriteWaveFormatEx(
        ref SpanWriter w,
        WzWaveFormatTag formatTag,
        ushort channels,
        int sampleRate,
        int avgBytesPerSec,
        ushort blockAlign,
        ushort bitsPerSample,
        ushort extraSize) {
        w.WriteUInt16LittleEndian((ushort) formatTag);
        w.WriteUInt16LittleEndian(channels);
        w.WriteInt32LittleEndian(sampleRate);
        w.WriteInt32LittleEndian(avgBytesPerSec);
        w.WriteUInt16LittleEndian(blockAlign);
        w.WriteUInt16LittleEndian(bitsPerSample);
        w.WriteUInt16LittleEndian(extraSize);
    }
}
