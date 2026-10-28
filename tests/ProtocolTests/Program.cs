using System;
using System.Collections.Generic;
using MchoseBattery;
using MchoseBattery.Hid;

namespace MchoseBattery.Tests
{
    /// <summary>
    /// 协议与状态机的最小测试集。
    /// 覆盖：帧构造、帧解析（含校验和失败）、0x0900 负载解析、legacy 解析、状态归一化。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        private static void Check(bool condition, string name, string detail = null)
        {
            if (condition)
            {
                _passed++;
                return;
            }
            Failures.Add(detail == null ? name : $"{name}（{detail}）");
            Console.WriteLine($"  FAIL  {name}{(detail == null ? "" : "  -> " + detail)}");
        }

        private static void CheckEqual<T>(T actual, T expected, string name)
        {
            bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
            Check(ok, name, ok ? null : $"期望 {expected}，实际 {actual}");
        }

        private static int Main()
        {
            TestBuildFrame();
            TestParseFrame();
            TestParseDeviceInfo();
            TestParseLegacy();
            TestStatusBuilder();
            TestG87();
            TestV9();

            Console.WriteLine();
            Console.WriteLine($"通过 {_passed} 项，失败 {Failures.Count} 项");
            if (Failures.Count > 0)
            {
                foreach (string f in Failures)
                {
                    Console.WriteLine("  - " + f);
                }
                return 1;
            }
            return 0;
        }

        // ------------------------------------------------- G87（官方 WebHID 协议）

        /// <summary>
        /// G87 的常量与偏移全部来自官方 M HUB 网页驱动的 WebHID 实现，
        /// 期望值取自本机 G87 接收器的真实收发帧，避免纯理论断言。
        /// </summary>
        private static void TestG87()
        {
            Console.WriteLine("G87Protocol");

            byte[] frame = G87Protocol.BuildBatteryFrame();
            CheckEqual(frame.Length, 20, "G87 帧长 20");
            CheckEqual(frame[0], (byte)0x13, "G87 报告 ID 0x13");
            CheckEqual(frame[1], (byte)0x4A, "G87 命令 0x4A");
            CheckEqual(frame[2], (byte)0x01, "G87 总分片 1");
            CheckEqual(frame[3], (byte)0x00, "G87 分片序号 0");

            int sum = 0;
            for (int i = 0; i < frame.Length - 1; i++)
            {
                sum += frame[i];
            }
            CheckEqual(frame[19], (byte)(sum & 0xFF), "G87 末字节为前 19 字节求和");
            CheckEqual(frame[19], (byte)0x5E, "G87 校验值 0x5E");

            // 本机实测：send 13 4A 01 00 00 … 5E
            //           recv 13 4A 01 00 02 64 01 00 … C5
            byte[] reply =
            {
                0x13, 0x4A, 0x01, 0x00, 0x02, 0x64, 0x01, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0xC5
            };

            int percent;
            int status;
            Check(G87Protocol.TryParse(reply, out percent, out status), "G87 解析实测应答");
            CheckEqual(percent, 100, "G87 电量 raw[5]=0x64 → 100%");
            CheckEqual(status, 0x01, "G87 状态 raw[6]=0x01");

            // 位序校准：官方 parser 的 bit4 是 LSB-first，第一个 bit4（fullStatus）取低 4 位，
            // 第二个（chargeStatus）取高 4 位。本机对照实测：
            //   无线未接线  raw[6]=0x01   插上 Type-C  raw[6]=0x10
            CheckEqual(G87Protocol.IsCharging(0x01), false, "无线未接线 0x01 → 不充电");
            CheckEqual(G87Protocol.IsFull(0x01), true, "无线未接线 0x01 → 满电位为 1");
            CheckEqual(G87Protocol.IsCharging(0x10), true, "插线充电 0x10 → 充电中");
            CheckEqual(G87Protocol.IsFull(0x10), false, "插线充电 0x10 → 满电位为 0");

            // 插上 Type-C 后的真实应答：13 4A 01 00 02 64 10 00 … D4
            byte[] wiredReply =
            {
                0x13, 0x4A, 0x01, 0x00, 0x02, 0x64, 0x10, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0xD4
            };
            Check(G87Protocol.TryParse(wiredReply, out percent, out status), "G87 解析插线应答");
            CheckEqual(percent, 100, "插线时电量仍为 100%");
            CheckEqual(status, 0x10, "插线时 raw[6]=0x10");
            CheckEqual(G87Protocol.IsCharging(status), true, "插线应答判定为充电中");
            CheckEqual(G87Protocol.IsFull(status), false, "插线应答满电位为 0");

            Check(!G87Protocol.TryParse(null, out percent, out status), "G87 拒绝空帧");

            byte[] wrongCmd = (byte[])reply.Clone();
            wrongCmd[1] = 0x4B;
            Check(!G87Protocol.TryParse(wrongCmd, out percent, out status), "G87 拒绝命令回显不符的帧");

            byte[] wrongId = (byte[])reply.Clone();
            wrongId[0] = 0x12;
            Check(!G87Protocol.TryParse(wrongId, out percent, out status), "G87 拒绝报告 ID 不符的帧");

            // ---------------- 有线通道 ----------------
            // 官方设备表：G87 有线身份 = PID 8705(0x2201) + usagePage 65280(0xFF00)，
            // 走 Feature 报告 0x06，命令串 "06 87 00 00 01 00 02 00" + 512 字节 0。
            CheckEqual(G87Protocol.WiredUsagePage, 0xFF00, "G87 有线 usagePage = 0xFF00");
            CheckEqual(G87Protocol.WirelessUsagePage, 0xFF04, "G87 无线 usagePage = 0xFF04");
            CheckEqual(G87Protocol.WiredReportId, (byte)0x06, "G87 有线 Feature 报告 ID = 0x06");
            CheckEqual(G87Protocol.WiredCmdBattery, (byte)0x87, "G87 有线命令 = 0x87");
            CheckEqual(G87Protocol.WiredFrameLen, 520, "G87 有线 Feature 报告长 520");

            byte[] wiredFeature = G87Protocol.BuildWiredBatteryFeature();
            CheckEqual(wiredFeature.Length, 520, "G87 有线 Feature 构造长度 520");
            CheckEqual(wiredFeature[0], (byte)0x06, "G87 有线 Feature 首字节 = 报告 ID 0x06");
            CheckEqual(wiredFeature[1], (byte)0x87, "G87 有线 Feature 次字节 = 命令 0x87");
            CheckEqual(wiredFeature[4], (byte)0x01, "G87 有线 Feature 第 5 字节 = 0x01");
            CheckEqual(wiredFeature[6], (byte)0x02, "G87 有线 Feature 第 7 字节 = 0x02");
            bool allZero = true;
            for (int i = 8; i < wiredFeature.Length; i++)
            {
                if (wiredFeature[i] != 0) { allZero = false; break; }
            }
            Check(allZero, "G87 有线 Feature 尾部 512 字节全为 0");

            // 官方定义：`06 87 00 00 01 00 02 00 ${"00 ".repeat(512).trim()}`
            // 即 8 字节头 + 512 字节 0 = 520 字节
            CheckEqual(wiredFeature[7], (byte)0x00, "G87 有线 Feature 第 8 字节 = 0x00");
            CheckEqual(wiredFeature.Length, 8 + 512, "G87 有线 Feature = 8 字节头 + 512 字节负载");

            // 官方 getBatteryParser = bae(true)：6 字节 space0 → batteryLevel → 两个 bit4。
            // 回读的 Feature 报告首字节是报告 ID，负载从第 1 字节起。
            // 官方 AE(n,e=2) 切前 2 字节后：d[0..5]=space0(6) d[6]=battery d[7]=status
            // 换算回整份报告：raw[2..7]=space0  raw[8]=batteryLevel  raw[9]=status
            var wiredBack = new byte[520];
            wiredBack[0] = 0x06;                  // 报告 ID
            wiredBack[1] = 0x00;                  // AE 切掉的第 2 字节
            wiredBack[2] = 0x00;                  // space0 起
            wiredBack[7] = 0x00;                  // space0 结束
            wiredBack[8] = 88;                    // batteryLevel
            wiredBack[9] = 0x10;                  // chargeStatus(高 4 位)=1
            Check(G87Protocol.TryParseWired(wiredBack, out percent, out status),
                "G87 解析有线 Feature 应答");
            CheckEqual(percent, 88, "G87 有线电量解析");
            CheckEqual(status, 0x10, "G87 有线状态解析");
            CheckEqual(G87Protocol.IsCharging(status), true, "G87 有线判定充电中");
            CheckEqual(G87Protocol.IsFull(status), false, "G87 有线满电位为 0");

            var wiredBack2 = (byte[])wiredBack.Clone();
            wiredBack2[9] = 0x01;                 // 低 4 位满电、高 4 位不充电
            Check(G87Protocol.TryParseWired(wiredBack2, out percent, out status),
                "G87 解析有线应答（满电）");
            CheckEqual(percent, 88, "G87 有线满电时电量不变");
            CheckEqual(G87Protocol.IsCharging(status), false, "G87 有线满电不充电");

            // 真机实测回包（有线模式，官方命令原文）：
            //   send  06 87 00 00 01 00 02 00 + 512×00
            //   recv  06 87 00 00 01 00 02 00 | 64 10 00 00 …
            var liveWired = new byte[520];
            liveWired[0] = 0x06;      // 报告 ID
            liveWired[1] = 0x87;      // 命令
            liveWired[2] = 0x00;
            liveWired[3] = 0x00;
            liveWired[4] = 0x01;
            liveWired[5] = 0x00;
            liveWired[6] = 0x02;
            liveWired[7] = 0x00;      // space0 结束
            liveWired[8] = 100;       // batteryLevel
            liveWired[9] = 0x10;      // 高4位=充电中
            Check(G87Protocol.TryParseWired(liveWired, out percent, out status),
                "G87 解析真机有线回包");
            CheckEqual(percent, 100, "G87 真机有线电量 = 100");
            CheckEqual(status, 0x10, "G87 真机有线 status = 0x10");
            CheckEqual(G87Protocol.IsCharging(status), true, "G87 真机有线判定充电中");
            CheckEqual(G87Protocol.IsFull(status), false, "G87 真机有线满电位为 0");

            Check(!G87Protocol.TryParseWired(null, out percent, out status),
                "G87 有线拒绝空应答");
            Check(!G87Protocol.TryParseWired(new byte[4], out percent, out status),
                "G87 有线拒绝过短应答");

            // ---------------- 蓝牙通道 ----------------
            // 本机实测（G87 切到蓝牙：BLE HID 身份 VID 3554 / PID FA07，
            // 厂商集合 usagePage 0xFF02、in=out=20、feat=0）：
            //   send  13 4A 01 00 00 … 5E        ← 与 2.4G 逐字节相同
            //   recv  13 4A 01 00 02 64 01 … C5  ← 与 2.4G 逐字节相同
            // 结论：蓝牙与 2.4G 共用同一套 0x13/0x4A 协议，差异只在设备身份
            // （VID/PID 不同、没有 Feature 通道）。
            Check(G87Protocol.IsKeyboardVendor(0x41E4), "G87 接受 USB 身份 0x41E4");
            Check(G87Protocol.IsKeyboardVendor(0x3554), "G87 接受蓝牙身份 0x3554");
            Check(!G87Protocol.IsKeyboardVendor(0x3837), "G87 拒绝 V7 鼠标身份 0x3837");

            CheckEqual(G87Protocol.ConnectModeFor(0x41E4, 0x2001, false), "2.4G",
                "0x2001 判定为 2.4G");
            CheckEqual(G87Protocol.ConnectModeFor(0x41E4, 0x2201, true), "有线",
                "0x2201 判定为有线");
            CheckEqual(G87Protocol.ConnectModeFor(0x3554, 0xFA07, false), "蓝牙",
                "0x3554/0xFA07 判定为蓝牙");

            Check(G87Protocol.TryParse(reply, out percent, out status),
                "G87 蓝牙回包与 2.4G 同构，可正常解析");
            CheckEqual(percent, 100, "G87 蓝牙电量 raw[5]=0x64 → 100%");
            CheckEqual(status, 0x01, "G87 蓝牙状态 raw[6]=0x01");
            CheckEqual(G87Protocol.IsCharging(status), false, "G87 蓝牙未接线不充电");
        }

        // ---------------------------------------------------------------- 帧构造

        private static void TestBuildFrame()
        {
            Console.WriteLine("BuildFrame");
            byte[] frame = MchoseProtocol.BuildFrame(MchoseProtocol.CmdDeviceInfo);

            CheckEqual(frame.Length, 64, "帧长 64");
            CheckEqual(frame[0], (byte)0x4D, "报告 ID = 0x4D");
            CheckEqual(frame[1], (byte)0x01, "固定头 0x01");
            CheckEqual(frame[2], (byte)1, "flags = 1");
            CheckEqual(frame[3], (byte)0, "数据长度 = 0");
            CheckEqual(frame[4], (byte)0x00, "命令低字节");
            CheckEqual(frame[5], (byte)0x09, "命令高字节");
            // 校验：xor(frame[2..8))
            byte expect = (byte)(1 ^ 0 ^ 0x00 ^ 0x09 ^ 0 ^ 0);
            CheckEqual(frame[8], expect, "校验和 = 1^0x09");

            byte[] withData = MchoseProtocol.BuildFrame(0x1234, new byte[] { 0xAA, 0xBB });
            CheckEqual(withData[3], (byte)2, "带数据时长度字段 = 2");
            CheckEqual(withData[8], (byte)0xAA, "数据首字节写入 offset 8");
            CheckEqual(withData[9], (byte)0xBB, "数据次字节写入 offset 9");
            withData[0] = 0; // 占位，避免分析器把未使用变量当问题

            // ---------------- 蓝牙身份（实测） ----------------
            // 鼠标拨到蓝牙后 USB 3837/1016 厂商接口不再应答，
            // BLE HID 身份为 0x05AC/0x024F，且没有任何厂商自定义集合，
            // 0x4D 帧在 20 字节通道上写会得到 87（报告 ID 不匹配）。
            CheckEqual(MchoseProtocol.BluetoothVendorId, 0x05AC, "V7 蓝牙 VID = 0x05AC");
            CheckEqual(MchoseProtocol.BluetoothProductId, 0x024F, "V7 蓝牙 PID = 0x024F");

            const string mouseBle =
                @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&0105ac_pid&024f_rev&0127_c4c3e6f5c2ad&col03#a&18addbf7&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string keyboardBle =
                @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&023554_pid&fa07_rev&6701_eb05166a828f&col02#a&208d6ef2&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

            CheckEqual(MchoseProtocol.ParseBluetoothAddress(mouseBle), "c4c3e6f5c2ad",
                "从鼠标 BLE 路径解析蓝牙地址");
            CheckEqual(MchoseProtocol.ParseBluetoothAddress(keyboardBle), "eb05166a828f",
                "从键盘 BLE 路径解析蓝牙地址");
            CheckEqual(MchoseProtocol.ParseBluetoothAddress(
                    @"\\?\hid#vid_3837&pid_1016&mi_02#8&1e410d00&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
                (string)null, "USB 路径没有蓝牙地址");
            CheckEqual(MchoseProtocol.ParseBluetoothAddress(null), (string)null,
                "空路径返回 null");
            CheckEqual(MchoseProtocol.ParseBluetoothAddress("_rev&0127_abc&col01"), (string)null,
                "地址长度不足返回 null");
            CheckEqual(MchoseProtocol.ParseBluetoothAddress("_rev&0127_zzzzzzzzzzzz&col01"), (string)null,
                "地址含非 hex 返回 null");
        }

        // ---------------------------------------------------------------- 帧解析

        private static byte[] MakeReply(byte[] payload, byte flags = 1, bool corruptChecksum = false)
        {
            // raw = [0x4D] + body；body[0]=0x01, body[1]=flags, body[2]=len,
            // body[3..4]=cmd, body[5]=seq, body[6]=biz, body[7..]=payload
            int len = payload.Length;
            var body = new byte[8 + len];
            body[0] = 0x01;
            body[1] = flags;
            body[2] = (byte)len;
            body[3] = (byte)(MchoseProtocol.CmdDeviceInfo & 0xFF);
            body[4] = (byte)(MchoseProtocol.CmdDeviceInfo >> 8);
            body[5] = 0;
            body[6] = 0;
            Array.Copy(payload, 0, body, 7, len);

            if (flags == 1)
            {
                byte checksum = 0;
                for (int i = 1; i < 7 + len; i++)
                {
                    checksum ^= body[i];
                }
                body[7 + len] = checksum;
            }

            var raw = new byte[1 + body.Length];
            raw[0] = 0x4D;
            Array.Copy(body, 0, raw, 1, body.Length);

            if (corruptChecksum)
            {
                raw[raw.Length - 1] ^= 0xFF;
            }
            return raw;
        }

        private static void TestParseFrame()
        {
            Console.WriteLine("TryParseFrame");

            var payload = new byte[20];
            payload[10] = 1;    // connect = 2.4G
            payload[11] = 0;    // charging
            payload[12] = 66;   // battery

            byte[] raw = MakeReply(payload);
            int cmd;
            byte[] body;
            int seq;
            bool ok = MchoseProtocol.TryParseFrame(raw, out cmd, out body, out seq);

            Check(ok, "合法帧解析成功");
            CheckEqual(cmd, MchoseProtocol.CmdDeviceInfo, "命令号一致");
            CheckEqual(body.Length, payload.Length, "负载长度一致");
            CheckEqual(body[12], (byte)66, "负载电量字节位置");

            // 某些路径会在报告 ID 前多带一个字节，解析器需要能在偏移 1 命中
            var prefixed = new byte[raw.Length + 1];
            prefixed[0] = 0x00;
            Array.Copy(raw, 0, prefixed, 1, raw.Length);
            Check(MchoseProtocol.TryParseFrame(prefixed, out cmd, out body, out seq),
                "首字节带前缀时偏移 1 解析");

            Check(!MchoseProtocol.TryParseFrame(MakeReply(payload, 1, true), out cmd, out body, out seq),
                "校验和错误时拒绝");
            Check(!MchoseProtocol.TryParseFrame(null, out cmd, out body, out seq), "null 输入返回 false");
            Check(!MchoseProtocol.TryParseFrame(new byte[3], out cmd, out body, out seq), "过短输入返回 false");
        }

        // ---------------------------------------------------------------- 负载解析

        private static void TestParseDeviceInfo()
        {
            Console.WriteLine("ParseDeviceInfo");

            var payload = new byte[20];
            payload[0] = 0x37; payload[1] = 0x38;   // VID 0x3837
            payload[2] = 0x16; payload[3] = 0x10;   // PID 0x1016
            payload[10] = 1;                         // 2.4G
            payload[11] = 0;                         // 放电
            payload[12] = 77;

            BatteryResult r = MchoseProtocol.ParseDeviceInfo(payload);
            Check(r != null, "正常负载可解析");
            CheckEqual(r.Percent, 77, "电量 77");
            CheckEqual(r.Charging, false, "未充电");
            CheckEqual(r.Vid, 0x3837, "VID");
            CheckEqual(r.Pid, 0x1016, "PID");

            // 连接方式不再由 payload 里的 connect 字节决定（官方 AK 的 connectStatus
            // 实测恒为 1，只表示"是否连接"），改由 productId 按官方 nT 名单判定。
            CheckEqual(MchoseProtocol.ConnectModeFor(0x1016), "2.4G",
                "官方 nT 名单含 4118(0x1016) → 2.4G");
            CheckEqual(MchoseProtocol.ConnectModeFor(0x402A), "有线",
                "0x402A 不在官方 nT 名单 → 有线");

            payload[11] = 1;
            BatteryResult charging = MchoseProtocol.ParseDeviceInfo(payload);
            CheckEqual(charging.Charging, true, "charge=1 表示充电中");

            Check(MchoseProtocol.ParseDeviceInfo(new byte[5]) == null, "过短负载返回 null");

            // ---------------- V7 有线模式的错误应答 ----------------
            // 实测：鼠标切到有线档时，本体接口(0x1016)回
            //   4D 01 01 01 00 09 00 00 | FE F7
            // dataLen=1 → payload 仅 1 字节，是"该接口不实现 0x0900"的错误码，
            // 必须与"无应答"区分：识别到就立刻换接口，不做无谓重发。
            CheckEqual(MchoseProtocol.IsErrorReply(new byte[1]), true,
                "V7 错误应答（dataLen=1）被识别");
            CheckEqual(MchoseProtocol.IsErrorReply(new byte[12]), true,
                "V7 不足 13 字节的应答被识别为错误");
            CheckEqual(MchoseProtocol.IsErrorReply(new byte[16]), false,
                "V7 正常 16 字节应答不是错误");
            CheckEqual(MchoseProtocol.IsErrorReply(null), true,
                "V7 null 应答被识别为错误");

            // 真机实测帧：鼠标切到有线档时本体接口的原始应答
            var errFrame = new byte[64];
            errFrame[0] = 0x4D; errFrame[1] = 0x01; errFrame[2] = 0x01;
            errFrame[3] = 0x01;                       // dataLen = 1
            errFrame[4] = 0x00; errFrame[5] = 0x09;  // cmd 0x0900
            errFrame[6] = 0x00; errFrame[7] = 0x00;
            errFrame[8] = 0xFE; errFrame[9] = 0xF7;  // 错误码
            int errCmd; byte[] errPayload; int errSeq;
            Check(MchoseProtocol.TryParseFrame(errFrame, out errCmd, out errPayload, out errSeq),
                "V7 错误帧 checksum 通过（需靠长度识别）");
            CheckEqual(errCmd, 0x0900, "V7 错误帧命令号仍是 0x0900");
            CheckEqual(errPayload.Length, 1, "V7 错误帧 payload 长度 = 1");
            Check(MchoseProtocol.IsErrorReply(errPayload),
                "V7 错误帧 payload 被识别为错误应答");
            Check(MchoseProtocol.ParseDeviceInfo(errPayload) == null,
                "V7 错误帧不会被解析成 0% 电量");

            // 真机实测帧（原文照抄）：
            //   4D 01 01 10 00 09 00 00 | 37 38 26 40 03 00 00 50 00 00 01 02 64 00 00 00 | 45 | 00…
            // body[2]=0x10 → dataLen=16；payload 从 body[7] 起 = 37 38 26 40 …
            var liveFrame = new byte[64];
            byte[] liveBytes = {
                0x4D, 0x01, 0x01, 0x10, 0x00, 0x09, 0x00, 0x00,
                0x37, 0x38, 0x26, 0x40, 0x03, 0x00, 0x00, 0x50,
                0x00, 0x00, 0x01, 0x02, 0x64, 0x00, 0x00, 0x00,
                0x45, 0x00, 0x00, 0x00,
            };
            Array.Copy(liveBytes, 0, liveFrame, 0, liveBytes.Length);
            // body[7] = payload 起点 → vid 在 payload[0..1]
            // payload[10]=connectStatus  payload[11]=chargeStatus  payload[12]=batteryLevel
            // 实际 body[7+10]=body[17]=0x01  body[7+11]=body[18]=0x02  body[7+12]=body[19]=0x64
            int liveCmd; byte[] livePayload; int liveSeq;
            Check(MchoseProtocol.TryParseFrame(liveFrame, out liveCmd, out livePayload, out liveSeq),
                "V7 真机应答帧可解析");
            CheckEqual(livePayload.Length, 16, "V7 真机应答 payload 长度 = 16");
            Check(!MchoseProtocol.IsErrorReply(livePayload), "V7 真机应答不是错误");
            BatteryResult liveR = MchoseProtocol.ParseDeviceInfo(livePayload);
            Check(liveR != null, "V7 真机应答可解析出电量");
            CheckEqual(liveR.Percent, 100, "V7 真机应答电量 = 100");
            CheckEqual(liveR.Charging, false, "V7 真机应答 chargeStatus=2 → 未充电");
            CheckEqual(liveR.Pid, 0x4026, "V7 真机应答内部子型号 pid = 0x4026");

            // 连接方式：本体(0x1016/4118) 在官方 nT 名单 → 2.4G；
            //           dongle(0x402A/16426) 不在名单 → 有线
            // 实测两态互斥：2.4G 档只有本体应答，有线档只有 dongle 应答。
            CheckEqual(MchoseProtocol.ConnectModeFor(0x1016), "2.4G",
                "V7 本体 0x1016 → 2.4G（官方 nT 含 4118）");
            CheckEqual(MchoseProtocol.ConnectModeFor(0x402A), "有线",
                "V7 dongle 0x402A → 有线（不在官方 nT）");
            Check(MchoseProtocol.ParseDeviceInfo(null) == null, "null 负载返回 null");
            // chargeStatus 语义：官方 UI 判 `chargeStatus === 1` 才是充电中。
            // 本机实测：2.4G 充电时 n[11]=0x01；USB 有线未充电时 n[11]=0x02。
            // 用 != 0 会把 0x02 误判成充电中，必须精确等于 1。
            var chargePayload = new byte[20];
            chargePayload[10] = 1;    // connect
            chargePayload[11] = 0x02; // chargeStatus = 2 → 未充电
            chargePayload[12] = 100;  // battery
            BatteryResult cr = MchoseProtocol.ParseDeviceInfo(chargePayload);
            Check(cr != null, "chargeStatus=2 可解析");
            CheckEqual(cr.Charging, false, "chargeStatus=0x02 不算充电中");
            CheckEqual(cr.Percent, 100, "chargeStatus=2 时电量正常");

            var chargePayload2 = (byte[])chargePayload.Clone();
            chargePayload2[11] = 0x01; // chargeStatus = 1 → 充电中
            CheckEqual(MchoseProtocol.ParseDeviceInfo(chargePayload2).Charging, true,
                "chargeStatus=0x01 判定充电中");

            var chargePayload3 = (byte[])chargePayload.Clone();
            chargePayload3[11] = 0x00; // chargeStatus = 0 → 未充电
            CheckEqual(MchoseProtocol.ParseDeviceInfo(chargePayload3).Charging, false,
                "chargeStatus=0x00 不算充电中");

            CheckEqual(MchoseProtocol.ChargeStatusCharging, (byte)1, "官方充电取值常量 = 1");

            // 官方 nT 名单（无线身份）：[4116, 4120, 4124, 4118, 4125]
            CheckEqual(MchoseProtocol.WirelessProductIds.Length, 5, "官方 nT 名单长度 = 5");
            foreach (int pid in new[] { 4116, 4120, 4124, 4118, 4125 })
            {
                CheckEqual(MchoseProtocol.ConnectModeFor(pid), "2.4G",
                    "nT 名单内 PID " + pid + " → 2.4G");
            }

            // 官方 LKt() 有线白名单：VID 14391 下 32896/24772/24579/24585/24774
            foreach (int pid in new[] { 32896, 24772, 24579, 24585, 24774 })
            {
                CheckEqual(MchoseProtocol.ConnectModeFor(pid), "有线",
                    "LKt 有线名单内 PID " + pid + " → 有线");
            }

        }

        private static void TestParseLegacy()
        {
            Console.WriteLine("ParseLegacy");

            // body 为取反后的真实内容；raw 里先放回显字节 0x06^0xFF，再放 body^0xFF
            var body = new byte[16];
            body[0] = 0x37; body[1] = 0x38;         // VID
            body[2] = 0x2A; body[3] = 0x40;         // PID 0x402A
            body[8] = 0x20;                          // status: (0x20>>5)&7 = 1 -> 2.4G
            body[9] = 55;                            // battery
            body[10] = 0x01;                         // charging

            var raw = new byte[body.Length + 2];
            raw[0] = 0x00;
            raw[1] = (byte)(MchoseProtocol.EchoByte ^ MchoseProtocol.XorMask);  // 0xF9
            for (int i = 0; i < body.Length; i++)
            {
                raw[2 + i] = (byte)(body[i] ^ MchoseProtocol.XorMask);
            }

            BatteryResult r = MchoseProtocol.ParseLegacy(raw);
            Check(r != null, "legacy 帧可解析");
            CheckEqual(r.Percent, 55, "legacy 电量");
            CheckEqual(r.Charging, true, "legacy 充电标志");
            CheckEqual(r.ConnectMode, "2.4G", "legacy 连接方式");
            CheckEqual(r.Vid, 0x3837, "legacy VID 校验");

            body[8] = 0x00;
            for (int i = 0; i < body.Length; i++)
            {
                raw[2 + i] = (byte)(body[i] ^ MchoseProtocol.XorMask);
            }
            CheckEqual(MchoseProtocol.ParseLegacy(raw).ConnectMode, "有线", "legacy status=0 表示有线");

            Check(MchoseProtocol.ParseLegacy(new byte[3]) == null, "过短输入返回 null");
            Check(MchoseProtocol.ParseLegacy(null) == null, "null 输入返回 null");
        }

        // ---------------------------------------------------------------- 状态机

        private static void TestStatusBuilder()
        {
            Console.WriteLine("StatusBuilder");

            var good = new LastGood { Percent = 66, Mode = StatusCodes.Mode24G, Updated = "12:00:00", HasValue = true };
            int fails;

            CollectResult ok = new CollectResult { Ok = true, Percent = 77, Charging = false, ConnectMode = "2.4G" };
            StatusSnapshot s = StatusBuilder.Build(ok, 20, 8, good, 3, out fails);
            CheckEqual(s.State, StatusCodes.StateOk, "正常 -> state=0");
            CheckEqual(s.Percent, 77, "正常 -> 电量");
            CheckEqual(s.Theme, StatusCodes.ThemeOk, "正常 -> theme=0");
            CheckEqual(s.Alert, StatusCodes.AlertNone, "正常 -> 无告警");
            CheckEqual(fails, 0, "正常 -> fails 归零");
            CheckEqual(s.PercentText, "77", "正常 -> 文本为数值");

            CollectResult low = new CollectResult { Ok = true, Percent = 15, Charging = false, ConnectMode = "2.4G" };
            s = StatusBuilder.Build(low, 20, 8, good, 0, out fails);
            CheckEqual(s.Theme, StatusCodes.ThemeLow, "低电 -> theme=3");
            CheckEqual(s.Alert, StatusCodes.AlertLow, "低电 -> alert=1");

            CollectResult critical = new CollectResult { Ok = true, Percent = 5, Charging = false, ConnectMode = "2.4G" };
            s = StatusBuilder.Build(critical, 20, 8, good, 0, out fails);
            CheckEqual(s.Theme, StatusCodes.ThemeCritical, "极低电 -> theme=4");
            CheckEqual(s.Alert, StatusCodes.AlertCritical, "极低电 -> alert=2");

            CollectResult charging = new CollectResult { Ok = true, Percent = 15, Charging = true, ConnectMode = "有线" };
            s = StatusBuilder.Build(charging, 20, 8, good, 0, out fails);
            CheckEqual(s.Alert, StatusCodes.AlertNone, "充电中不叠加低电告警");
            CheckEqual(s.Theme, StatusCodes.ThemeOk, "充电中 -> theme=0");
            CheckEqual(s.Mode, StatusCodes.ModeWired, "有线 -> mode=1");
            CheckEqual(s.Charging, StatusCodes.ChgOn, "充电中 -> charging=1");

            CollectResult offline = new CollectResult { Ok = false, Reason = "no_response" };
            s = StatusBuilder.Build(offline, 20, 8, good, 4, out fails);
            CheckEqual(s.State, StatusCodes.StateOffline, "无应答 -> state=1");
            CheckEqual(s.Theme, StatusCodes.ThemeOffline, "掉线 -> theme=1");
            CheckEqual(s.Percent, 66, "掉线保留上次电量");
            CheckEqual(s.PercentText, "66", "掉线时文本仍是旧数值");
            CheckEqual(s.Updated, "12:00:00", "掉线保留上次时间");
            CheckEqual(s.Charging, StatusCodes.ChgOffline, "掉线不再声称充放电");
            CheckEqual(fails, 5, "掉线 fails 累加");

            var empty = new LastGood();
            s = StatusBuilder.Build(offline, 20, 8, empty, 0, out fails);
            CheckEqual(s.PercentText, "--", "从未读到时显示 --");

            CollectResult error = new CollectResult { Ok = false, Reason = "no_dependency" };
            s = StatusBuilder.Build(error, 20, 8, good, 0, out fails);
            CheckEqual(s.State, StatusCodes.StateError, "依赖缺失 -> state=2");
            CheckEqual(s.Theme, StatusCodes.ThemeError, "异常 -> theme=2");
            CheckEqual(s.Alert, StatusCodes.AlertError, "异常 -> alert=4");

            CollectResult timeout = new CollectResult { Ok = false, Reason = "timeout" };
            s = StatusBuilder.Build(timeout, 20, 8, good, 0, out fails);
            CheckEqual(s.State, StatusCodes.StateOffline, "超时按掉线处理");

            CollectResult zero = new CollectResult { Ok = true, Percent = 0, Charging = false, ConnectMode = "2.4G" };
            s = StatusBuilder.Build(zero, 20, 8, good, 0, out fails);
            CheckEqual(s.Theme, StatusCodes.ThemeCritical, "电量 0 按极低电处理");

            CollectResult bluetooth = new CollectResult
            {
                Ok = true, Percent = 100, Charging = false, ConnectMode = "蓝牙"
            };
            s = StatusBuilder.Build(bluetooth, 20, 8, good, 0, out fails);
            CheckEqual(s.Mode, StatusCodes.ModeBluetooth, "蓝牙 -> mode=3");
            CheckEqual(s.State, StatusCodes.StateOk, "蓝牙 -> state=0");
            CheckEqual(s.Charging, StatusCodes.ChgIdle, "蓝牙未充电 -> charging=0");

            // 蓝牙鼠标走 BLE GATT：只有百分比，没有充放电方向。
            CollectResult ble = new CollectResult
            {
                Ok = true, Percent = 91, Charging = false, ChargingKnown = false, ConnectMode = "蓝牙"
            };
            s = StatusBuilder.Build(ble, 20, 8, good, 0, out fails);
            CheckEqual(s.Percent, 91, "BLE 电量 91");
            CheckEqual(s.Mode, StatusCodes.ModeBluetooth, "BLE -> mode=3");
            CheckEqual(s.Charging, StatusCodes.ChgOffline, "BLE 充电态未知 -> charging=2（界面留空）");
            CheckEqual(s.ChargingKnown, false, "BLE ChargingKnown=false");
            CheckEqual(s.Alert, StatusCodes.AlertNone, "BLE 91% 不触发告警");
        }

        // ------------------------------------------------- V9 Pro 2（官方桌面客户端 HidEQ 0x0B）

        /// <summary>
        /// 期望值全部取自真机：先用官方客户端把设备页打开，只读监听抓到的原始输入报告，
        /// 再用同一套字节反推请求、发出去拿到完全一致的回包。
        /// 抓到的原帧：55 AA 01 01 03 00 01 0B 01 56 5F（0x56 = 86%，与客户端显示一致）。
        /// </summary>
        private static void TestV9()
        {
            Console.WriteLine("V9Protocol");

            byte[] request = V9Protocol.BuildBatteryRequest();
            CheckEqual(request.Length, 64, "V9 请求帧长 64");
            CheckEqual(request[0], (byte)0x55, "V9 报告 ID 0x55");
            CheckEqual(request[1], (byte)0xAA, "V9 起始符 0xAA");
            CheckEqual(request[2], (byte)0x01, "V9 协议版本 0x01");
            CheckEqual(request[3], (byte)0x00, "V9 请求不带校验 flags=0");
            CheckEqual(request[4], (byte)0x01, "V9 length = payload(0)+1");
            CheckEqual(request[5], (byte)0x00, "V9 cmdType[0]=0（Request）");
            CheckEqual(request[6], (byte)0x01, "V9 cmdType[1]=1（Request）");
            CheckEqual(request[7], (byte)0x0B, "V9 命令 0x0B = GET_BATTERY_SLEEP");
            CheckEqual(request[8], (byte)0x00, "V9 无校验时校验位写 0");
            CheckEqual(request[63], (byte)0x00, "V9 请求尾部补零");

            // 校验算法与官方 C() 对齐：01 ^ 03 ^ 00 ^ 01 ^ 0B ^ 01 ^ 56 = 0x5F
            CheckEqual(
                V9Protocol.ComputeChecksum(0x01, 0x03, 0x00, 0x01, 0x0B, new byte[] { 0x01, 0x56 }),
                (byte)0x5F,
                "V9 抓包帧校验和 0x5F");

            byte[] realReply = Hex("55 AA 01 01 03 00 01 0B 01 56 5F", 64);
            V9Protocol.BatteryReading reading;
            bool ok = V9Protocol.TryParseBattery(realReply, out reading);
            Check(ok, "V9 真机回包可解析");
            if (ok)
            {
                CheckEqual(reading.Percent, 86, "V9 真机回包电量 86%");
                CheckEqual(reading.Charging, false, "V9 真机回包未充电");
                CheckEqual(reading.Asleep, false, "V9 真机回包非休眠");
            }

            // 充电态：bit7 置位，低 7 位仍是电量
            byte[] charging = Hex("55 AA 01 01 03 00 01 0B 01 D6 DF", 64);
            ok = V9Protocol.TryParseBattery(charging, out reading);
            Check(ok, "V9 充电帧可解析");
            if (ok)
            {
                CheckEqual(reading.Charging, true, "V9 bit7=1 判定为充电中");
                CheckEqual(reading.Percent, 86, "V9 充电时电量仍取低 7 位");
            }

            // 休眠：payload[0]=0，官方此时把 batteryLevel 置空
            byte[] asleep = Hex("55 AA 01 01 03 00 01 0B 00 56 5E", 64);
            ok = V9Protocol.TryParseBattery(asleep, out reading);
            Check(ok, "V9 休眠帧可解析");
            if (ok)
            {
                CheckEqual(reading.Asleep, true, "V9 payload[0]=0 判定为休眠");
            }

            // 失败路径：校验错、命令不符、报告 ID 不符、负载过短、通知类型
            byte[] badChecksum = Hex("55 AA 01 01 03 00 01 0B 01 56 5E", 64);
            Check(!V9Protocol.TryParseBattery(badChecksum, out reading), "V9 校验和错被拒绝");

            byte[] wrongCmd = Hex("55 AA 01 01 03 00 01 0C 01 56 58", 64);
            Check(!V9Protocol.TryParseBattery(wrongCmd, out reading), "V9 非 0x0B 命令被拒绝");

            byte[] wrongId = Hex("4D AA 01 01 03 00 01 0B 01 56 5F", 64);
            Check(!V9Protocol.TryParseBattery(wrongId, out reading), "V9 报告 ID 非 0x55 被拒绝");

            byte[] shortPayload = Hex("55 AA 01 01 02 00 01 0B 56 5A", 64);
            Check(!V9Protocol.TryParseBattery(shortPayload, out reading), "V9 负载不足 2 字节被拒绝");

            byte[] notify = Hex("55 AA 01 01 03 00 04 0B 01 56 5A", 64);
            Check(!V9Protocol.TryParseBattery(notify, out reading), "V9 通知类型 [0,4] 不当作应答");

            Check(!V9Protocol.TryParseBattery(null, out reading), "V9 null 输入不抛异常");
            Check(!V9Protocol.TryParseBattery(new byte[4], out reading), "V9 过短报告不抛异常");

            // 两种身份：0x6011 接收器（2.4G）、0x6010 耳机本体（有线），共用同一套帧
            Check(V9Protocol.IsHeadsetProduct(0x6011), "V9 接收器 PID 属于耳机");
            Check(V9Protocol.IsHeadsetProduct(0x6010), "V9 有线 PID 属于耳机");
            Check(!V9Protocol.IsHeadsetProduct(0x1016), "V7 鼠标 PID 不属于耳机");
            CheckEqual(V9Protocol.ConnectModeFor(0x6011), "2.4G", "V9 接收器 -> 2.4G");
            CheckEqual(V9Protocol.ConnectModeFor(0x6010), "有线", "V9 有线身份 -> 有线");

            // 有线档真机回包：0xD6 的 bit7 置位 = 充电中，低 7 位仍为 86
            byte[] wiredCharging = Hex("55 AA 01 01 03 00 01 0B 01 D6 DF", 64);
            ok = V9Protocol.TryParseBattery(wiredCharging, out reading);
            Check(ok, "V9 有线档充电动画帧可解析");
            if (ok)
            {
                CheckEqual(reading.Charging, true, "V9 有线档判定为充电中");
                CheckEqual(reading.Percent, 86, "V9 有线档电量 86%");
                CheckEqual(reading.Asleep, false, "V9 有线档非休眠");
            }
        }

        /// <summary>把十六进制串转成定长报告，便于用真实抓包字节写断言。</summary>
        private static byte[] Hex(string text, int length)
        {
            var result = new byte[length];
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < length; i++)
            {
                result[i] = Convert.ToByte(parts[i], 16);
            }
            return result;
        }
    }
}
