using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Session;

namespace ProactiveAI.Tests.Fakes;

/// <summary>In-memory session history.</summary>
public sealed class FakeSessionArchive : ISessionArchive
{
    private readonly Dictionary<string, SessionRecord> _records = new(StringComparer.Ordinal);

    public int Saves { get; private set; }

    public Exception? FailWith { get; set; }

    public void Save(SessionRecord record)
    {
        lock (_records)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }
            Saves++;
            _records[record.Summary.Id] = record;
        }
    }

    public IReadOnlyList<SessionSummary> List()
    {
        lock (_records)
        {
            return _records.Values.Select(r => r.Summary).OrderByDescending(s => s.StartedUtc).ToList();
        }
    }

    public SessionRecord? Load(string id)
    {
        lock (_records)
        {
            return _records.GetValueOrDefault(id);
        }
    }

    public bool Delete(string id)
    {
        lock (_records)
        {
            return _records.Remove(id);
        }
    }

    public void DeleteAll()
    {
        lock (_records)
        {
            _records.Clear();
        }
    }
}

/// <summary>Test sound that "plays" by invoking a callback (e.g. pushing loud audio into the fake loopback capture).</summary>
public sealed class FakeTestSoundPlayer : ITestSoundPlayer
{
    public TimeSpan Duration => TimeSpan.FromMilliseconds(400);

    public List<string> Played { get; } = new();

    public Func<string, Task>? OnPlay { get; set; }

    public Exception? FailWith { get; set; }

    public async Task PlayAsync(string playbackDeviceId, CancellationToken cancellationToken)
    {
        Played.Add(playbackDeviceId);
        if (FailWith is not null)
        {
            throw FailWith;
        }
        if (OnPlay is not null)
        {
            await OnPlay(playbackDeviceId);
        }
    }
}

/// <summary>Intent classifier driven by the test; records every request.</summary>
public sealed class FakeIntentClassifier : IQuestionIntentClassifier
{
    public List<IntentRequest> Requests { get; } = new();
    public Func<IntentRequest, IntentVerdict> Respond { get; set; } = _ => new IntentVerdict(true, 0.9, null);
    public Exception? Fail { get; set; }
    public TimeSpan Delay { get; set; }

    public async Task<IntentVerdict> ClassifyAsync(IntentRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }
        if (Fail is not null)
        {
            throw Fail;
        }
        return Respond(request);
    }
}

public static class Signals
{
    /// <summary>Speech-like noise with a random, non-periodic syllable envelope (bursts of 80–300 ms, short gaps).</summary>
    public static short[] Speech(TimeSpan duration, int seed, double amplitude = 0.3, double modulationHz = 4)
    {
        var rng = new Random(seed);
        int n = PipelineAudio.Samples(duration);
        var s = new short[n];
        int i = 0;
        while (i < n)
        {
            int burst = PipelineAudio.Samples(TimeSpan.FromMilliseconds(80 + rng.Next(220)));
            int gap = PipelineAudio.Samples(TimeSpan.FromMilliseconds(20 + rng.Next(110)));
            double level = amplitude * (0.35 + 0.65 * rng.NextDouble());
            for (int k = 0; k < burst && i < n; k++, i++)
            {
                double env = Math.Sin(Math.PI * k / burst); // smooth syllable shape
                s[i] = (short)Math.Clamp((rng.NextDouble() * 2 - 1) * level * env * 32767, short.MinValue, short.MaxValue);
            }
            for (int k = 0; k < gap && i < n; k++, i++)
            {
                s[i] = (short)((rng.NextDouble() * 2 - 1) * 30);
            }
        }
        return s;
    }

    public static short[] Silence(TimeSpan duration) => new short[PipelineAudio.Samples(duration)];

    public static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    public static short[] DelayAndScale(short[] source, TimeSpan delay, double gain)
    {
        int d = PipelineAudio.Samples(delay);
        var r = new short[source.Length + d];
        for (int i = 0; i < source.Length; i++)
        {
            r[i + d] = (short)Math.Clamp(source[i] * gain, short.MinValue, short.MaxValue);
        }
        return r;
    }

    public static short[] Mix(short[] a, short[] b)
    {
        var r = new short[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++)
        {
            int v = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
            r[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
        return r;
    }

    public static IEnumerable<short[]> Frames(short[] samples)
    {
        for (int i = 0; i + PipelineAudio.FrameSamples <= samples.Length; i += PipelineAudio.FrameSamples)
        {
            yield return samples.AsSpan(i, PipelineAudio.FrameSamples).ToArray();
        }
    }

    /// <summary>
    /// Loudspeaker echo as a laptop microphone hears it: delayed, attenuated, with early reflections and a
    /// residual-echo gain that an AEC varies by up to ~14 dB in 200 ms steps, plus a microphone noise floor.
    /// </summary>
    public static short[] RoomEcho(short[] playback, int seed, double gain = 0.12, int delayMs = 80, bool variableAec = true)
    {
        var rng = new Random(seed);
        int n = playback.Length;
        var y = new double[n];
        (int Ms, double G)[] taps = [(delayMs, 1.0), (delayMs + 35, 0.5), (delayMs + 110, 0.25)];
        foreach (var (ms, g) in taps)
        {
            int d = PipelineAudio.Samples(TimeSpan.FromMilliseconds(ms));
            for (int i = d; i < n; i++)
            {
                y[i] += playback[i - d] * g * gain;
            }
        }
        int block = PipelineAudio.Samples(TimeSpan.FromMilliseconds(200));
        var r = new short[n];
        for (int b = 0; b < n; b += block)
        {
            double aec = variableAec ? Math.Pow(10, -rng.NextDouble() * 14 / 20) : 1.0;
            for (int i = b; i < Math.Min(n, b + block); i++)
            {
                r[i] = (short)Math.Clamp(y[i] * aec + (rng.NextDouble() * 2 - 1) * 40, short.MinValue, short.MaxValue);
            }
        }
        return r;
    }

    public static short[] Scale(short[] s, double g) => s.Select(v => (short)Math.Clamp(v * g, short.MinValue, short.MaxValue)).ToArray();

    public static short[] Place(short[] s, TimeSpan offset, TimeSpan total)
    {
        var r = new short[PipelineAudio.Samples(total)];
        int o = PipelineAudio.Samples(offset);
        Array.Copy(s, 0, r, o, Math.Min(s.Length, r.Length - o));
        return r;
    }
}

/// <summary>Deterministic capture source driven by the test.</summary>
public sealed class FakeCaptureSource(CaptureRequest request) : ICaptureSource
{
    public CaptureRequest Request { get; } = request;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public AudioSourceKind Kind => Request.Kind;

    public event Action<AudioChunk>? ChunkCaptured;
    public event Action<CaptureFault>? Faulted;

    public CaptureStartInfo Start()
    {
        Started = true;
        return new CaptureStartInfo(Request.DeviceId, "Fake " + Request.Kind, "24kHz", Request.Kind == AudioSourceKind.Microphone ? EchoControlStatus.CommunicationsModeOnly : EchoControlStatus.NotApplicable, null);
    }

    public void Push(short[] samples, long qpc100ns) => ChunkCaptured?.Invoke(new AudioChunk(samples, qpc100ns, false));

    public void Fault(CaptureFaultKind kind) => Faulted?.Invoke(new CaptureFault(Kind, kind, "fault"));

    public Task StopAsync()
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Stopped = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeCaptureFactory : ICaptureSourceFactory
{
    public List<FakeCaptureSource> Created { get; } = new();
    public Func<CaptureRequest, Exception?>? FailWith { get; set; }

    public FakeCaptureSource? Latest(AudioSourceKind kind) => Created.LastOrDefault(c => c.Kind == kind);

    public ICaptureSource Create(CaptureRequest request)
    {
        var src = new FakeCaptureSource(request);
        lock (Created)
        {
            Created.Add(src);
        }
        if (FailWith?.Invoke(request) is { } ex)
        {
            return new ThrowingCapture(request, ex);
        }
        return src;
    }

    private sealed class ThrowingCapture(CaptureRequest r, Exception ex) : ICaptureSource
    {
        public AudioSourceKind Kind => r.Kind;
        public event Action<AudioChunk>? ChunkCaptured { add { } remove { } }
        public event Action<CaptureFault>? Faulted { add { } remove { } }
        public CaptureStartInfo Start() => throw ex;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class FakeDeviceCatalog : IAudioDeviceCatalog
{
    public List<AudioDeviceInfo> Devices { get; } =
    [
        new("spk-1", "Speakers", AudioSourceKind.System, true),
        new("mic-1", "Microphone", AudioSourceKind.Microphone, true),
    ];

    public event EventHandler<AudioDeviceChange>? DeviceChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSourceKind kind) => Devices.Where(d => d.Kind == kind).ToList();

    public AudioDeviceInfo? GetDefault(AudioSourceKind kind) => Devices.FirstOrDefault(d => d.Kind == kind && d.IsDefault);

    public AudioDeviceInfo? Find(AudioSourceKind kind, string deviceId) => Devices.FirstOrDefault(d => d.Kind == kind && d.Id == deviceId);

    public void Raise(AudioDeviceChange change) => DeviceChanged?.Invoke(this, change);
}

/// <summary>Transcription stream whose provider events are injected by the test.</summary>
public sealed class FakeTranscriptionStream : ITranscriptionStream
{
    private readonly Channel<TranscriptionEvent> _events = Channel.CreateUnbounded<TranscriptionEvent>();
    private int _item;

    public List<int> AppendSizes { get; } = new();
    /// <summary>All audio appended to the stream, in order (guarded by <see cref="AppendSizes"/>).</summary>
    public List<short> Sent { get; } = new();
    public int Commits { get; private set; }
    public int Clears { get; private set; }
    public bool Disposed { get; private set; }
    public TimeSpan AppendDelay { get; set; }
    public List<string> CommitIds { get; } = new();

    /// <summary>When true, each commit automatically produces committed + delta + completed events.</summary>
    public bool AutoTranscribe { get; set; } = true;
    public Func<int, string> TextFor { get; set; } = i => $"utterance {i}";

    public async ValueTask AppendAsync(ReadOnlyMemory<short> pcm, CancellationToken cancellationToken)
    {
        if (AppendDelay > TimeSpan.Zero)
        {
            await Task.Delay(AppendDelay, cancellationToken);
        }
        lock (AppendSizes)
        {
            AppendSizes.Add(pcm.Length);
            Sent.AddRange(pcm.ToArray());
        }
    }

    public ValueTask CommitAsync(string clientEventId, CancellationToken cancellationToken)
    {
        Commits++;
        CommitIds.Add(clientEventId);
        if (AutoTranscribe)
        {
            int i = ++_item;
            string id = $"item_{i}";
            Emit(new TranscriptionCommitted(id, null));
            Emit(new TranscriptionDelta(id, TextFor(i)[..Math.Min(4, TextFor(i).Length)]));
            Emit(new TranscriptionCompleted(id, TextFor(i), null));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        Clears++;
        return ValueTask.CompletedTask;
    }

    public void Emit(TranscriptionEvent ev) => _events.Writer.TryWrite(ev);

    public void Close(ProviderFailureKind kind) => Emit(new TranscriptionClosed(kind, "test"));

    public async IAsyncEnumerable<TranscriptionEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_events.Reader.TryRead(out var ev))
            {
                yield return ev;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    private int _connects;
    public List<FakeTranscriptionStream> Streams { get; } = new();
    public Func<int, Exception?>? FailOnConnect { get; set; }
    public TimeSpan ConnectDelay { get; set; }
    public Action<FakeTranscriptionStream>? Configure { get; set; }

    public int Connects => Volatile.Read(ref _connects);

    public async Task<ITranscriptionStream> ConnectAsync(TranscriptionStreamOptions options, CancellationToken cancellationToken)
    {
        int n = Interlocked.Increment(ref _connects);
        if (ConnectDelay > TimeSpan.Zero)
        {
            await Task.Delay(ConnectDelay, cancellationToken);
        }
        if (FailOnConnect?.Invoke(n) is { } ex)
        {
            throw ex;
        }
        var s = new FakeTranscriptionStream();
        Configure?.Invoke(s);
        lock (Streams)
        {
            Streams.Add(s);
        }
        return s;
    }
}

public sealed class FakeDiarizer : ISpeakerDiarizer
{
    public List<DiarizationRequest> Requests { get; } = new();
    public Func<DiarizationRequest, DiarizationResult>? Respond { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public Exception? Fail { get; set; }

    public async Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Fail is { } ex)
        {
            throw ex;
        }
        return Respond?.Invoke(request) ?? new DiarizationResult([], null);
    }
}

/// <summary>Assistant model returning scripted streamed JSON. A gate can hold the stream open.</summary>
public sealed class FakeAssistantModel : IAssistantModel
{
    public List<AssistantModelRequest> Requests { get; } = new();
    public List<DateTimeOffset> RequestTimes { get; } = new();
    public List<string> WarmUps { get; } = new();
    public Func<AssistantModelRequest, string>? Respond { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public int ChunkSize { get; set; } = 40;
    public Func<int, Exception?>? FailAttempt { get; set; }
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    public Task WarmUpAsync(string model, CancellationToken cancellationToken)
    {
        lock (WarmUps)
        {
            WarmUps.Add(model);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AssistantStreamUpdate> StreamAsync(AssistantModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int n;
        lock (Requests)
        {
            Requests.Add(request);
            RequestTimes.Add(DateTimeOffset.UtcNow);
            n = Requests.Count;
        }
        if (FailAttempt?.Invoke(n) is { } ex)
        {
            throw ex;
        }
        string json = Respond?.Invoke(request) ?? Json.None();
        int half = json.Length / 2;
        for (int i = 0; i < json.Length; i += ChunkSize)
        {
            if (Gate is { } gate && i >= half)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(ChunkDelay, cancellationToken);
            }
            yield return new AssistantTextDelta(json.Substring(i, Math.Min(ChunkSize, json.Length - i)));
        }
        yield return new AssistantCompleted(json, new UsageInfo(100, 50));
    }
}

public static class Json
{
    public static string None() => Build(kind: "none", notify: false);

    public static string Answer(string questionId, int revision, IEnumerable<string> evidence, string spoken = "This is the full spoken answer with explanation and an example.", IEnumerable<string>? prep = null, string direct = "Direct position answering the question.", string? followsUpOn = null, IEnumerable<string>? filledIn = null)
        => Build("answer", true, questionId, revision, direct: direct, spoken: spoken, evidence: evidence, prep: prep, questionText: "the question", category: "answer", followsUpOn: followsUpOn, filledIn: filledIn);

    public static string Insight(IEnumerable<string> evidence, string title = "Clarify ordering", string suggestion = "The ordering requirement is unresolved.")
        => Build("insight", true, title: title, suggestion: suggestion, evidence: evidence, category: "requirement");

    public static string Build(string kind, bool notify, string? questionId = null, int? revision = null, string? title = null, string? direct = null,
        string? spoken = null, string? suggestion = null, IEnumerable<string>? evidence = null, IEnumerable<string>? prep = null, string? questionText = null,
        string category = "none", IEnumerable<string>? missing = null, string? followsUpOn = null, IEnumerable<string>? filledIn = null)
    {
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["response_kind"] = kind,
            ["should_notify"] = notify,
            ["question_id"] = questionId,
            ["question_revision"] = revision,
            ["title"] = title,
            ["direct_position"] = direct,
            ["follows_up_on"] = followsUpOn,
            ["spoken_answer"] = spoken,
            ["filled_in_details"] = new System.Text.Json.Nodes.JsonArray((filledIn ?? []).Select(m => (System.Text.Json.Nodes.JsonNode?)m).ToArray()),
            ["suggestion"] = suggestion,
            ["suggested_question"] = null,
            ["supporting_points"] = new System.Text.Json.Nodes.JsonArray("point one", "point two"),
            ["assumptions_and_limits"] = new System.Text.Json.Nodes.JsonArray("limit one"),
            ["likely_followups"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["question"] = "Why?", ["response_direction"] = "Because of X." }),
            ["missing_facts"] = new System.Text.Json.Nodes.JsonArray((missing ?? []).Select(m => (System.Text.Json.Nodes.JsonNode?)m).ToArray()),
            ["question_text"] = questionText,
            ["question_segment_ids"] = new System.Text.Json.Nodes.JsonArray(),
            ["category"] = category,
            ["priority"] = "normal",
            ["evidence_segment_ids"] = new System.Text.Json.Nodes.JsonArray((evidence ?? []).Select(e => (System.Text.Json.Nodes.JsonNode?)e).ToArray()),
            ["preparation_evidence_ids"] = new System.Text.Json.Nodes.JsonArray((prep ?? []).Select(e => (System.Text.Json.Nodes.JsonNode?)e).ToArray()),
            ["answer_language"] = "en",
            ["decision_note"] = "test",
        };
        return o.ToJsonString();
    }
}

public sealed class FakeCredentials(bool has = true) : ICredentialStore
{
    public bool Has { get; set; } = has;
    public bool HasApiKey() => Has;
    public string? ReadApiKey() => Has ? "sk-test-not-a-real-key-000000000000" : null;
    public void WriteApiKey(string apiKey) => Has = true;
    public void DeleteApiKey() => Has = false;
}

/// <summary>Character-based token counter for deterministic budget tests.</summary>
public sealed class CharTokenCounter : ITokenCounter
{
    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met: " + (because ?? "(unspecified)"));
            }
            await Task.Delay(10);
        }
    }
}

public static class Clock
{
    /// <summary>A fake time provider starting at a fixed date.</summary>
    public static FakeTimeProvider Fake() => new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.Zero };
}
