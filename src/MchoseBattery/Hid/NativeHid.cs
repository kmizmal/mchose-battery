using System;
using System.Runtime.InteropServices;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// HID 设备的原生枚举与读写。用 SetupAPI + hid.dll 直连，
    /// 不依赖 hidapi.dll，省掉一层外部依赖。
    /// </summary>
    internal static class NativeHid
    {
        // ---------------- 常量 ----------------
        internal const uint DigcfPresent = 0x02;
        internal const uint DigcfDeviceInterface = 0x10;

        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x01;
        internal const uint FileShareWrite = 0x02;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;

        internal const int ErrorIoPending = 997;
        internal const uint WaitObject0 = 0x00000000;
        internal const uint WaitTimeout = 0x00000102;
        internal const uint Infinite = 0xFFFFFFFF;

        internal const int HidpStatusSuccess = 0x00110000;

        internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        // ---------------- 结构 ----------------
        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr EventHandle;
        }

        // ---------------- P/Invoke ----------------
        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(IntPtr device, ref HiddAttributes attributes);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetFeature(IntPtr device, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetFeature(IntPtr device, byte[] reportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
            out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool ReadFile(
            IntPtr file, byte[] buffer, int numberOfBytesToRead,
            IntPtr numberOfBytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool WriteFile(
            IntPtr file, byte[] buffer, int numberOfBytesToWrite,
            IntPtr numberOfBytesWritten, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool GetOverlappedResult(
            IntPtr file, ref NativeOverlapped overlapped, out int numberOfBytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CancelIo(IntPtr file);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateMutex(IntPtr attributes, bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool ReleaseMutex(IntPtr mutex);
    }
}
