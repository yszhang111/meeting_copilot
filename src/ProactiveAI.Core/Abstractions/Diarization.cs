namespace ProactiveAI.Core.Abstractions;

/// <summary>A short reference clip used to keep speaker identity stable across diarization windows.</summary>
public sealed record SpeakerReference(string Name, short[] Pcm);

public sealed record DiarizationRequest(string Model, short[] Pcm, IReadOnlyList<SpeakerReference> References, IReadOnlyList<string> Languages);

/// <summary>A provider segment; times are seconds relative to the start of the submitted window.</summary>
public sealed record DiarizedSpan(string RawSpeaker, double StartSeconds, double EndSeconds, string Text);

public sealed record DiarizationResult(IReadOnlyList<DiarizedSpan> Spans, UsageInfo? Usage);

public interface ISpeakerDiarizer
{
    Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken);
}
