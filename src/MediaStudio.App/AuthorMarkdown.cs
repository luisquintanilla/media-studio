using Markdig;
using Markdig.Parsers.Inlines;

namespace MediaStudio.App;

public static class AuthorMarkdown
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().DisableHtml();
        builder.InlineParsers.RemoveAll(parser => parser is LinkInlineParser or AutolinkInlineParser);
        return builder.Build();
    }
    public static string Render(string text) => Markdown.ToHtml(text, Pipeline);
}
