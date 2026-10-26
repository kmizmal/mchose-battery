// HID 探测工具：枚举本机 HID 接口，对指定设备做协议扫描。
// 用法：
//   HidProbe list
//   HidProbe scan  [--vid 0x41E4] [--pid 0x2001] [--usage 0xFF02]
//   HidProbe info  --vid 0x41E4
//   HidProbe raw   --path "\\?\hid#..." --hex 4D010100... [--ms 500]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HidProbe
{
    internal static class Native
    {
        internal const uint DigcfPresent = 0x02;
        internal const uint DigcfDeviceInterface = 0x10;
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x01;
        internal const uint FileShareWrite = 0x02;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const int ErrorIoPending = 997;
        internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(IntPtr device, ref HiddAttributes a);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr pp);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr pp);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr pp, ref HidpCaps caps);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetProductString(IntPtr d, IntPtr buf, int len);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetManufacturerString(IntPtr d, IntPtr buf, int len);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetSerialNumberString(IntPtr d, IntPtr buf, int len);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetFeature(IntPtr d, byte[] buf, int len);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetFeature(IntPtr d, byte[] buf, int len);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetOutputReport(IntPtr d, byte[] buf, int len);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort capsLength, IntPtr pp);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool DeviceIoControl(
            IntPtr h, uint ioctl, IntPtr inBuf, int inLen,
            IntPtr outBuf, int outLen, ref int bytesReturned, IntPtr overlapped);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SpDeviceInterfaceData did);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr set, ref SpDeviceInterfaceData did, IntPtr detail, int size, out int required, IntPtr devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool ReadFile(
            IntPtr f, IntPtr buf, int n, IntPtr read, ref NativeOverlapped ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool WriteFile(
            IntPtr f, byte[] buf, int n, IntPtr written, ref NativeOverlapped ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool GetOverlappedResult(
            IntPtr f, ref NativeOverlapped ov, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CancelIo(IntPtr f);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool WaitForSingleObjectIs(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr h, int ms);
    }

    internal sealed class Dev
    {
        public string Path;
        public int Vid;
        public int Pid;
        public int UsagePage;
        public int Usage;
        public int InLen;
        public int OutLen;
        public int FeatLen;
        public string Product = "";
        public string Manufacturer = "";
        public string Serial = "";
        public int Interface = -1;
    }

    internal static class Program
    {
        private static int ParseHex(string s)
        {
            if (s == null) return -1;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            int v;
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static List<Dev> Enumerate()
        {
            var list = new List<Dev>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DigcfPresent | Native.DigcfDeviceInterface);
            if (set == Native.InvalidHandleValue) return list;

            try
            {
                for (uint i = 0; i < 256; i++)
                {
                    var did = new Native.SpDeviceInterfaceData();
                    did.CbSize = IntPtr.Size == 8 ? 32 : 28;
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did)) break;

                    int required;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal(required);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, required, out required, IntPtr.Zero))
                        {
                            path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    if (string.IsNullOrEmpty(path)) continue;

                    var d = new Dev { Path = path };
                    string low = path.ToLowerInvariant();
                    d.Vid = ExtractHex(low, "vid_");
                    d.Pid = ExtractHex(low, "pid_");
                    d.Interface = ExtractHex(low, "&mi_");
                    if (d.Interface < 0) d.Interface = ExtractHex(low, "mi_");

                    IntPtr h = Native.CreateFile(path, 0, Native.FileShareRead | Native.FileShareWrite,
                        IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                    if (h == Native.InvalidHandleValue) { list.Add(d); continue; }

                    try
                    {
                        var attr = new Native.HiddAttributes { Size = Marshal.SizeOf(typeof(Native.HiddAttributes)) };
                        if (Native.HidD_GetAttributes(h, ref attr))
                        {
                            d.Vid = attr.VendorId;
                            d.Pid = attr.ProductId;
                        }

                        IntPtr pp;
                        if (Native.HidD_GetPreparsedData(h, out pp))
                        {
                            try
                            {
                                var caps = new Native.HidpCaps();
                                if (Native.HidP_GetCaps(pp, ref caps) == 0x00110000)
                                {
                                    d.UsagePage = caps.UsagePage;
                                    d.Usage = caps.Usage;
                                    d.InLen = caps.InputReportByteLength;
                                    d.OutLen = caps.OutputReportByteLength;
                                    d.FeatLen = caps.FeatureReportByteLength;
                                }
                            }
                            finally { Native.HidD_FreePreparsedData(pp); }
                        }

                        d.Product = GetString(h, Native.HidD_GetProductString);
                        d.Manufacturer = GetString(h, Native.HidD_GetManufacturerString);
                        d.Serial = GetString(h, Native.HidD_GetSerialNumberString);
                    }
                    finally { Native.CloseHandle(h); }

                    list.Add(d);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            return list;
        }

        private delegate bool GetStringFn(IntPtr d, IntPtr buf, int len);

        private static string GetString(IntPtr h, GetStringFn fn)
        {
            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                for (int i = 0; i < 510; i += 2) Marshal.WriteInt16(buf, i, 0);
                return fn(h, buf, 510) ? Marshal.PtrToStringUni(buf) ?? "" : "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static int ExtractHex(string low, string key)
        {
            int at = low.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;
            at += key.Length;
            int end = at;
            while (end < low.Length && Uri.IsHexDigit(low[end])) end++;
            if (end == at) return -1;
            int v;
            return int.TryParse(low.Substring(at, end - at), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "<null>";
            var sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static void PrintList(List<Dev> all, int vidFilter)
        {
            Console.WriteLine("VID    PID    UP     U      I/F In  Out Feat Product / Manufacturer");
            Console.WriteLine("------------------------------------------------------------------------------");
            foreach (Dev d in all)
            {
                if (vidFilter >= 0 && d.Vid != vidFilter) continue;
                Console.WriteLine("{0}  {1}  {2}  {3}  {4}  {5}  {6}  {7}  {8} | {9}",
                    d.Vid.ToString("X4"), d.Pid.ToString("X4"),
                    d.UsagePage.ToString("X4"), d.Usage.ToString("X4"),
                    d.Interface.ToString("X2"),
                    d.InLen.ToString().PadLeft(3), d.OutLen.ToString().PadLeft(3), d.FeatLen.ToString().PadLeft(3),
                    d.Product, d.Manufacturer);
            }
        }

        private static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("用法: HidProbe list | scan | info | raw");
                return;
            }

            string cmd = args[0].ToLowerInvariant();
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length - 1; i += 2)
            {
                opts[args[i].TrimStart('-')] = args[i + 1];
            }

            int vid = opts.ContainsKey("vid") ? ParseHex(opts["vid"]) : -1;
            int pid = opts.ContainsKey("pid") ? ParseHex(opts["pid"]) : -1;
            int upage = opts.ContainsKey("usage") ? ParseHex(opts["usage"]) : -1;
            int ms = opts.ContainsKey("ms") ? int.Parse(opts["ms"], CultureInfo.InvariantCulture) : 400;

            var all = Enumerate();

            if (cmd == "g87")
            {
                // 调插件自己的 G87Battery.Query()，验证发布的那份实现
                var sw = System.Diagnostics.Stopwatch.StartNew();
                MchoseBattery.CollectResult r = MchoseBattery.Hid.G87Battery.Query(
                    opts.ContainsKey("ms") ? int.Parse(opts["ms"], CultureInfo.InvariantCulture) : 600);
                sw.Stop();
                Console.WriteLine("ok       : {0}", r.Ok);
                Console.WriteLine("percent  : {0}", r.Percent);
                Console.WriteLine("charging : {0}", r.Charging);
                Console.WriteLine("mode     : {0}", r.ConnectMode);
                Console.WriteLine("reason   : {0}", r.Reason);
                Console.WriteLine("detail   : {0}", r.Detail);
                Console.WriteLine("耗时     : {0} ms", sw.ElapsedMilliseconds);
                return;
            }

            if (cmd == "ble")
            {
                // 蓝牙档专用：读 BLE GATT Battery Service（V7 蓝牙无厂商 HID 通道）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int percent;
                string detail;
                bool ok = MchoseBattery.Hid.BleBattery.TryQuery(
                    MchoseBattery.Hid.MchoseProtocol.BluetoothVendorId,
                    MchoseBattery.Hid.MchoseProtocol.BluetoothProductId,
                    out percent, out detail);
                sw.Stop();
                Console.WriteLine("ok      : {0}", ok);
                Console.WriteLine("percent : {0}", percent);
                Console.WriteLine("detail  : {0}", detail);
                Console.WriteLine("耗时    : {0} ms", sw.ElapsedMilliseconds);
                return;
            }

            if (cmd == "v7")
            {
                // 调插件自己的 HidBattery.Query()，验证发布的那份实现
                var sw = System.Diagnostics.Stopwatch.StartNew();
                MchoseBattery.CollectResult r = MchoseBattery.Hid.HidBattery.Query(
                    opts.ContainsKey("ms") ? int.Parse(opts["ms"], CultureInfo.InvariantCulture) : 600,
                    opts.ContainsKey("budget") ? int.Parse(opts["budget"], CultureInfo.InvariantCulture) : 8000);
                sw.Stop();
                Console.WriteLine("ok       : {0}", r.Ok);
                Console.WriteLine("percent  : {0}", r.Percent);
                Console.WriteLine("charging : {0}", r.Charging);
                Console.WriteLine("mode     : {0}", r.ConnectMode);
                Console.WriteLine("reason   : {0}", r.Reason);
                Console.WriteLine("detail   : {0}", r.Detail);
                Console.WriteLine("耗时     : {0} ms", sw.ElapsedMilliseconds);
                return;
            }

            if (cmd == "list")
            {
                PrintList(all, vid);
                return;
            }

            if (cmd == "info")
            {
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    Console.WriteLine("path   : {0}", d.Path);
                    Console.WriteLine("vid/pid: {0:X4}/{1:X4}  usagePage {2:X4} usage {3:X4} mi {4:X2}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface);
                    Console.WriteLine("caps   : in={0} out={1} feat={2}", d.InLen, d.OutLen, d.FeatLen);
                    Console.WriteLine("product: {0}", d.Product);
                    Console.WriteLine("vendor : {0}", d.Manufacturer);
                    Console.WriteLine("serial : {0}", d.Serial);
                    Console.WriteLine();
                }
                return;
            }

            if (cmd == "raw")
            {
                string path = opts.ContainsKey("path") ? opts["path"] : null;
                if (string.IsNullOrEmpty(path))
                {
                    Console.WriteLine("需要 --path");
                    return;
                }
                byte[] frame = ParseBytes(opts.ContainsKey("hex") ? opts["hex"] : "");
                using (var ch = Channel.Open(path))
                {
                    if (!ch.Ok) { Console.WriteLine("打开失败"); return; }
                    Console.WriteLine("send   : {0}", Hex(frame));
                    ch.Write(frame);
                    for (int i = 0; i < 8; i++)
                    {
                        byte[] r = ch.Read(ms);
                        if (r == null) break;
                        Console.WriteLine("recv[{0}]: {1}", i, Hex(r));
                    }
                }
                return;
            }

            if (cmd == "find")
            {
                // find --vid 0x41E4 --pid 0x2201 --usage 0xFF00 --feat 520
                // 按条件定位接口并打印可用的设备路径，避免手拼路径字符串
                int fpid = opts.ContainsKey("pid") ? ParseHex(opts["pid"]) : -1;
                int uusage = opts.ContainsKey("usage") ? ParseHex(opts["usage"]) : -1;
                int ffeat = opts.ContainsKey("feat")
                    ? int.Parse(opts["feat"], CultureInfo.InvariantCulture) : -1;
                int fin = opts.ContainsKey("in")
                    ? int.Parse(opts["in"], CultureInfo.InvariantCulture) : -1;
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (fpid >= 0 && d.Pid != fpid) continue;
                    if (uusage >= 0 && d.UsagePage != uusage) continue;
                    if (ffeat >= 0 && d.FeatLen != ffeat) continue;
                    if (fin >= 0 && d.InLen != fin) continue;
                    Console.WriteLine("path: {0}", d.Path);
                    Console.WriteLine("  {0:X4}/{1:X4} UP={2:X4} U={3:X4} mi={4:X2} in={5} out={6} feat={7} | {8}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface,
                        d.InLen, d.OutLen, d.FeatLen, d.Product);
                }
                return;
            }

            if (cmd == "setf")
            {
                // setf --path <p> --hex <feature 报告> [--len N] [--ms N]
                // 官方 legacy 通道走 Feature 报告：写出去后设备用输入报告回话
                string path = opts.ContainsKey("path") ? opts["path"] : null;
                if (string.IsNullOrEmpty(path))
                {
                    Console.WriteLine("需要 --path");
                    return;
                }
                byte[] feat = ParseBytes(opts.ContainsKey("hex") ? opts["hex"] : "");
                int flen = opts.ContainsKey("len") ? int.Parse(opts["len"], CultureInfo.InvariantCulture) : feat.Length;
                // HidD_SetFeature 要求缓冲长度与接口的 Feature 报告长度一致，
                // 首字节固定是报告 ID，其余按接口长度补零。
                var send = new byte[flen];
                Array.Copy(feat, send, Math.Min(feat.Length, flen));
                using (var ch = Channel.Open(path))
                {
                    if (!ch.Ok) { Console.WriteLine("打开失败"); return; }
                    Console.WriteLine("setfeat: {0}", Hex(send));
                    bool ok = Native.HidD_SetFeature(ch.Handle, send, flen);
                    Console.WriteLine("SetFeature: {0}", ok ? "OK" : "失败");
                    if (!ok) return;
                    // 官方 legacy 通道用 Feature 报告问、用 Feature 报告答
                    var back = new byte[flen];
                    back[0] = send[0];
                    if (Native.HidD_GetFeature(ch.Handle, back, flen))
                    {
                        Console.WriteLine("getfeat: {0}", Hex(back));
                    }
                    else
                    {
                        Console.WriteLine("GetFeature: 失败");
                    }
                    for (int i = 0; i < 8; i++)
                    {
                        byte[] r = ch.Read(ms);
                        if (r == null) break;
                        Console.WriteLine("recv[{0}]: {1}", i, Hex(r));
                    }
                }
                return;
            }

            if (cmd == "try")
            {
                // try --vid 0x41E4 --usage 0xFF02 --rid 13 --cmds 0900,0100,... [--data 00] [--len 20]
                int trid = opts.ContainsKey("rid") ? ParseHex(opts["rid"]) : 0x13;
                int tlen = opts.ContainsKey("len") ? int.Parse(opts["len"], CultureInfo.InvariantCulture) : 20;
                byte[] tdata = ParseBytes(opts.ContainsKey("data") ? opts["data"] : "");
                string[] cmds;
                if (opts.ContainsKey("cmds")) cmds = opts["cmds"].Split(',', StringSplitOptions.RemoveEmptyEntries);
                else cmds = new[] { "0900" };

                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;

                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} mi={3:X2}", d.Vid, d.Pid, d.UsagePage, d.Interface);
                    using (var ch = Channel.Open(d.Path))
                    {
                        if (!ch.Ok) { Console.WriteLine("    打开失败"); continue; }
                        foreach (string cs in cmds)
                        {
                            int c = ParseHex(cs);
                            byte[] frame = BuildFrame(trid, c, tdata, 1, tlen);
                            if (!ch.Write(frame)) { Console.WriteLine("    cmd {0:X4} 写入失败", c); continue; }
                            byte[] r = ch.Read(ms);
                            Console.WriteLine("    cmd {0:X4} send {1}", c, Hex(frame));
                            Console.WriteLine("           recv {0}", r == null ? "<无应答>" : Hex(r));
                            if (r != null)
                            {
                                byte[] r2 = ch.Read(150);
                                if (r2 != null) Console.WriteLine("           recv2 {0}", Hex(r2));
                            }
                            Thread.Sleep(30);
                        }
                    }
                }
                return;
            }

            if (cmd == "feat")
            {
                // feat --vid 0x41E4 --usage 0xFF04 [--len 65] [--rid 0x00-0xFF]
                int fl = opts.ContainsKey("len") ? int.Parse(opts["len"], CultureInfo.InvariantCulture) : 65;
                var ids = new List<int>();
                if (opts.ContainsKey("rid"))
                {
                    foreach (string part in opts["rid"].Split(','))
                    {
                        if (part.Contains("-")) { string[] ab = part.Split('-'); for (int k = ParseHex(ab[0]); k <= ParseHex(ab[1]); k++) ids.Add(k); }
                        else ids.Add(ParseHex(part));
                    }
                }
                else for (int k = 0; k <= 0xFF; k++) ids.Add(k);

                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;
                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} U={3:X4} mi={4:X2} feat={5}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface, d.FeatLen);
                    using (var ch = Channel.Open(d.Path))
                    {
                        if (!ch.Ok) { Console.WriteLine("    打开失败"); continue; }
                        foreach (int rid in ids)
                        {
                            var buf = new byte[fl];
                            buf[0] = (byte)rid;
                            if (!Native.HidD_GetFeature(ch.Handle, buf, fl)) continue;
                            bool trivial = true;
                            for (int i = 1; i < fl; i++) if (buf[i] != 0) { trivial = false; break; }
                            if (trivial && buf[0] == 0) continue;
                            Console.WriteLine("    feat {0:X2} -> {1}", rid, Hex(buf));
                        }
                    }
                }
                return;
            }

            if (cmd == "caps")
            {
                string[] kinds = { "Input", "Output", "Feature" };
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;
                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} U={3:X4} mi={4:X2} :: {5}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface, d.Product);
                    IntPtr h = Native.CreateFile(d.Path, 0, Native.FileShareRead | Native.FileShareWrite,
                        IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                    if (h == Native.InvalidHandleValue) { Console.WriteLine("    打开失败"); continue; }
                    try
                    {
                        IntPtr pp;
                        if (!Native.HidD_GetPreparsedData(h, out pp)) { Console.WriteLine("    无 preparsed"); continue; }
                        try
                        {
                            for (int rt = 0; rt < 3; rt++)
                            {
                                ushort cnt = 64;
                                IntPtr cbuf = Marshal.AllocHGlobal(72 * cnt);
                                try
                                {
                                    int st = Native.HidP_GetValueCaps(rt, cbuf, ref cnt, pp);
                                    if (st != 0x00110000) { Console.WriteLine("    {0}: 无值字段 (0x{1:X})", kinds[rt], st); continue; }
                                    Console.WriteLine("    {0}: {1} 个值字段", kinds[rt], cnt);
                                    for (int n = 0; n < cnt; n++)
                                    {
                                        IntPtr p = IntPtr.Add(cbuf, 72 * n);
                                        int up = Marshal.ReadInt16(p, 0);
                                        int rid = Marshal.ReadByte(p, 2);
                                        ushort bitSize = (ushort)Marshal.ReadInt16(p, 18);
                                        ushort repCnt = (ushort)Marshal.ReadInt16(p, 20);
                                        int lmin = Marshal.ReadInt32(p, 40);
                                        int lmax = Marshal.ReadInt32(p, 44);
                                        int isRange = Marshal.ReadByte(p, 12);
                                        int usageOrMin = Marshal.ReadInt16(p, 56);
                                        int usageMax = Marshal.ReadInt16(p, 58);
                                        Console.WriteLine("       rid=0x{0:X2} up=0x{1:X4} usage=0x{2:X4}{3} bits={4} count={5} range=[{6},{7}]",
                                            rid, up, usageOrMin,
                                            isRange != 0 ? "-0x" + usageMax.ToString("X4") : "",
                                            bitSize, repCnt, lmin, lmax);
                                    }
                                }
                                finally { Marshal.FreeHGlobal(cbuf); }
                            }
                        }
                        finally { Native.HidD_FreePreparsedData(pp); }
                    }
                    finally { Native.CloseHandle(h); }
                    Console.WriteLine();
                }
                return;
            }

            if (cmd == "sweep")
            {
                int rid = opts.ContainsKey("rid") ? ParseHex(opts["rid"]) : 0x06;
                int idx = opts.ContainsKey("idx") ? int.Parse(opts["idx"], CultureInfo.InvariantCulture) : 1;
                int nb = opts.ContainsKey("n") ? int.Parse(opts["n"], CultureInfo.InvariantCulture) : 8;
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;
                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} mi={3:X2} rid=0x{4:X2} idx={5}",
                        d.Vid, d.Pid, d.UsagePage, d.Interface, rid, idx);
                    using (var ch = Channel.Open(d.Path))
                    {
                        if (!ch.Ok) { Console.WriteLine("    打开失败"); continue; }
                        var baseBuf = new byte[nb]; baseBuf[0] = (byte)rid;
                        Native.HidD_GetFeature(ch.Handle, baseBuf, nb);
                        string baseline = Hex(baseBuf);
                        Console.WriteLine("    baseline {0}", baseline);
                        for (int v = 0; v <= 0xFF; v++)
                        {
                            var o = new byte[nb]; o[0] = (byte)rid; o[idx] = (byte)v;
                            if (!Native.HidD_SetFeature(ch.Handle, o, nb)) continue;
                            var i2 = new byte[nb]; i2[0] = (byte)rid;
                            Native.HidD_GetFeature(ch.Handle, i2, nb);
                            string now = Hex(i2);
                            if (now != baseline)
                            {
                                Console.WriteLine("    set[{0}]={1:X2} -> {2}", idx, v, now);
                                baseline = now;
                            }
                        }
                    }
                }
                return;
            }

            if (cmd == "desc")
            {
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;
                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} U={3:X4} mi={4:X2} :: {5}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface, d.Product);
                    IntPtr h = Native.CreateFile(d.Path, 0, Native.FileShareRead | Native.FileShareWrite,
                        IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                    if (h == Native.InvalidHandleValue) { Console.WriteLine("    打开失败"); continue; }
                    try
                    {
                        int len = 4096;
                        IntPtr buf = IntPtr.Zero;
                        byte[] desc = null;
                        try
                        {
                            buf = Marshal.AllocHGlobal(len);
                            // IOCTL_HID_GET_REPORT_DESCRIPTOR：FILE_DEVICE_KEYBOARD=0x0B，
                            // 暴力试 function/method 组合，用描述符首字节签名校验。
                            for (int fn = 0; fn <= 15 && desc == null; fn++)
                            {
                                for (int m = 3; m >= 0 && desc == null; m--)
                                {
                                    uint code = (0x0B << 16) | (uint)(fn << 2) | (uint)m;
                                    int outLen = 0;
                                    for (int k = 0; k < len; k += 2) Marshal.WriteInt16(buf, k, 0);
                                    bool ok = Native.DeviceIoControl(h, code, IntPtr.Zero, 0, buf, len, ref outLen, IntPtr.Zero);
                                    if (!ok || outLen < 8) continue;
                                    byte b0 = Marshal.ReadByte(buf, 0);
                                    if (b0 != 0x05 && b0 != 0x06 && b0 != 0x09 && b0 != 0x85) continue;
                                    desc = new byte[outLen];
                                    Marshal.Copy(buf, desc, 0, outLen);
                                    Console.WriteLine("    IOCTL 0x{0:X8}", code);
                                }
                            }
                            if (desc == null)
                            {
                                Console.WriteLine("    未能取到报告描述符");
                                continue;
                            }
                            Console.WriteLine("    长度 {0}: {1}", desc.Length, Hex(desc));
                            Console.WriteLine("    --- 解析 ---");
                            Describe(desc);
                        }
                        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
                    }
                    finally { Native.CloseHandle(h); }
                    Console.WriteLine();
                }
                return;
            }

            if (cmd == "scan")
            {
                var hits = new List<Dev>();
                foreach (Dev d in all)
                {
                    if (vid >= 0 && d.Vid != vid) continue;
                    if (pid >= 0 && d.Pid != pid) continue;
                    if (upage >= 0 && d.UsagePage != upage) continue;
                    hits.Add(d);
                }
                Console.WriteLine("命中 {0} 个接口", hits.Count);

                foreach (Dev d in hits)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== {0:X4}/{1:X4} UP={2:X4} U={3:X4} mi={4:X2} in={5} out={6} feat={7} :: {8}",
                        d.Vid, d.Pid, d.UsagePage, d.Usage, d.Interface, d.InLen, d.OutLen, d.FeatLen, d.Product);
                    Console.WriteLine("    {0}", d.Path);

                    using (var ch = Channel.Open(d.Path))
                    {
                        if (!ch.Ok)
                        {
                            Console.WriteLine("    [!] 打开失败 (共享冲突或权限)");
                            continue;
                        }

                        // 1) 被动监听：看设备是否自发上报
                        Console.WriteLine("    -- 被动监听 1200ms --");
                        int seen = 0;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 1200 && seen < 6)
                        {
                            byte[] r = ch.Read(300);
                            if (r == null) continue;
                            Console.WriteLine("    spont: {0}", Hex(r));
                            seen++;
                        }

                        // 2) 主动探测：多报告 ID × 命令 0x0900
                        int flen = opts.ContainsKey("len") ? int.Parse(opts["len"], CultureInfo.InvariantCulture) : 64;
                        string ridsSpec = opts.ContainsKey("rid") ? opts["rid"] : null;
                        int[] reportIds;
                        if (!string.IsNullOrEmpty(ridsSpec))
                        {
                            var tmp = new List<int>();
                            foreach (string part in ridsSpec.Split(','))
                            {
                                if (part.Contains("-"))
                                {
                                    string[] ab = part.Split('-');
                                    int a = ParseHex(ab[0]), b = ParseHex(ab[1]);
                                    for (int k = a; k <= b; k++) tmp.Add(k);
                                }
                                else tmp.Add(ParseHex(part));
                            }
                            reportIds = tmp.ToArray();
                        }
                        else
                        {
                            reportIds = new[] { 0x4D, 0x11, 0x08, 0x07, 0x06, 0x09, 0x0A, 0x0C, 0x0D, 0x12, 0x5A, 0xF0, 0x01 };
                        }

                        Console.WriteLine("    -- 主动探测: 帧长 {0}, rid {1} 个 --", flen, reportIds.Length);
                        int maxRid = Math.Min(reportIds.Length, 300);
                        foreach (int rid in reportIds)
                        {
                            byte[] frame = BuildFrame(rid, 0x0900, null, 1, flen);
                            if (!ch.Write(frame)) continue;
                            byte[] r = ch.Read(ms);
                            if (r != null)
                            {
                                Console.WriteLine("    rid {0:X2} cmd 0900 -> {1}", rid, Hex(r));
                                byte[] more = ch.Read(150);
                                if (more != null) Console.WriteLine("             + {0}", Hex(more));
                            }
                            Thread.Sleep(10);
                        }

                        // 3) 命令扫描（固定 0x4D）
                        Console.WriteLine("    -- cmd 扫描 (rid 4D, 帧长 {0}) --", flen);
                        int[] cmds = { 0x0900, 0x0100, 0x0200, 0x0300, 0x0400, 0x0500, 0x0A00, 0x0001, 0x0002, 0x0003 };
                        foreach (int c in cmds)
                        {
                            byte[] frame = BuildFrame(0x4D, c, null, 1, flen);
                            if (!ch.Write(frame)) continue;
                            byte[] r = ch.Read(250);
                            if (r != null) Console.WriteLine("    cmd {0:X4} -> {1}", c, Hex(r));
                            Thread.Sleep(10);
                        }

                        // 4) Feature 读取
                        if (d.FeatLen > 0)
                        {
                            int len = Math.Max(d.FeatLen, 65);
                            Console.WriteLine("    -- SetFeature + GetFeature (len {0}) --", len);
                            foreach (int rid in new[] { 0x11, 0x4D, 0x08, 0x06, 0x01, 0x07, 0x02, 0x03, 0x04, 0x05, 0x0A, 0x0C, 0x0D, 0x12, 0x5A })
                            {
                                var outBuf = new byte[len];
                                outBuf[0] = (byte)rid;
                                outBuf[1] = 0x01;
                                outBuf[2] = 0x01;
                                outBuf[4] = 0x00;   // cmd 0x0900 LE
                                outBuf[5] = 0x09;
                                byte c = 0;
                                for (int i = 2; i < 8; i++) c ^= outBuf[i];
                                outBuf[8] = c;

                                bool w = Native.HidD_SetFeature(ch.Handle, outBuf, len);
                                byte[] r = null;
                                for (int k = 0; k < 3; k++)
                                {
                                    r = ch.Read(200);
                                    if (r != null) break;
                                }
                                var inBuf = new byte[len];
                                inBuf[0] = (byte)rid;
                                bool g = Native.HidD_GetFeature(ch.Handle, inBuf, len);
                                if (w || r != null || g)
                                {
                                    Console.WriteLine("    feat {0:X2} set={1} get={2} -> {3}",
                                        rid, w, g, g ? Hex(inBuf) : "-");
                                    if (r != null) Console.WriteLine("         read -> {0}", Hex(r));
                                }
                                Thread.Sleep(10);
                            }
                        }

                        // 5) SetOutputReport 通道（部分厂商设备只认这个）
                        if (d.OutLen > 0)
                        {
                            Console.WriteLine("    -- SetOutputReport (len {0}) --", d.OutLen);
                            foreach (int rid in new[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x11, 0x4D })
                            {
                                var b = new byte[d.OutLen];
                                b[0] = (byte)rid;
                                b[1] = 0x01;
                                b[2] = 0x01;
                                b[4] = 0x00;
                                b[5] = 0x09;
                                byte c = 0;
                                for (int i = 2; i < Math.Min(8, d.OutLen); i++) c ^= b[i];
                                if (8 < d.OutLen) b[8] = c;
                                if (!Native.HidD_SetOutputReport(ch.Handle, b, b.Length)) continue;
                                byte[] r = ch.Read(250);
                                if (r != null) Console.WriteLine("    sout {0:X2} -> {1}", rid, Hex(r));
                                Thread.Sleep(10);
                            }
                        }
                    }
                }
            }
        }

        private static string[] MainTags = { "Input", "Output", "Collection", "Feature", "EndCollection" };
        private static string[] GlobalTags =
        {
            "UsagePage", "LogicalMin", "LogicalMax", "PhysicalMin", "PhysicalMax",
            "UnitExp", "Unit", "ReportSize", "ReportID", "ReportCount", "Push", "Pop"
        };
        private static string[] LocalTags =
        {
            "Usage", "UsageMin", "UsageMax", "DesignatorIdx", "DesignatorMin", "DesignatorMax",
            "StringIdx", "StringMin", "StringMax", "Delimiter"
        };

        private static void Describe(byte[] d)
        {
            int i = 0;
            int indent = 0;
            while (i < d.Length)
            {
                byte prefix = d[i++];
                if (prefix == 0xFE)
                {
                    if (i + 2 > d.Length) break;
                    int sz = d[i], tg = d[i + 1];
                    i += 2;
                    Console.WriteLine("        [long tag {0:X2} size {1}]", tg, sz);
                    i += sz;
                    continue;
                }
                int bSize = prefix & 0x03;
                int bType = (prefix >> 2) & 0x03;
                int bTag = (prefix >> 4) & 0x0F;
                int n = bSize == 3 ? 4 : bSize;
                if (i + n > d.Length) break;
                uint val = 0;
                for (int k = 0; k < n; k++) val |= (uint)d[i + k] << (8 * k);
                string data = "";
                for (int k = 0; k < n; k++) data += d[i + k].ToString("X2") + " ";
                i += n;

                string name;
                if (bType == 0) name = bTag < MainTags.Length ? MainTags[bTag] : "Main?" + bTag;
                else if (bType == 1) name = bTag < GlobalTags.Length ? GlobalTags[bTag] : "Global?" + bTag;
                else if (bType == 2) name = bTag < LocalTags.Length ? LocalTags[bTag] : "Local?" + bTag;
                else name = "Resv?" + bTag;

                if (bTag == 0xA0 && bType == 0) indent += 2;
                if (bTag == 0xC0 && bType == 0) indent = Math.Max(0, indent - 2);
                Console.WriteLine("        {0}{1,-14} {2}  (0x{3:X})",
                    new string(' ', indent), name, data.TrimEnd(), val);
            }
        }

        private static byte[] ParseBytes(string hex)
        {
            var list = new List<byte>();
            string s = hex.Replace(" ", "").Replace("-", "").Replace(",", "");
            for (int i = 0; i + 1 < s.Length; i += 2)
            {
                byte b;
                if (byte.TryParse(s.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    list.Add(b);
            }
            return list.ToArray();
        }

        /// <summary>与 V7 插件同构的帧：rid, 0x01, flags, len, cmdLo, cmdHi, 0, 0, data…, xor。</summary>
        private static byte[] BuildFrame(int reportId, int commandId, byte[] data, byte flags, int len = 64)
        {
            data = data ?? new byte[0];
            if (len < 9) len = 64;
            var frame = new byte[len];
            frame[0] = (byte)reportId;
            frame[1] = 0x01;
            frame[2] = flags;
            frame[3] = (byte)data.Length;
            frame[4] = (byte)(commandId & 0xFF);
            frame[5] = (byte)((commandId >> 8) & 0xFF);
            frame[6] = 0;
            frame[7] = 0;
            Array.Copy(data, 0, frame, 8, data.Length);
            if (flags == 1)
            {
                byte c = 0;
                for (int i = 2; i < 8 + data.Length; i++) c ^= frame[i];
                frame[8 + data.Length] = c;
            }
            return frame;
        }

        private sealed class Channel : IDisposable
        {
            private IntPtr _h = Native.InvalidHandleValue;
            private IntPtr _evt = IntPtr.Zero;
            public bool Ok;
            public IntPtr Handle { get { return _h; } }

            public static Channel Open(string path)
            {
                var c = new Channel();
                c._h = Native.CreateFile(path,
                    Native.GenericRead | Native.GenericWrite,
                    Native.FileShareRead | Native.FileShareWrite,
                    IntPtr.Zero, Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
                if (c._h == Native.InvalidHandleValue)
                {
                    c._h = Native.CreateFile(path, 0,
                        Native.FileShareRead | Native.FileShareWrite,
                        IntPtr.Zero, Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
                }
                c.Ok = c._h != Native.InvalidHandleValue;
                if (c.Ok) c._evt = Native.CreateEvent(IntPtr.Zero, false, false, null);
                return c;
            }

            public bool Write(byte[] frame)
            {
                if (_h == Native.InvalidHandleValue) return false;
                var ov = new Native.NativeOverlapped { EventHandle = _evt };
                bool ok = Native.WriteFile(_h, frame, frame.Length, IntPtr.Zero, ref ov);
                if (!ok && Marshal.GetLastWin32Error() != Native.ErrorIoPending) return false;
                Native.WaitForSingleObject(_evt, 300);
                return true;
            }

            public byte[] Read(int timeoutMs)
            {
                if (_h == Native.InvalidHandleValue) return null;
                int len = 128;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    var ov = new Native.NativeOverlapped { EventHandle = _evt };
                    bool ok = Native.ReadFile(_h, buf, len, IntPtr.Zero, ref ov);
                    if (!ok)
                    {
                        if (Marshal.GetLastWin32Error() != Native.ErrorIoPending) return null;
                        uint w = Native.WaitForSingleObject(_evt, timeoutMs);
                        if (w != 0) { Native.CancelIo(_h); return null; }
                    }
                    int n;
                    if (!Native.GetOverlappedResult(_h, ref ov, out n, true)) return null;
                    if (n <= 0) return null;
                    var res = new byte[n];
                    Marshal.Copy(buf, res, 0, n);
                    return res;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            public void Dispose()
            {
                if (_h != Native.InvalidHandleValue) { Native.CloseHandle(_h); _h = Native.InvalidHandleValue; }
                if (_evt != IntPtr.Zero) { Native.CloseHandle(_evt); _evt = IntPtr.Zero; }
            }
        }
    }
}
