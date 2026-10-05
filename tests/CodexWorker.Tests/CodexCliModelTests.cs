namespace CodexWorker.Tests;

public sealed class CodexCliModelTests
{
    [Fact]
    public void ReadsOnlyCompleteCliHeaderIgnoringLaterToolOutput()
    {
        const string header = "OpenAI Codex v1.2.3\n--------\nworkdir: /project\nmodel: cli-model\nreasoning effort: medium\n--------\n";
        Assert.Equal("cli-model", CodexCliModel.TryReadStartupHeader(header + "model: invented\n"));
        Assert.Null(CodexCliModel.TryReadStartupHeader(header[..^9]));
        Assert.Null(CodexCliModel.TryReadStartupHeader("user\n" + header));
        Assert.Null(CodexCliModel.TryReadStartupHeader(new string('x', 8192) + header));
    }

    [Theory]
    [InlineData("model: secret with spaces")]
    [InlineData("model: ")]
    [InlineData("model: first\nmodel: second")]
    [InlineData("provider: openai")]
    public void MissingInvalidOrAmbiguousIdentityIsUnavailable(string fields)
    {
        Assert.Null(CodexCliModel.TryReadStartupHeader($"OpenAI Codex v1\n--------\n{fields}\n--------\n"));
    }
}
