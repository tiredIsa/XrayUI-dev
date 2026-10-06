using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;

namespace XrayUI.Services
{
    public partial class XrayService
    {
        // Public so helper cores (e.g. RealLatencyProbeService's throwaway speed-test core) can
        // launch the same engine without duplicating the path.
        public static readonly string ExePath = Path.Combine(
            AppContext.BaseDirectory, "Assets", "engine", "xray.exe");

        public static readonly string RulesDir = Path.Combine(
            AppContext.BaseDirectory, "Assets", "rules");

        private readonly string _configPath;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private int _shutdownRequested;

        private const int LogBufferMax = 500;

        // Upper bound on waiting for the readiness line of a freshly launched core (see
        // XrayReadySignal). Normally Ready lands in tens of ms; this cap only bites when
        // the core stays silent, where it reproduces the old fixed-delay behavior.
        private static readonly TimeSpan StartupReadyCap = TimeSpan.FromSeconds(3);

        private Process? _process;

        // Kernel-level safety net: xray.exe is assigned to a kill-on-close Job Object, so if the
        // UI process dies any way (taskkill /F, AV crash, OOM) the kernel kills xray.exe too.
        // Created lazily on first start and reused across Start/Stop cycles.
        private JobObjectGuard? _jobGuard;

        private StringBuilder _startupLog = new();
        private bool _collectStartupLog;
        private readonly Lock _startupLogLock = new();

        // Fixed-size ring buffer: O(1) append + oldest-drop, no array shifting.
        private readonly string[] _logBuffer = new string[LogBufferMax];
        private int _logHead;    // index of the next write slot
        private int _logCount;   // number of valid entries (<= LogBufferMax)
        private readonly Lock _bufferLock = new();

        public XrayService() : this(Path.Combine(AppPaths.LocalAppDataDir, "xray_config.json")) { }

        internal XrayService(string configPath)
        {
            _configPath = configPath;
        }

        public bool IsRunning
        {
            get
            {
                var process = Volatile.Read(ref _process);
                try { return process is { HasExited: false }; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public string LastError { get; private set; } = string.Empty;

        public event EventHandler<string>? LogReceived;

        public event EventHandler<bool>? RunningChanged;

        public IReadOnlyList<string> GetLogBuffer()
        {
            lock (_bufferLock)
            {
                if (_logCount == 0)
                {
                    return Array.Empty<string>();
                }

                var snapshot = new string[_logCount];
                if (_logCount < LogBufferMax)
                {
                    // Not yet wrapped — data is contiguous in [0, _logCount)
                    Array.Copy(_logBuffer, 0, snapshot, 0, _logCount);
                }
                else
                {
                    // Wrapped — oldest at _logHead, newest at _logHead-1
                    int tailCount = LogBufferMax - _logHead;
                    Array.Copy(_logBuffer, _logHead, snapshot, 0, tailCount);
                    Array.Copy(_logBuffer, 0, snapshot, tailCount, _logHead);
                }
                return snapshot;
            }
        }

        public void ClearLogBuffer()
        {
            lock (_bufferLock)
            {
                Array.Clear(_logBuffer, 0, _logBuffer.Length);
                _logHead = 0;
                _logCount = 0;
            }
        }

        private void AppendLog(string line)
        {
            lock (_bufferLock)
            {
                _logBuffer[_logHead] = line;
                _logHead = (_logHead + 1) % LogBufferMax;
                if (_logCount < LogBufferMax)
                {
                    _logCount++;
                }
            }

            NotifySubscribers(LogReceived, line);
        }

        private void NotifySubscribers<T>(EventHandler<T>? handlers, T value)
        {
            if (handlers is null) return;
            foreach (EventHandler<T> handler in handlers.GetInvocationList())
            {
                try { handler(this, value); }
                catch (Exception ex)
                {
                    // Process callbacks run on worker threads. A UI/log consumer must not
                    // crash the host and consequently kill its job-owned core.
                    Debug.WriteLine($"[XrayService] Event subscriber failed: {ex}");
                }
            }
        }

        private void BeginStartupLogCapture()
        {
            lock (_startupLogLock)
            {
                _startupLog = new StringBuilder();
                _collectStartupLog = true;
            }
        }

        private void AppendStartupLog(string line)
        {
            lock (_startupLogLock)
            {
                if (!_collectStartupLog)
                {
                    return;
                }

                _startupLog.AppendLine(line);
            }
        }

        private string StopStartupLogCaptureAndRead()
        {
            lock (_startupLogLock)
            {
                _collectStartupLog = false;
                var text = _startupLog.Length > 0
                    ? _startupLog.ToString().Trim()
                    : string.Empty;
                _startupLog = new StringBuilder();
                return text;
            }
        }

        private void StopStartupLogCapture()
        {
            lock (_startupLogLock)
            {
                _collectStartupLog = false;
                _startupLog = new StringBuilder();
            }
        }

        public async Task<bool> StartAsync(string configJson)
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                if (Volatile.Read(ref _shutdownRequested) != 0) return false;
                return await StartCoreAsync(configJson);
            }
            finally { _lifecycleLock.Release(); }
        }

        private async Task<bool> StartCoreAsync(string configJson)
        {
            if (_process is not null)
            {
                // Restart path (e.g. ReapplyRoutingAsync): skip the DNS flush — the new xray
                // session is about to repopulate the resolver cache anyway, and flushing
                // adds avoidable latency to every routing/DNS/proxy-mode toggle.
                await StopCoreAsync();
            }

            LastError = string.Empty;

            if (!File.Exists(ExePath))
            {
                LastError = Loc.Format("Xray_ExeNotFound", ExePath);
                AppendLog(Loc.Format("XrayLog_Error", LastError));
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                await File.WriteAllTextAsync(_configPath, configJson);
                if (Volatile.Read(ref _shutdownRequested) != 0) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"run -config \"{_configPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = RulesDir;

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                Volatile.Write(ref _process, process);
                var readySignal = XrayReadySignal.Attach(process);

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendStartupLog(e.Data);
                    AppendLog(e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendStartupLog(e.Data);
                    AppendLog(e.Data);
                };

                process.Exited += OnProcessExited;

                BeginStartupLogCapture();
                if (Volatile.Read(ref _shutdownRequested) != 0)
                {
                    await StopCoreAsync();
                    return false;
                }
                process.Start();
                AttachToJobObject(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                AppendLog(Loc.Format("XrayLog_Started", ExePath));
                AppendLog(Loc.Format("XrayLog_Config", _configPath));
                AppendLog($"[XrayUI] core PID: {process.Id}");

                // Ready typically lands well under 100ms; Exited surfaces bad configs / port
                // clashes / TUN elevation failures immediately instead of after a fixed wait.
                // TimedOut with the process still alive counts as success, same as before.
                var readyStopwatch = Stopwatch.StartNew();
                var outcome = await readySignal.WaitAsync(StartupReadyCap);
                readyStopwatch.Stop();

                // Diagnostic for "occasionally slow start": logs how long the readiness wait
                // actually took and how it resolved.
                var readyLabel = outcome switch
                {
                    XrayReadySignal.Outcome.Ready => "Ready",
                    XrayReadySignal.Outcome.Exited => "Exited",
                    _ => "TimedOut"
                };
                AppendLog($"[XrayUI] core readiness: {readyLabel} in {readyStopwatch.ElapsedMilliseconds} ms");

                if (outcome == XrayReadySignal.Outcome.Exited || process.HasExited)
                {
                    // Process output is delivered through asynchronous DataReceived
                    // callbacks. WaitForExitAsync (unlike only checking HasExited) also
                    // drains the redirected stdout/stderr pipes before we snapshot the
                    // startup log; otherwise users only see the generic exit code.
                    try { await process.WaitForExitAsync(); } catch { }
                    var startupLog = StopStartupLogCaptureAndRead();
                    LastError = startupLog.Length > 0
                        ? startupLog
                        : Loc.Format("Xray_ExitedImmediately", process.ExitCode);
                    AppendLog(Loc.Format("XrayLog_StartFailed", LastError));
                    await StopCoreAsync();
                    return false;
                }

                StopStartupLogCapture();
                if (Volatile.Read(ref _shutdownRequested) != 0 || !ReferenceEquals(_process, process))
                    return false;
                NotifySubscribers(RunningChanged, true);
                return true;
            }
            catch (Exception ex)
            {
                StopStartupLogCapture();
                LastError = ex.Message;
                AppendLog(Loc.Format("XrayLog_Exception", ex.Message));
                await StopCoreAsync();
                return false;
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                await StopCoreAsync();
                FlushSystemDnsCache();
            }
            finally { _lifecycleLock.Release(); }
        }

        /// <summary>
        /// Kills the xray process and tears down state, without flushing the OS DNS cache.
        /// Used by StartAsync on the restart path so reapply doesn't pay DNS flush latency
        /// on every routing/DNS/proxy-mode toggle. No-op if not running.
        /// </summary>
        private async Task StopCoreAsync()
        {
            var process = Interlocked.Exchange(ref _process, null);
            if (process is null)
            {
                return;
            }

            process.Exited -= OnProcessExited;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }

            AppendLog(L.XrayLog_Stopped);
            NotifySubscribers(RunningChanged, false);
        }

        /// <summary>
        /// Best-effort DNS resolver cache flush to clear any cached fake IPs (198.18.0.0/15) that
        /// might linger in the Windows resolver cache after a FakeDNS-enabled run. Harmless
        /// when FakeDNS was not used. Runs unconditionally on every stop to keep XrayService
        /// stateless w.r.t. last-run config.
        /// </summary>
        private void FlushSystemDnsCache()
        {
            try
            {
                if (!DnsFlushResolverCache())
                {
                    Debug.WriteLine($"[DNS] DnsFlushResolverCache failed: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                // best-effort, never block stop on this
                Debug.WriteLine($"[DNS] DnsFlushResolverCache exception: {ex.Message}");
            }
        }

        [DllImport("dnsapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DnsFlushResolverCache();

        public void StopForShutdown()
        {
            // Shutdown cannot wait for an async operation that needs the UI dispatcher.
            // Detach ownership atomically and prevent queued starts from launching later.
            Interlocked.Exchange(ref _shutdownRequested, 1);
            var process = Interlocked.Exchange(ref _process, null);
            if (process is null)
            {
                return;
            }

            process.Exited -= OnProcessExited;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(500);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }

            AppendLog(L.XrayLog_Shutdown);
            NotifySubscribers(RunningChanged, false);
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            if (sender is Process process && ReferenceEquals(Volatile.Read(ref _process), process))
                _ = ReportProcessExitAsync(process);
        }

        private async Task ReportProcessExitAsync(Process process)
        {
            try
            {
                // Exited can arrive before stderr's panic/fatal output. Drain it before
                // notifying the UI, and ignore an old process after a stop or restart.
                await process.WaitForExitAsync().ConfigureAwait(false);
                await _lifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(Volatile.Read(ref _process), process)) return;
                    var exit = Loc.Format("Xray_ExitedImmediately", process.ExitCode);
                    var recentOutput = string.Join(Environment.NewLine, GetLogBuffer().TakeLast(40));
                    LastError = string.IsNullOrWhiteSpace(recentOutput) ? exit : exit + Environment.NewLine + recentOutput;
                    AppendLog($"[XrayUI] core PID {process.Id} exited with code {process.ExitCode}");
                    NotifySubscribers(RunningChanged, false);
                }
                finally { _lifecycleLock.Release(); }
            }
            catch (Exception ex) { Debug.WriteLine($"[XrayService] Exit reporting failed: {ex}"); }
        }

        // ─────────── Job Object: orphan-xray safety net ───────────
        // The kill-on-close Job Object interop lives in Helpers/JobObjectGuard (shared with the
        // real-delay speed-test core). The guard is created lazily and reused across Start/Stop
        // cycles; each launched process is assigned to it.

        private void AttachToJobObject(Process process)
        {
            try
            {
                _jobGuard ??= JobObjectGuard.Create();
                if (_jobGuard is null)
                {
                    AppendLog(Loc.Format("XrayLog_JobCreateFailed", Marshal.GetLastWin32Error()));
                    return;
                }

                if (!_jobGuard.TryAssign(process))
                {
                    AppendLog(Loc.Format("XrayLog_JobAssignFailed", Marshal.GetLastWin32Error()));
                }
            }
            catch (Exception ex)
            {
                AppendLog(Loc.Format("XrayLog_JobBindException", ex.Message));
            }
        }
    }
}
