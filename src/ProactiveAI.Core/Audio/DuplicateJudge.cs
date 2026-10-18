using ProactiveAI.Core.Text;

namespace ProactiveAI.Core.Audio;

public enum DuplicateVerdict
{
    KeepBoth,
    MicrophoneIsEcho,
    PlaybackIsMonitorEcho,
}

/// <summary>
/// Conservative cross-source duplicate decision. Requires (1) overlapping timing (checked by the caller),
/// (2) the shorter text largely contained in the other, and (3) acoustic correlation with the expected
/// lead direction. Similar text without acoustic corroboration (a person repeating someone) is kept.
/// </summary>
public static class DuplicateJudge
{
    public const double MinCorrelation = 0.7;
    public const double MinContainment = 0.6;

    public static DuplicateVerdict Decide(string micText, string systemText, EchoCorrelation playbackLeads, EchoCorrelation micLeads)
    {
        double micInSys = TextTools.Containment(micText, systemText);
        double sysInMic = TextTools.Containment(systemText, micText);
        if (micInSys < MinContainment && sysInMic < MinContainment)
        {
            return DuplicateVerdict.KeepBoth;
        }
        if (playbackLeads.Correlation >= MinCorrelation && playbackLeads.Correlation >= micLeads.Correlation && micInSys >= MinContainment)
        {
            return DuplicateVerdict.MicrophoneIsEcho;
        }
        if (micLeads.Correlation >= MinCorrelation && micLeads.Correlation > playbackLeads.Correlation && sysInMic >= MinContainment)
        {
            return DuplicateVerdict.PlaybackIsMonitorEcho;
        }
        return DuplicateVerdict.KeepBoth;
    }
}
