using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.History;
using ProactiveAI.Infrastructure.Security;

namespace ProactiveAI.Infrastructure.History;

/// <summary>
/// Session history under %LOCALAPPDATA%\ProactiveAI\sessions. Each session is two files — a small summary for the list
/// and the full record — both JSON encrypted with DPAPI for the current Windows user. Text only; never audio. Content is
/// never logged.
/// </summary>
public sealed partial class EncryptedSessionArchive : ISessionArchive
{
    private const string SummaryExtension = ".summary";
    private const string RecordExtension = ".session";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProactiveAI.SessionHistory.v1");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public EncryptedSessionArchive(string directory, ILogger<EncryptedSessionArchive> log)
    {
        _dir = directory;
        _log = log;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProactiveAI", "sessions");

    public string Directory => _dir;

    public void Save(SessionRecord record)
    {
        string id = ValidId(record.Summary.Id);
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            Write(Path.Combine(_dir, id + RecordExtension), JsonSerializer.SerializeToUtf8Bytes(record, Json));
            Write(Path.Combine(_dir, id + SummaryExtension), JsonSerializer.SerializeToUtf8Bytes(record.Summary, Json));
        }
    }

    public IReadOnlyList<SessionSummary> List()
    {
        var list = new List<SessionSummary>();
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_dir))
            {
                return list;
            }
            int failed = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*" + SummaryExtension))
            {
                try
                {
                    var summary = JsonSerializer.Deserialize<SessionSummary>(Read(file), Json);
                    if (summary is not null && File.Exists(Path.Combine(_dir, summary.Id + RecordExtension)))
                    {
                        list.Add(summary);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    failed++;
                }
            }
            if (failed > 0)
            {
                _log.LogWarning("Session history: {Count} entries could not be read and were skipped", failed);
            }
        }
        return list.OrderByDescending(s => s.StartedUtc).ToList();
    }

    public SessionRecord? Load(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        lock (_gate)
        {
            string path = Path.Combine(_dir, id + RecordExtension);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<SessionRecord>(Read(path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _log.LogWarning("Session history entry could not be read: {Type}", ex.GetType().Name);
                return null;
            }
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }
        lock (_gate)
        {
            bool any = false;
            foreach (var ext in new[] { RecordExtension, SummaryExtension })
            {
                string path = Path.Combine(_dir, id + ext);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    any = true;
                }
            }
            return any;
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_dir))
            {
                return;
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir).Where(f => f.EndsWith(RecordExtension, StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(SummaryExtension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(file);
            }
        }
    }

    private static void Write(string path, byte[] plain)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Dpapi.Protect(plain, Entropy));
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] Read(string path) => Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);

    private static bool IsValidId(string id) => IdPattern().IsMatch(id);

    private static string ValidId(string id) => IsValidId(id) ? id : throw new ArgumentException("Invalid session id", nameof(id));

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();
}
