namespace ProactiveAI.Infrastructure.Settings;

/// <summary>
/// Where the app keeps its local data: %LOCALAPPDATA%\MeetingCopilot. The app was called "Proactive AI" before and used
/// %LOCALAPPDATA%\ProactiveAI; <see cref="MoveLegacyData"/> moves that data over once. File contents (DPAPI entropy
/// included) are unchanged, so moved sessions and résumés stay readable.
/// </summary>
public static class AppDataLocation
{
    public const string FolderName = "MeetingCopilot";
    public const string LegacyFolderName = "ProactiveAI";

    public static string Default => Path.Combine(LocalAppData, FolderName);

    public static string Legacy => Path.Combine(LocalAppData, LegacyFolderName);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Moves everything from <paramref name="legacy"/> into <paramref name="target"/>: the whole folder when the target
    /// does not exist yet, otherwise file by file, keeping any file the target already has. Emptied legacy folders are
    /// removed. Never throws; failures are counted.
    /// </summary>
    public static LegacyDataMove MoveLegacyData(string legacy, string target)
    {
        if (!Directory.Exists(legacy))
        {
            return LegacyDataMove.None;
        }
        int total = 0;
        try
        {
            total = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).Count();
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                Directory.Move(legacy, target);
                return new LegacyDataMove(total, 0, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall back to moving file by file
        }

        int moved = 0, kept = 0, failed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToList())
            {
                var dest = Path.Combine(target, Path.GetRelativePath(legacy, file));
                try
                {
                    if (File.Exists(dest))
                    {
                        kept++; // the new location already has this file; the old copy is left where it was
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(file, dest);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }
            RemoveEmptyDirectories(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = Math.Max(failed, total - moved - kept);
        }
        return new LegacyDataMove(moved, kept, failed);
    }

    private static void RemoveEmptyDirectories(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            RemoveEmptyDirectories(sub);
        }
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }
}

/// <summary>Result of <see cref="AppDataLocation.MoveLegacyData"/>: files moved, files kept because the new location had them, failures.</summary>
public sealed record LegacyDataMove(int Moved, int Kept, int Failed)
{
    public static readonly LegacyDataMove None = new(0, 0, 0);

    public bool AnythingFound => Moved + Kept + Failed > 0;
}
