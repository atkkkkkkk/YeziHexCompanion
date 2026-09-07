using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using YeziCompanion.Models;

namespace YeziCompanion.Services;

public sealed class AramKitService : IDisposable
{
    private const string SourceUrl = "https://aramkit.com/zh-CN/champions";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(2);
    public bool UsedStaleCache { get; private set; }
    public string? RefreshError { get; private set; }

    private static readonly Regex RowRegex = new(
        "<a href=\"/zh-CN/champions/(?<slug>[^\"]+)\" class=\"table-row champion-row\">(?<body>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionRegex = new(
        "aria-label=\"游戏版本:\\s*(?<version>v?[\\d.]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string CachePath
    {
        get
        {
            var directory = AppStorage.DirectoryPath;
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "aramkit-champions.json");
        }
    }

    public AramKitService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("YeziCompanion/2.2.2 (desktop companion)");
    }

    public async Task<ChampionDataset> LoadAsync(CancellationToken cancellationToken, bool force = false)
    {
        UsedStaleCache = false;
        RefreshError = null;
        var cached = await TryReadCacheAsync(cancellationToken);
        if (!force && cached is not null && DateTimeOffset.Now - cached.FetchedAt < CacheLifetime)
            return cached;

        try
        {
            using var response = await _httpClient.GetAsync(SourceUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var dataset = Parse(html);
            try { await WriteCacheAsync(dataset, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RefreshError = "已获取最新数据，但暂时无法保存离线缓存。";
            }
            return dataset;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (cached is not null)
        {
            UsedStaleCache = true;
            RefreshError = ex.Message;
            return cached;
        }
    }

    internal static ChampionDataset Parse(string html)
    {
        var champions = new List<ChampionStat>();
        foreach (Match match in RowRegex.Matches(html))
        {
            var slug = match.Groups["slug"].Value;
            var body = match.Groups["body"].Value;
            var rankMatch = Regex.Match(body, "<span class=\"rank-cell\">#?(?<value>\\d+|—)</span>");
            var imageMatch = Regex.Match(body, "src=\"(?<icon>https://data\\.aramkit\\.com/assets/champion/(?<id>\\d+)-[^\"]+)\"[^>]+alt=\"(?<title>[^\"]+)\"");
            var nameMatch = Regex.Match(body, "<small>(?<name>[^<]+)</small>");
            var tierMatch = Regex.Match(body, "data-tier=\"(?<tier>[^\"]+)\"");
            var statMatches = Regex.Matches(body, "<span class=\"row-stat\"><strong>(?<value>[\\d.]+)%?</strong><small>(?<label>胜率|选取率)</small></span>");
            if (!imageMatch.Success || !nameMatch.Success || !rankMatch.Success) continue;

            double? winRate = null;
            double? pickRate = null;
            foreach (Match statMatch in statMatches)
            {
                if (!double.TryParse(statMatch.Groups["value"].Value, CultureInfo.InvariantCulture, out var value)) continue;
                if (statMatch.Groups["label"].Value == "胜率") winRate = value;
                if (statMatch.Groups["label"].Value == "选取率") pickRate = value;
            }

            champions.Add(new ChampionStat(
                int.Parse(imageMatch.Groups["id"].Value, CultureInfo.InvariantCulture),
                int.TryParse(rankMatch.Groups["value"].Value, out var rank) ? rank : null,
                WebUtility.HtmlDecode(slug),
                WebUtility.HtmlDecode(imageMatch.Groups["title"].Value),
                WebUtility.HtmlDecode(nameMatch.Groups["name"].Value),
                tierMatch.Success ? WebUtility.HtmlDecode(tierMatch.Groups["tier"].Value) : "—",
                winRate,
                pickRate,
                WebUtility.HtmlDecode(imageMatch.Groups["icon"].Value)));
        }

        if (champions.Count < 150 || champions.Select(c => c.ChampionId).Distinct().Count() != champions.Count)
            throw new InvalidDataException($"ARAMKit 页面结构可能已变化，仅解析到 {champions.Count} 个英雄。");

        var versionMatch = VersionRegex.Match(html);
        var version = versionMatch.Success ? versionMatch.Groups["version"].Value : "未知版本";
        return new ChampionDataset(version, DateTimeOffset.Now, champions.OrderBy(x => x.Rank ?? int.MaxValue).ToList());
    }

    private static async Task<ChampionDataset?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            await using var stream = File.OpenRead(CachePath);
            var dataset = await JsonSerializer.DeserializeAsync<ChampionDataset>(stream, cancellationToken: cancellationToken);
            return IsValidDataset(dataset) ? dataset : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    internal static bool IsValidDataset(ChampionDataset? dataset) => dataset?.Champions is { Count: >= 150 } heroes &&
        heroes.All(c => c is not null && c.ChampionId > 0 && !string.IsNullOrWhiteSpace(c.Name) &&
            !string.IsNullOrWhiteSpace(c.Slug) && !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Tier)) &&
        heroes.Select(c => c.ChampionId).Distinct().Count() == heroes.Count;

    private static Task WriteCacheAsync(ChampionDataset dataset, CancellationToken cancellationToken) =>
        AppStorage.WriteAtomicAsync(CachePath, dataset, cancellationToken);

    public void Dispose() => _httpClient.Dispose();
}
