using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace YeziCompanion.Services;

public sealed class LcuClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _webSocketUri;
    private readonly string _authorizationValue;

    private LcuClient(string protocol, int port, string password, string discoverySource)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{protocol}://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{password}"));
        _authorizationValue = $"Basic {auth}";
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        var webSocketProtocol = string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        _webSocketUri = new Uri($"{webSocketProtocol}://127.0.0.1:{port}/");
        DiscoverySource = discoverySource;
    }

    public string DiscoverySource { get; }

    // In-memory transport for deterministic regression checks; it never opens sockets.
    internal LcuClient(HttpMessageHandler testHandler)
    {
        _http = new HttpClient(testHandler) { BaseAddress = new Uri("https://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        _webSocketUri = new Uri("wss://127.0.0.1:1");
        _authorizationValue = string.Empty;
        DiscoverySource = "offline-test";
    }

    public static bool IsLeagueClientProcessRunning()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    if (string.Equals(process.ProcessName, "LeagueClient", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(process.ProcessName, "LeagueClientUx", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Process metadata can disappear or be protected while the client starts.
                }
            }
        }
        catch
        {
            // Treat an unavailable process list as unknown rather than claiming a client exists.
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return false;
    }

    private static LcuLogCredentials? _lastWorkingCredentials;

    public static async Task<LcuClient?> TryDiscoverAsync(CancellationToken cancellationToken)
    {
        var credentials = new List<LcuLogCredentials>();
        if (_lastWorkingCredentials is not null) credentials.Add(_lastWorkingCredentials);
        credentials.AddRange(await DiscoverTencentLogCredentialsAsync(cancellationToken));
        foreach (var path in DiscoverLockfileCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) continue;
                var parts = (await File.ReadAllTextAsync(path, cancellationToken)).Trim().Split(':');
                if (parts.Length >= 5 && parts[4] == "https" &&
                    int.TryParse(parts[2], out var port) && port is > 0 and <= 65535 && parts[3].Length > 0)
                    credentials.Add(new LcuLogCredentials(port, parts[3]));
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // A stale log must not hold up a valid current session. Each validation has a short deadline.
        var probes = credentials.Distinct().Take(6).Select(async candidate =>
        {
            var client = new LcuClient("https", candidate.Port, candidate.Token, "本机客户端连接");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(800));
            try
            {
                if (await client.IsAliveAsync(deadline.Token))
                {
                    _lastWorkingCredentials = candidate;
                    return client;
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            client.Dispose();
            return null;
        }).ToList();

        while (probes.Count > 0)
        {
            var completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            var client = await completed;
            if (client is null) continue;
            foreach (var pending in probes)
                _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result?.Dispose(); },
                    TaskScheduler.Default);
            if (cancellationToken.IsCancellationRequested) { client.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            return client;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/lol-gameflow/v1/gameflow-phase", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetPhaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/lol-gameflow/v1/gameflow-phase", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<ChampSelectSnapshot?> GetChampSelectAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/lol-champ-select/v1/session", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseChampSelectSnapshot(document.RootElement);
    }

    public async Task WatchChampSelectEventsAsync(
        Func<ChampSelectSnapshot, CancellationToken, Task> onSnapshot,
        Action onConnected,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.SetRequestHeader("Authorization", _authorizationValue);
        socket.Options.AddSubProtocol("wamp");
        await socket.ConnectAsync(_webSocketUri, cancellationToken);
        onConnected();

        var subscription = Encoding.UTF8.GetBytes("[5,\"OnJsonApiEvent_lol-champ-select_v1_session\"]");
        await socket.SendAsync(subscription, WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[65_536];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 1_048_576) throw new InvalidDataException("客户端事件消息超出大小限制。");
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || message.Length == 0) continue;
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3 ||
                root[0].ValueKind != JsonValueKind.Number ||
                !root[0].TryGetInt32(out var messageType) || messageType != 8)
                continue;

            var payload = root[2];
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("uri", out var uri) ||
                uri.ValueKind != JsonValueKind.String ||
                !string.Equals(uri.GetString(), "/lol-champ-select/v1/session", StringComparison.OrdinalIgnoreCase) ||
                !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                continue;

            var snapshot = ParseChampSelectSnapshot(data);
            if (snapshot is not null)
                await onSnapshot(snapshot, cancellationToken);
        }
    }

    internal static ChampSelectSnapshot? ParseChampSelectSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var bench = new List<int>();
        if (root.TryGetProperty("benchChampionIds", out var benchElement) &&
            benchElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in benchElement.EnumerateArray())
                AddChampionId(item, bench);
        }
        if (root.TryGetProperty("benchChampions", out var benchChampionsElement) &&
            benchChampionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in benchChampionsElement.EnumerateArray())
                AddChampionId(item, bench);
        }

        var localCellId = root.TryGetProperty("localPlayerCellId", out var cellElement)
            ? ReadLong(cellElement, -1)
            : -1;
        var currentChampionId = 0;
        var teammateChampionIds = new List<int>();
        if (root.TryGetProperty("myTeam", out var teamElement) && teamElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in teamElement.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.Object) continue;
                if (!member.TryGetProperty("championId", out var champion)) continue;
                var championIds = new List<int>();
                AddChampionId(champion, championIds);
                var championId = championIds.FirstOrDefault();
                if (championId <= 0) continue;

                var isLocalPlayer = member.TryGetProperty("cellId", out var memberCell) &&
                                    localCellId >= 0 && ReadLong(memberCell, -2) == localCellId;
                if (isLocalPlayer)
                    currentChampionId = championId;
                else
                    teammateChampionIds.Add(championId);
            }
        }

        var gameId = root.TryGetProperty("gameId", out var gameIdElement) ? ReadLong(gameIdElement, 0) : 0;
        var benchEnabled = (root.TryGetProperty("benchEnabled", out var enabledElement) &&
                            enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                            enabledElement.GetBoolean()) || bench.Count > 0;
        return new ChampSelectSnapshot(
            gameId,
            benchEnabled,
            currentChampionId,
            bench.Except(teammateChampionIds).Where(id => id != currentChampionId).Distinct().ToList(),
            teammateChampionIds.Distinct().ToList());
    }

    private static long ReadLong(JsonElement element, long fallback) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
        _ => fallback
    };

    public async Task<bool> IsHexAramAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/lol-gameflow/v1/session", cancellationToken);
        if (!response.IsSuccessStatusCode) return false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ContainsHexAramMarker(document.RootElement);
    }

    public async Task<(bool Success, string Message)> SwapBenchChampionAsync(int championId, CancellationToken cancellationToken)
    {
        return await PostWithFallbackAsync(
            [
                $"/lol-champ-select/v1/session/bench/swap/{championId}",
                $"/lol-lobby-team-builder/champ-select/v1/session/bench/swap/{championId}"
            ],
            cancellationToken,
            fallbackOnlyWhenRouteMissing: true);
    }

    public async Task<(bool Success, string Message)> AcceptReadyCheckAsync(CancellationToken cancellationToken)
    {
        return await PostWithFallbackAsync(
            [
                "/lol-matchmaking/v1/ready-check/accept",
                "/lol-lobby-team-builder/v1/ready-check/accept"
            ],
            cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private static bool ContainsHexAramMarker(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("queueId") && IsInteger(property.Value, 2400))
                        return true;
                    // 国服 gameflow 会把队列写成 gameData.queue.id，而不是 queueId。
                    if (property.NameEquals("queue") && property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("id", out var queueId) && IsInteger(queueId, 2400))
                        return true;
                    if ((property.NameEquals("gameMode") || property.NameEquals("queueType")) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(property.Value.GetString(), "KIWI", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (ContainsHexAramMarker(property.Value)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsHexAramMarker(item)) return true;
                break;
        }
        return false;
    }

    private async Task<(bool Success, string Message)> PostWithFallbackAsync(
        IReadOnlyList<string> endpoints,
        CancellationToken cancellationToken,
        bool fallbackOnlyWhenRouteMissing = false)
    {
        var failures = new List<string>();
        foreach (var endpoint in endpoints)
        {
            using var response = await _http.PostAsync(endpoint, new StringContent(string.Empty), cancellationToken);
            if (response.IsSuccessStatusCode)
                return (true, $"HTTP {(int)response.StatusCode} · {endpoint}");

            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (body.Length > 240) body = body[..240] + "…";
            failures.Add($"{endpoint}: HTTP {(int)response.StatusCode}{(body.Length > 0 ? $" {body}" : string.Empty)}");
            if (fallbackOnlyWhenRouteMissing &&
                response.StatusCode is not System.Net.HttpStatusCode.NotFound and
                not System.Net.HttpStatusCode.MethodNotAllowed)
                break;
        }

        return (false, string.Join(" | ", failures));
    }

    private static bool IsInteger(JsonElement element, int expected)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var numeric) && numeric == expected,
            JsonValueKind.String => int.TryParse(element.GetString(), out var text) && text == expected,
            _ => false
        };
    }

    private static void AddChampionId(JsonElement element, List<int> destination)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericId) && numericId > 0)
        {
            destination.Add(numericId);
            return;
        }
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var stringId) && stringId > 0)
        {
            destination.Add(stringId);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var propertyName in new[] { "championId", "id" })
        {
            if (!element.TryGetProperty(propertyName, out var property)) continue;
            AddChampionId(property, destination);
            return;
        }
    }

    private static IEnumerable<string> DiscoverLockfileCandidates()
    {
        // Avoid MainModule: it can block on protected LeagueClient processes during a game.
        return DiscoverTencentLeagueClientDirectories()
            .SelectMany(directory => new[] { Path.Combine(directory, "lockfile"), Path.Combine(directory, "..", "lockfile") })
            .Concat(new[] { @"C:\Riot Games\League of Legends\lockfile", @"C:\Riot Games\League of Legends\LeagueClient\lockfile" })
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<LcuLogCredentials>> DiscoverTencentLogCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var credentials = new List<LcuLogCredentials>();
        foreach (var directory in DiscoverTencentLeagueClientDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                var logs = new DirectoryInfo(directory)
                    .EnumerateFiles("*_LeagueClientUx.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(6);

                foreach (var log in logs)
                {
                    try
                    {
                        var candidate = await ReadLogCredentialsAsync(log, cancellationToken);
                        if (candidate is not null) credentials.Add(candidate);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Skip only the log that is temporarily unavailable.
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Registry entries, directories and logs are optional discovery hints.
            }
        }
        return credentials;
    }

    internal static async Task<LcuLogCredentials?> ReadLogCredentialsAsync(FileInfo log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Directory metadata can report zero while the client's open log already contains data.
        // Read the shared file itself; never gate this on FileInfo.Length (including after Refresh).
        await using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[65_536];
        var charactersRead = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        var content = new string(buffer, 0, charactersRead);
        var portMatch = Regex.Match(content, "--app-port(?:=|\\s+)\"?(?<port>\\d+)");
        var tokenMatch = Regex.Match(content,
            "--remoting-auth-token(?:=|\\s+)(?:\"(?<quoted>[^\"]+)\"|(?<plain>[^\\s\"]+))");
        if (!portMatch.Success || !tokenMatch.Success ||
            !int.TryParse(portMatch.Groups["port"].Value, out var port) || port is < 1 or > 65535)
            return null;
        var token = tokenMatch.Groups["quoted"].Success
            ? tokenMatch.Groups["quoted"].Value : tokenMatch.Groups["plain"].Value;
        return string.IsNullOrWhiteSpace(token) ? null : new LcuLogCredentials(port, token);
    }

    private static IEnumerable<string> DiscoverTencentLeagueClientDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\LOL");
            if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
            {
                var directory = Path.Combine(installPath, "LeagueClient");
                if (seen.Add(directory)) directories.Add(directory);
            }
        }
        catch
        {
        }

        foreach (var directory in new[]
        {
            @"C:\WeGameApps\英雄联盟\LeagueClient",
            @"D:\WeGameApps\英雄联盟\LeagueClient",
            @"E:\WeGameApps\英雄联盟\LeagueClient",
            @"F:\WeGameApps\英雄联盟\LeagueClient"
        })
        {
            if (seen.Add(directory)) directories.Add(directory);
        }

        try
        {
            var hintPath = Path.Combine(AppStorage.DirectoryPath, "client-directory.json");
            if (File.Exists(hintPath))
            {
                var hint = JsonSerializer.Deserialize<string>(File.ReadAllText(hintPath));
                if (!string.IsNullOrWhiteSpace(hint) && Path.IsPathRooted(hint) && !hint.StartsWith(@"\\") && seen.Add(hint))
                    directories.Insert(0, hint);
            }
        }
        catch { }
        return directories;
    }

    internal sealed record LcuLogCredentials(int Port, string Token);
}

public sealed record ChampSelectSnapshot(
    long GameId,
    bool BenchEnabled,
    int CurrentChampionId,
    IReadOnlyList<int> BenchChampionIds,
    IReadOnlyList<int> TeammateChampionIds);
