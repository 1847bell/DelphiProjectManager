using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DelphiBuilder;
using Xunit;

namespace DelphiBuilder.Tests;

public sealed class McpServerTests
{
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public Task InitializeNegotiatesSupportedVersionsAndAdvertisesOnlyImplementedCapabilities(string version) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);

        var result = await InitializeAsync(client, cancellationToken, version);

        Assert.Equal(version, result.GetProperty("protocolVersion").GetString());
        var capabilities = result.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("tools").ValueKind);
        Assert.False(capabilities.TryGetProperty("logging", out _));
        Assert.False(capabilities.TryGetProperty("resources", out _));
        Assert.False(capabilities.TryGetProperty("tasks", out _));
        var serverInfo = result.GetProperty("serverInfo");
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()));
    });

    [Fact]
    public Task InitializeOffersASupportedVersionWhenTheClientVersionIsUnknown() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);

        var result = await InitializeAsync(client, cancellationToken, "2099-01-01");

        Assert.Contains(result.GetProperty("protocolVersion").GetString(), new[] { "2025-06-18", "2025-11-25" });
    });

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"protocolVersion":42,"capabilities":{},"clientInfo":{"name":"tests","version":"1"}}""")]
    [InlineData("""{"protocolVersion":null,"capabilities":{},"clientInfo":{"name":"tests","version":"1"}}""")]
    public Task InvalidInitializeParametersAreRejectedWithoutBreakingTheConnection(string parameters) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);

        await client.RequestAsync("invalid-initialize", "initialize", Json(parameters), cancellationToken);
        var response = await ReadAsync(client, cancellationToken);
        AssertId(response, "invalid-initialize");
        RequireError(response, -32602);

        var result = await InitializeAsync(client, cancellationToken);
        Assert.Equal("2025-11-25", result.GetProperty("protocolVersion").GetString());
    });

    [Fact]
    public Task ToolDiscoveryPublishesStrictArgumentSchemas() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync("tools", "tools/list", new { }, cancellationToken);
        var result = RequireResult(await ReadAsync(client, cancellationToken), "tools");
        var tools = result.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        Assert.Equal(new[] { "build_project", "get_project", "list_projects" }, tools.Keys.Order(StringComparer.Ordinal));
        foreach (var tool in tools.Values)
        {
            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        }

        var listSchema = tools["list_projects"].GetProperty("inputSchema");
        Assert.Empty(listSchema.GetProperty("properties").EnumerateObject());
        if (listSchema.TryGetProperty("required", out var listRequired))
            Assert.Empty(listRequired.EnumerateArray());

        var getSchema = tools["get_project"].GetProperty("inputSchema");
        Assert.Equal(new[] { "projectId" }, getSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "projectId" }, getSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertPositiveIntegerSchema(getSchema.GetProperty("properties").GetProperty("projectId"));

        var buildSchema = tools["build_project"].GetProperty("inputSchema");
        Assert.Equal(new[] { "projectId" }, buildSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var buildProperties = buildSchema.GetProperty("properties");
        Assert.Equal(new[] { "configuration", "platform", "projectId", "rebuild" }, buildProperties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        AssertPositiveIntegerSchema(buildProperties.GetProperty("projectId"));
        Assert.Equal("string", buildProperties.GetProperty("configuration").GetProperty("type").GetString());
        Assert.True(buildProperties.GetProperty("configuration").GetProperty("minLength").GetInt32() > 0);
        Assert.Equal("string", buildProperties.GetProperty("platform").GetProperty("type").GetString());
        Assert.Equal(new[] { "Win32", "Win64" }, buildProperties.GetProperty("platform").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("boolean", buildProperties.GetProperty("rebuild").GetProperty("type").GetString());
    });

    [Theory]
    [InlineData("list_projects", "{}")]
    [InlineData("get_project", """{"projectId":42}""")]
    [InlineData("build_project", """{"projectId":42}""")]
    [InlineData("build_project", """{"projectId":42,"configuration":"Release","platform":"Win64","rebuild":true}""")]
    [InlineData("build_project", """{"projectId":42,"configuration":"Debug","platform":"Win32","rebuild":false}""")]
    public Task ValidToolCallsReachTheHandlerAndReturnMatchingTextAndStructuredJson(string toolName, string arguments) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var calls = 0;
        string? actualName = null;
        JsonElement actualArguments = default;
        var data = new { projectId = 42, name = "示例项目", outputs = new[] { "bin/Win32", "bin/Win64" }, completed = true };
        await using var server = new McpServer((name, values, token) =>
        {
            Interlocked.Increment(ref calls);
            actualName = name;
            actualArguments = values.Clone();
            Assert.True(token.CanBeCanceled);
            return Task.FromResult(new McpToolResult(data));
        }, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync(7, "tools/call", new { name = toolName, arguments = Json(arguments) }, cancellationToken);
        var result = RequireResult(await ReadAsync(client, cancellationToken), 7);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(toolName, actualName);
        Assert.True(JsonElement.DeepEquals(Json(arguments), actualArguments));
        AssertToolResult(result, data);
    });

    [Fact]
    public Task ToolDomainErrorsRemainToolResultsRatherThanJsonRpcErrors() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var data = new { error = "busy", message = "已有构建正在运行。" };
        await using var server = new McpServer((_, _, _) => Task.FromResult(new McpToolResult(data, IsError: true)), pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync("busy-build", "tools/call", new { name = "build_project", arguments = new { projectId = 1 } }, cancellationToken);

        AssertToolResult(RequireResult(await ReadAsync(client, cancellationToken), "busy-build"), data, isError: true);
    });

    [Theory]
    [InlineData("list_projects", """{"projectId":1}""")]
    [InlineData("list_projects", "null")]
    [InlineData("list_projects", "[]")]
    [InlineData("list_projects", "1")]
    [InlineData("list_projects", "\"arguments\"")]
    [InlineData("get_project", "{}")]
    [InlineData("get_project", """{"projectId":0}""")]
    [InlineData("get_project", """{"projectId":-1}""")]
    [InlineData("get_project", """{"projectId":1.5}""")]
    [InlineData("get_project", """{"projectId":"1"}""")]
    [InlineData("get_project", """{"projectId":true}""")]
    [InlineData("get_project", """{"projectId":null}""")]
    [InlineData("get_project", """{"projectId":1,"unexpected":true}""")]
    [InlineData("get_project", """{"ProjectId":1}""")]
    [InlineData("build_project", "{}")]
    [InlineData("build_project", """{"projectId":0}""")]
    [InlineData("build_project", """{"projectId":-1}""")]
    [InlineData("build_project", """{"projectId":1.5}""")]
    [InlineData("build_project", """{"projectId":"1"}""")]
    [InlineData("build_project", """{"projectId":true}""")]
    [InlineData("build_project", """{"projectId":null}""")]
    [InlineData("build_project", """{"projectId":1,"configuration":""}""")]
    [InlineData("build_project", """{"projectId":1,"configuration":42}""")]
    [InlineData("build_project", """{"projectId":1,"configuration":null}""")]
    [InlineData("build_project", """{"projectId":1,"platform":"Win16"}""")]
    [InlineData("build_project", """{"projectId":1,"platform":"win64"}""")]
    [InlineData("build_project", """{"projectId":1,"platform":64}""")]
    [InlineData("build_project", """{"projectId":1,"platform":null}""")]
    [InlineData("build_project", """{"projectId":1,"rebuild":"true"}""")]
    [InlineData("build_project", """{"projectId":1,"rebuild":1}""")]
    [InlineData("build_project", """{"projectId":1,"rebuild":null}""")]
    [InlineData("build_project", """{"projectId":1,"command":"dcc64.exe"}""")]
    [InlineData("delete_project", """{"projectId":1}""")]
    [InlineData("", "{}")]
    public Task InvalidToolArgumentsAndUnknownToolsNeverReachTheHandler(string toolName, string arguments) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var calls = 0;
        await using var server = new McpServer((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new McpToolResult(new { }));
        }, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync("invalid-tool", "tools/call", new { name = toolName, arguments = Json(arguments) }, cancellationToken);
        var response = await ReadAsync(client, cancellationToken);
        AssertId(response, "invalid-tool");
        RequireError(response, -32602);
        Assert.Equal(0, Volatile.Read(ref calls));
    });

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"name":42,"arguments":{}}""")]
    [InlineData("""{"name":null,"arguments":{}}""")]
    [InlineData("""{"name":"get_project"}""")]
    public Task InvalidToolCallParametersNeverReachTheHandler(string parameters) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var calls = 0;
        await using var server = new McpServer((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new McpToolResult(new { }));
        }, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync(23, "tools/call", Json(parameters), cancellationToken);
        var response = await ReadAsync(client, cancellationToken);
        AssertId(response, 23);
        RequireError(response, -32602);
        Assert.Equal(0, Volatile.Read(ref calls));
    });

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [InlineData("""{"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":42}""")]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"ping"}""")]
    public Task InvalidJsonRpcRequestsReturnStandardErrorsAndKeepTheSessionUsable(string request) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.SendRawAsync(request, cancellationToken);
        RequireError(await ReadAsync(client, cancellationToken), -32600);

        await client.RequestAsync("after-invalid-request", "ping", new { }, cancellationToken);
        Assert.Empty(RequireResult(await ReadAsync(client, cancellationToken), "after-invalid-request").EnumerateObject());
    });

    [Fact]
    public Task UnknownMethodsReturnMethodNotFound() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        await using var server = new McpServer(UnexpectedToolCall, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.RequestAsync("unknown-method", "resources/list", new { }, cancellationToken);
        var response = await ReadAsync(client, cancellationToken);
        AssertId(response, "unknown-method");
        RequireError(response, -32601);
    });

    [Fact]
    public Task NotificationsProduceNoResponseBeforeTheFollowingPing() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var calls = 0;
        await using var server = new McpServer((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new McpToolResult(new { }));
        }, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.NotifyAsync("notifications/initialized", new { }, cancellationToken);
        await client.NotifyAsync("notifications/unknown", new { }, cancellationToken);
        await client.NotifyAsync("notifications/cancelled", new { requestId = "not-active", reason = "test" }, cancellationToken);
        await client.NotifyAsync("ping", new { }, cancellationToken);
        await client.NotifyAsync("tools/call", new { name = "get_project", arguments = new { projectId = 0 } }, cancellationToken);
        await client.RequestAsync("notification-barrier", "ping", new { }, cancellationToken);

        Assert.Empty(RequireResult(await ReadAsync(client, cancellationToken), "notification-barrier").EnumerateObject());
        Assert.Equal(0, Volatile.Read(ref calls));
    });

    [Fact]
    public Task MalformedJsonReturnsParseErrorAndAValidToolCallStillSucceeds() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var data = new { projects = Array.Empty<object>() };
        await using var server = new McpServer((_, _, _) => Task.FromResult(new McpToolResult(data)), pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);

        await client.SendRawAsync("{not-json", cancellationToken);
        var response = await ReadAsync(client, cancellationToken);
        RequireError(response, -32700);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        await client.RequestAsync("after-bad-json", "ping", new { }, cancellationToken);
        Assert.Empty(RequireResult(await ReadAsync(client, cancellationToken), "after-bad-json").EnumerateObject());
        await client.RequestAsync("projects-after-bad-json", "tools/call", new { name = "list_projects", arguments = new { } }, cancellationToken);
        AssertToolResult(RequireResult(await ReadAsync(client, cancellationToken), "projects-after-bad-json"), data);
    });

    [Fact]
    public Task ConcurrentToolCallsReturnCompleteResponsesCorrelatedToTheirRequestIds() => WithinTimeoutAsync(async cancellationToken =>
    {
        const int count = 6;
        var pipeName = NewPipeName();
        var entered = 0;
        var allEntered = NewSignal();
        var release = NewSignal();
        await using var server = new McpServer(async (_, arguments, token) =>
        {
            var projectId = arguments.GetProperty("projectId").GetInt32();
            if (Interlocked.Increment(ref entered) == count)
                allEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new McpToolResult(new { projectId, status = "complete" });
        }, pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);
        try
        {
            for (var id = 1; id <= count; id++)
                await client.RequestAsync(id, "tools/call", new { name = "get_project", arguments = new { projectId = id } }, cancellationToken);
            await allEntered.Task.WaitAsync(cancellationToken);
            release.TrySetResult();

            var received = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var response = await ReadAsync(client, cancellationToken);
                var id = response.GetProperty("id").GetInt32();
                Assert.InRange(id, 1, count);
                Assert.True(received.Add(id));
                AssertToolResult(RequireResult(response, id), new { projectId = id, status = "complete" });
            }
            Assert.Equal(count, received.Count);
        }
        finally
        {
            release.TrySetResult();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellationTargetsTheExactRequestIdWithoutCancellingAnotherBuild(bool cancelStringId) => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        object cancelledId = cancelStringId ? "17" : 17;
        object survivingId = cancelStringId ? 17 : "17";
        var first = new PendingCall(1);
        var second = new PendingCall(2);
        await using var server = new McpServer((_, arguments, token) =>
            (arguments.GetProperty("projectId").GetInt32() == 1 ? first : second).RunAsync(token), pipeName);
        server.Start();
        using var client = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(client, cancellationToken);
        try
        {
            await client.RequestAsync(cancelledId, "tools/call", new { name = "build_project", arguments = new { projectId = 1 } }, cancellationToken);
            await client.RequestAsync(survivingId, "tools/call", new { name = "build_project", arguments = new { projectId = 2 } }, cancellationToken);
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(cancellationToken);

            await client.NotifyAsync("notifications/cancelled", new { requestId = "not-active" }, cancellationToken);
            await client.RequestAsync("unknown-cancel-barrier", "ping", new { }, cancellationToken);
            Assert.Empty(RequireResult(await ReadAsync(client, cancellationToken), "unknown-cancel-barrier").EnumerateObject());
            Assert.False(first.Cancelled.Task.IsCompleted);
            Assert.False(second.Cancelled.Task.IsCompleted);

            await client.NotifyAsync("notifications/cancelled", new { requestId = cancelledId, reason = "cancel first build" }, cancellationToken);
            await first.Cancelled.Task.WaitAsync(cancellationToken);
            await first.Exited.Task.WaitAsync(cancellationToken);
            await client.RequestAsync("while-building", "ping", new { }, cancellationToken);
            Assert.Empty(RequireResult(await ReadPastCancelledAsync(client, "while-building", cancelledId, cancellationToken), "while-building").EnumerateObject());
            Assert.False(second.Cancelled.Task.IsCompleted);
            Assert.False(second.Exited.Task.IsCompleted);

            second.Complete();
            AssertToolResult(RequireResult(await ReadPastCancelledAsync(client, survivingId, cancelledId, cancellationToken), survivingId), new { projectId = 2 });
            await second.Exited.Task.WaitAsync(cancellationToken);
            Assert.False(second.Cancelled.Task.IsCompleted);
        }
        finally
        {
            first.Complete();
            second.Complete();
        }
    });

    [Fact]
    public Task DisconnectCancelsAllItsCallsButNotAnotherConnectionWithTheSameRequestId() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var calls = new[] { new PendingCall(1), new PendingCall(2), new PendingCall(3) };
        await using var server = new McpServer((_, arguments, token) => calls[arguments.GetProperty("projectId").GetInt32() - 1].RunAsync(token), pipeName);
        server.Start();
        using var disconnected = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        using var survivor = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(disconnected, cancellationToken);
        await InitializeAsync(survivor, cancellationToken);
        try
        {
            await disconnected.RequestAsync(1, "tools/call", new { name = "build_project", arguments = new { projectId = 1 } }, cancellationToken);
            await disconnected.RequestAsync(2, "tools/call", new { name = "build_project", arguments = new { projectId = 2 } }, cancellationToken);
            await survivor.RequestAsync(1, "tools/call", new { name = "build_project", arguments = new { projectId = 3 } }, cancellationToken);
            await Task.WhenAll(calls.Select(call => call.Entered.Task)).WaitAsync(cancellationToken);

            disconnected.Dispose();
            await Task.WhenAll(calls.Take(2).Select(call => call.Cancelled.Task)).WaitAsync(cancellationToken);
            await Task.WhenAll(calls.Take(2).Select(call => call.Exited.Task)).WaitAsync(cancellationToken);
            await survivor.RequestAsync("survivor-ping", "ping", new { }, cancellationToken);
            Assert.Empty(RequireResult(await ReadAsync(survivor, cancellationToken), "survivor-ping").EnumerateObject());
            Assert.False(calls[2].Cancelled.Task.IsCompleted);
            Assert.False(calls[2].Exited.Task.IsCompleted);

            calls[2].Complete();
            AssertToolResult(RequireResult(await ReadAsync(survivor, cancellationToken), 1), new { projectId = 3 });
            using var nextClient = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
            await InitializeAsync(nextClient, cancellationToken);
            await nextClient.RequestAsync("new-connection", "ping", new { }, cancellationToken);
            Assert.Empty(RequireResult(await ReadAsync(nextClient, cancellationToken), "new-connection").EnumerateObject());
        }
        finally
        {
            foreach (var call in calls)
                call.Complete();
        }
    });

    [Fact]
    public Task DisposeCancelsEveryConnectionWaitsForHandlersAndStopsAcceptingClients() => WithinTimeoutAsync(async cancellationToken =>
    {
        var pipeName = NewPipeName();
        var entered = new[] { NewSignal(), NewSignal() };
        var cancelled = new[] { NewSignal(), NewSignal() };
        var mayExit = new[] { NewSignal(), NewSignal() };
        var exited = new[] { NewSignal(), NewSignal() };
        var release = NewSignal();
        await using var server = new McpServer(async (_, arguments, token) =>
        {
            var index = arguments.GetProperty("projectId").GetInt32() - 1;
            using var registration = token.Register(() => cancelled[index].TrySetResult());
            entered[index].TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return new McpToolResult(new { projectId = index + 1 });
            }
            finally
            {
                await mayExit[index].Task.WaitAsync(cancellationToken);
                exited[index].TrySetResult();
            }
        }, pipeName);
        server.Start();
        using var first = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        using var second = await PipeConnection.ConnectAsync(pipeName, cancellationToken);
        await InitializeAsync(first, cancellationToken);
        await InitializeAsync(second, cancellationToken);
        try
        {
            await first.RequestAsync(1, "tools/call", new { name = "build_project", arguments = new { projectId = 1 } }, cancellationToken);
            await second.RequestAsync(1, "tools/call", new { name = "build_project", arguments = new { projectId = 2 } }, cancellationToken);
            await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(cancellationToken);

            var disposal = server.DisposeAsync().AsTask();
            await Task.WhenAll(cancelled.Select(signal => signal.Task)).WaitAsync(cancellationToken);
            Assert.False(disposal.IsCompleted);
            mayExit[0].TrySetResult();
            await exited[0].Task.WaitAsync(cancellationToken);
            Assert.False(disposal.IsCompleted);
            mayExit[1].TrySetResult();
            await disposal.WaitAsync(cancellationToken);
            await Task.WhenAll(exited.Select(signal => signal.Task)).WaitAsync(cancellationToken);
            await ExpectDisconnectAsync(first, cancellationToken);
            await ExpectDisconnectAsync(second, cancellationToken);

            using var probe = NewClient(pipeName);
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            var exception = await Record.ExceptionAsync(() => probe.ConnectAsync(probeTimeout.Token));
            Assert.NotNull(exception);
            Assert.True(exception is OperationCanceledException or TimeoutException or IOException, exception.ToString());
            Assert.False(probe.IsConnected);
        }
        finally
        {
            release.TrySetResult();
            foreach (var signal in mayExit)
                signal.TrySetResult();
        }
    });

    private static async Task WithinTimeoutAsync(Func<CancellationToken, Task> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await action(timeout.Token).WaitAsync(timeout.Token);
    }

    private static string NewPipeName() => "DelphiBuilder.Tests.Mcp." + Guid.NewGuid().ToString("N");

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static Task<McpToolResult> UnexpectedToolCall(string name, JsonElement arguments, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"Unexpected tool handler invocation: {name}");

    private static NamedPipeClientStream NewClient(string pipeName) => new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<JsonElement> InitializeAsync(PipeConnection client, CancellationToken cancellationToken, string version = "2025-11-25")
    {
        await client.RequestAsync("initialize", "initialize", new
        {
            protocolVersion = version,
            capabilities = new { },
            clientInfo = new { name = "DelphiBuilder.Tests", version = "1.0" }
        }, cancellationToken);
        var result = RequireResult(await ReadAsync(client, cancellationToken), "initialize");
        await client.NotifyAsync("notifications/initialized", new { }, cancellationToken);
        return result;
    }

    private static async Task<JsonElement> ReadAsync(PipeConnection client, CancellationToken cancellationToken)
    {
        var line = await client.ReadLineAsync(cancellationToken);
        Assert.NotNull(line);
        return Json(line);
    }

    private static async Task<JsonElement> ReadPastCancelledAsync(PipeConnection client, object expectedId, object cancelledId, CancellationToken cancellationToken)
    {
        var response = await ReadAsync(client, cancellationToken);
        if (JsonElement.DeepEquals(response.GetProperty("id"), JsonSerializer.SerializeToElement(expectedId)))
            return response;
        AssertEnvelope(response);
        AssertId(response, cancelledId);
        return await ReadAsync(client, cancellationToken);
    }

    private static async Task ExpectDisconnectAsync(PipeConnection client, CancellationToken cancellationToken)
    {
        try
        {
            while (await client.ReadLineAsync(cancellationToken) is { } line)
                AssertEnvelope(Json(line));
        }
        catch (IOException)
        {
            // A closed pipe may be reported as a broken-pipe error instead of EOF.
        }
    }

    private static void AssertEnvelope(JsonElement response)
    {
        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.True(response.TryGetProperty("id", out _));
        Assert.NotEqual(response.TryGetProperty("result", out _), response.TryGetProperty("error", out _));
    }

    private static void AssertId(JsonElement response, object id) =>
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(id), response.GetProperty("id")), response.GetRawText());

    private static JsonElement RequireResult(JsonElement response, object id)
    {
        AssertEnvelope(response);
        AssertId(response, id);
        Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
        return response.GetProperty("result");
    }

    private static void RequireError(JsonElement response, int code)
    {
        AssertEnvelope(response);
        Assert.False(response.TryGetProperty("result", out _), response.GetRawText());
        var error = response.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private static void AssertToolResult(JsonElement result, object data, bool isError = false)
    {
        var expected = JsonSerializer.SerializeToElement(data);
        Assert.True(JsonElement.DeepEquals(expected, result.GetProperty("structuredContent")), result.GetRawText());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(expected, Json(content.GetProperty("text").GetString()!)));
        if (isError)
            Assert.True(result.GetProperty("isError").GetBoolean());
        else if (result.TryGetProperty("isError", out var actualError))
            Assert.False(actualError.GetBoolean());
    }

    private static void AssertPositiveIntegerSchema(JsonElement schema)
    {
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.True((schema.TryGetProperty("minimum", out var minimum) && minimum.GetDouble() >= 1)
            || (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && exclusiveMinimum.GetDouble() >= 0));
    }

    private sealed class PendingCall(int projectId)
    {
        private readonly TaskCompletionSource _release = NewSignal();
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Cancelled { get; } = NewSignal();
        public TaskCompletionSource Exited { get; } = NewSignal();

        public async Task<McpToolResult> RunAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new McpToolResult(new { projectId });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }

        public void Complete() => _release.TrySetResult();
    }

    private sealed class PipeConnection : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;

        private PipeConnection(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        }

        public static async Task<PipeConnection> ConnectAsync(string pipeName, CancellationToken cancellationToken)
        {
            var pipe = NewClient(pipeName);
            try
            {
                await pipe.ConnectAsync(cancellationToken);
                return new PipeConnection(pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public Task RequestAsync(object id, string method, object parameters, CancellationToken cancellationToken) =>
            SendRawAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), cancellationToken);

        public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
            SendRawAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }), cancellationToken);

        public async Task SendRawAsync(string message, CancellationToken cancellationToken) =>
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), cancellationToken);

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _reader.ReadLineAsync(cancellationToken);

        public void Dispose()
        {
            _reader.Dispose();
            _pipe.Dispose();
        }
    }
}
