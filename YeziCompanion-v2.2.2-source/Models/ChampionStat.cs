using System.ComponentModel;

namespace YeziCompanion.Models;

public sealed record ChampionStat(
    int ChampionId,
    int? Rank,
    string Slug,
    string Title,
    string Name,
    string Tier,
    double? WinRate,
    double? PickRate,
    string IconUrl);

public sealed record ChampionDataset(
    string Version,
    DateTimeOffset FetchedAt,
    List<ChampionStat> Champions);

public sealed class ChampionRow : INotifyPropertyChanged
{
    private bool _isAvailable;
    private bool _isTeammateHeld;
    private bool _isArmed;
    private System.Windows.Media.ImageSource? _portrait;
    public System.Windows.Media.ImageSource? Portrait
    {
        get => _portrait;
        set { _portrait = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Portrait))); }
    }

    public ChampionRow(ChampionStat stat) => Stat = stat;

    public ChampionStat Stat { get; private set; }
    public string Initial => string.IsNullOrEmpty(Name) ? "·" : Name[..1];
    public void UpdateStat(ChampionStat stat)
    {
        if (Stat == stat) return;
        if (Stat.IconUrl != stat.IconUrl) Portrait = null;
        Stat = stat;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    public int ChampionId => Stat.ChampionId;
    public int? Rank => Stat.Rank;
    public string Title => Stat.Title;
    public string Name => Stat.Name;
    public string Tier => Stat.Tier;
    public double? WinRate => Stat.WinRate;
    public double? PickRate => Stat.PickRate;
    public string IconUrl => Stat.IconUrl;
    public string RankDisplay => Rank is null ? "—" : $"#{Rank}";
    public string WinRateDisplay => WinRate is null ? "—" : $"{WinRate:0.0}%";
    public string PickRateDisplay => PickRate is null ? "—" : $"{PickRate:0.0}%";
    public string DetailsUrl => $"https://aramkit.com/zh-CN/champions/{Stat.Slug}";

    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetField(ref _isAvailable, value, nameof(IsAvailable), nameof(ActionText), nameof(AvailabilityText));
    }

    public bool IsArmed
    {
        get => _isArmed;
        set => SetField(ref _isArmed, value, nameof(IsArmed), nameof(ActionText));
    }

    public bool IsTeammateHeld
    {
        get => _isTeammateHeld;
        set => SetField(ref _isTeammateHeld, value, nameof(IsTeammateHeld), nameof(ActionText), nameof(AvailabilityText));
    }

    public string ActionText => IsArmed
        ? "已候选 · 重试"
        : IsAvailable
        ? "立即抢选"
        : IsTeammateHeld
        ? "监视换下"
        : "设为候选";
    public string AvailabilityText => IsAvailable ? "共享席" : IsTeammateHeld ? "队友持有" : "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref bool field, bool value, params string[] propertyNames)
    {
        if (field == value) return;
        field = value;
        foreach (var propertyName in propertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
