using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Settings;
using ProactiveAI.Infrastructure.Settings;

namespace ProactiveAI.Tests;

public class LatencySettingsTests
{
    [Fact]
    public void DefaultsUseTheFastAnswerPath()
    {
        var s = new AppSettings().Normalize();
        Assert.Equal("gpt-6-sol", s.AnswerModel);
        Assert.Equal("none", s.AnswerReasoningEffort);
        Assert.Equal(300, s.QuestionDebounceMilliseconds);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void OldDefaultsAreMigratedButUserChoicesAreKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pai-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(dir, NullLogger<JsonSettingsStore>.Instance);
            string Write(object v1)
            {
                var json = JsonSerializer.Serialize(v1, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                File.WriteAllText(Path.Combine(dir, "settings.json"), json);
                return json;
            }

            Write(new { schemaVersion = 1, answerModel = "gpt-6.1-sol", answerReasoningEffort = "low", questionDebounceMilliseconds = 1000, sensitivity = 3 });
            var migrated = store.Load();
            Assert.Equal("gpt-6-sol", migrated.AnswerModel);
            Assert.Equal("none", migrated.AnswerReasoningEffort);
            Assert.Equal(300, migrated.QuestionDebounceMilliseconds);
            Assert.Equal(3, migrated.Sensitivity); // unrelated settings untouched
            Assert.Equal(2, migrated.SchemaVersion);

            Write(new { schemaVersion = 1, answerModel = "gpt-6-astra", answerReasoningEffort = "medium", questionDebounceMilliseconds = 1500 });
            var custom = store.Load();
            Assert.Equal("gpt-6-astra", custom.AnswerModel);
            Assert.Equal("medium", custom.AnswerReasoningEffort);
            Assert.Equal(1500, custom.QuestionDebounceMilliseconds);

            // Saved v2 settings are not migrated again (a user who chooses 6.1 Sol / 1000 ms keeps it).
            store.Save(custom with { AnswerModel = "gpt-6.1-sol", AnswerReasoningEffort = "low", QuestionDebounceMilliseconds = 1000 });
            var kept = store.Load();
            Assert.Equal("gpt-6.1-sol", kept.AnswerModel);
            Assert.Equal(1000, kept.QuestionDebounceMilliseconds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("gpt-6.1-sol", "none", "low")]
    [InlineData("gpt-6-astra", "none", "low")]
    [InlineData("gpt-6-sol", "none", "none")]
    [InlineData("gpt-6-luna", "none", "none")]
    [InlineData("gpt-6.1-sol", "medium", "medium")]
    public void UnsupportedNoneEffortFallsBackToLow(string model, string effort, string expected)
    {
        var s = new AppSettings { AnswerModel = model, AnswerReasoningEffort = effort, IntentModel = model, IntentReasoningEffort = effort }.Normalize();
        Assert.Equal(expected, s.AnswerReasoningEffort);
        Assert.Equal(expected, s.IntentReasoningEffort);
    }

    [Fact]
    public void AnswerTextIsGeneratedRightAfterTheDecisionFields()
    {
        // Structured output is produced in schema property order: the text the user reads must not wait for ids/title.
        // The short follow-up link comes right after the first visible text so it is kept even if the answer is interrupted.
        var props = JsonNode.Parse(PromptLibrary.Schema)!["properties"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(["response_kind", "should_notify", "direct_position", "follows_up_on", "spoken_answer", "filled_in_details"], props.Take(6));
        Assert.True(props.IndexOf("spoken_answer") < props.IndexOf("title"));
        Assert.True(props.IndexOf("spoken_answer") < props.IndexOf("question_id"));
    }
}
