namespace WinBitTorrent.EngineHost;

internal sealed record EngineHostOptions(string PipeName, string DataRoot)
{
    public static EngineHostOptions Parse(string[] args)
    {
        string? pipeName = null;
        string? dataRoot = null;
        foreach (var argument in args)
        {
            if (argument.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
                pipeName = argument[7..];
            else if (argument.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
                dataRoot = argument[12..];
        }

        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("--pipe is required.");
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("--data-root is required.");

        return new EngineHostOptions(pipeName, Path.GetFullPath(dataRoot));
    }
}
