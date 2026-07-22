using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Net;

namespace WinBitTorrent.Infrastructure.Trackers;

public sealed partial class RuTrackerProvider : ITrackerSearchProvider, ITrackerProxyOptions, ITrackerInteractiveAuthentication, ITrackerSessionControl, IDisposable
{
    private static readonly Uri[] Mirrors = [new("https://rutracker.org"), new("https://rutracker.net")];
    private static readonly Uri OfficialAddonProxy = new("https://ps1.blockme.site:443");
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/138.0.0.0 Safari/537.36";
    private static readonly string CurlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
    private static readonly Encoding Cp1251 = CreateEncoding();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _curlGate = new(1, 1);
    private HttpClient? _client;
    private CookieContainer? _cookies;
    private Uri? _activeMirror;
    private IPAddress? _activeProxyAddress;
    private IPAddress[] _proxyAddresses = [];
    private string? _curlCookieFile;

    public string Id => "rutracker";
    public string DisplayName => "RuTracker";
    public Uri HomePage => Mirrors[0];
    public Uri LoginPage => new(Mirrors[0], "/forum/login.php");
    public bool UseBuiltInProxy { get; set; }
    public string BuiltInProxyDescription => "ps1.blockme.site:443";

    public async Task SignInAsync(TrackerCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.UserName) || string.IsNullOrEmpty(credentials.Password))
            throw new TrackerAuthenticationException("Enter your RuTracker username and password.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (UseBuiltInProxy)
            {
                await SignInWithBuiltInProxyAsync(credentials, cancellationToken).ConfigureAwait(false);
                return;
            }

            var errors = new List<Exception>();
            foreach (var mirror in Mirrors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResetClient();
                CreateClient(mirror);
                try
                {
                    var form = EncodeForm(new Dictionary<string, string>
                    {
                        ["login_username"] = credentials.UserName.Trim(),
                        ["login_password"] = credentials.Password,
                        ["login"] = "Вход"
                    });
                    using var content = new ByteArrayContent(Encoding.ASCII.GetBytes(form));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                    using var response = await _client!.PostAsync(new Uri(mirror, "/forum/login.php"), content, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    if (_cookies!.GetCookies(new Uri(mirror, "/forum/")).Cast<Cookie>().Any(static cookie => cookie.Name == "bb_session"))
                        return;

                    throw new TrackerAuthenticationException("RuTracker rejected the username or password. A captcha may also be required on the website.");
                }
                catch (TrackerAuthenticationException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    errors.Add(exception);
                }
            }

            var lastError = errors.LastOrDefault();
            throw new TrackerAuthenticationException(
                lastError is null
                    ? "RuTracker is unavailable through its official addresses."
                    : $"RuTracker is unavailable through its official addresses: {lastError.Message}",
                lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ImportSessionCookiesAsync(IReadOnlyCollection<Cookie> cookies, CancellationToken cancellationToken = default)
    {
        var session = cookies.FirstOrDefault(static cookie =>
            cookie.Name.Equals("bb_session", StringComparison.OrdinalIgnoreCase) &&
            cookie.Domain.Contains("rutracker", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(cookie.Value));
        if (session is null)
            throw new TrackerAuthenticationException("RuTracker has not completed sign-in yet. Complete the captcha and sign in inside the browser first.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mirror = Mirrors.FirstOrDefault(candidate =>
                session.Domain.TrimStart('.').Equals(candidate.Host, StringComparison.OrdinalIgnoreCase)) ?? Mirrors[0];

            ResetClient();
            ResetCurlSession();
            if (UseBuiltInProxy)
            {
                _proxyAddresses = (await Dns.GetHostAddressesAsync(OfficialAddonProxy.Host, cancellationToken).ConfigureAwait(false))
                    .OrderBy(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();
                _activeProxyAddress = _proxyAddresses.FirstOrDefault()
                    ?? throw new HttpRequestException("The RuTracker proxy has no available addresses.");
                _activeMirror = mirror;
                WriteCurlCookieJar(cookies);
            }
            else
            {
                CreateClient(mirror);
                foreach (var cookie in cookies.Where(static cookie => cookie.Domain.Contains("rutracker", StringComparison.OrdinalIgnoreCase)))
                {
                    try { _cookies!.Add(CloneCookie(cookie)); }
                    catch (CookieException) { }
                }
            }

            try
            {
                var indexUri = new Uri(mirror, "/forum/index.php");
                string page;
                if (UseBuiltInProxy)
                {
                    var result = await SendAuthenticatedWithCurlAsync(HttpMethod.Get, indexUri, cancellationToken).ConfigureAwait(false);
                    page = Cp1251.GetString(result.Body);
                }
                else
                {
                    using var response = await _client!.GetAsync(indexUri, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    page = Cp1251.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                }
                EnsurePageIsAuthenticated(page);
            }
            catch
            {
                ResetClient();
                ResetCurlSession();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetClient();
            ResetCurlSession();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SignInWithBuiltInProxyAsync(TrackerCredentials credentials, CancellationToken cancellationToken)
    {
        if (!File.Exists(CurlPath))
            throw new TrackerAuthenticationException("The Windows curl network component is unavailable.");

        ResetClient();
        ResetCurlSession();
        _proxyAddresses = (await Dns.GetHostAddressesAsync(OfficialAddonProxy.Host, cancellationToken).ConfigureAwait(false))
            .OrderBy(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        var form = EncodeForm(new Dictionary<string, string>
        {
            ["login_username"] = credentials.UserName.Trim(),
            ["login_password"] = credentials.Password,
            ["login"] = "Вход"
        });
        var body = Encoding.ASCII.GetBytes(form);
        var errors = new List<Exception>();

        foreach (var mirror in Mirrors)
        {
            foreach (var proxyAddress in _proxyAddresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResetCurlCookieFile();
                try
                {
                    var result = await SendWithCurlAsync(
                        HttpMethod.Post,
                        new Uri(mirror, "/forum/login.php"),
                        body,
                        proxyAddress,
                        cancellationToken).ConfigureAwait(false);

                    if (result.StatusCode is < 200 or >= 400)
                        throw new HttpRequestException($"RuTracker returned HTTP {result.StatusCode}.");

                    if (CurlCookieHasSession())
                    {
                        _activeMirror = mirror;
                        _activeProxyAddress = proxyAddress;
                        return;
                    }

                    throw new TrackerAuthenticationException("RuTracker rejected the username or password. A captcha may also be required on the website.");
                }
                catch (TrackerAuthenticationException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
                {
                    errors.Add(exception);
                }
            }
        }

        var lastError = errors.LastOrDefault();
        throw new TrackerAuthenticationException(
            lastError is null
                ? "RuTracker is unavailable through its built-in proxy."
                : $"RuTracker is unavailable through its built-in proxy: {lastError.Message}",
            lastError);
    }

    public async Task<IReadOnlyList<TrackerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSignedIn();
            var firstUri = ForumUri("tracker.php?nm=" + PercentEncode(query.Trim()));
            var firstPage = await GetPageAsync(firstUri, cancellationToken).ConfigureAwait(false);
            EnsurePageIsAuthenticated(firstPage);

            var pages = PageQueryRegex().Matches(firstPage)
                .Select(match => WebUtility.HtmlDecode(match.Groups["query"].Value))
                .Distinct(StringComparer.Ordinal)
                .Take(9)
                .Select(page => GetPageAsync(ForumUri("tracker.php?" + page), cancellationToken))
                .ToArray();

            var allPages = new List<string> { firstPage };
            if (pages.Length > 0)
                allPages.AddRange(await Task.WhenAll(pages).ConfigureAwait(false));

            var results = new Dictionary<string, TrackerSearchResult>(StringComparer.Ordinal);
            foreach (var page in allPages)
            {
                foreach (var result in ParseResults(page, _activeMirror!))
                    results.TryAdd(result.Id, result);
            }

            return results.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> DownloadTorrentAsync(string resultId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSignedIn();
            if (!long.TryParse(resultId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("Invalid RuTracker topic id.", nameof(resultId));

            byte[] bytes;
            if (UseBuiltInProxy)
            {
                bytes = (await SendAuthenticatedWithCurlAsync(
                    HttpMethod.Get,
                    ForumUri("dl.php?t=" + resultId),
                    cancellationToken).ConfigureAwait(false)).Body;
            }
            else
            {
                using var response = await _client!.GetAsync(ForumUri("dl.php?t=" + resultId), cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            if (bytes.Length == 0 || bytes[0] != (byte)'d')
                throw new TrackerAuthenticationException("RuTracker did not return a torrent file. Sign in again or complete the captcha on the website.");
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static IEnumerable<TrackerSearchResult> ParseResults(string page, Uri mirror)
    {
        foreach (Match row in RowRegex().Matches(page))
        {
            var match = TorrentDataRegex().Match(row.Value);
            if (!match.Success)
                continue;

            var id = match.Groups["id"].Value;
            var title = WebUtility.HtmlDecode(TagRegex().Replace(match.Groups["title"].Value, string.Empty)).Trim();
            _ = long.TryParse(match.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            _ = int.TryParse(match.Groups["seeds"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeds);
            _ = int.TryParse(match.Groups["leech"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leechers);
            DateTimeOffset? published = null;
            if (long.TryParse(match.Groups["pub_date"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                try { published = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
                catch (ArgumentOutOfRangeException) { }
            }

            yield return new TrackerSearchResult(id, title, size, seeds, leechers, published, new Uri(mirror, "/forum/viewtopic.php?t=" + id));
        }
    }

    private async Task<string> GetPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (UseBuiltInProxy)
        {
            var result = await SendAuthenticatedWithCurlAsync(HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            return Cp1251.GetString(result.Body);
        }

        using var response = await _client!.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Cp1251.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static void EnsurePageIsAuthenticated(string page)
    {
        if (page.Contains("login_username", StringComparison.OrdinalIgnoreCase))
            throw new TrackerAuthenticationException("The RuTracker session has expired. Sign in again.");
    }

    private Uri ForumUri(string relative)
    {
        EnsureSignedIn();
        return new Uri(_activeMirror!, "/forum/" + relative);
    }

    private void EnsureSignedIn()
    {
        var hasTransport = UseBuiltInProxy
            ? _activeProxyAddress is not null && _curlCookieFile is not null
            : _client is not null;
        if (!hasTransport || _activeMirror is null)
            throw new TrackerAuthenticationException("Sign in to RuTracker first.");
    }

    private void CreateClient(Uri mirror)
    {
        _cookies = new CookieContainer();
        var systemProxy = CreateSystemProxy();
        var handler = CreateSystemProxyHandler(_cookies, systemProxy);
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        _activeMirror = mirror;
    }

    private static HttpMessageHandler CreateSystemProxyHandler(CookieContainer cookies, IWebProxy proxy)
        => new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            UseProxy = true,
            Proxy = proxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

    private async Task<CurlResult> SendAuthenticatedWithCurlAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken)
    {
        EnsureSignedIn();
        var addresses = _proxyAddresses
            .OrderByDescending(address => address.Equals(_activeProxyAddress))
            .ToArray();
        var errors = new List<Exception>();

        foreach (var address in addresses)
        {
            try
            {
                var result = await SendWithCurlAsync(method, uri, null, address, cancellationToken).ConfigureAwait(false);
                if (result.StatusCode is >= 200 and < 400)
                {
                    _activeProxyAddress = address;
                    return result;
                }
                errors.Add(new HttpRequestException($"RuTracker returned HTTP {result.StatusCode}."));
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                errors.Add(exception);
            }
        }

        throw new HttpRequestException("All RuTracker proxy nodes failed.", errors.LastOrDefault());
    }

    private async Task<CurlResult> SendWithCurlAsync(
        HttpMethod method,
        Uri uri,
        byte[]? requestBody,
        IPAddress proxyAddress,
        CancellationToken cancellationToken)
    {
        await _curlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var outputFile = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-rutracker-{Guid.NewGuid():N}.response");
        try
        {
            var startInfo = new ProcessStartInfo(CurlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = requestBody is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            AddCurlArgument(startInfo, "--silent");
            AddCurlArgument(startInfo, "--show-error");
            AddCurlArgument(startInfo, "--location");
            AddCurlArgument(startInfo, "--http1.1");
            AddCurlArgument(startInfo, "--compressed");
            AddCurlArgument(startInfo, "--connect-timeout", "3");
            AddCurlArgument(startInfo, "--max-time", "15");
            AddCurlArgument(startInfo, "--proxy", OfficialAddonProxy.AbsoluteUri);
            AddCurlArgument(startInfo, "--resolve", $"{OfficialAddonProxy.Host}:{OfficialAddonProxy.Port}:{proxyAddress}");
            AddCurlArgument(startInfo, "--cookie", EnsureCurlCookieFile());
            AddCurlArgument(startInfo, "--cookie-jar", EnsureCurlCookieFile());
            AddCurlArgument(startInfo, "--user-agent", BrowserUserAgent);
            AddCurlArgument(startInfo, "--header", "Accept-Language: ru-RU,ru;q=0.9,en;q=0.7");
            AddCurlArgument(startInfo, "--output", outputFile);
            AddCurlArgument(startInfo, "--write-out", "%{http_code}");
            if (method == HttpMethod.Post)
            {
                AddCurlArgument(startInfo, "--header", "Content-Type: application/x-www-form-urlencoded");
                AddCurlArgument(startInfo, "--data-binary", "@-");
            }
            AddCurlArgument(startInfo, uri.AbsoluteUri);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new HttpRequestException("The Windows curl network component could not be started.");

            var statusTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (requestBody is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(requestBody, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                throw;
            }

            var statusText = await statusTask.ConfigureAwait(false);
            var errorText = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new HttpRequestException(string.IsNullOrWhiteSpace(errorText) ? $"curl exited with code {process.ExitCode}." : errorText.Trim());
            if (!int.TryParse(statusText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
                throw new HttpRequestException("curl did not return an HTTP status code.");

            return new CurlResult(statusCode, await File.ReadAllBytesAsync(outputFile, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            try { File.Delete(outputFile); }
            catch { }
            _curlGate.Release();
        }
    }

    private static void AddCurlArgument(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values)
            startInfo.ArgumentList.Add(value);
    }

    private string EnsureCurlCookieFile()
    {
        if (_curlCookieFile is not null)
            return _curlCookieFile;
        _curlCookieFile = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-rutracker-{Guid.NewGuid():N}.cookies");
        File.WriteAllText(_curlCookieFile, string.Empty);
        return _curlCookieFile;
    }

    private bool CurlCookieHasSession()
        => _curlCookieFile is not null &&
           File.Exists(_curlCookieFile) &&
           File.ReadLines(_curlCookieFile).Any(static line => line.Contains("\tbb_session\t", StringComparison.Ordinal));

    private void WriteCurlCookieJar(IEnumerable<Cookie> cookies)
    {
        var builder = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var cookie in cookies.Where(static cookie => cookie.Domain.Contains("rutracker", StringComparison.OrdinalIgnoreCase)))
        {
            var domain = cookie.Domain.StartsWith(".", StringComparison.Ordinal) ? cookie.Domain : "." + cookie.Domain;
            if (cookie.HttpOnly)
                domain = "#HttpOnly_" + domain;
            var expires = cookie.Expires == DateTime.MinValue
                ? 0
                : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds();
            builder.Append(domain).Append('\t')
                .Append("TRUE\t")
                .Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path).Append('\t')
                .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(cookie.Name).Append('\t')
                .Append(cookie.Value).Append('\n');
        }
        File.WriteAllText(EnsureCurlCookieFile(), builder.ToString(), Encoding.UTF8);
    }

    private static Cookie CloneCookie(Cookie source)
        => new(source.Name, source.Value, string.IsNullOrEmpty(source.Path) ? "/" : source.Path, source.Domain)
        {
            Expires = source.Expires,
            HttpOnly = source.HttpOnly,
            Secure = source.Secure
        };

    private void ResetCurlCookieFile()
    {
        var cookieFile = EnsureCurlCookieFile();
        File.WriteAllText(cookieFile, string.Empty);
    }

    private void ResetCurlSession()
    {
        if (_curlCookieFile is not null)
        {
            try { File.Delete(_curlCookieFile); }
            catch { }
        }
        _curlCookieFile = null;
        _activeProxyAddress = null;
        _proxyAddresses = [];
    }

    private static string EncodeForm(IReadOnlyDictionary<string, string> values)
        => string.Join("&", values.Select(pair => PercentEncode(pair.Key) + "=" + PercentEncode(pair.Value)));

    internal static string PercentEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var valueByte in Cp1251.GetBytes(value))
        {
            if ((valueByte >= 'a' && valueByte <= 'z') ||
                (valueByte >= 'A' && valueByte <= 'Z') ||
                (valueByte >= '0' && valueByte <= '9') ||
                valueByte is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)valueByte);
            }
            else if (valueByte == (byte)' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    private static IWebProxy CreateSystemProxy() => SystemProxyResolver.Create(Mirrors[0]);

    private void ResetClient()
    {
        _client?.Dispose();
        _client = null;
        _cookies = null;
        _activeMirror = null;
    }

    public void Dispose()
    {
        ResetClient();
        ResetCurlSession();
        _gate.Dispose();
        _curlGate.Dispose();
    }

    private sealed record CurlResult(int StatusCode, byte[] Body);

    [GeneratedRegex("<tr id=[\"']trs-tr-\\d+[\"'].*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex("a\\s+data-topic_id=[\"'](?<id>\\d+?)[\"'].*?>(?<title>.+?)<.+?data-ts_text=[\"'](?<size>\\d+?)[\"'].+?data-ts_text=[\"'](?<seeds>[-\\d]+?)[\"'].+?leechmed.+?>(?<leech>\\d+?)<.+?data-ts_text=[\"'](?<pub_date>\\d+?)[\"']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TorrentDataRegex();

    [GeneratedRegex("href=[\"']tracker\\.php\\?(?<query>[^\"'<>]*start=\\d+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex PageQueryRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
