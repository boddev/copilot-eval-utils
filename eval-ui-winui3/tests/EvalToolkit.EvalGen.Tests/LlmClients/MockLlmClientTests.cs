using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

public sealed class MockLlmClientTests
{
    public sealed record Reply(string Name);

    [Fact]
    public async Task ReturnsDefaultResponseWhenNoMatch()
    {
        var client = new MockLlmClient(new Reply("default"));
        Reply result = await client.GenerateStructuredAsync<Reply>("anything", "schema");
        Assert.Equal("default", result.Name);
    }

    [Fact]
    public async Task ReturnsMatchingResponseBySubstring()
    {
        var client = new MockLlmClient(new Reply("default"));
        client.SetResponse("find customers", new Reply("matched"));
        Reply result = await client.GenerateStructuredAsync<Reply>("Please find customers in region X", "schema");
        Assert.Equal("matched", result.Name);
    }

    [Fact]
    public async Task FirstMatchWins()
    {
        var client = new MockLlmClient(new Reply("default"));
        client.SetResponse("find", new Reply("first"));
        client.SetResponse("customers", new Reply("second"));
        Reply result = await client.GenerateStructuredAsync<Reply>("find customers", "schema");
        // Dictionary iteration order in .NET preserves insertion order for the standard generic Dictionary.
        Assert.Equal("first", result.Name);
    }

    [Fact]
    public async Task AuthenticateIsNoop()
    {
        var client = new MockLlmClient();
        await client.AuthenticateAsync();
    }
}
