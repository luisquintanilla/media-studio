using MediaStudio.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Copilot;

public sealed class AgentPipelineTests
{
    private sealed class DescribedAgent(AgentRuntimeInfo? info, params string[] replies) : DelegatingAIAgent(new ScriptedAgent(replies))
    {
        public ScriptedAgent Scripted => (ScriptedAgent)InnerAgent;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(AgentRuntimeInfo) ? info : base.GetService(serviceType, serviceKey);
    }

    private static readonly AgentRuntimeInfo Copilot = new(AuthoringRuntime.Copilot, "github-copilot", "model-x", IsCloud: true);
    private static readonly AgentRuntimeInfo Local = new(AuthoringRuntime.Maf, "local", "qwen3.5:4b");

    [Fact]
    public async Task CloudGuardBlocksCloudAgentsWhenCloudIsOff()
    {
        var inner = new DescribedAgent(Copilot, "never");
        var agent = AgentPipeline.Wrap(inner, allowCloud: false);
        var error = await Assert.ThrowsAsync<VideoException>(() => agent.RunAsync("hi"));
        Assert.Equal(CloudGuardAgent.ErrorCode, error.Code);
        Assert.Equal(DecisionRoutingChatClient.CloudDisabledMessage, error.Message);
        await Assert.ThrowsAsync<VideoException>(async () => { await foreach (var _ in agent.RunStreamingAsync("hi")) { } });
        Assert.Empty(inner.Scripted.Transcripts);
    }

    [Fact]
    public async Task UsageReceiptCarriesRuntimeModelAndTokens()
    {
        var copilot = await AgentPipeline.Wrap(new DescribedAgent(Copilot, "done"), allowCloud: true).RunAsync("hi");
        var receipt = AgentPipeline.Receipt(copilot)!;
        Assert.Equal((AuthoringRuntime.Copilot, "model-x", 15L), (receipt.Runtime, receipt.Model, receipt.TotalTokens));
        Assert.Equal("🤖 Copilot · model-x", receipt.Badge());

        var local = AgentPipeline.Wrap(new DescribedAgent(Local, "streamed"), allowCloud: false);
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in local.RunStreamingAsync("hi")) updates.Add(update);
        Assert.Equal("streamed", string.Concat(updates.Select(u => u.Text)));
        var last = AgentPipeline.Receipt(updates[^1])!;
        Assert.Equal("⚡ local · qwen3.5:4b", last.Badge());
        Assert.Equal(15L, last.TotalTokens);
        Assert.Equal(Local, local.GetService<AgentRuntimeInfo>());
    }

    [Fact]
    public async Task UndescribedAgentsPassTheGuard()
    {
        var response = await AgentPipeline.Wrap(new ScriptedAgent("ok"), allowCloud: false).RunAsync("hi");
        Assert.Equal("ok", response.Text);
        Assert.Equal(AuthoringRuntime.Maf, AgentPipeline.Receipt(response)!.Runtime);
    }
}
