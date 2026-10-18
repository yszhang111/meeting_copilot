namespace ProactiveAI.Core.Context;

/// <param name="Id">Library id (GUID, "N" format).</param>
/// <param name="Name">Display name, initially the file name.</param>
/// <param name="Format">Source format: "pdf" or "docx".</param>
/// <param name="Note">Extraction note shown to the user (for example "text was shortened").</param>
public sealed record ResumeInfo(string Id, string Name, string Format, int Chars, DateTimeOffset AddedUtc, string? Note = null);

/// <summary>A résumé: extracted text only, never the original file.</summary>
public sealed record Resume(ResumeInfo Info, string Text);

/// <summary>Saved résumé versions. Implementations protect the text at rest and never log it.</summary>
public interface IResumeLibrary
{
    /// <summary>Saved résumés, newest first. Unreadable entries are skipped.</summary>
    IReadOnlyList<ResumeInfo> List();

    Resume? Load(string id);

    void Save(Resume resume);

    bool Delete(string id);

    void DeleteAll();
}

/// <param name="Note">Shown to the user, e.g. "shortened to 60,000 characters".</param>
public sealed record ExtractedText(string Text, string Format, string? Note);

/// <summary>Reads the text of a résumé file locally (nothing is uploaded).</summary>
public interface IDocumentTextExtractor
{
    /// <summary>File extensions (lower case, with dot) that can be read.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <exception cref="DocumentReadException">The file cannot be read or contains no text.</exception>
    Task<ExtractedText> ExtractAsync(string path, CancellationToken cancellationToken);
}

/// <summary>A document could not be read; the message is safe to show to the user (no content).</summary>
public sealed class DocumentReadException(string message) : Exception(message);
