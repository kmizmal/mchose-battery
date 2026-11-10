using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// V9 Pro 2 耳机（VID 3837，厂商集合 0xFF07，报告 ID 0x55）的通道层与调度。
    /// 两种身份共用同一套帧：`0x6011` 接收器（2.4G）、`0x6010` 耳机本体（有线）。
    ///
    /// 与鼠标/键盘的差别：
    ///   · 该集合没有 Feature 报告（feat = 0），只能走 64 字节输出报告 + 输入报告回包；
    ///   · 报告首字节固定是 0x55，起始符 0xAA 从第二个字节开始，容易误判成「无报告 ID」；
    ///   · 官方客户端是按需查询（进设备页/点刷新才问一次），没有周期心跳，
    ///     所以不能靠被动监听判断设备在线，必须主动问一次。
    /// </summary>
    internal static class V9Battery
    {
        private const int RetryLimit = 3;
        private const int ReadBufferLen = 65;

        /// <summary>完整查询：枚举 FF07 通道，问一次 0x0B，任一接口给出合法应答即成功。</summary>
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
                // 蓝牙档没有 0xFF07 厂商集合，USB 身份整个消失；但耳机仍在经典蓝牙上
                // 连着，系统会把它的 GATT Battery Level 映射到 HFP 节点的
                // DEVPKEY_Bluetooth_Battery 上，可以直接读到百分比。
                CollectResult viaBluetooth = QueryBluetooth();
                if (viaBluetooth != null)
                {
                    return viaBluetooth;
                }

                return new CollectResult
                {
                    Ok = false,
                    Reason = "absent",
                    Detail = "未找到 VID_3837 的 0xFF07 厂商接口（6011 接收器 / 6010 有线），且无蓝牙电池属性",
                };
            }

            string lastNote = null;
            foreach (DeviceTarget target in targets)
            {
                CollectResult result;
                try
                {
                    result = QueryTarget(target, windowMs, out lastNote);
                }
                catch (Exception ex)
                {
                    lastNote = "接口打开失败：" + ex.Message;
                    continue;
                }

                if (result != null)
                {
                    return result;
                }
            }

            return new CollectResult { Ok = false, Reason = "no_response", Detail = lastNote };
        }

        /// <summary>按 VID + PID + 用途页筛选，不写死设备路径。</summary>
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

                    // 路径里带 vid/pid，先做一次廉价过滤，避免为每个 HID 接口都开句柄。
                    // 6011 = 接收器（2.4G），6010 = 耳机本体（有线），两档都要认。
                    string low = path.ToLowerInvariant();
                    if (low.IndexOf("vid_3837", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }
                    if (low.IndexOf("pid_6011", StringComparison.Ordinal) < 0
                        && low.IndexOf("pid_6010", StringComparison.Ordinal) < 0)
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

            // 有线(0x6010) 排前：插着线时它秒回，接收器那条即使仍在枚举也不代答，
            // 先试无用的通道会白等满重试窗口。
            targets.Sort(delegate(DeviceTarget a, DeviceTarget b)
            {
                return ChannelRank(a.ProductId).CompareTo(ChannelRank(b.ProductId));
            });

            return targets;
        }

        /// <summary>
        /// 蓝牙档回退：耳机走经典蓝牙时不暴露任何厂商 HID 集合，也没有 BLE HID 身份。
        /// 此时唯一可读的是系统映射的电池属性（与鼠标 BLE 路径同一个 DEVPKEY），
        /// 宿主节点却是 HFP（`{0000111E-…}`，挂在 BTHENUM 而不是 BTHLE）。
        ///
        /// ⚠ 节点存在 ≠ 设备在线（2026-11-10 纠错）：断开蓝牙后 HFP 节点仍常驻
        /// （`AlwaysShowDeviceAsConnected=True`）且电池属性残留旧值，「读到即在线」
        /// 会把断开误判成已连接。必须再用 WinRT `ConnectionStatus` 验真实链路：
        /// 明确 Disconnected → 返回 null（回落 absent，皮肤显示断开+最后读数）；
        /// 无法确认（解析/查询失败）→ 保留读数避免误报断开，原因写进 Detail。
        /// </summary>
        private static CollectResult QueryBluetooth()
        {
            int percent;
            string instanceId;
            string friendlyName;
            string detail;

            try
            {
                // 耳机蓝牙档的电池属性挂在 HFP 节点上；该节点属于本机连着的所有耳机，
                // 属性里没有厂商信息，只能靠系统给的友好名（<设备名> Hands-Free AG）认设备。
                if (!BleBattery.TryReadBatteryByName(
                        "BTHENUM", V9Protocol.HfpServiceUuid,
                        V9Protocol.BluetoothFriendlyNameKeyword,
                        out percent, out instanceId, out friendlyName, out detail))
                {
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }

            // 验真实链路：属性可能是断开后的残留值。
            string linkNote;
            ulong address;
            bool connected;
            string linkDetail = null;
            if (V9Protocol.TryParseAddressFromInstanceId(instanceId, out address)
                && BthLink.TryIsConnected(address, out connected, out linkDetail))
            {
                if (!connected)
                {
                    // 明确断开：HFP 节点与电池属性都是残留，按不在场处理。
                    return null;
                }
                linkNote = "conn=" + linkDetail;
            }
            else
            {
                // 无法确认连接状态：保留读数（宁多显示不误报断开），把原因留给日志。
                linkNote = linkDetail == null
                    ? "conn=unknown"
                    : "conn=unknown(" + linkDetail + ")";
            }

            return new CollectResult
            {
                Ok = true,
                Percent = percent,
                Charging = false,
                // 系统只给百分比，没有充放电方向；与鼠标蓝牙路径同一口径。
                ChargingKnown = false,
                ConnectMode = "蓝牙",
                Reason = "ok",
                Detail = string.Format("HFP Battery {0}% · {1} · {2} · {3}",
                                       percent, friendlyName, instanceId, linkNote),
            };
        }

        private static int ChannelRank(int productId)
        {
            return productId == V9Protocol.WiredProductId ? 0 : 1;
        }

        private static string GetDevicePath(IntPtr set, ref NativeHid.SpDeviceInterfaceData did)
        {
            int required;
            NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out required, IntPtr.Zero))
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

        /// <summary>打开接口确认身份：VID/PID 命中且用途页 0xFF07、进出报告长度都是 64。</summary>
        private static bool TryInspect(string path, out DeviceTarget target)
        {
            target = null;
            IntPtr handle = NativeHid.CreateFile(
                path, 0,
                NativeHid.FileShareRead | NativeHid.FileShareWrite,
                IntPtr.Zero, NativeHid.OpenExisting, 0, IntPtr.Zero);
            if (handle == NativeHid.InvalidHandleValue)
            {
                return false;
            }

            try
            {
                var attributes = new NativeHid.HiddAttributes
                {
                    Size = Marshal.SizeOf(typeof(NativeHid.HiddAttributes)),
                };
                if (!NativeHid.HidD_GetAttributes(handle, ref attributes))
                {
                    return false;
                }
                if (attributes.VendorId != V9Protocol.VendorId)
                {
                    return false;
                }
                if (!V9Protocol.IsHeadsetProduct(attributes.ProductId))
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
                    if (NativeHid.HidP_GetCaps(preparsed, ref caps) != 0x00110000)
                    {
                        return false;
                    }
                    if (caps.UsagePage != V9Protocol.UsagePage)
                    {
                        return false;
                    }
                    if (caps.InputReportByteLength != V9Protocol.FrameLen
                        || caps.OutputReportByteLength != V9Protocol.FrameLen)
                    {
                        return false;
                    }

                    target = new DeviceTarget
                    {
                        Path = path,
                        ProductId = attributes.ProductId,
                        UsagePage = caps.UsagePage,
                        InLen = caps.InputReportByteLength,
                        OutLen = caps.OutputReportByteLength,
                        FeatLen = caps.FeatureReportByteLength,
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

        /// <summary>对单个接口问一次 0x0B；重试只在「写成功但窗口内无回包」时进行。</summary>
        private static CollectResult QueryTarget(DeviceTarget target, int windowMs, out string note)
        {
            note = null;
            using (var channel = V9Channel.Open(target.Path))
            {
                byte[] request = V9Protocol.BuildBatteryRequest();
                for (int attempt = 0; attempt < RetryLimit; attempt++)
                {
                    if (!channel.Write(request))
                    {
                        note = "输出报告 0x55 写入失败";
                        return null;
                    }

                    byte[] report;
                    if (!channel.WaitBatteryReply(windowMs, out report))
                    {
                        note = "0x0B 无应答";
                        continue;
                    }

                    V9Protocol.BatteryReading reading;
                    if (!V9Protocol.TryParseBattery(report, out reading))
                    {
                        note = "应答无法解析";
                        continue;
                    }

                    if (reading.Asleep)
                    {
                        // 官方语义：休眠时电量置空。这里如实上报「休眠」，不编造数值。
                        return new CollectResult
                        {
                            Ok = false,
                            Reason = "asleep",
                            Detail = "耳机处于休眠",
                        };
                    }

                    return new CollectResult
                    {
                        Ok = true,
                        Percent = reading.Percent,
                        Charging = reading.Charging,
                        ChargingKnown = true,
                        ConnectMode = V9Protocol.ConnectModeFor(target.ProductId),
                        Reason = "ok",
                        Detail = string.Format(
                            "PID 0x{0:X4} · Report 0x55 · cmd 0x0B", target.ProductId),
                    };
                }
            }

            return null;
        }

        /// <summary>一次采集的候选接口。</summary>
        internal sealed class DeviceTarget
        {
            internal string Path;
            internal int ProductId;
            internal int UsagePage;
            internal int InLen;
            internal int OutLen;
            internal int FeatLen;
        }

        /// <summary>64 字节输出报告 + 输入报告回包的通道。</summary>
        private sealed class V9Channel : IDisposable
        {
            private IntPtr _handle = NativeHid.InvalidHandleValue;
            private IntPtr _readEvent = IntPtr.Zero;
            private IntPtr _writeEvent = IntPtr.Zero;

            private V9Channel()
            {
            }

            internal static V9Channel Open(string path)
            {
                var channel = new V9Channel();
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
                channel._writeEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
                return channel;
            }

            internal bool Write(byte[] frame)
            {
                if (_handle == NativeHid.InvalidHandleValue || _writeEvent == IntPtr.Zero)
                {
                    return false;
                }

                NativeHid.ResetEvent(_writeEvent);
                var overlapped = new NativeHid.NativeOverlapped { EventHandle = _writeEvent };
                int written;

                bool ok = NativeHid.WriteFile(_handle, frame, frame.Length, IntPtr.Zero, ref overlapped);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != NativeHid.ErrorIoPending)
                    {
                        return false;
                    }
                    if (NativeHid.WaitForSingleObject(_writeEvent, 2000) != NativeHid.WaitObject0)
                    {
                        NativeHid.CancelIo(_handle);
                        return false;
                    }
                }

                return NativeHid.GetOverlappedResult(_handle, ref overlapped, out written, false)
                       && written >= frame.Length - 1;
            }

            internal byte[] Read(int timeoutMs)
            {
                if (_handle == NativeHid.InvalidHandleValue || _readEvent == IntPtr.Zero)
                {
                    return null;
                }

                var buffer = new byte[ReadBufferLen];
                NativeHid.ResetEvent(_readEvent);
                var overlapped = new NativeHid.NativeOverlapped { EventHandle = _readEvent };
                int read;

                bool ok = NativeHid.ReadFile(_handle, buffer, buffer.Length, IntPtr.Zero, ref overlapped);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != NativeHid.ErrorIoPending)
                    {
                        return null;
                    }
                    if (NativeHid.WaitForSingleObject(_readEvent, Math.Max(10, timeoutMs)) != NativeHid.WaitObject0)
                    {
                        NativeHid.CancelIo(_handle);
                        return null;
                    }
                }

                if (!NativeHid.GetOverlappedResult(_handle, ref overlapped, out read, false) || read <= 0)
                {
                    return null;
                }

                var result = new byte[read];
                Array.Copy(buffer, result, read);
                return result;
            }

            /// <summary>窗口内等待 0x0B 的应答；其他命令（如提示音文案 0x16/0x1C）直接丢弃继续等。</summary>
            internal bool WaitBatteryReply(int windowMs, out byte[] report)
            {
                report = null;
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < windowMs)
                {
                    int remaining = (int)Math.Max(10, windowMs - stopwatch.ElapsedMilliseconds);
                    byte[] raw = Read(remaining);
                    if (raw == null || raw.Length == 0)
                    {
                        return false;
                    }

                    V9Protocol.BatteryReading reading;
                    if (!V9Protocol.TryParseBattery(raw, out reading))
                    {
                        continue;
                    }

                    report = raw;
                    return true;
                }

                return false;
            }

            public void Dispose()
            {
                if (_writeEvent != IntPtr.Zero)
                {
                    NativeHid.CloseHandle(_writeEvent);
                    _writeEvent = IntPtr.Zero;
                }
                if (_readEvent != IntPtr.Zero)
                {
                    NativeHid.CloseHandle(_readEvent);
                    _readEvent = IntPtr.Zero;
                }
                if (_handle != NativeHid.InvalidHandleValue)
                {
                    NativeHid.CloseHandle(_handle);
                    _handle = NativeHid.InvalidHandleValue;
                }
            }
        }
    }
}
