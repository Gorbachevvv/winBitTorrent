using System.Net;
using System.Text;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Api;

namespace WinBitTorrent.Infrastructure.Tests;

public sealed class QbittorrentApiTests
{
    [Fact]
    public async Task SendsBearerKeyAndDeserializesRidDelta()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/app/version" => Text("v5.2.3"),
            "/api/v2/sync/maindata" => Json("""{"rid":2,"full_update":false,"torrents":{"abc":{"name":"Test","future":42}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var profile = new ServerProfile(Guid.NewGuid(), "Test", ProfileKind.Remote, new Uri("https://qbit.test/"), AuthenticationMode.ApiKey);
        await using var api = QbittorrentApi.Create(profile, "top-secret", handler);

        Assert.Equal("v5.2.3", await api.Application.GetVersionAsync());
        var delta = await api.Sync.GetMainDataAsync(1);

        Assert.Equal(2, delta.ResponseId);
        Assert.Equal("Test", delta.Torrents!["abc"].Name);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("top-secret", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Contains(handler.Requests, request => request.RequestUri!.Query.Contains("rid=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurnsErrorResponsesIntoTypedExceptions()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") });
        var profile = ServerProfile.CreateLocal(new Uri("http://127.0.0.1:1/"));
        await using var api = QbittorrentApi.Create(profile, handler: handler);
        var exception = await Assert.ThrowsAsync<QbittorrentApiException>(() => api.Application.GetVersionAsync());
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("Forbidden", exception.Response);
    }

    [Fact]
    public async Task PollsMetadataUsingSourceUntilBackendCompletes()
    {
        var calls = 0;
        var requestBody = string.Empty;
        var handler = new RecordingHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            calls++;
            var status = calls == 1 ? HttpStatusCode.Accepted : HttpStatusCode.OK;
            return Json("""{"info":{"name":"Preview","length":12,"files":[{"path":"a.bin","length":12}]}}""", status);
        });
        await using var api = QbittorrentApi.Create(ServerProfile.CreateLocal(new Uri("http://127.0.0.1:1/")), handler: handler);

        var result = await api.Torrents.FetchMetadataAsync("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");

        Assert.Equal(2, calls);
        Assert.StartsWith("source=magnet%3A", requestBody, StringComparison.Ordinal);
        Assert.Equal("Preview", result["info"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddsSelectedMetadataFilePriorities()
    {
        var requestBody = string.Empty;
        var handler = new RecordingHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Text(string.Empty);
        });
        await using var api = QbittorrentApi.Create(ServerProfile.CreateLocal(new Uri("http://127.0.0.1:1/")), handler: handler);
        var source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        await api.Torrents.AddAsync(new TorrentAddRequest([source], [], FilePriorities: [1, 0, 1]));

        Assert.Contains("name=filePriorities", requestBody, StringComparison.Ordinal);
        Assert.Contains("1,0,1", requestBody, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }
}
