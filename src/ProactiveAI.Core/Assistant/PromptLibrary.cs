using System.Reflection;

namespace ProactiveAI.Core.Assistant;

/// <summary>Loads versioned runtime prompt resources embedded in the Core assembly.</summary>
public static class PromptLibrary
{
    // 1.1: answer text (direct_position, spoken_answer) streams right after the decision fields.
    // 1.2: session-long question memory (session_log, related_exchanges) and follows_up_on after direct_position.
    // 1.3: speakable answers about the user's own work; supplied details listed in filled_in_details (user decision 2026-10-17).
    // 1.4: the user's résumé (interview mode) as facts about the user.
    public const string Version = "assistant-v1.5";
    public const string SchemaName = "assistant_result_v1";

    private static readonly Lazy<string> InstructionsTemplate = new(() => Load("assistant-instructions.v1.md"));
    private static readonly Lazy<string> SchemaJson = new(() => Load("assistant-result.v1.schema.json"));

    public static string Instructions => InstructionsTemplate.Value;

    public static string Schema => SchemaJson.Value;

    private static string Load(string name)
    {
        var asm = typeof(PromptLibrary).Assembly;
        using var stream = asm.GetManifestResourceStream("ProactiveAI.Prompts." + name)
                           ?? throw new InvalidOperationException($"Missing prompt resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
