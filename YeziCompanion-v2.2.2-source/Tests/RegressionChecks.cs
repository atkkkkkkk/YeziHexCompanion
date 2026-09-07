using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Net;
using System.Net.Http;
using YeziCompanion.Models;
using YeziCompanion.Services;

namespace YeziCompanion;

public partial class MainWindow
{
    private async Task<Dictionary<string, bool>> RunRegressionChecksAsync()
    {
        var checks = new Dictionary<string, bool>();
        // Reproduce a cached zero-length directory entry with data readable through an open writer.
        var logPath = Path.Combine(AppStorage.DirectoryPath, "test-client-discovery.log");
        await File.WriteAllTextAsync(logPath, "");
        var staleLog = new FileInfo(logPath);
        staleLog.Refresh();
        var cachedLength = staleLog.Length;
        checks["TrulyEmptyLogHasNoCredentials"] = await LcuClient.ReadLogCredentialsAsync(staleLog, CancellationToken.None) is null;
        await using (var writer = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("--app-port=12345 --remoting-auth-token=offline-test-token\n"));
            await writer.FlushAsync();
            var found = await LcuClient.ReadLogCredentialsAsync(staleLog, CancellationToken.None);
            checks["ZeroLengthMetadataStillReadsOpenLog"] = cachedLength == 0 && staleLog.Length == 0 &&
                found is { Port: 12345, Token: "offline-test-token" };
        }
        await File.WriteAllTextAsync(logPath, "--app-port=12345");
        checks["IncompleteLogCanRecoverLater"] = await LcuClient.ReadLogCredentialsAsync(staleLog, CancellationToken.None) is null;
        await File.AppendAllTextAsync(logPath, " --remoting-auth-token=\"offline-retry\"\n");
        checks["IncompleteLogCanRecoverLater"] &= await LcuClient.ReadLogCredentialsAsync(staleLog, CancellationToken.None)
            is { Port: 12345, Token: "offline-retry" };
        await File.WriteAllTextAsync(logPath, "--app-port=99999 --remoting-auth-token=offline-invalid\n");
        checks["LogRejectsInvalidPort"] = await LcuClient.ReadLogCredentialsAsync(staleLog, CancellationToken.None) is null;
        using (var cancelledRead = new CancellationTokenSource())
        {
            cancelledRead.Cancel();
            try { await LcuClient.ReadLogCredentialsAsync(staleLog, cancelledRead.Token); checks["LogReadHonorsCancellation"] = false; }
            catch (OperationCanceledException) { checks["LogReadHonorsCancellation"] = true; }
        }
        var first = _rows[0];
        var second = _rows[1];
        ArmChampion(first.ChampionId);
        var revision = _candidateRevision;
        ArmChampion(second.ChampionId);
        CompleteGrab(first.ChampionId, 123, "stale response");
        checks["OldResponseKeepsNewCandidate"] = _armedChampionId == second.ChampionId && second.IsArmed && !IsCurrentCandidate(first.ChampionId, revision);
        revision = _candidateRevision;
        ArmChampion(second.ChampionId);
        checks["RepeatedClickInvalidatesOldResponse"] = !IsCurrentCandidate(second.ChampionId, revision) && second.IsArmed;
        revision = _candidateRevision;
        CancelArmed();
        checks["CancelInvalidatesInFlightResponse"] = !IsCurrentCandidate(second.ChampionId, revision) && !second.IsArmed && !CancelCandidateButton.IsEnabled;

        using (var transport = new DelayedSwapTransport())
        using (var client = new LcuClient(transport))
        {
            _lcu = client;
            _latestIsHexAram = true;
            _champSelectWasActive = true; // Prevent any window activation in an offline test.
            ArmChampion(first.ChampionId);
            var snapshot = new ChampSelectSnapshot(900, true, 0, [first.ChampionId], []);
            var eventDelivery = HandlePushedChampSelectAsync(client, snapshot, _lifetime.Token);
            await eventDelivery.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            checks["EventReceiverDoesNotWaitForSwap"] = eventDelivery.IsCompletedSuccessfully && !transport.Response.Task.IsCompleted;
            ArmChampion(second.ChampionId);
            transport.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
            await _swapGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            _swapGate.Release();
            checks["DelayedHttpResultKeepsNewCandidate"] = _armedChampionId == second.ChampionId && second.IsArmed;
            checks["SwapSentExactlyOnce"] = transport.RequestCount == 1;
            _lcu = null;
            ClearLatestChampSelect();
            _champSelectWasActive = false;
        }

        ArmChampion(first.ChampionId);
        var dataset = new ChampionDataset("test-refresh", DateTimeOffset.Now, _rows.Select(r => r.Stat).ToList());
        ApplyDataset(dataset);
        checks["RefreshPreservesRowsAndCandidate"] = ReferenceEquals(first, _rowsById[first.ChampionId]) && first.IsArmed && _armedChampionId == first.ChampionId;
        var rejected = false;
        try { ApplyDataset(dataset with { Champions = [first.Stat, first.Stat] }); }
        catch (InvalidDataException) { rejected = true; }
        checks["InvalidDatasetPreservesCurrentData"] = rejected && _rows.Count == dataset.Champions.Count && first.IsArmed;

        var last = _rows[^1];
        ChampionGrid.ScrollIntoView(last);
        RootSurface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var container = ChampionGrid.ItemContainerGenerator.ContainerFromItem(last) as DataGridRow;
        var button = container is null ? null : FindVisualChild<Button>(container);
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["RecycledRowButtonTargetsCorrectHero"] = _armedChampionId == last.ChampionId && last.IsArmed;

        SearchBox.Text = "__no_such_champion__";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks["SearchEmptyState"] = RowsView.IsEmpty && EmptyStatePanel.Visibility == Visibility.Visible;
        CaptureUiPreviewIfRequested("empty");
        SearchBox.Clear();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        checks["SearchClearRestoresList"] = RowsView.Cast<object>().Count() == _rows.Count && EmptyStatePanel.Visibility == Visibility.Collapsed;

        using var mixed = JsonDocument.Parse("""
            {"gameId":"123", "localPlayerCellId":"1", "benchEnabled":true,
             "benchChampionIds":[22,22,526,157,null,"33"],
             "myTeam":[null,7,{"cellId":"1","championId":157},{"cellId":2,"championId":526}]}
            """);
        var parsed = LcuClient.ParseChampSelectSnapshot(mixed.RootElement);
        checks["MixedTypesAndConflictingOwnership"] = parsed is { GameId: 123, CurrentChampionId: 157 } &&
            parsed.BenchChampionIds.SequenceEqual([22, 33]) && parsed.TeammateChampionIds.SequenceEqual([526]);
        using var nullable = JsonDocument.Parse("""{"gameId":null,"localPlayerCellId":null,"myTeam":[null]}""");
        checks["NullSessionFieldsDoNotCrash"] = LcuClient.ParseChampSelectSnapshot(nullable.RootElement) is { GameId: 0, CurrentChampionId: 0 };

        var settingsPath = Path.Combine(AppStorage.DirectoryPath, "test-preferences.json");
        var preferences = new UserPreferences(false, true, false);
        await AppStorage.WriteAtomicAsync(settingsPath, preferences);
        checks["SettingsRoundTrip"] = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(settingsPath)) == preferences;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await AppStorage.WriteAtomicAsync(settingsPath, new UserPreferences(), cancelled.Token); }
        catch (OperationCanceledException) { }
        checks["CancelledWriteKeepsPreviousSettings"] = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(settingsPath)) == preferences;
        checks["WebpPortraitsDecoded"] = _rows.Count(r => r.Portrait is not null) == 8;
        _websiteOpenedForCurrentGame = false;
        _hexChampSelectSeenForCurrentGame = true;
        _finalChampionId = int.MaxValue;
        checks["MissingHeroDoesNotConsumeWebsiteOpen"] = !TryOpenFinalChampionWebsite(false) && !_websiteOpenedForCurrentGame;
        _finalChampionId = first.ChampionId;
        checks["WebsiteRecoversWhenHeroBecomesKnown"] = TryOpenFinalChampionWebsite(false) && !TryOpenFinalChampionWebsite(false);

        using (var transport = new LiveGameTransport())
        using (var liveClient = new LiveGameClient(transport))
        {
            var live = await liveClient.TryReadAsync(CancellationToken.None);
            checks["MidGameStartupRecognizesOwnHero"] = live is { Mode: "KIWI", ChampionKey: "Gwen", ChampionName: "灵罗娃娃" };
            _autoOpenWebsiteEnabled = false;
            if (live is not null) ApplyLiveGame(live);
            checks["LiveOnlyConnectionUpdatesHeroAndState"] = _lcu is null && _currentChampionId == 887 &&
                ConnectionText.Text == "对局已连接" && ConnectionHintText.Text.Contains("选人功能");
            _autoOpenWebsiteEnabled = true;
            transport.FailPlayerDetails = true;
            checks["MissingPlayerDetailsKeepsGameConnection"] = await liveClient.TryReadAsync(CancellationToken.None) is { Mode: "KIWI", ChampionKey: "" };
            transport.FailGame = true;
            checks["MissingGameNeverReportsConnected"] = await liveClient.TryReadAsync(CancellationToken.None) is null;
            checks["GameFallbackIsReadOnly"] = transport.OnlyGetRequests;
        }

        CancelArmed();
        UpdateAvailability([second.ChampionId], [first.ChampionId]);
        UpdateCurrentChampion(_rows[2].ChampionId);
        ArmChampion(second.ChampionId);
        ActivityText.Text = "离线场景预览 · 候选与当前英雄";
        ChampionGrid.ScrollIntoView(first);
        RootSurface.Measure(new Size(1104, 780));
        RootSurface.Arrange(new Rect(0, 0, 1104, 780));
        RootSurface.UpdateLayout();
        CaptureUiPreviewIfRequested("selection");
        RootSurface.Measure(new Size(924, 641));
        RootSurface.Arrange(new Rect(0, 0, 924, 641));
        RootSurface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        RootSurface.UpdateLayout();
        checks["MinimumWidthFitsAllColumns"] = ChampionGrid.Columns.Sum(c => c.ActualWidth) <= ChampionGrid.ActualWidth + 1;
        checks["MinimumHeightKeepsUsableTable"] = ChampionGrid.ActualHeight >= 170;
        var lastNavBottom = TeammateHeroesRadio.TransformToAncestor(RootSurface).Transform(new Point(0, TeammateHeroesRadio.ActualHeight)).Y;
        var connectionTop = ConnectionCenter.TransformToAncestor(RootSurface).Transform(new Point(0, 0)).Y;
        checks["SidebarSectionsDoNotOverlap"] = lastNavBottom + 8 <= connectionTop;
        CaptureUiPreviewIfRequested("compact");
        CancelArmed();
        return checks;
    }

    private void WriteApplicationIcon()
    {
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawImage(Icon, new Rect(0, 0, 256, 256));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        Directory.CreateDirectory(AppStorage.DirectoryPath);
        using var output = new BinaryWriter(File.Create(Path.Combine(AppStorage.DirectoryPath, "App.ico")));
        output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)1);
        output.Write((byte)0); output.Write((byte)0); output.Write((byte)0); output.Write((byte)0);
        output.Write((ushort)1); output.Write((ushort)32);
        output.Write((uint)png.Length); output.Write((uint)22); output.Write(png.ToArray());
    }

    private sealed class DelayedSwapTransport : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> RequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Unexpected test request");
            RequestCount++;
            RequestArrived.TrySetResult(true);
            return Response.Task.WaitAsync(token);
        }
    }

    private sealed class LiveGameTransport : HttpMessageHandler
    {
        internal bool FailPlayerDetails { get; set; }
        internal bool FailGame { get; set; }
        internal bool OnlyGetRequests { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            OnlyGetRequests &= request.Method == HttpMethod.Get;
            var path = request.RequestUri!.AbsolutePath;
            if (FailGame || (FailPlayerDetails && path != "/liveclientdata/gamestats"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json = path switch
            {
                "/liveclientdata/gamestats" => """{"gameMode":"KIWI","gameTime":123.5}""",
                "/liveclientdata/activeplayer" => """{"riotId":"offline#test"}""",
                "/liveclientdata/playerlist" => """[{"riotId":"someone#else","championName":"亚索","rawChampionName":"game_character_displayname_Yasuo"},{"riotId":"offline#test","championName":"灵罗娃娃","rawChampionName":"game_character_displayname_Gwen"}]""",
                _ => throw new InvalidOperationException("Unexpected live data route")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
