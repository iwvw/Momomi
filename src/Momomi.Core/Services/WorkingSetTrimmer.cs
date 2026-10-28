using System.Runtime;
using System.Runtime.InteropServices;

namespace Momomi.Core.Services;

/// <summary>
/// 主动回收工作集（working set）。
///
/// 背景：本程序托管堆只有几 MB，但工作集能到一两百兆。
/// 差额几乎全是 WinUI 3 / D3D / 显卡驱动在渲染时申请的**原生内存**：
/// 交换链后备缓冲、着色器编译缓存、字形与纹理缓存、D3D 的 staging 资源等。
/// 这些页一旦被访问过就一直留在工作集里，即使窗口已经隐藏、
/// 那部分资源短期内不会再碰。
///
/// 关键点：<see cref="GC.Collect(int, GCCollectionMode, bool, bool)"/> 只回收托管对象，
/// 对上面这些原生页完全无效。必须调 <c>EmptyWorkingSet</c> 把它们
/// 从进程工作集里剥出去交还系统。被换出的页在缺页时会自动重新读回来，
/// 不需要程序做任何事，代价只是下次用到时多几次缺页。
///
/// 调用时机必须选在「确定没有渲染活动」的时刻：
/// 正在拖动动画、展开面板时清工作集会让那些操作立即大量缺页，表现为卡顿。
/// 所以这里用来源集合跟踪谁在渲染，而不是让调用方自己判断。
/// </summary>
public static class WorkingSetTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 两次修剪之间的最小间隔。
    ///
    /// 界面收起后立刻修剪，可以把工作集从上百兆打到个位数。
    ///
    /// 但间隔太小时问题很明显：连续快速开合会反复丢弃刚读回来的页，
    /// 每次展开都要重新缺页；而且修剪本身要遍历整个进程的页表，
    /// 进程越大越慢。60 秒是「关掉界面后数字会变小」和
    /// 「再次打开仍然热」之间的折中。
    /// </summary>
    private const int MinimumIntervalMs = 60_000;

    /// <summary>
    /// 收起界面后延迟多久再修剪。
    ///
    /// 不用「收起就立刻修剪」：收起往往只是临时看一眼，几秒后又要打开。
    /// 立刻把页全交出去，再打开时就得重新读回来，
    /// 这正是「冷却之后第一次展开卡一下」的直接来源。
    ///
    /// 延迟期间任何界面重新打开都会取消这次修剪。
    /// </summary>
    private const int PostHideDelayMs = 45_000;

    private static readonly object Gate = new();
    private static readonly HashSet<string> ActiveSources = new(StringComparer.Ordinal);

    /// <summary>
    /// 各来源最后一次「有渲染活动」的时刻。
    ///
    /// 只靠 Begin/End 配对会有一个问题：用户把窗口开着但几小时不动，
    /// 那个来源就一直算活跃，工作集永远不回收。
    /// 而实际上窗口静止时 XAML 不重绘、D3D 也不出新帧，
    /// 那部分内存同样可以交还系统。
    ///
    /// 所以活跃状态带超时：超过 <see cref="ActivityTimeoutMs"/> 没有任何
    /// 渲染迹象，就当作不活跃。每次鼠标/键盘/窗口事件都会刷新时间戳。
    /// </summary>
    private static readonly Dictionary<string, long> LastActiveMs = new(StringComparer.Ordinal);

    /// <summary>
    /// 活跃状态的有效期。
    ///
    /// 短视频动画（200ms 滑入）之后如果用户不动，1 分钟足以判定「静止」。
    /// 设太长会让工作站一会儿就涨回去，设太短会在用户读界面的间隙频繁修剪。
    /// </summary>
    private const int ActivityTimeoutMs = 60_000;
    private static long _lastTrimMs = Environment.TickCount64;
    private static bool _hasTrimmed;

    /// <summary>
    /// 当前是否有界面正在渲染。
    ///
    /// 用来源集合而不是单个 bool：主窗口、迷你面板、托盘菜单会互相独立地
    /// 显示和隐藏，用一个 bool 时任意一个隐藏就会把标记清掉，
    /// 另一个还在渲染的窗口就会在下一帧被清掉工作集。
    ///
    /// 同时带超时：窗口开着但长时间不动也算「不活跃」，见 <see cref="LastActiveMs"/>。
    /// </summary>
    public static bool IsUiActive
    {
        get
        {
            lock (Gate) return ActiveSources.Count > 0;
        }
    }

    /// <summary>
    /// 是否有来源在「近期」有过渲染活动。
    ///
    /// 这是 <see cref="TryTrim"/> 实际使用的判定：窗口开着但静止超过
    /// <see cref="ActivityTimeoutMs"/> 时不算活跃，允许回收。
    /// </summary>
    private static bool HasRecentActivity()
    {
        if (ActiveSources.Count == 0) return false;

        long now = Environment.TickCount64;
        foreach (string source in ActiveSources)
        {
            if (!LastActiveMs.TryGetValue(source, out long stamp)) return true;
            if (now - stamp < ActivityTimeoutMs) return true;
        }

        return false;
    }

    /// <summary>标记某个界面开始渲染。</summary>
    public static void BeginActivity(string source)
    {
        lock (Gate)
        {
            ActiveSources.Add(source);
            LastActiveMs[source] = Environment.TickCount64;
        }

        // 界面重新打开，取消排队中的延迟修剪：马上又要渲染了，
        // 这时候把页交出去只会让首帧重新缺页。
        CancelScheduledTrim();
    }

    /// <summary>标记某个界面停止渲染。</summary>
    public static void EndActivity(string source)
    {
        lock (Gate)
        {
            ActiveSources.Remove(source);
            LastActiveMs.Remove(source);
        }
    }

    /// <summary>
    /// 报告某个界面刚有渲染活动（鼠标移动、按键、窗口事件等）。
    ///
    /// 只对已经登记的来源生效：未登记的来源本来就没在渲染，
    /// 不该因为一次指针事件就把它算进来。
    /// </summary>
    public static void Touch(string source)
    {
        lock (Gate)
        {
            if (ActiveSources.Contains(source)) LastActiveMs[source] = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 尝试修剪一次工作集。返回是否真的执行了。
    ///
    /// 只在没有界面渲染、且距上次修剪超过 <see cref="MinimumIntervalMs"/> 时才动手。
    ///
    /// <b>EmptyWorkingSet 不在锁内调用</b>：它要遍历整个进程的页表，
    /// 工作集大时能跑几十毫秒。持锁期间 UI 线程上的
    /// <see cref="BeginActivity"/>（用户点开面板的第一件事）会一起等锁，
    /// 表现就是「点了没反应，过一会儿才弹出来」。
    /// </summary>
    public static bool TryTrim(string reason, bool force = false)
    {
        lock (Gate)
        {
            if (!force)
            {
                if (HasRecentActivity()) return false;
                if (_hasTrimmed && Environment.TickCount64 - _lastTrimMs < MinimumIntervalMs) return false;
            }

            _hasTrimmed = true;
            _lastTrimMs = Environment.TickCount64;
        }

        var before = WorkingSetBytes();

        bool ok;
        try
        {
            ok = EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            return false;
        }

        // EmptyWorkingSet 在部分系统上会返回 false 但仍有效果，
        // 所以不把它当作失败处理。
        var after = WorkingSetBytes();
        Log($"工作集回收（{reason}）：{before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB ok={ok}");

        return ok;
    }

    private static readonly object ScheduleGate = new();
    private static Timer? _deferredTrim;

    /// <summary>
    /// 系统当前可用物理内存（字节）。取不到时返回 -1。
    /// </summary>
    private static long AvailableBytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return -1;

            return (long)status.ullAvailPhys;
        }
        catch
        {
            return -1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>
    /// 内存紧张时才值得回收。
    ///
    /// 这条判断是「冷却后首次展开卡顿」的根本解法。
    ///
    /// 之前不管系统有没有内存压力，只要界面一隐藏就把几十兆页交还系统。
    /// 交还的代价不是零：下次展开时那些代码页、D3D 资源、字形位图
    /// 都要重新从页文件读回来，几十兆软缺页正好压在 UI 线程上——
    /// 这就是「冷却久了点一下卡，之后再开合又很流畅」的全部原因。
    /// 物理内存明明充裕时这么做纯粹是自己制造卡顿。
    ///
    /// 阈值取 3 GB：低于这个量系统本身就开始感到压力，
    /// 这时候交还几十兆是有意义的。
    /// </summary>
    private static bool MemoryPressure()
    {
        var available = AvailableBytes();
        return available >= 0 && available < 3L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// 界面收起后安排一次延迟修剪。
    ///
    /// 为什么不立刻修剪：收起面板往往只是临时看一眼，
    /// 几秒后又要打开。立刻交还页会让下次展开重新缺页，
    /// 冷启动的卡顿就是这么产生的。延迟
    /// <see cref="PostHideDelayMs"/> 毫秒再动手，期间任何界面
    /// 重新打开都会取消；系统内存充裕时干脆不做。
    /// </summary>
    public static void ScheduleTrim(string reason)
    {
        if (!MemoryPressure()) return;

        lock (ScheduleGate)
        {
            _deferredTrim ??= new Timer(_ => RunDeferredTrim(reason), null,
                Timeout.Infinite, Timeout.Infinite);

            _deferredTrim.Change(PostHideDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>取消已排队的延迟修剪（界面重新打开时调用）。</summary>
    public static void CancelScheduledTrim()
    {
        lock (ScheduleGate)
        {
            _deferredTrim?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static void RunDeferredTrim(string reason)
    {
        try
        {
            TryTrim(reason);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 写一行日志到数据目录下的 app.log。
    ///
    /// 这里不引用 Momomi.App 的 WriteLog：Core 是被 App 引用的下层，
    /// 反向依赖会形成循环。内存回收属于低频事件（一分钟最多几次），
    /// 直接开流写一行足够，不必为它建一整套日志设施。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Momomi.Data.MomomiAppData.ResolveDirectory();
            string path = Path.Combine(dir, "app.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MEM  ] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响回收本身。
        }
    }

    /// <summary>
    /// 先把托管堆里的空闲页压紧，再交给系统回收。
    ///
    /// 单独调 EmptyWorkingSet 不会压缩托管堆：已经死掉的 gen2 对象
    /// 所占据的页仍然在进程地址空间里，会被重新读入工作集。
    /// 必须先跑一次真正的阻塞式回收，才能把这些页还出去。
    ///
    /// 代价是一次几十毫秒的停顿，所以只能在没有渲染活动时调用。
    /// </summary>
    public static void CompactAndTrim(string reason)
    {
        if (IsUiActive) return;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch
        {
        }

        TryTrim(reason);
    }

    /// <summary>
    /// 压缩托管堆，然后<b>排队</b>一次延迟修剪。
    ///
    /// 与 <see cref="CompactAndTrim"/> 的区别只在最后一步：不立刻交还页，
    /// 而是等 <see cref="PostHideDelayMs"/> 之后再看要不要交。
    ///
    /// 用户最小化主窗口往往马上又打开，立刻交还页会让下次唤回重新缺页。
    /// 延迟期间重新打开会自动取消；系统内存充裕时干脆不做。
    /// 托管堆压缩本身仍然立刻做——它只影响托管对象，代价是几十毫秒停顿，
    /// 且能把 <c>ReleasePageContent</c> 掉下的页面一起还回去。
    /// </summary>
    public static void CompactAndScheduleTrim(string reason)
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch
        {
        }

        ScheduleTrim(reason);
    }

    /// <summary>
    /// 启动低频看门狗：只在界面长时间不渲染时修剪。
    ///
    /// 不做高频轮询：工作集不会自己涨回去，涨回去说明有渲染活动，
    /// 那时候本来就不该修剪。一分钟看一次即可。
    /// </summary>
    public static IDisposable StartIdleWatchdog(TimeSpan interval)
    {
        var timer = new Timer(_ =>
        {
            try
            {
                // 没有内存压力就不要主动交还页：这里每一次回收，
                // 都会让用户下一次展开面板多一次缺页。
                if (!MemoryPressure()) return;

                TryTrim("idle");
            }
            catch
            {
            }
        }, null, interval, interval);

        return timer;
    }

    private static long WorkingSetBytes()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }
}
