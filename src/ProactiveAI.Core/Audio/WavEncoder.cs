using System.Buffers.Binary;

namespace ProactiveAI.Core.Audio;

/// <summary>Encodes pipeline PCM into an in-memory WAV container for upload. Never touches disk.</summary>
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate = PipelineAudio.SampleRate)
    {
        int dataBytes = samples.Length * 2;
        var bytes = new byte[44 + dataBytes];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        var data = span[44..];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], samples[i]);
        }
        return bytes;
    }

    public static byte[] ToPcmBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }
        return bytes;
    }
}
