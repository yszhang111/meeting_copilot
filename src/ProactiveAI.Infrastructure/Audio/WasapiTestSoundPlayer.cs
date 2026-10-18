using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProactiveAI.Core.Session;

namespace ProactiveAI.Infrastructure.Audio;

/// <summary>Plays a short three-note chime (about 1.2 s, peak about -10 dBFS) on a WASAPI playback endpoint (shared mode).</summary>
public sealed class WasapiTestSoundPlayer : ITestSoundPlayer
{
    public TimeSpan Duration => Chime.Length;

    public Task PlayAsync(string playbackDeviceId, CancellationToken cancellationToken) =>
        // Off the UI thread: the player raises PlaybackStopped on the captured synchronization context.
        Task.Run(async () =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(playbackDeviceId);
            var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(100).Build();
            try
            {
                WaveFormat mix;
                try
                {
                    mix = player.DeviceMixFormat;
                }
                catch
                {
                    mix = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
                }
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception is { } ex)
                    {
                        done.TrySetException(ex);
                    }
                    else
                    {
                        done.TrySetResult();
                    }
                };
                player.Init(new Chime(mix.SampleRate, mix.Channels).ToWaveProvider());
                player.Play();
                using (cancellationToken.Register(() => player.Stop()))
                {
                    await done.Task.WaitAsync(Chime.Length + TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                player.Dispose();
            }
        }, cancellationToken);

    /// <summary>E5, G5 and C6 bell tones with soft attack and exponential decay.</summary>
    internal sealed class Chime : ISampleProvider
    {
        public static readonly TimeSpan Length = TimeSpan.FromMilliseconds(1250);
        private static readonly (double Hz, double Start)[] Notes = [(659.25, 0.0), (783.99, 0.16), (1046.5, 0.32)];
        private readonly int _rate;
        private readonly int _channels;
        private readonly long _total;
        private long _frame;

        public Chime(int sampleRate, int channels)
        {
            _rate = sampleRate;
            _channels = Math.Max(1, channels);
            _total = (long)(Length.TotalSeconds * sampleRate);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int frames = Math.Min(buffer.Length / _channels, (int)Math.Max(0, _total - _frame));
            for (int i = 0; i < frames; i++, _frame++)
            {
                float v = (float)Sample(_frame / (double)_rate);
                for (int c = 0; c < _channels; c++)
                {
                    buffer[i * _channels + c] = v;
                }
            }
            return frames * _channels;
        }

        internal static double Sample(double t)
        {
            double v = 0;
            foreach (var (hz, start) in Notes)
            {
                double u = t - start;
                if (u < 0)
                {
                    continue;
                }
                double attack = Math.Min(1, u / 0.01);
                double decay = Math.Exp(-u / 0.28);
                v += 0.12 * attack * decay * (Math.Sin(2 * Math.PI * hz * u) + 0.3 * Math.Sin(4 * Math.PI * hz * u));
            }
            double fadeOut = Math.Clamp((Length.TotalSeconds - t) / 0.05, 0, 1);
            return v * fadeOut;
        }
    }
}
