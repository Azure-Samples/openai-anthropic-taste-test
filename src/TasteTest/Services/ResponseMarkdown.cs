using System.Globalization;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace TasteTest.Services;

public static class ResponseMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string ToHtml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using StringWriter writer = new StringWriter(CultureInfo.InvariantCulture);
        HtmlRenderer renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        // Model-generated URLs and images are inert: no script links or remote image requests.
        renderer.ObjectRenderers.Insert(0, new TextOnlyLinkRenderer());
        renderer.ObjectRenderers.Insert(0, new TextOnlyAutolinkRenderer());
        renderer.Render(Markdown.Parse(text, Pipeline));
        return writer.ToString();
    }

    private sealed class TextOnlyLinkRenderer : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline obj) => renderer.WriteChildren(obj);
    }

    private sealed class TextOnlyAutolinkRenderer : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline obj) => renderer.WriteEscape(obj.Url);
    }
}
