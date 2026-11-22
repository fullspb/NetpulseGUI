using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetpulseGUI.Core.Processes;

/// <summary>Объём трафика одного приложения (все его процессы объединены) за интервал.</summary>
public sealed class AppUsage
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Rx { get; set; }
    public long Tx { get; set; }
    public HashSet<int> Pids { get; } = new();
    public string Key => string.IsNullOrEmpty(Path) ? Name : Path;
}

/// <summary>
/// Трафик по процессам через ETW (провайдер ядра Kernel Network: TCP/IP и UDP, IPv4 и IPv6).
/// Требует прав администратора. Петлевой (loopback) трафик игнорируется.
/// </summary>
public sealed class ProcessTrafficMonitor : IDisposable
{
    private readonly string _sessionName;
    private readonly ProcessResolver _resolver = new();
    private readonly object _sync = new();
    private Dictionary<int, (long rx, long tx)> _pending = new();
    private readonly HashSet<int> _exitedSinceDrain = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _running;

    public ProcessTrafficMonitor(string sessionName)
    {
        _sessionName = sessionName;
    }

    public bool IsRunning => _running;
    public string? LastError { get; private set; }

    /// <summary>Событие для логирования (сообщение, исключение).</summary>
    public event Action<string, Exception?>? Log;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Останавливает «зависшую» сессию с нашим именем (после аварийного завершения).</summary>
    public static bool CleanupStaleSession(string sessionName)
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(sessionName)) return false;
            using var stale = new TraceEventSession(sessionName, TraceEventSessionOptions.Attach);
            stale.Stop(noThrow: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Start()
    {
        if (_running) return true;
        if (!IsElevated())
        {
            LastError = "Нужны права администратора";
            return false;
        }

        try
        {
            if (CleanupStaleSession(_sessionName))
                Log?.Invoke($"Остановлена зависшая ETW-сессия {_sessionName}", null);
            CleanupStaleSession(Config.AppPaths.LegacyEtwSessionName);

            var session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 16 };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);

            var k = session.Source.Kernel;
            k.TcpIpRecv += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, true);
            k.TcpIpSend += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, false);
            k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, true);
            k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, false);
            k.UdpIpRecv += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, true);
            k.UdpIpSend += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, false);
            k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, true);
            k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, d.saddr, d.daddr, false);
            k.ProcessStart += d => _resolver.OnProcessStart(d.ProcessID, d.ImageFileName);
            k.ProcessDCStart += d => _resolver.NoteImageName(d.ProcessID, d.ImageFileName);
            k.ImageLoad += d =>
            {
                var file = d.FileName;
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    _resolver.NoteImagePath(d.ProcessID, file);
            };
            k.ProcessStop += d =>
            {
                lock (_sync) _exitedSinceDrain.Add(d.ProcessID);
            };

            _session = session;
            _running = true;
            _thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log?.Invoke("ETW: обработка событий прервана", ex);
                }
                finally
                {
                    _running = false;
                }
            })
            {
                IsBackground = true,
                Name = "NetpulseGUI ETW",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log?.Invoke("ETW: не удалось запустить сессию", ex);
            StopSession();
            return false;
        }
    }

    private void Add(int pid, int size, IPAddress? src, IPAddress? dst, bool receive)
    {
        if (size <= 0) return;
        if ((src != null && IPAddress.IsLoopback(src)) || (dst != null && IPAddress.IsLoopback(dst))) return;
        bool isNew;
        lock (_sync)
        {
            isNew = !_pending.TryGetValue(pid, out var v);
            _pending[pid] = receive ? (v.rx + size, v.tx) : (v.rx, v.tx + size);
        }
        // Первый пакет PID в этом интервале: фиксируем путь, пока процесс точно жив (результат кэшируется).
        if (isNew) _resolver.EnsureResolved(pid);
    }

    /// <summary>Забрать накопленные байты с момента прошлого вызова, объединённые по exe.</summary>
    public List<AppUsage> Drain()
    {
        Dictionary<int, (long rx, long tx)> batch;
        int[] exited;
        lock (_sync)
        {
            batch = _pending;
            _pending = new Dictionary<int, (long rx, long tx)>(Math.Max(16, batch.Count));
            exited = _exitedSinceDrain.ToArray();
            _exitedSinceDrain.Clear();
        }

        var byApp = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, v) in batch)
        {
            var id = _resolver.Resolve(pid);
            if (!byApp.TryGetValue(id.Key, out var usage))
            {
                usage = new AppUsage { Name = id.Name, Path = id.Path };
                byApp[id.Key] = usage;
            }
            usage.Rx += v.rx;
            usage.Tx += v.tx;
            usage.Pids.Add(pid);
        }

        // Завершившиеся процессы забываем после учёта их последних байтов.
        foreach (var pid in exited)
        {
            _resolver.OnProcessExit(pid);
            _resolver.ForgetImageName(pid);
        }

        return byApp.Values.ToList();
    }

    public void Stop()
    {
        StopSession();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    private void StopSession()
    {
        var s = Interlocked.Exchange(ref _session, null);
        if (s == null) return;
        try
        {
            s.Stop(noThrow: true);
            s.Dispose();
        }
        catch (Exception ex)
        {
            Log?.Invoke("ETW: ошибка при остановке сессии", ex);
        }
        _running = false;
    }

    public void Dispose() => Stop();
}
