using NAudio.Dsp;
using NAudio.Wave;

namespace ProactiveAI.Infrastructure.Audio;

/// <summary>
/// Fallback conversion from a device mix format to pipeline audio (24 kHz mono PCM16) when the audio
/// engine cannot convert for us. Downmixes by averaging channels and resamples with WDL.
/// </summary>
internal sealed class PcmConverter
{
    private readonly WaveFormat _source;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly WdlResampler? _resampler;
    private float[] _mono = Array.Empty<float>();
    private float[] _out = Array.Empty<float>();

    public PcmConverter(WaveFormat source)
    {
        _source = source;
        _channels = Math.Max(1, source.Channels);
        _bytesPerSample = source.BitsPerSample / 8;
        _isFloat = source.Encoding == WaveFormatEncoding.IeeeFloat
                   || (source is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (source.SampleRate != Core.Audio.PipelineAudio.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(source.SampleRate, Core.Audio.PipelineAudio.SampleRate);
        }
    }

    public bool IsPassThrough => !_isFloat && _bytesPerSample == 2 && _channels == 1 && _resampler is null;

    public short[] Convert(ReadOnlySpan<byte> buffer, bool silent)
    {
        int frames = buffer.Length / (_bytesPerSample * _channels);
        if (frames == 0)
        {
            return Array.Empty<short>();
        }
        if (_mono.Length < frames)
        {
            _mono = new float[frames];
        }
        if (silent)
        {
            Array.Clear(_mono, 0, frames);
        }
        else
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    sum += ReadSample(buffer, (f * _channels + c) * _bytesPerSample);
                }
                _mono[f] = sum / _channels;
            }
        }

        ReadOnlySpan<float> output;
        if (_resampler is null)
        {
            output = _mono.AsSpan(0, frames);
        }
        else
        {
            int want = _resampler.ResamplePrepare(frames, 1, out Span<float> inBuf);
            _mono.AsSpan(0, Math.Min(want, frames)).CopyTo(inBuf);
            int maxOut = (int)(frames * (double)Core.Audio.PipelineAudio.SampleRate / _source.SampleRate) + 16;
            if (_out.Length < maxOut)
            {
                _out = new float[maxOut];
            }
            int produced = _resampler.ResampleOut(_out, Math.Min(want, frames), maxOut, 1);
            output = _out.AsSpan(0, produced);
        }

        var result = new short[output.Length];
        for (int i = 0; i < output.Length; i++)
        {
            float v = Math.Clamp(output[i], -1f, 1f);
            result[i] = (short)Math.Round(v * 32767f);
        }
        return result;
    }

    private float ReadSample(ReadOnlySpan<byte> b, int offset)
    {
        if (_isFloat)
        {
            return _bytesPerSample == 8 ? (float)BitConverter.ToDouble(b.Slice(offset, 8)) : BitConverter.ToSingle(b.Slice(offset, 4));
        }
        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(b.Slice(offset, 2)) / 32768f,
            3 => ((b[offset] << 8 | b[offset + 1] << 16 | b[offset + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(b.Slice(offset, 4)) / 2147483648f,
            1 => (b[offset] - 128) / 128f,
            _ => 0f,
        };
    }
}
