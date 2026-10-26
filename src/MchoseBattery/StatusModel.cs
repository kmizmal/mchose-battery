using System;
using MchoseBattery.Hid;

namespace MchoseBattery
{
    /// <summary>一次采集的归一化结果，供状态机消费。</summary>
    internal sealed class CollectResult
    {
        public bool Ok;
        public int Percent;
        public bool Charging;

        /// <summary>
        /// 充电状态是否可信。BLE GATT 只上报百分比、没有充放电信息，
        /// 蓝牙鼠标这条路径必须置 false，避免把"未知"显示成"放电中"。
        /// </summary>
        public bool ChargingKnown = true;

        public string ConnectMode = string.Empty;
        public string Reason = "ok";
        public string Detail = string.Empty;
    }

    /// <summary>状态码。数值直接对应皮肤里的 Substitute 映射，不要改动。</summary>
    internal static class StatusCodes
    {
        public const int StateOk = 0;
        public const int StateOffline = 1;
        public const int StateError = 2;

        public const int ChgIdle = 0;
        public const int ChgOn = 1;
        public const int ChgOffline = 2;

        public const int Mode24G = 0;
        public const int ModeWired = 1;
        public const int ModeUnknown = 2;

        /// <summary>蓝牙模式，对应皮肤 Substitute 的 "^3$"。</summary>
        public const int ModeBluetooth = 3;

        public const int AlertNone = 0;
        public const int AlertLow = 1;
        public const int AlertCritical = 2;
        public const int AlertOffline = 3;
        public const int AlertError = 4;

        public const int ThemeOk = 0;
        public const int ThemeOffline = 1;
        public const int ThemeError = 2;
        public const int ThemeLow = 3;
        public const int ThemeCritical = 4;
    }

    /// <summary>一次可对外展示的完整状态。创建后不再修改，可安全跨线程读取。</summary>
    internal sealed class StatusSnapshot
    {
        public int State = StatusCodes.StateOffline;
        public int Percent;
        public string PercentText = "--";
        public int Charging = StatusCodes.ChgOffline;
        public int Mode = StatusCodes.ModeUnknown;
        public int Alert = StatusCodes.AlertOffline;
        public int Theme = StatusCodes.ThemeOffline;
        public int Fails;
        public string Updated = "--:--:--";
        public string Reason = "idle";
        public bool ChargingKnown;

        public static StatusSnapshot Initial()
        {
            return new StatusSnapshot();
        }
    }

    /// <summary>最后一次成功读取的值，掉线时继续沿用，避免界面清零。</summary>
    internal sealed class LastGood
    {
        public int Percent;
        public int Mode;
        public string Updated = "--:--:--";
        public bool HasValue;
    }

    internal static class StatusBuilder
    {
        /// <summary>把一次采集结果折算成展示状态。</summary>
        internal static StatusSnapshot Build(
            CollectResult result, int low, int critical,
            LastGood lastGood, int previousFails, out int fails)
        {
            var snap = new StatusSnapshot();

            if (result.Ok)
            {
                fails = 0;
                snap.State = StatusCodes.StateOk;
                snap.Percent = Math.Max(0, Math.Min(100, result.Percent));
                snap.Charging = result.ChargingKnown
                    ? (result.Charging ? StatusCodes.ChgOn : StatusCodes.ChgIdle)
                    : StatusCodes.ChgOffline;
                snap.ChargingKnown = result.ChargingKnown;
                snap.Mode = MapMode(result.ConnectMode);
                snap.Updated = DateTime.Now.ToString("HH:mm:ss");
                snap.Reason = "ok";

                // 充电中是恢复过程，不叠加低电告警
                if (result.Charging)
                {
                    snap.Alert = StatusCodes.AlertNone;
                }
                else if (snap.Percent <= critical)
                {
                    snap.Alert = StatusCodes.AlertCritical;
                }
                else if (snap.Percent <= low)
                {
                    snap.Alert = StatusCodes.AlertLow;
                }
                else
                {
                    snap.Alert = StatusCodes.AlertNone;
                }
            }
            else
            {
                fails = previousFails + 1;
                snap.State = MapState(result.Reason);
                snap.Reason = result.Reason ?? "exception";
                snap.Alert = snap.State == StatusCodes.StateError
                    ? StatusCodes.AlertError
                    : StatusCodes.AlertOffline;

                // 掉线不清零：沿用最后一次成功读数，界面靠灰色 + 「已断开」表达旧值
                if (lastGood != null && lastGood.HasValue)
                {
                    snap.Percent = lastGood.Percent;
                    snap.Mode = lastGood.Mode;
                    snap.Updated = lastGood.Updated;
                }
                snap.Charging = StatusCodes.ChgOffline;
                snap.ChargingKnown = false;
            }

            snap.Fails = fails;
            snap.PercentText = (snap.State == StatusCodes.StateOk || (lastGood != null && lastGood.HasValue))
                ? snap.Percent.ToString()
                : "--";

            if (snap.State == StatusCodes.StateOffline)
            {
                snap.Theme = StatusCodes.ThemeOffline;
            }
            else if (snap.State == StatusCodes.StateError)
            {
                snap.Theme = StatusCodes.ThemeError;
            }
            else if (snap.Alert == StatusCodes.AlertCritical)
            {
                snap.Theme = StatusCodes.ThemeCritical;
            }
            else if (snap.Alert == StatusCodes.AlertLow)
            {
                snap.Theme = StatusCodes.ThemeLow;
            }
            else
            {
                snap.Theme = StatusCodes.ThemeOk;
            }

            return snap;
        }

        private static int MapMode(string mode)
        {
            if (mode == "2.4G")
            {
                return StatusCodes.Mode24G;
            }
            if (mode == "有线")
            {
                return StatusCodes.ModeWired;
            }
            if (mode == "蓝牙")
            {
                return StatusCodes.ModeBluetooth;
            }
            return StatusCodes.ModeUnknown;
        }

        private static int MapState(string reason)
        {
            switch (reason)
            {
                case "no_source":
                case "no_dependency":
                case "exception":
                case "bad_output":
                    return StatusCodes.StateError;
                default:
                    // absent / no_response / timeout / idle 都表现为"设备没应答"
                    return StatusCodes.StateOffline;
            }
        }
    }
}
