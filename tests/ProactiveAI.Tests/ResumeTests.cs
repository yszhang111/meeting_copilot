using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Infrastructure.Documents;
using ProactiveAI.Tests.Fakes;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ProactiveAI.Tests;

public class ResumeTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    internal static byte[] Docx(string bodyXml, string? headerXml = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("word/document.xml", $"<w:document xmlns:w=\"{W}\" xmlns:mc=\"{Mc}\"><w:body>{bodyXml}</w:body></w:document>");
            if (headerXml is not null)
            {
                Add("word/header1.xml", $"<w:hdr xmlns:w=\"{W}\">{headerXml}</w:hdr>");
            }
        }
        return ms.ToArray();
    }

    internal static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        double y = 760;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new PdfPoint(40, y), font);
            y -= 18;
        }
        return builder.Build();
    }

    [Fact]
    public void DocxParagraphsTablesTabsAndTextBoxesAreReadOnce()
    {
        string body =
            "<w:p><w:r><w:t>张三</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t xml:space=\"preserve\">后端工程师 </w:t></w:r><w:r><w:tab/><w:t>5 年经验</w:t></w:r></w:p>" +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>教育</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>北京大学 计算机硕士</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
            "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><w:txbxContent><w:p><w:r><w:t>订单系统缓存改造</w:t></w:r></w:p></w:txbxContent></w:drawing></mc:Choice>" +
            "<mc:Fallback><w:pict><w:txbxContent><w:p><w:r><w:t>订单系统缓存改造</w:t></w:r></w:p></w:txbxContent></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>" +
            "<w:p><w:r><w:t>P99 从 800ms 降到 120ms</w:t><w:br/><w:t>第二行</w:t></w:r><w:r><w:delText>删除的旧内容</w:delText></w:r></w:p>";
        var result = ResumeTextExtractor.FromBytes(Docx(body, "<w:p><w:r><w:t>电话 138-0000-0000</w:t></w:r></w:p>"), ".docx");

        Assert.Equal("docx", result.Format);
        var lines = result.Text.Split('\n');
        Assert.Equal("电话 138-0000-0000", lines[0]); // header first
        Assert.Contains("后端工程师 | 5 年经验", lines);
        Assert.Contains("教育", lines);
        Assert.Contains("北京大学 计算机硕士", lines);
        Assert.Single(lines, l => l.Contains("订单系统缓存改造")); // text box read once, not again from the fallback
        Assert.Contains("P99 从 800ms 降到 120ms\n第二行", result.Text);
        Assert.DoesNotContain("删除的旧内容", result.Text); // tracked deletions are not text
    }

    [Fact]
    public void PdfTextLayerIsExtracted()
    {
        var result = ResumeTextExtractor.FromBytes(Pdf("Jane Doe", "Senior Backend Engineer at Acme Logistics", "Reduced p99 latency from 800 ms to 120 ms"), ".pdf");
        Assert.Equal("pdf", result.Format);
        Assert.Contains("Jane Doe", result.Text);
        Assert.Contains("Senior Backend Engineer at Acme Logistics", result.Text);
        Assert.Contains("800 ms to 120 ms", result.Text);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task UnreadableFilesGiveAnExplanationAndNoContent()
    {
        var empty = Assert.Throws<DocumentReadException>(() => ResumeTextExtractor.FromBytes(Pdf(), ".pdf"));
        Assert.Contains("scanned", empty.Message);
        Assert.Throws<DocumentReadException>(() => ResumeTextExtractor.FromBytes(Encoding.UTF8.GetBytes("not a pdf at all"), ".pdf"));
        Assert.Throws<DocumentReadException>(() => ResumeTextExtractor.FromBytes(Encoding.UTF8.GetBytes("not a zip"), ".docx"));

        var extractor = new ResumeTextExtractor();
        var dir = Path.Combine(Path.GetTempPath(), "pai-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var doc = Path.Combine(dir, "old.doc");
            await File.WriteAllBytesAsync(doc, [1, 2, 3]);
            var ex = await Assert.ThrowsAsync<DocumentReadException>(() => extractor.ExtractAsync(doc, CancellationToken.None));
            Assert.Contains(".docx", ex.Message);
            var docx = Path.Combine(dir, "简历.docx");
            await File.WriteAllBytesAsync(docx, Docx("<w:p><w:r><w:t>张三 后端工程师 五年经验 负责电商订单系统的缓存改造与性能优化</w:t></w:r></w:p>"));
            Assert.Contains("负责电商订单系统", (await extractor.ExtractAsync(docx, CancellationToken.None)).Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LongDocumentsAreShortenedWithANote()
    {
        string para = string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>负责订单系统的缓存改造和性能优化工作。</w:t></w:r></w:p>", 4000));
        var result = ResumeTextExtractor.FromBytes(Docx(para), ".docx");
        Assert.Equal(ResumeTextExtractor.MaxChars, result.Text.Length);
        Assert.NotNull(result.Note);
    }

    [Theory]
    [InlineData("resume-word.docx")]
    [InlineData("resume-word.pdf")]
    [InlineData("resume-chromium.pdf")]
    public async Task RealWordAndBrowserExportsKeepChineseTextAndColumnOrder(string file)
    {
        // Synthetic person; generated from Fixtures/resume/resume-source.html with Word (docx, PDF) and headless Edge (PDF).
        var result = await new ResumeTextExtractor().ExtractAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "resume", file), CancellationToken.None);
        string compact = string.Concat(result.Text.Where(c => !char.IsWhiteSpace(c))); // line wraps differ between exporters
        Assert.StartsWith("张伟", result.Text);
        Assert.Contains("浙江大学", result.Text);
        Assert.Contains("星河电商有限公司", result.Text);
        Assert.Contains("P99从800毫秒降到120毫秒，峰值QPS约2万", compact);
        Assert.Contains("重复扣款投诉下降 95%", result.Text);
        Assert.Contains("每日约3000万条轨迹事件", compact);
        Assert.Contains("技能：Java、Go、Redis、Kafka、MySQL、Kubernetes", result.Text);
        // Left column (education) is read before the right column (experience), not interleaved line by line.
        Assert.True(result.Text.IndexOf("浙江大学", StringComparison.Ordinal) < result.Text.IndexOf("工作经历", StringComparison.Ordinal));
        Assert.DoesNotContain("浙 江", result.Text);
    }

    [Fact]
    public void NormalizeKeepsWordsAndCellsApartAndTidiesWhitespace()
    {
        Assert.Equal("姓名 | 张三\n\n浙江大学 计算机科学与技术\nSenior Engineer at Acme",
            ResumeTextExtractor.Normalize("姓名\t张三\r\n\n\n\n浙江大学\u3000计算机科学与技术\nSenior   Engineer at Acme\u0007"));
    }

    [Fact]
    public void LibraryRoundTripsEncryptsAtRestAndValidatesIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pai-resumes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new EncryptedResumeLibrary(dir, NullLogger<EncryptedResumeLibrary>.Instance);
            var older = new Resume(new ResumeInfo("a1", "简历-v1.pdf", "pdf", 20, DateTimeOffset.UtcNow.AddDays(-1)), "张三 后端工程师 订单系统缓存改造");
            var newer = new Resume(new ResumeInfo("b2", "简历-v2.docx", "docx", 21, DateTimeOffset.UtcNow, "note"), "张三 高级后端工程师 支付系统");
            lib.Save(older);
            lib.Save(newer);
            Assert.Equal(["b2", "a1"], lib.List().Select(r => r.Id));
            Assert.Equal(older, lib.Load("a1"));
            foreach (var file in Directory.GetFiles(dir))
            {
                var bytes = File.ReadAllBytes(file);
                foreach (var secret in new[] { "订单系统", "张三", "简历-v1" })
                {
                    Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, $"{Path.GetFileName(file)} contains plaintext");
                }
            }
            File.WriteAllBytes(Path.Combine(dir, "zz.info"), [1, 2, 3]);
            Assert.Equal(2, lib.List().Count); // damaged entries are skipped
            Assert.Null(lib.Load("..\\a1"));
            Assert.Throws<ArgumentException>(() => lib.Save(older with { Info = older.Info with { Id = "../x" } }));
            Assert.True(lib.Delete("a1"));
            Assert.Null(lib.Load("a1"));
            lib.DeleteAll();
            Assert.Empty(lib.List());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static readonly DateTimeOffset SampleAdded = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static Resume SampleResume(string text = "张三\n后端工程师，5 年经验\n\n2021-2024 某电商公司 负责订单系统缓存改造，P99 从 800 毫秒降到 120 毫秒") =>
        new(new ResumeInfo("abcdef123456", "张三-简历-v2.pdf", "pdf", text.Length, SampleAdded), text);

    [Fact]
    public void ResumeExcerptsHaveStableIdsAndClearingNotesKeepsTheResume()
    {
        var prep = new PreparationNotes();
        int changed = 0;
        prep.Changed += () => changed++;
        prep.SetResume(SampleResume());
        prep.SetResume(SampleResume()); // same content: no change
        Assert.Equal(1, changed);
        var first = prep.Excerpts();
        Assert.All(first, e => Assert.StartsWith("cv-", e.Id));
        Assert.All(first, e => Assert.Equal(PrepField.Resume, e.Field));
        Assert.Equal(first.Select(e => e.Id), prep.Excerpts().Select(e => e.Id));
        prep.Set(PrepField.RoleOrTopic, "后端面试");
        prep.Clear();
        Assert.NotNull(prep.Resume);
        prep.SetResume(null);
        Assert.Empty(prep.Excerpts());
    }

    [Fact]
    public void ChunksBreakAtBlankLinesAndStayBounded()
    {
        string text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"第 {i} 行：负责某个模块的设计与实现"));
        var chunks = PreparationNotes.Chunk(text).ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= PreparationNotes.ResumeChunkChars));
        Assert.Equal(text.Replace("\n", string.Empty), string.Concat(chunks).Replace("\n", string.Empty));
    }

    private static BuiltPrompt Build(PreparationNotes prep, Scenario scenario, PromptBudgets? budgets = null)
    {
        var time = TimeProvider.System;
        var clock = new SessionClock(time, TimeSpan.FromSeconds(60));
        var store = new TranscriptStore(time);
        var seg = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, 1), "你在订单系统里具体做了什么？", TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(55), AudioAttributionDefault.Pending, null)!;
        var builder = new PromptBuilder(store, new SpeakerRegistry(), new QuestionHistory(), prep, new InsightFilter(time), new CharTokenCounter(), clock, _ => Party.Other);
        if (budgets is not null)
        {
            builder.Budgets = budgets;
        }
        var q = new QuestionCandidate("q-1", 1, seg.Text, [seg.Id], AudioSourceKind.System, null, false, false, null, false, TimeSpan.FromSeconds(55), time.GetUtcNow());
        return builder.Build(new PromptRequest(scenario, AnswerDepth.Normal, "auto", q, [], null));
    }

    [Fact]
    public void ResumeIsSentInInterviewModeAsCachedFactsAboutTheUser()
    {
        var prep = new PreparationNotes();
        prep.Set(PrepField.RoleOrTopic, "高级后端工程师面试");
        prep.SetResume(SampleResume());

        var interview = Build(prep, Scenario.Interview);
        var block = interview.Context[0];
        Assert.True(block.CacheBreakpoint);
        Assert.Contains("Notes written by the user:", block.Text);
        Assert.Contains("The user's résumé \"张三-简历-v2.pdf\" (facts about the user):", block.Text);
        Assert.Contains("P99 从 800 毫秒降到 120 毫秒", block.Text);
        Assert.Contains(prep.Excerpts().First(e => e.Resume is not null).Id, interview.PreparationIds);
        Assert.Contains("résumé", interview.Instructions);

        foreach (var other in new[] { Scenario.Meeting, Scenario.Defense })
        {
            var built = Build(prep, other);
            Assert.DoesNotContain("800 毫秒", string.Concat(built.Context.Select(c => c.Text)) + built.Input);
            Assert.DoesNotContain(built.PreparationIds, id => id.StartsWith("cv-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ResumeLargerThanTheBudgetIsSentAsTheMostRelevantExcerpts()
    {
        var prep = new PreparationNotes();
        string filler = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"项目 {i}：负责数据报表与内部工具的开发维护工作，使用 Python 和 SQL 完成日常需求。"));
        prep.SetResume(SampleResume(filler + "\n\n订单系统缓存改造：P99 从 800 毫秒降到 120 毫秒"));
        var built = Build(prep, Scenario.Interview, new PromptBudgets { Preparation = 300 });
        Assert.DoesNotContain(built.Context, c => c.Text.Contains("résumé \""));
        Assert.Contains("## Preparation excerpts relevant to this request", built.Input);
        Assert.Contains("订单系统缓存改造", built.Input);
        Assert.Contains("omitted", built.Input);
    }
}
