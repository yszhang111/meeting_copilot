using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Context;

namespace ProactiveAI.Probe;

/// <summary>Extracts a résumé file exactly like the app does and prints the text and its token size. No API calls.</summary>
public static class ResumeCheck
{
    public static async Task<int> RunAsync(Options o, IReadOnlyList<string> paths)
    {
        await using var sp = Host.Build();
        var extractor = sp.GetRequiredService<IDocumentTextExtractor>();
        var tokens = sp.GetRequiredService<ITokenCounter>();
        foreach (var path in paths)
        {
            Console.WriteLine($"=== {Path.GetFileName(path)}");
            try
            {
                var r = await extractor.ExtractAsync(path, CancellationToken.None);
                var prep = new PreparationNotes();
                prep.SetResume(new Resume(new ResumeInfo("probe", Path.GetFileName(path), r.Format, r.Text.Length, DateTimeOffset.UtcNow), r.Text));
                int chunks = prep.Excerpts().Count;
                Console.WriteLine($"format={r.Format} chars={r.Text.Length} tokens={tokens.Count(r.Text)} excerpts={chunks} note={r.Note ?? "-"}");
                if (!o.Has("quiet"))
                {
                    Console.WriteLine(r.Text);
                }
            }
            catch (DocumentReadException ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
            }
            Console.WriteLine();
        }
        return 0;
    }
}
