using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MchoseBattery.Hid
{
    /// <summary>一个匹配到的 HID 接口。</summary>
    internal sealed class DeviceTarget
    {
        public string Path;
        public int ProductId;
        public int UsagePage;
        public int Usage;
        public int InterfaceNumber = -1;
        public string Product = string.Empty;

        /// <summary>该接口是否走"有线"通道（Feature 报告）。G87 专用。</summary>
        public int VendorId;

        /// <summary>该接口来自蓝牙（BLE HID）身份，而非 USB 接收器/有线身份。</summary>
        public bool IsBluetooth;

        public bool IsWiredChannel;
    }

    /// <summary>
    /// V7 电量读取：枚举 → 逐个接口查询 → 0x4D 命令通道 → legacy 0x11 回退。
    /// 移植自官方 Python 脚本，逻辑与偏移保持一致。
    /// </summary>
    internal static class HidBattery
    {
        private const int RetryLimit = 6;
        private const int ReadBufferLen = 65;

        /// <summary>按 VID + UsagePage 自动发现，不写死 PID。</summary>
        internal static List<DeviceTarget> EnumerateTargets()
        {
            var targets = new List<DeviceTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Guid hidGuid;
            NativeHid.HidD_GetHidGuid(out hidGuid);

            IntPtr set = NativeHid.SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                NativeHid.DigcfPresent | NativeHid.DigcfDeviceInterface);
            if (set == NativeHid.InvalidHandleValue)
            {
                return targets;
            }

            try
            {
                var did = new NativeHid.SpDeviceInterfaceData();
                did.CbSize = Marshal.SizeOf(typeof(NativeHid.SpDeviceInterfaceData));

                uint index = 0;
                while (true)
                {
                    did.CbSize = Marshal.SizeOf(typeof(NativeHid.SpDeviceInterfaceData));
                    if (!NativeHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did))
                    {
                        break;
                    }
                    index++;

                    string path = GetDevicePath(set, ref did);
                    if (path == null || !seen.Add(path))
                    {
                        continue;
                    }

                    DeviceTarget target;
                    if (TryInspect(path, out target))
                    {
                        targets.Add(target);
                    }
                }
            }
            finally
            {
                NativeHid.SetupDiDestroyDeviceInfoList(set);
            }

            targets.Sort(CompareTargets);
            return targets;
        }

        private static int CompareTargets(DeviceTarget a, DeviceTarget b)
        {
            int aPidRank = Array.IndexOf(MchoseProtocol.ProductIds, a.ProductId) >= 0 ? 0 : 1;
            int bPidRank = Array.IndexOf(MchoseProtocol.ProductIds, b.ProductId) >= 0 ? 0 : 1;
            if (aPidRank != bPidRank)
            {
                return aPidRank - bPidRank;
            }

            int aPageRank = a.UsagePage == MchoseProtocol.UsagePages[0] ? 0 : 1;
            int bPageRank = b.UsagePage == MchoseProtocol.UsagePages[0] ? 0 : 1;
            if (aPageRank != bPageRank)
            {
                return aPageRank - bPageRank;
            }

            return a.InterfaceNumber.CompareTo(b.InterfaceNumber);
        }

        private static string GetDevicePath(IntPtr set, ref NativeHid.SpDeviceInterfaceData did)
        {
            int required;
            NativeHid.SetupDiGetDeviceInterfaceDetail(
                set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                // cbSize 在 x64 是 8，x86 是 6（DWORD + 对齐后的 WCHAR）
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeHid.SetupDiGetDeviceInterfaceDetail(
                        set, ref did, buffer, required, out required, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>检查 VID / UsagePage，命中时填充 target。</summary>
        private static bool TryInspect(string path, out DeviceTarget target)
        {
            target = null;
            IntPtr handle = NativeHid.CreateFile(
                path,
                0,  // 仅查询属性，不需要读写权限
                NativeHid.FileShareRead | NativeHid.FileShareWrite,
                IntPtr.Zero, NativeHid.OpenExisting, 0, IntPtr.Zero);
            if (handle == NativeHid.InvalidHandleValue)
            {
                return false;
            }

            try
            {
                var attrs = new NativeHid.HiddAttributes();
                attrs.Size = Marshal.SizeOf(typeof(NativeHid.HiddAttributes));
                if (!NativeHid.HidD_GetAttributes(handle, ref attrs))
                {
                    return false;
                }
                if (attrs.VendorId != MchoseProtocol.VendorId)
                {
                    return false;
                }

                IntPtr preparsed;
                if (!NativeHid.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
                {
                    return false;
                }

                int usagePage;
                int usage;
                try
                {
                    var caps = new NativeHid.HidpCaps();
                    caps.Reserved = new ushort[17];
                    if (NativeHid.HidP_GetCaps(preparsed, ref caps) != NativeHid.HidpStatusSuccess)
                    {
                        return false;
                    }
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                }
                finally
                {
                    NativeHid.HidD_FreePreparsedData(preparsed);
                }

                if (Array.IndexOf(MchoseProtocol.UsagePages, usagePage) < 0)
                {
                    return false;
                }

                target = new DeviceTarget
                {
                    Path = path,
                    ProductId = attrs.ProductId,
                    UsagePage = usagePage,
                    Usage = usage,
                    InterfaceNumber = ParseInterface(path),
                };
                return true;
            }
            finally
            {
                NativeHid.CloseHandle(handle);
            }
        }

        private static readonly Regex MiPattern = new Regex(@"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);

        private static int ParseInterface(string path)
        {
            Match m = MiPattern.Match(path ?? string.Empty);
            if (m.Success)
            {
                int value;
                if (int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out value))
                {
                    return value;
                }
            }
            return -1;
        }

        /// <summary>完整查询：遍历候选接口，任一成功即返回。</summary>
        internal static CollectResult Query(int windowMs, int budgetMs)
        {
            var sw = Stopwatch.StartNew();
            List<DeviceTarget> targets;
            try
            {
                targets = EnumerateTargets();
            }
            catch (Exception ex)
            {
                return new CollectResult { Ok = false, Reason = "exception", Detail = ex.Message };
            }

            if (targets.Count == 0)
            {
                // 蓝牙档可能连 USB 厂商接口都不再枚举，直接试 BLE。
                CollectResult bluetooth = QueryBluetooth();
                if (bluetooth != null)
                {
                    return bluetooth;
                }

                return new CollectResult { Ok = false, Reason = "absent", Detail = "未找到 VID_3837 的厂商接口" };
            }

            string lastDetail = null;
            foreach (DeviceTarget target in targets)
            {
                if (budgetMs > 0 && sw.ElapsedMilliseconds > budgetMs)
                {
                    lastDetail = "超出采集预算";
                    break;
                }

                BatteryResult result;
                string note;
                try
                {
                    result = QueryTarget(target, windowMs, out note);
                }
                catch (Exception ex)
                {
                    lastDetail = "接口打开失败：" + ex.Message;
                    continue;
                }

                if (result != null)
                {
                    return new CollectResult
                    {
                        Ok = true,
                        Percent = result.Percent,
                        Charging = result.Charging,
                        ConnectMode = result.ConnectMode,
                        Reason = "ok",
                        Detail = result.Channel,
                    };
                }

                lastDetail = note;
            }

            // 切到蓝牙档后 USB 厂商接口通常仍在枚举，但整条链路不再代答；
            // 此时 0x4D 帧无处可发，改读 BLE GATT 电池。
            CollectResult viaBle = QueryBluetooth();
            if (viaBle != null)
            {
                return viaBle;
            }

            return new CollectResult { Ok = false, Reason = "no_response", Detail = lastDetail };
        }

        /// <summary>
        /// 蓝牙档回退：HID 层没有厂商集合，只能读 BLE GATT Battery Service。
        /// 未命中蓝牙身份时返回 null，交回调用方给出原有错误文案。
        /// </summary>
        private static CollectResult QueryBluetooth()
        {
            int percent;
            string detail;
            if (!BleBattery.TryQuery(
                    MchoseProtocol.BluetoothVendorId, MchoseProtocol.BluetoothProductId,
                    out percent, out detail))
            {
                return null;
            }

            return new CollectResult
            {
                Ok = true,
                Percent = percent,
                Charging = false,
                // GATT 只有百分比，没有充放电方向。
                ChargingKnown = false,
                ConnectMode = "蓝牙",
                Reason = "ok",
                Detail = detail,
            };
        }

        /// <summary>对单个接口做 0x4D 命令 + legacy 回退。</summary>
        private static BatteryResult QueryTarget(DeviceTarget target, int windowMs, out string note)
        {
            note = null;
            using (var channel = HidChannel.Open(target.Path))
            {
                byte[] frame = MchoseProtocol.BuildFrame(MchoseProtocol.CmdDeviceInfo);
                for (int attempt = 0; attempt < RetryLimit; attempt++)
                {
                    if (!channel.Write(frame))
                    {
                        note = "输出报告 0x4D 写入失败";
                        break;
                    }

                    byte[] payload;
                    if (channel.WaitReply(MchoseProtocol.CmdDeviceInfo, windowMs, out payload))
                    {
                        // 短应答 = 该接口不实现 0x0900（实测：鼠标切有线时本体接口
                        // 回 `FE F7` 错误码，dataLen=1）。这时立刻换下一个接口，
                        // 不做无谓重发。
                        if (MchoseProtocol.IsErrorReply(payload))
                        {
                            note = "接口不实现 0x0900";
                            break;
                        }

                        BatteryResult parsed = MchoseProtocol.ParseDeviceInfo(payload);
                        if (parsed == null)
                        {
                            note = "应答过短";
                            continue;
                        }
                        parsed.Channel = $"Report 0x{MchoseProtocol.ReportId:X2} · cmd 0x{MchoseProtocol.CmdDeviceInfo:X4}";
                        parsed.Raw = BitConverter.ToString(payload);

                        // 连接方式按官方规则判定：productId 是否落在官方 nT（无线名单）。
                        // 官方鼠标页原文：connectMode = nT.includes(productId) ? 1 : 0
                        // 注意不是"哪个接口应答"——本体(0x1016)本身就是无线身份。
                        parsed.ConnectMode = MchoseProtocol.ConnectModeFor(target.ProductId);
                        return parsed;
                    }
                }

                // 前代通道回退
                byte[] order = MchoseProtocol.LegacyOrder();
                var report = new byte[order.Length];
                for (int i = 0; i < order.Length; i++)
                {
                    report[i] = (byte)(order[i] ^ MchoseProtocol.XorMask);
                }
                if (channel.SetFeature(report))
                {
                    var deadline = Stopwatch.StartNew();
                    while (deadline.ElapsedMilliseconds < windowMs)
                    {
                        byte[] raw = channel.Read((int)Math.Max(10, windowMs - deadline.ElapsedMilliseconds));
                        if (raw == null)
                        {
                            continue;
                        }
                        BatteryResult legacy = MchoseProtocol.ParseLegacy(raw);
                        if (legacy != null)
                        {
                            legacy.Channel = $"Feature 0x{MchoseProtocol.LegacyReportId:X2}（legacy）";
                            legacy.Raw = BitConverter.ToString(raw);
                            return legacy;
                        }
                    }
                    note = "legacy 0x11 无应答";
                }
                else
                {
                    note = "SetFeature(0x11) 不可用";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 一个已打开的 HID 通道。所有 I/O 都用 OVERLAPPED + 事件等待做超时，
    /// 避免同步 ReadFile 在设备休眠时无限阻塞后台线程。
    /// </summary>
    internal sealed class HidChannel : IDisposable
    {
        private IntPtr _handle = NativeHid.InvalidHandleValue;
        private IntPtr _readEvent = IntPtr.Zero;
        private IntPtr _writeEvent = IntPtr.Zero;

        private HidChannel()
        {
        }

        internal static HidChannel Open(string path)
        {
            var channel = new HidChannel();
            channel._handle = NativeHid.CreateFile(
                path,
                NativeHid.GenericRead | NativeHid.GenericWrite,
                NativeHid.FileShareRead | NativeHid.FileShareWrite,
                IntPtr.Zero,
                NativeHid.OpenExisting,
                NativeHid.FileFlagOverlapped,
                IntPtr.Zero);
            if (channel._handle == NativeHid.InvalidHandleValue)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateFile 失败，Win32 错误 {err}");
            }

            channel._readEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            channel._writeEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            return channel;
        }

        internal bool Write(byte[] buffer)
        {
            if (_handle == NativeHid.InvalidHandleValue || _writeEvent == IntPtr.Zero)
            {
                return false;
            }

            NativeHid.ResetEvent(_writeEvent);
            var ov = new NativeHid.NativeOverlapped { EventHandle = _writeEvent };
            int written;

            bool ok = NativeHid.WriteFile(_handle, buffer, buffer.Length, IntPtr.Zero, ref ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != NativeHid.ErrorIoPending)
                {
                    return false;
                }
                uint wait = NativeHid.WaitForSingleObject(_writeEvent, 2000);
                if (wait != NativeHid.WaitObject0)
                {
                    NativeHid.CancelIo(_handle);
                    return false;
                }
            }

            return NativeHid.GetOverlappedResult(_handle, ref ov, out written, false)
                   && written >= buffer.Length - 1;
        }

        internal byte[] Read(int timeoutMs)
        {
            if (_handle == NativeHid.InvalidHandleValue || _readEvent == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new byte[65];
            NativeHid.ResetEvent(_readEvent);
            var ov = new NativeHid.NativeOverlapped { EventHandle = _readEvent };
            int read;

            bool ok = NativeHid.ReadFile(_handle, buffer, buffer.Length, IntPtr.Zero, ref ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != NativeHid.ErrorIoPending)
                {
                    return null;
                }
                uint wait = NativeHid.WaitForSingleObject(_readEvent, Math.Max(10, timeoutMs));
                if (wait != NativeHid.WaitObject0)
                {
                    NativeHid.CancelIo(_handle);
                    return null;
                }
            }

            if (!NativeHid.GetOverlappedResult(_handle, ref ov, out read, false) || read <= 0)
            {
                return null;
            }

            var result = new byte[read];
            Array.Copy(buffer, result, read);
            return result;
        }

        /// <summary>在窗口内等待指定命令的应答；首字节 0xFF 视为设备忙，继续等。</summary>
        internal bool WaitReply(int expectCmd, int windowMs, out byte[] payload)
        {
            payload = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < windowMs)
            {
                int remaining = (int)Math.Max(10, windowMs - sw.ElapsedMilliseconds);
                byte[] raw = Read(remaining);
                if (raw == null || raw.Length == 0)
                {
                    return false;
                }
                if (raw[0] == MchoseProtocol.BusyByte)
                {
                    continue;
                }

                int cmd;
                byte[] body;
                int seq;
                if (!MchoseProtocol.TryParseFrame(raw, out cmd, out body, out seq))
                {
                    continue;
                }
                if (cmd != expectCmd)
                {
                    continue;
                }

                payload = body;
                return true;
            }

            return false;
        }

        internal bool SetFeature(byte[] report)
        {
            if (_handle == NativeHid.InvalidHandleValue)
            {
                return false;
            }
            return NativeHid.HidD_SetFeature(_handle, report, report.Length);
        }

        public void Dispose()
        {
            if (_handle != NativeHid.InvalidHandleValue)
            {
                NativeHid.CloseHandle(_handle);
                _handle = NativeHid.InvalidHandleValue;
            }
            if (_readEvent != IntPtr.Zero)
            {
                NativeHid.CloseHandle(_readEvent);
                _readEvent = IntPtr.Zero;
            }
            if (_writeEvent != IntPtr.Zero)
            {
                NativeHid.CloseHandle(_writeEvent);
                _writeEvent = IntPtr.Zero;
            }
        }
    }
}
