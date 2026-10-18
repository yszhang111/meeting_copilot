using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;

namespace ProactiveAI.Probe;

/// <summary>Runs the pre-session device check on the real default devices: microphone level, then a test chime that system-audio capture must pick up. No API calls; nothing is recorded.</summary>
public static class DeviceCheckTest
{
    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var check = sp.GetRequiredService<DeviceCheck>(); // disposed with the container
        if (o.Has("restart"))
        {
            return await RestartAsync(check, settings, o.GetInt("cycles", 5));
        }
        if (o.Has("after-session"))
        {
            return await AfterSessionAsync(sp, check, settings);
        }
        if (o.Has("race"))
        {
            return await StartStopRaceAsync(sp, o.GetInt("cycles", 30));
        }
        double micMax = -120, sysMax = -120;
        int ticks = 0;
        check.LevelsChanged += l =>
        {
            micMax = Math.Max(micMax, l.MicrophoneDbfs);
            sysMax = Math.Max(sysMax, l.SystemDbfs);
            Interlocked.Increment(ref ticks);
        };
        await check.StartAsync(settings.MicrophoneDeviceId, microphone: !o.Has("no-mic"), settings.PlaybackDeviceId);
        Console.WriteLine($"Microphone: {check.MicrophoneName ?? "-"} error={check.MicrophoneError ?? "none"}");
        Console.WriteLine($"Playback:   {check.PlaybackName ?? "-"} error={check.SystemError ?? "none"}");
        await Task.Delay(1500);
        Console.WriteLine($"Idle 1.5 s: level events={ticks} micMax={micMax:F0} dBFS systemMax={sysMax:F0} dBFS");
        var r = await check.TestSpeakersAsync(CancellationToken.None);
        Console.WriteLine($"Test sound: played={r.Played} capturedBySystemAudio={r.CapturedBySystemAudio} capturePeak={r.CapturePeakDbfs:F0} dBFS heardByMicrophone={r.HeardByMicrophone} error={r.Error ?? "none"}");
        await check.StopAsync();
        return r.Played && r.CapturedBySystemAudio ? 0 : 4;
    }

    /// <summary>
    /// User report 2026-10-17: in a session turn the microphone off, stop, open New session (system-only device check), turn the
    /// microphone on → the check hangs. Same calls as the app; connects to the transcription service for a few seconds.
    /// </summary>
    private static async Task<int> AfterSessionAsync(IServiceProvider sp, DeviceCheck check, AppSettings settings)
    {
        var coordinator = sp.GetRequiredService<SessionCoordinator>();
        coordinator.UpdateSettings(settings with { SaveSessionHistory = false, SystemAudioEnabled = true });
        async Task<bool> Timed(string label, Func<Task> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = action();
            bool done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task;
            Console.WriteLine($"{label,-46} {(done ? $"{sw.ElapsedMilliseconds,5} ms" : "TIMEOUT (>10 s)")}");
            return done;
        }
        await Timed("session: microphone on", () => coordinator.SetMicrophoneEnabledAsync(true));
        await Timed("session: start", coordinator.StartAsync);
        await Task.Delay(2500);
        await Timed("session: microphone off", () => coordinator.SetMicrophoneEnabledAsync(false));
        await Task.Delay(1500);
        await Timed("session: stop", coordinator.StopAsync);
        await Timed("check: start (system only)", () => check.StartAsync(settings.MicrophoneDeviceId, false, settings.PlaybackDeviceId));
        await Task.Delay(2000);
        var toggle = Task.Run(() => coordinator.SetMicrophoneEnabledAsync(true)); // what the toggle does, concurrently
        bool ok = await Timed("check: restart (microphone on)", () => check.StartAsync(settings.MicrophoneDeviceId, true, settings.PlaybackDeviceId));
        await Timed("coordinator: microphone on (toggle)", () => toggle);
        if (!ok)
        {
            Console.WriteLine($"HANG — process {Environment.ProcessId} waits 90 s for a stack dump (dotnet-stack report -p {Environment.ProcessId})");
            await Task.Delay(TimeSpan.FromSeconds(90));
            return 4;
        }
        await Timed("check: stop", check.StopAsync);
        Console.WriteLine("No hang.");
        return 0;
    }

    /// <summary>
    /// Starts a real capture and stops it immediately, many times (NAudio 3.2.0 lost a stop issued before its capture thread
    /// was running, so the stop waited forever). System loopback and microphone. No API calls; nothing is recorded.
    /// </summary>
    private static async Task<int> StartStopRaceAsync(IServiceProvider sp, int cycles)
    {
        var factory = sp.GetRequiredService<ICaptureSourceFactory>();
        var devices = sp.GetRequiredService<IAudioDeviceCatalog>();
        int hung = 0;
        foreach (var kind in new[] { AudioSourceKind.System, AudioSourceKind.Microphone })
        {
            var device = devices.GetDefault(kind);
            if (device is null)
            {
                Console.WriteLine($"{kind}: no device");
                continue;
            }
            var times = new List<long>();
            for (int i = 0; i < cycles; i++)
            {
                var capture = factory.Create(new CaptureRequest(kind, device.Id, null, false));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                capture.Start();
                var stop = capture.DisposeAsync().AsTask();
                if (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))) != stop)
                {
                    hung++;
                    Console.WriteLine($"{kind} cycle {i + 1}: stop right after start HUNG (>5 s)");
                    break;
                }
                times.Add(sw.ElapsedMilliseconds);
            }
            if (times.Count > 0)
            {
                Console.WriteLine($"{kind}: {times.Count} start+stop cycles, max {times.Max()} ms, median {times.Order().ElementAt(times.Count / 2)} ms");
            }
        }
        Console.WriteLine(hung == 0 ? "No hang." : $"{hung} hang(s).");
        return hung == 0 ? 0 : 4;
    }

    /// <summary>
    /// The new-session page restarts the check whenever the microphone is toggled: system audio only (silence), then system
    /// audio + microphone, and back. Every start/stop must finish promptly and leave the playback device usable.
    /// </summary>
    private static async Task<int> RestartAsync(DeviceCheck check, AppSettings settings, int cycles)
    {
        int failures = 0;
        async Task<bool> Timed(string label, Func<Task> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = action();
            bool done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task;
            Console.WriteLine($"{label,-34} {(done ? $"{sw.ElapsedMilliseconds,5} ms" : "TIMEOUT (>10 s)")} mic={check.MicrophoneName ?? "-"} playback={check.PlaybackName ?? "-"} errors={check.MicrophoneError ?? "-"}/{check.SystemError ?? "-"}");
            if (!done)
            {
                failures++;
            }
            return done;
        }
        for (int i = 1; i <= cycles; i++)
        {
            bool mic = i % 2 == 0;
            if (!await Timed($"cycle {i}: start (microphone={mic})", () => check.StartAsync(settings.MicrophoneDeviceId, mic, settings.PlaybackDeviceId)))
            {
                break;
            }
            await Task.Delay(2000); // nothing playing: loopback delivers no packets
        }
        await Timed("final stop", check.StopAsync);
        Console.WriteLine(failures == 0 ? "All starts/stops completed." : $"{failures} start/stop call(s) hung.");
        return failures == 0 ? 0 : 4;
    }
}
