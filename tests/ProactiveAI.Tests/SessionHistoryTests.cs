using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Infrastructure.History;
using ProactiveAI.Infrastructure.Settings;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class SessionHistoryTests
{
    private static SessionRecord Sample(string id, DateTimeOffset started, string question = "Why did you choose PostgreSQL over MongoDB?")
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var speakers = new SpeakerRegistry();
        var interviewer = speakers.CreateDiarized(AudioSourceKind.System, false);
        speakers.Rename(interviewer.Id, "Interviewer");
        speakers.EnsureSelfAlias();
        var q = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, 1), question, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6),
            new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.Assigned, interviewer.Id, [], 0, null)), "i1")!;
        store.ApplyFinal(new TurnKey(1, AudioSourceKind.Microphone, 1, 2), "Mainly for transactions and joins.", TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10),
            new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, null)), "i2");
        var echo = store.ApplyFinal(new TurnKey(1, AudioSourceKind.Microphone, 1, 3), question, TimeSpan.FromSeconds(3.1), TimeSpan.FromSeconds(6.1),
            AudioAttributionDefault.Pending, "i3")!;
        store.MarkDuplicate(echo.Id, q.Id, "echo");
        store.AddMarker(SegmentKind.Gap, AudioSourceKind.System, 1, 1, TimeSpan.FromSeconds(7), "Paused");
        var cards = new List<AssistantCard>
        {
            new() { Id = "card-1", Kind = CardKind.Answer, State = CardState.Complete, QuestionText = question, DirectPosition = "Relational guarantees mattered most.",
                    SpokenAnswer = "We needed multi-row transactions…", SupportingPoints = ["ACID"], CreatedUtc = started.AddSeconds(7) },
            new() { Id = "card-2", Kind = CardKind.NoAction, State = CardState.Complete, CreatedUtc = started.AddSeconds(8) },
        };
        return SessionRecordBuilder.Build(id, started, started.AddMinutes(5), TimeSpan.FromMinutes(5), Scenario.Interview, store.Snapshot(), cards, speakers);
    }

    [Fact]
    public void RecordKeepsSpokenLinesAndAnswersOnly()
    {
        var r = Sample("abc", DateTimeOffset.UtcNow);
        Assert.Equal(2, r.Lines.Count); // echo duplicate and gap marker are not saved
        Assert.Equal("Interviewer", r.Lines[0].Speaker);
        Assert.False(r.Lines[0].IsSelf);
        Assert.Equal("Me (microphone)", r.Lines[1].Speaker);
        Assert.True(r.Lines[1].IsSelf);
        var card = Assert.Single(r.Cards); // "no action" cards are not saved
        Assert.Equal("card-1", card.Id);
        Assert.Equal("Why did you choose PostgreSQL over MongoDB?", r.Summary.Title);
        Assert.Equal(2, r.Summary.LineCount);
        Assert.Equal(1, r.Summary.AnswerCount);
    }

    [Fact]
    public void TitleFallsBackToTheFirstLineAndIsShortened()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        string longText = string.Join(' ', Enumerable.Repeat("word", 60));
        store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, 1), longText, TimeSpan.Zero, TimeSpan.FromSeconds(9), AudioAttributionDefault.Pending, "i1");
        var r = SessionRecordBuilder.Build("x", DateTimeOffset.UtcNow, null, TimeSpan.FromSeconds(10), Scenario.Meeting, store.Snapshot(), [], new SpeakerRegistry());
        Assert.True(r.Summary.Title.Length <= SessionRecordBuilder.MaxTitleChars);
        Assert.EndsWith("…", r.Summary.Title);
        Assert.Equal(0, r.Summary.AnswerCount);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "pai-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FilledInDetailsAreSavedAndOlderRecordsWithoutThemStillLoad()
    {
        var card = new AssistantCard
        {
            Id = "card-1", Kind = CardKind.Answer, State = CardState.Complete, QuestionText = "How did you measure it?", DirectPosition = "With a replay load test.",
            SpokenAnswer = "We replayed a week of production traffic…", FilledInDetails = ["Replayed one week of production traffic"], CreatedUtc = DateTimeOffset.UtcNow,
        };
        var record = SessionRecordBuilder.Build("f1", DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(1), Scenario.Interview, [], [card], new SpeakerRegistry());
        Assert.Equal(["Replayed one week of production traffic"], record.Cards[0].FilledInDetails);

        // Records saved before the field existed have no "filledInDetails" property.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(record, options))!;
        node["cards"]![0]!.AsObject().Remove("filledInDetails");
        var old = node.Deserialize<SessionRecord>(options)!;
        Assert.Null(old.Cards[0].FilledInDetails); // the history view treats null as "none"
    }

    [Fact]
    public void ArchiveRoundTripsAndEncryptsAtRest()
    {
        var dir = TempDir();
        try
        {
            var archive = new EncryptedSessionArchive(dir, NullLogger<EncryptedSessionArchive>.Instance);
            var older = Sample("a1", DateTimeOffset.UtcNow.AddHours(-2), "Explain consistent hashing in your own words?");
            var newer = Sample("b2", DateTimeOffset.UtcNow);
            archive.Save(older);
            archive.Save(newer);

            var list = archive.List();
            Assert.Equal(["b2", "a1"], list.Select(s => s.Id)); // newest first
            var loaded = archive.Load("a1")!;
            Assert.Equal(older.Summary, loaded.Summary);
            Assert.Equal(older.Lines, loaded.Lines);
            Assert.Equal(older.Cards[0].SpokenAnswer, loaded.Cards[0].SpokenAnswer);
            Assert.Equal(older.Cards[0].SupportingPoints, loaded.Cards[0].SupportingPoints);

            foreach (var file in Directory.GetFiles(dir))
            {
                var bytes = File.ReadAllBytes(file);
                foreach (var secret in new[] { "consistent hashing", "PostgreSQL", "Interviewer" })
                {
                    Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(secret)), $"{Path.GetFileName(file)} contains plaintext '{secret}'");
                    Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(secret)), $"{Path.GetFileName(file)} contains UTF-16 plaintext '{secret}'");
                }
            }

            // Saving again replaces the record (autosave, then final save).
            archive.Save(newer with { Summary = newer.Summary with { Title = "Updated" } });
            Assert.Equal(2, archive.List().Count);
            Assert.Equal("Updated", archive.Load("b2")!.Summary.Title);

            Assert.True(archive.Delete("a1"));
            Assert.Null(archive.Load("a1"));
            Assert.Equal(["b2"], archive.List().Select(s => s.Id));
            archive.DeleteAll();
            Assert.Empty(archive.List());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void DamagedEntriesAreSkippedAndIdsCannotEscapeTheFolder()
    {
        var dir = TempDir();
        try
        {
            var archive = new EncryptedSessionArchive(dir, NullLogger<EncryptedSessionArchive>.Instance);
            archive.Save(Sample("good", DateTimeOffset.UtcNow));
            File.WriteAllBytes(Path.Combine(dir, "broken.summary"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(dir, "broken.session"), [1, 2, 3, 4]);
            Assert.Equal(["good"], archive.List().Select(s => s.Id));
            Assert.Null(archive.Load("broken"));
            Assert.Null(archive.Load("..\\..\\settings"));
            Assert.False(archive.Delete("..\\x"));
            Assert.Throws<ArgumentException>(() => archive.Save(Sample("../evil", DateTimeOffset.UtcNow)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void MicrophoneAndHistoryAreOnByDefaultIncludingForOlderSettingsFiles()
    {
        var s = new AppSettings();
        Assert.True(s.MicrophoneEnabled);
        Assert.True(s.SaveSessionHistory);
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(new { schemaVersion = 2, sensitivity = 2 }));
            var loaded = new JsonSettingsStore(dir, NullLogger<JsonSettingsStore>.Instance).Load();
            Assert.True(loaded.MicrophoneEnabled);
            Assert.True(loaded.SaveSessionHistory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class DeviceCheckTests
{
    private static (DeviceCheck Check, FakeCaptureFactory Capture, FakeTestSoundPlayer Player) Create()
    {
        var capture = new FakeCaptureFactory();
        var player = new FakeTestSoundPlayer();
        return (new DeviceCheck(capture, player, new FakeDeviceCatalog(), NullLogger<DeviceCheck>.Instance), capture, player);
    }

    private static void Push(FakeCaptureSource src, short[] samples)
    {
        int chunk = PipelineAudio.Samples(TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < samples.Length; i += chunk)
        {
            src.Push(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)).ToArray(), 0);
        }
    }

    [Fact]
    public async Task MicrophoneLevelAndSpeechAreReportedFromRawCaptures()
    {
        var (check, capture, _) = Create();
        await using var _check = check;
        var levels = new List<DeviceLevels>();
        check.LevelsChanged += l => { lock (levels) { levels.Add(l); } };
        await check.StartAsync(null, microphone: true, null);

        var mic = capture.Latest(AudioSourceKind.Microphone)!;
        var loopback = capture.Latest(AudioSourceKind.System)!;
        Assert.Equal("mic-1", mic.Request.DeviceId);
        Assert.False(mic.Request.UseCommunicationsProcessing);
        Assert.Equal("spk-1", loopback.Request.DeviceId);
        Assert.True(mic.Started && loopback.Started);
        Assert.Equal("Microphone", check.MicrophoneName);

        Push(mic, Signals.Speech(TimeSpan.FromMilliseconds(300), 1));
        await Wait.UntilAsync(() => { lock (levels) { return levels.Any(l => l.MicrophoneHeardSpeech && l.MicrophoneDbfs > DeviceCheck.SpeechDbfs); } }, 3000, "speech level");

        await check.StopAsync();
        Assert.True(mic.Stopped && loopback.Stopped);
    }

    [Fact]
    public async Task SpeakerTestPassesWhenSystemCaptureHearsTheSound()
    {
        var (check, capture, player) = Create();
        await using var _check = check;
        await check.StartAsync(null, microphone: true, null);
        var mic = capture.Latest(AudioSourceKind.Microphone)!;
        Push(mic, Signals.Silence(TimeSpan.FromMilliseconds(500))); // quiet room before the test
        player.OnPlay = _ =>
        {
            var chime = Signals.Speech(TimeSpan.FromMilliseconds(400), 2);
            Push(capture.Latest(AudioSourceKind.System)!, chime);
            Push(mic, Signals.Scale(chime, 0.3)); // laptop microphone hears the speakers
            return Task.CompletedTask;
        };
        var r = await check.TestSpeakersAsync(CancellationToken.None);
        Assert.Equal(["spk-1"], player.Played);
        Assert.True(r.Played);
        Assert.True(r.CapturedBySystemAudio);
        Assert.True(r.CapturePeakDbfs > DeviceCheck.CaptureThresholdDbfs);
        Assert.True(r.HeardByMicrophone);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task SpeakerTestReportsSilentCaptureAndPlaybackErrors()
    {
        var (check, _, player) = Create();
        await using var _check = check;
        await check.StartAsync(null, microphone: false, null);
        var silent = await check.TestSpeakersAsync(CancellationToken.None);
        Assert.True(silent.Played);
        Assert.False(silent.CapturedBySystemAudio);
        Assert.False(silent.HeardByMicrophone);

        player.FailWith = new InvalidOperationException("device busy");
        var failed = await check.TestSpeakersAsync(CancellationToken.None);
        Assert.False(failed.Played);
        Assert.NotNull(failed.Error);
    }

    [Fact]
    public async Task MicrophoneFailureIsReportedWhileSystemAudioStillWorks()
    {
        var (check, capture, _) = Create();
        await using var _check = check;
        capture.FailWith = r => r.Kind == AudioSourceKind.Microphone ? new InvalidOperationException("Microphone access is denied.") : null;
        await check.StartAsync(null, microphone: true, null);
        Assert.Equal("Microphone access is denied.", check.MicrophoneError);
        Assert.Null(check.SystemError);
        Assert.True(capture.Latest(AudioSourceKind.System)!.Started);
    }
}
