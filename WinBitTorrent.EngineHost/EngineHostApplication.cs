using System.Text;

namespace WinBitTorrent.EngineHost;

internal static class EngineHostApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = EngineHostOptions.Parse(args);
            var authenticationToken = (await Console.In.ReadLineAsync().ConfigureAwait(false))?.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(authenticationToken))
                throw new InvalidOperationException("The parent process did not provide an authentication token.");

            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            Directory.CreateDirectory(options.DataRoot);

            await using var state = await EngineState.OpenAsync(options.DataRoot).ConfigureAwait(false);
            await using var remoteApi = await RemoteApiServer.StartAsync(state).ConfigureAwait(false);
            await using var server = new EnginePipeServer(options.PipeName, authenticationToken, state);
            await server.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }
}
