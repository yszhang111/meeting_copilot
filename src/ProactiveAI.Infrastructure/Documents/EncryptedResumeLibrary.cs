using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Context;
using ProactiveAI.Infrastructure.Security;

namespace ProactiveAI.Infrastructure.Documents;

/// <summary>
/// Résumé versions under %LOCALAPPDATA%\ProactiveAI\resumes: per résumé a small info file (for the list) and the extracted
/// text, both encrypted with DPAPI for the current Windows user. The original file is never stored. Content is never logged.
/// </summary>
public sealed partial class EncryptedResumeLibrary : IResumeLibrary
{
    private const string InfoExtension = ".info";
    private const string TextExtension = ".text";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProactiveAI.Resumes.v1");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public EncryptedResumeLibrary(string directory, ILogger<EncryptedResumeLibrary> log)
    {
        _dir = directory;
        _log = log;
    }

    public string Directory => _dir;

    public IReadOnlyList<ResumeInfo> List()
    {
        var list = new List<ResumeInfo>();
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_dir))
            {
                return list;
            }
            int failed = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*" + InfoExtension))
            {
                try
                {
                    var info = JsonSerializer.Deserialize<ResumeInfo>(Read(file), Json);
                    if (info is not null && IsValidId(info.Id) && File.Exists(Path.Combine(_dir, info.Id + TextExtension)))
                    {
                        list.Add(info);
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    failed++;
                }
            }
            if (failed > 0)
            {
                _log.LogWarning("Résumé library: {Count} entries could not be read and were skipped", failed);
            }
        }
        return list.OrderByDescending(r => r.AddedUtc).ToList();
    }

    public Resume? Load(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        lock (_gate)
        {
            string infoPath = Path.Combine(_dir, id + InfoExtension), textPath = Path.Combine(_dir, id + TextExtension);
            if (!File.Exists(infoPath) || !File.Exists(textPath))
            {
                return null;
            }
            try
            {
                var info = JsonSerializer.Deserialize<ResumeInfo>(Read(infoPath), Json);
                return info is null ? null : new Resume(info, Encoding.UTF8.GetString(Read(textPath)));
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                _log.LogWarning("Résumé could not be read: {Type}", ex.GetType().Name);
                return null;
            }
        }
    }

    public void Save(Resume resume)
    {
        if (!IsValidId(resume.Info.Id))
        {
            throw new ArgumentException("Invalid résumé id", nameof(resume));
        }
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            Write(Path.Combine(_dir, resume.Info.Id + TextExtension), Encoding.UTF8.GetBytes(resume.Text));
            Write(Path.Combine(_dir, resume.Info.Id + InfoExtension), JsonSerializer.SerializeToUtf8Bytes(resume.Info, Json));
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
            foreach (var ext in new[] { InfoExtension, TextExtension })
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
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir).Where(f => f.EndsWith(InfoExtension, StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(TextExtension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(file);
            }
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException or DecoderFallbackException;

    private static void Write(string path, byte[] plain)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Dpapi.Protect(plain, Entropy));
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] Read(string path) => Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);

    private static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();
}
