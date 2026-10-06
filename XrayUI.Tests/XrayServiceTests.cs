using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XrayUI.Services;

namespace XrayUI.Tests;

public class XrayServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XrayUI-core-tests", Guid.NewGuid().ToString("N"));
    private readonly XrayService _service;
    private readonly ConcurrentQueue<Process> _processes = new();

    public XrayServiceTests()
    {
        _service = new XrayService(Path.Combine(_directory, "config.json"));
        _service.LogReceived += (_, line) =>
        {
            const string prefix = "[XrayUI] core PID: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                _processes.Enqueue(Process.GetProcessById(int.Parse(line[prefix.Length..])));
        };
    }

    [Fact]
    public async Task ConcurrentStarts_ReplaceOnlyTheirOwnProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        var states = new ConcurrentQueue<(bool Notified, bool Actual)>();
        _service.RunningChanged += (_, running) => states.Enqueue((running, _service.IsRunning));

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _service.StartAsync(Config())));

        Assert.All(results, Assert.True);
        Assert.True(_service.IsRunning);
        var processes = _processes.ToArray();
        Assert.Equal(3, processes.Length);
        Assert.All(processes[..^1], process => Assert.True(process.HasExited));
        Assert.False(processes[^1].HasExited);
        Assert.All(states, state => Assert.Equal(state.Notified, state.Actual));
    }

    [Fact]
    public async Task StopOverlappingRestart_DoesNotDisposeNewProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        Assert.True(await _service.StartAsync(Config()));

        var stop = _service.StopAsync();
        var restart = _service.StartAsync(Config());
        await stop;

        Assert.True(await restart);
        Assert.True(_service.IsRunning);
        Assert.True(_processes.First().HasExited);
        Assert.False(_processes.Last().HasExited);
    }

    [Fact]
    public async Task ThrowingEventSubscribers_DoNotFailStartupOrBlockOtherSubscribers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        var delivered = new ConcurrentQueue<bool>();
        _service.LogReceived += (_, _) => throw new InvalidOperationException("log consumer failed");
        _service.RunningChanged += (_, _) => throw new InvalidOperationException("state consumer failed");
        _service.RunningChanged += (_, running) => delivered.Enqueue(running);

        Assert.True(await _service.StartAsync(Config()));
        Assert.True(_service.IsRunning);
        await _service.StopAsync();

        Assert.Equal(new[] { true, false }, delivered);
        Assert.All(_processes, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task ShutdownDuringStartup_StopsTheProcessAndRejectsLaterStarts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        Task<bool>? queuedStart = null;
        _service.LogReceived += (_, line) =>
        {
            if (line.StartsWith("[XrayUI] core PID: ", StringComparison.Ordinal))
            {
                queuedStart = _service.StartAsync(Config());
                _service.StopForShutdown();
            }
        };

        Assert.False(await _service.StartAsync(Config()));
        Assert.NotNull(queuedStart);
        Assert.False(await queuedStart);
        Assert.False(await _service.StartAsync(Config()));
        Assert.False(_service.IsRunning);
        Assert.Single(_processes);
        Assert.All(_processes, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task FailedStart_PreservesCoreErrorAndAllowsNextStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        Assert.False(await _service.StartAsync(Config().Replace("socks", "unknown-test-protocol")));
        Assert.False(_service.IsRunning);
        Assert.Contains("unknown-test-protocol", _service.LastError);

        Assert.True(await _service.StartAsync(Config()));
        Assert.True(_service.IsRunning);
        Assert.Empty(_service.LastError);
    }

    [Fact]
    public async Task UnexpectedExit_ReportsExitCodeAndRecentCoreOutput()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Bundled core is a Windows executable.");
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.RunningChanged += (_, running) =>
        {
            if (!running) exited.TrySetResult();
        };
        Assert.True(await _service.StartAsync(Config()));

        _processes.Single().Kill();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(_service.IsRunning);
        Assert.Contains("Xray_ExitedImmediately", _service.LastError);
        Assert.Contains("core: Xray", _service.LastError);
        Assert.Contains(_service.GetLogBuffer(), line => line.Contains("exited with code", StringComparison.Ordinal));
    }

    private static string Config()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $$"""
            {
              "log": { "loglevel": "warning" },
              "inbounds": [{ "protocol": "socks", "listen": "127.0.0.1", "port": {{port}} }],
              "outbounds": [{ "protocol": "freedom" }]
            }
            """;
    }

    public void Dispose()
    {
        _service.StopForShutdown();
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            finally { process.Dispose(); }
        }
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
