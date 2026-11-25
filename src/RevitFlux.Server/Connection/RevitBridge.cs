using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitFlux.Server.Connection;

/// <summary>
/// TCP bridge to the RevitFlux plugin running inside Revit.
/// Sends JSON-RPC requests and reads line-delimited responses.
/// </summary>
public sealed class RevitBridge : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _commandTimeout;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _requestCounter;

    public RevitBridge(string host = "127.0.0.1", int port = 8090, int commandTimeoutSeconds = 300)
    {
        _host = host;
        _port = port;
        _commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { Connected: true }) return;

        Disconnect();
        _client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _client.ConnectAsync(_host, _port, cts.Token);
        }
        catch (SocketException ex)
        {
            throw new RevitUnreachableException($"SocketError: {ex.SocketErrorCode}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RevitUnreachableException("connection attempt timed out", ex);
        }
        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// Send a JSON-RPC command to the Revit plugin and return the result.
    /// Opens a TCP connection per call (same pattern as the TS server).
    /// </summary>
    public async Task<JToken> SendCommandAsync(string method, JObject parameters, CancellationToken ct = default)
    {
        // Failures are returned, not thrown: MCP clients hide exception messages from the model.
        try
        {
            await EnsureConnectedAsync(ct);
        }
        catch (RevitUnreachableException ex)
        {
            return Failure("RevitNotConnected",
                $"RevitFlux is not reachable on {_host}:{_port} ({ex.Message}). Nothing was run.",
                "Ask the user to open Revit with a project and click RevitFlux Switch on the RevitFlux ribbon tab " +
                "(it is off each time Revit starts), then retry. If the port was changed, check REVITFLUX_PORT " +
                "and ~/.revitflux/settings.json.");
        }

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
            ["id"] = id
        };
        var sent = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_commandTimeout);

        while (true)
        {
            string? line;
            try
            {
                if (!sent)
                {
                    sent = true;
                    await _writer!.WriteLineAsync(request.ToString(Formatting.None));
                }
                line = await _reader!.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failure("Timeout",
                    $"'{method}' got no reply from Revit within {_commandTimeout.TotalSeconds}s. It may still be running or may have completed.",
                    OutcomeUnknown);
            }
            catch (IOException)
            {
                line = null;
            }
            if (line == null)
                return Failure("ConnectionLost",
                    $"Revit closed the connection before replying to '{method}'. The operation may or may not have run.",
                    OutcomeUnknown);

            var response = JObject.Parse(line);
            if (response["id"]?.ToString() != id) continue;

            if (response["error"] != null)
            {
                // The plugin uses JSON-RPC `error` as a semantic application-failure channel
                // (CortexResult.Fail). MCP clients render any thrown exception as an opaque
                // "An error occurred invoking <tool>", hiding code/message/suggestion from the
                // model. So we surface the structured CortexError as a normal application
                // payload {"success": false, "error": {...}} that flows through to the LLM.
                var err = response["error"]!;
                var errorPayload = err["data"] is JObject structured
                    ? (JToken)structured.DeepClone()
                    : new JObject
                    {
                        ["message"] = err["message"]?.ToString() ?? "Unknown Revit error"
                    };
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = errorPayload
                };
            }

            return response["result"] ?? JValue.CreateNull();
        }
    }

    private const string OutcomeUnknown =
        "Do not retry blindly: check the model first. Also check that Revit is still open and not showing a dialog.";

    private static JObject Failure(string code, string message, string suggestion) => new()
    {
        ["success"] = false,
        ["error"] = new JObject { ["code"] = code, ["message"] = message, ["suggestion"] = suggestion }
    };

    private sealed class RevitUnreachableException(string message, Exception inner) : Exception(message, inner);

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }

    public void Dispose() => Disconnect();
}

/// <summary>
/// Manages a per-request TCP connection to the Revit plugin.
/// Serializes access (only one command at a time, like the TS mutex).
/// </summary>
public sealed class RevitConnectionManager
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly int _port;

    public RevitConnectionManager(int port = 8090)
    {
        _port = port;
    }

    public async Task<JToken> ExecuteAsync(string method, JObject parameters, CancellationToken ct = default)
        => await ExecuteAsync(method, parameters, commandTimeoutSeconds: 300, ct);

    /// <summary>
    /// Overload with explicit command timeout — use for long-running operations
    /// such as IFC export on large models.
    /// </summary>
    public async Task<JToken> ExecuteAsync(string method, JObject parameters, int commandTimeoutSeconds, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            using var bridge = new RevitBridge(port: _port, commandTimeoutSeconds: commandTimeoutSeconds);
            return await bridge.SendCommandAsync(method, parameters, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Reads the port from settings or environment, same logic as the TS server.
    /// </summary>
    public static int ResolvePort()
    {
        var envPort = Environment.GetEnvironmentVariable("REVITFLUX_PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var ep) && ep > 0 && ep <= 65535)
            return ep;

        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".revitflux", "settings.json");
            if (File.Exists(settingsPath))
            {
                var json = JObject.Parse(File.ReadAllText(settingsPath));
                var port = json["Port"]?.Value<int>();
                if (port is > 0 and <= 65535) return port.Value;
            }
        }
        catch { }

        return 8090;
    }
}
