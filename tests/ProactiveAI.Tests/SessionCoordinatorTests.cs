using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class SessionCoordinatorTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeCaptureFactory Capture = new();
        public FakeDeviceCatalog Devices = new();
        public FakeTranscriptionProvider Transcription = new();
        public FakeDiarizer Diarizer = new();
        public FakeAssistantModel Model = new();
        public FakeCredentials Credentials = new();
        public SessionCoordinator Session;

        public Harness(AppSettings? settings = null, IQuestionIntentClassifier? intent = null, ISessionArchive? archive = null)
        {
            Session = new SessionCoordinator(Capture, Devices, Transcription, Diarizer, Model, Credentials, new CharTokenCounter(), TimeProvider.System,
                settings ?? new AppSettings { Scenario = Scenario.Interview }, NullLoggerFactory.Instance, intent, archive);
        }

        public FakeTranscriptionStream Stream(AudioSourceKind kind, int index = -1)
        {
            lock (Transcription.Streams)
            {
                var list = Transcription.Streams;
                return index < 0 ? list[^1] : list[index];
            }
        }

        /// <summary>Plays system and microphone tracks simultaneously in 10 ms packets with shared timestamps.</summary>
        public async Task PlayAsync(short[]? system, short[]? mic, int packetMs = 10)
        {
            var sys = Capture.Latest(AudioSourceKind.System);
            var micSrc = Capture.Latest(AudioSourceKind.Microphone);
            long start = Session.Clock.Now100ns();
            int chunk = PipelineAudio.Samples(TimeSpan.FromMilliseconds(packetMs));
            int length = Math.Max(system?.Length ?? 0, mic?.Length ?? 0);
            int n = 0;
            for (int i = 0; i < length; i += chunk)
            {
                long qpc = start + PipelineAudio.Duration(i).Ticks;
                if (system is not null && i < system.Length)
                {
                    sys!.Push(system.AsSpan(i, Math.Min(chunk, system.Length - i)).ToArray(), qpc);
                }
                if (mic is not null && i < mic.Length)
                {
                    micSrc!.Push(mic.AsSpan(i, Math.Min(chunk, mic.Length - i)).ToArray(), qpc);
                }
                if (++n % 10 == 0)
                {
                    await Task.Delay(15);
                }
            }
        }

        /// <summary>Delivers a speech turn followed by silence through the latest capture in realistic 10 ms WASAPI packets.</summary>
        /// <param name="realTime">Pace packets like a real device (needed when asserting on the throttled status/level meter).</param>
        public async Task SpeakAsync(AudioSourceKind kind, double seconds = 1.2, int seed = 1, int packetMs = 10, bool realTime = false)
        {
            var src = Capture.Latest(kind)!;
            var audio = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(0.4)), Signals.Speech(TimeSpan.FromSeconds(seconds), seed), Signals.Silence(TimeSpan.FromSeconds(1.0)));
            long start = Session.Clock.Now100ns();
            int chunk = PipelineAudio.Samples(TimeSpan.FromMilliseconds(packetMs));
            var pace = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            for (int i = 0; i < audio.Length; i += chunk)
            {
                var part = audio.AsSpan(i, Math.Min(chunk, audio.Length - i)).ToArray();
                src.Push(part, start + PipelineAudio.Duration(i).Ticks);
                if (realTime)
                {
                    var wait = PipelineAudio.Duration(i + part.Length) - pace.Elapsed;
                    if (wait > TimeSpan.FromMilliseconds(5))
                    {
                        await Task.Delay(wait);
                    }
                }
                else if (++n % Math.Max(1, 100 / packetMs) == 0)
                {
                    await Task.Delay(15);
                }
            }
        }

        public async ValueTask DisposeAsync() => await Session.DisposeAsync();
    }

    [Theory]
    [InlineData(10)] // shared-mode WASAPI engine period
    [InlineData(3)]  // low-latency / odd packet sizes
    [InlineData(100)]
    public async Task SmallCapturePacketsStillFormFramesAndTurns(int packetMs)
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting, SystemAudioEnabled = false });
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        double peakLevel = -120;
        h.Session.StatusChanged += s => peakLevel = Math.Max(peakLevel, s.Microphone.LevelDbfs);
        await Task.Delay(500); // real capture starts well after the session clock origin
        await h.SpeakAsync(AudioSourceKind.Microphone, packetMs: packetMs, realTime: true);
        try
        {
            await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal && s.Kind == SegmentKind.Speech), because: $"caption with {packetMs} ms packets");
        }
        catch (TimeoutException)
        {
            var m = h.Session.Snapshot.Microphone.Metrics;
            var st = h.Stream(AudioSourceKind.Microphone);
            Assert.Fail($"no caption: metrics={m} appends={st.AppendSizes.Count} sentSamples={st.AppendSizes.Sum()} commits={st.Commits} clears={st.Clears} peak={peakLevel:F0} connects={h.Transcription.Connects} state={h.Session.State} msg={h.Session.Snapshot.Message} segs={h.Session.Transcript.Count}");
        }
        Assert.Equal(1, h.Stream(AudioSourceKind.Microphone).Commits);
        Assert.True(peakLevel > -40, $"level meter saw {peakLevel:F0} dBFS");
    }

    [Fact]
    public async Task LoudspeakerEchoIsHiddenEvenWhenStoppedBeforeTheCaptionIsFinal()
    {
        // Segment-level echo hiding (the layer behind any canceller): Windows processing leaves the echo in.
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview, EchoCancellation = EchoCancellationMode.Windows });
        h.Transcription.Configure = s => s.AutoTranscribe = false;
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        await Task.Delay(300);
        var playback = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(0.3)), Signals.Speech(TimeSpan.FromSeconds(3), 11));
        await h.PlayAsync(playback, Signals.RoomEcho(playback, 12, 0.2, 90, variableAec: false));
        // Provisional captions on both sources, then the user stops before either turn ends.
        h.Stream(AudioSourceKind.System, 0).Emit(new TranscriptionDelta("s1", "The future is autonomous, right?"));
        h.Stream(AudioSourceKind.Microphone, 1).Emit(new TranscriptionDelta("m1", "The future is autonomous right"));
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Count(s => s.Kind == SegmentKind.Speech) == 2, because: "two provisional captions");
        await h.Session.StopAsync();
        var mic = h.Session.Transcript.Snapshot().Single(s => s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech);
        var sys = h.Session.Transcript.Snapshot().Single(s => s.Source == AudioSourceKind.System && s.Kind == SegmentKind.Speech);
        Assert.Equal(sys.Id, mic.DuplicateOfSegmentId);
        Assert.Null(sys.DuplicateOfSegmentId);
    }

    [Fact]
    public async Task EchoOnTheMicrophoneDoesNotInterruptInterviewQuestionDetection()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview, EchoCancellation = EchoCancellationMode.Windows });
        h.Transcription.Configure = s => s.TextFor = _ => "Can you explain how a B-tree index works?";
        h.Model.Respond = r =>
        {
            var qid = System.Text.RegularExpressions.Regex.Match(r.Input, "question_id=\"(q-\\d+)\"").Groups[1].Value;
            var segId = System.Text.RegularExpressions.Regex.Match(r.Input, "segments=\"(seg-\\d+)").Groups[1].Value;
            return Json.Answer(qid, 1, [segId]);
        };
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        await Task.Delay(300);
        var playback = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(0.3)), Signals.Speech(TimeSpan.FromSeconds(2), 13), Signals.Silence(TimeSpan.FromSeconds(1.2)));
        await h.PlayAsync(playback, Signals.RoomEcho(playback, 14, 0.2, 90, variableAec: false));
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Count(s => s.Kind == SegmentKind.Speech && s.IsFinal) == 2, because: "both finals");
        var mic = h.Session.Transcript.Snapshot().Single(s => s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech);
        Assert.NotNull(mic.DuplicateOfSegmentId);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer despite echo");
    }

    [Fact]
    public async Task LocalSpeechWhilePlaybackIsSilentStaysAsMe()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview });
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        await Task.Delay(300);
        var local = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(0.3)), Signals.Speech(TimeSpan.FromSeconds(1.5), 15), Signals.Silence(TimeSpan.FromSeconds(1.2)));
        await h.PlayAsync(Signals.Silence(TimeSpan.FromSeconds(local.Length / (double)PipelineAudio.SampleRate)), local);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.Source == AudioSourceKind.Microphone && s.IsFinal));
        var mic = h.Session.Transcript.Snapshot().Single(s => s.Source == AudioSourceKind.Microphone);
        Assert.Null(mic.DuplicateOfSegmentId);
        Assert.Equal(AttributionStatus.SourceAlias, mic.Attribution.Status);
    }

    [Theory]
    [InlineData(EchoCancellationMode.Windows, true)]
    [InlineData(EchoCancellationMode.Off, false)]
    public async Task OtherEchoModesRequestWindowsProcessingOrNothing(EchoCancellationMode mode, bool windows)
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting, EchoCancellation = mode });
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        var mic = h.Capture.Latest(AudioSourceKind.Microphone)!;
        Assert.Equal(windows, mic.Request.UseCommunicationsProcessing);
        Assert.Equal(windows ? "spk-1" : null, mic.Request.EchoReferenceRenderDeviceId);
        Assert.NotEqual(EchoControlStatus.BuiltInCanceller, h.Session.Snapshot.Microphone.EchoControl);
    }

    [Fact]
    public async Task BuiltInCancellerFallsBackToWindowsProcessingWithoutSystemCapture()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting, SystemAudioEnabled = false });
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        var mic = h.Capture.Latest(AudioSourceKind.Microphone)!;
        Assert.True(mic.Request.UseCommunicationsProcessing);
        Assert.Equal("spk-1", mic.Request.EchoReferenceRenderDeviceId);
    }

    private static double Correlation(IReadOnlyList<short> a, IReadOnlyList<short> b, int lag)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            int j = i + lag;
            if (j < 0 || j >= b.Count)
            {
                continue;
            }
            ab += (double)a[i] * b[j];
            aa += (double)a[i] * a[i];
            bb += (double)b[j] * b[j];
        }
        return ab / Math.Sqrt(aa * bb + 1e-9);
    }

    [Fact]
    public async Task ResidualEchoBeforeTheCancellerConvergesIsStillHidden()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview });
        h.Transcription.Configure = s => s.TextFor = _ => "Can you explain how a B-tree index works?";
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        await Task.Delay(300);
        // Loud playback from the start: while the canceller is still learning (partially converged), residual echo
        // opens microphone turns, and that residual no longer resembles the playback closely.
        var playback = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(0.3)), Signals.Speech(TimeSpan.FromSeconds(6), 41), Signals.Silence(TimeSpan.FromSeconds(1.2)));
        await h.PlayAsync(playback, Signals.RoomEcho(playback, 42, 0.5, 90, variableAec: false));
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.Source == AudioSourceKind.Microphone && s.IsFinal), because: "residual echo caption");
        await Task.Delay(500);
        var mic = h.Session.Transcript.Snapshot().Where(s => s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech).ToList();
        Assert.NotEmpty(mic);
        Assert.All(mic, s => Assert.True(s.DuplicateOfSegmentId is not null, $"visible echo caption {s.AudioStart}-{s.AudioEnd}"));
    }

    [Fact]
    public async Task BuiltInCancellerRemovesLoudspeakerEchoButKeepsLocalSpeech()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting });
        await h.Session.SetMicrophoneEnabledAsync(true);
        await h.Session.StartAsync();
        await Task.Delay(300);
        // 12 s of continuous loudspeaker playback; the user talks over it from 8 s to 10 s.
        var total = TimeSpan.FromSeconds(12);
        var playback = Signals.Speech(total, 21);
        var echo = Signals.RoomEcho(playback, 22, 0.25, 90, variableAec: false);
        var local = Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(2), 23), 0.6);
        var mic = Signals.Mix(echo, Signals.Place(local, TimeSpan.FromSeconds(8), total));
        var micStart = h.Session.Clock.Now;
        await h.PlayAsync(playback, mic);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.Source == AudioSourceKind.Microphone && s.IsFinal && s.AudioEnd > micStart + TimeSpan.FromSeconds(8.5)),
            8000, "caption for the local speech");

        var status = h.Session.Snapshot.Microphone;
        Assert.Equal(EchoControlStatus.BuiltInCanceller, status.EchoControl);
        Assert.Contains("removing", status.EchoDetail);
        var micSegments = h.Session.Transcript.Snapshot().Where(s => s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech).ToList();
        // After the canceller has learnt the echo path, echo alone no longer opens microphone turns...
        Assert.DoesNotContain(micSegments, s => s.AudioStart > micStart + TimeSpan.FromSeconds(4) && s.AudioEnd < micStart + TimeSpan.FromSeconds(7.9));
        // ...while the user's own speech is transcribed as "Me" and is not hidden as an echo.
        var mine = micSegments.Single(s => s.AudioEnd > micStart + TimeSpan.FromSeconds(8.5));
        Assert.Null(mine.DuplicateOfSegmentId);
        Assert.Equal(AttributionStatus.SourceAlias, mine.Attribution.Status);
        // What was sent for transcription is the local talker, not the playback.
        var stream = h.Stream(AudioSourceKind.Microphone, 1);
        List<short> sent;
        lock (stream.AppendSizes)
        {
            sent = stream.Sent.ToList();
        }
        var lastTurn = sent.Skip(Math.Max(0, sent.Count - PipelineAudio.Samples(TimeSpan.FromSeconds(2.6)))).ToList();
        double toLocal = Enumerable.Range(-PipelineAudio.Samples(TimeSpan.FromSeconds(0.5)), PipelineAudio.Samples(TimeSpan.FromSeconds(1)))
            .Where(l => l % 12 == 0).Max(l => Correlation(local, lastTurn, l));
        Assert.True(toLocal > 0.9, $"sent audio correlation with the local talker {toLocal:F2}");
    }

    [Fact]
    public async Task ConcurrentStartsOpenExactlyOneCaptureAndConnection()
    {
        await using var h = new Harness();
        h.Transcription.ConnectDelay = TimeSpan.FromMilliseconds(100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(h.Session.StartAsync)));
        Assert.Equal(SessionState.Listening, h.Session.State);
        Assert.Equal(1, h.Transcription.Connects);
        Assert.Single(h.Capture.Created);
    }

    [Fact]
    public async Task StartWithoutKeyOrSourcesReportsErrorWithoutConnecting()
    {
        await using var h = new Harness();
        h.Credentials.Has = false;
        await h.Session.StartAsync();
        Assert.Equal(SessionState.Error, h.Session.State);
        Assert.Equal(0, h.Transcription.Connects);

        h.Credentials.Has = true;
        h.Session.UpdateSettings(h.Session.Settings with { SystemAudioEnabled = false });
        await h.Session.StartAsync();
        Assert.Equal(SessionState.Error, h.Session.State);
        Assert.Equal(0, h.Transcription.Connects);
    }

    [Fact]
    public async Task CapturedSpeechBecomesFinalTranscriptAndUploadsOnlyDuringSpeech()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal && s.Kind == SegmentKind.Speech), because: "final caption");
        var stream = h.Stream(AudioSourceKind.System);
        Assert.Equal(1, stream.Commits);
        int sent = stream.AppendSizes.Sum();
        Assert.InRange(sent, PipelineAudio.Samples(TimeSpan.FromSeconds(1.2)), PipelineAudio.Samples(TimeSpan.FromSeconds(2.4)));
        var seg = h.Session.Transcript.Snapshot().Single(s => s.Kind == SegmentKind.Speech);
        Assert.Equal(AttributionStatus.Pending, seg.Attribution.Status); // speaker label arrives later
        Assert.InRange(seg.AudioStart.TotalSeconds, 0, 1.0);
    }

    [Fact]
    public async Task PauseRejectsLateResultsAndResumeUsesANewGenerationWithGapMarker()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.AutoTranscribe = false;
        await h.Session.StartAsync();
        var oldStream = h.Stream(AudioSourceKind.System);
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => oldStream.Commits == 1);
        await h.Session.PauseAsync();
        Assert.True(oldStream.Disposed);
        Assert.True(h.Capture.Latest(AudioSourceKind.System)!.Stopped);
        oldStream.Emit(new TranscriptionCompleted("late", "late text after pause", null));
        await Task.Delay(100);
        Assert.DoesNotContain(h.Session.Transcript.Snapshot(), s => s.Text.Contains("late text"));

        await h.Session.ResumeAsync();
        Assert.Equal(SessionState.Listening, h.Session.State);
        Assert.Equal(2, h.Transcription.Connects);
        Assert.Contains(h.Session.Transcript.Snapshot(), s => s.Kind == SegmentKind.Gap && s.Note!.Contains("paused"));
    }

    [Fact]
    public async Task MicrophoneIsOffByDefaultAndMuteStopsOnlyTheMicrophone()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        Assert.Null(h.Capture.Latest(AudioSourceKind.Microphone));

        await h.Session.SetMicrophoneEnabledAsync(true);
        var mic1 = h.Capture.Latest(AudioSourceKind.Microphone)!;
        Assert.True(mic1.Started);
        // Built-in canceller (default): the raw microphone is captured, without a second Windows canceller.
        Assert.False(mic1.Request.UseCommunicationsProcessing);
        Assert.Null(mic1.Request.EchoReferenceRenderDeviceId);
        Assert.Equal(EchoControlStatus.BuiltInCanceller, h.Session.Snapshot.Microphone.EchoControl);

        await h.Session.SetMicrophoneMutedAsync(true);
        Assert.True(mic1.Stopped);
        Assert.False(h.Capture.Latest(AudioSourceKind.System)!.Stopped);
        int sentBefore = h.Transcription.Streams.Sum(s => s.AppendSizes.Sum());
        mic1.Push(Signals.Speech(TimeSpan.FromSeconds(1), 3), h.Session.Clock.Now100ns()); // capture callback after mute
        await Task.Delay(200);
        Assert.Equal(sentBefore, h.Transcription.Streams.Sum(s => s.AppendSizes.Sum()));

        await h.Session.SetMicrophoneMutedAsync(false);
        var mic2 = h.Capture.Latest(AudioSourceKind.Microphone)!;
        Assert.NotSame(mic1, mic2);
        Assert.True(h.Session.Snapshot.Microphone.Generation > 1);
    }

    [Fact]
    public async Task PersonalMicrophoneUsesSourceAliasAndIsNotTreatedAsAQuestionSource()
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview, SystemAudioEnabled = false });
        await h.Session.SetMicrophoneEnabledAsync(true);
        h.Transcription.Configure = s => s.TextFor = _ => "Could you explain what you mean by that?";
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.Microphone);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal));
        var seg = h.Session.Transcript.Snapshot().Single();
        Assert.Equal(AttributionStatus.SourceAlias, seg.Attribution.Status);
        await Task.Delay(1500);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(h.Session.Board.Cards);
    }

    [Fact]
    public async Task ResetClearsSessionContentButKeepsPreparation()
    {
        await using var h = new Harness();
        h.Session.Preparation.Set(PrepField.RoleOrTopic, "Backend interview");
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Count > 0);
        h.Session.References.Offer("spk-1", Signals.Speech(TimeSpan.FromSeconds(3), 1), TimeSpan.Zero);
        h.Session.History.Upsert(new QuestionExchange("q", 1, new PinnedExcerpt([], "q"), null, null, TimeSpan.Zero));
        await h.Session.ResetAsync();
        Assert.Equal(SessionState.Stopped, h.Session.State);
        Assert.Equal(0, h.Session.Transcript.Count);
        Assert.Equal(0, h.Session.References.Count);
        Assert.Empty(h.Session.History.Items);
        Assert.Empty(h.Session.Board.Cards);
        Assert.Equal("Backend interview", h.Session.Preparation.Get(PrepField.RoleOrTopic));
    }

    [Fact]
    public async Task StopKeepsTranscriptAndClearsVoiceReferences()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Count > 0);
        h.Session.References.Offer("spk-1", Signals.Speech(TimeSpan.FromSeconds(3), 1), TimeSpan.Zero);
        await h.Session.StopAsync();
        await h.Session.StopAsync(); // repeated stop is safe
        Assert.Equal(SessionState.Stopped, h.Session.State);
        Assert.True(h.Session.Transcript.Count > 0);
        Assert.Equal(0, h.Session.References.Count);
    }

    [Fact]
    public async Task NetworkDropReconnectsWithNewGenerationAndMarksGap()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        h.Stream(AudioSourceKind.System).Close(ProviderFailureKind.Network);
        await Wait.UntilAsync(() => h.Transcription.Connects == 2 && h.Session.State == SessionState.Listening, 8000, "reconnected");
        Assert.Contains(h.Session.Transcript.Snapshot(), s => s.Kind == SegmentKind.Gap && s.Note!.Contains("Reconnected"));
        Assert.Equal(2, h.Capture.Created.Count);
    }

    [Fact]
    public async Task AuthenticationFailureStopsWithoutEndlessRetries()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        h.Stream(AudioSourceKind.System).Emit(new TranscriptionError(ProviderFailureKind.Authentication, "invalid_api_key", "bad key", true));
        await Wait.UntilAsync(() => h.Session.State == SessionState.Error);
        await Task.Delay(1500);
        Assert.Equal(1, h.Transcription.Connects);
        Assert.False(h.Session.Snapshot.CanRetry);
    }

    [Fact]
    public async Task EndpointLossPausesTheWholeSessionAndShowsMissingSource()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        h.Devices.Raise(new AudioDeviceChange(AudioDeviceChangeKind.Removed, null, "spk-1"));
        await Wait.UntilAsync(() => h.Session.State == SessionState.Paused);
        Assert.True(h.Session.Snapshot.System.Missing);
        Assert.Contains("disconnected", h.Session.Snapshot.Message);
    }

    [Fact]
    public async Task DefaultDeviceChangePausesWhenFollowingDefault()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        h.Devices.Raise(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, AudioSourceKind.System, "spk-2"));
        await Wait.UntilAsync(() => h.Session.State == SessionState.Paused);
        Assert.Contains("default", h.Session.Snapshot.Message);
    }

    [Fact]
    public async Task SleepPausesAndResumeRequiresUserAction()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        await h.Session.OnSystemSuspendAsync();
        Assert.Equal(SessionState.Paused, h.Session.State);
        await Task.Delay(300);
        Assert.Equal(SessionState.Paused, h.Session.State);
    }

    [Fact]
    public async Task QueueOverflowDiscardsStaleAudioAndRecordsGap()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.AppendDelay = TimeSpan.FromMilliseconds(400);
        await h.Session.StartAsync();
        var src = h.Capture.Latest(AudioSourceKind.System)!;
        long t = h.Session.Clock.Now100ns();
        var speech = Signals.Speech(TimeSpan.FromSeconds(6), 9);
        int chunk = PipelineAudio.Samples(TimeSpan.FromMilliseconds(100));
        for (int i = 0; i < speech.Length; i += chunk)
        {
            src.Push(speech.AsSpan(i, chunk).ToArray(), t + PipelineAudio.Duration(i).Ticks); // faster than real time
        }
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.Kind == SegmentKind.Gap && s.Note!.Contains("backlog")), 8000, "gap marker");
        var metrics = h.Session.Snapshot.System.Metrics!;
        Assert.True(metrics.DroppedMs > 0);
        Assert.True(metrics.QueuedMs <= 2000);
        Assert.True(h.Stream(AudioSourceKind.System).Clears >= 1);
    }

    [Fact]
    public async Task SlowAnalysisDoesNotBlockCaptions()
    {
        await using var h = new Harness();
        h.Model.Gate = new TaskCompletionSource(); // the reasoning model never completes
        h.Transcription.Configure = s => s.TextFor = i => i == 1 ? "Can you explain how consistent hashing works?" : "Second utterance continues.";
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System, seed: 1);
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1, 6000, "question sent");
        await h.SpeakAsync(AudioSourceKind.System, seed: 2);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Count(s => s.IsFinal) == 2, 6000, "captions continue");
        Assert.True(h.Session.Assistant.IsBusy);
    }

    [Fact]
    public async Task InterviewQuestionProducesAnswerEndToEnd()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.TextFor = _ => "Can you explain how a B-tree index works?";
        h.Model.Respond = r =>
        {
            var segId = System.Text.RegularExpressions.Regex.Match(r.Input, "seg-\\d+").Value;
            var qid = System.Text.RegularExpressions.Regex.Match(r.Input, "question_id=\"(q-\\d+)\"").Groups[1].Value;
            return Json.Answer(qid, 1, [segId], spoken: "A B-tree keeps keys sorted in wide pages...");
        };
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer");
        var card = h.Session.Board.Cards.Single();
        Assert.Equal(CardKind.Answer, card.Kind);
        Assert.True(card.SpeakerUncertain);
    }

    private const string ImplicitRequest = "我想听听你在上一个项目里做缓存设计的思路";

    private const string BTree = "Can you explain how a B-tree index works?";

    /// <summary>Pushes speech then <paramref name="silenceMs"/> of silence through the system capture (fast, frame-timed).</summary>
    private static async Task PushAsync(Harness h, double speechSeconds, int silenceMs, int seed = 1, long? startQpc = null)
    {
        var src = h.Capture.Latest(AudioSourceKind.System)!;
        var audio = Signals.Concat(Signals.Speech(TimeSpan.FromSeconds(speechSeconds), seed), Signals.Silence(TimeSpan.FromMilliseconds(silenceMs)));
        long start = startQpc ?? h.Session.Clock.Now100ns();
        int chunk = PipelineAudio.Samples(TimeSpan.FromMilliseconds(10));
        for (int i = 0, n = 0; i < audio.Length; i += chunk, n++)
        {
            src.Push(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)).ToArray(), start + PipelineAudio.Duration(i).Ticks);
            if (n % 10 == 9)
            {
                await Task.Delay(15);
            }
        }
    }

    [Fact]
    public async Task AnswerStartsFromTheProvisionalCaptionBeforeTheFinalTranscript()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.AutoTranscribe = false;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System); // speech, then silence: the turn is committed
        var stream = h.Stream(AudioSourceKind.System, 0);
        await Wait.UntilAsync(() => stream.Commits == 1, because: "commit");
        stream.Emit(new TranscriptionDelta("item_1", BTree));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1, 3000, "early answer request");
        Assert.False(h.Session.Transcript.Snapshot().Single(s => s.Kind == SegmentKind.Speech).IsFinal);

        stream.Emit(new TranscriptionCompleted("item_1", BTree, null));
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer");
        await Task.Delay(600); // past the debounce: an equal final must not regenerate
        Assert.Single(h.Model.Requests);
        Assert.Equal(BTree, h.Session.Board.Cards.Single().QuestionText);
    }

    [Fact]
    public async Task SpeakerContinuingAfterAQuestionPauseGetsTheFullQuestionAnswered()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.AutoTranscribe = false;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        long start = h.Session.Clock.Now100ns();
        await PushAsync(h, 1.2, 400, startQpc: start); // a 400 ms pause: reported, but the turn stays open
        var stream = h.Stream(AudioSourceKind.System, 0);
        stream.Emit(new TranscriptionDelta("item_1", "Can you explain caching?"));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1, 3000, "early answer request during the pause");
        Assert.Equal(0, stream.Commits);

        // The interviewer continues in the same turn, then stops.
        await PushAsync(h, 1.0, 1000, seed: 2, startQpc: start + TimeSpan.FromSeconds(1.6).Ticks);
        await Wait.UntilAsync(() => stream.Commits == 1, because: "commit");
        const string full = "Can you explain caching? For example in a web shop.";
        stream.Emit(new TranscriptionDelta("item_1", " For example in a web shop."));
        stream.Emit(new TranscriptionCompleted("item_1", full, null));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 2, 3000, "regeneration with the final text");
        Assert.Contains("For example in a web shop.", h.Model.Requests[1].Input);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer");
        Assert.Equal(full, h.Session.Board.Cards.Single().QuestionText);
    }

    [Fact]
    public async Task ProvisionalTextWithoutAQuestionMarkWaitsForTheFinal()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.AutoTranscribe = false;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        var stream = h.Stream(AudioSourceKind.System, 0);
        await Wait.UntilAsync(() => stream.Commits == 1, because: "commit");
        stream.Emit(new TranscriptionDelta("item_1", "Can you explain how a B-tree index"));
        await Task.Delay(700);
        Assert.Empty(h.Model.Requests);
        stream.Emit(new TranscriptionCompleted("item_1", BTree, null));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1, 3000, "answer after the final");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "fast")]
    public async Task FastModeSettingSelectsTheServiceTierOfAnswerRequests(bool fast, string? tier)
    {
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview, AnswerFastMode = fast });
        h.Transcription.Configure = s => s.TextFor = _ => BTree;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Model.Requests.Count > 0, 8000, "answer request");
        Assert.Equal(tier, h.Model.Requests[0].ServiceTier);
    }

    [Fact]
    public async Task AnswerRequestStartsWellUnderASecondAfterTheFinalCaption()
    {
        await using var h = new Harness();
        h.Transcription.Configure = s => s.TextFor = _ => "Can you explain how a B-tree index works?";
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Model.Requests.Count > 0, 8000, "answer request");
        var seg = h.Session.Transcript.Snapshot().Single(s => s.Kind == SegmentKind.Speech);
        var wait = h.Model.RequestTimes[0] - seg.FinalizedUtc!.Value;
        // 300 ms debounce, evaluated when it elapses (previously 1 s debounce + up to 250 ms tick).
        Assert.InRange(wait.TotalMilliseconds, 250, 800);
    }

    [Fact]
    public async Task ConnectionIsWarmedAtStartAndWhenSomeoneElseStartsTalking()
    {
        await using var h = new Harness();
        await h.Session.StartAsync();
        await Wait.UntilAsync(() => h.Model.WarmUps.Count == 1, because: "warm-up at start");
        Assert.Equal("gpt-6-sol", h.Model.WarmUps[0]);
        await h.SpeakAsync(AudioSourceKind.System);
        await Task.Delay(200);
        Assert.Single(h.Model.WarmUps); // throttled: the start warm-up is recent
        await h.Session.StopAsync();
        await h.Session.StartAsync(); // a new session warms up again
        await Wait.UntilAsync(() => h.Model.WarmUps.Count == 2, because: "warm-up at second start");
    }

    private static Func<AssistantModelRequest, string> AnswerAny => r =>
    {
        var segId = System.Text.RegularExpressions.Regex.Match(r.Input, "seg-\\d+").Value;
        var q = System.Text.RegularExpressions.Regex.Match(r.Input, "question_id=\"(q-\\d+)\" revision=\"(\\d+)\"");
        return Json.Answer(q.Groups[1].Value, int.Parse(q.Groups[2].Value), [segId], spoken: "缓存就是把常用的东西放在手边……");
    };

    [Fact]
    public async Task QuestionTheLocalRulesMissAnsweredAfterAiIntentCheck()
    {
        var intent = new FakeIntentClassifier();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview }, intent);
        h.Transcription.Configure = s => s.TextFor = _ => ImplicitRequest;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer after AI intent check");
        var req = Assert.Single(intent.Requests);
        Assert.Equal(ImplicitRequest, req.Text);
        Assert.Equal("gpt-6-luna", req.Model);
        Assert.Equal("none", req.ReasoningEffort);
        Assert.Equal(ImplicitRequest, h.Session.Board.Cards.Single().QuestionText);
    }

    [Fact]
    public async Task ConfidentQuestionsSkipTheAiCheck()
    {
        var intent = new FakeIntentClassifier();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview }, intent);
        h.Transcription.Configure = s => s.TextFor = _ => "Can you explain how a B-tree index works?";
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer");
        Assert.Empty(intent.Requests);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("fail")]
    [InlineData("timeout")]
    [InlineData("disabled")]
    public async Task NegativeFailedOrDisabledAiCheckProducesNoAnswer(string mode)
    {
        var intent = new FakeIntentClassifier();
        switch (mode)
        {
            case "no": intent.Respond = _ => new IntentVerdict(false, 0.9, null); break;
            case "fail": intent.Fail = new ProviderException(ProviderFailureKind.RateLimited, "rate_limit_exceeded", "limited"); break;
            case "timeout": intent.Delay = TimeSpan.FromSeconds(10); break;
        }
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview, AiQuestionDetection = mode != "disabled" }, intent);
        h.Transcription.Configure = s => s.TextFor = _ => ImplicitRequest;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal), because: "caption");
        await Task.Delay(mode == "timeout" ? 7000 : 2000);
        Assert.Empty(h.Session.Board.Cards);
        Assert.Empty(h.Model.Requests);
        Assert.Equal(mode == "disabled" ? 0 : 1, intent.Requests.Count);
        Assert.Equal(SessionState.Listening, h.Session.State);
    }

    [Fact]
    public async Task MeetingModeAsksTheAiWhenTheLocalRulesFindNothing()
    {
        var intent = new FakeIntentClassifier();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting, MeetingAnalysisIntervalSeconds = 4 }, intent);
        h.Transcription.Configure = s => s.TextFor = _ => ImplicitRequest;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Model.Requests.Count > 0, 12000, "meeting review after AI intent check");
        Assert.Equal(ImplicitRequest, Assert.Single(intent.Requests).Text);
        Assert.Contains("Question", h.Model.Requests[0].Input);
    }

    [Fact]
    public async Task StoppingSavesTranscriptAndAnswersToHistory()
    {
        var archive = new FakeSessionArchive();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Interview }, archive: archive);
        h.Transcription.Configure = s => s.TextFor = _ => BTree;
        h.Model.Respond = AnswerAny;
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Board.Cards.Any(c => c.State == CardState.Complete), 8000, "answer");
        await h.Session.StopAsync();

        var summary = Assert.Single(archive.List());
        Assert.Equal(h.Session.ArchiveId, summary.Id);
        Assert.NotNull(summary.EndedUtc);
        Assert.Equal(Scenario.Interview, summary.Scenario);
        Assert.Equal(BTree, summary.Title);
        Assert.Equal(1, summary.AnswerCount);
        var record = archive.Load(summary.Id)!;
        Assert.Equal(BTree, Assert.Single(record.Lines).Text);
        var card = Assert.Single(record.Cards);
        Assert.Equal(CardKind.Answer, card.Kind);
        Assert.False(string.IsNullOrWhiteSpace(card.SpokenAnswer));
    }

    [Fact]
    public async Task StartingANewSessionKeepsThePreviousOneInHistory()
    {
        var archive = new FakeSessionArchive();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting }, archive: archive);
        h.Transcription.Configure = s => s.TextFor = _ => "first session line";
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal), because: "first caption");
        await h.Session.StopAsync();
        string first = h.Session.ArchiveId!;

        h.Transcription.Configure = s => s.TextFor = _ => "second session line";
        await h.Session.StartAsync();
        Assert.Empty(h.Session.Transcript.Snapshot().Where(s => s.Kind == SegmentKind.Speech)); // the live view starts empty
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal), because: "second caption");
        await h.Session.StopAsync();

        Assert.NotEqual(first, h.Session.ArchiveId);
        Assert.Equal(2, archive.List().Count);
        Assert.Equal("first session line", archive.Load(first)!.Lines.Single().Text);
        Assert.Equal("second session line", archive.Load(h.Session.ArchiveId!)!.Lines.Single().Text);
    }

    [Fact]
    public async Task RunningSessionIsAutosavedAndCompletedOnStop()
    {
        var archive = new FakeSessionArchive();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting }, archive: archive);
        h.Session.ArchiveAutosaveInterval = TimeSpan.FromMilliseconds(300);
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => archive.List().Count == 1, 5000, "autosave while listening");
        Assert.Null(archive.List()[0].EndedUtc);
        await h.Session.StopAsync();
        var summary = Assert.Single(archive.List());
        Assert.NotNull(summary.EndedUtc);
    }

    [Theory]
    [InlineData(true, false)]   // history on, but nothing was said
    [InlineData(false, true)]   // speech, but history turned off
    public async Task EmptySessionsAndDisabledHistoryAreNotSaved(bool historyOn, bool speak)
    {
        var archive = new FakeSessionArchive();
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting, SaveSessionHistory = historyOn }, archive: archive);
        await h.Session.StartAsync();
        if (speak)
        {
            await h.SpeakAsync(AudioSourceKind.System);
            await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal), because: "caption");
        }
        await h.Session.StopAsync();
        Assert.Empty(archive.List());
        Assert.Equal(0, archive.Saves);
    }

    [Fact]
    public async Task HistoryWriteFailureDoesNotBreakStop()
    {
        var archive = new FakeSessionArchive { FailWith = new IOException("disk full") };
        await using var h = new Harness(new AppSettings { Scenario = Scenario.Meeting }, archive: archive);
        await h.Session.StartAsync();
        await h.SpeakAsync(AudioSourceKind.System);
        await Wait.UntilAsync(() => h.Session.Transcript.Snapshot().Any(s => s.IsFinal), because: "caption");
        await h.Session.StopAsync();
        Assert.Equal(SessionState.Stopped, h.Session.State);
        archive.FailWith = null;
        await h.Session.StartAsync(); // the unsaved session is retried before the new one starts
        Assert.Single(archive.List());
    }

    [Fact]
    public async Task TenStartStopCyclesLeaveNoActiveCapturesOrStreams()
    {
        await using var h = new Harness();
        for (int i = 0; i < 10; i++)
        {
            await h.Session.StartAsync();
            Assert.Equal(SessionState.Listening, h.Session.State);
            await h.Session.StopAsync();
        }
        Assert.Equal(10, h.Transcription.Connects);
        Assert.All(h.Capture.Created, c => Assert.True(c.Stopped));
        Assert.All(h.Transcription.Streams, s => Assert.True(s.Disposed));
    }
}
