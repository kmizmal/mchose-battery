using System;

namespace MchoseBattery.Hid
{
    /// <summary>一次电量读取的结果（对应原 Python 的 BatteryResult）。</summary>
    internal sealed class BatteryResult
    {
        public int Percent;
        public bool Charging;
        public string ConnectMode = string.Empty;
        public int Vid;
        public int Pid;
        public int Firmware;
        public string Channel = string.Empty;
        public string Raw = string.Empty;
    }

    /// <summary>
    /// 迈从 V7 的厂商私有三报文协议。全部常量与解析偏移沿用官方脚本的取值，
    /// 任何数字都不要"顺手优化"，否则会读不出数据。
    /// </summary>
    internal static class MchoseProtocol
    {
        internal const int VendorId = 0x3837;
        // 同一只 V7 有两种枚举模式：0x1016（官方设备表 pid 4118）、0x402A（16426）
        internal static readonly int[] ProductIds = { 0x1016, 0x402A };
        internal static readonly int[] UsagePages = { 0xFF01, 0xFF0B };

        /// <summary>
        /// 蓝牙档的 HID 身份。实测：鼠标拨到蓝牙后 USB 的 3837/1016 厂商接口不再应答，
        /// Windows 以 BLE HID 重新枚举为 0x05AC/0x024F，共 5 个 collection 全是标准 HID
        /// （键盘/系统控制/鼠标/消费控制/NKRO），**没有任何厂商自定义集合**。
        /// 因此 0x4D 帧在蓝牙下没有可用通道（实测向 20 字节通道写入返回 87 -
        /// ERROR_INVALID_PARAMETER，报告 ID 不匹配），电量只能取自 BLE GATT Battery Service。
        /// </summary>
        internal const int BluetoothVendorId = 0x05AC;

        /// <summary>蓝牙档的 PID（实测 BLE HID 身份）。</summary>
        internal const int BluetoothProductId = 0x024F;

        /// <summary>
        /// 从 BLE HID 接口路径里取蓝牙地址，形如
        /// `..._dev_vid&amp;0105ac_pid&amp;024f_rev&amp;0127_c4c3e6f5c2ad&amp;col03#...`
        /// → `c4c3e6f5c2ad`（12 位小写 hex，未做字节反转）。
        /// 该地址与 BTHLE 设备实例 ID `BTHLE\DEV_C4C3E6F5C2AD` 的后半段一致。
        /// </summary>
        internal static string ParseBluetoothAddress(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            int at = path.IndexOf("_rev&", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            at += 5;
            int underscore = path.IndexOf('_', at);
            if (underscore < 0)
            {
                return null;
            }

            int start = underscore + 1;
            int end = path.IndexOf('&', start);
            if (end < 0)
            {
                end = path.Length;
            }

            string address = path.Substring(start, end - start);
            if (address.Length != 12)
            {
                return null;
            }

            for (int i = 0; i < address.Length; i++)
            {
                if (!Uri.IsHexDigit(address[i]))
                {
                    return null;
                }
            }

            return address.ToLowerInvariant();
        }

        internal const byte ReportId = 0x4D;      // 官方常量 77
        internal const int FrameLen = 64;         // 含报告 ID
        internal const int CmdDeviceInfo = 0x0900;

        internal const byte BusyByte = 0xFF;      // 回包首字节 0xFF = 设备忙

        // 应答负载偏移（官方 AK）
        internal const int OffVid = 0;
        internal const int OffPid = 2;
        internal const int OffConnect = 10;
        internal const int OffCharge = 11;
        internal const int OffBattery = 12;

        /// <summary>
        /// 官方 chargeStatus 取值 1 = 充电中。
        /// 依据官方 UI 判定：`chargeStatus!==1` 走放电配色，`chargeStatus===1` 走充电配色。
        /// 实测：2.4G 充电时 n[11]=0x01，鼠标仅插线未充电时 n[11]=0x02。
        /// </summary>
        internal const byte ChargeStatusCharging = 1;

        /// <summary>
        /// 官方 nT 名单：这些 productId 属于"无线（2.4G）"身份。
        ///
        /// 官方鼠标页的连接方式判定只有一行：
        ///     connectMode = nT.includes(productId) ? 1 /*无线*/ : 0 /*有线*/
        /// 其中 nT = [4116, 4120, 4124, 4118, 4125]。
        ///
        /// 本机实测 V7 鼠标枚举为 3837/0x1016(4118)，落在名单内 → 无线。
        /// 官方 LKt() 的有线白名单（32896/24772/24579/24585/24774 @ VID 14391）
        /// 同样不含 4118，两条独立规则结论一致。
        /// </summary>
        internal static readonly int[] WirelessProductIds = { 4116, 4120, 4124, 4118, 4125 };

        /// <summary>官方 LKt() 的有线白名单（VID 14391 = 迈从）。</summary>
        internal static readonly int[] WiredProductIds = { 32896, 24772, 24579, 24585, 24774 };

        // legacy 通道
        internal const byte LegacyReportId = 0x11;
        internal const int OrderLen = 21;
        internal const byte XorMask = 0xFF;
        internal const byte EchoByte = 0x06;
        internal const int LegVid = 0;
        internal const int LegFw = 4;
        internal const int LegStatus = 8;
        internal const int LegBattery = 9;
        internal const int LegCharge = 10;

        /// <summary>按官方 i1e() 构造 64 字节帧（首字节为报告 ID）。</summary>
        internal static byte[] BuildFrame(int commandId, byte[] data = null, byte flags = 1)
        {
            data = data ?? Array.Empty<byte>();
            var frame = new byte[FrameLen];
            frame[0] = ReportId;
            frame[1] = 0x01;
            frame[2] = flags;
            frame[3] = (byte)data.Length;
            frame[4] = (byte)(commandId & 0xFF);
            frame[5] = (byte)((commandId >> 8) & 0xFF);
            frame[6] = 0;   // biz code
            frame[7] = 0;   // sequence
            Array.Copy(data, 0, frame, 8, data.Length);
            if (flags == 1)
            {
                byte checksum = 0;
                for (int i = 2; i < 8 + data.Length; i++)
                {
                    checksum ^= frame[i];
                }
                frame[8 + data.Length] = checksum;
            }
            return frame;
        }

        /// <summary>
        /// 解析应答。hidapi 的 read 会把报告 ID 放在首字节，但某些路径会剥离它，
        /// 因此 0/1 两个偏移都试，与原脚本一致。
        /// </summary>
        internal static bool TryParseFrame(byte[] raw, out int commandId, out byte[] payload, out int sequence)
        {
            commandId = 0;
            payload = null;
            sequence = 0;
            if (raw == null)
            {
                return false;
            }

            for (int offset = 0; offset <= 1; offset++)
            {
                if (raw.Length < offset + 9)
                {
                    continue;
                }
                if (raw[offset] != ReportId)
                {
                    continue;
                }

                int bodyStart = offset + 1;
                int bodyLen = raw.Length - bodyStart;
                if (bodyLen < 8)
                {
                    continue;
                }

                int dataLen = raw[bodyStart + 2];
                if (dataLen < 0 || 7 + dataLen > bodyLen)
                {
                    continue;
                }

                byte flags = raw[bodyStart + 1];
                if (flags == 1)
                {
                    byte checksum = 0;
                    for (int i = 1; i < 7 + dataLen; i++)
                    {
                        checksum ^= raw[bodyStart + i];
                    }
                    if (checksum != raw[bodyStart + 7 + dataLen])
                    {
                        continue;
                    }
                }

                commandId = raw[bodyStart + 3] | (raw[bodyStart + 4] << 8);
                sequence = raw[bodyStart + 5];
                payload = new byte[dataLen];
                Array.Copy(raw, bodyStart + 7, payload, 0, dataLen);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判断是不是"该接口不实现此命令"的错误应答。
        ///
        /// 实测（V7 鼠标切到有线模式时）：鼠标本体接口(0x1016)会回一帧
        ///   4D 01 01 01 00 09 00 00 | FE F7
        /// dataLen=1，payload 只有 2 字节的错误码，
        /// 而真实应答 dataLen=16（vid/pid/…/batteryLevel 共 16 字节）。
        ///
        /// 这类短应答必须与"无应答"区分开：它说明该接口不实现 0x0900，
        /// 应当立即换下一个接口，而不是重发 6 次。
        /// </summary>
        internal static bool IsErrorReply(byte[] payload)
        {
            return payload == null || payload.Length < OffBattery + 1;
        }

        /// <summary>按官方 AK() 解析 0x0900 应答。</summary>
        internal static BatteryResult ParseDeviceInfo(byte[] payload)
        {
            if (payload == null || payload.Length < OffBattery + 1)
            {
                return null;
            }

            // 官方 AK 把 n[10] 叫 connectStatus，但实测它恒为 1（无线/有线都一样），
            // 只表示"是否连接"，不能用来判断连接方式。
            // 官方真正的判定依据是 productId（见 WirelessProductIds 注释）。
            int connect = payload[OffConnect];

            return new BatteryResult
            {
                Percent = payload[OffBattery],
                // 官方 UI：chargeStatus === 1 才算充电中。
                // 用 != 0 会把 0x02（仅插线未充电）误判成充电中。
                Charging = payload[OffCharge] == ChargeStatusCharging,
                ConnectMode = string.Empty,   // 由调用方按 productId 填充
                Vid = payload[OffVid] | (payload[OffVid + 1] << 8),
                Pid = payload[OffPid] | (payload[OffPid + 1] << 8),
            };
        }

        /// <summary>
        /// 按官方规则判定连接方式：productId 落在 nT（无线名单）→ "2.4G"，
        /// 否则 → "有线"。
        /// </summary>
        internal static string ConnectModeFor(int productId)
        {
            return Array.IndexOf(WirelessProductIds, productId) >= 0 ? "2.4G" : "有线";
        }

        /// <summary>前代通道：回显 ^0xFF == 0x06，正文整体取反后按固定偏移取值。</summary>
        internal static BatteryResult ParseLegacy(byte[] raw)
        {
            if (raw == null)
            {
                return null;
            }

            for (int i = -1; i < raw.Length - 2; i++)
            {
                int echoAt = i + 1;
                if (echoAt < 0 || echoAt >= raw.Length)
                {
                    continue;
                }
                if ((byte)(raw[echoAt] ^ XorMask) != EchoByte)
                {
                    continue;
                }

                int bodyLen = raw.Length - (echoAt + 1);
                if (bodyLen < LegCharge + 1)
                {
                    continue;
                }

                var body = new byte[bodyLen];
                for (int k = 0; k < bodyLen; k++)
                {
                    body[k] = (byte)(raw[echoAt + 1 + k] ^ XorMask);
                }

                int vid = body[LegVid] | (body[LegVid + 1] << 8);
                if (vid != VendorId)
                {
                    continue;
                }

                int status = body[LegStatus];
                return new BatteryResult
                {
                    Percent = body[LegBattery],
                    Charging = (body[LegCharge] & 0x0F) != 0,
                    ConnectMode = ((status >> 5) & 0x07) != 0 ? "2.4G" : "有线",
                    Vid = vid,
                    Pid = body[2] | (body[3] << 8),
                    Firmware = body[LegFw] | (body[LegFw + 1] << 8) | (body[LegFw + 2] << 16) | (body[LegFw + 3] << 24),
                };
            }

            return null;
        }

        /// <summary>legacy 通道的 Feature 报告内容：[0x11, 0x06, 0…]。</summary>
        internal static byte[] LegacyOrder()
        {
            var buf = new byte[OrderLen];
            buf[0] = LegacyReportId;
            buf[1] = EchoByte;
            return buf;
        }
    }
}
