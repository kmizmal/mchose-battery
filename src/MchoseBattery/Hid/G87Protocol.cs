using System;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// 迈从 G87 键盘（2.4G 接收器）的厂商协议常量与编解码。
    ///
    /// 全部取值来自官方 M HUB 网页驱动（https://www.mchose.com.cn/#/connectDevice）
    /// 的 WebHID 实现，不是推测：
    ///   设备表   {vendorId:16868 (0x41E4), productId:8193 (0x2001), usagePage:65284 (0xFF04)}
    ///   命令表   ["getBattery", { wiredCommand:"06 87 …",
    ///                             wirelessCommand:"13 4a 01 00 00 … 00" }]
    ///   parser   getBatteryWirelessParser = bae(false)
    ///            = uint8("batteryLevel") + bit4("fullStatus") + bit4("chargeStatus")
    ///            其中 bit4 → bitN → setNextParser("bit") 是 LSB-first，
    ///            即第一个 bit4 取低 4 位、第二个取高 4 位（见下方位序说明）。
    ///   发送     readCommand 走 l = `${wirelessCommand} ${tJe(o)}`，
    ///            再 iJe(l) 去掉首字节后 sendReport(0x13, data)，
    ///            即校验和 = 命令全部字节之和（tJe），且发送串不含报告 ID。
    ///   应答     commonParser = uint8(commandParams) uint8(total) uint8(current)，
    ///            payload = t.slice(4, -1)，t 为去掉报告 ID 后的 19 字节。
    ///
    /// 本机实测（G87 接收器）：
    ///   send  13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E
    ///   无线未接线  recv … 64 01 00 …   → raw[6] = 0x01
    ///   插上 Type-C recv … 64 10 00 …   → raw[6] = 0x10
    ///
    /// raw[6] 的位序（官方 parser 的 bit4 是 LSB-first，第一个 bit4 取低 4 位）：
    ///   低 4 位 = fullStatus  （满电）   无线 0x01 → 满电=1；插线 0x10 → 满电=0
    ///   高 4 位 = chargeStatus（充电中） 无线 0x01 → 充电=0；插线 0x10 → 充电=1
    /// 与官方消费逻辑一致：有线路径读 fullStatus，无线路径把 fullStatus 强制置 0，
    /// 但两条路径都读 chargeStatus —— 所以 chargeStatus 才是充电的权威标志。
    ///
    /// 本文件不碰任何 Win32 API，可直接被测试工程链接。
    /// </summary>
    internal static class G87Protocol
    {
        internal const int VendorId = 0x41E4;

        /// <summary>
        /// 蓝牙（BLE HID）通道的厂商 ID。
        /// 蓝牙模式下 Windows 报的是 Bluetooth SIG company id 0x3554，
        /// 而不是 USB 通道的 0x41E4；实测两者共用同一套 0x13 帧协议，
        /// 回包字节完全一致，差异只在设备身份与可用的报告类型。
        /// </summary>
        internal const int BluetoothVendorId = 0x3554;

        /// <summary>官方命名映射 `[8193, 8705].includes(pid) → "G87"`。</summary>
        internal static readonly int[] ProductIds = { 0x2001, 0x2201 };

        /// <summary>
        /// 蓝牙模式下键盘的 PID。不属于官方 USB 设备表（那两条是接收器/有线身份），
        /// 是 BLE HID 枚举出来的实测身份，仅用于连接方式判定与日志。
        /// </summary>
        internal const int BluetoothProductId = 0xFA07;

        /// <summary>USB（0x41E4）与蓝牙（0x3554）两种身份都属于 G87。</summary>
        internal static bool IsKeyboardVendor(int vendorId)
        {
            return vendorId == VendorId || vendorId == BluetoothVendorId;
        }

        /// <summary>
        /// 判定连接方式。蓝牙通道没有 Feature 报告（feat=0），只能走 20 字节
        /// 输出/输入；有线通道有 520 字节 Feature 报告；其余 20 字节通道即 2.4G。
        /// </summary>
        internal static string ConnectModeFor(int vendorId, int productId, bool isWired)
        {
            if (isWired)
            {
                return "有线";
            }
            if (vendorId == BluetoothVendorId || productId == BluetoothProductId)
            {
                return "蓝牙";
            }
            return "2.4G";
        }

        internal const byte ReportId = 0x13;      // 官方 wirelessRId = 19
        internal const byte CmdBattery = 0x4A;    // 官方 getBattery.wirelessCommand 次字节
        internal const int FrameLen = 20;         // 1 报告 ID + 18 数据 + 1 校验

        // 应答偏移，相对完整 20 字节报告（含报告 ID）
        internal const int OffCommand = 1;
        internal const int OffTotal = 2;
        internal const int OffSeq = 3;
        internal const int OffBattery = 5;
        internal const int OffStatus = 6;

        /// <summary>
        /// 构造电量查询帧。布局与官方 splitCommand 一致：
        /// [0]=报告ID [1]=命令 [2]=总分片 [3]=分片序号 [4]=YY [5..18]=负载 [19]=求和校验。
        /// </summary>
        internal static byte[] BuildBatteryFrame()
        {
            var frame = new byte[FrameLen];
            frame[0] = ReportId;
            frame[1] = CmdBattery;
            frame[2] = 0x01;   // 总分片数
            frame[3] = 0x00;   // 当前分片
            frame[4] = 0x00;   // YY

            int sum = 0;
            for (int i = 0; i < FrameLen - 1; i++)
            {
                sum += frame[i];
            }
            frame[FrameLen - 1] = (byte)(sum & 0xFF);
            return frame;
        }

        /// <summary>解析应答，只接受报告 ID 与命令回显都匹配的帧。</summary>
        internal static bool TryParse(byte[] raw, out int percent, out int status)
        {
            percent = 0;
            status = 0;
            if (raw == null || raw.Length < OffStatus + 2)
            {
                return false;
            }
            if (raw[0] != ReportId || raw[OffCommand] != CmdBattery)
            {
                return false;
            }

            percent = raw[OffBattery];
            status = raw[OffStatus];
            return true;
        }

        /// <summary>
        /// 满电标志：官方 parser 的第一个 bit4，占低 4 位。
        /// 官方无线路径会把它强制置 0（无线时设备上报的该位不可信），仅作参考。
        /// </summary>
        internal static bool IsFull(int status)
        {
            return (status & 0x0F) != 0;
        }

        /// <summary>
        /// 充电标志：官方 parser 的第二个 bit4，占高 4 位。
        /// 官方有线路径与无线路径都读这一位，是充电状态的权威来源。
        /// </summary>
        internal static bool IsCharging(int status)
        {
            return (status & 0xF0) != 0;
        }

        // ---------------- 有线通道（键盘插 Type-C 时切换到这套身份） ----------------
        //
        // 官方设备表里 G87 有两个身份，用法完全不同：
        //   无线  {vendorId:16868, productId:8193 (0x2001), usagePage:65284 (0xFF04)}
        //   有线  {vendorId:16868, productId:8705 (0x2201), usagePage:65280 (0xFF00)}
        //
        // 有线通道的官方实现（dJe.readCommand 的 isWired 分支）：
        //   const l = my.getCommandConfig(e).wiredCommand.slice(3).split(" ").map(p => "0x" + p);
        //   await this.device.sendFeatureReport(this.currentReportId, new Uint8Array(l));
        //   const a = await this.device.receiveFeatureReport(this.currentReportId);
        //
        // 关键点：
        //   1. 走的是 **Feature 报告**（SetFeature/GetFeature），不是输出报告；
        //   2. wiredCommand 字符串以 "06 " 开头，slice(3) 去掉前三个字符后
        //      剩下的 "87 00 00 01 00 02 00 …" 才是真正发出的字节；
        //   3. 报告 ID 由 currentReportId 单独传给 sendFeatureReport，
        //      官方 OE 表里 G87_V2 用 wireRid:9，默认（hJe）是 6；
        //      命令串里的 "06" 对应官方默认的 wireRId = 6，所以报告 ID 取 0x06；
        //   4. 总长 520 字节：8 字节头 + 512 字节填充（"00 ".repeat(512)）。
        internal const byte WiredReportId = 0x06;
        internal const byte WiredCmdBattery = 0x87;
        /// <summary>
        /// 有线 Feature 报告长度 = 520 字节。
        /// 官方定义：`06 87 00 00 01 00 02 00 ${"00 ".repeat(512).trim()}`
        /// 即 8 字节头 + 512 字节 0。本机实测接口的 Feature 长度也是 520。
        /// </summary>
        internal const int WiredFrameLen = 520;

        /// <summary>官方 usagePage 65280 = 0xFF00，即 G87 有线身份。</summary>
        internal const int WiredUsagePage = 0xFF00;

        /// <summary>官方 usagePage 65284 = 0xFF04，即 G87 无线（2.4G 接收器）身份。</summary>
        internal const int WirelessUsagePage = 0xFF04;

        /// <summary>
        /// 构造有线电量查询的 Feature 报告。
        /// 官方 wiredCommand = "06 87 00 00 01 00 02 00 " + "00 "×512，
        /// 全串 520 字节；去掉 "06 "（报告 ID 前缀）后剩 519 字节为发送负载，
        /// 加上报告 ID 共 520 字节 = WiredFrameLen。
        /// </summary>
        internal static byte[] BuildWiredBatteryFeature()
        {
            var buf = new byte[WiredFrameLen];
            buf[0] = WiredReportId;                 // 报告 ID
            buf[1] = WiredCmdBattery;               // 命令 0x87
            buf[2] = 0x00;
            buf[3] = 0x00;
            buf[4] = 0x01;
            buf[5] = 0x00;
            buf[6] = 0x02;
            buf[7] = 0x00;
            // 其余 512 字节保持 0
            return buf;
        }

        /// <summary>
        /// 解析有线应答。官方 getBatteryParser = bae(true)，
        /// 即在 uint8("batteryLevel") 之前还有一个 6 字节的 space0 数组，
        /// 之后是 bit4("fullStatus") + bit4("chargeStatus")（LSB-first）。
        /// </summary>
        internal static bool TryParseWired(byte[] raw, out int percent, out int status)
        {
            percent = 0;
            status = 0;
            if (raw == null || raw.Length < 9)
            {
                return false;
            }

            // 官方 readCommand 的有线分支：
            //   const a = await this.device.receiveFeatureReport(this.currentReportId);
            //   const { buf: d } = AE(a);          // AE(n, e=2) → 切掉前 2 字节
            //   h = s.parse(d);                    // s = getBatteryParser = bae(true)
            //
            // AE 切掉前 2 字节后，负载 d 的布局（官方 bae(true) = 6 字节 space0
            // + uint8 batteryLevel + bit4 + bit4）：
            //   d[0..5] = space0(6)
            //   d[6]    = batteryLevel
            //   d[7]    = fullStatus(低4位) | chargeStatus(高4位)
            //
            // 换算成整份报告的偏移（raw = d 前面加回 2 字节）：
            //   raw[2..7] = space0   raw[8] = batteryLevel   raw[9] = status
            //
            // 本机实测回包：06 87 00 00 01 00 02 00 | 64 10 00 …
            //   → d[6]=0x64=100%，d[7]=0x10（高4位充电中）
            for (int at = 2; at <= 3; at++)
            {
                if (raw.Length < at + 8)
                {
                    continue;
                }

                int battery = raw[at + 6];
                int st = raw[at + 7];
                if (battery <= 100)
                {
                    percent = battery;
                    status = st;
                    return true;
                }
            }

            return false;
        }
    }
}
