# VoltPerch

[English](README.md)

VoltPerch 是一款免费、开源的 Windows 外设电量监控工具，可在桌面窗口和系统托盘中查看设备连接状态与电量。

它将支持的键盘、鼠标、手柄和蓝牙设备汇总到同一列表。设备断开后会保留最近一次有效电量，并明确标注该读数已离线。

[下载 Windows x64 版本](https://github.com/WangHunter/VoltPerch/releases/latest) · [版本记录](CHANGELOG.md) · [MIT 开源许可证](LICENSE)

[功能](#功能) · [设备兼容性](#设备兼容性) · [安装](#安装与运行) · [源码构建](#从源码构建) · [参与贡献](CONTRIBUTING.md)

## 功能

- 显示连接状态、电量和充电信息，具体取决于设备和 Windows 提供的数据。
- 紧凑的托盘设备列表优先显示已连接设备，也可选择查看全部设备。
- 托盘图标为透明背景的电池图形，不显示数字；百分比可在设备列表中查看。支持自动选择设备，也可固定显示某一设备的状态。
- 设备断开后保留上次有效电量，并标注为非实时读数。
- 低电量通知带有重复提醒抑制机制。
- 支持浅色、深色和跟随系统主题；窗口内容会按比例缩放，并支持键盘操作。
- 默认使用英文界面，也可在设置中切换为简体中文。
- 可通过设备右键菜单或详情移除设备，并在设置中恢复。
- 数据保存在本机，无遥测、无需账号，也不会自动下载更新。

VoltPerch 只保存每台设备最新的有效电量，不记录历史曲线，也不估算剩余使用时间。

## 系统要求

- Windows 10 2004 版或更高版本，或 Windows 11。
- x64 电脑，并已安装相应设备驱动。
- 设备及连接方式须受支持。蓝牙电量读取取决于 Windows 和设备实际报告的信息。

Windows x64 便携版已包含 .NET 运行环境，使用时无需另外安装运行环境或 SDK。

## 设备兼容性

是否支持取决于具体设备、固件和连接方式。Windows 能枚举到的设备不代表都能读取电量。

| 设备或连接方式 | 已实现的支持 | 限制与验证范围 |
| --- | --- | --- |
| VGN V98pro V2，2.4 GHz 接收器 | 电量百分比、充电信息、设备响应状态，以及唤醒后触发刷新 | 已在目标型号上验证，包括拔下和重新插入接收器。若接收器与设备静默断开，程序可能无法区分设备休眠和关机。 |
| SteelSeries Rival 650，有线 USB | 电量百分比、充电信息和连接状态 | 已在目标型号的有线模式下验证。 |
| SteelSeries Rival 650，2.4 GHz 接收器 | 已实现针对该型号的电量查询 | 无线接收器模式仍需在实际设备上进行受控验证。有线模式的结果不能证明无线模式可用。 |
| 通过 Windows.Gaming.Input 读取的 Xbox 手柄 | 可逐个显示手柄电量，并支持多个手柄 | 系统提供的容量比例会标记为估算值，不是手柄直接报告的精确百分比。已验证接收器模式；手柄关机后再连接的受控测试尚未完成。 |
| 通过 XInput 读取的 Xbox 手柄 | 以 Empty / Low / Medium / Full 等粗略档位作为备用信息 | 不会把粗略档位伪装成精确百分比。无法确认对应设备时不会强行配对。仅检测到 USB 供电不能证明设备正在充电或已充满。 |
| 能提供 Windows 电池属性的经典蓝牙设备 | Windows 连接信息及该设备关联的电量值，包括 HFP 服务节点 | 数据是否可用、是否新鲜取决于 Windows 和设备。显示的是 Windows 最近一次报告，没有可靠的设备采样时间戳。此读取路径已在 JBL TUNE760NC 上验证。 |
| 其他经典蓝牙设备 | 可列出已知设备及其连接状态 | 设备已连接也可能没有提供可用电量。 |
| BLE/GATT 电池服务、左右耳机及充电盒独立电量、其他厂商私有 2.4 GHz 协议 | 尚未实现 | Windows 能列出设备并不意味着这些连接方式已支持读取电量。 |

自动检查覆盖数据解析、状态变化和显示规则，不能证明尚未实际测试的设备兼容。欢迎通过[设备支持请求表单](https://github.com/WangHunter/VoltPerch/issues/new?template=device_support.yml)提交设备型号和连接方式。

## 安装与运行

1. 从 [Releases 发布页](https://github.com/WangHunter/VoltPerch/releases)下载 Windows x64 便携版 ZIP 文件。
2. 将整个压缩包解压到有写入权限的文件夹，例如 `D:\Tools\VoltPerch`。
3. 运行该文件夹中的 `VoltPerch.exe`，并让程序文件保持在同一目录下。

程序启动后会显示主窗口。默认情况下关闭窗口会将程序收至托盘；如需完全退出，请右键单击托盘图标并选择 **Exit（退出）**。再次运行程序时会唤起已有窗口。

### 切换为简体中文

程序默认显示英文。打开 **Settings（设置）> General（常规）> Language（语言）**，选择 **简体中文** 即可切换界面语言。切换会立即生效；选择 **English** 可切回英文。切换语言不会影响设备数据。

浅色或深色外观可在 **Theme（主题）** 中选择，也可以跟随 Windows 系统设置。

### 查看设备电量

- 显示短横线代表当前没有有效电量读数，不代表电量为零。
- 近似标记代表系统估算值；粗略的电量档位会按档位显示。
- 设备断开后会保留上次有效电量，并清楚标注为非实时数据。
- 接收器插在电脑上并不代表外设已经连接。
- 某些休眠设备需要先唤醒，程序才能获取新读数。

单击设备可查看连接方式、电量来源和兼容信息。托盘列表默认只显示已连接设备；点击 **Show all（显示全部）** 可查看离线及待确认设备。将鼠标悬停在托盘图标上不会显示设备名称或电量。

### 托盘图标与设备选择

托盘图标只显示电池图形，不显示数字；点击图标可打开设备列表并查看具体百分比。程序默认根据可用的实时电量选择设备，也可以在设备详情中固定某一设备。

自动选择优先使用设备报告的精确百分比，其次是系统估算值，最后是粗略电量档位。离线读数不会用于选择。

### 电量提醒

低电量提醒的默认阈值为 20%，默认关闭声音。首次提醒之后，电量还需至少再下降 5 个百分点，并且距离上一次提醒至少 30 分钟，才会再次提醒。充电状态、缓存读数、系统估算值和粗略电量档位不会触发百分比提醒。

### 移除和恢复设备

右键单击设备并选择 **Remove device（移除设备）**，或在设备详情中使用相同选项。此操作会在 VoltPerch 中隐藏设备并清除它保存的电量及提醒状态，不会更改 Windows 蓝牙配对或设备的物理连接。

前往 **Settings（设置）> Devices & data（设备与数据）> Removed devices（已移除设备）** 可恢复设备。恢复后程序会重新读取电量；已清除的旧电量不会恢复。

### 升级

1. 通过托盘菜单选择 **Exit（退出）**，并确认程序已停止运行。
2. 备份 `VoltPerch.exe` 所在文件夹中的 `data` 目录。
3. 将新版解压到同一文件夹，替换程序文件并保留 `data` 目录。
4. 再次运行 `VoltPerch.exe`。

程序运行时不要替换文件。迁移到新目录时，请一并移动 `data` 以保留设置和电量读数。如有需要，可在新位置重新开启登录时启动。

## 数据与隐私

VoltPerch 将数据保存在可执行文件旁边的 `data` 文件夹中：

| 文件 | 用途 |
| --- | --- |
| `data/settings.json` | 语言、主题、刷新间隔和通知设置 |
| `data/devices.json` | 设备信息和最近一次有效电量 |
| `data/identities.json` | 持久化的设备身份映射 |
| `data/removed-devices.json` | 在应用中隐藏的设备 |
| `data/alerts.json` | 用于抑制重复提醒的通知状态 |

这些文件可能包含设备标识符和个人设备名称。提交问题或诊断资料前，请检查并移除敏感信息。应用不会上传设备列表，也不会发送遥测数据。电量查询不会更改灯光、宏、键位或固件。键盘唤醒检测只读取原始输入标头中的设备身份，不会记录按键或输入内容。

## 从源码构建

在 Windows 上安装 .NET 10 SDK，然后在仓库根目录运行：

```powershell
dotnet publish src/BatteryDesk/BatteryDesk.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

构建文件会输出到 `artifacts/publish`。源码目录仍使用原来的 `BatteryDesk` 名称，应用名称为 VoltPerch。

### 运行检查

```powershell
dotnet run --project tools/CoreChecks/CoreChecks.csproj -c Release
dotnet run --project tools/WakeChecks/WakeChecks.csproj -c Release
dotnet run --project tools/XInputChecks/XInputChecks.csproj -c Release
dotnet run --project tools/WgiChecks/WgiChecks.csproj -c Release
dotnet run --project tools/BluetoothChecks/BluetoothChecks.csproj -c Release
dotnet run --project tools/LocalizationChecks/LocalizationChecks.csproj -c Release
```

这些检查覆盖状态与缓存策略、唤醒和连接后的刷新、手柄数据解析与映射、蓝牙属性归属及语言资源。设备探测和实体设备断开重连测试需要相应硬件；仅运行自动检查不能证明新电脑上的设备兼容性。

## 参与贡献

欢迎提交错误报告、可复现的设备兼容性结果和针对性的代码改进。在新增设备协议或修改电量语义前，请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。发现软件缺陷时，请使用[错误报告表单](https://github.com/WangHunter/VoltPerch/issues/new?template=bug_report.yml)。

## 许可证与致谢

VoltPerch 使用 [MIT 许可证](LICENSE)。第三方协议资料及运行环境声明见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) 和 `licenses` 目录。

VoltPerch 是独立开源项目，与兼容性说明中提及的硬件制造商及其他项目无隶属关系。
