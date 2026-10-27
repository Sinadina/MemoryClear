// ============================================================================
//  MemoryClear — 单文件内存分级清理工具
//  平台：Windows 10/11（x64 / AnyCPU），运行时依赖系统自带的 .NET Framework 4.x
//  编译：csc.exe /target:winexe /win32manifest:app.manifest /out:MemoryClear.exe MemoryClear.cs
//        引用：System.dll System.Drawing.dll System.Windows.Forms.dll
//
//  设计要点
//   1. 分级清理 L1~L4，全部基于 ntdll!NtSetSystemInformation(SystemMemoryListInformation)
//      与 SetProcessWorkingSetSizeEx，不用任何“清理软件”常见的伪释放手段。
//   2. 提权只弹一次：GUI 以 asInvoker 运行，首次真正清理时 runas 拉起常驻助手进程，
//      之后同一会话复用同一助手，不再弹 UAC；退出时助手被主进程主动结束，
//      助手自身还有父进程看门狗（主进程崩溃也会自杀）。
//   3. 进度是真实步骤进度（含逐进程裁剪的真实百分比），UI 只做平滑插值。
//   4. 内存快照读 standby / modified / free / commit 结构，不用“可用内存+X GB”当成绩。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MemoryClear
{
    // =====================================================================
    //  主题
    // =====================================================================
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(18, 20, 24);
        public static readonly Color Card = Color.FromArgb(28, 31, 38);
        public static readonly Color CardHi = Color.FromArgb(36, 40, 49);
        public static readonly Color Line = Color.FromArgb(46, 51, 62);
        public static readonly Color Text = Color.FromArgb(233, 236, 241);
        public static readonly Color Muted = Color.FromArgb(139, 147, 161);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color Standby = Color.FromArgb(124, 92, 255);
        public static readonly Color Warn = Color.FromArgb(245, 166, 35);
        public static readonly Color Danger = Color.FromArgb(255, 90, 95);
        public static readonly Color Ok = Color.FromArgb(53, 199, 126);
        public static readonly Color LogBg = Color.FromArgb(15, 17, 21);
    }

    internal static class Ui
    {
        public static float Scale = 1f;
        public static int Px(float v) { return (int)Math.Round(v * Scale); }
        public static Font F(float pt, FontStyle st) { return new Font("Microsoft YaHei UI", pt, st, GraphicsUnit.Point); }
        public static string Bytes(long b)
        {
            if (b < 0) b = 0;
            double v = b;
            if (v >= 1024.0 * 1024 * 1024) return (v / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (v >= 1024.0 * 1024) return (v / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (v / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // =====================================================================
    //  Win32 / NT
    // =====================================================================
    internal static class Native
    {
        public const int SystemMemoryListInformation = 80;

        public const int MemListEmptyWorkingSets = 2;
        public const int MemListFlushModifiedList = 3;
        public const int MemListPurgeStandbyList = 4;
        public const int MemListPurgeLowPriorityStandbyList = 5;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint SYNCHRONIZE = 0x00100000;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int SE_PRIVILEGE_ENABLED = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
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

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public IntPtr CommitTotal;
            public IntPtr CommitLimit;
            public IntPtr CommitPeak;
            public IntPtr PhysicalTotal;
            public IntPtr PhysicalAvailable;
            public IntPtr SystemCache;
            public IntPtr KernelTotal;
            public IntPtr KernelPaged;
            public IntPtr KernelNonpaged;
            public IntPtr PageSize;
            public IntPtr HandleCount;
            public IntPtr ProcessCount;
            public IntPtr ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcesses(int[] pProcessIds, int cb, out int pBytesReturned);

        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        /// <summary>闪任务栏图标（不抢焦点、不常驻）。FLASHW_TRAY|FLASHW_TIMERNOFG</summary>
        public static void FlashTaskbar(IntPtr hwnd)
        {
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = hwnd;
                fi.dwFlags = 0x00000002 | 0x0000000C;
                fi.uCount = 4;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        // ------------------------------------------------------------------
        public static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out token)) return false;
            try
            {
                long luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                return ok && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        /// <summary>执行一条 SYSTEM_MEMORY_LIST_COMMAND。需要 SeProfileSingleProcessPrivilege。</summary>
        public static void PurgeList(int command)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, command);
                int status = NtSetSystemInformation(SystemMemoryListInformation, buf, 4);
                if (status != 0)
                {
                    string hint = "";
                    if (status == unchecked((int)0xC0000022) || status == unchecked((int)0xC0000061))
                        hint = "（权限不足：该操作需要管理员权限）";
                    throw new InvalidOperationException("内存列表命令 " + command + " 失败，NTSTATUS=0x" +
                        ((uint)status).ToString("X8") + hint);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>查询内存列表分布（standby 各级 / modified / free / zero），单位字节。无需提权。</summary>
        public static bool QueryMemoryLists(long pageSize,
            out long standbyTotal, out long standbyLow, out long modified, out long free, out long zero)
        {
            standbyTotal = standbyLow = modified = free = zero = 0;
            int slots = 22;
            int size = slots * IntPtr.Size + 64;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i += 8) Marshal.WriteInt64(buf, i, 0);
                int ret;
                int status = NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out ret);
                if (status != 0) return false;

                Func<int, long> at = delegate(int idx) { return Marshal.ReadIntPtr(buf, idx * IntPtr.Size).ToInt64(); };

                zero = at(0) * pageSize;
                free = at(1) * pageSize;
                modified = at(2) * pageSize;
                long all = 0, low = 0;
                for (int i = 0; i < 8; i++)
                {
                    long v = at(5 + i) * pageSize;
                    all += v;
                    if (i >= 5) low += v;   // 优先级 5~7 视为低优先级待命缓存
                }
                standbyTotal = all;
                standbyLow = low;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static string ProcessNameSafe(IntPtr hProcess)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int len = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref len))
                    return Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
            }
            catch { }
            return "";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING_S
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        // 只定义到 WorkingSetSize 为止的前缀，足以读到进程名与工作集
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivateSize;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public UNICODE_STRING_S ImageName;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public ulong UniqueProcessKey;
            public ulong PeakVirtualSize;
            public ulong VirtualSize;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
        }

        /// <summary>
        /// 已压缩内存：取内核 "Memory Compression" 进程的工作集（任务管理器“使用中(已压缩)”的括号值）。
        /// 需要遍历 SystemProcessInformation，找不到就返回 0。
        /// </summary>
        public static long GetCompressedBytes(out string procName)
        {
            procName = null;
            int size = 1024 * 1024;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int ret;
                    int st = NtQuerySystemInformation(5 /*SystemProcessInformation*/, buf, size, out ret);
                    if (st == unchecked((int)0xC0000004))     // STATUS_INFO_LENGTH_MISMATCH
                    {
                        Marshal.FreeHGlobal(buf);
                        size = Math.Max(size * 2, ret + 65536);
                        continue;
                    }
                    if (st != 0) { Marshal.FreeHGlobal(buf); return 0; }

                    int off = 0;
                    while (true)
                    {
                        SYSTEM_PROCESS_INFORMATION sp = (SYSTEM_PROCESS_INFORMATION)
                            Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + off), typeof(SYSTEM_PROCESS_INFORMATION));
                        if (sp.ImageName.Length > 0 && sp.ImageName.Buffer != IntPtr.Zero)
                        {
                            string name = Marshal.PtrToStringUni(sp.ImageName.Buffer, sp.ImageName.Length / 2);
                            if (string.Equals(name, "Memory Compression", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(name, "MemCompression", StringComparison.OrdinalIgnoreCase))
                            {
                                procName = name;
                                return (long)sp.WorkingSetSize;
                            }
                        }
                        if (sp.NextEntryOffset == 0) break;
                        off += (int)sp.NextEntryOffset;
                        if (off <= 0 || off >= size) break;
                    }
                    Marshal.FreeHGlobal(buf);
                    return 0;
                }
                catch { try { Marshal.FreeHGlobal(buf); } catch { } return 0; }
            }
            return 0;
        }

        /// <summary>用 EnumProcesses 精确统计进程数（PSAPI 的 ProcessCount 在 Win11 上不可靠）</summary>
        public static long CountProcesses()
        {
            try
            {
                int[] pids = new int[16384];
                int bytes;
                if (!EnumProcesses(pids, pids.Length * 4, out bytes)) return 0;
                return bytes / 4;
            }
            catch { return 0; }
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // =====================================================================
    //  快照
    // =====================================================================
    internal sealed class MemSnapshot
    {
        public long TotalPhys;
        public long AvailPhys;
        public long CommitTotal;
        public long CommitLimit;
        public long StandbyTotal;
        public long StandbyLow;
        public long Modified;
        public long FreePages;
        public long ZeroPages;
        public long ProcessCount;
        public long PagedPool;
        public long NonPagedPool;
        public long Compressed;
        public bool HasLists;

        public double AvailRatio { get { return TotalPhys > 0 ? AvailPhys / (double)TotalPhys : 1.0; } }
        public double CommitRatio { get { return CommitLimit > 0 ? CommitTotal / (double)CommitLimit : 0.0; } }
        public long UsedPhys { get { return Math.Max(0, TotalPhys - AvailPhys); } }
        /// <summary>已缓存 = 待命缓存 + 已修改（可直接回收的那部分）</summary>
        public long CachedBytes { get { return StandbyTotal + Modified; } }

        /// <summary>物理内存里“空闲”部分（不含待命缓存）</summary>
        public long IdlePhys
        {
            get
            {
                long v = AvailPhys - StandbyTotal;
                return v < 0 ? 0 : v;
            }
        }

        public static MemSnapshot Read()
        {
            MemSnapshot s = new MemSnapshot();
            try
            {
                Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    s.TotalPhys = (long)m.ullTotalPhys;
                    s.AvailPhys = (long)m.ullAvailPhys;
                }
            }
            catch { }

            long pageSize = 4096;
            try
            {
                Native.PERFORMANCE_INFORMATION pi;
                if (Native.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION))))
                {
                    pageSize = pi.PageSize.ToInt64();
                    if (pageSize <= 0) pageSize = 4096;
                    s.CommitTotal = pi.CommitTotal.ToInt64() * pageSize;
                    s.CommitLimit = pi.CommitLimit.ToInt64() * pageSize;
                    s.PagedPool = pi.KernelPaged.ToInt64() * pageSize;
                    s.NonPagedPool = pi.KernelNonpaged.ToInt64() * pageSize;
                    // 注意：PSAPI 的 ProcessCount 在部分 Win11 版本上不可靠，进程数单独用 EnumProcesses 统计
                    s.ProcessCount = Native.CountProcesses();
                }
            }
            catch { }

            string compName;
            s.Compressed = Native.GetCompressedBytes(out compName);

            long st, sl, md, fr, zr;
            if (Native.QueryMemoryLists(pageSize, out st, out sl, out md, out fr, out zr))
            {
                s.StandbyTotal = st;
                s.StandbyLow = sl;
                s.Modified = md;
                s.FreePages = fr;
                s.ZeroPages = zr;
                s.HasLists = true;
            }
            return s;
        }

        public string Describe()
        {
            return string.Format("可用 {0} / 总计 {1}，待命缓存 {2}（低优先级 {3}），已修改 {4}，提交 {5} / {6}",
                Ui.Bytes(AvailPhys), Ui.Bytes(TotalPhys), Ui.Bytes(StandbyTotal), Ui.Bytes(StandbyLow),
                Ui.Bytes(Modified), Ui.Bytes(CommitTotal), Ui.Bytes(CommitLimit));
        }
    }

    // =====================================================================
    //  策略：推荐级别 + 告警判定
    // =====================================================================
    internal enum Severity { Ok = 0, Warn = 1, Critical = 2 }

    internal static class Policy
    {
        private const long MB = 1024L * 1024L;

        public static string LevelName(int level)
        {
            switch (level)
            {
                case 1: return "L1 轻度";
                case 2: return "L2 标准";
                case 3: return "L3 深度";
                case 4: return "L4 极限";
                case 5: return "L5 全系统";
                default: return "L?" + level;
            }
        }

        /// <summary>按当前内存状况挑默认分级</summary>
        public static int Recommend(MemSnapshot s, out string reason)
        {
            double av = s.AvailRatio;
            double cm = s.CommitRatio;
            long standby = s.StandbyTotal;
            long modified = s.Modified;

            if (s.HasLists && standby < 1024 * MB && av > 0.25)
            {
                reason = "待命缓存不多（" + Ui.Bytes(standby) + "），轻度清理即可";
                return 1;
            }
            if (cm > 0.90)
            {
                reason = string.Format("提交率已达 {0:0}%，清理缓存无法降低提交量，建议同时排查占用进程", cm * 100);
                return av < 0.12 ? 4 : 2;
            }
            if (av >= 0.45)
            {
                // 内存充足但缓存攒了不少：标准清理能把备用缓存真正变成可用，效果直观
                if (s.HasLists && standby >= 1024 * MB)
                {
                    reason = string.Format("内存充足，但有 {0} 待命缓存可回收，标准清理即可", Ui.Bytes(standby));
                    return 2;
                }
                reason = string.Format("可用内存充足（{0}），轻度清理即可", Ui.Bytes(s.AvailPhys));
                return 1;
            }
            if (av >= 0.28)
            {
                if (modified > 512 * MB) { reason = "可用内存偏紧且有较多已修改页，先刷盘再清缓存"; return 3; }
                reason = string.Format("可用内存偏紧（{0}），标准清理可腾出待命缓存", Ui.Bytes(s.AvailPhys));
                return 2;
            }
            if (av >= 0.14)
            {
                if (modified > 512 * MB) { reason = "内存紧张，且已修改页较多，建议深度清理"; return 3; }
                reason = string.Format("内存紧张（可用 {0}），标准清理优先", Ui.Bytes(s.AvailPhys));
                return 2;
            }
            reason = string.Format("内存极度紧张（可用仅 {0}），建议极限清理（逐进程裁剪）", Ui.Bytes(s.AvailPhys));
            return 4;
        }

        public static Severity Judge(MemSnapshot s, out string detail)
        {
            double av = s.AvailRatio;
            double cm = s.CommitRatio;
            if (av < 0.10 || cm > 0.95)
            {
                detail = string.Format("可用 {0}（{1:0}%），提交率 {2:0}%", Ui.Bytes(s.AvailPhys), av * 100, cm * 100);
                return Severity.Critical;
            }
            if (av < 0.18 || cm > 0.88)
            {
                detail = string.Format("可用 {0}（{1:0}%），提交率 {2:0}%", Ui.Bytes(s.AvailPhys), av * 100, cm * 100);
                return Severity.Warn;
            }
            // “可用内存”里绝大部分只是可回收的待命缓存时，一次清理确实能帮上忙
            if (s.HasLists && s.AvailPhys > 0 && av < 0.35
                && s.StandbyTotal * 100 / Math.Max(1, s.AvailPhys) >= 60)
            {
                detail = string.Format("可用 {0} 中有 {1} 是可回收缓存（占 {2:0}%），清理收益明显",
                    Ui.Bytes(s.AvailPhys), Ui.Bytes(s.StandbyTotal),
                    s.StandbyTotal * 100.0 / Math.Max(1, s.AvailPhys));
                return Severity.Warn;
            }
            detail = string.Format("可用 {0} / {1}，待命缓存 {2}", Ui.Bytes(s.AvailPhys), Ui.Bytes(s.TotalPhys), Ui.Bytes(s.StandbyTotal));
            return Severity.Ok;
        }
    }

    // =====================================================================
    //  清理执行
    // =====================================================================
    internal static class CleanEngine
    {
        private static readonly string[] Protected =
        {
            "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "svchost", "fontdrvhost", "audiodg", "wudfhost",
            "msmpeng", "nissrv", "mpcmdrun", "securityhealthservice", "securityhealthsystray",
            "hipsdaemon", "hipstray", "usysdiag", "wsctrl", "memoryclear"
        };

        public static bool IsProtectedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < Protected.Length; i++) if (Protected[i] == name) return true;
            return false;
        }

        public static void Run(int level, Action<double, string> report, Func<bool> cancel, bool selfOnly)
        {
            bool pSp = Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
            report(4, pSp ? "已启用内存配置特权" : "特权启用失败（可能需要管理员权限）");

            switch (level)
            {
                case 1:
                    report(25, "正在丢弃低优先级待命缓存…");
                    Native.PurgeList(Native.MemListPurgeLowPriorityStandbyList);
                    report(95, "低优先级待命缓存已丢弃");
                    break;

                case 2:
                    report(25, "正在丢弃待命（文件）缓存…");
                    Native.PurgeList(Native.MemListPurgeStandbyList);
                    report(95, "待命缓存已丢弃");
                    break;

                case 3:
                    report(12, "正在把已修改页刷写到磁盘…");
                    Native.PurgeList(Native.MemListFlushModifiedList);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    report(55, "正在丢弃待命（文件）缓存…");
                    Native.PurgeList(Native.MemListPurgeStandbyList);
                    report(95, "已修改页与待命缓存已清理");
                    break;

                case 4:
                    TrimProcesses(report, cancel, 6, 66, selfOnly);
                    // 裁剪只是把工作集页推进待命/已修改列表；必须再把它们真正回收掉，
                    // 否则会出现“清理完缓存反而变大”的反直觉结果。
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    report(72, "正在把已修改页刷写到磁盘…");
                    Native.PurgeList(Native.MemListFlushModifiedList);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    report(84, "正在丢弃待命（文件）缓存…");
                    Native.PurgeList(Native.MemListPurgeStandbyList);
                    report(94, "工作集已裁剪，待命/脏页已回收");
                    break;

                case 5:
                    // 内核级、无差别地清空所有进程的工作集，再刷脏页、丢待命缓存。
                    // 不需要逐进程提权，连系统进程、其它会话、杀毒软件的内存也会被压回可用。
                    report(8, "正在清空全系统工作集（所有进程，含系统进程）…");
                    Native.PurgeList(Native.MemListEmptyWorkingSets);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    report(45, "正在把已修改页刷写到磁盘…");
                    Native.PurgeList(Native.MemListFlushModifiedList);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    report(72, "正在丢弃待命（文件）缓存…");
                    Native.PurgeList(Native.MemListPurgeStandbyList);
                    report(95, "全系统工作集 / 已修改页 / 待命缓存已全部转为可用");
                    break;

                default:
                    throw new ArgumentException("未知清理级别：" + level);
            }
            report(100, "指令执行完毕");
        }

        private static void TrimProcesses(Action<double, string> report, Func<bool> cancel,
            double from, double to, bool selfOnly)
        {
            int me = Process.GetCurrentProcess().Id;
            int trimmed = 0, skipped = 0, failed = 0, seen = 0;

            if (selfOnly)
            {
                report(from, "（自测模式）仅裁剪本进程工作集");
                using (Process self = Process.GetCurrentProcess())
                {
                    IntPtr hs = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                        false, self.Id);
                    if (hs != IntPtr.Zero)
                    {
                        try { if (Native.SetProcessWorkingSetSizeEx(hs, (IntPtr)(-1), (IntPtr)(-1), 0)) trimmed++; else failed++; }
                        finally { Native.CloseHandle(hs); }
                    }
                    else failed++;
                }
                report(to, string.Format("已裁剪 {0} 个进程，失败 {1} 个", trimmed, failed));
                return;
            }

            int[] pids = new int[16384];
            int bytes;
            if (!Native.EnumProcesses(pids, pids.Length * 4, out bytes))
                throw new InvalidOperationException("无法枚举进程（EnumProcesses 失败，" + Marshal.GetLastWin32Error() + "）");
            int n = bytes / 4;
            if (n <= 0) throw new InvalidOperationException("进程列表为空");

            for (int i = 0; i < n; i++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                int pid = pids[i];
                if (pid <= 4 || pid == me) { skipped++; continue; }

                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) { skipped++; }
                else
                {
                    try
                    {
                        string name = Native.ProcessNameSafe(h);
                        if (IsProtectedName(name)) { skipped++; }
                        else if (Native.SetProcessWorkingSetSizeEx(h, (IntPtr)(-1), (IntPtr)(-1), 0)) { trimmed++; }
                        else { failed++; }
                    }
                    finally { Native.CloseHandle(h); }
                }

                double pct = from + (to - from) * (i + 1) / (double)n;
                if (seen == 0 || (i & 7) == 0 || i == n - 1)
                    report(pct, string.Format("裁剪工作集 {0}/{1}，已裁剪 {2}", i + 1, n, trimmed));
                seen++;
            }
            report(to, string.Format("已裁剪 {0} 个进程，跳过 {1} 个（系统/白名单/无权限），失败 {2} 个",
                trimmed, skipped, failed));
        }
    }

    // =====================================================================
    //  提权助手（父进程侧）
    // =====================================================================
    internal sealed class CleanHelper : IDisposable
    {
        /// <summary>本程序创建过的所有子进程 PID，退出时逐个确认清理</summary>
        public static readonly List<int> SpawnedPids = new List<int>();

        private Process _proc;
        private NamedPipeServerStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _pipeName;

        public int ProcessId { get { return _proc != null ? _proc.Id : 0; } }
        public bool IsAlive
        {
            get
            {
                try { return _proc != null && !_proc.HasExited && _pipe != null && _pipe.IsConnected; }
                catch { return false; }
            }
        }

        /// <summary>拉起（或复用）提权助手。仅在没有助手时才会触发一次 UAC。</summary>
        public void EnsureStarted()
        {
            if (IsAlive) return;
            Dispose();

            _pipeName = "MemoryClear_" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            psi.Arguments = "--helper \"" + _pipeName + "\" " + Process.GetCurrentProcess().Id;
            psi.UseShellExecute = true;
            // 测试钩子：MEMORYCLEAR_NO_ELEVATE=1 时不提权直接起助手，便于无人值守验证管道协议
            psi.Verb = Environment.GetEnvironmentVariable("MEMORYCLEAR_NO_ELEVATE") == "1" ? null : "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            try
            {
                _proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Dispose();
                if (ex.NativeErrorCode == 1223)
                    throw new InvalidOperationException("已取消管理员授权，无法执行清理。\r\n可以右键本程序 → 以管理员身份运行，避免每次询问。");
                throw new InvalidOperationException("无法启动提权组件：" + ex.Message);
            }
            if (_proc == null) { Dispose(); throw new InvalidOperationException("提权组件启动失败"); }
            lock (SpawnedPids) { if (!SpawnedPids.Contains(_proc.Id)) SpawnedPids.Add(_proc.Id); }

            IAsyncResult wait = _pipe.BeginWaitForConnection(null, null);
            if (!wait.AsyncWaitHandle.WaitOne(120000))
            {
                Dispose();
                throw new InvalidOperationException("提权组件未在 120 秒内连接（可能被安全软件拦截，或未在 UAC 窗口点“是”）");
            }
            _pipe.EndWaitForConnection(wait);
            _reader = new StreamReader(_pipe, new UTF8Encoding(false));
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false));
            _writer.AutoFlush = true;
            string hello = _reader.ReadLine();
            if (hello != "READY")
            {
                Dispose();
                throw new InvalidOperationException("提权组件握手失败：" + (hello == null ? "连接中断" : hello));
            }
        }

        /// <summary>让助手执行一次清理，进度实时回传</summary>
        public void Clean(int level, Action<double, string> progress)
        {
            if (!IsAlive) EnsureStarted();
            _writer.WriteLine("CLEAN " + level.ToString(CultureInfo.InvariantCulture));
            while (true)
            {
                string line;
                try { line = _reader.ReadLine(); }
                catch (IOException) { line = null; }
                if (line == null)
                {
                    Dispose();
                    throw new InvalidOperationException("提权组件意外退出（可能被结束或被安全软件拦截）");
                }
                if (line.StartsWith("P|"))
                {
                    string[] parts = line.Split(new char[] { '|' }, 3);
                    double pct;
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pct);
                    progress(pct, parts.Length > 2 ? parts[2] : "");
                }
                else if (line == "OK") return;
                else if (line.StartsWith("ERR|")) throw new InvalidOperationException(line.Substring(4));
            }
        }

        public void Dispose()
        {
            try { if (_writer != null) _writer.WriteLine("QUIT"); } catch { }
            StreamWriter w = _writer; _writer = null;
            if (w != null) { try { w.Flush(); } catch { } }
            if (_proc != null)
            {
                try { if (!_proc.HasExited) _proc.WaitForExit(1500); } catch { }
                try { if (!_proc.HasExited) _proc.Kill(); } catch { }
                try { _proc.Dispose(); } catch { }
                _proc = null;
            }
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            try { if (_reader != null) _reader.Dispose(); } catch { }
            _pipe = null; _reader = null;
        }
    }

    // =====================================================================
    //  提权助手（子进程侧）
    // =====================================================================
    internal static class HelperMode
    {
        public static void Run(string pipeName, int parentPid)
        {
            StartWatchdog(parentPid);
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName,
                    PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(90000);
                    UTF8Encoding enc = new UTF8Encoding(false);
                    using (StreamReader rd = new StreamReader(pipe, enc))
                    using (StreamWriter wr = new StreamWriter(pipe, enc))
                    {
                        wr.AutoFlush = true;
                        wr.WriteLine("READY");
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line == "QUIT") break;
                            if (!line.StartsWith("CLEAN")) continue;
                            int level;
                            if (!int.TryParse(line.Substring(5).Trim(), out level)) { wr.WriteLine("ERR|参数错误"); continue; }
                            try
                            {
                                DateTime last = DateTime.MinValue;
                                CleanEngine.Run(level, delegate(double pct, string msg)
                                {
                                    // 限流：百分比变化 >=1 或消息变化才回传，避免刷爆管道
                                    if (DateTime.Now - last < TimeSpan.FromMilliseconds(40) && pct < 100) return;
                                    last = DateTime.Now;
                                    wr.WriteLine("P|" + ((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture) + "|" + OneLine(msg));
                                }, null, false);
                                wr.WriteLine("OK");
                            }
                            catch (Exception ex)
                            {
                                wr.WriteLine("ERR|" + OneLine(ex.Message));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "MemoryClear_helper.log"),
                        DateTime.Now.ToString("s") + " helper error: " + ex + Environment.NewLine);
                }
                catch { }
            }
            Environment.Exit(0);
        }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private static void StartWatchdog(int parentPid)
        {
            if (parentPid <= 0) return;
            Thread t = new Thread(delegate()
            {
                try
                {
                    IntPtr h = Native.OpenProcess(Native.SYNCHRONIZE, false, parentPid);
                    if (h == IntPtr.Zero) { Environment.Exit(0); return; }
                    Native.WaitForSingleObject(h, 0xFFFFFFFF);   // 父进程一退出就跟着退
                    Native.CloseHandle(h);
                }
                catch { }
                Environment.Exit(0);
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    // =====================================================================
    //  自绘控件
    // =====================================================================
    internal sealed class WarningStrip : Control
    {
        private Severity _state = Severity.Ok;
        private string _title = "";
        private string _detail = "";
        private Font _fTitle = Ui.F(10.5f, FontStyle.Bold);
        private Font _fDetail = Ui.F(8.8f, FontStyle.Regular);
        public event EventHandler Clicked;

        public WarningStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
        }

        public void SetState(Severity s, string title, string detail)
        {
            _state = s; _title = title; _detail = detail;
            Cursor = s == Severity.Ok ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_state != Severity.Ok && Clicked != null) Clicked(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = _state == Severity.Ok ? Theme.Ok : (_state == Severity.Warn ? Theme.Warn : Theme.Danger);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.Px(10);
            using (GraphicsPath p = Ui.Round(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(30, c))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(90, c), 1f)) g.DrawPath(pen, p);
            }
            int iconD = Ui.Px(22);
            int ix = Ui.Px(16);
            int iy = (Height - iconD) / 2;
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, ix, iy, iconD, iconD);
            string glyph = _state == Severity.Ok ? "✓" : (_state == Severity.Warn ? "!" : "×");
            using (Font gf = Ui.F(10.5f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.Bg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, gf, b, new RectangleF(ix, iy - Ui.Px(1), iconD, iconD), sf);
            }
            int tx = ix + iconD + Ui.Px(12);
            int rightReserve = _state == Severity.Ok ? 0 : Ui.Px(150);
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString(_title, _fTitle, b, tx, Ui.Px(8));
            using (SolidBrush b = new SolidBrush(Theme.Muted))
                g.DrawString(_detail, _fDetail, b, tx, Ui.Px(8) + _fTitle.Height + Ui.Px(2));
            if (_state != Severity.Ok)
            {
                string hint = "点击立即清理 ›";
                using (Font hf = Ui.F(9f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(c))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(hint, hf, b, new RectangleF(Width - rightReserve, 0, rightReserve - Ui.Px(14), Height), sf);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (_fTitle != null) _fTitle.Dispose(); if (_fDetail != null) _fDetail.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class UiProgress : Control
    {
        private double _value;

        public UiProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public double Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Height / 2;
            using (GraphicsPath p = Ui.Round(r, rad))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(36, 40, 49)))
                g.FillPath(b, p);
            int w = (int)Math.Round((Width - 1) * _value / 100.0);
            if (w > 2)
            {
                Rectangle rf = new Rectangle(0, 0, w, Height - 1);
                using (GraphicsPath p = Ui.Round(rf, rad))
                using (LinearGradientBrush b = new LinearGradientBrush(rf, Theme.Accent, Color.FromArgb(122, 173, 255), 0f))
                    g.FillPath(b, p);
            }
        }
    }

    // =====================================================================
    //  任务管理器风格的内存显示：60 秒使用量曲线 / 内存组合 / 数据网格
    // =====================================================================
    internal sealed class MemComposition : Control
    {
        private MemSnapshot _s;
        private readonly Font _fName = Ui.F(8.4f, FontStyle.Regular);
        private readonly Font _fVal = Ui.F(9.2f, FontStyle.Bold);
        private readonly Font _fTitle = Ui.F(9f, FontStyle.Regular);

        public MemComposition()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetSnapshot(MemSnapshot s) { _s = s; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush tb = new SolidBrush(Theme.Muted))
                g.DrawString("内存组合", _fTitle, tb, 0, 0);

            int barH = Ui.Px(24);
            int barY = Ui.Px(20);
            Rectangle bar = new Rectangle(0, barY, Width - 1, barH);
            using (GraphicsPath p = Ui.Round(bar, Ui.Px(4)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(36, 40, 49))) g.FillPath(b, p);

            long[] vals = new long[4];
            Color[] cols = { Theme.Accent, Theme.Warn, Theme.Standby, Color.FromArgb(58, 64, 78) };
            string[] names = { "使用中", "已修改", "待命", "空闲" };
            if (_s != null && _s.TotalPhys > 0)
            {
                long total = _s.TotalPhys;
                long used = Math.Min(total, _s.UsedPhys);
                long standby = _s.HasLists ? Math.Min(total - used, _s.StandbyTotal) : 0;
                long modified = _s.HasLists ? Math.Min(total - used - standby, _s.Modified) : 0;
                long idle = Math.Max(0, total - used - standby - modified);
                vals[0] = used; vals[1] = modified; vals[2] = standby; vals[3] = idle;

                using (GraphicsPath clip = Ui.Round(bar, Ui.Px(4)))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(clip);
                    double x = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (vals[i] <= 0) continue;
                        double f = vals[i] / (double)total;
                        int px = (int)Math.Round(bar.Width * f);
                        if (px < 1) px = 1;
                        using (SolidBrush b = new SolidBrush(cols[i]))
                            g.FillRectangle(b, (int)Math.Round(x), barY, px, barH);
                        x += bar.Width * f;
                    }
                    g.Restore(st);
                }
            }
            using (Pen pen = new Pen(Theme.Line, 1f))
                using (GraphicsPath bp = Ui.Round(bar, Ui.Px(4))) g.DrawPath(pen, bp);

            // 单行图例：色块 + 名称 + 数值，四列均分
            int colW = Width / 4;
            int ly = barY + barH + Ui.Px(7);
            for (int i = 0; i < 4; i++)
            {
                int x = i * colW;
                using (SolidBrush b = new SolidBrush(cols[i]))
                    g.FillRectangle(b, x, ly + Ui.Px(4), Ui.Px(9), Ui.Px(9));
                using (SolidBrush b = new SolidBrush(Theme.Muted))
                    g.DrawString(names[i], _fName, b, x + Ui.Px(14), ly);
                using (SolidBrush b = new SolidBrush(Theme.Text))
                    g.DrawString(Ui.Bytes(vals[i]), _fVal, b, x + Ui.Px(14) + Ui.Px(50), ly);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fName.Dispose(); _fVal.Dispose(); _fTitle.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class StatGrid : Control
    {
        private MemSnapshot _s;
        private readonly Font _fL = Ui.F(8f, FontStyle.Regular);
        private readonly Font _fV = Ui.F(9.8f, FontStyle.Bold);

        public StatGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetSnapshot(MemSnapshot s) { _s = s; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_s == null) return;
            Graphics g = e.Graphics;
            string[] names = { "使用中（已压缩）", "可用", "已提交", "已缓存", "分页缓冲池", "非分页缓冲池" };
            string[] vals =
            {
                Ui.Bytes(_s.UsedPhys) + (_s.Compressed > 0 ? "（" + Ui.Bytes(_s.Compressed) + "）" : ""),
                Ui.Bytes(_s.AvailPhys),
                Ui.Bytes(_s.CommitTotal) + " / " + Ui.Bytes(_s.CommitLimit),
                Ui.Bytes(_s.CachedBytes),
                Ui.Bytes(_s.PagedPool),
                Ui.Bytes(_s.NonPagedPool)
            };
            int rows = 2, cols = 3;
            int cw = Width / cols, ch = Height / rows;
            for (int i = 0; i < names.Length; i++)
            {
                int cx = (i % cols) * cw;
                int cy = (i / cols) * ch;
                using (SolidBrush b = new SolidBrush(Theme.Muted))
                    g.DrawString(names[i], _fL, b, cx, cy);
                using (SolidBrush b = new SolidBrush(Theme.Text))
                    g.DrawString(vals[i], _fV, b, cx, cy + Ui.Px(15));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fL.Dispose(); _fV.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // =====================================================================
    //  硬件信息（WMI，后台读取一次）
    // =====================================================================
    internal static class HwInfoReader
    {
        public static string Read(long osTotalBytes)
        {
            try
            {
                double cap = 0;
                int modules = 0, slotsTotal = 0;
                uint speed = 0;
                uint smbios = 0;
                using (System.Management.ManagementObjectSearcher se = new System.Management.ManagementObjectSearcher(
                    "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                {
                    foreach (System.Management.ManagementBaseObject mo in se.Get())
                    {
                        cap += Convert.ToDouble(mo["Capacity"] ?? 0);
                        modules++;
                        try
                        {
                            uint cs = mo["ConfiguredClockSpeed"] == null ? 0 : Convert.ToUInt32(mo["ConfiguredClockSpeed"]);
                            uint sp = mo["Speed"] == null ? 0 : Convert.ToUInt32(mo["Speed"]);
                            if (cs > speed) speed = cs;
                            if (sp > speed) speed = sp;
                            if (smbios == 0 && mo["SMBIOSMemoryType"] != null)
                                smbios = Convert.ToUInt32(mo["SMBIOSMemoryType"]);
                        }
                        catch { }
                    }
                }
                try
                {
                    using (System.Management.ManagementObjectSearcher se = new System.Management.ManagementObjectSearcher(
                        "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                        foreach (System.Management.ManagementBaseObject mo in se.Get())
                            slotsTotal += Convert.ToInt32(mo["MemoryDevices"] ?? 0);
                }
                catch { }
                if (slotsTotal < modules) slotsTotal = modules;

                string type = "";
                switch (smbios)
                {
                    case 20: type = "DDR"; break;
                    case 21: type = "DDR2"; break;
                    case 24: type = "DDR3"; break;
                    case 26: type = "DDR4"; break;
                    case 34: type = "DDR5"; break;
                    case 35: type = "LPDDR5"; break;
                    default: type = ""; break;
                }

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                if (cap > 0) sb.Append(Ui.Bytes((long)cap));
                if (type.Length > 0) sb.Append("  " + type);
                if (speed > 0) sb.Append("  ·  " + speed + " MT/s");
                if (slotsTotal > 0) sb.Append("  ·  " + modules + "/" + slotsTotal + " 插槽");
                long reserved = (long)cap - osTotalBytes;
                if (cap > 0 && reserved > 8 * 1024 * 1024) sb.Append("  ·  硬件保留 " + Ui.Bytes(reserved));
                return sb.ToString();
            }
            catch { return ""; }
        }
    }

    internal sealed class LevelCard : Control
    {
        private bool _hover;
        private bool _selected;
        private bool _recommended;
        public int Index;
        public string Title = "";
        public string Desc = "";
        private Font _fTitle = Ui.F(10f, FontStyle.Bold);
        private Font _fDesc = Ui.F(8.6f, FontStyle.Regular);
        private Font _fBadge = Ui.F(8f, FontStyle.Bold);

        public LevelCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        public bool Recommended
        {
            get { return _recommended; }
            set { if (_recommended != value) { _recommended = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.Px(10);
            using (GraphicsPath p = Ui.Round(r, rad))
            {
                Color fill = _selected ? Theme.CardHi : (_hover ? Color.FromArgb(32, 36, 44) : Theme.Card);
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                Color bc = _selected ? Theme.Accent : Theme.Line;
                using (Pen pen = new Pen(bc, _selected ? 1.6f : 1f)) g.DrawPath(pen, p);
            }

            // 单选圆点
            int cx = Ui.Px(20), cy = Height / 2, d = Ui.Px(16);
            using (Pen pen = new Pen(_selected ? Theme.Accent : Theme.Line, 1.6f))
                g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
            if (_selected)
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, cx - d / 2 + Ui.Px(4), cy - d / 2 + Ui.Px(4), d - Ui.Px(8), d - Ui.Px(8));

            int tx = cx + d / 2 + Ui.Px(12);
            int badgeW = 0;
            if (_recommended)
            {
                string badge = "推荐";
                SizeF bs = g.MeasureString(badge, _fBadge);
                badgeW = (int)bs.Width + Ui.Px(14);
                Rectangle br2 = new Rectangle(Width - Ui.Px(14) - badgeW, cy - Ui.Px(11), badgeW, Ui.Px(22));
                using (GraphicsPath bp = Ui.Round(br2, Ui.Px(6)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, Theme.Accent)))
                    g.FillPath(b, bp);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(badge, _fBadge, b, br2, sf);
                }
            }

            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString(Title, _fTitle, b, tx, Ui.Px(11));
            int descW = Width - tx - Ui.Px(16) - (badgeW > 0 ? badgeW + Ui.Px(10) : 0);
            using (SolidBrush b = new SolidBrush(Theme.Muted))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(Desc, _fDesc, b, new RectangleF(tx, Ui.Px(11) + _fTitle.Height + Ui.Px(1), descW, _fDesc.Height + Ui.Px(4)), sf);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fTitle.Dispose(); _fDesc.Dispose(); _fBadge.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // =====================================================================
    //  主窗体
    // =====================================================================
    internal sealed class MainForm : Form
    {
        private MemSnapshot _snap;
        private int _recLevel = 2;
        private int _selLevel = 2;
        private string _recReason = "";
        private bool _userPicked;
        private Severity _severity = Severity.Ok;
        private DateTime _lastBalloon = DateTime.MinValue;

        private WarningStrip _strip;
        private Label _lblTitle, _lblSub, _lblHw, _lblLevels, _lblProg, _lblPct, _lblResult;
        private MemComposition _comp;
        private StatGrid _stats;
        private UiProgress _prog;
        private LevelCard[] _cards = new LevelCard[5];
        private Button _btnStart, _btnCancel, _btnExit, _btnRefresh;
        private TextBox _log;
        private System.Windows.Forms.Timer _monitor, _anim;
        private CleanHelper _helper;
        private Thread _worker;
        private volatile bool _cancel;
        private double _targetPct, _shownPct;
        private bool _busy;
        private MemSnapshot _pendingBefore;

        public MainForm()
        {
            Text = "MemoryClear · 内存分级清理";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            using (Graphics g = CreateGraphics()) Ui.Scale = g.DpiX / 96f;

            // 尺寸自适应：先按设计尺寸设置，再确保整个外框落在工作区内（不被任务栏遮挡）
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int cw = Ui.Px(960), ch = Ui.Px(884);
            ClientSize = new Size(Math.Min(cw, wa.Width - Ui.Px(24)), Math.Min(ch, wa.Height - Ui.Px(24)));
            if (Height > wa.Height - Ui.Px(8))
                ClientSize = new Size(ClientSize.Width, ClientSize.Height - (Height - (wa.Height - Ui.Px(8))));
            if (Width > wa.Width - Ui.Px(8))
                ClientSize = new Size(ClientSize.Width - (Width - (wa.Width - Ui.Px(8))), ClientSize.Height);
            MinimumSize = new Size(Ui.Px(880), Ui.Px(620));

            StartPosition = FormStartPosition.Manual;
            int lx = wa.Left + (wa.Width - Width) / 2;
            int ly = wa.Top + (wa.Height - Height) / 2;
            if (ly < wa.Top) ly = wa.Top;
            if (ly + Height > wa.Bottom) ly = wa.Bottom - Height;
            if (lx < wa.Left) lx = wa.Left;
            Location = new Point(lx, ly);

            BuildUi();
            LayoutAll();

            _snap = MemSnapshot.Read();
            RefreshSnapshot(true);

            _monitor = new System.Windows.Forms.Timer();
            _monitor.Interval = 1000;   // 曲线 60 点 = 60 秒
            _monitor.Tick += new EventHandler(delegate { RefreshSnapshot(false); });
            _monitor.Start();

            _anim = new System.Windows.Forms.Timer();
            _anim.Interval = 30;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();

            Thread hwThread = new Thread(delegate()
            {
                try
                {
                    string line = HwInfoReader.Read(MemSnapshot.Read().TotalPhys);
                    if (line.Length > 0) BeginInvoke(new Action(delegate { _lblHw.Text = line; }));
                }
                catch { }
            });
            hwThread.IsBackground = true;
            hwThread.Start();

            Log("就绪。当前" + (_helper != null && _helper.IsAlive ? "已有提权组件" : "未提权") +
                "；首次清理时会提示一次 UAC，之后本会话不再提示。");
            Log("本程序不注册任何自启动项、不常驻后台：关闭窗口即完全退出，并结束它启动的全部子进程。");
        }

        // ---------------------------------------------------------------- UI
        private void BuildUi()
        {
            _strip = new WarningStrip();
            _strip.Clicked += new EventHandler(delegate { if (!_busy) { SetLevel(_recLevel, true); StartClean(); } });
            Controls.Add(_strip);

            _lblTitle = MkLabel("MemoryClear", Ui.F(15f, FontStyle.Bold), Theme.Text);
            _lblSub = MkLabel("内存分级清理 · 只在真正需要时动系统缓存", Ui.F(9f, FontStyle.Regular), Theme.Muted);
            _lblHw = MkLabel("", Ui.F(9f, FontStyle.Regular), Theme.Muted);
            _lblHw.TextAlign = ContentAlignment.MiddleRight;
            _lblLevels = MkLabel("清理级别", Ui.F(10.5f, FontStyle.Bold), Theme.Text);
            _lblProg = MkLabel("就绪", Ui.F(9f, FontStyle.Regular), Theme.Muted);
            _lblPct = MkLabel("0%", Ui.F(9f, FontStyle.Bold), Theme.Muted);
            _lblPct.TextAlign = ContentAlignment.MiddleRight;
            _lblResult = MkLabel("", Ui.F(9f, FontStyle.Regular), Theme.Muted);

            _comp = new MemComposition();
            _stats = new StatGrid();
            _prog = new UiProgress();
            Controls.Add(_comp);
            Controls.Add(_stats);
            Controls.Add(_prog);

            string[] titles = { "L1  轻度", "L2  标准", "L3  深度", "L4  极限", "L5  全系统" };
            string[] descs =
            {
                "只丢弃低优先级待命缓存，热数据不受影响，几乎无副作用",
                "丢弃全部待命（备用）缓存，直接转为可用内存，机械硬盘会有读盘抖动",
                "先把已修改页刷写到磁盘（脏页转可用），再丢弃待命缓存",
                "逐个进程裁剪工作集（系统/杀毒/本程序白名单），随后刷脏页并丢弃待命缓存",
                "内核级清空所有进程的工作集 + 刷脏页 + 丢弃待命缓存，最彻底（见效最大，会明显卡顿）"
            };
            for (int i = 0; i < 5; i++)
            {
                LevelCard c = new LevelCard();
                c.Index = i + 1;
                c.Title = titles[i];
                c.Desc = descs[i];
                c.Text = titles[i];                        // 让 UIA/读屏能拿到名字
                c.AccessibleName = titles[i];
                c.AccessibleRole = AccessibleRole.RadioButton;
                int lv = i + 1;
                c.Click += new EventHandler(delegate { SetLevel(lv, true); });
                _cards[i] = c;
                Controls.Add(c);
            }

            _btnStart = MkButton("开始清理", Theme.Accent, Color.White);
            _btnStart.Click += new EventHandler(delegate { StartClean(); });
            _btnCancel = MkButton("取消", Theme.Card, Theme.Text);
            _btnCancel.Enabled = false;
            _btnCancel.Click += new EventHandler(delegate
            {
                _cancel = true;
                Log("正在取消…");
            });
            _btnExit = MkButton("退出", Theme.Card, Theme.Text);
            _btnExit.Click += new EventHandler(delegate { Close(); });
            _btnRefresh = MkButton("刷新状态", Theme.Card, Theme.Text);
            _btnRefresh.Click += new EventHandler(delegate { RefreshSnapshot(true); });

            Controls.Add(_btnStart); Controls.Add(_btnCancel);
            Controls.Add(_btnExit); Controls.Add(_btnRefresh);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.WordWrap = false;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.LogBg;
            _log.ForeColor = Color.FromArgb(168, 176, 190);
            _log.Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
            Controls.Add(_log);
        }

        private Label MkLabel(string text, Font f, Color c)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.AutoSize = false;
            Controls.Add(l);
            return l;
        }

        private Button MkButton(string text, Color back, Color fore)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = Ui.F(9.5f, FontStyle.Bold);
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void LayoutAll()
        {
            int pad = Ui.Px(20);
            int w = ClientSize.Width, h = ClientSize.Height;
            int cw = w - pad * 2;
            int y = Ui.Px(16);

            _strip.SetBounds(pad, y, cw, Ui.Px(52)); y += Ui.Px(52) + Ui.Px(14);

            _lblTitle.SetBounds(pad, y, Ui.Px(200), Ui.Px(28));
            _lblHw.SetBounds(pad + Ui.Px(205), y + Ui.Px(4), cw - Ui.Px(205) - Ui.Px(102), Ui.Px(20));
            _btnRefresh.SetBounds(w - pad - Ui.Px(92), y + Ui.Px(1), Ui.Px(92), Ui.Px(28));
            y += Ui.Px(32);
            _lblSub.SetBounds(pad, y, cw, Ui.Px(20)); y += Ui.Px(26);

            // 内存容量显示（任务管理器风格）：一条内存组合 + 下方数据网格
            _comp.SetBounds(pad, y, cw, Ui.Px(72));
            y += Ui.Px(72) + Ui.Px(8);
            _stats.SetBounds(pad, y, cw, Ui.Px(76));
            y += Ui.Px(76) + Ui.Px(14);

            _lblLevels.SetBounds(pad, y, cw, Ui.Px(20)); y += Ui.Px(24);
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].SetBounds(pad, y, cw, Ui.Px(58));
                y += Ui.Px(58) + Ui.Px(6);
            }
            y += Ui.Px(6);

            _prog.SetBounds(pad, y, cw, Ui.Px(8)); y += Ui.Px(12);
            _lblProg.SetBounds(pad, y, cw - Ui.Px(60), Ui.Px(20));
            _lblPct.SetBounds(w - pad - Ui.Px(56), y, Ui.Px(56), Ui.Px(20)); y += Ui.Px(22);
            _lblResult.SetBounds(pad, y, cw, Ui.Px(20)); y += Ui.Px(24);

            _btnStart.SetBounds(pad, y, Ui.Px(180), Ui.Px(42));
            _btnCancel.SetBounds(pad + Ui.Px(190), y, Ui.Px(110), Ui.Px(42));
            _btnExit.SetBounds(w - pad - Ui.Px(110), y, Ui.Px(110), Ui.Px(42));
            y += Ui.Px(42) + Ui.Px(12);

            int logH = h - y - Ui.Px(14);
            if (logH < Ui.Px(60)) logH = Ui.Px(60);
            _log.SetBounds(pad, y, cw, logH);
        }

        // ------------------------------------------------------------ 状态刷新
        private void RefreshSnapshot(bool force)
        {
            _snap = MemSnapshot.Read();

            string reason;
            int rec = Policy.Recommend(_snap, out reason);
            _recLevel = rec;
            _recReason = reason;
            if (!_userPicked || force && _selLevel < 1) SetLevel(rec, false);
            else UpdateCards();

            _comp.SetSnapshot(_snap);
            _stats.SetSnapshot(_snap);

            ApplyPendingDelta();

            string detail;
            Severity sev = Policy.Judge(_snap, out detail);
            if (sev != _severity)
            {
                _severity = sev;
                if (sev == Severity.Ok)
                    _strip.SetState(Severity.Ok, "内存状态正常", detail + "    ·    推荐 " + Policy.LevelName(_recLevel));
                else if (sev == Severity.Warn)
                    _strip.SetState(Severity.Warn, "内存偏紧", detail + "    ·    推荐 " + Policy.LevelName(_recLevel) + "：" + _recReason);
                else
                    _strip.SetState(Severity.Critical, "内存紧张", detail + "    ·    建议 " + Policy.LevelName(_recLevel) + "：" + _recReason);
                Log("状态：" + SeverityText(sev) + "，" + detail);

                if (sev != Severity.Ok && (DateTime.Now - _lastBalloon).TotalSeconds > 90)
                {
                    _lastBalloon = DateTime.Now;
                    FlashTaskbar();   // 只在窗口运行时闪任务栏提示，不做托盘常驻
                }
            }
            else
            {
                // 同一状态下只更新细节文案，避免频繁重绘闪烁
                if (sev == Severity.Ok) _strip.SetState(Severity.Ok, "内存状态正常", detail);
            }
        }

        /// <summary>清理结束后，用第一次常规刷新的快照算出前后差值（不额外采样、不额外占进度）</summary>
        private void ApplyPendingDelta()
        {
            if (_pendingBefore == null || _busy) return;
            MemSnapshot b = _pendingBefore;
            _pendingBefore = null;
            long dAvail = _snap.AvailPhys - b.AvailPhys;
            long dStandby = _snap.StandbyTotal - b.StandbyTotal;
            string delta = string.Format("可用 {0}{1} · 待命缓存 {2}{3}",
                dAvail >= 0 ? "+" : "-", Ui.Bytes(Math.Abs(dAvail)),
                dStandby <= 0 ? "-" : "+", Ui.Bytes(Math.Abs(dStandby)));
            _lblResult.Text = delta + " · 清理完成";
            _lblResult.ForeColor = Theme.Ok;
            Log(delta);
            if (_snap.CommitRatio > 0.9)
                Log("提示：提交量已接近上限（" + Ui.Bytes(_snap.CommitTotal) + " / " + Ui.Bytes(_snap.CommitLimit) +
                    "），清理缓存不能降低提交量，建议排查占用内存的进程或增大页面文件。");
        }

        private void FlashTaskbar()
        {
            if (IsHandleCreated) Native.FlashTaskbar(Handle);
        }

        private static string SeverityText(Severity s)
        {
            return s == Severity.Ok ? "正常" : (s == Severity.Warn ? "偏紧" : "紧张");
        }

        private void SetLevel(int level, bool byUser)
        {
            if (level < 1) level = 1;
            if (level > 5) level = 5;
            _selLevel = level;
            if (byUser) _userPicked = true;
            UpdateCards();
        }

        private void UpdateCards()
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].Selected = (_cards[i].Index == _selLevel);
                _cards[i].Recommended = (_cards[i].Index == _recLevel);
            }
            int diff = _selLevel - _recLevel;
            _lblResult.Text = (diff == 0 ? "已选择推荐级别：" : "已手动选择：") + Policy.LevelName(_selLevel) +
                              (diff == 0 ? "" : "（推荐 " + Policy.LevelName(_recLevel) + "）");
            _lblResult.ForeColor = diff == 0 ? Theme.Muted : Theme.Warn;
        }

        // ------------------------------------------------------------ 清理流程
        private void StartClean()
        {
            if (_busy) return;
            int level = _selLevel;
            if (level == 5)
            {
                DialogResult r5 = MessageBox.Show(
                    "L5 会由内核清空【所有进程】的工作集，包括系统进程和杀毒软件，正在运行的程序都要重新读盘。\r\n\r\n" +
                    "这是最彻底的一档，代价是之后几十秒内整机会明显卡顿，机械硬盘尤其明显。\r\n\r\n确定继续吗？",
                    "确认 L5 全系统清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r5 != DialogResult.Yes) return;
            }
            _busy = true;
            _cancel = false;
            _btnStart.Enabled = false;
            _btnCancel.Enabled = true;
            _btnRefresh.Enabled = false;
            _targetPct = 0;
            _shownPct = 0;
            _prog.Value = 0;
            _lblPct.Text = "0%";
            Log("──────── 开始 " + Policy.LevelName(level) + " 清理 ────────");
            _lblProg.Text = "准备中…";

            bool elevated = Native.IsElevated();
            _worker = new Thread(delegate() { WorkThread(level, elevated); });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void ReportP(double pct, string msg)
        {
            _targetPct = Math.Max(_targetPct, Math.Max(0, Math.Min(100, pct)));
            if (!string.IsNullOrEmpty(msg) && _lblProg.IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _lblProg.Text = msg;
                        _log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
                    }));
                }
                catch { }
            }
        }

        private void WorkThread(int level, bool elevated)
        {
            MemSnapshot before = null;
            string error = null;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                ReportP(2, "采集清理前快照");
                before = MemSnapshot.Read();
                LogFromThread("清理前：" + before.Describe());

                Action<double, string> prog = delegate(double p, string m)
                {
                    if (_cancel) throw new OperationCanceledException();
                    ReportP(12 + p * 0.74, m);
                };

                if (elevated)
                {
                    LogFromThread("当前已是管理员权限，直接在本进程执行");
                    CleanEngine.Run(level, prog, delegate { return _cancel; }, false);
                }
                else
                {
                    ReportP(6, "请求管理员权限（本次会话仅一次）");
                    if (_helper == null) _helper = new CleanHelper();
                    bool reused = _helper.IsAlive;
                    _helper.EnsureStarted();
                    LogFromThread(reused
                        ? "复用已提权的清理组件（PID " + _helper.ProcessId + "），不再弹 UAC"
                        : "提权清理组件已启动（PID " + _helper.ProcessId + "），后续清理复用它");
                    _helper.Clean(level, prog);
                }

                ReportP(100, "完成");
            }
            catch (OperationCanceledException) { error = "已取消"; }
            catch (Exception ex)
            {
                error = ex.Message;
                LogFromThread("错误：" + ex.GetType().Name + " - " + ex.Message);
            }
            sw.Stop();
            try { BeginInvoke(new Action(delegate { FinishWork(level, before, error, sw.ElapsedMilliseconds); })); }
            catch { }
        }

        private void FinishWork(int level, MemSnapshot before, string error, long ms)
        {
            _busy = false;
            _btnStart.Enabled = true;
            _btnCancel.Enabled = false;
            _btnRefresh.Enabled = true;
            _targetPct = error == null ? 100 : _shownPct;   // 失败时进度停住，不假装走完

            if (error != null)
            {
                _lblProg.Text = error;
                _lblProg.ForeColor = Theme.Danger;
                _pendingBefore = null;
            }
            else
            {
                _lblProg.ForeColor = Theme.Muted;
                _lblProg.Text = "清理完成";
                _lblResult.Text = "清理完成 · 耗时 " + (ms / 1000.0).ToString("0.0") + " s";
                _lblResult.ForeColor = Theme.Muted;
                _pendingBefore = before;       // 差值由下一次常规刷新（≤1s）就地算出，不再额外采样
            }
            RefreshSnapshot(true);
        }

        private void AnimTick(object sender, EventArgs e)
        {
            if (Math.Abs(_targetPct - _shownPct) < 0.3)
            {
                if (_shownPct != _targetPct) { _shownPct = _targetPct; _prog.Value = _shownPct; _lblPct.Text = ((int)Math.Round(_shownPct)) + "%"; }
                return;
            }
            // 平滑插值 + 最小步进，快速完成时也让进度条可见地走完
            double step = Math.Max(0.8, (_targetPct - _shownPct) * 0.18);
            _shownPct = Math.Min(_targetPct, _shownPct + step);
            _prog.Value = _shownPct;
            _lblPct.Text = ((int)Math.Round(_shownPct)) + "%";
        }

        private static readonly string DbgLog = Environment.GetEnvironmentVariable("MEMORYCLEAR_LOG");

        private void Log(string msg)
        {
            if (DbgLog != null && DbgLog.Length > 0)
            {
                try { File.AppendAllText(DbgLog, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); }
                catch { }
            }
            if (_log == null) return;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
        }

        private void LogFromThread(string msg)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate { Log(msg); }));
            }
            catch { }
        }

        // ------------------------------------------------------------ 退出清理
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _monitor.Stop();
            _anim.Stop();
            _cancel = true;
            KillAllSpawned();
            base.OnFormClosing(e);
        }

        /// <summary>结束本程序创建的所有子进程（含提权组件），并复核一次</summary>
        public static void KillAllSpawned()
        {
            List<int> pids = new List<int>();
            lock (CleanHelper.SpawnedPids) { pids.AddRange(CleanHelper.SpawnedPids); }
            for (int i = 0; i < pids.Count; i++)
            {
                try
                {
                    Process p = Process.GetProcessById(pids[i]);
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    p.Dispose();
                }
                catch { }
            }
            lock (CleanHelper.SpawnedPids) { CleanHelper.SpawnedPids.Clear(); }

            // 复核：仍存在的同名子进程（按启动时间/父路径无法可靠判断时，按 PID 已处理，这里仅兜底剩余句柄）
            CheckRemaining();
        }

        private static void CheckRemaining()
        {
            // 只清理“同名且没有主窗口”的残留实例 —— 也就是本程序的提权助手；
            // 用户自己另开的 GUI 实例有主窗口，不会误杀。
            try
            {
                string self = Process.GetCurrentProcess().ProcessName;
                Process[] all = Process.GetProcessesByName(self);
                int me = Process.GetCurrentProcess().Id;
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        if (all[i].Id != me && all[i].MainWindowHandle == IntPtr.Zero)
                            all[i].Kill();
                    }
                    catch { }
                    finally { all[i].Dispose(); }
                }
            }
            catch { }
        }
    }

    // =====================================================================
    //  入口
    // =====================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string mode = args[0].ToLowerInvariant();
                if (mode == "--helper")
                {
                    string pipe = args.Length > 1 ? args[1] : "";
                    int ppid = 0;
                    if (args.Length > 2) int.TryParse(args[2], out ppid);
                    HelperMode.Run(pipe, ppid);
                    return;
                }
                if (mode == "--selftest") { SelfTest(); return; }
                if (mode == "--policy-test") { PolicyTest(); return; }
                if (mode == "--cli") { RunCli(args); return; }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new ThreadExceptionEventHandler(delegate(object s, ThreadExceptionEventArgs e)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "MemoryClear_error.log"),
                        DateTime.Now.ToString("s") + " UI: " + e.Exception + Environment.NewLine);
                }
                catch { }
                MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(delegate { MainForm.KillAllSpawned(); });
            Application.Run(new MainForm());
        }

        // ------------------------------------------------------------ 自测（只读）
        private static void SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== MemoryClear selftest ==");
            sb.AppendLine("管理员权限: " + Native.IsElevated());
            MemSnapshot s = MemSnapshot.Read();
            sb.AppendLine("总物理内存: " + Ui.Bytes(s.TotalPhys) + " (" + s.TotalPhys + " B)");
            sb.AppendLine("可用: " + Ui.Bytes(s.AvailPhys) + "  可用比例: " + (s.AvailRatio * 100).ToString("0.0") + "%");
            sb.AppendLine("提交: " + Ui.Bytes(s.CommitTotal) + " / " + Ui.Bytes(s.CommitLimit) + "  (" + (s.CommitRatio * 100).ToString("0.0") + "%)");
            sb.AppendLine("待命缓存: " + Ui.Bytes(s.StandbyTotal) + "  低优先级: " + Ui.Bytes(s.StandbyLow));
            sb.AppendLine("已修改: " + Ui.Bytes(s.Modified) + "  空闲页: " + Ui.Bytes(s.FreePages) + "  零页: " + Ui.Bytes(s.ZeroPages));
            sb.AppendLine("已压缩: " + Ui.Bytes(s.Compressed) + "  已缓存: " + Ui.Bytes(s.CachedBytes));
            sb.AppendLine("分页缓冲池: " + Ui.Bytes(s.PagedPool) + "  非分页缓冲池: " + Ui.Bytes(s.NonPagedPool));
            sb.AppendLine("硬件: " + HwInfoReader.Read(s.TotalPhys));
            sb.AppendLine("列表查询可用: " + s.HasLists + "  进程数: " + s.ProcessCount);
            string reason;
            int rec = Policy.Recommend(s, out reason);
            sb.AppendLine("推荐级别: " + Policy.LevelName(rec) + " —— " + reason);
            string detail;
            sb.AppendLine("告警判定: " + Policy.Judge(s, out detail) + " —— " + detail);

            int[] pids = new int[16384];
            int bytes;
            Native.EnumProcesses(pids, pids.Length * 4, out bytes);
            int n = bytes / 4, trimmable = 0, prot = 0, noaccess = 0;
            for (int i = 0; i < n; i++)
            {
                int pid = pids[i];
                if (pid <= 4) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) { noaccess++; continue; }
                try
                {
                    string name = Native.ProcessNameSafe(h);
                    if (CleanEngine.IsProtectedName(name)) prot++;
                    else trimmable++;
                }
                finally { Native.CloseHandle(h); }
            }
            sb.AppendLine(string.Format("L4 预演: 共 {0} 个进程，可裁剪 {1}，白名单 {2}，无权限 {3}", n, trimmable, prot, noaccess));
            sb.AppendLine("提权路径可用（不触发 UAC 的检查）: " + Native.EnablePrivilege("SeProfileSingleProcessPrivilege"));
            sb.AppendLine("== end ==");

            string text = sb.ToString();
            try { AttachConsole(-1); } catch { }
            try { Console.Write(text); } catch { }
            string file = Path.Combine(Path.GetTempPath(), "MemoryClear_selftest.txt");
            try { File.WriteAllText(file, text); } catch { }
            Environment.Exit(0);
        }

        // ------------------------------------------------------------ 策略自测
        private sealed class PolicyCase
        {
            public string Name;
            public double AvailGB, CommitLimitGB, CommitRatio, StandbyGB, ModifiedMB;
            public PolicyCase(string n, double avail, double lim, double ratio, double standby, double modified)
            { Name = n; AvailGB = avail; CommitLimitGB = lim; CommitRatio = ratio; StandbyGB = standby; ModifiedMB = modified; }
        }

        private static void PolicyTest()
        {
            const double GB = 1024.0 * 1024 * 1024;
            const double MB = 1024.0 * 1024;
            StringBuilder sb = new StringBuilder();
            string outFile = Path.Combine(Path.GetTempPath(), "MemoryClear_policy.txt");
            try
            {
                sb.AppendLine("== Policy 自测：合成快照 → 告警等级 / 推荐分级（总物理内存固定 32GB）==");
                sb.AppendLine("场景                    可用GB 提交率 待命GB 改MB | 告警      推荐  说明");
                PolicyCase[] cases =
                {
                    new PolicyCase("内存充足·缓存大", 20, 32, 0.55, 6,   300),
                    new PolicyCase("内存充足·缓存少", 22, 32, 0.55, 0.1, 100),
                    new PolicyCase("偏紧·缓存偏多",    9, 32, 0.80, 7,   300),
                    new PolicyCase("偏紧·脏页多",      9, 32, 0.70, 6,   900),
                    new PolicyCase("紧张",             5, 32, 0.85, 3.5, 300),
                    new PolicyCase("极度紧张",         3, 32, 0.88, 1.5, 400),
                    new PolicyCase("紧张·提交率告急",  3, 32, 0.93, 1.5, 300),
                };
                for (int i = 0; i < cases.Length; i++)
                {
                    PolicyCase c = cases[i];
                    MemSnapshot s = new MemSnapshot();
                    s.TotalPhys = (long)(32 * GB);
                    s.AvailPhys = (long)(c.AvailGB * GB);
                    s.CommitLimit = (long)(c.CommitLimitGB * GB);
                    s.CommitTotal = (long)(c.CommitLimitGB * c.CommitRatio * GB);
                    s.StandbyTotal = (long)(c.StandbyGB * GB);
                    s.StandbyLow = s.StandbyTotal / 2;
                    s.Modified = (long)(c.ModifiedMB * MB);
                    s.HasLists = true;
                    string reason, detail;
                    int rec = Policy.Recommend(s, out reason);
                    Severity sev = Policy.Judge(s, out detail);
                    sb.AppendLine(string.Format("{0,-20} {1,6:0.0} {2,5:0}% {3,6:0.0} {4,4:0} | {5,-9} {6,-6} {7}",
                        c.Name, c.AvailGB, s.CommitRatio * 100, c.StandbyGB, c.ModifiedMB,
                        sev.ToString(), Policy.LevelName(rec), reason));
                }
            }
            catch (Exception ex) { sb.AppendLine("policy-test 异常: " + ex); }
            string text = sb.ToString();
            try { File.WriteAllText(outFile, text); } catch { }
            try { AttachConsole(-1); } catch { }
            try { Console.Write(text); } catch { }
            Environment.Exit(0);
        }

        // ------------------------------------------------------------ CLI
        private static void RunCli(string[] args)
        {
            int level = 1;
            bool dryRun = false, selfOnly = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--dry-run") dryRun = true;
                else if (a == "--self-only") selfOnly = true;
                else if (a.StartsWith("--level")) { if (i + 1 < args.Length) int.TryParse(args[++i], out level); }
                else { int l; if (int.TryParse(a, out l)) level = l; }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== MemoryClear cli: level " + level + (dryRun ? " (dry-run)" : "") + " ==");
            sb.AppendLine("管理员: " + Native.IsElevated());
            MemSnapshot before = MemSnapshot.Read();
            sb.AppendLine("前: " + before.Describe());
            try
            {
                if (dryRun)
                {
                    string reason;
                    sb.AppendLine("推荐: " + Policy.LevelName(Policy.Recommend(before, out reason)) + " —— " + reason);
                    sb.AppendLine("（dry-run：未执行任何清理指令）");
                }
                else
                {
                    CleanEngine.Run(level, delegate(double p, string m)
                    {
                        sb.AppendLine(string.Format("  [{0,3:0}%] {1}", p, m));
                    }, delegate { return false; }, selfOnly);
                }
            }
            catch (Exception ex) { sb.AppendLine("失败: " + ex.GetType().Name + ": " + ex.Message); }
            MemSnapshot after = MemSnapshot.Read();
            sb.AppendLine("后: " + after.Describe());

            string text = sb.ToString();
            try { AttachConsole(-1); } catch { }
            try { Console.Write(text); } catch { }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MemoryClear_cli.txt"), text); } catch { }
            Environment.Exit(0);
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
    }
}
