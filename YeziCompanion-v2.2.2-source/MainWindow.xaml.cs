using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using System.Windows.Navigation;
using YeziCompanion.Models;
using YeziCompanion.Services;

namespace YeziCompanion;

public partial class MainWindow : Window
{
    private const int MaxSwapAttempts = 8;
    private static readonly TimeSpan SwapInterval = TimeSpan.FromMilliseconds(150);
    private readonly ObservableCollection<ChampionRow> _rows = [];
    private readonly Dictionary<int, ChampionRow> _rowsById = [];
    private readonly HashSet<int> _availableChampionIds = [];
    private readonly HashSet<int> _teammateChampionIds = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AramKitService _aramKit = new();
    private readonly PortraitService _portraits = new();
    private readonly LiveGameClient _liveGame = new();
    private bool _liveConnected;
    private double _lastLiveGameTime;
    private bool _reconnectRequested;
    private readonly SemaphoreSlim _swapGate = new(1, 1);
    private readonly Channel<string> _grabLogQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private bool _loadingData;
    private bool _preferencesReady;
    private int _candidateRevision;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private bool AnimationsEnabled => !AppStorage.IsSelfTest && SystemParameters.ClientAreaAnimation;
    private Task? _grabLogWriterTask;
    private DispatcherOperation? _pendingFilterRefresh;
    private LcuClient? _lcu;
    private CancellationTokenSource? _eventStreamLifetime;
    private Task? _eventStreamTask;
    private ChampSelectSnapshot? _latestSnapshot;
    private DateTimeOffset _latestSnapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHexModeCheck = DateTimeOffset.MinValue;
    private bool _latestIsHexAram;
    private int? _armedChampionId;
    private bool _autoGrabEnabled;
    private bool _champSelectWasActive;
    private long _completedGameId;
    private int _swapAttempts;
    private DateTimeOffset _lastSwapAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAcceptAttempt = DateTimeOffset.MinValue;
    private bool _autoAcceptEnabled;
    private bool _readyCheckAccepted;
    private int _acceptAttempts;
    private int _currentChampionId;
    private bool _autoOpenWebsiteEnabled;
    private bool _hexChampSelectSeenForCurrentGame;
    private bool _websiteOpenedForCurrentGame;
    private int _finalChampionId;
    private long _trackedGameId;
    private DateTimeOffset _lastWebsiteAttempt = DateTimeOffset.MinValue;
    private string? _lastDiagnosticState;
    private bool _introAnimated;
    private bool? _connectionAnimationState;

    public ICollectionView RowsView { get; }

    public MainWindow()
    {
        InitializeComponent();
        RowsView = CollectionViewSource.GetDefaultView(_rows);
        RowsView.Filter = FilterRow;
        DataContext = this;
        AllHeroesRadio.IsChecked = true;
        _grabLogWriterTask = Task.Run(RunGrabLogWriterAsync);
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WriteGrabDiagnostic("window-loaded", 0, $"pid={Environment.ProcessId}");
        var runUiSelfTest = Environment.GetCommandLineArgs().Contains(
            "--self-test-ui",
            StringComparer.OrdinalIgnoreCase);
        if (!runUiSelfTest)
        {
            await LoadPreferencesAsync();
            _ = MonitorClientAsync(_lifetime.Token);
        }
        await LoadDataAsync();
        if (!runUiSelfTest)
        {
            _ = RefreshDataPeriodicallyAsync(_lifetime.Token);
        }
        if (runUiSelfTest)
            await RunUiSelfTestAsync();
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_introAnimated) return;
        _introAnimated = true;
        if (!AnimationsEnabled) return;

        MainContent.BeginAnimation(
            OpacityProperty,
            CreateEaseOutAnimation(0, 1, 220));
        MainContentTransform.BeginAnimation(
            TranslateTransform.YProperty,
            CreateEaseOutAnimation(9, 0, 240));
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        WriteGrabDiagnostic("window-closed", 0, $"pid={Environment.ProcessId}");
        _grabLogQueue.Writer.TryComplete();
        try
        {
            _grabLogWriterTask?.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
        }
        _lifetime.Cancel();
        StopChampSelectEventStream();
        _lcu?.Dispose();
        _aramKit.Dispose();
        _portraits.Dispose();
        _liveGame.Dispose();
    }

    private async Task LoadDataAsync(bool force = false)
    {
        if (_loadingData || _lifetime.IsCancellationRequested) return;
        _loadingData = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "更新中…";
        try
        {
            ActivityText.Text = force ? "正在刷新 ARAMKit 数据…" : "正在加载 ARAMKit 数据…";
            var dataset = await _aramKit.LoadAsync(_lifetime.Token, force);
            _lifetime.Token.ThrowIfCancellationRequested();
            ApplyDataset(dataset);
            _ = LoadPortraitsAsync(_rows.ToArray(), _lifetime.Token);
            DatasetText.Text = $"{dataset.Version} · {dataset.Champions.Count} 位英雄 · {dataset.FetchedAt:MM-dd HH:mm}";
            DataHealthText.Text = _aramKit.UsedStaleCache ? "离线缓存" : "数据就绪";
            DataHealthText.Foreground = (Brush)FindResource(_aramKit.UsedStaleCache ? "GoldBrush" : "AccentBrush");
            DataHealthText.ToolTip = _aramKit.RefreshError ?? "每两小时检查更新，也可手动刷新。";
            if (_aramKit.UsedStaleCache)
                ActivityText.Text = "暂时无法更新数据，已保留最近缓存；客户端监控继续运行。";
            else if (_lcu is null)
                ActivityText.Text = "数据已就绪，正在等待英雄联盟客户端。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"数据加载失败：{ex.Message}";
            DataHealthText.Text = "更新失败";
            DataHealthText.ToolTip = ex.Message;
            WriteGrabDiagnostic("data-load-error", 0, ex.ToString());
        }
        finally
        {
            _loadingData = false;
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "刷新数据";
        }
    }

    private void ApplyDataset(ChampionDataset dataset)
    {
        if (!AramKitService.IsValidDataset(dataset)) throw new InvalidDataException("英雄数据不完整，已保留现有列表。");
        var ids = dataset.Champions.Select(c => c.ChampionId).ToHashSet();
        // Keep existing row identities; WPF cannot process collection moves while a view refresh is deferred.
        {
            foreach (var removed in _rows.Where(r => !ids.Contains(r.ChampionId)).ToArray())
            {
                _rows.Remove(removed);
                _rowsById.Remove(removed.ChampionId);
            }
            foreach (var stat in dataset.Champions)
            {
                if (_rowsById.TryGetValue(stat.ChampionId, out var existing)) existing.UpdateStat(stat);
                else
                {
                    var row = new ChampionRow(stat) { IsArmed = _armedChampionId == stat.ChampionId };
                    _rowsById.Add(stat.ChampionId, row);
                    _rows.Add(row);
                }
            }
            for (var i = 0; i < dataset.Champions.Count; i++)
            {
                var currentIndex = _rows.IndexOf(_rowsById[dataset.Champions[i].ChampionId]);
                if (currentIndex != i) _rows.Move(currentIndex, i);
            }
        }
        UpdateAvailability(_availableChampionIds, _teammateChampionIds, force: true);
        UpdateCurrentChampion(_currentChampionId, force: true);
    }

    private async Task RefreshDataPeriodicallyAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(2));
        try { while (await timer.WaitForNextTickAsync(token)) await LoadDataAsync(force: true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task LoadPortraitsAsync(ChampionRow[] rows, CancellationToken token)
    {
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                if (row.Portrait is not null) return;
                var url = row.IconUrl;
                var portrait = await _portraits.LoadAsync(url, token);
                if (!token.IsCancellationRequested && row.IconUrl == url) row.Portrait = portrait;
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { WriteGrabDiagnostic("portrait-error", 0, ex.Message); }
    }

    private async Task LoadPreferencesAsync()
    {
        try
        {
            var path = Path.Combine(AppStorage.DirectoryPath, "preferences.json");
            if (File.Exists(path))
            {
                var preferences = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(path));
                if (preferences is not null)
                {
                    AutoGrabCheckBox.IsChecked = preferences.AutoGrab;
                    AutoAcceptCheckBox.IsChecked = preferences.AutoAccept;
                    AutoOpenWebsiteCheckBox.IsChecked = preferences.AutoOpenWebsite;
                }
            }
        }
        catch (Exception ex) { WriteGrabDiagnostic("preferences-read-error", 0, ex.Message); }
        _preferencesReady = true;
    }

    private async Task SavePreferencesAsync()
    {
        if (!_preferencesReady || AppStorage.IsSelfTest) return;
        await _preferencesGate.WaitAsync();
        try
        {
            await AppStorage.WriteAtomicAsync(Path.Combine(AppStorage.DirectoryPath, "preferences.json"),
                new UserPreferences(_autoGrabEnabled, _autoAcceptEnabled, _autoOpenWebsiteEnabled));
        }
        catch (Exception ex) { WriteGrabDiagnostic("preferences-write-error", 0, ex.Message); }
        finally { _preferencesGate.Release(); }
    }

    private async Task MonitorClientAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_reconnectRequested)
                {
                    _reconnectRequested = false;
                    _candidateRevision++;
                    StopChampSelectEventStream();
                    _lcu?.Dispose();
                    _lcu = null;
                    ClearLatestChampSelect();
                }
                if (_lcu is null)
                {
                    if (!_liveConnected) await Dispatcher.InvokeAsync(() => SetConnectionState(
                        false,
                        "正在连接英雄联盟客户端",
                        "正在后台读取国服客户端连接信息；界面可正常操作。"));
                    // 进程 MainModule/日志扫描在部分国服反作弊环境中可能阻塞，绝不能放在界面线程。
                    var discoveryTask = Task.Run(
                        () => LcuClient.TryDiscoverAsync(cancellationToken),
                        cancellationToken);
                    var live = await _liveGame.TryReadAsync(cancellationToken);
                    _liveConnected = live is not null;
                    if (live is not null) ApplyLiveGame(live);
                    _lcu = await discoveryTask;
                    if (_lcu is null)
                    {
                        if (live is not null)
                        {
                            await Task.Delay(2000, cancellationToken);
                            continue;
                        }
                        var clientProcessRunning = await Task.Run(
                            LcuClient.IsLeagueClientProcessRunning,
                            cancellationToken);
                        await Dispatcher.InvokeAsync(() => SetConnectionState(
                            false,
                            clientProcessRunning ? "客户端连接暂不可用" : "等待英雄联盟客户端",
                            clientProcessRunning
                                ? "已检测到客户端，连接信息暂不可读；自动重试中，游戏启动后可连接对局数据。"
                                : "未发现正在运行的 League Client。"));
                        await Task.Delay(1800, cancellationToken);
                        continue;
                    }
                    StartChampSelectEventStream(_lcu, cancellationToken);
                }

                var phase = await _lcu.GetPhaseAsync(cancellationToken);
                if (phase is null) throw new HttpRequestException("客户端阶段接口暂时不可用。");

                if (string.Equals(phase, "ReadyCheck", StringComparison.OrdinalIgnoreCase))
                {
                    _champSelectWasActive = false;
                    ResetGameWebsiteTracking();
                    ClearLatestChampSelect();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateAvailability([], []);
                        UpdateCurrentChampion(0);
                        SetConnectionState(true, "对局等待接受", _readyCheckAccepted
                            ? "已自动接受对局，正在等待其他玩家。"
                            : _autoAcceptEnabled
                            ? "自动接受已启用，正在确认对局。"
                            : "自动接受未启用，请在客户端中手动接受。");
                    });
                    await TryAutoAcceptAsync(cancellationToken);
                    await Task.Delay(300, cancellationToken);
                    continue;
                }

                _readyCheckAccepted = false;
                _acceptAttempts = 0;
                if (!string.Equals(phase, "ChampSelect", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(phase, "Matchmaking", StringComparison.OrdinalIgnoreCase))
                        ResetGameWebsiteTracking();
                    _champSelectWasActive = false;
                    ClearLatestChampSelect();
                    if (IsGameStartPhase(phase) && _finalChampionId == 0)
                    {
                        var live = await _liveGame.TryReadAsync(cancellationToken);
                        if (live is not null) ApplyLiveGame(live);
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetConnectionState(true, IsGameStartPhase(phase) ? "对局已连接" : "客户端已连接", FriendlyPhase(phase));
                        UpdateAvailability([], []);
                        UpdateCurrentChampion(IsGameStartPhase(phase) ? _finalChampionId : 0);
                        if (IsGameStartPhase(phase))
                            TryOpenFinalChampionWebsite();
                    });
                    await Task.Delay(700, cancellationToken);
                    continue;
                }

                var pollStartedAt = DateTimeOffset.Now;
                _liveConnected = false;
                var snapshot = await _lcu.GetChampSelectAsync(cancellationToken);
                if (snapshot is null)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                // A WebSocket event that arrived while this GET was running is newer.
                // Do not let the slower polling response roll the UI or bench state backwards.
                if (_latestSnapshotAt > pollStartedAt)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                _latestSnapshot = snapshot;
                _latestSnapshotAt = DateTimeOffset.Now;
                if (!_latestIsHexAram && DateTimeOffset.Now - _lastHexModeCheck >= TimeSpan.FromMilliseconds(500))
                {
                    _lastHexModeCheck = DateTimeOffset.Now;
                    var modeClient = _lcu;
                    var detectedMode = await modeClient.IsHexAramAsync(cancellationToken);
                    if (!ReferenceEquals(modeClient, _lcu) || !ReferenceEquals(snapshot, _latestSnapshot)) continue;
                    _latestIsHexAram = detectedMode;
                }
                var isHexAram = _latestIsHexAram;
                if (!ReferenceEquals(_latestSnapshot, snapshot)) continue;
                if (isHexAram)
                {
                    _hexChampSelectSeenForCurrentGame = true;
                    if (snapshot.CurrentChampionId > 0)
                        _finalChampionId = snapshot.CurrentChampionId;
                    if (snapshot.GameId > 0)
                        _trackedGameId = snapshot.GameId;
                }

                var enteringHexAram = isHexAram && !_champSelectWasActive;
                _champSelectWasActive = isHexAram;
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateAvailability(snapshot.BenchChampionIds, snapshot.TeammateChampionIds);
                    UpdateCurrentChampion(snapshot.CurrentChampionId);
                    UpdateGrabState(snapshot, isHexAram);
                    SetConnectionState(
                        true,
                        isHexAram ? "海克斯大乱斗选人中" : "其他模式选人中",
                        isHexAram
                            ? $"共享席 {snapshot.BenchChampionIds.Count} 位 · 队友持有 {snapshot.TeammateChampionIds.Count} 位 · 事件推送优先。"
                            : "自动抢选只在 KIWI 或队列 2400 时运行。");

                    if (enteringHexAram)
                    {
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        Show();
                        Activate();
                        Topmost = true;
                        Topmost = false;
                    }
                });

                if (isHexAram)
                    await TryAutoGrabAsync(snapshot, cancellationToken);

                await Task.Delay(isHexAram ? 100 : 500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                StopChampSelectEventStream();
                _lcu?.Dispose();
                _lcu = null;
                ClearLatestChampSelect();
                UpdateAvailability([], []);
                SetConnectionState(false, "连接超时，正在重试", "客户端响应超时，正在自动重新连接。");
            }
            catch (Exception ex)
            {
                ClearLatestChampSelect();
                UpdateAvailability([], []);
                await Dispatcher.InvokeAsync(() => SetConnectionState(false, "连接暂时中断", ex.Message));
                StopChampSelectEventStream();
                _lcu?.Dispose();
                _lcu = null;
                try { await Task.Delay(1200, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private void StartChampSelectEventStream(LcuClient client, CancellationToken applicationToken)
    {
        StopChampSelectEventStream();
        _eventStreamLifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        var token = _eventStreamLifetime.Token;
        _eventStreamTask = Task.Run(() => RunChampSelectEventStreamAsync(client, token), token);
    }

    private void StopChampSelectEventStream()
    {
        var lifetime = _eventStreamLifetime;
        var streamTask = _eventStreamTask;
        _eventStreamLifetime = null;
        _eventStreamTask = null;
        if (lifetime is null) return;
        try
        {
            lifetime.Cancel();
        }
        catch
        {
        }
        if (streamTask is null)
        {
            lifetime.Dispose();
        }
        else
        {
            _ = streamTask.ContinueWith(
                _ => lifetime.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunChampSelectEventStreamAsync(LcuClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await client.WatchChampSelectEventsAsync(
                    (snapshot, token) => HandlePushedChampSelectAsync(client, snapshot, token),
                    () => WriteGrabDiagnostic("event-stream-connected", 0, client.DiscoverySource),
                    cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    WriteGrabDiagnostic("event-stream-closed", 0, "client closed the websocket");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                WriteGrabDiagnostic("event-stream-error", 0, ex.Message);
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandlePushedChampSelectAsync(
        LcuClient client, ChampSelectSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Accept snapshots on the same dispatcher as clicks and polling. Do not wait for
        // a swap HTTP response before reading the next WebSocket message.
        if (cancellationToken.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(client, _lcu))
                _ = ProcessPushedChampSelectAsync(client, snapshot, cancellationToken);
        });
    }

    private async Task ProcessPushedChampSelectAsync(
        LcuClient client, ChampSelectSnapshot snapshot, CancellationToken cancellationToken)
    {
        try { await ApplyPushedChampSelectAsync(client, snapshot, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { WriteGrabDiagnostic("event-processing-error", 0, ex.Message); }
    }

    private async Task ApplyPushedChampSelectAsync(
        LcuClient client,
        ChampSelectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var eventReceivedAt = DateTimeOffset.Now;
        var previousSnapshot = _latestSnapshot;
        var target = _armedChampionId;
        var targetJustAppeared = target is int targetId &&
                                 snapshot.BenchChampionIds.Contains(targetId) &&
                                 (previousSnapshot is null || !previousSnapshot.BenchChampionIds.Contains(targetId));

        _latestSnapshot = snapshot;
        _latestSnapshotAt = DateTimeOffset.Now;
        if (!_latestIsHexAram)
        {
            _lastHexModeCheck = DateTimeOffset.Now;
            var isHexAram = await client.IsHexAramAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(client, _lcu) ||
                !ReferenceEquals(snapshot, _latestSnapshot)) return;
            _latestIsHexAram = isHexAram;
        }
        if (!_latestIsHexAram || cancellationToken.IsCancellationRequested ||
            !ReferenceEquals(client, _lcu) || !ReferenceEquals(snapshot, _latestSnapshot)) return;

        _hexChampSelectSeenForCurrentGame = true;
        if (snapshot.CurrentChampionId > 0)
            _finalChampionId = snapshot.CurrentChampionId;
        if (snapshot.GameId > 0)
            _trackedGameId = snapshot.GameId;

        var enteringHexAram = !_champSelectWasActive;
        _champSelectWasActive = true;
        var grabTask = TryAutoGrabAsync(
            snapshot,
            cancellationToken,
            forceImmediate: targetJustAppeared,
            detectedAt: targetJustAppeared ? eventReceivedAt : null);
        await Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(client, _lcu) ||
                !ReferenceEquals(snapshot, _latestSnapshot)) return;
            UpdateAvailability(snapshot.BenchChampionIds, snapshot.TeammateChampionIds);
            UpdateCurrentChampion(snapshot.CurrentChampionId);
            UpdateGrabState(snapshot, true);
            if (enteringHexAram)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                Topmost = true;
                Topmost = false;
            }
        });

        if (targetJustAppeared && target is int appearedChampionId)
            WriteGrabDiagnostic("bench-event", appearedChampionId, $"gameId={snapshot.GameId}; websocket push");
        await grabTask;
    }

    private async Task TryAutoAcceptAsync(CancellationToken cancellationToken)
    {
        if (!_autoAcceptEnabled || _readyCheckAccepted || _acceptAttempts >= 3) return;
        if (DateTimeOffset.Now - _lastAcceptAttempt < TimeSpan.FromMilliseconds(750)) return;
        _lastAcceptAttempt = DateTimeOffset.Now;
        _acceptAttempts++;

        var result = await _lcu!.AcceptReadyCheckAsync(cancellationToken);
        if (result.Success)
        {
            _readyCheckAccepted = true;
            await Dispatcher.InvokeAsync(() =>
            {
                ActivityText.Text = $"已自动接受对局 · {result.Message}";
                SystemSounds.Asterisk.Play();
            });
        }
        else
        {
            await Dispatcher.InvokeAsync(() => ActivityText.Text = $"自动接受第 {_acceptAttempts}/3 次未成功：{result.Message}");
        }
    }

    private async Task TryAutoGrabAsync(
        ChampSelectSnapshot snapshot,
        CancellationToken cancellationToken,
        bool forceImmediate = false,
        DateTimeOffset? detectedAt = null)
    {
        var target = _armedChampionId;
        var revision = _candidateRevision;
        var client = _lcu;
        if (!_autoGrabEnabled || target is null || !snapshot.BenchEnabled || client is null) return;
        if (snapshot.CurrentChampionId == target)
        {
            await Dispatcher.InvokeAsync(() => CompleteGrab(target.Value, snapshot.GameId, "目标英雄已经在你手中。"));
            return;
        }
        if (!snapshot.BenchChampionIds.Contains(target.Value))
        {
            _swapAttempts = 0;
            return;
        }
        if (_completedGameId == snapshot.GameId && snapshot.GameId != 0) return;
        if (_swapAttempts >= MaxSwapAttempts) return;
        if (!forceImmediate && DateTimeOffset.Now - _lastSwapAttempt < SwapInterval) return;
        if (!await _swapGate.WaitAsync(0, cancellationToken))
        {
            if (forceImmediate)
                await Dispatcher.InvokeAsync(() => ActivityText.Text = "交换请求已经在处理中，无需重复点击。");
            return;
        }

        try
        {
            if (!IsCurrentCandidate(target.Value, revision) || !_autoGrabEnabled || !ReferenceEquals(client, _lcu)) return;
            _lastSwapAttempt = DateTimeOffset.Now;
            _swapAttempts++;
            var attempt = _swapAttempts;
            var requestStartedAt = DateTimeOffset.Now;
            var startedAt = Stopwatch.GetTimestamp();
            var result = await client.SwapBenchChampionAsync(target.Value, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var detectionDelay = detectedAt is null
                ? (double?)null
                : (requestStartedAt - detectedAt.Value).TotalMilliseconds;
            WriteGrabDiagnostic(
                "swap-request",
                target.Value,
                $"attempt={attempt}; immediate={forceImmediate}; gameId={snapshot.GameId}; " +
                $"detectedToRequestMs={(detectionDelay is null ? "n/a" : detectionDelay.Value.ToString("0.0"))}");
            WriteGrabDiagnostic(
                result.Success ? "swap-success" : "swap-failure",
                target.Value,
                $"attempt={attempt}; elapsedMs={elapsed:0}; {result.Message}");

            if (!IsCurrentCandidate(target.Value, revision) || !_autoGrabEnabled ||
                !ReferenceEquals(client, _lcu) || cancellationToken.IsCancellationRequested)
            {
                WriteGrabDiagnostic("stale-result-ignored", target.Value, "candidate changed while request was in flight");
                return;
            }

            if (result.Success)
            {
                await Dispatcher.InvokeAsync(() => CompleteGrab(
                    target.Value,
                    snapshot.GameId,
                    $"自动抢选成功 · {elapsed:0}ms · {result.Message}"));
                SystemSounds.Asterisk.Play();
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ActivityText.Text = $"第 {attempt}/{MaxSwapAttempts} 次抢选未成功：{result.Message}";
                    ArmedSubtitle.Text = $"目标仍在共享席；第 {attempt}/{MaxSwapAttempts} 次未成功，正在高速重试。";
                });
            }
        }
        finally
        {
            _swapGate.Release();
        }
    }

    private void CompleteGrab(int championId, long gameId, string message)
    {
        if (_armedChampionId != championId) return;
        _candidateRevision++;
        WriteGrabDiagnostic("completed", championId, $"gameId={gameId}; {message}");
        _finalChampionId = championId;
        if (gameId > 0) _trackedGameId = gameId;
        _completedGameId = gameId;
        _swapAttempts = 0;
        ActivityText.Text = message;
        _rowsById.TryGetValue(championId, out var row);
        if (row is not null)
        {
            ArmedTitle.Text = $"已拿到 · {row.Name}";
            ArmedSubtitle.Text = message;
        }
        ArmedCard.BorderBrush = (Brush)FindResource("SuccessBrush");
        AnimateCandidateFeedback();
        if (_armedChampionId is int armedId && _rowsById.TryGetValue(armedId, out var armedRow))
            armedRow.IsArmed = false;
        _armedChampionId = null;
        CancelCandidateButton.IsEnabled = false;
    }

    private void SetConnectionState(bool connected, string title, string detail)
    {
        var stateKey = $"{connected}|{title}|{detail}";
        if (stateKey == _lastDiagnosticState) return;

        ConnectionDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("DangerBrush");
        ConnectionBadge.Background = (Brush)FindResource(
            connected ? "ConnectedBadgeBrush" : "PendingBadgeBrush");
        ConnectionBadge.BorderBrush = (Brush)FindResource(
            connected ? "BorderStrongBrush" : "BorderBrush");
        ConnectionText.Text = title;
        ConnectionHintText.Text = detail;
        ActivityText.Text = detail;
        UpdateConnectionAnimation(connected);
        AnimateStatusText(ConnectionText);
        WriteDiagnosticState(connected, title, detail);
    }

    private void UpdateConnectionAnimation(bool connected)
    {
        if (_connectionAnimationState == connected) return;
        _connectionAnimationState = connected;
        ConnectionDot.BeginAnimation(OpacityProperty, null);
        ConnectionDot.Opacity = 1;
        if (connected || AppStorage.IsSelfTest || !SystemParameters.ClientAreaAnimation) return;

        var pulse = new DoubleAnimation(0.42, 1, TimeSpan.FromMilliseconds(720))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(pulse, 30);
        ConnectionDot.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateEaseOutAnimation(double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        return animation;
    }

    private static void AnimateStatusText(UIElement element)
    {
        if (AppStorage.IsSelfTest || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, CreateEaseOutAnimation(0.5, 1, 130));
    }

    private void AnimateCandidateFeedback()
    {
        if (AppStorage.IsSelfTest || !SystemParameters.ClientAreaAnimation) return;
        ArmedCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateEaseOutAnimation(0.988, 1, 145));
        ArmedCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateEaseOutAnimation(0.988, 1, 145));
    }

    private void AnimateFilteredView()
    {
        if (AppStorage.IsSelfTest || !SystemParameters.ClientAreaAnimation) return;
        ChampionGrid.BeginAnimation(OpacityProperty, CreateEaseOutAnimation(0.78, 1, 105));
    }

    private void WriteDiagnosticState(bool connected, string title, string detail)
    {
        var stateKey = $"{connected}|{title}|{detail}";
        if (stateKey == _lastDiagnosticState) return;
        _lastDiagnosticState = stateKey;
        try
        {
            var directory = AppStorage.DirectoryPath;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new
            {
                Connected = connected,
                Title = title,
                Detail = detail,
                UpdatedAt = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "connection-status.json"), json);
        }
        catch
        {
        }
    }

    private void UpdateAvailability(
        IEnumerable<int> availableIds,
        IEnumerable<int> teammateIds,
        bool force = false)
    {
        var teammates = teammateIds.Where(id => id > 0).ToHashSet();
        var ids = availableIds.Where(id => id > 0 && !teammates.Contains(id)).ToHashSet();
        var changed = force ||
                      !_availableChampionIds.SetEquals(ids) ||
                      !_teammateChampionIds.SetEquals(teammates);
        if (!changed) return;

        _availableChampionIds.Clear();
        _availableChampionIds.UnionWith(ids);
        _teammateChampionIds.Clear();
        _teammateChampionIds.UnionWith(teammates);
        foreach (var row in _rows)
        {
            row.IsAvailable = _availableChampionIds.Contains(row.ChampionId);
            row.IsTeammateHeld = _teammateChampionIds.Contains(row.ChampionId);
        }
        UpdateScopeLabels();
        if (AvailableHeroesRadio.IsChecked == true || TeammateHeroesRadio.IsChecked == true)
            RowsView.Refresh();
        UpdateEmptyState();
    }

    private void UpdateScopeLabels()
    {
        AllHeroesRadio.Content = $"全部英雄 {_rows.Count}";
        AvailableHeroesRadio.Content = $"共享席空闲 {_availableChampionIds.Count}";
        TeammateHeroesRadio.Content = $"队友持有 {_teammateChampionIds.Count}";
    }

    private void UpdateCurrentChampion(int championId, bool force = false)
    {
        if (!force && _currentChampionId == championId) return;
        _currentChampionId = championId;
        var row = championId > 0 && _rowsById.TryGetValue(championId, out var championRow)
            ? championRow
            : null;
        CurrentChampionCard.DataContext = row;
        CurrentChampionContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        CurrentChampionPlaceholder.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is not null && AnimationsEnabled)
        {
            CurrentChampionContent.BeginAnimation(
                OpacityProperty,
                CreateEaseOutAnimation(0.28, 1, 155));
            CurrentChampionTransform.BeginAnimation(
                TranslateTransform.YProperty,
                CreateEaseOutAnimation(5, 0, 170));
        }
    }

    private static bool IsGameStartPhase(string? phase)
    {
        return string.Equals(phase, "GameStart", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(phase, "InProgress", StringComparison.OrdinalIgnoreCase);
    }

    private void ResetGameWebsiteTracking()
    {
        _hexChampSelectSeenForCurrentGame = false;
        _websiteOpenedForCurrentGame = false;
        _finalChampionId = 0;
        _trackedGameId = 0;
        _lastWebsiteAttempt = DateTimeOffset.MinValue;
    }

    private bool TryOpenFinalChampionWebsite(bool launchBrowser = true)
    {
        if (!_autoOpenWebsiteEnabled || !_hexChampSelectSeenForCurrentGame || _websiteOpenedForCurrentGame)
            return false;

        _rowsById.TryGetValue(_finalChampionId, out var row);
        if (row is null)
        {
            ActivityText.Text = "已进入游戏，但未能确定最终英雄，因此没有打开数据页面。";
            return false;
        }

        if (!launchBrowser) { _websiteOpenedForCurrentGame = true; return true; }
        if (DateTimeOffset.Now - _lastWebsiteAttempt < TimeSpan.FromSeconds(30)) return false;
        _lastWebsiteAttempt = DateTimeOffset.Now;

        try
        {
            Process.Start(new ProcessStartInfo(row.DetailsUrl) { UseShellExecute = true });
            _websiteOpenedForCurrentGame = true;
            ActivityText.Text = $"已打开 {row.Name} 的 ARAMKit 数据页面（本局仅一次）。";
            WriteGrabDiagnostic("website-opened", row.ChampionId, $"gameId={_trackedGameId}; {row.DetailsUrl}");
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"无法打开 {row.Name} 的数据页面：{ex.Message}";
            WriteGrabDiagnostic("website-open-error", row.ChampionId, ex.Message);
            return false;
        }
        return true;
    }

    private void ClearLatestChampSelect()
    {
        _latestSnapshot = null;
        _latestSnapshotAt = DateTimeOffset.MinValue;
        _latestIsHexAram = false;
        _lastHexModeCheck = DateTimeOffset.MinValue;
    }

    private async Task TriggerImmediateGrabAsync(int championId)
    {
        if (_armedChampionId != championId || !_autoGrabEnabled) return;
        var revision = _candidateRevision;
        var client = _lcu;
        if (client is null)
        {
            ArmedSubtitle.Text = "候选已设置；正在重新连接英雄联盟客户端。";
            return;
        }

        try
        {
            ArmedSubtitle.Text = "点击已收到，正在立即读取共享席并提交交换。";
            ActivityText.Text = "正在执行立即抢选…";

            var snapshot = _latestSnapshot;
            if (snapshot is null || DateTimeOffset.Now - _latestSnapshotAt > TimeSpan.FromMilliseconds(350))
            {
                var readStartedAt = DateTimeOffset.Now;
                snapshot = await client.GetChampSelectAsync(_lifetime.Token);
                if (!IsCurrentCandidate(championId, revision) || !ReferenceEquals(client, _lcu)) return;
                if (_latestSnapshotAt > readStartedAt) snapshot = _latestSnapshot;
                if (snapshot is not null)
                {
                    _latestSnapshot = snapshot;
                    _latestSnapshotAt = DateTimeOffset.Now;
                }
            }

            if (snapshot is null)
            {
                ArmedSubtitle.Text = "候选已设置，但客户端暂未返回选人数据；后台会持续高速检测。";
                ActivityText.Text = "候选已设置；进入海克斯大乱斗选人阶段后自动抢选。";
                WriteGrabDiagnostic("immediate-no-session", championId, "champ-select session unavailable");
                return;
            }

            if (!_latestIsHexAram)
            {
                _lastHexModeCheck = DateTimeOffset.Now;
                var detectedMode = await client.IsHexAramAsync(_lifetime.Token);
                if (!IsCurrentCandidate(championId, revision) || !ReferenceEquals(client, _lcu)) return;
                _latestIsHexAram = detectedMode;
            }
            if (!IsCurrentCandidate(championId, revision) || !ReferenceEquals(client, _lcu)) return;
            if (!_latestIsHexAram)
            {
                ArmedSubtitle.Text = "候选已设置，但当前模式尚未识别为海克斯大乱斗。";
                ActivityText.Text = "候选已设置；当前不是海克斯大乱斗选人阶段。";
                WriteGrabDiagnostic("immediate-mode-blocked", championId, "not detected as KIWI/2400");
                return;
            }

            UpdateAvailability(snapshot.BenchChampionIds, snapshot.TeammateChampionIds);
            UpdateCurrentChampion(snapshot.CurrentChampionId);
            UpdateGrabState(snapshot, true);
            if (!snapshot.BenchChampionIds.Contains(championId) && snapshot.CurrentChampionId != championId)
            {
                ArmedSubtitle.Text = snapshot.TeammateChampionIds.Contains(championId)
                    ? "候选已设置；目标当前由队友持有，被换下后将由事件推送立即抢选。"
                    : $"候选已设置；目标尚不在共享席，事件推送监听中（当前 {snapshot.BenchChampionIds.Count} 位）。";
                ActivityText.Text = "候选已布防；检测到目标进入共享席时会立即交换。";
                WriteGrabDiagnostic("immediate-waiting", championId, $"benchCount={snapshot.BenchChampionIds.Count}");
                return;
            }

            await TryAutoGrabAsync(snapshot, _lifetime.Token, forceImmediate: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ArmedSubtitle.Text = "立即请求遇到临时错误；后台轮询仍会继续尝试。";
            ActivityText.Text = $"立即抢选请求失败：{ex.Message}";
            WriteGrabDiagnostic("immediate-error", championId, ex.Message);
        }
    }

    private void UpdateGrabState(ChampSelectSnapshot snapshot, bool isHexAram)
    {
        if (_armedChampionId is not int target) return;
        if (!_autoGrabEnabled)
        {
            ArmedSubtitle.Text = "候选已保留，但自动抢选当前已暂停。";
            return;
        }
        if (!isHexAram)
        {
            ArmedSubtitle.Text = "候选已保留，但当前选人模式未识别为海克斯大乱斗。";
            return;
        }
        if (snapshot.CurrentChampionId == target)
        {
            ArmedSubtitle.Text = "已检测到目标英雄在你手中，正在确认结果。";
            return;
        }
        if (!snapshot.BenchEnabled)
        {
            ArmedSubtitle.Text = "已进入海克斯选人，但客户端尚未开放共享席。";
            return;
        }
        if (snapshot.BenchChampionIds.Contains(target))
        {
            ArmedSubtitle.Text = _swapAttempts >= MaxSwapAttempts
                ? $"已停止自动重试：连续 {MaxSwapAttempts} 次交换均未成功。再次点击该英雄可立即重新布防。"
                : _swapAttempts > 0
                ? $"目标在共享席，已尝试 {_swapAttempts}/{MaxSwapAttempts} 次；正在高速重试。"
                : "已检测到目标进入共享席，正在立即抢选。";
            return;
        }
        if (snapshot.TeammateChampionIds.Contains(target))
        {
            ArmedSubtitle.Text = "自动抢选正在运行；目标当前由队友持有，等待被换下进入共享席。";
            return;
        }
        ArmedSubtitle.Text = $"自动抢选正在运行；等待目标进入共享席（当前 {snapshot.BenchChampionIds.Count} 位）。";
    }

    private void WriteGrabDiagnostic(string eventName, int championId, string detail)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ObservedAt = DateTimeOffset.Now,
                Event = eventName,
                ChampionId = championId,
                Detail = detail
            });
            _grabLogQueue.Writer.TryWrite(line);
        }
        catch
        {
        }
    }

    private async Task RunGrabLogWriterAsync()
    {
        try
        {
            var directory = AppStorage.DirectoryPath;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "grab-attempts.jsonl");
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream);
            await foreach (var firstLine in _grabLogQueue.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(firstLine);
                while (_grabLogQueue.Reader.TryRead(out var nextLine))
                    await writer.WriteLineAsync(nextLine);
                await writer.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never delay or interrupt selection requests.
            try
            {
                var directory = AppStorage.DirectoryPath;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "grab-log-error.txt"), ex.ToString());
            }
            catch
            {
            }
        }
    }

    private bool FilterRow(object item)
    {
        if (item is not ChampionRow row) return false;
        if (AvailableHeroesRadio?.IsChecked == true &&
            !_availableChampionIds.Contains(row.ChampionId))
            return false;
        if (TeammateHeroesRadio?.IsChecked == true &&
            !_teammateChampionIds.Contains(row.ChampionId))
            return false;
        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        return row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Tier.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.RankDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool ArmChampion(int championId)
    {
        if (!_rowsById.ContainsKey(championId)) return false;
        _candidateRevision++;
        CancelCandidateButton.IsEnabled = true;
        if (_armedChampionId == championId)
        {
            _completedGameId = 0;
            _swapAttempts = 0;
            _lastSwapAttempt = DateTimeOffset.MinValue;
            ArmedSubtitle.Text = "候选已重新布防，正在立即重试；重复点击不会取消候选。";
            ActivityText.Text = "已收到重复点击，立即重试抢选。";
            WriteGrabDiagnostic("rearmed", championId, "same champion clicked again");
            ArmedCard.BorderBrush = (Brush)FindResource("AccentBrush");
            AnimateCandidateFeedback();
            return true;
        }

        if (!_rowsById.TryGetValue(championId, out var row)) return false;
        if (_armedChampionId is int previousId && _rowsById.TryGetValue(previousId, out var previousRow))
            previousRow.IsArmed = false;
        _armedChampionId = championId;
        _completedGameId = 0;
        _swapAttempts = 0;
        _lastSwapAttempt = DateTimeOffset.MinValue;
        row.IsArmed = true;
        AutoGrabCheckBox.IsChecked = true;
        ArmedTitle.Text = $"候选：{row.Name} · {row.Tier} 级 · 胜率 {row.WinRateDisplay}";
        ArmedSubtitle.Text = row.IsAvailable
            ? "目标当前在共享席，正在尝试抢选。"
            : row.IsTeammateHeld
            ? "目标当前由队友持有；被换下或重随后进入共享席时将立即抢选。"
            : "已布防；目标进入共享席后将立即尝试交换。";
        ActivityText.Text = $"已设置 {row.Name}（ID {row.ChampionId}）为本局候选。";
        WriteGrabDiagnostic("armed", championId, $"available={row.IsAvailable}; teammateHeld={row.IsTeammateHeld}");
        ArmedCard.BorderBrush = (Brush)FindResource("AccentBrush");
        AnimateCandidateFeedback();
        return true;
    }

    private void CancelArmed()
    {
        _candidateRevision++;
        CancelCandidateButton.IsEnabled = false;
        if (_armedChampionId is int championId)
            WriteGrabDiagnostic("cancelled", championId, "cancel button clicked");
        if (_armedChampionId is int championIdToClear &&
            _rowsById.TryGetValue(championIdToClear, out var rowToClear))
            rowToClear.IsArmed = false;
        _armedChampionId = null;
        _swapAttempts = 0;
        _lastSwapAttempt = DateTimeOffset.MinValue;
        ArmedTitle.Text = "尚未设置候选英雄";
        ArmedSubtitle.Text = "在列表中点击“设为候选”，目标进入共享席时将自动抢选。";
        ActivityText.Text = "候选已取消。";
        ArmedCard.BorderBrush = (Brush)FindResource("BorderBrush");
        AnimateCandidateFeedback();
    }

    private async void ArmChampion_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var row = ResolveChampionRow(button);
        if (row is null)
        {
            ActivityText.Text = "按钮数据尚未就绪，请刷新数据后重试。";
            WriteGrabDiagnostic("button-invalid-context", 0, sender?.GetType().FullName ?? "null sender");
            return;
        }
        WriteGrabDiagnostic("button-click", row.ChampionId, "single click");
        await HandleChampionActionAsync(row);
    }

    private static ChampionRow? ResolveChampionRow(Button? button)
    {
        if (button?.DataContext is ChampionRow directRow) return directRow;
        return button is null
            ? null
            : FindVisualParent<DataGridRow>(button)?.Item as ChampionRow;
    }

    private async Task HandleChampionActionAsync(ChampionRow row)
    {
        if (ArmChampion(row.ChampionId))
            await TriggerImmediateGrabAsync(row.ChampionId);
    }

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async Task RunUiSelfTestAsync()
    {
        var row = _rows.FirstOrDefault();
        if (row is null)
        {
            WriteGrabDiagnostic("ui-self-test-failure", 0, "dataset is empty");
            Environment.ExitCode = 1;
            Close();
            return;
        }

        ChampionGrid.ScrollIntoView(row);
        ChampionGrid.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        CaptureUiPreviewIfRequested();
        var container = ChampionGrid.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
        var button = container is null ? null : FindVisualChild<Button>(container);
        if (button is null)
        {
            WriteGrabDiagnostic("ui-self-test-failure", row.ChampionId, "action button was not generated");
            Environment.ExitCode = 2;
            Close();
            return;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        var candidatePassed = _armedChampionId == row.ChampionId && row.IsArmed;
        var defaultsPassed = AutoGrabCheckBox.IsChecked == true &&
                             AutoAcceptCheckBox.IsChecked == true &&
                             AutoOpenWebsiteCheckBox.IsChecked == true;
        _hexChampSelectSeenForCurrentGame = true;
        _websiteOpenedForCurrentGame = false;
        _finalChampionId = row.ChampionId;
        var firstWebsiteOpen = TryOpenFinalChampionWebsite(launchBrowser: false);
        var duplicateWebsiteOpen = TryOpenFinalChampionWebsite(launchBrowser: false);
        using var snapshotDocument = JsonDocument.Parse("""
            {
              "gameId": 123,
              "benchEnabled": true,
              "benchChampionIds": [22, 33],
              "localPlayerCellId": 1,
              "myTeam": [
                { "cellId": 1, "championId": 157 },
                { "cellId": 2, "championId": 526 },
                { "cellId": 3, "championId": 516 }
              ]
            }
            """);
        var parsedSnapshot = LcuClient.ParseChampSelectSnapshot(snapshotDocument.RootElement);
        var teammateParserPassed = parsedSnapshot is not null &&
                                   parsedSnapshot.CurrentChampionId == 157 &&
                                   parsedSnapshot.BenchChampionIds.SequenceEqual([22, 33]) &&
                                   parsedSnapshot.TeammateChampionIds.SequenceEqual([526, 516]);
        if (parsedSnapshot is not null)
            UpdateAvailability(parsedSnapshot.BenchChampionIds, parsedSnapshot.TeammateChampionIds);
        var teammateUiPassed = _rows.FirstOrDefault(item => item.ChampionId == 526)?.IsTeammateHeld == true &&
                               _rows.FirstOrDefault(item => item.ChampionId == 22)?.IsAvailable == true;
        AvailableHeroesRadio.IsChecked = true;
        var availableFilterPassed = RowsView.Cast<ChampionRow>().Select(item => item.ChampionId).Order().SequenceEqual([22, 33]);
        TeammateHeroesRadio.IsChecked = true;
        var teammateFilterPassed = RowsView.Cast<ChampionRow>().Select(item => item.ChampionId).Order().SequenceEqual([516, 526]);
        AllHeroesRadio.IsChecked = true;
        var separateFiltersPassed = availableFilterPassed && teammateFilterPassed && RowsView.Cast<object>().Count() == _rows.Count;
        var regressionChecks = await RunRegressionChecksAsync();
        var passed = candidatePassed && defaultsPassed && firstWebsiteOpen && !duplicateWebsiteOpen &&
                     teammateParserPassed && teammateUiPassed && separateFiltersPassed && regressionChecks.Values.All(value => value);
        try
        {
            var reportDirectory = AppStorage.DirectoryPath;
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(
                Path.Combine(reportDirectory, "ui-self-test.json"),
                JsonSerializer.Serialize(new
                {
                    Passed = passed,
                    Candidate = candidatePassed,
                    Defaults = defaultsPassed,
                    WebsiteOnce = firstWebsiteOpen && !duplicateWebsiteOpen,
                    TeammateParser = teammateParserPassed,
                    TeammateUi = teammateUiPassed,
                    SeparateFilters = separateFiltersPassed,
                    RegressionChecks = regressionChecks,
                    RowCount = _rows.Count,
                    IndexedRowCount = _rowsById.Count,
                    FirstChampionId = row.ChampionId,
                    IndexContainsFirst = _rowsById.ContainsKey(row.ChampionId),
                    AvailableCount = _availableChampionIds.Count,
                    TeammateCount = _teammateChampionIds.Count,
                    ObservedAt = DateTimeOffset.Now
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
        WriteGrabDiagnostic(
            passed ? "ui-self-test-pass" : "ui-self-test-failure",
            row.ChampionId,
            $"buttonContent={button.Content}; armed={_armedChampionId}; rowArmed={row.IsArmed}; " +
            $"defaults={defaultsPassed}; websiteOnce={firstWebsiteOpen && !duplicateWebsiteOpen}; " +
            $"teammateParser={teammateParserPassed}; teammateUi={teammateUiPassed}; separateFilters={separateFiltersPassed}");
        Environment.ExitCode = passed ? 0 : 3;
        Close();
    }

    internal async Task RunHeadlessSelfTestAsync()
    {
        WriteApplicationIcon();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Tests", "champions.json");
        var dataset = JsonSerializer.Deserialize<ChampionDataset>(await File.ReadAllTextAsync(fixture))
            ?? throw new InvalidDataException("Missing test dataset");
        ApplyDataset(dataset);
        foreach (var portraitFile in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Tests", "portraits"), "*.webp"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(portraitFile), out var id) && _rowsById.TryGetValue(id, out var hero))
                hero.Portrait = PortraitService.Decode(await File.ReadAllBytesAsync(portraitFile));
        DatasetText.Text = $"{dataset.Version} · {dataset.Champions.Count} 位英雄 · 离线验收样本";
        DataHealthText.Text = "离线预览";
        ActivityText.Text = "离线验收 · 不连接客户端";
        RootSurface.Measure(new Size(1104, 780));
        RootSurface.Arrange(new Rect(0, 0, 1104, 780));
        RootSurface.UpdateLayout();
        await RunUiSelfTestAsync();
        MainWindow_Closed(null, EventArgs.Empty);
    }

    private void CaptureUiPreviewIfRequested(string? variant = null)
    {
        var requestedPath = Environment.GetEnvironmentVariable("HEXARAM_V2_UI_CAPTURE");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;

        try
        {
            RootSurface.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(RootSurface.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(RootSurface.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(RootSurface);

            var fullPath = Path.GetFullPath(requestedPath);
            if (variant is not null)
                fullPath = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + "-" + variant + ".png");
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(fullPath);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            WriteGrabDiagnostic("ui-capture-failure", 0, ex.Message);
        }
    }

    private void CancelArmed_Click(object sender, RoutedEventArgs e) => CancelArmed();

    private void AutoGrabCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _autoGrabEnabled = AutoGrabCheckBox.IsChecked == true;
        _ = SavePreferencesAsync();
        if (IsLoaded)
            ActivityText.Text = _autoGrabEnabled ? "自动抢选已启用。" : "自动抢选已暂停，候选目标仍保留。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RowsView is null || _pendingFilterRefresh?.Status == DispatcherOperationStatus.Pending)
            return;

        _pendingFilterRefresh = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _pendingFilterRefresh = null;
                RowsView.Refresh();
                UpdateEmptyState();
                AnimateFilteredView();
            }));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        RowsView?.Refresh();
        UpdateEmptyState();
        AnimateFilteredView();
    }

    private void AutoAcceptCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _autoAcceptEnabled = AutoAcceptCheckBox.IsChecked == true;
        _ = SavePreferencesAsync();
        if (IsLoaded)
            ActivityText.Text = _autoAcceptEnabled ? "自动接受对局已启用。" : "自动接受对局已关闭。";
    }

    private void AutoOpenWebsiteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _autoOpenWebsiteEnabled = AutoOpenWebsiteCheckBox.IsChecked == true;
        _ = SavePreferencesAsync();
        if (IsLoaded)
            ActivityText.Text = _autoOpenWebsiteEnabled
                ? "进入海克斯对局后将自动打开最终英雄数据（每局一次）。"
                : "进游戏自动打开英雄数据已关闭。";
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel is null || RowsView is null) return;
        var isEmpty = !RowsView.Cast<object>().Any();
        var wasVisible = EmptyStatePanel.Visibility == Visibility.Visible;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (isEmpty && !wasVisible && AnimationsEnabled)
            EmptyStatePanel.BeginAnimation(OpacityProperty, CreateEaseOutAnimation(0, 1, 140));
        if (!isEmpty) return;
        var query = SearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
            EmptyStateText.Text = $"没有匹配“{query}”的英雄；清空搜索后重试。";
        else if (AvailableHeroesRadio?.IsChecked == true)
            EmptyStateText.Text = _champSelectWasActive
                ? "共享席当前没有空闲英雄；可切换到“队友持有”或“全部英雄”。"
                : "进入海克斯大乱斗选人阶段后，这里会显示共享席空闲英雄。";
        else if (TeammateHeroesRadio?.IsChecked == true)
            EmptyStateText.Text = _champSelectWasActive
                ? "当前没有可监视的队友英雄；可切换到“共享席空闲”或“全部英雄”。"
                : "进入海克斯大乱斗选人阶段后，这里会单独显示队友持有的英雄。";
        else
            EmptyStateText.Text = "没有符合搜索条件的英雄。";
    }

    private void ChampionLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(e.Uri.Host, "aramkit.com", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                ActivityText.Text = "未能打开浏览器，请检查默认浏览器设置。";
                WriteGrabDiagnostic("website-open-error", 0, ex.Message);
            }
        }
        e.Handled = true;
    }

    private async void RefreshData_Click(object sender, RoutedEventArgs e) => await LoadDataAsync(force: true);

    private bool IsCurrentCandidate(int championId, int revision) =>
        _armedChampionId == championId && _candidateRevision == revision;

    private void ChampionGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ChampionGrid.Columns.Count < 2) return;
        var fixedWidth = ChampionGrid.Columns.Where((_, index) => index != 1).Sum(column => column.Width.Value);
        var width = Math.Max(160, ChampionGrid.ActualWidth - fixedWidth - 16);
        ChampionGrid.Columns[1].Width = new DataGridLength(width);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private void ApplyLiveGame(LiveGameSnapshot live)
    {
        if (live.GameTime + 5 < _lastLiveGameTime) ResetGameWebsiteTracking();
        _lastLiveGameTime = live.GameTime;
        _liveConnected = true;
        var key = new string(live.ChampionKey.Where(char.IsLetterOrDigit).ToArray());
        var row = _rows.FirstOrDefault(r => (key.Length > 0 &&
            string.Equals(new string(r.Stat.Slug.Where(char.IsLetterOrDigit).ToArray()), key, StringComparison.OrdinalIgnoreCase)) ||
            (live.ChampionName.Length > 0 && (r.Name == live.ChampionName || r.Title == live.ChampionName)));
        if (row is not null) _finalChampionId = row.ChampionId;
        UpdateAvailability([], []);
        UpdateCurrentChampion(row?.ChampionId ?? _finalChampionId);
        SetConnectionState(true, "对局已连接", _lcu is null
            ? "已连接对局数据，可查看当前英雄；选人功能将在客户端连接恢复后可用。"
            : "对局进行中，当前英雄数据已同步。");
        if (_armedChampionId is not null) ArmedSubtitle.Text = "对局进行中，候选将在下一次海克斯选人阶段生效。";
        if (string.Equals(live.Mode, "KIWI", StringComparison.OrdinalIgnoreCase))
        {
            _hexChampSelectSeenForCurrentGame = true;
            TryOpenFinalChampionWebsite();
        }
    }

    private static string FriendlyPhase(string? phase) => phase switch
    {
        "Lobby" => "已连接大厅，进入选人后自动同步英雄。",
        "Matchmaking" => "正在匹配对局，自动接受将按开关设置执行。",
        "InProgress" or "GameStart" => "对局进行中，可在底部查看当前英雄数据。",
        "EndOfGame" or "WaitingForStats" or "PreEndOfGame" => "对局已结束，等待返回大厅。",
        _ => "客户端连接正常，等待进入选人阶段。"
    };

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        _reconnectRequested = true;
        ActivityText.Text = "已安排重新连接，正在检查本机会话。";
    }

    private async void ChooseClientDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择英雄联盟或 LeagueClient 文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        var directory = Directory.Exists(Path.Combine(dialog.FolderName, "LeagueClient"))
            ? Path.Combine(dialog.FolderName, "LeagueClient") : dialog.FolderName;
        try
        {
            await AppStorage.WriteAtomicAsync(Path.Combine(AppStorage.DirectoryPath, "client-directory.json"), directory);
            Reconnect_Click(sender, e);
        }
        catch (Exception ex) { ActivityText.Text = "无法保存游戏目录：" + ex.Message; }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape && SearchBox.IsKeyboardFocusWithin)
        { SearchBox.Clear(); e.Handled = true; }
    }
}
