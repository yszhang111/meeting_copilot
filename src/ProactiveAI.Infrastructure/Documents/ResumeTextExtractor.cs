using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ProactiveAI.Core.Context;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ProactiveAI.Infrastructure.Documents;

/// <summary>
/// Local text extraction for résumés: PDF (text layer, via PdfPig) and Word .docx (Open XML read directly). Nothing is
/// uploaded and the original file is not kept. Scanned PDFs without a text layer are rejected with an explanation.
/// </summary>
public sealed partial class ResumeTextExtractor : IDocumentTextExtractor
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxChars = 60_000;
    private const long MaxXmlBytes = 50 * 1024 * 1024; // guards against zip bombs
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf", ".docx"];

    public Task<ExtractedText> ExtractAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
        {
            throw new DocumentReadException(ext == ".doc"
                ? "Old Word .doc files are not supported. Save the résumé as .docx or PDF."
                : "Only PDF and Word (.docx) résumés are supported.");
        }
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new DocumentReadException("The file was not found.");
        }
        if (info.Length > MaxFileBytes)
        {
            throw new DocumentReadException("The file is larger than 20 MB.");
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DocumentReadException("The file could not be opened. Close it in other programs and try again.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return FromBytes(bytes, ext, cancellationToken);
    }, cancellationToken);

    internal static ExtractedText FromBytes(byte[] bytes, string ext, CancellationToken cancellationToken = default)
    {
        string text = Normalize(ext == ".pdf" ? ReadPdf(bytes, cancellationToken) : ReadDocx(bytes));
        if (text.Count(char.IsLetterOrDigit) < 20)
        {
            throw new DocumentReadException(ext == ".pdf"
                ? "No text found in this PDF. It is probably a scanned image; export the résumé from Word/WPS as a PDF, or upload the .docx."
                : "No text found in this document.");
        }
        string? note = null;
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
            note = $"Long document: only the first {MaxChars:N0} characters are used.";
        }
        return new ExtractedText(text, ext.TrimStart('.'), note);
    }

    internal static string ReadPdf(byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Encrypted", StringComparison.Ordinal))
        {
            throw new DocumentReadException("The PDF is password-protected. Save an unprotected copy and try again.");
        }
        catch (Exception)
        {
            throw new DocumentReadException("The PDF could not be read. It may be damaged; try exporting it again.");
        }
    }

    /// <summary>Paragraphs, line breaks, tabs, table cells and text boxes of the body, with headers first (names often sit there).</summary>
    internal static string ReadDocx(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var body = zip.GetEntry("word/document.xml") ?? throw new DocumentReadException("This .docx file has no document body.");
            var sb = new StringBuilder();
            foreach (var header in zip.Entries.Where(e => HeaderPattern().IsMatch(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                AppendWordXml(header, sb);
            }
            AppendWordXml(body, sb);
            return sb.ToString();
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            throw new DocumentReadException("The Word document could not be read. It may be damaged; try saving it again.");
        }
    }

    private static void AppendWordXml(ZipArchiveEntry entry, StringBuilder sb)
    {
        if (entry.Length > MaxXmlBytes)
        {
            throw new DocumentReadException("The Word document is too large.");
        }
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        bool inText = false;
        int skipDepth = -1; // inside mc:Fallback: the same content as mc:Choice (e.g. text boxes), reading both would duplicate it
        while (reader.Read())
        {
            if (skipDepth >= 0)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == skipDepth)
                {
                    skipDepth = -1;
                }
                continue;
            }
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == Mc && reader.LocalName == "Fallback":
                    if (!reader.IsEmptyElement)
                    {
                        skipDepth = reader.Depth;
                    }
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == W:
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab":
                            sb.Append('\t');
                            break;
                        case "br":
                        case "cr":
                            sb.Append('\n');
                            break;
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA when inText:
                    sb.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == W:
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = false;
                            break;
                        case "p":
                        case "tr":
                            sb.Append('\n');
                            break;
                        case "tc":
                            sb.Append('\t');
                            break;
                    }
                    break;
            }
        }
        sb.Append('\n');
    }

    /// <summary>Trims lines, removes control characters, turns tabs (table cells, aligned columns) into " | " and limits blank lines.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c is '\u00A0' or '\u3000' ? ' ' : c);
            }
        }
        var lines = sb.ToString().Split('\n').Select(l =>
        {
            string line = Tabs().Replace(l, " | ");
            return MultiSpace().Replace(line, " ").Trim().Trim('|').Trim();
        });
        return BlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"^word/header\d*\.xml$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"[ ]*\t[\t ]*")]
    private static partial Regex Tabs();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
