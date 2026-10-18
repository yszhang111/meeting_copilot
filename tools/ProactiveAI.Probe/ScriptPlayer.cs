using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Diagnostics;
using Windows.Media.SpeechSynthesis;

namespace ProactiveAI.Probe;

public sealed record ScriptVoice(string Voice, string Pitch = "default", string Rate = "0%");

public sealed record ScriptLine(string Speaker, string Text, int PauseAfterMs = 600, int WaitAfterMs = 0);

public sealed record SpeechScript(string Name, Dictionary<string, ScriptVoice> Voices, List<ScriptLine> Lines, string? Preparation = null, string? Scenario = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static SpeechScript Load(string path) =>
        JsonSerializer.Deserialize<SpeechScript>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid script");
}

/// <summary>
/// Synthesizes a scripted multi-voice dialogue with Windows TTS (in memory) and plays it through the
/// default output device, recording ground-truth turn times on the session clock.
/// </summary>
public sealed class ScriptPlayer
{
    public static IReadOnlyList<string> InstalledVoices() =>
        SpeechSynthesizer.AllVoices.Select(v => $"{v.DisplayName} ({v.Language}, {v.Gender})").ToList();

    public static async Task<List<(ScriptLine Line, short[] Pcm)>> SynthesizeAsync(SpeechScript script)
    {
        var result = new List<(ScriptLine, short[])>();
        using var synth = new SpeechSynthesizer();
        foreach (var line in script.Lines)
        {
            var v = script.Voices[line.Speaker];
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(x => x.DisplayName.Contains(v.Voice, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Voice '{v.Voice}' is not installed.");
            synth.Voice = voice;
            string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{voice.Language}'>" +
                          $"<prosody pitch='{v.Pitch}' rate='{v.Rate}'>{System.Security.SecurityElement.Escape(line.Text)}</prosody></speak>";
            using var stream = await synth.SynthesizeSsmlToStreamAsync(ssml);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
            result.Add((line, ToPipeline(bytes)));
        }
        return result;
    }

    public static async Task<List<GroundTruthTurn>> PlayAsync(List<(ScriptLine Line, short[] Pcm)> lines, SessionClock clock, Action<string>? onLine, CancellationToken ct)
    {
        var truth = new List<GroundTruthTurn>();
        foreach (var (line, pcm) in lines)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = WavEncoder.ToPcmBytes(pcm);
            using var provider = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(PipelineAudio.SampleRate, 16, 1));
            using var output = new WaveOut();
            var done = new TaskCompletionSource();
            output.PlaybackStopped += (_, _) => done.TrySetResult();
            output.Init(provider);
            var start = clock.Now + TimeSpan.FromMilliseconds(60);
            output.Play();
            onLine?.Invoke($"[{start:mm\\:ss}] {line.Speaker}: {line.Text}");
            await done.Task.WaitAsync(ct);
            var end = start + PipelineAudio.Duration(pcm.Length);
            truth.Add(new GroundTruthTurn(line.Speaker, start, end, line.Text));
            await Task.Delay(line.PauseAfterMs + line.WaitAfterMs, ct);
        }
        return truth;
    }

    private static short[] ToPipeline(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new StereoToMonoSampleProvider(samples);
        }
        if (samples.WaveFormat.SampleRate != PipelineAudio.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, PipelineAudio.SampleRate);
        }
        var list = new List<short>();
        var buf = new float[4800];
        int n;
        while ((n = samples.Read(buf.AsSpan())) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                list.Add((short)Math.Clamp(buf[i] * 32767f, short.MinValue, short.MaxValue));
            }
        }
        // trim leading/trailing digital silence so ground truth reflects audible speech
        int first = list.FindIndex(s => Math.Abs((int)s) > 300);
        int last = list.FindLastIndex(s => Math.Abs((int)s) > 300);
        if (first < 0)
        {
            return list.ToArray();
        }
        return list.GetRange(first, last - first + 1).ToArray();
    }
}
