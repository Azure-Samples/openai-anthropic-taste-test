using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class ResponseMarkdownTests
{
    [Fact]
    public void ToHtml_RendersHeadingsListsEmphasisAndCode()
    {
        var html = ResponseMarkdown.ToHtml("## Example\n\n**Important**\n\n- One\n- Two\n\n```csharp\nvar x = 1;\n```");
        Assert.Contains("<h2>Example</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Important</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<li>One</li>", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://example.org\"></iframe>")]
    [InlineData("[click](javascript:alert%281%29)")]
    [InlineData("![tracking](https://example.org/pixel)")]
    [InlineData("<https://example.org>")]
    [InlineData("[encoded](java&#x73;cript:alert%281%29)")]
    public void ToHtml_DoesNotEmitActiveContentOrRemoteImages(string markdown)
    {
        var html = ResponseMarkdown.ToHtml(markdown);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
    }
}
