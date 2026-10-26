using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MchoseBattery
{
    /// <summary>
    /// 一个 measure 实例的状态。
    ///
    /// 皮肤里会有两类实例：
    ///   · 主实例（不写 Field）：负责按间隔触发采集，Update 返回电量数值
    ///   · 字段实例（Field=Percent / Theme / …）：只读共享缓存，负责把某个字段喂给 Meter
    /// </summary>
    internal sealed class MeasureContext : IDisposable
    {
        private const int BufferChars = 512;

        internal IntPtr Rm;
        internal string Field = string.Empty;
        internal bool IsMaster;
        internal int IntervalMinutes = 20;
        internal int LowBattery = 20;
        internal int CriticalBattery = 8;
        internal int WindowMs = 450;
        internal int BudgetMs = 12000;
        internal bool Sound;
        internal bool Debug;
        internal string CachePath = string.Empty;
        internal string Device = "auto";
        internal DeviceSlot Slot;
        internal double Value;
        internal int GetStringLogs;

        private IntPtr _buffer;

        internal void AllocateBuffer()
        {
            if (_buffer != IntPtr.Zero)
            {
                return;
            }
            _buffer = Marshal.AllocHGlobal(BufferChars * 2);
            Marshal.WriteInt16(_buffer, 0);
        }

        internal IntPtr TextPointer
        {
            get { return _buffer; }
        }

        /// <summary>把字符串写进预分配缓冲，GetString 只返回指针，不做分配。</summary>
        internal void SetText(string text)
        {
            if (_buffer == IntPtr.Zero)
            {
                return;
            }
            text = text ?? string.Empty;
            if (text.Length >= BufferChars)
            {
                text = text.Substring(0, BufferChars - 1);
            }
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            Marshal.Copy(bytes, 0, _buffer, bytes.Length);
            Marshal.WriteInt16(_buffer, bytes.Length, 0);
        }

        public void Dispose()
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Rainmeter 插件导出入口。
    /// 注意：本文件里的方法名就是 DLL 的导出名，IL 后处理按名字注入 .export，
    /// 改动方法名等于改动 ABI，不要重命名。
    /// </summary>
    public static class Plugin
    {
        private const string Tag = "[MCHOSE_Battery] ";

        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            var context = new MeasureContext { Rm = rm };
            context.AllocateBuffer();
            data = GCHandle.ToIntPtr(GCHandle.Alloc(context));
        }

        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            MeasureContext context = GetContext(data);
            if (context == null)
            {
                return;
            }

            context.Rm = rm;

            string field;
            RainmeterApi.ReadString(rm, "Field", string.Empty, out field);
            context.Field = field;
            context.IsMaster = string.IsNullOrEmpty(field)
                               || field.Equals("All", StringComparison.OrdinalIgnoreCase);

            context.IntervalMinutes = RainmeterApi.ReadInt(rm, "IntervalMinutes", 20);
            context.LowBattery = RainmeterApi.ReadInt(rm, "LowBattery", 20);
            context.CriticalBattery = RainmeterApi.ReadInt(rm, "CriticalBattery", 8);
            context.WindowMs = RainmeterApi.ReadInt(rm, "WindowMs", 450);
            context.BudgetMs = RainmeterApi.ReadInt(rm, "BudgetMs", 12000);
            context.Sound = RainmeterApi.ReadInt(rm, "Sound", 0) != 0;
            context.Debug = RainmeterApi.ReadInt(rm, "Debug", 0) != 0;
            RainmeterApi.ReadString(rm, "CacheFile", string.Empty, out var cachePath);
            context.CachePath = cachePath;

            string device;
            RainmeterApi.ReadString(rm, "Device", "auto", out device);
            context.Device = device;

            maxValue = 100.0;

            // 每个实例都要定位到自己的设备桶，这样同一张卡片的主实例与若干
            // Field= 实例才会共享同一份数据，而两张卡片互不影响。
            // 只有主实例写配置，否则字段实例会用默认值把主实例的配置冲掉。
            context.Slot = SharedState.GetSlot(context.Device);

            if (context.IsMaster)
            {
                SharedState.Configure(
                    context.Slot,
                    context.LowBattery, context.CriticalBattery, context.IntervalMinutes,
                    context.WindowMs, context.BudgetMs, context.Sound, context.CachePath,
                    context.Debug);
            }
        }

        public static double Update(IntPtr data)
        {
            MeasureContext context = GetContext(data);
            if (context == null)
            {
                return 0.0;
            }

            if (context.IsMaster)
            {
                SharedState.EnsureFresh(context.Slot, false);
            }

            StatusSnapshot snapshot = context.Slot.Current;
            ApplyField(context, snapshot);
            return context.Value;
        }

        /// <summary>
        /// 字符串取值。
        ///
        /// 注意签名：Rainmeter 对原生插件定义的 GetString 是
        ///     LPCWSTR GetString(void* data)
        /// 即**直接返回字符串指针**，不是 void + 出参。
        /// 早期误写成 void GetString(void* data, WCHAR** str, BOOL request) 时，
        /// 参数个数不匹配，Rainmeter 会把函数返回值当成指针，读到未初始化内存，表现为界面乱码。
        /// </summary>
        public static IntPtr GetString(IntPtr data)
        {
            MeasureContext context = GetContext(data);
            if (context == null)
            {
                return IntPtr.Zero;
            }

            if (context.Debug && context.GetStringLogs < 2)
            {
                context.GetStringLogs++;
                SharedState.Log(context.Rm, RainmeterApi.LogNotice,
                    $"GetString field='{context.Field}' text='{Marshal.PtrToStringUni(context.TextPointer)}'");
            }

            return context.TextPointer;
        }

        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            MeasureContext context = GetContext(data);
            if (context == null)
            {
                return;
            }

            string command = args == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUni(args) ?? string.Empty);
            command = command.Trim();

            if (command.Equals("Refresh", StringComparison.OrdinalIgnoreCase)
                || command.Equals("Update", StringComparison.OrdinalIgnoreCase))
            {
                SharedState.EnsureFresh(context.Slot, true);
            }
            else if (command.Equals("Log", StringComparison.OrdinalIgnoreCase))
            {
                StatusSnapshot snap = context.Slot.Current;
                SharedState.Log(context.Rm, RainmeterApi.LogNotice,
                    $"state={snap.State} percent={snap.Percent} mode={snap.Mode} theme={snap.Theme} " +
                    $"alert={snap.Alert} reason={snap.Reason} updated={snap.Updated}");
            }
        }

        public static void Finalize(IntPtr data)
        {
            if (data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(data);
            var context = handle.Target as MeasureContext;
            handle.Free();
            if (context != null)
            {
                context.Dispose();
            }
        }

        private static MeasureContext GetContext(IntPtr data)
        {
            if (data == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return GCHandle.FromIntPtr(data).Target as MeasureContext;
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyField(MeasureContext context, StatusSnapshot snapshot)
        {
            string field = (context.Field ?? string.Empty).Trim();

            switch (field.ToLowerInvariant())
            {
                case "":
                case "all":
                    context.Value = snapshot.Percent;
                    context.SetText(ToJson(snapshot));
                    return;
                case "state":
                    context.Value = snapshot.State;
                    context.SetText(snapshot.State.ToString());
                    return;
                case "percent":
                    context.Value = snapshot.Percent;
                    context.SetText(snapshot.Percent.ToString());
                    return;
                case "percenttext":
                    context.Value = snapshot.Percent;
                    context.SetText(snapshot.PercentText);
                    return;
                case "charging":
                    context.Value = snapshot.Charging;
                    context.SetText(snapshot.Charging.ToString());
                    return;
                case "mode":
                    context.Value = snapshot.Mode;
                    context.SetText(snapshot.Mode.ToString());
                    return;
                case "alert":
                    context.Value = snapshot.Alert;
                    context.SetText(snapshot.Alert.ToString());
                    return;
                case "theme":
                    context.Value = snapshot.Theme;
                    context.SetText(snapshot.Theme.ToString());
                    return;
                case "fails":
                    context.Value = snapshot.Fails;
                    context.SetText(snapshot.Fails.ToString());
                    return;
                case "updated":
                    context.Value = 0;
                    context.SetText(snapshot.Updated);
                    return;
                case "reason":
                    context.Value = 0;
                    context.SetText(snapshot.Reason);
                    return;
                case "device":
                    context.Value = 0;
                    context.SetText(context.Slot.Device);
                    return;
                default:
                    context.Value = 0;
                    context.SetText(string.Empty);
                    return;
            }
        }

        private static string ToJson(StatusSnapshot s)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"state\":").Append(s.State).Append(',');
            sb.Append("\"percent\":").Append(s.Percent).Append(',');
            sb.Append("\"percent_text\":\"").Append(s.PercentText).Append("\",");
            sb.Append("\"charging\":").Append(s.Charging).Append(',');
            sb.Append("\"mode\":").Append(s.Mode).Append(',');
            sb.Append("\"alert\":").Append(s.Alert).Append(',');
            sb.Append("\"theme\":").Append(s.Theme).Append(',');
            sb.Append("\"fails\":").Append(s.Fails).Append(',');
            sb.Append("\"updated\":\"").Append(s.Updated).Append("\",");
            sb.Append("\"reason\":\"").Append(s.Reason).Append("\"");
            sb.Append('}');
            return sb.ToString();
        }
    }
}
