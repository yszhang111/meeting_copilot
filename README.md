# Proactive AI — native Windows meeting, interview and defense assistant

Proactive AI is a WinUI 3 desktop app for Windows 11 x64. When you explicitly start listening, it:

- captures **one playback device** (WASAPI loopback) and, if you switch it on, **one microphone**;
- streams speech to OpenAI for **live captions** (`gpt-live-transcribe`, Realtime WebSocket);
- labels **who spoke** with audio-based diarization (`gpt-4o-transcribe-diarize`) on short in-memory windows, keeping labels stable across windows with temporary reference clips;
- detects substantive questions in **Interview** and **Defense** mode and streams a **full, structured spoken answer** (direct position, spoken answer, support, limits, likely follow-ups) grounded in your preparation notes;
- in **Meeting** mode, occasionally offers a brief insight or follow-up question;
- shows answers in a focus-safe floating overlay and a tray icon.

Nothing is recorded to disk. See *Privacy* below.

## Requirements

| What | Version / note |
| --- | --- |
| OS | Windows 11 x64 (developed on build 26200; minimum supported 22621 for echo-cancellation reference control) |
| To build | .NET SDK **10.0.402** (`winget install --id Microsoft.DotNet.SDK.10 -e`). Visual Studio is optional. |
| To run the zip | Nothing else: the zip is self-contained (.NET runtime and Windows App SDK included). |
| Account | An OpenAI API key with access to the models listed in Settings. |

## Build, test, run

```powershell
# from the repository root
dotnet build src\ProactiveAI.Desktop -c Debug -p:Platform=x64
dotnet test --project tests\ProactiveAI.Tests            # 97 deterministic tests, no network
.\src\ProactiveAI.Desktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\ProactiveAI.exe
```

Store your key (masked prompt, written to Windows Credential Manager — never to files or logs):

```powershell
powershell -ExecutionPolicy Bypass -File tools\Set-ApiKey.ps1      # or Settings > OpenAI API key
```

### Distribution (unpackaged, self-contained zip)

```powershell
powershell -ExecutionPolicy Bypass -File tools\Publish.ps1
# -> artifacts\ProactiveAI-0.1.0-win-x64.zip  (extract anywhere, run ProactiveAI.exe)
```

The zip is **not code-signed**. Windows SmartScreen may warn on first launch of a downloaded copy; no certificate, MSIX, Developer Mode or trust change is needed or performed. To distribute widely, sign `ProactiveAI.exe` with your own code-signing certificate.

### Verification tool (opt-in, uses your API key)

```powershell
$probe = ".\tools\ProactiveAI.Probe\bin\Debug\net10.0-windows10.0.26100.0\ProactiveAI.Probe.exe"
dotnet build tools\ProactiveAI.Probe
& $probe models                                   # model visibility + realtime endpoint check (cheap)
& $probe eval --report artifacts\eval.md          # suggestion-quality fixtures through the real model
& $probe session --script panel --report artifacts\panel.md       # plays a 4-voice TTS panel through your speakers and listens
& $probe session --script interview --report artifacts\interview.md
& $probe session --script defense --report artifacts\defense.md
& $probe session --mic --seconds 60               # your own microphone (explicit)
```

`session` captures **everything playing on the default output device** and uploads it — pause other audio first.

## Using the app

1. The app opens on **Home**: start a **New session**, or open a previous session to read its transcript and answers (Copy all / Delete).
2. **New session** shows a device check: pick **Meeting / Interview / Defense**, toggle **System audio** and/or **Microphone** (the microphone is on by default; your last choice is remembered), speak to see the microphone level, and select **Play test sound** to confirm the speakers are captured (it also tells you whether the microphone hears the speakers, i.e. you are on loudspeakers). The check is optional. With loudspeakers, keep system audio on: the built-in echo canceller (**Settings > Echo cancellation**, default) uses it to remove the playback from your microphone. It learns the echo path during the first seconds of playback; the status strip shows how much echo it currently removes. Headphones avoid echo entirely. In **Interview** mode, choose the **résumé** for this interview (or upload one: PDF or Word .docx, also by dropping the file on that section). You can keep several versions; the last choice is pre-selected and can be changed here or on the live page under **Preparation**, where **Extracted text** shows exactly what the AI reads. Scanned PDFs without a text layer and old .doc files are not supported. Optionally fill **Preparation** notes on the live page (role/topic, your contribution, method, verified results, limitations, terminology).
3. Select **Start** and confirm the disclosure. Captions appear with speaker labels (pending at first, revised in place within ~10–25 s).
4. When a question is asked, the **Assistant** panel and overlay stream a draft answer. Answers about your own projects and research are written so you can say them as they are. Details you never mentioned (for example how something was measured, tools, steps, reasons) are filled in with typical practice and listed under **AI filled in — check before saying**. Measured results (scores, percentages, latency, counts), employers, job titles, degrees, publications and citations are never made up; when an answer needs one, it appears under **Facts that must not be made up — add them yourself**. Clear questions are recognized locally and instantly — usually already from the live caption, before it is finalized; turns the local rules are unsure about are checked by a small AI model first (about 1 s; **Settings > AI question detection**). **Settings > Fast mode for answers** (off by default) uses OpenAI Fast mode for answer requests: the first lines appear roughly 0.3–1 s sooner at about twice the price of those requests. Pin it while reading; a "newer answer" banner appears instead of replacing what you read. Copy, dismiss, cancel or regenerate as needed. Select transcript lines and **Answer this question** if detection missed one.
5. Rename speakers or correct a line's speaker from its label. Pause / Resume / Stop / Reset; Reset clears the session but keeps preparation notes (they have their own Clear button). The session is saved to **History** on stop (and every 30 s while it runs), so starting a new session never loses the previous one.

Closing the main window stops and exits unless **Settings > Closing the main window** keeps it in the tray.

## Privacy

- Listening starts only on **Start**. System audio (everything on the selected output) and the microphone (if on) are streamed to OpenAI.
- Speaker labeling uploads ~20 s windows from a ≤60 s in-memory buffer per source plus up to a few 2.5–8 s reference snippets per speaker; all are deleted on stop/reset/exit and never stored as voice profiles.
- The AI model receives ~2 minutes of recent transcript (≤6k tokens); question memory for the whole session — the latest two questions with what you replied in full, every earlier question in a compact log (older entries shortened to the question only), and fuller copies of earlier exchanges on the current follow-up chain or about the same topic (≤6.7k tokens together); and preparation notes plus, in interview mode, your selected résumé (≤10k tokens); total ≤28k tokens. Question memory is kept in memory for the session only and cleared on Reset. With AI question detection on, turns the local rules are unsure about (≤1,200 characters plus up to three preceding lines) are also sent to the question detection model.
- Requests use OpenAI prompt caching: the instructions, preparation notes and question log are marked as reusable prefixes (OpenAI keeps the cached state for at least 30 minutes after last use, per its prompt-caching policy); the rest of each request is not written to the cache.
- Audio is never written to disk. The device check plays a local chime and measures levels only; nothing is uploaded.
- Session history: the transcript text (with speaker labels) and the assistant's answers and insights are saved to `%LOCALAPPDATA%\ProactiveAI\sessions`, encrypted with Windows DPAPI for your Windows account. Preparation notes are not saved. Turn history off or delete all saved sessions in **Settings > Session history**; delete single sessions on Home or in the session view. Logs (`%LOCALAPPDATA%\ProactiveAI\logs`, 2 × 1 MB max) contain metadata only.
- Résumés: the file is read locally (nothing is uploaded while reading) and only the extracted text is kept, encrypted with DPAPI in `%LOCALAPPDATA%\ProactiveAI\resumes`; the original file is not stored. In interview mode the selected résumé is sent to OpenAI with answer requests (in full when notes and résumé fit about 10,000 tokens, otherwise the most relevant parts) as facts about you. Delete a version next to the chooser or all of them in **Settings > Session history**.
- Cloud processing follows your OpenAI account's data policies. Obtain consent where required.

## Repository layout

```
src/ProactiveAI.Core            portable domain logic (net10.0): session lifecycle, VAD, timeline, transcript,
                                speakers/diarization, question detection, prompts, validation, scheduling
src/ProactiveAI.Infrastructure  Windows/OpenAI adapters: NAudio WASAPI capture, Realtime WebSocket, diarization,
                                Responses SSE, Credential Manager, settings, tokenizer, bounded log
src/ProactiveAI.Desktop         WinUI 3 app: main window, overlay, tray, view models
tests/ProactiveAI.Tests         xUnit v3 tests with fakes and fixtures
tools/                          Set-ApiKey.ps1, Publish.ps1, Make-AppIcon.ps1, ProactiveAI.Probe (live verification)
docs/                           COMPATIBILITY.md, ARCHITECTURE.md, PROGRESS.md, MANUAL_VALIDATION.md
```

## Known limitations

See `docs/PROGRESS.md` (verification status) and `docs/ARCHITECTURE.md` (design limits). Deferred by design: multiple endpoints per source, per-process capture, persistent history/recordings, cross-session voice profiles, document ingestion, web research, spoken output, global shortcuts, click-through overlays, advanced multi-monitor layouts.
