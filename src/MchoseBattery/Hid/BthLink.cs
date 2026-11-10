using System;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Foundation;

namespace MchoseBattery.Hid
{
    /// <summary>
    /// 经典蓝牙真实连接状态判定（WinRT `BluetoothDevice.ConnectionStatus`）。
    ///
    /// 为什么需要它（2026-11-10 实证，ZMAL-PC / Win11 26100）：
    ///   · 耳机断开蓝牙后，BTHENUM 的 HFP 服务节点**不会消失**：
    ///     `DEVPKEY_DeviceContainer_AlwaysShowDeviceAsConnected = True`，
    ///     节点仍 Present、Status=OK、Problem=CM_PROB_NONE；
    ///   · 节点上的 `DEVPKEY_Bluetooth_Battery` **残留断开前的旧值**（实测仍读 80%）。
    ///   因此「读到电池属性」不能作为「设备在线」的判据（旧实现的误判根因）。
    ///   真实链路状态要用 WinRT：断开时 `ConnectionStatus = Disconnected`。
    ///
    /// 实现约束：
    ///   · 目标框架 net48 + 系统自带拆分 winmd（System32\WinMetadata），无 NuGet 依赖；
    ///   · 不用 AsTask/System.Runtime.WindowsRuntime——GAC 里那份绑定的是 union
    ///     Windows.winmd，与拆分 winmd 类型标识冲突；直接挂 Completed 回调同步等待；
    ///   · 经 IL 往返（ildasm → ilasm，build_plugin.py 的导出注入流程）后 WinRT
    ///     引用保留 `windowsruntime` ContentType，加载与调用已实测正常。
    /// </summary>
    internal static class BthLink
    {
        /// <summary>查询超时。正常查询 20 ms 内返回，5 s 足够覆盖蓝牙服务繁忙。</summary>
        private const int TimeoutMs = 5000;

        /// <summary>
        /// 查询指定地址的经典蓝牙设备当前是否连接。
        /// 返回 false 表示「无法确认」（超时/异常/设备不存在），调用方应降级保留读数，
        /// 而不是武断地显示已断开。
        /// </summary>
        internal static bool TryIsConnected(ulong address, out bool connected, out string detail)
        {
            connected = false;
            detail = null;

            try
            {
                IAsyncOperation<BluetoothDevice> op =
                    BluetoothDevice.FromBluetoothAddressAsync(address);
                if (op == null)
                {
                    detail = "连接状态查询未发起";
                    return false;
                }

                var done = new ManualResetEvent(false);
                op.Completed = delegate(IAsyncOperation<BluetoothDevice> a, AsyncStatus s)
                {
                    done.Set();
                };

                // 回调可能在挂载前已完成：WaitOne 前先看一眼状态。
                if (op.Status == AsyncStatus.Started)
                {
                    done.WaitOne(TimeoutMs);
                }

                if (op.Status != AsyncStatus.Completed)
                {
                    detail = "连接状态查询未完成：" + op.Status;
                    return false;
                }

                BluetoothDevice device = op.GetResults();
                if (device == null)
                {
                    detail = "连接状态查询返回空设备";
                    return false;
                }

                connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                detail = device.ConnectionStatus.ToString();
                return true;
            }
            catch (Exception ex)
            {
                detail = "连接状态查询异常：" + ex.Message;
                return false;
            }
        }
    }
}
