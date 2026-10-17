using System.Buffers.Binary;
using Maple.Classic.File.IO;

namespace Maple.Classic.Repacker.Engine.Audio;

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
        if (TryParseWaveFormatEx(header, out ushort formatTag, out ushort ch, out int sr, out int avgBytesPerSec, out ushort blockAlign, out ushort bps)) {
            if (formatTag == 0x0001 && !payload.IsEmpty) {
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
            if (formatTag == 0x0055) {
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

    public static WzValue.Sound CreateFromAudioBytes(ReadOnlySpan<byte> audioBytes, int? durationMsOverride = null) {
        if (audioBytes.IsEmpty) {
            return new WzValue.Sound(0, durationMsOverride ?? 0);
        }

        // Check if input is RIFF WAVE
        if (audioBytes.Length >= 44 &&
            audioBytes[..4].SequenceEqual("RIFF"u8) &&
            audioBytes.Slice(8, 4).SequenceEqual("WAVE"u8)) {
            ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(20, 2));
            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(22, 2));
            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.Slice(24, 4));
            int byteRate = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.Slice(28, 4));
            ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(32, 2));
            ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(audioBytes.Slice(34, 2));

            // Find "data" chunk or store full RIFF WAVE with PCM WAVEFORMATEX header
            byte[] dx8Header = BuildDx8WaveHeader(formatTag == 0 ? (ushort) 1 : formatTag, channels, sampleRate, byteRate, blockAlign, bitsPerSample);
            int computedDuration = durationMsOverride ?? (byteRate > 0 ? (int) ((long) (audioBytes.Length - 44) * 1000L / byteRate) : 1000);
            return new WzValue.Sound(audioBytes.Length, Math.Max(1, computedDuration), audioBytes.ToArray(), dx8Header);
        }

        // MP3 or generic audio stream
        (int mp3Sr, int mp3Ch, int bitrateKbps) = InspectMp3Header(audioBytes);
        int sr = mp3Sr > 0 ? mp3Sr : 44100;
        ushort ch = (ushort) (mp3Ch > 0 ? mp3Ch : 2);
        int byteRateMp3 = bitrateKbps > 0 ? (bitrateKbps * 1000) / 8 : 16000;
        int duration = durationMsOverride ?? (byteRateMp3 > 0 ? (int) ((long) audioBytes.Length * 1000L / byteRateMp3) : 1000);
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
        out ushort formatTag,
        out ushort channels,
        out int sampleRate,
        out int avgBytesPerSec,
        out ushort blockAlign,
        out ushort bitsPerSample) {
        formatTag = 0;
        channels = 2;
        sampleRate = 44100;
        avgBytesPerSec = 176400;
        blockAlign = 4;
        bitsPerSample = 16;

        // Standard WZ Sound_DX8 header has 52 bytes of GUIDs, 1 byte waveHeaderLen ( >= 18 ), then WAVEFORMATEX
        int wfOffset = -1;
        if (header.Length >= 53 + 18 && header[52] >= 16 && 53 + header[52] <= header.Length) {
            wfOffset = 53;
        } else if (header.Length >= 18) {
            wfOffset = header.Length - 18;
        }

        if (wfOffset < 0 || wfOffset + 16 > header.Length) {
            return false;
        }

        ReadOnlySpan<byte> wf = header[wfOffset..];
        formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wf[..2]);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(2, 2));
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wf.Slice(4, 4));
        avgBytesPerSec = BinaryPrimitives.ReadInt32LittleEndian(wf.Slice(8, 4));
        blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(12, 2));
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wf.Slice(14, 2));
        return channels is >= 1 and <= 8 && sampleRate is >= 4000 and <= 192000;
    }

    private static byte[] WrapPcmInRiffWave(
        ReadOnlySpan<byte> pcm,
        ushort channels,
        int sampleRate,
        int avgBytesPerSec,
        ushort blockAlign,
        ushort bitsPerSample) {
        byte[] wav = new byte[44 + pcm.Length];
        Span<byte> s = wav;
        "RIFF"u8.CopyTo(s[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), (uint) (36 + pcm.Length));
        "WAVE"u8.CopyTo(s.Slice(8, 4));
        "fmt "u8.CopyTo(s.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(28, 4), avgBytesPerSec > 0 ? avgBytesPerSec : sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(32, 2), blockAlign > 0 ? blockAlign : (ushort) (channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(34, 2), bitsPerSample > 0 ? bitsPerSample : (ushort) 16);
        "data"u8.CopyTo(s.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40, 4), (uint) pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }

    private static byte[] BuildDx8WaveHeader(
        ushort formatTag,
        ushort channels,
        int sampleRate,
        int avgBytesPerSec,
        ushort blockAlign,
        ushort bitsPerSample) {
        byte[] header = new byte[52 + 1 + 18];
        StandardDx8GuidHeader.CopyTo(header);
        header[52] = 18;
        Span<byte> wf = header.AsSpan(53, 18);
        BinaryPrimitives.WriteUInt16LittleEndian(wf[..2], formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(2, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wf.Slice(4, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wf.Slice(8, 4), avgBytesPerSec);
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(12, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(14, 2), bitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(16, 2), 0); // cbSize = 0
        return header;
    }

    private static byte[] BuildDx8Mp3Header(ushort channels, int sampleRate, int avgBytesPerSec) {
        // 52-byte DX8 GUIDs + 1 byte len (30) + 30-byte MPEGLAYER3WAVEFORMAT = 83 bytes
        byte[] header = new byte[52 + 1 + 30];
        StandardDx8GuidHeader.CopyTo(header);
        header[52] = 30;
        Span<byte> wf = header.AsSpan(53, 30);
        BinaryPrimitives.WriteUInt16LittleEndian(wf[..2], 0x0055); // WAVE_FORMAT_MPEGLAYER3
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(2, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wf.Slice(4, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wf.Slice(8, 4), avgBytesPerSec);
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(12, 2), 1); // nBlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(14, 2), 0); // wBitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(16, 2), 12); // cbSize = 12
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(18, 2), 1);  // wID = MPEGLAYER3_ID_MPEG
        BinaryPrimitives.WriteUInt32LittleEndian(wf.Slice(20, 4), 2);  // fdwFlags = MPEGLAYER3_FLAG_PADDING_OFF
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(24, 2), 417); // nBlockSize
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(26, 2), 1);  // nFramesPerBlock
        BinaryPrimitives.WriteUInt16LittleEndian(wf.Slice(28, 2), 1393); // nCodecDelay
        return header;
    }
}
