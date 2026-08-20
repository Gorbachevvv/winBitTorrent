using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.Infrastructure.Engine;

internal sealed class EnginePipeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<EngineRpcResponse>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _nextId;

    private EnginePipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = ReadLoopAsync();
    }

    public static async Task<(EnginePipeClient Client, EngineHello Hello)> ConnectAsync(
        string pipeName,
        string authenticationToken,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        var client = new EnginePipeClient(pipe);
        try
        {
            var hello = await client.SendAsync<EngineHello>(
                EngineRpcMethods.Authenticate,
                new { },
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            return (client, hello);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<T> InvokeAsync<T>(string method, object? payload = null, CancellationToken cancellationToken = default)
        => SendAsync<T>(method, payload ?? new { }, null, cancellationToken);

    public async Task InvokeAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
        => _ = await SendAsync<JsonElement>(method, payload ?? new { }, null, cancellationToken).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(
        string method,
        object payload,
        string? authenticationToken,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<EngineRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Unable to allocate an engine request identifier.");

        using var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((ConcurrentDictionary<long, TaskCompletionSource<EngineRpcResponse>>, long, CancellationToken))state!;
            if (tuple.Item1.TryRemove(tuple.Item2, out var source))
                source.TrySetCanceled(tuple.Item3);
        }, (_pending, id, cancellationToken));

        try
        {
            var request = new EngineRpcRequest(
                EngineRpcProtocol.Version,
                id,
                method,
                JsonSerializer.SerializeToElement(payload, JsonOptions),
                authenticationToken);
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.ConfigureAwait(false);
            if (!response.Success)
                throw new LocalEngineException(response.Error?.Message ?? "The local engine rejected the request.", response.Error?.Code, response.Error?.Details);
            return response.Payload.Deserialize<T>(JsonOptions)
                ?? throw new LocalEngineException($"Engine method '{method}' returned an empty response.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(EngineRpcRequest request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length > EngineRpcProtocol.MaximumMessageBytes)
            throw new InvalidOperationException("The engine request is too large.");
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var length = new byte[sizeof(int)];
            while (!_lifetime.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(length, _lifetime.Token).ConfigureAwait(false);
                var messageLength = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (messageLength <= 0 || messageLength > EngineRpcProtocol.MaximumMessageBytes)
                    throw new InvalidDataException($"Invalid engine response length {messageLength}.");
                var payload = new byte[messageLength];
                await _pipe.ReadExactlyAsync(payload, _lifetime.Token).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<EngineRpcResponse>(payload, JsonOptions)
                    ?? throw new InvalidDataException("The engine returned an empty response.");
                if (_pending.TryRemove(response.Id, out var completion))
                    completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var terminal = failure ?? new EndOfStreamException("The local engine connection closed.");
            foreach (var (_, completion) in _pending)
                completion.TrySetException(terminal);
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _lifetime.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch { }
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}
