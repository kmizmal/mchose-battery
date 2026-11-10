# v1.3.1 · 修复耳机蓝牙断开后仍显示「已连接」

修一个会让人对插件失去信任的误判：V9 Pro 2 的蓝牙**已经断开**，卡片却一直显示「蓝牙 80%」。

## 根因

旧实现把「读到电池属性」当成「设备在线」。实测证明这个前提是错的：

| 断开后观测点 | 实际值 |
| --- | --- |
| HFP 节点 `BTHENUM\{0000111E-…}…&<MAC>_C00000000` | **仍存在**，`Status=OK`、`IsPresent=True`、`Problem=CM_PROB_NONE` |
| 该节点的 `DEVPKEY_Bluetooth_Battery` | **仍是 80**（断开前的残留值） |
| 根节点 `DEVPKEY_DeviceContainer_AlwaysShowDeviceAsConnected` | `True` |
| WinRT `BluetoothDevice.ConnectionStatus` | **Disconnected** ← 唯一说真话的源 |

节点常驻 + 属性是残留 ⇒ 条件永远成立 ⇒ 永远 `Ok=true`。

## 修复

读到属性后再用 **WinRT `ConnectionStatus`** 验一次真实链路：

| 查询结果 | 插件行为 |
| --- | --- |
| `Connected` | 正常出读数，`Detail` 追加 `conn=Connected` |
| `Disconnected` | 返回 `absent` → 卡片回落「已断开 + 最后读数 + 最后时间」 |
| 查询失败 / 超时 | **保留读数**并标 `conn=unknown(…)`，宁可多显示也不误报断开 |

配套改动：

- `Hid/V9Protocol.cs` 新增 `TryParseAddressFromInstanceId()`：从实例 ID 尾段 `&<12 位十六进制>_C…` 取蓝牙地址。该 hex 就是地址的 MSB-first 显示序（`&111122342037_` → `0x111122342037`），**不需要翻转字节序**；长度不足或含非 hex 字符一律拒绝。
- `Hid/BthLink.cs`（新增）：WinRT 查询封装，`Completed` 回调 + `ManualResetEvent` 同步等待 5 s。
- `MchoseBattery.csproj`：引用系统自带的拆分 winmd（`System32\WinMetadata\`），不引任何 NuGet 包，仍是零外部依赖。

## 验证

- 测试 **221 → 232 项全绿**（新增 11 条地址解析断言：正确解析、小写 hex、根节点不误解析、长度不足、非 hex、null/空串）
- 断开态真机：`Ok=False Reason=absent`，**62 ms**
- 连接态真机：`Ok=True Percent=80 Mode=蓝牙`，`Detail` 含 `conn=Connected`，**63 ms**
- 断开 / 连接两态切换后卡片正确回落与恢复（手动点击刷新实测）
- x64 / x86 双架构构建通过，6 个导出齐全
- 构建上的一个坑：`ildasm → ilasm` 的 IL 往返**不会丢掉** `.assembly extern windowsruntime` 引用，WinRT 调用经往返后仍能正常加载与执行（已实测）

## 安装

下载 `MCHOSE_Battery_1.3.rmskin` 双击安装即可覆盖。若安装器提示插件 DLL 被占用，先完全退出 Rainmeter（托盘图标右键退出）再装。

- 文件大小：140.5 KB（143875 字节）
- SHA256：`D9A45191B07D7D7921D9277B184291EF01DC957EB57D145463469CCB2F93D571`

> 本项目为第三方开源项目，与迈从（MCHOSE）官方无关，未获其授权或认可。
> 插件只通过公开的 HID / BLE 接口读取设备上报的数据，不修改固件、不写入设备配置。
