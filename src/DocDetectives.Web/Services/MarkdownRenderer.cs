using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace DocDetectives.Web.Services;

/// <summary>Renders LLM answers (Markdown) to HTML, with raw HTML disabled and [n] citations styled.</summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        html = CitationRegex().Replace(html, m => $"<sup class=\"cite\" data-source=\"{m.Groups[1].Value}\">{m.Groups[1].Value}</sup>");
        return new MarkupString(html);
    }

    // Matches [1], [12] but not Markdown links (already rendered to <a> by then).
    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex CitationRegex();
}
