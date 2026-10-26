using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// G87 电量读取。与 V7 通道完全独立，互不影响：
    /// V7 走 0x3837 / usagePage 0xFF01 / 报告 0x4D / 64 字节，
    /// G87 走 0x41E4 / 厂商页 / 报告 0x13 / 20 字节（无线），
    /// 或 0x41E4 / usagePage 0xFF00 / Feature 报告 0x06 / 519 字节（有线）。
    /// </summary>
    internal static class G87Battery
    {
        private const int RetryLimit = 3;
        private const int ReadBufferLen = 32;

        /// <summary>
        /// 蓝牙通道的读取窗口下限（毫秒）。BLE 链路从省电态唤醒要 1 秒以上：
        /// 本机实测首次查询用 600 ms 窗口时 3 次尝试全部无应答，
        /// 链路唤醒后同一帧 88 ms 即回。
        /// </summary>
        private const int BluetoothWindowMs = 1500;

        /// <summary>枚举 G87 接收器上可收发的厂商接口。</summary>
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

                while (NativeHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did))
                {
                    index++;

                    string path = GetDevicePath(set, ref did);
                    if (string.IsNullOrEmpty(path) || !seen.Add(path))
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

            // 通道排序：有线 → 蓝牙 → 2.4G（见 ChannelRank）。
            // 键盘切模式后，其余身份的接口仍会枚举出来但不再代答；
            // 不排序的话，先试的那个通道每次都要耗满 3 次重试 × windowMs，
            // 表现为读数超时甚至整体失败。
            targets.Sort(CompareTargets);
            return targets;
        }

        /// <summary>
        /// 通道优先级：有线(0) → 蓝牙(1) → 2.4G(2)。
        ///
        /// · 有线通道走 Feature，秒回；
        /// · BLE 设备节点只有在键盘真的处于蓝牙模式且连接保持时才存在，
        ///   所以在场即说明键盘当前走蓝牙，应优先试；
        /// · 2.4G 接收器插着就常驻枚举，哪怕键盘已切到蓝牙/有线，因此排最后，
        ///   避免每次先白等满 3 次重试。
        /// </summary>
        private static int ChannelRank(DeviceTarget t)
        {
            if (t.IsWiredChannel)
            {
                return 0;
            }
            if (t.IsBluetooth)
            {
                return 1;
            }
            return 2;
        }

        private static int CompareTargets(DeviceTarget a, DeviceTarget b)
        {
            int rankA = ChannelRank(a);
            int rankB = ChannelRank(b);
            if (rankA != rankB)
            {
                return rankA - rankB;
            }

            // 同类里按接口号稳定排序
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

        /// <summary>
        /// 只收 G87 的厂商接口：USB 身份 VID=0x41E4 或蓝牙身份 VID=0x3554，
        /// 且具备 20 字节厂商输入/输出报告（2.4G、蓝牙）或 520 字节 Feature（有线）。
        /// </summary>
        private static bool TryInspect(string path, out DeviceTarget target)
        {
            target = null;

            IntPtr handle = NativeHid.CreateFile(
                path, 0, NativeHid.FileShareRead | NativeHid.FileShareWrite,
                IntPtr.Zero, NativeHid.OpenExisting, 0, IntPtr.Zero);
            if (handle == NativeHid.InvalidHandleValue)
            {
                return false;
            }

            try
            {
                var attr = new NativeHid.HiddAttributes();
                attr.Size = Marshal.SizeOf(typeof(NativeHid.HiddAttributes));
                if (!NativeHid.HidD_GetAttributes(handle, ref attr) ||
                    !G87Protocol.IsKeyboardVendor(attr.VendorId))
                {
                    return false;
                }

                IntPtr preparsed;
                if (!NativeHid.HidD_GetPreparsedData(handle, out preparsed))
                {
                    return false;
                }

                try
                {
                    var caps = new NativeHid.HidpCaps();
                    if (NativeHid.HidP_GetCaps(preparsed, ref caps) != NativeHid.HidpStatusSuccess)
                    {
                        return false;
                    }

                    // 厂商自定义页（0xFFxx）。
                    //
                    // 官方设备表里 G87 的"身份"按 usagePage 标注：
                    //   无线 8193(0x2001) + 65284(0xFF04)
                    //   有线 8705(0x2201) + 65280(0xFF00)
                    // 但本机实测：接收器(0x2001) 上的 0x4A 数据接口落在 FF02 上，
                    // FF04 那条是 8 字节 Feature。usagePage 只是身份标签，
                    // 真正的判据是报告长度：
                    //   无线 → 20 字节输入 + 输出报告（0x13 通道）
                    //   有线 → 519 字节 Feature 报告（0x06 通道）
                    if ((caps.UsagePage & 0xFF00) != 0xFF00)
                    {
                        return false;
                    }

                    bool wireless = caps.OutputReportByteLength >= G87Protocol.FrameLen &&
                                    caps.InputReportByteLength >= G87Protocol.FrameLen;
                    bool wired = caps.FeatureReportByteLength >= G87Protocol.WiredFrameLen;
                    if (!wireless && !wired)
                    {
                        return false;
                    }

                    target = new DeviceTarget
                    {
                        Path = path,
                        VendorId = attr.VendorId,
                        ProductId = attr.ProductId,
                        UsagePage = caps.UsagePage,
                        Usage = caps.Usage,
                        InterfaceNumber = ParseInterface(path),
                        IsBluetooth = attr.VendorId == G87Protocol.BluetoothVendorId ||
                                      attr.ProductId == G87Protocol.BluetoothProductId,
                        IsWiredChannel = !wireless && wired,
                    };
                    return true;
                }
                finally
                {
                    NativeHid.HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                NativeHid.CloseHandle(handle);
            }
        }

        private static int ParseInterface(string path)
        {
            int at = (path ?? string.Empty).ToLowerInvariant().IndexOf("&mi_", StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }
            at += 4;
            int end = at;
            while (end < path.Length && Uri.IsHexDigit(path[end]))
            {
                end++;
            }
            if (end == at)
            {
                return -1;
            }
            int value;
            return int.TryParse(path.Substring(at, end - at),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        /// <summary>完整查询：遍历候选接口发 0x4A，任一返回有效应答即成功。</summary>
        internal static CollectResult Query(int windowMs)
        {
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
                return new CollectResult { Ok = false, Reason = "absent", Detail = "未找到 G87 厂商接口（USB 0x41E4 / 蓝牙 0x3554）" };
            }

            string lastDetail = null;
            foreach (DeviceTarget target in targets)
            {
                try
                {
                    if (target.IsWiredChannel)
                    {
                        // 有线身份：官方走 Feature 报告（SetFeature 519 字节 → GetFeature 回读）
                        CollectResult wired = QueryWired(target);
                        if (wired != null && wired.Ok)
                        {
                            return wired;
                        }
                        lastDetail = wired != null ? wired.Detail : "有线通道无应答";
                        continue;
                    }

                    int window = target.IsBluetooth
                        ? Math.Max(windowMs, BluetoothWindowMs)
                        : windowMs;

                    using (var channel = TinyChannel.Open(target.Path))
                    {
                        byte[] frame = G87Protocol.BuildBatteryFrame();
                        for (int attempt = 0; attempt < RetryLimit; attempt++)
                        {
                            if (!channel.Write(frame))
                            {
                                lastDetail = "输出报告 0x13 写入失败";
                                break;
                            }

                            var sw = Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < window)
                            {
                                int remaining = (int)Math.Max(10, window - sw.ElapsedMilliseconds);
                                byte[] raw = channel.Read(remaining);
                                if (raw == null)
                                {
                                    break;
                                }

                                int percent;
                                int status;
                                if (!G87Protocol.TryParse(raw, out percent, out status))
                                {
                                    continue;
                                }

                                return new CollectResult
                                {
                                    Ok = true,
                                    Percent = percent,
                                    // status 字节位序已用插线/无线对照实测校准：
                                    //   无线未接线 0x01 → 低4位(满电)=1, 高4位(充电)=0
                                    //   插上 Type-C 0x10 → 低4位(满电)=0, 高4位(充电)=1
                                    // 官方 parser 的 bit4 是 LSB-first：第一个 bit4（fullStatus）取低 4 位，
                                    // 第二个（chargeStatus）取高 4 位；官方有/无线路径都读 chargeStatus，
                                    // 它才是充电状态的权威标志。
                                    Charging = G87Protocol.IsCharging(status),
                                    ConnectMode = G87Protocol.ConnectModeFor(
                                        target.VendorId, target.ProductId, false),
                                    Reason = "ok",
                                    Detail = string.Format(
                                        "Report 0x{0:X2} · cmd 0x{1:X2} · {2:X4}/{3:X4}",
                                        G87Protocol.ReportId, G87Protocol.CmdBattery,
                                        target.VendorId, target.ProductId),
                                };
                            }

                            lastDetail = "0x4A 无应答";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastDetail = "接口打开失败：" + ex.Message;
                }
            }

            return new CollectResult { Ok = false, Reason = "no_response", Detail = lastDetail };
        }

        /// <summary>
        /// 有线通道：官方 dJe.readCommand 的 isWired 分支 ——
        /// SetFeature 发 519 字节（含报告 ID 0x06，命令 0x87），再用 GetFeature 回读负载。
        /// </summary>
        private static CollectResult QueryWired(DeviceTarget target)
        {
            try
            {
                using (var channel = TinyChannel.Open(target.Path))
                {
                    byte[] feature = G87Protocol.BuildWiredBatteryFeature();
                    for (int attempt = 0; attempt < RetryLimit; attempt++)
                    {
                        if (!channel.SetFeature(feature))
                        {
                            return new CollectResult
                            {
                                Ok = false,
                                Reason = "no_response",
                                Detail = "SetFeature(0x06) 失败",
                            };
                        }

                        byte[] back = channel.GetFeature(G87Protocol.WiredFrameLen);
                        if (back == null)
                        {
                            continue;
                        }

                        int percent;
                        int status;
                        if (!G87Protocol.TryParseWired(back, out percent, out status))
                        {
                            continue;
                        }

                        return new CollectResult
                        {
                            Ok = true,
                            Percent = percent,
                            Charging = G87Protocol.IsCharging(status),
                            ConnectMode = "有线",
                            Reason = "ok",
                            Detail = string.Format(
                                "Feature 0x{0:X2} · cmd 0x{1:X2} · {2} 字节",
                                G87Protocol.WiredReportId, G87Protocol.WiredCmdBattery,
                                G87Protocol.WiredFrameLen),
                        };
                    }

                    return new CollectResult
                    {
                        Ok = false,
                        Reason = "no_response",
                        Detail = "Feature 0x06 无有效应答",
                    };
                }
            }
            catch (Exception ex)
            {
                return new CollectResult
                {
                    Ok = false,
                    Reason = "no_response",
                    Detail = "有线通道异常：" + ex.Message,
                };
            }
        }

        /// <summary>G87 专用通道：20/32 字节报告 + 重叠 I/O，与 V7 的 65 字节通道分开。</summary>
        private sealed class TinyChannel : IDisposable
        {
            private IntPtr _handle = NativeHid.InvalidHandleValue;
            private IntPtr _readEvent = IntPtr.Zero;

            private TinyChannel()
            {
            }

            internal static TinyChannel Open(string path)
            {
                var channel = new TinyChannel();
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
                    throw new InvalidOperationException(
                        "CreateFile 失败，Win32 错误 " + Marshal.GetLastWin32Error());
                }

                channel._readEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
                return channel;
            }

            internal bool Write(byte[] frame)
            {
                if (_handle == NativeHid.InvalidHandleValue)
                {
                    return false;
                }

                var overlapped = new NativeHid.NativeOverlapped { EventHandle = _readEvent };
                bool ok = NativeHid.WriteFile(_handle, frame, frame.Length, IntPtr.Zero, ref overlapped);
                if (!ok && Marshal.GetLastWin32Error() != NativeHid.ErrorIoPending)
                {
                    return false;
                }
                NativeHid.WaitForSingleObject(_readEvent, 300);
                return true;
            }

            internal byte[] Read(int timeoutMs)
            {
                if (_handle == NativeHid.InvalidHandleValue)
                {
                    return null;
                }

                // 重叠 I/O 期间缓冲不能移动，用固定句柄而非裸 byte[]。
                var buffer = new byte[ReadBufferLen];
                GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var overlapped = new NativeHid.NativeOverlapped { EventHandle = _readEvent };
                    bool ok = NativeHid.ReadFile(_handle, buffer, buffer.Length, IntPtr.Zero, ref overlapped);
                    if (!ok)
                    {
                        if (Marshal.GetLastWin32Error() != NativeHid.ErrorIoPending)
                        {
                            return null;
                        }
                        if (NativeHid.WaitForSingleObject(_readEvent, timeoutMs) != NativeHid.WaitObject0)
                        {
                            NativeHid.CancelIo(_handle);
                            return null;
                        }
                    }

                    int transferred;
                    if (!NativeHid.GetOverlappedResult(_handle, ref overlapped, out transferred, true))
                    {
                        return null;
                    }
                    if (transferred <= 0)
                    {
                        return null;
                    }

                    var result = new byte[transferred];
                    Array.Copy(buffer, result, transferred);
                    return result;
                }
                finally
                {
                    pin.Free();
                }
            }

            /// <summary>
            /// 发送 Feature 报告。官方有线通道用 sendFeatureReport，
            /// Windows 对应 HidD_SetFeature，缓冲区首字节是报告 ID。
            /// </summary>
            internal bool SetFeature(byte[] report)
            {
                if (_handle == NativeHid.InvalidHandleValue)
                {
                    return false;
                }
                return NativeHid.HidD_SetFeature(_handle, report, report.Length);
            }

            /// <summary>回读 Feature 报告（官方 receiveFeatureReport）。</summary>
            internal byte[] GetFeature(int length)
            {
                if (_handle == NativeHid.InvalidHandleValue)
                {
                    return null;
                }
                var buffer = new byte[length];
                buffer[0] = G87Protocol.WiredReportId;
                if (!NativeHid.HidD_GetFeature(_handle, buffer, length))
                {
                    return null;
                }
                return buffer;
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
            }
        }
    }
}
