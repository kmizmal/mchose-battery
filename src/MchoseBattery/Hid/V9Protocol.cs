using System;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// MCHOSE V9 Pro 2 耳机（USB 接收器身份 3837/6011，厂商集合 0xFF07）的私有 HID 协议。
    ///
    /// 证据来源：官方桌面客户端远程下发的设备页脚本，随客户端缓存可复现：
    ///   · cdn.mchose.com.cn/customPage/prod/assets/crc32-BlQnRCeo.js —— 帧构造/解析/校验算法本体
    ///   · .../eq-DcwrcT_4.js        —— GET_BATTERY_SLEEP = 11 (0x0B)、reportId = 85 (0x55)
    ///   · .../headsetPage-DNy5Z21r.js —— 调用点「获取到电量信息（HidEQ 0x0B）」
    /// 并用真机回包逐字节复核（见 tests 与 README 的帧样例）。
    ///
    /// 报文：报告 ID 0x55，整条报告 64 字节 = 1 字节报告 ID + 63 字节报文体。
    /// 报文体偏移（下方常量均为「整条报告」偏移，已把报告 ID 算进去）：
    ///   [0]      0x55                     报告 ID
    ///   [1]      0xAA                     起始符 StartByte
    ///   [2]      0x01                     协议版本
    ///   [3]      flags                    0 = 不带校验，1 = 末位为 XOR 校验
    ///   [4]      length                   payload 长度 + 1
    ///   [5..6]   cmdType                  请求/应答 [0,1]、设置 [0,2]、通知 [0,4]、OTA [0,6]
    ///   [7]      cmd                      命令字
    ///   [8..]    payload
    ///   [8+payloadLen]                     XOR 校验（flags = 1 时有效）
    ///
    /// 校验算法对齐官方 C()：flags ^ length ^ cmdType[0] ^ cmdType[1] ^ cmd ^ payload 全部字节。
    /// </summary>
    internal static class V9Protocol
    {
        /// <summary>接收器与耳机本体共用迈从 VID。</summary>
        internal const int VendorId = 0x3837;

        /// <summary>V9 Pro 2 的接收器 PID（十进制 24593，2.4G 档，产品串 `MCHOSE V9 Pro 2`）。</summary>
        internal const int ProductId = 0x6011;

        /// <summary>
        /// 有线档 PID（十进制 24592，产品串 `MCHOSE V9 Pro 2 HEADSET`）。
        /// 插 USB-C 直连时整套身份更换：接口布局与报告 ID 完全一致，只有 PID 与产品串不同。
        /// 实测同一帧在两档下逐字节同构：`… 01 56 5F`（86%，放电）与 `… 01 D6 DF`（86%，充电）。
        /// </summary>
        internal const int WiredProductId = 0x6010;

        /// <summary>两个 PID 都属于 V9 Pro 2。</summary>
        internal static bool IsHeadsetProduct(int productId)
        {
            return productId == ProductId || productId == WiredProductId;
        }

        /// <summary>按 PID 判连接方式：有线身份 0x6010 → 有线，其余 → 2.4G。</summary>
        internal static string ConnectModeFor(int productId)
        {
            return productId == WiredProductId ? "有线" : "2.4G";
        }

        /// <summary>厂商用途页。实测 0xFF07，与鼠标(0xFF01)、键盘(0xFF00/0xFF04)都不同。</summary>
        internal const int UsagePage = 0xFF07;

        internal const byte ReportId = 0x55;

        /// <summary>整条报告长度（含报告 ID）。实测接口 In=Out=64 且无 Feature。</summary>
        internal const int FrameLen = 64;

        internal const byte StartByte = 0xAA;
        internal const byte ProtocolVersion = 0x01;

        /// <summary>命令字：电量与休眠状态。官方常量 GET_BATTERY_SLEEP = 11。</summary>
        internal const byte CmdBatterySleep = 0x0B;

        /// <summary>cmdType：请求与应答同为 [0,1]，靠方向区分。</summary>
        internal const byte CmdTypeRequestA = 0x00;
        internal const byte CmdTypeRequestB = 0x01;

        /// <summary>cmdType：主动通知 [0,4]（充放电状态变化推送）。</summary>
        internal const byte CmdTypeNotifyB = 0x04;

        /// <summary>flags：带校验。</summary>
        internal const byte FlagChecked = 0x01;

        /// <summary>
        /// 构造电量查询请求。
        /// 官方原文（eq-DcwrcT_4.js / getDeviceBatteryAndSleep）：
        ///   cmdType = Request([0,1])、cmd = 0x0B、payload = []、check = false
        /// 即报文体为 AA 01 00 01 00 01 0B 00，其余补零到 63 字节。
        /// </summary>
        internal static byte[] BuildBatteryRequest()
        {
            var frame = new byte[FrameLen];
            frame[0] = ReportId;
            frame[1] = StartByte;
            frame[2] = ProtocolVersion;
            frame[3] = 0x00;                      // flags：官方该请求 check = false
            frame[4] = 0x01;                      // length = payload(0) + 1
            frame[5] = CmdTypeRequestA;
            frame[6] = CmdTypeRequestB;
            frame[7] = CmdBatterySleep;
            frame[8] = 0x00;                      // 校验位：flags = 0 时官方写 0
            return frame;
        }

        /// <summary>解析 0x0B 应答的负载。官方 Z()：payload[0] 休眠态，payload[1] 的 bit7 充电、低 7 位电量。</summary>
        internal static BatteryReading ParseBatteryPayload(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return new BatteryReading
            {
                Asleep = payload[0] == 0x00,
                Charging = ((payload[1] >> 7) & 0x01) == 0x01,
                Percent = payload[1] & 0x7F,
            };
        }

        /// <summary>
        /// 从整条输入报告里解析电量应答。
        /// 只有 cmdType = [0,1] 且 cmd = 0x0B 才收；带校验时逐字节复核，避免把噪声当数据。
        /// </summary>
        internal static bool TryParseBattery(byte[] report, out BatteryReading reading)
        {
            reading = null;
            if (report == null || report.Length < 11)
            {
                return false;
            }
            if (report[0] != ReportId || report[1] != StartByte)
            {
                return false;
            }
            if (report[5] != CmdTypeRequestA || report[6] != CmdTypeRequestB)
            {
                return false;
            }
            if (report[7] != CmdBatterySleep)
            {
                return false;
            }

            int length = report[4];
            if (length < 3)
            {
                // payload 至少要有「休眠态 + 电量字节」两个
                return false;
            }

            int payloadLen = length - 1;
            int checksumIndex = 8 + payloadLen;
            if (checksumIndex >= report.Length)
            {
                return false;
            }
            if (report[3] == FlagChecked && !VerifyChecksum(report, payloadLen, checksumIndex))
            {
                return false;
            }

            var payload = new byte[payloadLen];
            Array.Copy(report, 8, payload, 0, payloadLen);
            reading = ParseBatteryPayload(payload);
            return reading != null;
        }

        /// <summary>官方 C() 的等价实现：只在校验位存在时调用。</summary>
        internal static byte ComputeChecksum(byte flags, byte length, byte cmdTypeA, byte cmdTypeB, byte cmd, byte[] payload)
        {
            byte value = flags;
            value ^= length;
            value ^= cmdTypeA;
            value ^= cmdTypeB;
            value ^= cmd;
            if (payload != null)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    value ^= payload[i];
                }
            }
            return value;
        }

        private static bool VerifyChecksum(byte[] report, int payloadLen, int checksumIndex)
        {
            var payload = new byte[payloadLen];
            Array.Copy(report, 8, payload, 0, payloadLen);
            byte expected = ComputeChecksum(report[3], report[4], report[5], report[6], report[7], payload);
            return expected == report[checksumIndex];
        }

        /// <summary>0x0B 应答的语义结果。</summary>
        internal sealed class BatteryReading
        {
            internal int Percent;
            internal bool Charging;

            /// <summary>耳机处于休眠（官方语义：batteryLevel 置空）。</summary>
            internal bool Asleep;
        }
    }
}
