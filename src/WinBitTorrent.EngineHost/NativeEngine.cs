using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinBitTorrent.EngineHost;

internal sealed class NativeEngine : IDisposable
{
    private nint _handle;

    private NativeEngine(nint handle) => _handle = handle;

    public static string Version
    {
        get
        {
            try
            {
                var pointer = WbtLibtorrentVersion();
                return Marshal.PtrToStringUTF8(pointer) ?? "unknown";
            }
            catch (DllNotFoundException)
            {
                return "native-bridge-not-loaded";
            }
            catch (EntryPointNotFoundException)
            {
                return "native-bridge-incompatible";
            }
        }
    }

    public static NativeEngine Open(string dataRoot)
    {
        var handle = WbtEngineCreate(dataRoot, out var error);
        try
        {
            if (handle == 0)
                throw new InvalidOperationException(Read(error, "Unable to create the native libtorrent session."));
            return new NativeEngine(handle);
        }
        finally
        {
            Free(error);
        }
    }

    public JsonElement Invoke(string method, JsonElement payload)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var result = WbtEngineInvoke(_handle, method, payload.GetRawText(), out var response, out var error);
        try
        {
            if (result != 0)
                throw new InvalidOperationException(Read(error, $"Native method '{method}' failed."));
            using var document = JsonDocument.Parse(Read(response, "{}"));
            return document.RootElement.Clone();
        }
        finally
        {
            Free(response);
            Free(error);
        }
    }

    public static byte[] CreateTorrent(JsonElement request, Func<int, int, bool> progress)
    {
        CreatorProgress callback = (completed, total, _) => progress(completed, total) ? 1 : 0;
        var result = WbtCreateTorrent(request.GetRawText(), callback, 0, out var data, out var size, out var error);
        try
        {
            if (result != 0)
                throw new InvalidOperationException(Read(error, "Torrent creation failed."));
            if (size > int.MaxValue)
                throw new InvalidOperationException("The generated torrent file is too large.");
            var bytes = new byte[(int)size];
            if (bytes.Length != 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            GC.KeepAlive(callback);
            return bytes;
        }
        finally
        {
            if (data != 0) WbtBytesFree(data);
            Free(error);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            WbtEngineDestroy(handle);
    }

    private static string Read(nint pointer, string fallback)
        => pointer == 0 ? fallback : Marshal.PtrToStringUTF8(pointer) ?? fallback;

    private static void Free(nint pointer)
    {
        if (pointer != 0)
            WbtStringFree(pointer);
    }

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_libtorrent_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint WbtLibtorrentVersion();

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_engine_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint WbtEngineCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dataRoot,
        out nint error);

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_engine_invoke", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WbtEngineInvoke(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string method,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string payload,
        out nint response,
        out nint error);

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_engine_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WbtEngineDestroy(nint handle);

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_string_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WbtStringFree(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreatorProgress(int completed, int total, nint context);

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_create_torrent", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WbtCreateTorrent(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string request,
        CreatorProgress progress,
        nint context,
        out nint data,
        out nuint size,
        out nint error);

    [DllImport("WinBitTorrent.Native.dll", EntryPoint = "wbt_bytes_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WbtBytesFree(nint value);
}
