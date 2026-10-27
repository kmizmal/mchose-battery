using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MchoseBattery.Hid;

namespace MchoseBattery
{
    /// <summary>
    /// 一个设备（一张卡片）的全部采集状态与配置。
    ///
    /// 关键：插件 DLL 在 Rainmeter 进程里只有一份实例，静态字段是进程级共享的。
    /// 若把状态放静态单例，同时加载鼠标卡片与键盘卡片时，后加载的那张会覆盖
    /// Device 配置，两张卡片就会读到同一个设备。因此按 Device 分桶，各存各的。
    ///
    /// 同一张卡片内的多个 measure（一个主实例采集 + 若干 Field= 只读实例）
    /// 必须落到同一个桶，所以皮肤里每个 measure 都要带 Device= 选项。
    /// </summary>
    internal sealed class DeviceSlot
    {
        internal readonly string Device;
        internal readonly object Gate = new object();

        internal volatile StatusSnapshot Current = StatusSnapshot.Initial();
        internal LastGood Last = new LastGood();
        internal int Fails;
        internal int LastAlert = StatusCodes.AlertNone;
        internal long BeepAtTicks;
        internal DateTime NextPollUtc = DateTime.MinValue;
        internal bool Polling;
        internal bool CacheLoaded;

        internal int Low = 20;
        internal int Critical = 8;
        internal int IntervalMinutes = 20;
        internal int WindowMs = 450;
        internal int BudgetMs = 12000;
        internal bool Sound;
        internal bool Debug;
        internal string CachePath = string.Empty;

        internal DeviceSlot(string device)
        {
            Device = device;
        }
    }

    /// <summary>
    /// 按设备分桶的采集状态表。
    /// 采集走线程池，Update 只读缓存 —— 主线程绝不能被 HID 往返拖住。
    /// </summary>
    internal static class SharedState
    {
        private static readonly object SlotsGate = new object();
        private static readonly Dictionary<string, DeviceSlot> Slots =
            new Dictionary<string, DeviceSlot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取（必要时创建）某个设备的状态桶。不改动配置。</summary>
        internal static DeviceSlot GetSlot(string device)
        {
            string key = Normalize(device);
            lock (SlotsGate)
            {
                DeviceSlot slot;
                if (!Slots.TryGetValue(key, out slot))
                {
                    slot = new DeviceSlot(key);
                    Slots[key] = slot;
                }
                return slot;
            }
        }

        /// <summary>只有主实例（不写 Field=）才调用，避免字段实例用默认值覆盖配置。</summary>
        internal static void Configure(
            DeviceSlot slot,
            int low, int critical, int intervalMinutes, int windowMs, int budgetMs,
            bool sound, string cachePath, bool debug)
        {
            lock (slot.Gate)
            {
                slot.Low = Math.Max(0, Math.Min(100, low));
                slot.Critical = Math.Max(0, Math.Min(slot.Low, critical));
                slot.IntervalMinutes = Math.Max(1, intervalMinutes);
                slot.WindowMs = Math.Max(80, windowMs);
                slot.BudgetMs = Math.Max(1000, budgetMs);
                slot.Sound = sound;
                slot.Debug = debug;

                if (!string.Equals(slot.CachePath, cachePath, StringComparison.OrdinalIgnoreCase))
                {
                    slot.CachePath = cachePath;
                    slot.CacheLoaded = false;
                }
            }
        }

        private static string Normalize(string device)
        {
            return string.IsNullOrEmpty(device) ? "auto" : device.Trim().ToLowerInvariant();
        }

        internal static void Log(IntPtr rm, int level, string message)
        {
            try
            {
                RainmeterApi.RmLog(rm, level, "[MCHOSE_Battery] " + message);
            }
            catch
            {
                // 日志失败不应影响采集
            }
        }

        /// <summary>到点就在后台采一次；force=true 用于左键手动刷新。</summary>
        internal static void EnsureFresh(DeviceSlot slot, bool force)
        {
            if (slot == null)
            {
                return;
            }

            if (!slot.CacheLoaded)
            {
                lock (slot.Gate)
                {
                    if (!slot.CacheLoaded)
                    {
                        LoadCacheLocked(slot);
                        slot.CacheLoaded = true;
                    }
                }
            }

            if (slot.Polling)
            {
                return;
            }
            if (!force && DateTime.UtcNow < slot.NextPollUtc)
            {
                return;
            }

            lock (slot.Gate)
            {
                if (slot.Polling)
                {
                    return;
                }
                if (!force && DateTime.UtcNow < slot.NextPollUtc)
                {
                    return;
                }
                slot.Polling = true;
                // 先占住下一个时间点，避免采集期间每个 Update 都重复排队
                slot.NextPollUtc = DateTime.UtcNow.AddMinutes(slot.IntervalMinutes);
            }

            ThreadPool.QueueUserWorkItem(Poll, slot);
        }

        /// <summary>
        /// 按桶里的 Device 选项选择采集通道。
        /// auto：先探 G87 键盘，再回落到 V7 鼠标；两者都不在线时回退 V7 的提示文案。
        /// 耳机不走 auto —— 它是 3837 家族里的另一套 64 字节 0x55 协议，必须显式 v9/headset。
        /// </summary>
        private static CollectResult QueryDevice(DeviceSlot slot)
        {
            int windowMs = slot.WindowMs;
            int budgetMs = slot.BudgetMs;

            switch (slot.Device)
            {
                case "g87":
                case "keyboard":
                    return G87Battery.Query(windowMs);

                case "v7":
                case "mouse":
                    return HidBattery.Query(windowMs, budgetMs);

                case "v9":
                case "headset":
                    return V9Battery.Query(windowMs);

                default:
                    CollectResult keyboard = G87Battery.Query(windowMs);
                    if (keyboard.Ok)
                    {
                        return keyboard;
                    }

                    CollectResult mouse = HidBattery.Query(windowMs, budgetMs);
                    if (mouse.Ok)
                    {
                        return mouse;
                    }

                    return keyboard.Reason == "absent" ? mouse : keyboard;
            }
        }

        private static void Poll(object state)
        {
            var slot = state as DeviceSlot;
            if (slot == null)
            {
                return;
            }

            CollectResult result = null;
            try
            {
                result = QueryDevice(slot);
            }
            catch (Exception ex)
            {
                result = new CollectResult { Ok = false, Reason = "exception", Detail = ex.Message };
            }

            bool shouldBeep = false;
            try
            {
                int fails;
                StatusSnapshot snapshot = StatusBuilder.Build(
                    result, slot.Low, slot.Critical, slot.Last, slot.Fails, out fails);

                lock (slot.Gate)
                {
                    slot.Fails = fails;
                    slot.Current = snapshot;

                    if (result.Ok)
                    {
                        slot.Last = new LastGood
                        {
                            Percent = snapshot.Percent,
                            Mode = snapshot.Mode,
                            Updated = snapshot.Updated,
                            HasValue = true,
                        };
                    }

                    if (slot.Sound)
                    {
                        shouldBeep = ShouldBeepLocked(slot, snapshot);
                    }
                    else if (snapshot.Alert == StatusCodes.AlertNone)
                    {
                        slot.LastAlert = StatusCodes.AlertNone;
                    }
                }

                SaveCache(slot);
            }
            catch
            {
                // 任何异常都不能让 Polling 卡在 true
            }
            finally
            {
                lock (slot.Gate)
                {
                    slot.Polling = false;
                    if (slot.NextPollUtc < DateTime.UtcNow)
                    {
                        slot.NextPollUtc = DateTime.UtcNow.AddMinutes(slot.IntervalMinutes);
                    }
                }
            }

            if (shouldBeep)
            {
                MessageBeep(0x00000030); // MB_ICONWARNING，与 Windows 声音方案一致
            }
        }

        private static bool ShouldBeepLocked(DeviceSlot slot, StatusSnapshot snapshot)
        {
            if (snapshot.Alert == StatusCodes.AlertNone)
            {
                slot.LastAlert = StatusCodes.AlertNone;
                return false;
            }

            long now = DateTime.UtcNow.Ticks;
            bool changed = snapshot.Alert != slot.LastAlert;
            bool lowKind = snapshot.Alert == StatusCodes.AlertLow
                           || snapshot.Alert == StatusCodes.AlertCritical;
            bool repeat = lowKind && (now - slot.BeepAtTicks) >= TimeSpan.TicksPerHour;

            if (!changed && !repeat)
            {
                return false;
            }

            slot.BeepAtTicks = now;
            slot.LastAlert = snapshot.Alert;
            return true;
        }

        // ---------------- 缓存（跨 Rainmeter 重启保留最后一次读数） ----------------

        private static void LoadCacheLocked(DeviceSlot slot)
        {
            if (string.IsNullOrEmpty(slot.CachePath) || !File.Exists(slot.CachePath))
            {
                return;
            }

            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(slot.CachePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                int value;
                var good = new LastGood();
                if (map.TryGetValue("percent", out var pctRaw) && int.TryParse(pctRaw, out value))
                {
                    good.Percent = Math.Max(0, Math.Min(100, value));
                    good.HasValue = true;
                }
                if (map.TryGetValue("mode", out var modeRaw) && int.TryParse(modeRaw, out value))
                {
                    good.Mode = value;
                }
                if (map.TryGetValue("updated", out var upd) && upd.Length == 8)
                {
                    good.Updated = upd;
                }
                slot.Last = good;

                if (map.TryGetValue("alert", out var alertRaw) && int.TryParse(alertRaw, out value))
                {
                    slot.LastAlert = value;
                }
                if (map.TryGetValue("beepTicks", out var beepRaw) && long.TryParse(beepRaw, out var ticks))
                {
                    slot.BeepAtTicks = ticks;
                }

                if (good.HasValue)
                {
                    // 首帧先用缓存值填充，等第一次真实采集完成再替换
                    slot.Current = new StatusSnapshot
                    {
                        State = StatusCodes.StateOffline,
                        Percent = good.Percent,
                        PercentText = good.Percent.ToString(),
                        Mode = good.Mode,
                        Updated = good.Updated,
                        Reason = "cached",
                        Theme = StatusCodes.ThemeOffline,
                        Alert = StatusCodes.AlertOffline,
                    };
                }
            }
            catch
            {
                // 缓存损坏时静默丢弃，不影响采集
            }
        }

        private static void SaveCache(DeviceSlot slot)
        {
            if (string.IsNullOrEmpty(slot.CachePath))
            {
                return;
            }

            try
            {
                StatusSnapshot snap = slot.Current;
                var sb = new StringBuilder();
                sb.AppendLine("percent=" + snap.Percent);
                sb.AppendLine("mode=" + snap.Mode);
                sb.AppendLine("updated=" + snap.Updated);
                sb.AppendLine("alert=" + slot.LastAlert);
                sb.AppendLine("beepTicks=" + slot.BeepAtTicks);
                sb.AppendLine("saved=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                string dir = Path.GetDirectoryName(slot.CachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = slot.CachePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(slot.CachePath))
                {
                    File.Delete(slot.CachePath);
                }
                File.Move(tmp, slot.CachePath);
            }
            catch
            {
                // 缓存写失败不影响主流程
            }
        }

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);
    }
}
