using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeAssistantAcDefender.Services;

/// <summary>One wiki document in the index: its file stem, display title, section, and sort order.</summary>
public sealed record WikiPageMeta(string Name, string Title, string Section, int Order);

/// <summary>A rendered heading captured for the "on this page" table of contents.</summary>
public sealed record WikiTocEntry(int Level, string Id, string Text);

/// <summary>A fully rendered wiki page: sanitized HTML plus its TOC.</summary>
public sealed record WikiDocument(string Name, string Title, string Section, string Html, IReadOnlyList<WikiTocEntry> Toc);

/// <summary>A global-search hit with a short surrounding snippet.</summary>
public sealed record WikiSearchResult(string Name, string Title, string Section, string Snippet);

/// <summary>
/// Serves the in-app Site Wiki from the same markdown GitHub Pages builds (<c>docs/wiki/*.md</c>).
/// Renders with Markdig, rewrites image/internal-link URLs to app routes, extracts a TOC, and builds
/// a lazy full-text search index. Content is the single source of truth — nothing here is generated.
/// </summary>
public sealed class WikiContentService
{
    public const string ImagesRequestPath = "/wikimedia";
    public const string HandbookSection = "Handbook";
    public const string ArticleSection = "Algorithm article";

    private static readonly (string Name, string Title)[] HandbookPages =
    [
        ("Home", "Home"),
        ("Website-Tour", "Website Tour"),
        ("Algorithms", "Algorithms (all 50)"),
        ("Every-Guard-Explained", "Every Guard, Explained Simply"),
        ("Defender-Logic", "Defender Logic"),
        ("Energy-and-Costs", "Energy & Costs"),
        ("Settings", "Settings"),
        ("API", "API"),
        ("Architecture", "Architecture"),
        ("Deployment", "Deployment"),
    ];

    private static readonly Regex NamePattern = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled);
    private static readonly Regex FrontMatterTitle = new("title:\\s*\"?(?<t>[^\"\r\n]+?)\"?\\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ImageSrc = new("(src=\")images/", RegexOptions.Compiled);
    private static readonly Regex InternalLink = new("href=\"(?<p>[A-Za-z0-9-]+)\\.html(?<frag>#[^\"]*)?\"", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex FrontMatterBlock = new("^\\ufeff?---\r?\n.*?\r?\n---\r?\n", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly string _root;
    private readonly MarkdownPipeline _pipeline;
    private readonly ConcurrentDictionary<string, WikiDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IReadOnlyList<WikiPageMeta>> _pages;
    private readonly Lazy<IReadOnlyList<(WikiPageMeta Meta, string Text)>> _index;

    public WikiContentService(IWebHostEnvironment environment)
    {
        _root = Path.Combine(environment.ContentRootPath, "docs", "wiki");
        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseAutoIdentifiers().Build();
        _pages = new Lazy<IReadOnlyList<WikiPageMeta>>(BuildPages);
        _index = new Lazy<IReadOnlyList<(WikiPageMeta, string)>>(BuildIndex);
    }

    public string DefaultPage => "Home";

    public IReadOnlyList<WikiPageMeta> Pages => _pages.Value;

    public IReadOnlyList<WikiPageMeta> Handbook => Pages.Where(p => p.Section == HandbookSection).ToList();

    public IReadOnlyList<WikiPageMeta> Articles => Pages.Where(p => p.Section == ArticleSection).ToList();

    public int DocumentCount => Pages.Count;

    public int ArticleCount => Articles.Count;

    public bool Exists(string name) => Pages.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public WikiPageMeta? Meta(string name) =>
        Pages.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public WikiDocument? Render(string name)
    {
        // Resolve through the indexed metadata first so route casing cannot select a
        // different filesystem path (Linux is case-sensitive) or poison the
        // case-insensitive cache with a null result.
        var canonicalName = Meta(name)?.Name;
        return canonicalName is null ? null : _cache.GetOrAdd(canonicalName, RenderUncached);
    }

    public IReadOnlyList<WikiSearchResult> Search(string query, int max = 10)
    {
        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return Array.Empty<WikiSearchResult>();
        }

        var scored = new List<(int Score, WikiSearchResult Result)>();
        foreach (var (meta, text) in _index.Value)
        {
            var titleHit = meta.Title.ToLowerInvariant().Contains(q) ? 100 : 0;
            var occurrences = CountOccurrences(text, q);
            var score = titleHit + Math.Min(occurrences, 50);
            if (score <= 0)
            {
                continue;
            }

            scored.Add((score, new WikiSearchResult(meta.Name, meta.Title, meta.Section, Snippet(text, q))));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(max)
            .Select(s => s.Result)
            .ToList();
    }

    private WikiDocument? RenderUncached(string name)
    {
        var path = SafePath(name);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var raw = File.ReadAllText(path);
        var (title, body) = StripFrontMatter(raw, name);

        var doc = Markdown.Parse(body, _pipeline);

        var toc = new List<WikiTocEntry>();
        foreach (var heading in doc.Descendants<HeadingBlock>())
        {
            if (heading.Level is not (2 or 3))
            {
                continue;
            }

            var id = heading.GetAttributes().Id;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            toc.Add(new WikiTocEntry(heading.Level, id, HeadingText(heading)));
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();

        var html = PostProcess(writer.ToString());
        var meta = Meta(name);
        return new WikiDocument(name, meta?.Title ?? title, meta?.Section ?? SectionFor(name), html, toc);
    }

    private static string PostProcess(string html)
    {
        // "images/foo.png" is served by a static-file provider mounted at /wikimedia.
        html = ImageSrc.Replace(html, m => m.Groups[1].Value + ImagesRequestPath + "/");
        // "Foo.html" and "Foo.html#frag" are in-app routes.
        html = InternalLink.Replace(html, m => $"href=\"/wiki/{m.Groups["p"].Value}{m.Groups["frag"].Value}\"");
        return html;
    }

    private IReadOnlyList<WikiPageMeta> BuildPages()
    {
        var pages = new List<WikiPageMeta>();
        var order = 0;
        foreach (var (name, title) in HandbookPages)
        {
            if (File.Exists(Path.Combine(_root, $"{name}.md")))
            {
                pages.Add(new WikiPageMeta(name, title, HandbookSection, order++));
            }
        }

        if (Directory.Exists(_root))
        {
            var articles = Directory.EnumerateFiles(_root, "Algorithm-*.md")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => new WikiPageMeta(name, TitleFor(name), ArticleSection, 0))
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var article in articles)
            {
                pages.Add(article with { Order = order++ });
            }
        }

        return pages;
    }

    private IReadOnlyList<(WikiPageMeta, string)> BuildIndex()
    {
        var index = new List<(WikiPageMeta, string)>();
        foreach (var meta in Pages)
        {
            var path = SafePath(meta.Name);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            var (_, body) = StripFrontMatter(File.ReadAllText(path), meta.Name);
            index.Add((meta, PlainText(body)));
        }

        return index;
    }

    private string TitleFor(string name)
    {
        var path = SafePath(name);
        if (path is not null && File.Exists(path))
        {
            var match = FrontMatterTitle.Match(File.ReadAllText(path));
            if (match.Success)
            {
                return match.Groups["t"].Value.Trim();
            }
        }

        return Titleize(name.StartsWith("Algorithm-", StringComparison.Ordinal) ? name["Algorithm-".Length..] : name);
    }

    private static string SectionFor(string name) =>
        name.StartsWith("Algorithm-", StringComparison.Ordinal) ? ArticleSection : HandbookSection;

    private (string Title, string Body) StripFrontMatter(string raw, string name)
    {
        var title = TitleFromRaw(raw) ?? Titleize(name);
        var body = FrontMatterBlock.Replace(raw, string.Empty, 1);
        return (title, body);
    }

    private static string? TitleFromRaw(string raw)
    {
        var match = FrontMatterTitle.Match(raw);
        return match.Success ? match.Groups["t"].Value.Trim() : null;
    }

    private static string Titleize(string slug) =>
        string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private string? SafePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name))
        {
            return null;
        }

        return Path.Combine(_root, $"{name}.md");
    }

    private static string HeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var inline in heading.Inline.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string PlainText(string markdown)
    {
        var text = HtmlTag.Replace(markdown, " ");
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch is '#' or '*' or '`' or '>' or '[' or ']' or '(' or ')' or '!' or '|' or '-' or '_' ? ' ' : ch);
        }

        return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Snippet(string text, string query)
    {
        var hit = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (hit < 0)
        {
            return text.Length <= 140 ? text : text[..140] + "…";
        }

        var start = Math.Max(0, hit - 45);
        var end = Math.Min(text.Length, hit + query.Length + 100);
        var snippet = text[start..end].Trim();
        return (start > 0 ? "…" : string.Empty) + snippet + (end < text.Length ? "…" : string.Empty);
    }
}
