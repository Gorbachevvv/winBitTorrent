using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.EngineHost;

internal sealed class EnginePipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly byte[] _authenticationTokenHash;
    private readonly EngineState _state;
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeServerStream? _pipe;

    public EnginePipeServer(string pipeName, string authenticationToken, EngineState state)
    {
        _pipeName = pipeName;
        _authenticationTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(authenticationToken));
        _state = state;
    }

    public async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                await ServeConnectionAsync(_pipe, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A desktop client may disappear without a graceful disconnect. Keep the
                // engine alive for the parent process to reconnect and recover its UI state.
            }
            finally
            {
                await _pipe.DisposeAsync().ConfigureAwait(false);
                _pipe = null;
            }
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var authenticated = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await ReadAsync<EngineRpcRequest>(stream, cancellationToken).ConfigureAwait(false);
            EngineRpcResponse response;
            if (request.Version != EngineRpcProtocol.Version)
            {
                response = Failure(request.Id, "protocol_mismatch", $"Engine protocol {request.Version} is not supported.");
            }
            else if (!authenticated)
            {
                authenticated = request.Method == EngineRpcMethods.Authenticate
                    && VerifyAuthenticationToken(request.AuthenticationToken);
                response = authenticated
                    ? Success(request.Id, _state.Hello)
                    : Failure(request.Id, "authentication_failed", "The engine authentication token is invalid.");
            }
            else if (request.Method == EngineRpcMethods.Shutdown)
            {
                await _state.FlushAsync(cancellationToken).ConfigureAwait(false);
                response = Success(request.Id, new { });
                await WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
                _lifetime.Cancel();
                return;
            }
            else
            {
                response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }

            await WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
            if (!authenticated)
                return;
        }
    }

    private async Task<EngineRpcResponse> DispatchAsync(EngineRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Success(request.Id, await _state.HandleAsync(request.Method, request.Payload, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return Failure(request.Id, "invalid_argument", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Failure(request.Id, "not_supported", exception.Message);
        }
        catch (Exception exception)
        {
            return Failure(request.Id, "engine_error", exception.Message, exception.ToString());
        }
    }

    private bool VerifyAuthenticationToken(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(_authenticationTokenHash, candidateHash);
    }

    private static EngineRpcResponse Success<T>(long id, T value)
        => new(EngineRpcProtocol.Version, id, true, EngineJson.Element(value));

    private static EngineRpcResponse Failure(long id, string code, string message, string? details = null)
        => new(EngineRpcProtocol.Version, id, false, EngineJson.EmptyObject, new EngineRpcError(code, message, details));

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > EngineRpcProtocol.MaximumMessageBytes)
            throw new InvalidDataException($"Invalid engine message length {length}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, EngineJson.Options)
            ?? throw new InvalidDataException("The engine message was empty.");
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, EngineJson.Options);
        var lengthBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_pipe is not null)
            await _pipe.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
