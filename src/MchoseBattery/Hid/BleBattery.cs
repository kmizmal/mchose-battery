using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// 通过 BLE GATT Battery Service 读电量。
    ///
    /// V7 拨到蓝牙档后，HID 层没有任何厂商自定义集合（见 MchoseProtocol.BluetoothVendorId
    /// 的注释），0x4D 帧无通道可用。Windows 会在 BLE 设备节点上暴露
    /// DEVPKEY_Bluetooth_Battery（{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2），
    /// 值即 Battery Level 百分比。这里用 SetupAPI 枚举 BTHLE 设备后读该属性，
    /// 不引入 WinRT 依赖。
    ///
    /// 定位方式：先枚举 HID 接口拿到目标的蓝牙地址（VID/PID 是硬判据），
    /// 再在 BTHLE 枚举器下按 `BTHLE\DEV_&lt;MAC&gt;\...` 前缀匹配设备实例。
    ///
    /// 已知限制：GATT 只提供百分比，**没有充电状态**，因此调用方必须把
    /// ChargingKnown 置为 false，不能把"未知"显示成"放电中"。
    /// </summary>
    internal static class BleBattery
    {
        private const uint DigcfPresent = 0x02;
        private const uint DigcfAllClasses = 0x04;
        // DEVPROP_TYPE_*：BYTE=0x03，INT16=0x04，UINT16=0x05，INT32=0x06，UINT32=0x07
        private const uint DevPropTypeSByte = 0x02;
        private const uint DevPropTypeByte = 0x03;
        private const uint DevPropTypeInt16 = 0x04;
        private const uint DevPropTypeUint16 = 0x05;
        private const uint DevPropTypeInt32 = 0x06;
        private const uint DevPropTypeUint32 = 0x07;

        private static readonly Guid BatteryFmtId =
            new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        private const uint BatteryPid = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            public Guid FmtId;
            public uint Pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevInfoData
        {
            public int CbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
            StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref DevPropKey propertyKey,
            out uint propertyType, byte[] propertyBuffer, int propertyBufferSize,
            out int requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        /// <summary>
        /// 在 HID 接口里找指定 VID/PID 的 BLE 设备，取其蓝牙地址后读 GATT 电量。
        /// </summary>
        internal static bool TryQuery(int vendorId, int productId, out int percent, out string detail)
        {
            percent = 0;
            detail = null;

            string address;
            try
            {
                address = FindBluetoothAddress(vendorId, productId);
            }
            catch (Exception ex)
            {
                detail = "枚举 BLE 接口失败：" + ex.Message;
                return false;
            }

            if (string.IsNullOrEmpty(address))
            {
                detail = string.Format("未找到 BLE 身份 {0:X4}/{1:X4}", vendorId, productId);
                return false;
            }

            string prefix = "BTHLE\\DEV_" + address.ToUpperInvariant() + "\\";
            int value;
            string instanceId;
            if (!TryReadBattery(prefix, out value, out instanceId, out detail))
            {
                return false;
            }

            percent = value;
            detail = string.Format("GATT Battery {0}% · {1}", value, instanceId);
            return true;
        }

        /// <summary>在 BTHLE 枚举器下按实例 ID 前缀定位设备并读电池属性。</summary>
        private static bool TryReadBattery(
            string instancePrefix, out int percent, out string instanceId, out string detail)
        {
            percent = 0;
            instanceId = null;
            detail = null;

            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, "BTHLE", IntPtr.Zero,
                                             DigcfPresent | DigcfAllClasses);
            if (set == NativeHid.InvalidHandleValue)
            {
                detail = "打开 BTHLE 设备集合失败";
                return false;
            }

            try
            {
                var did = new SpDevInfoData();
                did.CbSize = Marshal.SizeOf(typeof(SpDevInfoData));

                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref did); index++)
                {
                    string id = GetInstanceId(set, ref did);
                    if (string.IsNullOrEmpty(id) ||
                        !id.StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int value;
                    if (TryReadProperty(set, ref did, out value, out detail))
                    {
                        percent = value;
                        instanceId = id;
                        return true;
                    }
                }

                if (detail == null)
                {
                    detail = "未找到实例 " + instancePrefix + "*";
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        private static string GetInstanceId(IntPtr set, ref SpDevInfoData did)
        {
            int required;
            SetupDiGetDeviceInstanceIdW(set, ref did, null, 0, out required);
            if (required <= 1)
            {
                return null;
            }

            var sb = new StringBuilder(required);
            int got;
            if (!SetupDiGetDeviceInstanceIdW(set, ref did, sb, required, out got))
            {
                return null;
            }
            return sb.ToString();
        }

        private static bool TryReadProperty(
            IntPtr set, ref SpDevInfoData did, out int percent, out string detail)
        {
            percent = 0;
            detail = null;

            var key = new DevPropKey { FmtId = BatteryFmtId, Pid = BatteryPid };
            var buffer = new byte[16];
            uint type;
            int required;

            if (!SetupDiGetDevicePropertyW(set, ref did, ref key, out type, buffer,
                                           buffer.Length, out required, 0))
            {
                detail = "设备未暴露电池属性";
                return false;
            }

            int value;
            if (type == DevPropTypeByte)
            {
                value = buffer[0];
            }
            else if (type == DevPropTypeSByte)
            {
                value = (sbyte)buffer[0];
            }
            else if (type == DevPropTypeInt16 || type == DevPropTypeUint16)
            {
                value = BitConverter.ToInt16(buffer, 0);
            }
            else if (type == DevPropTypeInt32 || type == DevPropTypeUint32)
            {
                value = BitConverter.ToInt32(buffer, 0);
            }
            else
            {
                detail = string.Format("电池属性类型未知（{0}）", type);
                return false;
            }

            if (value < 0 || value > 100)
            {
                detail = string.Format("电池属性越界（{0}）", value);
                return false;
            }

            percent = value;
            return true;
        }

        /// <summary>枚举 HID 接口，挑出目标 BLE 身份并从其路径解析蓝牙地址。</summary>
        private static string FindBluetoothAddress(int vendorId, int productId)
        {
            Guid hidGuid;
            NativeHid.HidD_GetHidGuid(out hidGuid);

            IntPtr set = NativeHid.SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                NativeHid.DigcfPresent | NativeHid.DigcfDeviceInterface);
            if (set == NativeHid.InvalidHandleValue)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                    if (!MatchesIdentity(path, vendorId, productId))
                    {
                        continue;
                    }

                    string address = MchoseProtocol.ParseBluetoothAddress(path);
                    if (!string.IsNullOrEmpty(address))
                    {
                        return address;
                    }
                }
            }
            finally
            {
                NativeHid.SetupDiDestroyDeviceInfoList(set);
            }

            return null;
        }

        private static bool MatchesIdentity(string path, int vendorId, int productId)
        {
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
                if (!NativeHid.HidD_GetAttributes(handle, ref attr))
                {
                    return false;
                }
                return attr.VendorId == vendorId && attr.ProductId == productId;
            }
            finally
            {
                NativeHid.CloseHandle(handle);
            }
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
    }
}
