using ProactiveAI.Infrastructure.Security;
using ProactiveAI.Infrastructure.Settings;

namespace ProactiveAI.Tests;

/// <summary>The app was renamed from Proactive AI to Meeting Copilot; existing local data and the API key move over once.</summary>
public class RenameMigrationTests
{
    [Fact]
    public void LegacyDataFolderIsMovedWholeWhenTheNewOneDoesNotExist()
    {
        var root = TempRoot();
        try
        {
            var legacy = Path.Combine(root, "ProactiveAI");
            var target = Path.Combine(root, "MeetingCopilot");
            Write(legacy, "settings.json", "{\"a\":1}");
            Write(legacy, @"sessions\s1.session", "one");
            Write(legacy, @"resumes\r1.text", "cv");

            var result = AppDataLocation.MoveLegacyData(legacy, target);

            Assert.Equal(new LegacyDataMove(3, 0, 0), result);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal("one", File.ReadAllText(Path.Combine(target, @"sessions\s1.session")));
            Assert.Equal("cv", File.ReadAllText(Path.Combine(target, @"resumes\r1.text")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LegacyDataIsMergedWithoutOverwritingTheNewFolder()
    {
        var root = TempRoot();
        try
        {
            var legacy = Path.Combine(root, "ProactiveAI");
            var target = Path.Combine(root, "MeetingCopilot");
            Write(legacy, "settings.json", "old");
            Write(legacy, @"sessions\s1.session", "one");
            Write(target, "settings.json", "new");
            Write(target, @"sessions\s2.session", "two");

            var result = AppDataLocation.MoveLegacyData(legacy, target);

            Assert.Equal(new LegacyDataMove(1, 1, 0), result);
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal("one", File.ReadAllText(Path.Combine(target, @"sessions\s1.session")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(target, @"sessions\s2.session")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "settings.json"))); // kept, not deleted
            Assert.False(Directory.Exists(Path.Combine(legacy, "sessions"))); // emptied folders are removed
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NothingHappensWithoutLegacyData()
    {
        var root = TempRoot();
        try
        {
            var result = AppDataLocation.MoveLegacyData(Path.Combine(root, "ProactiveAI"), Path.Combine(root, "MeetingCopilot"));
            Assert.False(result.AnythingFound);
            Assert.False(Directory.Exists(Path.Combine(root, "MeetingCopilot")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ApiKeyMovesFromTheLegacyEntry()
    {
        var id = Guid.NewGuid().ToString("N");
        var legacy = new WindowsCredentialStore("MeetingCopilot/Test-legacy-" + id);
        var store = new WindowsCredentialStore("MeetingCopilot/Test-new-" + id);
        try
        {
            legacy.WriteApiKey("sk-test-legacy-0123456789abcdef");

            Assert.True(store.MoveFrom("MeetingCopilot/Test-legacy-" + id));
            Assert.Equal("sk-test-legacy-0123456789abcdef", store.ReadApiKey());
            Assert.False(legacy.HasApiKey());
            Assert.False(store.MoveFrom("MeetingCopilot/Test-legacy-" + id)); // nothing left to move
        }
        finally
        {
            legacy.DeleteApiKey();
            store.DeleteApiKey();
        }
    }

    [Fact]
    public void ExistingApiKeyWinsAndTheStaleLegacyEntryIsRemoved()
    {
        var id = Guid.NewGuid().ToString("N");
        var legacy = new WindowsCredentialStore("MeetingCopilot/Test-legacy-" + id);
        var store = new WindowsCredentialStore("MeetingCopilot/Test-new-" + id);
        try
        {
            legacy.WriteApiKey("sk-test-old-0123456789abcdef");
            store.WriteApiKey("sk-test-current-0123456789abcdef");

            Assert.False(store.MoveFrom("MeetingCopilot/Test-legacy-" + id));
            Assert.Equal("sk-test-current-0123456789abcdef", store.ReadApiKey());
            Assert.False(legacy.HasApiKey()); // a key deleted later cannot come back from the old entry
        }
        finally
        {
            legacy.DeleteApiKey();
            store.DeleteApiKey();
        }
    }

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string relative, string text)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
