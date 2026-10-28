# MCHOSE 电量 · Rainmeter 皮肤（C# 插件版）

[![build](https://github.com/kmizmal/mchose-battery/actions/workflows/build.yml/badge.svg)](https://github.com/kmizmal/mchose-battery/actions/workflows/build.yml)

显示迈从 **V7 鼠标**、**G87 键盘** 与 **V9 Pro 2 耳机** 的连接方式、剩余电量、充放电状态，并在掉线/低电时给出明确提示。
数据由随包安装的原生 **C# 插件** 直接读 HID / BLE，零外部依赖（不需要 Python、hidapi 或中间状态文件）。

> **免责声明**：本项目是第三方开源项目，与迈从（MCHOSE）官方**没有任何关系**，也未获得其授权或认可。
> 插件只通过公开的 HID / BLE 接口**读取**设备上报的电量与连接状态，不修改固件、不写入配置、
> 不改变设备任何行为。MCHOSE、V7、G87、V9 等名称、产品外观与商标均归迈从所有，仓库内的产品图
> 仅用于皮肤界面展示。使用本项目的风险由使用者自行承担，详见文末[许可证](#许可证)一节。

一套皮肤里放三张卡片，各管各的设备：

```
MCHOSE_Battery/
├─ @Resources/                共用资源（Variables.inc、产品图、说明）
├─ Mouse/V7Battery.ini        Config = MCHOSE_Battery\Mouse
├─ Keyboard/G87Battery.ini    Config = MCHOSE_Battery\Keyboard
└─ Headset/V9Battery.ini      Config = MCHOSE_Battery\Headset
```

> 必须拆成独立子目录：Rainmeter 里**同一 Config 下的 variant 是互斥的**，
> 多个 `.ini` 放同一目录时，加载后一个会把前一个替换掉。

## 交付内容

日常使用只需要安装包 `MCHOSE_Battery_1.1.rmskin`（皮肤 + 插件 DLL），从
[Releases](https://github.com/kmizmal/mchose-battery/releases) 下载后双击安装。仓库内的源码结构：

| 路径 | 说明 |
| --- | --- |
| `src/MchoseBattery/` | 插件源码（.NET Framework 4.8 / x64） |
| `MCHOSE_Battery/` | 皮肤源码（`.ini`/`.inc` 为 UTF-8 编辑源，打包时转 UTF-16） |
| `tools/HidProbe/` | HID 探测工具（枚举 / 抓帧 / 特征读 / 端到端验证） |
| `tools/ilasm/` | `ildasm` / `ilasm`，用于给托管 DLL 注入原生导出 |
| `build_plugin.py` | 构建插件：`dotnet build` → `ildasm` → 注入 `.export` → `ilasm` → 校验 |
| `build_package.py` | 打包：编码转换 → 生成 `RMSKIN.ini` → 压缩 → 追加 footer → 自检 |
| `tests/ProtocolTests/` | 协议与状态机的可执行测试（212 项） |
| `preview.png` | 实际运行截图 |
| `make_icons.py` | 生成卡片配色图标（12 张产品图） |

`dist/` 与各项目的 `bin/`、`obj/` 都是可再生产物，不入库，按[第六节](#六构建)重新生成。

## 一、架构

```
Rainmeter 主线程                    线程池                     HID 设备
─────────────────                  ────────                   ─────────
[MeasureV7].Update()  ──到点触发──▶  HidBattery.Query()
     │                                │  SetupAPI 枚举 → WriteFile(0x4D 帧)
     │                                │  ReadFile(OVERLAPPED + 超时)
     │                                │  无厂商通道 → 回退 BLE GATT 电池
     │                                ▼
     │                           StatusBuilder 归一化
     │                                │
     └──读缓存──▶ StatusSnapshot ◀────┘（lock 保护，不可变对象）
                       │
                       ├─▶ [MeasurePercentText] Field=PercentText
                       ├─▶ [MeasureTheme]      Field=Theme
                       └─▶ [MeasureUpdated]    Field=Updated
```

关键设计：**采集永远不在主线程**。`Update` 只读一份不可变快照，到点才把一次采集丢给线程池；
HID 往返（设备休眠时要等超时）不会卡住 Rainmeter 的任何一帧。

皮肤里有两类 measure：

- `[MeasureV7]` / `[MeasureG87]` —— **主实例**，不写 `Field`，负责按间隔触发采集；
- `Field=State/Percent/PercentText/Charging/Mode/Alert/Theme/Updated/Fails/Reason/Device` —— **字段实例**，
  只读同一份共享缓存，不重复采集。

## 二、数据获取逻辑

设备匹配（沿用官方脚本，不写死 PID）：

```c
VID       = 0x3837
PID       = 0x1016 / 0x402A      // 同一只 V7 的两种枚举模式
UsagePage = 0xFF01 / 0xFF0B      // 厂商自定义页
```

主通道是 64 字节的厂商帧：

```c
ReportID = 0x4D, CMD = 0x0900（设备信息，含电量）
帧结构：[0]=0x4D [1]=0x01 [2]=flags [3]=len [4..5]=cmd [6]=biz [7]=seq [8..]=data [+checksum]
checksum = XOR(frame[2 .. 7+len])
```

应答负载偏移（官方 `AK` 解析器原文）：
`10=connectStatus`、`11=chargeStatus`、`12=batteryLevel`、`13=gameMode`。
首字节 `0xFF` 表示设备忙，继续等；校验和不符则丢弃。连续 6 次（每次 450 ms）无有效应答后，
回退到前代通道：`HidD_SetFeature(0x11)` 写入 `[0x11,0x06,…]`，回包整体 XOR `0xFF` 后按固定偏移取值。

#### 连接方式与充电状态：官方判定依据

这两个字段**不能**从 payload 字节猜，官方有明确规则：

| 字段 | 官方判定 | 出处 |
| --- | --- | --- |
| 连接方式 | `connectMode = nT.includes(productId) ? 1(无线) : 0(有线)`，`nT = [4116, 4120, 4124, 4118, 4125]` | 官方鼠标页调用 `hgt()` 处 |
| 充电中 | `chargeStatus === 1`（UI 用 `chargeStatus!==1` 走放电配色、`===1` 走充电配色） | 官方电池状态组件 |

要点：

- **`0x1016` (4118) 就在官方 `nT` 名单里 → 无线（2.4G）**。
  官方 `LKt()` 的有线白名单（`32896/24772/24579/24585/24774` @ VID 14391）
  同样不含它 —— 两条独立规则结论一致。
  本机 V7 实测枚举就是 `3837/0x1016`，因此**鼠标插线时也仍显示「2.4G 无线」**
  （V7 插线只是充电，数据仍走 2.4G 链路）。
- `connectStatus`（偏移 10）实测恒为 `1`，只表示"是否连接"，**不能**当连接方式用。
- `chargeStatus` 实测 `0x01` = 充电中、`0x02` = 未充电。
  用 `!= 0` 判断会把 `0x02` 误判成充电中，必须精确等于 `1`。

#### 蓝牙档：HID 层没有厂商通道，改读 BLE GATT

鼠标拨到蓝牙后，`3837/1016` 的厂商接口仍会枚举出来，但**整条链路不再代答**
（`SetFeature(0x11)` 直接失败）。真实身份换成 BLE HID：

| 项目 | 实测结果 |
| --- | --- |
| 蓝牙身份 | `VID 0x05AC / PID 0x024F`（设备名 MCHOSE V7） |
| collection | 5 条，全部是标准 HID（键盘 / 系统控制 / 鼠标 / 消费控制 / NKRO） |
| 厂商自定义集合 | **0 条**，没有 `0xFFxx`，也就没有 20B / 64B 通道 |
| `0x4D` 帧能否复用 | **不能**：向键盘那条 20B 通道写入返回 `87`（报告 ID 不匹配） |

因此蓝牙下唯一的电量来源是 BLE 标准的 **GATT Battery Service**。
Windows 把它映射为设备属性 `DEVPKEY_Bluetooth_Battery`
（`{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2`），插件用 SetupAPI 直接读：

1. 枚举 HID 接口，按 `VID 0x05AC / PID 0x024F` 命中 BLE 身份；
2. 从接口路径 `…_rev&0127_c4c3e6f5c2ad&col03…` 取出蓝牙地址；
3. 在 `BTHLE` 枚举器下按 `BTHLE\DEV_<地址>\…` 前缀定位设备实例；
4. 读电池属性 → 91%（实测，7 ms）。

> `CM_Locate_DevNode` 需要**完整实例 ID**（含 `\8&13B500A9&0&…`）；
> 只给 `BTHLE\DEV_<MAC>` 会返回 `0x1E`，必须走 SetupAPI 枚举后按前缀匹配。

**能力差异（如实说明）**：GATT 只提供百分比，没有充放电方向。
蓝牙鼠标的 `charging` 因此输出 `2`（未知），皮肤按空串处理，
不会把"不知道"显示成"放电中"——`CollectResult.ChargingKnown` 就是为此新增的。

失败原因被归一成一套状态码，皮肤只做声明式映射：

| reason | 含义 | 界面表现 |
| --- | --- | --- |
| `ok` | 读到了 | 正常显示 |
| `absent` | 枚举不到厂商接口 | 「已断开」 |
| `no_response` / `timeout` | 接口在但没回包（休眠/关机/超出范围） | 「已断开」 |
| `exception` | 打开接口失败等 | 「异常」 |
| `cached` | 首帧、尚未采集 | 沿用缓存值 |

### G87 键盘：另一套协议

G87 走的是**与 V7 完全不同的厂商协议**。取值全部来自官方 M HUB 网页驱动
（`https://www.mchose.com.cn/#/connectDevice`）的 WebHID 实现，不是推测：

| 项目 | 官方代码中的原值 |
| --- | --- |
| 设备表 | `{vendorId:16868 (0x41E4), productId:8193 (0x2001), usagePage:65284 (0xFF04)}` |
| 电量命令 | `["getBattery", { wiredCommand:"06 87 …", wirelessCommand:"13 4a 01 00 00 … 00" }]` |
| 报告 ID | `wirelessRId = 19` = **0x13** |
| 解析器 | `getBatteryWirelessParser = bae(false)` = `uint8("batteryLevel") + bit4("fullStatus") + bit4("chargeStatus")` |
| 校验和 | `tJe()` = 全部字节**求和**（V7 用的是 XOR） |
| 帧长 | 20 字节（V7 是 64 字节） |

实测收发：

```text
send  13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E
recv  13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C5
                     ^^ ^^
                     |  └─ raw[6] = status
                     └──── raw[5] = batteryLevel = 0x64 = 100%
```

#### status 字节的位序（实测校准）

官方 parser 的 `bit4` → `bitN` → `setNextParser("bit")` 是 **LSB-first**：第一个 `bit4` 取低 4 位、
第二个取高 4 位。用「无线未插线 / 插上 Type-C」两个状态对照确认：

| 状态 | raw[6] | 低 4 位 = fullStatus（满电） | 高 4 位 = chargeStatus（充电中） |
| --- | --- | --- | --- |
| 无线未插线 | `0x01` | 1 | 0 |
| 插上 Type-C | `0x10` | 0 | 1 |

与官方消费逻辑吻合：有线路径读 `fullStatus`，无线路径把它强制置 0，
但两条路径都读 `chargeStatus` —— 所以**充电状态取高 4 位**。

#### 插上 Type-C：插线只是充电，身份不变

**实测（插线状态下枚举）**：设备仍然是 `41E4/0x2001`（2.4G 接收器），
接口集合与拔线时完全一致 —— **没有**出现新的 PID。

```
插线后枚举：41E4 2001  FF02  0002  in=20 out=20   ← 20 字节无线通道仍在
             41E4 2001  FF04  0002  feat=8
```

也就是说 G87 插上 Type-C 后**仍以 2.4G 身份工作**，Type-C 只负责供电；
数据链路不变，电量照常能读。只有当键盘切换到「有线模式」（而非仅插线充电）时，
才会枚举成另一个身份：

| 模式 | 身份（VID / PID） | UsagePage | 通道 | 命令 |
| --- | --- | --- | --- | --- |
| 无线（2.4G 接收器） | `41E4` / `0x2001` (8193) | `0xFF04` (65284) | 输出报告 `0x13`，20 字节 | `13 4a 01 00 …` |
| 有线（USB 直连模式） | `41E4` / `0x2201` (8705) | `0xFF00` (65280) | **Feature 报告 `0x06`**，520 字节 | `06 87 00 00 01 00 02 00` + 512×`00` |
| 蓝牙（BLE HID） | `3554` / `0xFA07` | `0xFF02` (65282) | 输出报告 `0x13`，20 字节 | `13 4a 01 00 …`（与 2.4G 逐字节相同） |

官方有线路径的实现（`dJe.readCommand` 的 `isWired` 分支）是
`sendFeatureReport(reportId, …)` → `receiveFeatureReport(reportId)`，
并用 `AE(n, e=2)` 切掉回读数据的前 2 字节，再交给 `getBatteryParser`（= `bae(true)`）解析
（比无线多一个 6 字节 `space0` 前缀）。

官方命令原文（`purify.es` 里的设备配置）：

```js
wiredCommand: `06 87 00 00 01 00 02 00 ${"00 ".repeat(512).trim()}`
```

即 **8 字节头 + 512 字节 0 = 520 字节**。本机实测接口的 Feature 长度也正好是 520。

实测回包与解析：

```
send  06 87 00 00 01 00 02 00 + 512×00
recv  06 87 00 00 01 00 02 00 | 64 10 00 00 …
        ↑  ↑  ↑  ↑  ↑  ↑  │  │  └────
        │  │  │  │  │  │  │  └────── raw[9] = status（高4位=充电中）
        │  │  │  │  │  │  └───────── raw[8] = batteryLevel = 0x64 = 100%
        │  │  │  │  │  └──────────── space0 结束
        │  │  │  │  └───────────────
        │  │  │  └────────────────── 官方命令头 8 字节
        │  │  └─────────────────────
        │  └────────────────────────
        └─────────────────────────── 报告 ID 0x06
```

按官方 `AE(n, e=2)` 切掉前 2 字节后，负载 `d` 的布局是
`d[0..5]` = space0(6)、`d[6]` = batteryLevel、`d[7]` = 两个 bit4。

插件已实现这三条通道，**按接口的报告长度自动识别**，插拔线/切档不用改任何设置：
20 字节输入+输出 → 走无线 `0x13`（2.4G 与蓝牙共用同一帧，靠 VID 区分显示）；
520 字节 Feature → 走有线 `0x06`。

> 注意：`usagePage` 只是官方给设备打的**身份标签**，本机实测接收器的 `0x4A`
> 数据接口实际落在 `FF02` 上，`FF04` 那条是 8 字节 Feature。
> 所以通道判据用的是报告长度，不是 usagePage 的精确值。

**插线实测结论**：插上 Type-C 后 `0x4A` 依然正常应答，
`status` 从 `0x01` 变成 `0x10`（高 4 位充电标志置位），
卡片正确显示「充电中」+「2.4G 无线」，电量读取不受影响。

### 蓝牙（BLE HID）：同一套帧，另一个身份

键盘拨到蓝牙档后，USB 身份完全消失，Windows 以 BLE HID 重新枚举：
`VID 0x3554 / PID 0xFA07`。六个 collection 里 `usagePage 0xFF02` 那条是
20 字节输入 + 20 字节输出，**`feat=0`（没有 Feature 通道）**。

实测收发与 2.4G **逐字节一致**：

```
send  13 4A 01 00 00 … 5E
recv  13 4A 01 00 02 64 01 … C5      ← 电量 = 0x64 = 100%，未充电
```

所以蓝牙**不需要新协议**，`0x13/0x4A` 原样复用，差异只有两点：

1. 设备身份是 `3554/0xFA07`，不是 `41E4/0x2001`；
2. 只有 20 字节输出报告，没有 Feature 通道，因此蓝牙档走不了有线路径。

实现上的三处配套：

- **身份白名单**：`G87Protocol.IsKeyboardVendor` 同时接受 `0x41E4`（USB）与 `0x3554`（蓝牙），
  其余判据（`0xFFxx` + 20 字节 in/out）保持不变。
- **读取窗口**：BLE 链路从省电态唤醒要 1 秒以上。本机实测首次查询用 600 ms 窗口时
  3 次尝试全部无应答（耗时 1811 ms）；链路唤醒后同一帧 88 ms 即回。
  故蓝牙通道的窗口下限提到 1500 ms（`BluetoothWindowMs`）。
- **通道排序**：**有线 → 蓝牙 → 2.4G**。BLE 设备节点只在键盘真的处于蓝牙档且连接保持时存在，
  在场即可优先试；2.4G 接收器插着就常驻枚举，排最后，避免每次先白等满重试。

状态码新增 `ModeBluetooth = 3`，配合皮肤 `Substitute` 的 `"^3$":"蓝牙"`；
`2` 仍然是「未知」，掉线/异常不会被误显示成蓝牙。

### V9 Pro 2 耳机：第三套协议（0x55 / 0xAA）

耳机与 V7 共用 VID `0x3837`，但协议和鼠标/键盘都**不兼容**。设备身份：

| 项 | 2.4G 档（接收器） | 有线档（USB-C 直连） |
| --- | --- | --- |
| VID / PID | `0x3837 / 0x6011`（14391 / 24593） | `0x3837 / 0x6010`（14391 / 24592） |
| 产品串 | `MCHOSE V9 Pro 2` | `MCHOSE V9 Pro 2 HEADSET` |
| 厂商集合 | `usagePage 0xFF07`、`usage 0x0222`、`mi_03` | 同左 |
| 报告长度 | In = Out = **64**，**Feat = 0** | 同左 |
| 报告 ID | **0x55** | **0x55** |

两档**共用同一套帧**，只有 PID/产品串不同；连接方式按 PID 判定（`0x6010` → 有线）。

报文体（整条 64 字节报告里，`[0]` 是报告 ID）：

```
[0]      0x55            报告 ID
[1]      0xAA            起始符
[2]      0x01            协议版本
[3]      flags           0 = 不带校验，1 = 末位为 XOR 校验
[4]      length          payload 长度 + 1
[5..6]   cmdType         请求/应答 [0,1]、设置 [0,2]、通知 [0,4]、OTA [0,6]
[7]      cmd             命令字
[8..]    payload
[8+len-1]                XOR 校验（flags = 1 时有效）
```

校验算法：`flags ^ length ^ cmdType[0] ^ cmdType[1] ^ cmd ^ payload 全部字节`。

电量与休眠是命令 **`0x0B`（GET_BATTERY_SLEEP）**，负载两字节：
`payload[0]` 休眠态（0 = 休眠）、`payload[1]` **bit7 = 充电、低 7 位 = 电量百分比**。

本机实测（2026-10-27，`55 AA` 为起始符，前面 `55` 是报告 ID）：

```
# 2.4G 档（PID 0x6011）
send  55 AA 01 00 01 00 01 0B 00 …                    ← 50 ms
recv  55 AA 01 01 03 00 01 0B 01 56 5F …              ← 86%，未充电

# 有线档（PID 0x6010，同一帧）
send  55 AA 01 00 01 00 01 0B 00 …
recv  55 AA 01 01 03 00 01 0B 01 D6 DF …              ← 86%，充电中（bit7=1）
```

`0x56 & 0x7F = 86`，与官方客户端显示的 86% 一致；
XOR 复算 `01^03^00^01^0B^01^56 = 0x5F`、`…^01^D6 = 0xDF`，与两档帧尾校验位分别吻合。

实现要点：

- **主动问，不能等**：官方客户端是按需查询（进设备页/点刷新才发一次），没有周期心跳。
  实测在设备页停留 180 秒、四路接口同时只读监听，客户端一句都没说；
  只有触发一次页面重进才抓到上述帧。所以插件必须主动发 `0x0B`。
- **不能套用 0x4D 家族**：初始按鼠标的 `0x4D + 0x0900` 发过一次，写入成功但零回包——
  该设备是 BES 方案，命令空间完全不同。**发送成功 ≠ 协议正确**。
- **休眠语义对齐官方**：`payload[0] = 0` 时官方把 `batteryLevel` 置空，
  插件同样返回 `reason = "asleep"`，不编造数值。
- **两档都支持**：接收器 `0x6011`（2.4G）与 USB-C 直连 `0x6010`（有线）；
  枚举时**有线排前**——插着线时接收器那条即使仍在枚举也不代答，先试会白等满重试窗口。
- **只差蓝牙档**：该档只有经典音频 + 一个 SPP 串口（本机映射为 COM6），
  没有厂商 HID 集合，也没有系统电池属性，详见[已知限制](#八已知限制)。

协议常量的证据来自官方桌面客户端远程下发的设备页脚本（`app.asar` 内主进程是 V8 字节码，
不含协议；真正的实现随页面从 CDN 加载）：`crc32-BlQnRCeo.js` 是帧构造/校验/解析本体，
`eq-DcwrcT_4.js` 定义 `GET_BATTERY_SLEEP = 11`、`reportId = 85`，
`headsetPage-DNy5Z21r.js` 是调用点。三者都可用客户端日志里的 URL 复现。

## 三、状态与提醒规则

| 情形 | 判定 | 视觉 |
| --- | --- | --- |
| 已连接 | `state=0`、电量 > 20% | 蓝色数字 + 蓝条 + 绿点「已连接」 |
| 电量偏低 | 0 < 电量 ≤ 20% | 橙色 + 「电量偏低」 |
| 电量极低 | 电量 ≤ 8% | 红色 + 「电量极低」 |
| 已断开 | `state=1` | 数字转灰但**保留上次读数**，红点「已断开」，页脚变「最后 HH:MM:SS」 |
| 数据异常 | `state=2` | 同样保留旧值，「异常」+「读取异常，点击卡片重试」 |

**断开不清零**：最后一次成功读取的 `percent / mode / updated` 存进缓存文件
（鼠标 `v7-state.txt`、键盘 `g87-state.txt`、耳机 `v9-state.txt`，三卡必须分开），
掉线或重启 Rainmeter 后继续沿用；只有从未读成功过才显示 `--`。

提示音默认关闭。`Variables.inc` 里 `SoundOn=1` 可开启：低电首次触发响一次，之后每小时最多一次，
掉线不重复响，时间基准只在真正发声时更新。

## 四、界面

- 264 × 138 卡片，暗色（`24,26,32`）+ 细描边 + 底部投影
- 默认整体不透明度 80%（`OpacityValue=203`），由 `OnRefreshAction` 每次刷新重新套用
- 进度条是 Shape 画的胶囊条：平时 3px，鼠标移到条区展开到 8px，圆角半径 = 条高 ÷ 2
- 数字用 `StringAlign=Right` 固定右端，位宽变化（7 / 77 / 100）不会推动 `%`
- 左键点卡片立即采集；中键循环切换官方产品图配色
  （鼠标：白 / 黑 / 红 / 粉；键盘：白 / 黑 / 青 / 粉 —— G87 官方配色里没有红色）
- 不弹任何 ToolTip，操作说明写在 `[Metadata] Information` 与包内 `README.txt`

## 五、封装方式

`.rmskin` **不是**普通 zip 改名。Rainmeter 的安装器会做两处硬校验：

1. **文件尾部 16 字节 footer**（`struct PackageFooter { int64 size; BYTE flags; char key[7]; }`），
   要求 `key == "RMSKIN"`、`size == 文件长度 - 16`、`flags == 0`。缺 footer 直接拒绝安装。
2. **插件必须放在 `Plugins\64bit\` 或 `Plugins\32bit\` 子目录**，安装器按宿主位数二选一。
   放在 `Plugins\` 根目录下不会被识别。

`build_package.py` 两件事都做了，并在打包后自检：footer 三字段、`Load=` 指向的皮肤是否存在、
`Plugins/64bit/*.dll` 是否为 x64。

`.rmskin` 把插件装到 `%LOCALAPPDATA%\Programs\Rainmeter\Plugins\`。手工部署时注意 Rainmeter
有**两个**插件目录，安装目录优先加载：

| 目录 | 优先级 |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Rainmeter\Plugins\` | **实际生效** |
| `%APPDATA%\Rainmeter\Plugins\` | 兜底，优先级低 |

只更新后者等于没改（本项目实际踩过这个坑）。

## 六、构建

```bash
python build_plugin.py                # x64 -> dist/plugin/MCHOSE_Battery.dll
python build_plugin.py --arch x86     # x86 -> dist/plugin/MCHOSE_Battery-32.dll
python build_plugin.py --arch both    # 两个一起出
python build_plugin.py --install       # x64 顺带复制到 Rainmeter 的 Plugins 目录
python build_package.py --install      # 打包 + 安装皮肤与插件
cd tests/ProtocolTests && dotnet run -c Release   # 175 项协议/状态机断言
```

### 持续集成

`.github/workflows/build.yml` 在 push 到 main、打 tag、PR 与手动触发时执行：
跑 175 项测试 → 构建 x64 + x86 → 打包 rmskin → 校验 footer → 上传 artifact（保留 30 天）。

workflow **只构建、不发布**：权限只有 `contents: read`，也刻意不调用任何 Release 接口。
发版仍由人工执行，避免 CI 拿到写权限：

```bash
gh release create v1.1.0 dist/MCHOSE_Battery_1.1.rmskin --notes-file notes.md
```

两个产物的差异只在 PE 头和 CLR 标志，源码完全相同：

| 文件 | machine | CLR flags | 适用宿主 |
| --- | --- | --- | --- |
| `MCHOSE_Battery.dll` | `0x8664`（PE32+） | `0x0` | 64 位 Rainmeter |
| `MCHOSE_Battery-32.dll` | `0x014C`（PE32） | `0x2`（32BITREQUIRED） | 32 位 Rainmeter |

32 位版本本机无法实机验证（这台机器上只有 64 位 Rainmeter），已校验的部分：PE 头位数、
32BITREQUIRED 标志、6 个导出符号齐全、原生导入表只有 `mscoree.dll`（合法的 .NET 混合 DLL）。

### 打包时的文件名约束（容易踩）

Rainmeter 的 `MeasurePlugin.cpp` 是**严格按 `Plugin=` 的值**拼路径的：

```cpp
pluginName = plugin;                       // 例如 MCHOSE_Battery
if (!*PathFindExtension(plugin.c_str()))
    pluginName.append(L".dll");            // -> MCHOSE_Battery.dll
pluginFile = GetPluginPath() + pluginName; // 先找安装目录
// 失败再找 UserPluginPath（%APPDATA%\Rainmeter\Plugins）
```

**没有 `-32` / `-64` 后缀回退**。因此：

- 想让同一个皮肤在两种位数下都能用 → `Plugins/32bit/` 和 `Plugins/64bit/` 里的文件**必须同名**
  `MCHOSE_Battery.dll`，只放各自的位数版本。安装器按宿主位数二选一复制到 Plugins 目录。
- 如果坚持把 32 位版命名为 `MCHOSE_Battery-32.dll` 并装到 Plugins 目录，那 32 位皮肤的
  `Plugin=` 就得写成 `MCHOSE_Battery-32`，皮肤不再是双位数通用。

## 七、排错

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 皮肤显示「异常」 | 插件未加载或 DLL 位数不符 | 看 Rainmeter 日志有无 `Unable to load plugin`；确认用的是 x64 DLL |
| 数字显示乱码 | `GetString` 导出签名写错 | 必须是 `LPCWSTR GetString(void* data)`，不是 `void GetString(data, WCHAR**, BOOL)` |
| 中文乱码 | `.ini` 被存成 UTF-8 | 用 `build_package.py` 生成，不要手工另存 |
| 插件替换不生效 | DLL 被 Rainmeter 占用 | 退出 Rainmeter 再复制，或重启宿主 |
| 耳机卡片一直「已断开」 | 耳机连着蓝牙、没连上接收器 | 把耳机拨到 2.4G 并确认官方客户端能识别；接收器枚举正常不代表链路已建立 |
| 耳机写入成功但零回包 | 套用了鼠标 `0x4D` / 键盘 `0x13` 帧 | 耳机是 `0x55` 报告 + `0xAA` 起始符 + 命令 `0x0B` |
| 刷新节奏不对 | 改了 `RefreshMinutes` 没刷新皮肤 | 右键 → 刷新皮肤 |

## 八、已知限制

1. **安装包默认只装 64 位**：`build_package.py` 把 x64 版放进 `Plugins/64bit/`。
   32 位插件已可构建（`--arch x86`），要在包里同时支持两种宿主，把
   `dist/plugin/MCHOSE_Battery-32.dll` 重命名为 `MCHOSE_Battery.dll` 后放进 `Plugins/32bit/`（见第六节）。
2. **休眠态不可细分**：接收器不代答时，「休眠 / 关机 / 超出范围」表现一致，统一显示「已断开」。
3. **首次读数为空**：全新安装且鼠标当时不在线，会显示 `--`，这是事实而非缺陷。
4. **进度条每帧由 Shape 重绘**：为了圆角与悬停变粗，宽度按电量实时计算；`Update` 不要低于 100 ms。
5. **G87 三种档位自动识别**：
   - **2.4G 档**：接收器 `41E4/0x2001`，20 字节 `0x13` 通道；
   - **有线档**：插 Type-C 只是充电，仍以 `0x2001` 枚举；把键盘拨到「有线模式」
     才会出现 `0x2201`，插件自动改走 Feature `0x06` —— 已真机验证（100% / 充电中 / 有线）；
   - **蓝牙档**：USB 身份消失，Windows 以 `3554/0xFA07` 重新枚举，`usagePage 0xFF02`
     的 20 字节通道原样应答同一帧 —— 已真机验证（100% / 未充电 / 蓝牙）。

   切档后其余身份的接口仍会枚举但不再代答，插件按 **有线 → 蓝牙 → 2.4G** 顺序尝试。
   蓝牙链路从省电态唤醒要 1 秒以上，窗口下限设为 1500 ms，最坏约 4.5 s 才回落「已断开」。
6. **V7 鼠标的三种档位会自动识别**：把鼠标底部开关拨到对应档位，插件会改走不同链路：

   | 鼠标档位 | 应答来源 | 卡片显示 |
   | --- | --- | --- |
   | 2.4G | 鼠标本体 `0x1016`（在官方 `nT` 名单内） | 「2.4G 无线」 |
   | 有线 | 2.4G 接收器 `0x402A`（不在名单内） | 「有线」 |
   | 蓝牙 | `0x05AC/0x024F` 的 BLE GATT Battery Service | 「蓝牙」（无充放电指示） |

   有线档时本体接口会回一帧错误码（`dataLen=1`、payload 仅 `FE F7`），
   插件识别到就立刻换接口，不会误判成 0% 电量。

   注：**仅插 USB 线不改变档位** —— 那是充电，数据链路不变。
   蓝牙档没有厂商 HID 通道，只能读 GATT 百分比；充电状态未知，界面不显示。
7. **V9 Pro 2 耳机支持 2.4G 与有线两档，蓝牙档不可用**：
   - 2.4G：接收器插着就一直枚举 `3837/0x6011` 的 `0xFF07` 通道，
     但**只有耳机真的连上接收器才代答**；没连上（切到蓝牙、或需重新配对）时
     表现与鼠标一致：接口在、问不答 → 「已断开」。
   - 有线：插 USB-C 直连后身份变为 `3837/0x6010`（产品串带 `HEADSET`），
     同一帧原样应答，卡片显示「有线」。已真机验证（86% / 充电中 / 9 ms）。
   - **蓝牙档无法支持**：该档下耳机只有经典蓝牙音频（A2DP/HFP）与一个 SPP 串口
     （本机映射为 COM6，用 `00001101` 标准 SPP + VID `02B0`），
     既没有厂商 HID 集合，系统也没有暴露电池属性（`{104EA319-…},2` 为空，HFP 节点同样为空）；
     串口实测 10 秒无任何主动上报，属于「必须先有请求帧才能问」的通道，目前无公开证据可依。
   - 耳机没有 Feature 报告，`GetFeature` 路径不可用；别套用鼠标的 `0x4D` 或键盘的 `0x13`。

## 许可证

本项目以 **GNU General Public License v3.0** 发布，全文见 [LICENSE](LICENSE)。

边界说明（重复一次，因为很重要）：

- 本项目为第三方作品，**与迈从（MCHOSE）官方无关**，未获其授权或认可；
- 插件只读取设备通过公开接口上报的数据，**不修改固件、不写入设备配置、不改变任何设备行为**；
- MCHOSE、V7、G87 等名称、产品外观、商标与固件的一切权利归迈从所有；
- 若官方认为本仓库存在不妥，请开 Issue 联系，我会配合处理。
