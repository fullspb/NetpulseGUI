using System.Runtime.InteropServices;
using System.Text;

namespace NetpulseGUI.Core.Processes;

public readonly record struct ProcessIdentity(string Name, string Path)
{
    public string Key => string.IsNullOrEmpty(Path) ? Name : Path;
}

/// <summary>
/// PID → имя exe и полный путь. Кэширует результат; запись удаляется при завершении процесса
/// (событие ETW ProcessStop), чтобы переиспользованный PID не приписывался старому exe.
/// </summary>
public sealed class ProcessResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Dictionary<int, ProcessIdentity> _cache = new();
    private readonly Dictionary<int, string> _imageNames = new();
    private readonly object _sync = new();

    public static readonly ProcessIdentity SystemIdentity = new("System", "System");
    public static readonly ProcessIdentity UnknownIdentity = new("Unknown", "");

    public int CacheSize
    {
        get { lock (_sync) return _cache.Count; }
    }

    /// <summary>Имя образа из событий ETW ProcessStart/DCStart — на случай, если процесс завершится до разрешения пути.</summary>
    public void NoteImageName(int pid, string imageFileName)
    {
        if (pid <= 4 || string.IsNullOrEmpty(imageFileName)) return;
        lock (_sync)
        {
            _imageNames[pid] = imageFileName;
            _cache.Remove(pid);
        }
    }

    /// <summary>
    /// Процесс запущен (ETW ProcessStart): сразу определяем полный путь, пока процесс жив —
    /// короткоживущие процессы (curl, установщики) могут завершиться раньше ближайшего опроса.
    /// </summary>
    public void OnProcessStart(int pid, string imageFileName)
    {
        NoteImageName(pid, imageFileName);
        Resolve(pid);
    }

    /// <summary>
    /// Полный путь к exe из события ETW ImageLoad. Доставка событий в реальном времени запаздывает
    /// примерно на секунду, поэтому для процессов, живущих доли секунды, это единственный источник пути.
    /// </summary>
    public void NoteImagePath(int pid, string fullPath)
    {
        if (pid <= 4 || string.IsNullOrEmpty(fullPath)) return;
        var name = System.IO.Path.GetFileName(fullPath);
        lock (_sync)
        {
            // Принимаем только главный модуль: имя должно совпасть с ImageFileName из ProcessStart (если оно известно).
            if (_imageNames.TryGetValue(pid, out var image) && !string.Equals(image, name, StringComparison.OrdinalIgnoreCase))
                return;
            if (_cache.TryGetValue(pid, out var cached) && !string.IsNullOrEmpty(cached.Path)) return;
            _cache[pid] = new ProcessIdentity(name, fullPath);
        }
    }

    /// <summary>Определить путь при первом появлении PID в сетевых событиях (если ещё не в кэше).</summary>
    public void EnsureResolved(int pid)
    {
        lock (_sync)
        {
            if (_cache.ContainsKey(pid)) return;
        }
        Resolve(pid);
    }

    public void OnProcessExit(int pid)
    {
        lock (_sync)
        {
            // Даём шанс дорешать последние события процесса по имени образа.
            _cache.Remove(pid);
        }
    }

    public void ForgetImageName(int pid)
    {
        lock (_sync) _imageNames.Remove(pid);
    }

    public ProcessIdentity Resolve(int pid)
    {
        if (pid is 0 or 4) return SystemIdentity;
        if (pid < 0) return UnknownIdentity;

        lock (_sync)
        {
            if (_cache.TryGetValue(pid, out var cached)) return cached;
        }

        var path = QueryPath(pid);
        ProcessIdentity id;
        if (!string.IsNullOrEmpty(path))
        {
            id = new ProcessIdentity(System.IO.Path.GetFileName(path), path);
        }
        else
        {
            string? image;
            lock (_sync) _imageNames.TryGetValue(pid, out image);
            id = string.IsNullOrEmpty(image) ? UnknownIdentity : new ProcessIdentity(image, "");
        }

        lock (_sync)
        {
            if (_cache.Count > 4096) _cache.Clear();
            _cache[pid] = id;
        }
        return id;
    }

    private static string? QueryPath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
