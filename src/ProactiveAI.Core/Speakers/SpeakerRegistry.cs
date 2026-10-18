using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Speakers;

public enum SpeakerRole
{
    Unknown,
    Interviewer,
    Examiner,
    Participant,
    Me,
}

/// <summary>
/// A session-local acoustic speaker label. <see cref="DisplayName"/> is only ever user-entered;
/// real identities are never inferred. Labels are not persisted between sessions.
/// </summary>
public sealed record SessionSpeaker(
    string Id,
    AudioSourceKind Source,
    string Label,
    string? DisplayName,
    int ColorIndex,
    SpeakerRole Role,
    bool IsProvisional,
    bool IsSourceAlias)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Label : DisplayName!;
}

public sealed class SpeakerRegistry
{
    public const string SelfAliasId = "self-mic";

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionSpeaker> _speakers = new(StringComparer.Ordinal);
    private int _next;

    public event Action? Changed;

    public IReadOnlyList<SessionSpeaker> All
    {
        get { lock (_gate) { return _speakers.Values.OrderBy(s => s.ColorIndex).ToList(); } }
    }

    public SessionSpeaker? Get(string? id)
    {
        if (id is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _speakers.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<SessionSpeaker> ForSource(AudioSourceKind source)
    {
        lock (_gate)
        {
            return _speakers.Values.Where(s => s.Source == source && !s.IsSourceAlias).OrderBy(s => s.ColorIndex).ToList();
        }
    }

    public SessionSpeaker CreateDiarized(AudioSourceKind source, bool provisional)
    {
        SessionSpeaker speaker;
        lock (_gate)
        {
            int n = _next++;
            speaker = new SessionSpeaker($"spk-{n + 1}", source, $"Speaker {LetterFor(n)}", null, n, SpeakerRole.Unknown, provisional, false);
            _speakers[speaker.Id] = speaker;
        }
        Changed?.Invoke();
        return speaker;
    }

    public SessionSpeaker EnsureSelfAlias()
    {
        SessionSpeaker speaker;
        bool created = false;
        lock (_gate)
        {
            if (!_speakers.TryGetValue(SelfAliasId, out speaker!))
            {
                speaker = new SessionSpeaker(SelfAliasId, AudioSourceKind.Microphone, "Me (microphone)", null, -1, SpeakerRole.Me, false, true);
                _speakers[SelfAliasId] = speaker;
                created = true;
            }
        }
        if (created)
        {
            Changed?.Invoke();
        }
        return speaker;
    }

    public void Rename(string id, string? displayName)
    {
        Update(id, s => s with { DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()[..Math.Min(displayName.Trim().Length, 40)] });
    }

    public void SetRole(string id, SpeakerRole role) => Update(id, s => s with { Role = role });

    public void ConfirmIdentity(string id) => Update(id, s => s with { IsProvisional = false });

    public void Clear()
    {
        lock (_gate)
        {
            _speakers.Clear();
            _next = 0;
        }
        Changed?.Invoke();
    }

    private void Update(string id, Func<SessionSpeaker, SessionSpeaker> f)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_speakers.TryGetValue(id, out var s))
            {
                var n = f(s);
                if (n != s)
                {
                    _speakers[id] = n;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static string LetterFor(int n)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return n < letters.Length ? letters[n].ToString() : $"{letters[n % letters.Length]}{n / letters.Length}";
    }
}
