using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Settings;

namespace ProactiveAI.Probe;

/// <summary>UI verification helper: writes (or deletes) one clearly labeled sample session in the local history.</summary>
public static class HistorySample
{
    public const string Id = "probe-sample-session";

    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var archive = sp.GetRequiredService<ISessionArchive>();
        if (o.Has("delete"))
        {
            Console.WriteLine(archive.Delete(Id) ? "Sample session deleted." : "No sample session found.");
            return 0;
        }
        var started = DateTimeOffset.UtcNow.AddMinutes(-12);
        HistoryLine Line(int i, double at, string speaker, bool self, string text) =>
            new($"l{i}", self ? AudioSourceKind.Microphone : AudioSourceKind.System, TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(at + 4), speaker, self, text, true);
        var lines = new List<HistoryLine>
        {
            Line(1, 3, "Speaker A", false, "[Sample] Thanks for joining. Let's start with your background in distributed systems."),
            Line(2, 9, "Me (microphone)", true, "[Sample] I have worked on payment infrastructure for three years."),
            Line(3, 16, "Speaker A", false, "[Sample] How would you explain caching to someone without a technical background?"),
        };
        var cards = new List<HistoryCard>
        {
            new("c1", CardKind.Answer, CardState.Complete, lines[2].Text, null,
                "[Sample] Caching keeps things you use often close at hand, so you don't fetch them from far away every time.",
                "[Sample] Imagine keeping the spices you cook with every day on the counter instead of in the basement…",
                null, null, ["Faster repeated access", "Less load on the source"], ["Cached data can become stale"],
                [new FollowUp("When would caching hurt?", "When data changes often and stale answers are costly.")], [], ["l3"], started.AddSeconds(20), "gpt-6-sol"),
        };
        var summary = new SessionSummary(Id, started, started.AddMinutes(10), TimeSpan.FromMinutes(10), Scenario.Interview,
            "[Sample] How would you explain caching to someone without a technical background?", lines.Count, 1);
        archive.Save(new SessionRecord(SessionRecord.CurrentVersion, summary, lines, cards));
        Console.WriteLine("Sample session written to history (delete with: history-sample --delete).");
        return 0;
    }
}
