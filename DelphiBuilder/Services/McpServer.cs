using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DelphiBuilder;

public sealed record McpToolResult(object Data, bool IsError = false);

public sealed class McpServer : IAsyncDisposable
{
    private const int MaximumLineLength = 64 * 1024;
    private const string LatestProtocolVersion = "2025-11-25";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] EnvelopeProperties = ["jsonrpc", "id", "method", "params"];
    private static readonly string[] InitializeProperties = ["protocolVersion", "capabilities", "clientInfo", "_meta"];
    private static readonly string[] EmptyProperties = ["_meta"];
    private static readonly string[] ListProperties = ["cursor", "_meta"];
    private static readonly string[] CallProperties = ["name", "arguments", "_meta"];
    private static readonly string[] CancelProperties = ["requestId", "reason", "_meta"];
    private static readonly string[] ProjectProperties = ["projectId"];
    private static readonly string[] BuildProperties = ["projectId", "configuration", "platform", "rebuild"];
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
    private static readonly JsonElement ToolDefinitions = JsonSerializer.Deserialize<JsonElement>("""
        [
          {
            "name": "list_projects",
            "description": "列出 DelphiBuilder 已登记的工作区和项目，不扫描或登记任意路径。",
            "inputSchema": {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
          },
          {
            "name": "get_project",
            "description": "读取已登记项目的详情及可用编译配置、平台。",
            "inputSchema": {
              "type": "object",
              "properties": { "projectId": { "type": "integer", "minimum": 1, "maximum": 9223372036854775807 } },
              "required": ["projectId"],
              "additionalProperties": false
            }
          },
          {
            "name": "build_project",
            "description": "编译已登记项目；省略配置或平台时使用项目当前目标。GUI 忙碌或存在未保存编辑时拒绝编译。",
            "inputSchema": {
              "type": "object",
              "properties": {
                "projectId": { "type": "integer", "minimum": 1, "maximum": 9223372036854775807 },
                "configuration": { "type": "string", "minLength": 1, "pattern": "\\S" },
                "platform": { "type": "string", "enum": ["Win32", "Win64"] },
                "rebuild": { "type": "boolean" }
              },
              "required": ["projectId"],
              "additionalProperties": false
            }
          }
        ]
        """);

    private readonly object _sync = new();
    private readonly Func<string, JsonElement, CancellationToken, Task<McpToolResult>> _callTool;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Connection> _connections = [];
    private NamedPipeServerStream? _anchor;
    private NamedPipeServerStream? _listener;
    private Task? _listenersTask;
    private Task? _disposeTask;
    private bool _started;
    private bool _stopping;

    public McpServer(Func<string, JsonElement, CancellationToken, Task<McpToolResult>> callTool, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(callTool);
        _callTool = callTool;
        _pipeName = pipeName ?? GetPipeName(AppContext.BaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(_pipeName);
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_started)
                return;

            // This handle is reused, never rotated away: it owns the name until shutdown.
            var anchor = CreatePipe(firstInstance: true);
            NamedPipeServerStream listener;
            try
            {
                listener = CreatePipe(firstInstance: false);
            }
            catch
            {
                anchor.Dispose();
                throw;
            }

            _anchor = anchor;
            _listener = listener;
            _started = true;
            _listenersTask = Task.Run(() => RunListenersAsync(anchor, listener));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _stopping = true;
                _disposeTask = Task.Run(StopAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    public static string GetPipeName(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant();
        return "DelphiBuilder.Mcp." + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(normalized)));
    }

    public static string CreateClientConfiguration(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["delphi-builder"] = new { command = Path.GetFullPath(executablePath), args = new[] { "--mcp" } }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task<int> RunStdioAsync()
    {
        // CurrentUserOnly also verifies the server owner when used by a Windows pipe client.
        // Identification prevents the server from impersonating the relay with a full user token.
        using var pipe = new NamedPipeClientStream(".", GetPipeName(AppContext.BaseDirectory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"无法连接本目录 DelphiBuilder 的 MCP 服务。请先启动图形界面并在设置中启用 MCP（连接限时 5 秒；仅限当前 Windows 用户）。{exception.Message}").ConfigureAwait(false);
            return 1;
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var relayStop = new CancellationTokenSource();
        var toServer = input.CopyToAsync(pipe, relayStop.Token);
        var fromServer = CopyOutputAsync(pipe, output, relayStop.Token);
        var first = await Task.WhenAny(toServer, fromServer).ConfigureAwait(false);
        var exitCode = 0;
        try
        {
            await first.ConfigureAwait(false);
            if (first == fromServer && !toServer.IsCompletedSuccessfully)
            {
                await Console.Error.WriteLineAsync("DelphiBuilder MCP 连接已关闭；请检查图形界面是否仍在运行且已启用 MCP。").ConfigureAwait(false);
                exitCode = 1;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            await Console.Error.WriteLineAsync($"DelphiBuilder MCP 转发失败：{exception.Message}").ConfigureAwait(false);
            exitCode = 1;
        }
        finally
        {
            await relayStop.CancelAsync().ConfigureAwait(false);
            pipe.Dispose();
            // A Windows standard-input handle need not support cancellation. Never hold process
            // shutdown waiting for it; observe a late failure, while closing the pipe cancels GUI work.
            _ = toServer.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await fromServer.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        return exitCode;
    }

    private static async Task CopyOutputAsync(Stream source, Stream output, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance) => new(_pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private async Task RunListenersAsync(NamedPipeServerStream anchor, NamedPipeServerStream listener)
    {
        var anchorTask = RunAnchorAsync(anchor);
        var acceptTask = AcceptConnectionsAsync(listener);
        await Task.WhenAny(anchorTask, acceptTask).ConfigureAwait(false);
        lock (_sync)
            _stopping = true;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(anchorTask, acceptTask).ConfigureAwait(false);
        }
    }

    private async Task RunAnchorAsync(NamedPipeServerStream anchor)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await anchor.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                Task connectionTask;
                lock (_sync)
                    connectionTask = _stopping ? Task.CompletedTask : StartConnection(anchor, ownsPipe: false);
                await connectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A client can disconnect before the accept/read completes. Keep the owning handle.
            }
            finally
            {
                if (anchor.IsConnected)
                {
                    try { anchor.Disconnect(); }
                    catch (IOException) { }
                }
            }
        }
    }

    private async Task AcceptConnectionsAsync(NamedPipeServerStream listener)
    {
        var current = listener;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await current.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (IOException) when (!_stop.IsCancellationRequested)
                {
                    current.Dispose();
                    lock (_sync)
                    {
                        if (_stopping)
                            return;
                        _listener = current = CreatePipe(firstInstance: false);
                    }
                    continue;
                }
                lock (_sync)
                {
                    if (_stopping)
                        return;
                    var connected = current;
                    // Replace the listener before handing the connection to its worker. The anchor
                    // stays open even if every client disconnects during this rotation.
                    _listener = current = CreatePipe(firstInstance: false);
                    StartConnection(connected, ownsPipe: true);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            current.Dispose();
        }
    }

    // Called with _sync held, so Dispose cannot miss a just-accepted connection.
    private Task StartConnection(NamedPipeServerStream pipe, bool ownsPipe)
    {
        var connection = new Connection(pipe, _stop.Token);
        _connections.Add(connection);
        connection.Completion = Task.Run(async () =>
        {
            try
            {
                await ServeConnectionAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                connection.Dispose();
                if (ownsPipe)
                    pipe.Dispose();
                lock (_sync)
                    _connections.Remove(connection);
            }
        });
        return connection.Completion;
    }

    private async Task StopAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_listenersTask is not null)
                    await _listenersTask.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Task[] connections;
                    lock (_sync)
                        connections = _connections.Select(connection => connection.Completion).ToArray();
                    await Task.WhenAll(connections).ConfigureAwait(false);
                }
                finally
                {
                    _listener?.Dispose();
                    _anchor?.Dispose();
                    _stop.Dispose();
                }
            }
        }
    }

    private async Task ServeConnectionAsync(Connection connection)
    {
        try
        {
            var lines = new BoundedLineReader(connection.Reader);
            while (!connection.Stop.IsCancellationRequested)
            {
                var (text, tooLong) = await lines.ReadAsync(connection.Stop.Token).ConfigureAwait(false);
                if (text is null)
                    break;
                if (tooLong)
                {
                    await connection.SendErrorAsync(null, -32700, "JSON-RPC 输入行不能超过 64 Ki 字符。").ConfigureAwait(false);
                    continue;
                }
                await HandleMessageAsync(connection, text).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
        {
        }
        finally
        {
            try
            {
                await connection.Stop.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                Task[] requests;
                lock (connection.Sync)
                    requests = connection.Work.Select(work => work.Completion).ToArray();
                await Task.WhenAll(requests).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleMessageAsync(Connection connection, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            await connection.SendErrorAsync(null, -32700, "Parse error").ConfigureAwait(false);
            return;
        }
        using (document)
        {
            var message = document.RootElement;
            JsonElement? id = null;
            var hasId = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out _);
            if (hasId)
            {
                var value = message.GetProperty("id");
                if (IsRequestId(value, allowNull: true))
                    id = value;
            }
            if (!HasOnlyProperties(message, EnvelopeProperties) ||
                !message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String ||
                (hasId && id is null))
            {
                await connection.SendErrorAsync(id, -32600, "Invalid Request").ConfigureAwait(false);
                return;
            }

            var method = methodElement.GetString()!;
            message.TryGetProperty("params", out var parameters);
            if (!hasId)
            {
                if (method == "notifications/initialized" && connection.InitializeReceived && IsEmptyParameters(parameters))
                    connection.Initialized = true;
                else if (method == "notifications/cancelled")
                    CancelRequest(connection, parameters);
                return;
            }

            if (method == "initialize")
            {
                if (connection.InitializeReceived)
                {
                    await connection.SendErrorAsync(id, -32600, "此连接已经初始化。").ConfigureAwait(false);
                    return;
                }
                if (!ValidateInitialize(parameters))
                {
                    await connection.SendErrorAsync(id, -32602, "initialize 需要 protocolVersion、capabilities 及含 name/version 的 clientInfo。").ConfigureAwait(false);
                    return;
                }
                var requestedVersion = parameters.GetProperty("protocolVersion").GetString()!;
                var negotiatedVersion = requestedVersion is "2025-06-18" or LatestProtocolVersion
                    ? requestedVersion : LatestProtocolVersion;
                connection.InitializeReceived = true;
                await connection.SendResultAsync(id, new
                {
                    protocolVersion = negotiatedVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "delphi-builder", version = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "1.0.0" }
                }).ConfigureAwait(false);
                return;
            }
            if (method == "ping")
            {
                if (IsEmptyParameters(parameters))
                    await connection.SendResultAsync(id, EmptyObject).ConfigureAwait(false);
                else
                    await connection.SendErrorAsync(id, -32602, "ping 不接受工具参数。").ConfigureAwait(false);
                return;
            }
            if (method is not ("tools/list" or "tools/call"))
            {
                await connection.SendErrorAsync(id, -32601, "Method not found").ConfigureAwait(false);
                return;
            }
            if (!connection.Initialized)
            {
                await connection.SendErrorAsync(id, -32002, "请先完成 initialize 和 notifications/initialized。").ConfigureAwait(false);
                return;
            }
            if (method == "tools/list")
            {
                if (parameters.ValueKind != JsonValueKind.Undefined &&
                    (!HasOnlyProperties(parameters, ListProperties) || !HasValidMetadata(parameters) || parameters.TryGetProperty("cursor", out _)))
                    await connection.SendErrorAsync(id, -32602, "tools/list 不使用分页，参数必须为空对象。").ConfigureAwait(false);
                else
                    await connection.SendResultAsync(id, new { tools = ToolDefinitions }).ConfigureAwait(false);
                return;
            }
            if (!HasOnlyProperties(parameters, CallProperties) || !HasValidMetadata(parameters) ||
                !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                await connection.SendErrorAsync(id, -32602, "tools/call 需要工具名称 name 和对象参数 arguments。").ConfigureAwait(false);
                return;
            }
            var name = nameElement.GetString()!;
            var arguments = parameters.TryGetProperty("arguments", out var suppliedArguments) ? suppliedArguments : EmptyObject;
            if (!ValidateToolArguments(name, arguments))
            {
                await connection.SendErrorAsync(id, -32602, "未知工具或工具参数不符合 inputSchema。").ConfigureAwait(false);
                return;
            }
            var key = RequestKey(id!.Value);
            var duplicate = false;
            lock (connection.Sync)
            {
                if (connection.Requests.ContainsKey(key))
                    duplicate = true;
                else
                {
                    var work = new ToolWork(key, id.Value.Clone(), arguments.Clone(), connection.Stop.Token);
                    connection.Requests.Add(key, work);
                    connection.Work.Add(work);
                    work.Completion = Task.Run(() => RunToolAsync(connection, work, name));
                }
            }
            if (duplicate)
                await connection.SendErrorAsync(id, -32600, "请求 ID 已被活动请求使用。").ConfigureAwait(false);
        }
    }

    private async Task RunToolAsync(Connection connection, ToolWork work, string name)
    {
        try
        {
            McpToolResult result;
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                result = await _callTool(name, work.Arguments, work.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
            {
                if (!connection.Stop.IsCancellationRequested)
                    await connection.SendErrorAsync(work.Id, -32800, "Request cancelled").ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                result = new McpToolResult(new { error = exception.Message }, IsError: true);
            }
            JsonElement data;
            try
            {
                data = JsonSerializer.SerializeToElement(result.Data, JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                await connection.SendErrorAsync(work.Id, -32603, "工具结果无法序列化。").ConfigureAwait(false);
                return;
            }
            await connection.SendResultAsync(work.Id, new
            {
                content = new[] { new { type = "text", text = data.GetRawText() } },
                structuredContent = data,
                isError = result.IsError
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            await connection.Stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await work.DisposeAsync().ConfigureAwait(false);
            lock (connection.Sync)
            {
                connection.Requests.Remove(work.Key);
                connection.Work.Remove(work);
            }
        }
    }

    private static void CancelRequest(Connection connection, JsonElement parameters)
    {
        if (!HasOnlyProperties(parameters, CancelProperties) || !HasValidMetadata(parameters) ||
            !parameters.TryGetProperty("requestId", out var id) || !IsRequestId(id, allowNull: false) ||
            (parameters.TryGetProperty("reason", out var reason) && reason.ValueKind != JsonValueKind.String))
            return;
        ToolWork? work;
        lock (connection.Sync)
            connection.Requests.TryGetValue(RequestKey(id), out work);
        if (work is null)
            return;
        work.Cancel();
    }

    private static bool ValidateInitialize(JsonElement parameters) =>
        HasOnlyProperties(parameters, InitializeProperties) && HasValidMetadata(parameters) &&
        parameters.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString()) &&
        parameters.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("clientInfo", out var clientInfo) && clientInfo.ValueKind == JsonValueKind.Object &&
        clientInfo.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()) &&
        clientInfo.TryGetProperty("version", out var clientVersion) && clientVersion.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(clientVersion.GetString());

    private static bool IsEmptyParameters(JsonElement parameters) => parameters.ValueKind == JsonValueKind.Undefined ||
        (HasOnlyProperties(parameters, EmptyProperties) && HasValidMetadata(parameters));

    private static bool HasValidMetadata(JsonElement parameters) =>
        !parameters.TryGetProperty("_meta", out var metadata) || metadata.ValueKind == JsonValueKind.Object;

    private static bool HasOnlyProperties(JsonElement value, ReadOnlySpan<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var seen = 0;
        foreach (var property in value.EnumerateObject())
        {
            var index = allowed.IndexOf(property.Name);
            if (index < 0 || (seen & (1 << index)) != 0)
                return false;
            seen |= 1 << index;
        }
        return true;
    }

    private static bool IsRequestId(JsonElement id, bool allowNull) =>
        id.ValueKind is JsonValueKind.String or JsonValueKind.Number || (allowNull && id.ValueKind == JsonValueKind.Null);

    private static string RequestKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetRawText();

    private static bool ValidateToolArguments(string name, JsonElement arguments)
    {
        if (name == "list_projects")
            return HasOnlyProperties(arguments, ReadOnlySpan<string>.Empty);
        if (name is not ("get_project" or "build_project") ||
            !HasOnlyProperties(arguments, name == "get_project" ? ProjectProperties : BuildProperties) ||
            !arguments.TryGetProperty("projectId", out var projectId) || projectId.ValueKind != JsonValueKind.Number ||
            !projectId.TryGetInt64(out var value) || value <= 0)
            return false;
        if (name == "get_project")
            return true;
        return (!arguments.TryGetProperty("configuration", out var configuration) ||
                (configuration.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(configuration.GetString()))) &&
            (!arguments.TryGetProperty("platform", out var platform) ||
                (platform.ValueKind == JsonValueKind.String && platform.GetString() is "Win32" or "Win64")) &&
            (!arguments.TryGetProperty("rebuild", out var rebuild) || rebuild.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    private sealed class Connection : IDisposable
    {
        public readonly object Sync = new();
        public readonly CancellationTokenSource Stop;
        public readonly StreamReader Reader;
        public readonly Dictionary<string, ToolWork> Requests = new(StringComparer.Ordinal);
        public readonly HashSet<ToolWork> Work = [];
        private static readonly byte[] Newline = [(byte)'\n'];
        private readonly NamedPipeServerStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public Task Completion = Task.CompletedTask;
        public bool InitializeReceived;
        public bool Initialized;

        public Connection(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            Stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _pipe = pipe;
        }

        public Task SendResultAsync(JsonElement? id, object result) => SendAsync(new { jsonrpc = "2.0", id, result });
        public Task SendErrorAsync(JsonElement? id, int code, string message) => SendAsync(new { jsonrpc = "2.0", id, error = new { code, message } });

        private async Task SendAsync(object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _writeLock.WaitAsync(Stop.Token).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(bytes, Stop.Token).ConfigureAwait(false);
                await _pipe.WriteAsync(Newline, Stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            Reader.Dispose();
            _writeLock.Dispose();
            Stop.Dispose();
        }
    }

    private sealed class ToolWork : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenRegistration _parentRegistration;
        private Task _cancellation = Task.CompletedTask;
        private bool _disposed;
        public readonly string Key;
        public readonly JsonElement Id;
        public readonly JsonElement Arguments;
        public readonly CancellationTokenSource Cancellation = new();
        public Task Completion = Task.CompletedTask;

        public ToolWork(string key, JsonElement id, JsonElement arguments, CancellationToken cancellationToken)
        {
            Key = key;
            Id = id;
            Arguments = arguments;
            _parentRegistration = cancellationToken.Register(static state => ((ToolWork)state!).Cancel(), this);
        }

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_disposed && !Cancellation.IsCancellationRequested) _cancellation = Cancellation.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _parentRegistration.Dispose();
            Task cancellation;
            lock (_sync)
            {
                _disposed = true;
                cancellation = _cancellation;
            }
            // A completed handler can run inline inside a cancellation callback. Do not dispose
            // its source until all remaining callbacks (including other consumers) have run.
            await cancellation.ConfigureAwait(false);
            Cancellation.Dispose();
        }
    }

    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private readonly StringBuilder _line = new();
        private int _offset;
        private int _length;

        public async Task<(string? Text, bool TooLong)> ReadAsync(CancellationToken cancellationToken)
        {
            _line.Clear();
            var tooLong = false;
            while (true)
            {
                if (_offset == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_length == 0)
                        return _line.Length == 0 && !tooLong ? (null, false) : Finish(tooLong);
                }
                var newline = _buffer.AsSpan(_offset, _length - _offset).IndexOf('\n');
                var count = newline < 0 ? _length - _offset : newline;
                if (!tooLong)
                {
                    if (count > MaximumLineLength + 1 - _line.Length)
                        tooLong = true;
                    else
                        _line.Append(_buffer, _offset, count);
                }
                _offset += count;
                if (newline >= 0)
                {
                    _offset++;
                    return Finish(tooLong);
                }
            }
        }

        private (string Text, bool TooLong) Finish(bool tooLong)
        {
            if (_line.Length > 0 && _line[^1] == '\r')
                _line.Length--;
            return tooLong || _line.Length > MaximumLineLength ? (string.Empty, true) : (_line.ToString(), false);
        }
    }
}
