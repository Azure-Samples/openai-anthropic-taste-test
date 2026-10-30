using System.Text.RegularExpressions;

namespace TasteTest.Tests;

public sealed class PageMetadataTests
{
    [Fact]
    public void AppDescription_DoesNotDiscloseProviders()
    {
        string source = File.ReadAllText(Path.Combine(
            SdkLiveTests.RepositoryRoot(), "src", "TasteTest", "Components", "App.razor"));
        Match description = Regex.Match(source, """<meta name="description" content="(?<text>[^"]+)"\s*/>""");

        Assert.True(description.Success);
        Assert.DoesNotMatch(new Regex("OpenAI|Anthropic|gpt-|claude-|Responses API|Messages API", RegexOptions.IgnoreCase),
            description.Groups["text"].Value);
    }
}
