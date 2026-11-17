using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using GitHub.Copilot;
using MediaStudio.Agents;
using MediaStudio.Agents.Copilot;
using MediaStudio.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction;
using VideoProduction.Providers;
using VideoProduction.Providers.Decisions;

namespace MediaStudio.Tests.Copilot;

#pragma warning disable GHCP001

public sealed class CopilotTests
{
    private static readonly ConnectionSettings Local = new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = "http://localhost:11434/v1" };
    private static readonly ConnectionSettings Azure = new() { Id = "azure", Kind = ConnectionKind.Foundry, Endpoint = "https://example.services.ai.azure.com/", Auth = AuthMode.Entra };
    private static readonly ConnectionSettings Subscription = new() { Id = "copilot", Kind = ConnectionKind.GitHubCopilot, Auth = AuthMode.LoggedInUser };

    private static ProviderSettings Settings(bool allowCloud, CopilotBackend backend = CopilotBackend.Subscription,
        string? byok = null, params ChatTier[] tiers) => new() {
            Privacy = new() { AllowCloud = allowCloud },
            Connections = [Local, Azure, Subscription],
            Chat = new() { Tiers = tiers },
            Decisions = new() { Julia = JuliaMode.Off },
            Agents = new() { Copilot = new() { Backend = backend, Model = "model-x", ByokConnection = byok } }
        };

    private static CopilotClientFactory Clients(ProviderSettings settings, Action? started = null) =>
        new(settings, homeDirectory: Path.Combine(Path.GetTempPath(), "ms-copilot-test-" + Guid.NewGuid().ToString("N")),
            cliPath: Environment.ProcessPath!, environment: _ => null,
            start: (_, _) => { started?.Invoke(); throw new InvalidOperationException("The fake runtime never starts."); });

    [Theory]
    [InlineData(AgentRole.Authoring, 1)]
    [InlineData(AgentRole.Illustration, 0)]
    [InlineData(AgentRole.Tier, 0)]
    public async Task SessionPolicyLocksEveryAmbientCapability(AgentRole role, int toolCount)
    {
        AIFunction[] tools = toolCount == 0 ? [] : [AIFunctionFactory.Create(() => "ok", "allowed_tool")];
        var config = CopilotSessionPolicy.Create(role, "model-x", tools, null, Path.Combine(Path.GetTempPath(), "work"));
        Assert.Equal(tools.Select(t => t.Name), config.AvailableTools!);
        Assert.Equal(toolCount, config.Tools!.Count);
        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableOnDemandInstructionDiscovery);
        Assert.False(config.EnableFileHooks);
        Assert.False(config.EnableSkills);
        Assert.False(config.EnableHostGitOperations);
        Assert.False(config.EnableSessionStore);
        Assert.True(config.SkipCustomInstructions);
        Assert.True(config.SkipEmbeddingRetrieval);
        Assert.Empty(config.McpServers!);
        Assert.Empty(config.CustomAgents!);
        Assert.Empty(config.AdditionalDirectories!);
        Assert.False(config.InfiniteSessions!.Enabled);
        Assert.Equal(SystemMessageMode.Replace, config.SystemMessage!.Mode);
        Assert.False(string.IsNullOrWhiteSpace(config.SystemMessage.Content));
        Assert.IsType<GitHub.Copilot.Rpc.PermissionDecisionReject>(await config.OnPermissionRequest!(null!, null!));
        foreach (var name in new[] { "shell", "view", "web_fetch", "write_file", "task" })
            Assert.Equal("deny", (await config.Hooks!.OnPreToolUse!(new() { ToolName = name }, null!))!.PermissionDecision);
        Assert.Equal(toolCount == 0 ? "deny" : "allow",
            (await config.Hooks!.OnPreToolUse!(new() { ToolName = "allowed_tool" }, null!))!.PermissionDecision);
    }

    [Fact]
    public void OnlyAuthoringMayRegisterTools() =>
        Assert.Throws<ArgumentException>(() => CopilotSessionPolicy.Create(AgentRole.Tier, null,
            [AIFunctionFactory.Create(() => "x", "t")], null, Path.GetTempPath()));

    [Fact]
    public async Task ByokMapsLocalAndFoundryConnections()
    {
        var local = CopilotAgentFactory.CreateByokProvider(Local, apiKeys: _ => throw new InvalidOperationException("no key"));
        Assert.Equal(("openai", "http://localhost:11434/v1", "completions"), (local.Type, local.BaseUrl, local.WireApi));
        Assert.Null(local.ApiKey);

        var credential = new FakeCredential();
        var foundry = CopilotAgentFactory.CreateByokProvider(Azure, _ => credential);
        Assert.Equal(("openai", "responses"), (foundry.Type, foundry.WireApi));
        Assert.Equal(FoundryEndpoints.From(Azure).OpenAIv1.AbsoluteUri.TrimEnd('/'), foundry.BaseUrl);
        Assert.Null(foundry.ApiKey);
        Assert.Equal("fake-token", await foundry.BearerTokenProvider!(null!));
        Assert.Equal([FoundryEndpoints.CognitiveServicesScope], credential.Scopes);

        var keyed = CopilotAgentFactory.CreateByokProvider(Azure with { Auth = AuthMode.ApiKey }, apiKeys: id => "key-for-" + id);
        Assert.Equal("key-for-azure", keyed.ApiKey);
        Assert.Null(keyed.BearerTokenProvider);

        Assert.Throws<VideoException>(() => CopilotAgentFactory.CreateByokProvider(Subscription));
    }

    [Fact]
    public async Task SubscriptionNeverStartsWhenCloudIsOff()
    {
        var started = 0;
        await using var clients = Clients(Settings(false), () => started++);
        Assert.True(clients.IsCloud);
        var error = await Assert.ThrowsAsync<VideoException>(() => clients.GetClientAsync());
        Assert.Equal("copilot.cloud-disabled", error.Code);
        Assert.Equal(DecisionRoutingChatClient.CloudDisabledMessage, error.Message);
        // Creating agents and tier clients never starts the runtime either.
        var agents = new CopilotAgentFactory(clients);
        var agent = agents.Create(AgentRole.Tier, [], null);
        Assert.Equal(new AgentRuntimeInfo(AuthoringRuntime.Copilot, "github-copilot", "model-x", true), agent.GetService<AgentRuntimeInfo>());
        await Assert.ThrowsAsync<VideoException>(() => agent.RunAsync("hi"));
        var probe = await new CopilotProbe(clients).ProbeAsync();
        Assert.False(probe.Authenticated);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task LocalByokIsNotCloudAndStartsWithoutSignIn()
    {
        var started = 0;
        await using var clients = Clients(Settings(false, CopilotBackend.Byok, "local"), () => started++);
        Assert.False(clients.IsCloud);
        var options = clients.CreateOptions();
        Assert.False(options.UseLoggedInUser);
        Assert.Null(options.GitHubToken);
        Assert.Equal(clients.ConfigDirectory, options.Environment!["COPILOT_HOME"]);
        Assert.Equal("true", options.Environment["COPILOT_GITHUB_TELEMETRY_DISABLED"]);
        Assert.Equal(["model-x"], (await options.OnListModels!(default)).Select(m => m.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => clients.GetClientAsync());
        Assert.Equal(1, started);
        var provider = new CopilotAgentFactory(clients).CreateProvider();
        Assert.Equal("http://localhost:11434/v1", provider!.BaseUrl);
        Assert.False(new CopilotAgentFactory(clients).RuntimeInfo().IsCloud);
    }

    [Fact]
    public void EnvironmentTokenIsUsedOnlyInEnvTokenModeAndNeverInherited()
    {
        var settings = Settings(true) with { Connections = [Subscription with { Auth = AuthMode.EnvToken }] };
        using var clients = new CopilotClientFactory(settings, Path.GetTempPath(), Environment.ProcessPath!,
            name => name is "GH_TOKEN" ? "token-from-env" : name is "PATH" ? "path" : null);
        var options = clients.CreateOptions();
        Assert.Equal("token-from-env", options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
        Assert.DoesNotContain("GH_TOKEN", options.Environment!.Keys);
        Assert.Equal("path", options.Environment["PATH"]);
    }

    [Fact]
    public async Task RouterNeverSelectsTheCopilotTierWhenCloudIsOff()
    {
        var settings = Settings(false, tiers: [new() { Name = "fast", Connection = "local", Model = "small" },
            new() { Name = "copilot", Connection = "copilot", Model = "model-x" }]);
        var services = new ServiceCollection().AddMediaStudioCopilot(settings);
        await using var provider = services.BuildServiceProvider();
        using var router = ProviderFactory.CreateRouter(settings, "authoring", () => new HeuristicDecisionClient(),
            provider.GetServices<ITierChatClientFactory>());
        Assert.Null(router.Tiers.Single(t => t.Name == "copilot").UnavailableReason);
        var selection = await router.SelectAsync([new ChatMessage(ChatRole.User, new string('x', 20_000))], new());
        Assert.Equal("fast", selection.SelectedRoute);
        Assert.False(selection.IsCloud);
    }

    [Fact]
    public async Task TierClientIsStatelessReportsMetadataAndRepairsJson()
    {
        var agent = new ScriptedAgent("```json\n{\"a\":1}\n```", "not json", "{\"b\":2}");
        using var client = new CopilotTierChatClient(agent, "model-x");
        Assert.Equal(("github-copilot", "model-x"), (client.GetService<ChatClientMetadata>()!.ProviderName, client.GetService<ChatClientMetadata>()!.DefaultModelId));

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], new() { ResponseFormat = ChatResponseFormat.Json });
        Assert.Equal("{\"a\":1}", first.Text);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "two")], new() { ResponseFormat = ChatResponseFormat.Json });
        Assert.Equal("{\"b\":2}", second.Text);
        Assert.Equal(3, agent.Sessions.Count);
        Assert.Equal(3, agent.Sessions.Distinct().Count());
        Assert.Contains(agent.Transcripts[2], m => m.Text == CopilotTierChatClient.RepairInstruction);
        Assert.DoesNotContain(agent.Transcripts[1], m => m.Text == "one");
        Assert.Equal("model-x", second.ModelId);
    }

    [Fact]
    public async Task TierClientFailsAfterOneRepairAndStreamsPlainText()
    {
        var agent = new ScriptedAgent("nope", "still nope", "plain answer");
        using var client = new CopilotTierChatClient(agent, "model-x");
        var error = await Assert.ThrowsAsync<VideoException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], new() { ResponseFormat = ChatResponseFormat.Json }));
        Assert.Equal("copilot.invalid-json", error.Code);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")])) updates.Add(update);
        Assert.Equal("plain answer", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public void DeviceLoginParsesRecordedOutput()
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Copilot", "fixtures", "device-login.txt"));
        var code = CopilotDeviceLogin.Parse(sample);
        Assert.Equal(new CopilotDeviceCode("AB12-CD34", CopilotDeviceLogin.DeviceUri), code);
        Assert.Null(CopilotDeviceLogin.Parse("Code AB12-CD34 without the page"));
        Assert.Null(CopilotDeviceLogin.Parse("Open https://github.com/login/device later"));
    }

    [Fact]
    public async Task DeviceLoginReadsUntilCodeAndTimesOutOrCancels()
    {
        using var reader = new StringReader("Starting...\nOpen https://github.com/login/device\nand enter WXYZ-1234\n");
        Assert.Equal("WXYZ-1234", (await CopilotDeviceLogin.ReadCodeAsync(reader, TimeSpan.FromSeconds(5)))!.UserCode);
        Assert.Null(await CopilotDeviceLogin.ReadCodeAsync(new StringReader("format changed"), TimeSpan.FromSeconds(5)));

        var polls = 0;
        Assert.True(await CopilotDeviceLogin.WaitForSignInAsync(_ => Task.FromResult(++polls == 3), TimeSpan.FromMilliseconds(1)));
        Assert.Equal(3, polls);
        Assert.False(await CopilotDeviceLogin.WaitForSignInAsync(_ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopilotDeviceLogin.WaitForSignInAsync(_ => Task.FromResult(false), cancellationToken: cancelled.Token));
    }

    [Theory]
    [InlineData("User is not entitled to Copilot", CopilotProbe.NotEntitled)]
    [InlineData("Blocked by organization policy", CopilotProbe.PolicyBlocked)]
    [InlineData("Something else", null)]
    public void ProbeMapsEntitlementAndPolicyErrors(string message, string? expected) =>
        Assert.Equal(expected, CopilotProbe.MapError(message));

    [Fact]
    public void ProbeDefaultsToTheUsersOwnSonnetOrGptModel()
    {
        Assert.Equal("claude-sonnet-x", CopilotProbe.DefaultModel(["o-mini", "gpt-y", "claude-sonnet-x"]));
        Assert.Equal("gpt-y", CopilotProbe.DefaultModel(["o-mini", "gpt-y"]));
        Assert.Null(CopilotProbe.DefaultModel([]));
    }

    [Fact]
    public void AuthoringRuntimesDescribeThemselves()
    {
        using var clients = Clients(Settings(true));
        var agents = new CopilotAgentFactory(clients);
        var info = agents.Create(AgentRole.Authoring, [AIFunctionFactory.Create(() => "ok", "edit_media_draft")], "Author.")
            .GetService<AgentRuntimeInfo>();
        Assert.Equal(AuthoringRuntime.Copilot, info!.Runtime);
        Assert.True(info.IsCloud);
        var byok = new CopilotAgentFactory(Clients(Settings(true, CopilotBackend.Byok, "local"))).RuntimeInfo();
        Assert.Equal(("copilot-byok:local", false), (byok.Provider, byok.IsCloud));
    }

    [Fact]
    public void AddMediaStudioCopilotRegistersTheTierRuntime()
    {
        var settings = Settings(true, tiers: [new() { Name = "copilot", Connection = "copilot", Model = "model-x" }]);
        using var services = new ServiceCollection().AddMediaStudioCopilot(settings).AddMediaStudioAI(settings).BuildServiceProvider();
        Assert.Same(services.GetRequiredService<CopilotClientFactory>(), services.GetRequiredService<CopilotAgentFactory>().Clients);
        var router = services.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Chat).GetService<DecisionRoutingChatClient>()!;
        Assert.Null(router.Tiers.Single().UnavailableReason);
        using var tier = router.Tiers.Single().CreateClient();
        Assert.Equal("github-copilot", tier.GetService<ChatClientMetadata>()!.ProviderName);
    }

    [Fact]
    public async Task CopilotIllustrationRuntimeReplacesTheIllustrationRouterOnly()
    {
        var settings = Settings(false, tiers: [new() { Name = "fast", Connection = "local", Model = "small" }]) with {
            Agents = new() { Illustration = new() { Runtime = AgentRuntime.Copilot }, Copilot = new() { Model = "model-x" } }
        };
        var started = 0;
        var services = new ServiceCollection().AddSingleton(Clients(settings, () => started++))
            .AddMediaStudioCopilot(settings).AddMediaStudioAI(settings);
        await using var provider = services.BuildServiceProvider();
        var illustration = provider.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Illustration);
        Assert.Null(illustration.GetService<DecisionRoutingChatClient>());
        Assert.Equal("github-copilot", illustration.GetService<ChatClientMetadata>()!.ProviderName);
        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Chat).GetService<DecisionRoutingChatClient>());
        var error = await Assert.ThrowsAsync<VideoException>(() => illustration.GetResponseAsync("Draw a river."));
        Assert.Equal("copilot.cloud-disabled", error.Code);
#pragma warning disable MEAI001
        var image = provider.GetRequiredKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration);
        var imageError = await Assert.ThrowsAsync<VideoException>(() => image.GenerateAsync(new("Draw a river.")));
#pragma warning restore MEAI001
        Assert.Equal("copilot.cloud-disabled", imageError.Code);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task ConfigurationCheckAddsCopilotRowsAndHonorsPrivacy()
    {
        var tier = new ChatTier { Name = "copilot", Connection = "copilot", Model = "model-x" };
        var started = 0;
        var check = new CopilotConfigurationCheck(s => Clients(s, () => started++));

        Assert.Empty(await check.CheckAsync(Settings(true), cloudOnly: false, CancellationToken.None));

        var off = await check.CheckAsync(Settings(false, tiers: tier), cloudOnly: false, CancellationToken.None);
        var skipped = Assert.Single(off);
        Assert.Equal(("copilot.auth", RoleCheckStatus.Skipped), (skipped.Role, skipped.Status));
        Assert.Equal(0, started);

        var failed = Assert.Single(await check.CheckAsync(Settings(true, tiers: tier), cloudOnly: false, CancellationToken.None));
        Assert.Equal(("copilot.auth", RoleCheckStatus.Failed), (failed.Role, failed.Status));
        Assert.Contains("never starts", failed.Remediation, StringComparison.Ordinal);
        Assert.Equal(1, started);

        var byok = Settings(false, CopilotBackend.Byok, "local") with { Agents = Settings(false, CopilotBackend.Byok, "local").Agents with { Authoring = new() { Runtime = AgentRuntime.Copilot } } };
        Assert.Equal("copilot.byok", Assert.Single(await check.CheckAsync(byok, cloudOnly: false, CancellationToken.None)).Role);
        Assert.Empty(await check.CheckAsync(byok, cloudOnly: true, CancellationToken.None));

        var rows = await new ConfigurationTester(extensions: [check]).TestAsync(Settings(false, tiers: tier) with { Privacy = new() { AllowCloud = true } }, cloudOnly: true);
        Assert.Equal("see the copilot.* rows", rows.Single(r => r.Role == "chat:copilot").Detail);
        Assert.Contains(rows, r => r.Role == "copilot.auth");
    }

    [Fact]
    [Trait("Category", "Copilot")]
    public async Task LiveSubscriptionStatusAndOneSentence()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN") is null) return;
        var settings = Settings(true) with {
            Connections = [Subscription with { Auth = AuthMode.EnvToken }],
            Agents = new() { Copilot = new() { Model = null } }
        };
        await using var clients = new CopilotClientFactory(settings);
        var status = await new CopilotProbe(clients).ProbeAsync();
        Assert.True(status.Ready, status.Remediation);
        Assert.NotEmpty(status.Models);
        using var tier = new CopilotTierChatClient(new CopilotAgentFactory(clients).Create(AgentRole.Tier, [], null,
            CopilotProbe.DefaultModel(status.Models)), CopilotProbe.DefaultModel(status.Models));
        var reply = await tier.GetResponseAsync("Reply with one short sentence about rivers.");
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}

/// <summary>A fake <see cref="AIAgent"/> that replies with scripted text and records each session and transcript.</summary>
internal sealed class ScriptedAgent(params string[] replies) : AIAgent
{
    private int next;
    public List<AgentSession> Sessions { get; } = [];
    public List<List<ChatMessage>> Transcripts { get; } = [];

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var session = new FakeSession();
        Sessions.Add(session);
        return new(session);
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        Transcripts.Add([.. messages]);
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, replies[next++])) {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 }
        });
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates()) yield return update;
    }

    private sealed class FakeSession : AgentSession;
}
