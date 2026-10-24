using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using WinBitTorrent.Core.EngineProtocol;
using WinBitTorrent.Infrastructure.Engine;

namespace WinBitTorrent.IntegrationTests;

public sealed class EnginePipeCancellationTests
{
    [Fact]
    public async Task CancellingInFlightRequestPreservesFrameAndNextRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelledRequest = new CancellationTokenSource();
        var pipeName = "WinBitTorrent.Cancellation." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await pipe.ConnectAsync(timeout.Token);
        await accept;
        await using var client = new EnginePipeClient(pipe);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        async Task<EngineRpcRequest> ReadRequestAsync(bool cancelAfterHeader)
        {
            var header = new byte[sizeof(int)];
            await server.ReadExactlyAsync(header, timeout.Token);
            if (cancelAfterHeader)
                cancelledRequest.Cancel();
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
            await server.ReadExactlyAsync(body, timeout.Token);
            return JsonSerializer.Deserialize<EngineRpcRequest>(body, options)!;
        }

        // Exceed the pipe buffer so cancellation occurs while the payload is being sent.
        var request = client.InvokeAsync("cancelled", new { value = new string('x', 1024 * 1024) }, cancelledRequest.Token);
        var first = await ReadRequestAsync(cancelAfterHeader: true);
        Assert.Equal("cancelled", first.Method);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var next = client.InvokeAsync<JsonElement>("next", cancellationToken: timeout.Token);
        var second = await ReadRequestAsync(cancelAfterHeader: false);
        Assert.Equal("next", second.Method);
        var response = JsonSerializer.SerializeToUtf8Bytes(new EngineRpcResponse(
            EngineRpcProtocol.Version, second.Id, true, JsonSerializer.SerializeToElement(new { ok = true })), options);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, response.Length);
        await server.WriteAsync(length, timeout.Token);
        await server.WriteAsync(response, timeout.Token);
        Assert.True((await next).GetProperty("ok").GetBoolean());
    }
}
