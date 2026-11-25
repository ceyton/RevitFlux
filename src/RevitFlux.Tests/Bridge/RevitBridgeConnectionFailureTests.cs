using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using RevitFlux.Server.Connection;
using Xunit;

namespace RevitFlux.Tests.Bridge;

/// <summary>Connection failures must reach the model as payloads, not opaque exceptions.</summary>
public class RevitBridgeConnectionFailureTests
{
    private static string? Code(JToken result)
    {
        Assert.False(result["success"]!.Value<bool>());
        return result["error"]!["code"]!.Value<string>();
    }

    private static TcpListener Listen(Func<TcpClient, StreamReader, Task> afterRequest)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream());
            await reader.ReadLineAsync();
            await afterRequest(client, reader);
        });
        return listener;
    }

    private static int Port(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    [Fact]
    public async Task SwitchOff_TellsTheUserToTurnItOn()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = Port(listener);
        listener.Stop();
        using var bridge = new RevitBridge(port: port);
        var result = await bridge.SendCommandAsync("get_project_info", new JObject());
        Assert.Equal("RevitNotConnected", Code(result));
        Assert.Contains("RevitFlux Switch", result["error"]!["suggestion"]!.Value<string>());
    }

    [Fact]
    public async Task ClosedAfterRequest_ReportsUnknownOutcome()
    {
        var listener = Listen((_, _) => Task.CompletedTask);
        using var bridge = new RevitBridge(port: Port(listener));
        Assert.Equal("ConnectionLost", Code(await bridge.SendCommandAsync("x", new JObject())));
        listener.Stop();
    }

    [Fact]
    public async Task NoReply_ReportsTimeout()
    {
        var listener = Listen((_, reader) => reader.ReadLineAsync());
        using var bridge = new RevitBridge(port: Port(listener), commandTimeoutSeconds: 1);
        Assert.Equal("Timeout", Code(await bridge.SendCommandAsync("x", new JObject())));
        listener.Stop();
    }

    [Fact]
    public async Task CallerCancellation_StillThrows()
    {
        var listener = Listen((_, reader) => reader.ReadLineAsync());
        using var bridge = new RevitBridge(port: Port(listener));
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bridge.SendCommandAsync("x", new JObject(), cts.Token));
        listener.Stop();
    }
}
