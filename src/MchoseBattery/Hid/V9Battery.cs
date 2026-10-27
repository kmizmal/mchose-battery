using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// V9 Pro 2 耳机（3837/6011，厂商集合 0xFF07，报告 ID 0x55）的通道层与调度。
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
                return new CollectResult
                {
                    Ok = false,
                    Reason = "absent",
                    Detail = "未找到 VID_3837/PID_6011 的 0xFF07 厂商接口",
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

                    // 路径里带 vid/pid，先做一次廉价过滤，避免为每个 HID 接口都开句柄
                    string low = path.ToLowerInvariant();
                    if (low.IndexOf("vid_3837", StringComparison.Ordinal) < 0
                        || low.IndexOf("pid_6011", StringComparison.Ordinal) < 0)
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

            return targets;
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
                if (attributes.VendorId != V9Protocol.VendorId || attributes.ProductId != V9Protocol.ProductId)
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
                        ConnectMode = "2.4G",
                        Reason = "ok",
                        Detail = "Report 0x55 · cmd 0x0B",
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
