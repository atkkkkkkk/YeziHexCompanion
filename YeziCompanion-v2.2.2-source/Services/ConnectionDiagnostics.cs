using System.Diagnostics;
using System.IO;

namespace YeziCompanion.Services;

internal static class ConnectionDiagnostics
{
    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        using var liveClient = new LiveGameClient();
        var lcuTask = Task.Run(() => LcuClient.TryDiscoverAsync(deadline.Token));
        var liveTask = liveClient.TryReadAsync(deadline.Token);
        using var lcu = await lcuTask;
        var live = await liveTask;
        await AppStorage.WriteAtomicAsync(Path.Combine(AppStorage.DirectoryPath, "connection-test.json"), new
        {
            ObservedAt = DateTimeOffset.Now,
            LcuConnected = lcu is not null,
            LiveGameConnected = live is not null,
            GameMode = live?.Mode,
            ChampionKey = live?.ChampionKey,
            ElapsedMs = watch.ElapsedMilliseconds,
            ReadOnly = true,
            WindowShown = false
        });
    }
}
