using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Infrastructure.Audio;
using ProactiveAI.Infrastructure.Documents;
using ProactiveAI.Infrastructure.History;
using ProactiveAI.Infrastructure.Logging;
using ProactiveAI.Infrastructure.OpenAI;
using ProactiveAI.Infrastructure.Security;
using ProactiveAI.Infrastructure.Settings;
using ProactiveAI.Infrastructure.Text;

namespace ProactiveAI.Infrastructure;

public static class ServiceRegistration
{
    /// <summary>Registers the production services shared by the desktop app and the verification tool.</summary>
    public static IServiceCollection AddProactiveAI(this IServiceCollection services, string? dataDirectory = null)
    {
        // The default location moved from %LOCALAPPDATA%\ProactiveAI when the app was renamed to Meeting Copilot; move
        // existing data before anything (including the log file) is created there.
        bool defaultLocation = dataDirectory is null;
        var moved = defaultLocation ? AppDataLocation.MoveLegacyData(AppDataLocation.Legacy, AppDataLocation.Default) : LegacyDataMove.None;
        string dir = dataDirectory ?? JsonSettingsStore.DefaultDirectory;
        var fileLogger = new BoundedFileLoggerProvider(Path.Combine(dir, "logs"));
        if (moved.AnythingFound)
        {
            fileLogger.CreateLogger("AppData").LogInformation(
                "Data moved from the former Proactive AI folder: moved={Moved} keptExisting={Kept} failed={Failed}",
                moved.Moved, moved.Kept, moved.Failed);
        }
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(fileLogger);
            b.AddDebug();
        });
        services.AddSingleton(fileLogger);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(dir, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<ICredentialStore>(sp =>
        {
            var store = new WindowsCredentialStore();
            if (defaultLocation)
            {
                var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AppData");
                try
                {
                    if (store.MoveFrom(WindowsCredentialStore.LegacyTarget))
                    {
                        log.LogInformation("API key moved to the Meeting Copilot entry in Credential Manager");
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning("API key could not be moved from the former entry ({Type})", ex.GetType().Name);
                }
            }
            return store;
        });
        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton(sp => new OpenAIConnection(sp.GetRequiredService<ICredentialStore>()) { SafetyIdentifier = InstallIdentifier(dir) });
        services.AddSingleton<ITranscriptionProvider, RealtimeTranscriptionProvider>();
        services.AddSingleton<ISpeakerDiarizer, OpenAIDiarizer>();
        services.AddSingleton<IAssistantModel, OpenAIResponsesModel>();
        services.AddSingleton<IQuestionIntentClassifier, ModelQuestionIntentClassifier>();
        services.AddSingleton<IModelAccessChecker, OpenAIModelAccessChecker>();
        services.AddSingleton<WindowsAudioDeviceCatalog>();
        services.AddSingleton<IAudioDeviceCatalog>(sp => sp.GetRequiredService<WindowsAudioDeviceCatalog>());
        services.AddSingleton<ICaptureSourceFactory, WasapiCaptureSourceFactory>();
        services.AddSingleton<ITestSoundPlayer, WasapiTestSoundPlayer>();
        services.AddTransient<DeviceCheck>();
        services.AddSingleton<ISessionArchive>(sp => new EncryptedSessionArchive(Path.Combine(dir, "sessions"), sp.GetRequiredService<ILogger<EncryptedSessionArchive>>()));
        services.AddSingleton<IResumeLibrary>(sp => new EncryptedResumeLibrary(Path.Combine(dir, "resumes"), sp.GetRequiredService<ILogger<EncryptedResumeLibrary>>()));
        services.AddSingleton<IDocumentTextExtractor, ResumeTextExtractor>();
        services.AddSingleton(sp => new SessionCoordinator(
            sp.GetRequiredService<ICaptureSourceFactory>(),
            sp.GetRequiredService<IAudioDeviceCatalog>(),
            sp.GetRequiredService<ITranscriptionProvider>(),
            sp.GetRequiredService<ISpeakerDiarizer>(),
            sp.GetRequiredService<IAssistantModel>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<ITokenCounter>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ISettingsStore>().Load(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IQuestionIntentClassifier>(),
            sp.GetRequiredService<ISessionArchive>()));
        return services;
    }

    /// <summary>A random per-install id, hashed, used only as the OpenAI safety identifier (no PII).</summary>
    private static string InstallIdentifier(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "install-id");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            }
            var raw = File.ReadAllText(path).Trim();
            return "pai-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
        }
        catch (IOException)
        {
            return "pai-anonymous";
        }
    }
}
