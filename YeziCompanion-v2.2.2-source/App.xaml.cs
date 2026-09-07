using System.IO;
using System.Windows;
using System.Windows.Threading;
using YeziCompanion.Services;

namespace YeziCompanion;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\HexARAMCompanion.SingleInstance";
    private const string ActivationEventName = @"Local\HexARAMCompanion.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationLifetime;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HexARAMCompanion.V2");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.txt"), e.Exception.ToString());
        }
        catch
        {
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var connectionTest = e.Args.Contains("--diagnose-connection", StringComparer.OrdinalIgnoreCase);
        var isUiSelfTest = e.Args.Contains("--self-test-ui", StringComparer.OrdinalIgnoreCase) || connectionTest;
        if (!isUiSelfTest)
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                try
                {
                    using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                    activationEvent.Set();
                }
                catch
                {
                }
                // Shutdown before WPF has completed startup can leave a headless process.
                // This is a duplicate process with no user state, so terminate it directly.
                Environment.Exit(0);
            }

            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationLifetime = new CancellationTokenSource();
            _ = Task.Run(() => ListenForActivation(_activationLifetime.Token));
        }

        base.OnStartup(e);
        if (isUiSelfTest)
        {
            // Build only a WPF visual tree: never Show(), focus, discover LCU, or open a browser.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (connectionTest) await ConnectionDiagnostics.RunAsync();
                else
                {
                    var testWindow = new MainWindow();
                    await testWindow.RunHeadlessSelfTestAsync();
                }
                Shutdown(Environment.ExitCode);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(AppStorage.DirectoryPath);
                File.WriteAllText(Path.Combine(AppStorage.DirectoryPath, "self-test-error.txt"), ex.ToString());
                Shutdown(10);
            }
        }
        else
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationLifetime?.Cancel();
        _activationEvent?.Set();
        _activationEvent?.Dispose();
        _activationLifetime?.Dispose();
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ListenForActivation(CancellationToken cancellationToken)
    {
        if (_activationEvent is null) return;
        var handles = new[] { _activationEvent, cancellationToken.WaitHandle };
        while (!cancellationToken.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 0) return;
            Dispatcher.BeginInvoke(ActivateMainWindow);
        }
    }

    private void ActivateMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

}
