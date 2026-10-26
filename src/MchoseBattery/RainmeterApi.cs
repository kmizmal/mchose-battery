using System;
using System.Runtime.InteropServices;

namespace MchoseBattery
{
    /// <summary>
    /// Rainmeter 插件 C API 的精简封装。只声明本插件用到的函数，
    /// 全部由 Rainmeter.exe 自身导出，无需额外链接库。
    /// </summary>
    internal static class RainmeterApi
    {
        private const string Dll = "Rainmeter.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr RmReadString(
            IntPtr rm,
            [MarshalAs(UnmanagedType.LPWStr)] string option,
            [MarshalAs(UnmanagedType.LPWStr)] string defValue,
            int replaceMeasures);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RmReadInt(
            IntPtr rm,
            [MarshalAs(UnmanagedType.LPWStr)] string option,
            int defValue);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double RmReadDouble(
            IntPtr rm,
            [MarshalAs(UnmanagedType.LPWStr)] string option,
            double defValue);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RmExecute(
            IntPtr rm,
            [MarshalAs(UnmanagedType.LPWStr)] string command);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RmLog(
            IntPtr rm,
            int level,
            [MarshalAs(UnmanagedType.LPWStr)] string message);

        internal const int LogNotice = 2;
        internal const int LogWarning = 3;
        internal const int LogError = 4;

        internal static void ReadString(IntPtr rm, string option, string defValue, out string value)
        {
            IntPtr ptr = RmReadString(rm, option, defValue ?? string.Empty, 0);
            value = ptr == IntPtr.Zero ? (defValue ?? string.Empty) : (Marshal.PtrToStringUni(ptr) ?? defValue ?? string.Empty);
            value = value.Trim();
        }

        internal static int ReadInt(IntPtr rm, string option, int defValue)
        {
            // 用字符串读再自己转，避免用户填了非数字时 Rainmeter 直接返回默认值而看不出问题
            string raw;
            ReadString(rm, option, null, out raw);
            if (string.IsNullOrEmpty(raw))
            {
                return defValue;
            }
            int parsed;
            return int.TryParse(raw, out parsed) ? parsed : defValue;
        }

        internal static double ReadDouble(IntPtr rm, string option, double defValue)
        {
            string raw;
            ReadString(rm, option, null, out raw);
            if (string.IsNullOrEmpty(raw))
            {
                return defValue;
            }
            double parsed;
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed) ? parsed : defValue;
        }
    }
}
