using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Questions;
using ProactiveAI.Infrastructure.Logging;
using ProactiveAI.Infrastructure.OpenAI;
using ProactiveAI.Infrastructure.Security;
using ProactiveAI.Infrastructure.Text;

namespace ProactiveAI.Tests;

public class InfrastructureTests
{
    [Fact]
    public void RealtimeEventsMapToTranscriptionEvents()
    {
        Assert.IsType<TranscriptionCommitted>(RealtimeTranscriptionStream.Map(JsonNode.Parse("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"item_1\",\"previous_item_id\":null}")!));
        var d = Assert.IsType<TranscriptionDelta>(RealtimeTranscriptionStream.Map(JsonNode.Parse("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_3\",\"content_index\":0,\"delta\":\"Hello,\"}")!));
        Assert.Equal("Hello,", d.Delta);
        var c = Assert.IsType<TranscriptionCompleted>(RealtimeTranscriptionStream.Map(JsonNode.Parse("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"item_3\",\"content_index\":0,\"transcript\":\"Hello, how are you?\"}")!));
        Assert.Equal("item_3", c.ItemId);
        var e = Assert.IsType<TranscriptionError>(RealtimeTranscriptionStream.Map(JsonNode.Parse("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\",\"message\":\"Incorrect API key provided: sk-abc12345678\",\"event_id\":\"commit-7\"}}")!));
        Assert.True(e.Fatal);
        Assert.Equal("commit-7", e.ClientEventId);
        Assert.DoesNotContain("sk-abc12345678", e.SanitizedMessage);
        Assert.Null(RealtimeTranscriptionStream.Map(JsonNode.Parse("{\"type\":\"rate_limits.updated\"}")!));
    }

    [Fact]
    public void DiarizedJsonParses()
    {
        string json = """
        {"task":"transcribe","duration":27.4,"text":"...","segments":[
          {"type":"transcript.text.segment","id":"seg_001","start":0.0,"end":4.7,"text":"Thanks for calling.","speaker":"agent"},
          {"type":"transcript.text.segment","id":"seg_002","start":4.7,"end":11.8,"text":"Hi.","speaker":"A"}],
         "usage":{"type":"duration","seconds":27}}
        """;
        var r = OpenAIDiarizer.Parse(json);
        Assert.Equal(2, r.Spans.Count);
        Assert.Equal("A", r.Spans[1].RawSpeaker);
        Assert.Equal(27, r.Usage!.AudioSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fast")]
    public async Task ResponsesRequestCarriesTheServiceTierOnlyWhenRequested(string? tier)
    {
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"{}\"}\n\n" +
                      "data: {\"type\":\"response.completed\",\"response\":{\"service_tier\":\"fast\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(sse, Encoding.UTF8, "text/event-stream") };
        });
        var http = new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri(OpenAIConnection.ApiBase) };
        var model = new OpenAIResponsesModel(new OpenAIConnection(new Fakes.FakeCredentials(), http), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIResponsesModel>.Instance);
        var updates = new List<AssistantStreamUpdate>();
        await foreach (var u in model.StreamAsync(new AssistantModelRequest("gpt-6-sol", "i", "x", "s", "{\"type\":\"object\"}", 10, "none", null, tier), CancellationToken.None))
        {
            updates.Add(u);
        }
        var json = JsonNode.Parse(body!)!;
        Assert.Equal(tier, json["service_tier"]?.GetValue<string>());
        Assert.Equal("fast", Assert.IsType<AssistantCompleted>(updates[^1]).ServiceTier);
    }

    private sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> respond) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public void ResponsesStreamEventsAreHandled()
    {
        var sb = new StringBuilder();
        var delta = OpenAIResponsesModel.Handle("{\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"output_index\":0,\"content_index\":0,\"delta\":\"{\\\"a\\\"\",\"sequence_number\":1}", sb);
        Assert.IsType<AssistantTextDelta>(delta);
        var done = Assert.IsType<AssistantCompleted>(OpenAIResponsesModel.Handle("{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"output_tokens_details\":{\"reasoning_tokens\":2}}}}", sb));
        Assert.Equal("{\"a\"", done.Text);
        Assert.Equal(2, done.Usage!.ReasoningTokens);
        var inc = Assert.IsType<AssistantIncomplete>(OpenAIResponsesModel.Handle("{\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}", sb));
        Assert.Equal("max_output_tokens", inc.Reason);
        Assert.Throws<ProviderException>(() => OpenAIResponsesModel.Handle("{\"type\":\"error\",\"code\":\"rate_limit_exceeded\",\"message\":\"slow down\"}", sb));
    }

    [Theory]
    [InlineData(401, "invalid_api_key", ProviderFailureKind.Authentication)]
    [InlineData(429, "insufficient_quota", ProviderFailureKind.QuotaExceeded)]
    [InlineData(429, "rate_limit_exceeded", ProviderFailureKind.RateLimited)]
    [InlineData(404, "model_not_found", ProviderFailureKind.InvalidModel)]
    [InlineData(503, "server_is_overloaded", ProviderFailureKind.ServerError)]
    [InlineData(403, null, ProviderFailureKind.PermissionDenied)]
    public void HttpErrorsAreClassified(int status, string? code, ProviderFailureKind expected)
    {
        string body = code is null ? "{}" : JsonSerializer.Serialize(new { error = new { code, message = "msg with key sk-proj-ABCDEFGHIJKLMNOP", type = "x" } });
        var ex = OpenAIConnection.MapError(status, body);
        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("sk-proj-ABCDEFGHIJKLMNOP", ex.Message);
    }

    [Fact]
    public void LogRedactionRemovesSecrets()
    {
        Assert.Equal("Authorization: [redacted] x", BoundedFileLoggerProvider.Redact("Authorization: Bearer sk-abcdefghijklmnop x"));
    }

    [Fact]
    public void TokenizerCountsAndFallbackIsConservative()
    {
        var counter = new TiktokenTokenCounter();
        Assert.False(counter.UsesFallback);
        int real = counter.Count("你好，世界。Hello world, this is a test.");
        Assert.InRange(real, 5, 30);
        Assert.True(TiktokenTokenCounter.ConservativeBound("你好世界") >= counter.Count("你好世界"));
    }

    [Fact]
    public void CredentialStoreRoundTripsWithoutTouchingTheProductionEntry()
    {
        var store = new WindowsCredentialStore("MeetingCopilot/Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(store.HasApiKey());
            store.WriteApiKey("  sk-test-0123456789abcdefghij  ");
            Assert.True(store.HasApiKey());
            Assert.Equal("sk-test-0123456789abcdefghij", store.ReadApiKey());
        }
        finally
        {
            store.DeleteApiKey();
        }
        Assert.False(store.HasApiKey());
        store.DeleteApiKey(); // deleting a missing entry is safe
    }

    [Fact]
    public void PromptSchemaIsStrictCompatible()
    {
        using var doc = JsonDocument.Parse(Core.Assistant.PromptLibrary.Schema);
        var root = doc.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var props = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.True(props.SetEquals(required!), "strict mode requires every property to be listed as required");
        Assert.DoesNotContain("maxLength", Core.Assistant.PromptLibrary.Schema); // not in the documented strict subset
    }
}

public class QualityFixtureTests
{
    private sealed record Fixture(string Id, string Scenario, List<Line> Transcript, Expect Expect, string Task = "question");
    private sealed record Line(string Speaker, string Text, double At);
    private sealed record Expect(List<string> Kinds, bool? DetectQuestion);

    private static List<Fixture> Load() =>
        JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "quality", "fixtures.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void FixtureSetCoversRequiredCasesInChineseAndEnglish()
    {
        var f = Load();
        Assert.True(f.Count >= 12);
        Assert.Contains(f, x => x.Transcript.Any(l => Core.Text.TextTools.ContainsCjk(l.Text)));
        Assert.Contains(f, x => x.Scenario == "Defense");
        Assert.Contains(f, x => x.Scenario == "Interview");
        Assert.Contains(f, x => x.Id.Contains("injection"));
        Assert.Contains(f, x => x.Expect.Kinds.SequenceEqual(["none"]));
    }

    [Fact]
    public void DeterministicStageOneExpectationsHold()
    {
        foreach (var f in Load().Where(x => x.Expect.DetectQuestion is not null))
        {
            var text = string.Join(" ", f.Transcript.TakeLast(2).Select(l => l.Text));
            var s = QuestionHeuristics.AnalyzeTurn(text);
            Assert.True((s.IsQuestion && !s.LikelyRhetoricalOrQuoted) == f.Expect.DetectQuestion, f.Id);
        }
    }

    [Fact]
    public void InterviewAndDefenseQuestionsAreDetectedByStageOne()
    {
        foreach (var f in Load().Where(x => x.Scenario is "Interview" or "Defense" && !x.Id.Contains("injection")))
        {
            var last = f.Transcript[^1].Text;
            var s = QuestionHeuristics.AnalyzeTurn(last);
            Assert.True(s.IsQuestion && !s.LikelyRhetoricalOrQuoted, $"{f.Id}: {last}");
        }
    }
}
