using System.Net.Http;
using System.Text.Json;

namespace YeziCompanion.Services;

internal sealed record LiveGameSnapshot(string Mode, double GameTime, string ChampionName, string ChampionKey);

internal sealed class LiveGameClient : IDisposable
{
    private readonly HttpClient _http;
    internal LiveGameClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true
        }) { BaseAddress = new Uri("https://127.0.0.1:2999"), Timeout = TimeSpan.FromMilliseconds(900) };
    }

    internal async Task<LiveGameSnapshot?> TryReadAsync(CancellationToken token)
    {
        try
        {
            using var stats = JsonDocument.Parse(await _http.GetStringAsync("/liveclientdata/gamestats", token));
            var mode = Text(stats.RootElement, "gameMode");
            if (string.IsNullOrEmpty(mode)) return null;
            var time = stats.RootElement.TryGetProperty("gameTime", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) ? seconds : 0;
            var result = new LiveGameSnapshot(mode, time, "", "");
            try
            {
                var activeTask = _http.GetStringAsync("/liveclientdata/activeplayer", token);
                var playersTask = _http.GetStringAsync("/liveclientdata/playerlist", token);
                await Task.WhenAll(activeTask, playersTask);
                using var active = JsonDocument.Parse(activeTask.Result);
                using var players = JsonDocument.Parse(playersTask.Result);
                return ParsePlayer(result, active.RootElement, players.RootElement);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return result; } // The game is connected even if champion details are still loading.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static LiveGameSnapshot ParsePlayer(LiveGameSnapshot result, JsonElement active, JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Array) return result;
        var riotId = Text(active, "riotId");
        var name = Text(active, "summonerName");
        foreach (var player in players.EnumerateArray())
        {
            if (!(riotId.Length > 0 && Text(player, "riotId") == riotId) &&
                !(name.Length > 0 && Text(player, "summonerName") == name)) continue;
            var raw = Text(player, "rawChampionName");
            const string prefix = "game_character_displayname_";
            return result with { ChampionName = Text(player, "championName"),
                ChampionKey = raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? raw[prefix.Length..] : "" };
        }
        return result;
    }

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : "";
    public void Dispose() => _http.Dispose();
}
