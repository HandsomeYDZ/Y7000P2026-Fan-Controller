# 项目交接文档 —— Y7000P2026-Fan-Controller

> 本文档记录截至 2026-09-05 的完整进度、实测数据、代码改动、事故记录与下一步建议，供后续 AI/开发者无缝接手。
> **接手前请先读第 0 节安全须知。**

---

## 0. 安全须知（必读，有事故前科）

1. **任何 WMI/EC 写入测试前，必须关闭以下全部进程**（本机实测教训：并发写导致切模式瞬间黑屏关机）：
   - `Lenovo Fan Controller.exe`（本项目 GUI）
   - `Lenovo Legion Toolkit.exe`（用户常驻）
   - 联想官方服务（LenovoSmartService / LenovoPcManagerService 等，一般不用动，但测试时避免与它们抢模式切换）
   - 检查命令：`tasklist | findstr /i "lenovo"`
2. 本机 UAC 提权经验：`Start-Process dotnet.exe -Verb RunAs` 传参不可靠（工作目录变 System32、参数拼接易错）；**用 bat 自提权模式**（`net session` 检测 + `Start-Process -FilePath '%~f0' -Verb RunAs -Wait`）已验证可行。诊断脚本必须全绝对路径。
3. 风扇表写入测试要用**温和档位**（step 3-5），先验证再加大；写 0 会让风扇停转。
4. 修改 `.git` 前确认远端：origin = `https://github.com/HandsomeYDZ/Y7000P2026-Fan-Controller.git`（用户 fork）。**所有代码改动尚未提交。**

---

## 1. 项目背景与目标

- 项目：Lenovo Legion 风扇控制软件（WinUI3 GUI 版），fork 自 `Kangarroar/Legion-Fan-Controller`（源头 `Z4ndyz/LegionGen5-6FanControl_WinRing_Smokeless_I-OPortsMethod`）。
- 架构：GUI（`Lenovo Fan Controller/`，net8.0-windows10.0.19041.0，WindowsAppSDK 1.8，自包含发布）通过 **PawnIO 驱动**（`\\?\GLOBALROOT\Device\PawnIO` + `C:\Program Files\PawnIO\LpcIO.bin` 模块）经 ITE EC 端口 0x4E/0x4F 直接读写 EC RAM；电源模式走 WMI `LENOVO_GAMEZONE_DATA`（Get/SetSmartFanMode），Fn+Q 事件监听 `LENOVO_GAMEZONE_THERMAL_MODE_EVENT`。
- `FanControl/` 子目录是原版 WinRing0 控制台程序（本 GUI 不依赖，仅退出时清理同名进程）。
- **用户目标**：不 PR 上游；修复后本地验证没问题，推送到自己的 fork。
- **用户环境**：Y7000P 2026 笔记本（主机名 LAPTOP-4P5RG84B），Windows 11，VBS 开启（Credential Guard 运行），PawnIO 驱动正常 RUNNING，常驻 Lenovo Legion Toolkit（用户用它对照正确读数）。

## 2. 环境与工具链（本机实测）

- **机器没有 .NET SDK**；SDK 8.0.424 已装到 `C:\Users\Legion-Desktop\dotnet\`，用全路径调用：`C:\Users\Legion-Desktop\dotnet\dotnet.exe`（bash 中 `"$HOME/dotnet/dotnet.exe"`）。
- 机器只有 7.0/9.0 运行时；**诊断工具（framework-dependent）必须用上面的 dotnet.exe 运行**；主 GUI 是 self-contained，直接跑 exe。
- 编译主项目：
  ```bash
  "$HOME/dotnet/dotnet.exe" build 'Lenovo Fan Controller.sln' -c Debug -p:Platform=x64
  ```
  （**必须 `-p:Platform=x64`**，否则报 NETSDK1032；编译通过，0 错误，警告均为项目原有）
- GUI 输出：`Lenovo Fan Controller/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Lenovo Fan Controller.exe`（需管理员）。
- EC 芯片 ID：EC 地址 0x2000/0x2001 = **0x5508**（ITE 5508，新平台），版本寄存器 0x2002=0x02。**不是** Gen5 的 0x5570/5571，也**不是** Gen6 的 0x8226/8227。

## 3. 原始 BUG 与根因（已定性）

GUI 显示：风扇 18045 / 16743 RPM、CPU 128°C、GPU 0°C。EC 实测：
- `0xC538` = 0x80 → 128°C；`0xC539` = 0x00 → 0°C
- `0xC5E0/E1` = 0x467D → 18045；`0xC5E2/E3` = 0x4167 → 16743

根因：**项目沿用 Gen5/6 的 EC 寄存器映射，而 ITE 5508 新 EC 布局不同**，这些地址现在是别的数据。与 VBS 无关（PawnIO 通路正常）。

## 4. 正确读取接口（已实测验证，LLT SensorsControllerV5 同款）

WMI `root\WMI` 的 `LENOVO_OTHER_METHOD.GetFeatureValue(IDs)`（需管理员；System.Management 调用）：

| 数据 | IDs | 实测值 |
|---|---|---|
| CPU 风扇转速 | 0x04030001 | 1900 RPM |
| GPU 风扇转速 | 0x04030002 | 1900 RPM |
| PCH 风扇转速 | 0x04030004 | 0 |
| CPU 温度 | 0x05040000 | 53-54°C |
| GPU 温度 | 0x05050000 | 45-46°C（dGPU 睡眠时为 0） |
| PCH 温度 | 0x05010000 | 42-43°C |

权威参考（已浅克隆到 `/tmp/LLT`）：
- `LenovoLegionToolkit-Team/LenovoLegionToolkit`（用户指路：原作者已弃更，此团队接手并支持 Y7000P 2026）
- `Lib/Controllers/Sensors/SensorsControllerV5.cs`（读取路径）、`Lib/Enums.cs` 的 `CapabilityID`（0x04030001/0x04030002/0x05040000/0x05050000 等）、`Lib/System/Management/WMI.LenovoOtherMethod.cs`（GetFeatureValue 封装）
- 温度 LLT 另有 LHM（其 fork 的 LibreHardwareMonitor 用 PawnIO 做 ring0，克隆在 `/tmp/LHM-LLT`）兜底 GPU 温度；本项目未引入 LHM，WMI 已够用。

## 5. 已完成代码改动（读取修复，已编译+用户验证通过）

- **新增** `Lenovo Fan Controller/LegionSensors.cs`：统一读取层。WMI `GetFeatureValue` 优先（缓存 ManagementObject、线程锁、失败返回 -1）；WMI 无效时**仅当 EC 芯片是已知 Gen5/6 型号**才回退旧 EC 寄存器；否则返回 -1（UI 显示 N/A）。
- **改** `Lenovo Fan Controller/Hardware/LegionEcReader.cs`：
  - 新增 `GetChipId()` / `IsLegacyGen56Chip()`（0x5570/5571/8226/8227）；
  - `DetectLegionGen()` 未知芯片返回 **0**（新平台），不再做寄存器猜测；
  - `SanitizeRpm` 上限 20000→**12000**（防垃圾值）。
- **改** `Lenovo Fan Controller/SettingsManager.cs`：`LegionGeneration` 缓存值允许 0。
- **改** `Lenovo Fan Controller/MainWindow.xaml.cs`：
  - 初始化：EC 芯片非 Gen5/6 时强制 `legionGeneration = 0` 并覆盖注册表（防旧版缓存 5/6）；
  - 监控 Timer：改异步（`Task.Run` 读 `LegionSensors`，防 UI 卡顿 + 防重入），-1 显示 "N/A"；
  - `ApplyFanCurveToEC()`：新平台跳过 `WriteFanAcclDeccl`（0xC560/0xC570 在新 EC 是动态数据区，写入有害）；
  - `LoadConfig()`：配置文件缺失时改用**内置默认曲线**（不再 `ReadCurrentECConfig()` 读 EC 垃圾），并 `needApply=true` 主动写回修复被污染的表；新增 `IsSaneFanConfig()` 校验（温度/RPM 单调、RPM 中间不得夹 0），非法则回落默认。
- **验证结果**：GUI 监控数值正确（用户确认）；曲线编辑器恢复正常（用户确认）。EC dump 确认温度斜坡表（0xC580/0xC591/0xC5A0/0xC5B1/0xC5C0/0xC5D1）写入可见且 `Fan_Get_Table` WMI 能回读一致。

## 6. 写侧问题（未解决，核心遗留）

**现象**：用户在 GUI 保存高 RPM 曲线后两个风扇都不加速；EC dump 显示 RPM 表区域 0xC551 部分字节被 EC 立即覆盖（0xC541 可写、0xC551 混合动态数据）→ **旧 EC 直写 RPM 表方式在新固件上不生效**（温度表区域可写但不代表固件采用）。

**新固件真实模型**（来自 LLT 源码 + pjt222/fancontrol 项目实测文档 + 本机 WMI 探测）：
1. Quiet(1)/Balanced(2)/Performance(3) 的风扇曲线是**固件预设、不可改**；仅 **Custom(255)** 模式接受 `LENOVO_FAN_METHOD.Fan_Set_Table` 写入。（用户也确认：LLT 只在自定义模式能调转速）
2. `LENOVO_FAN_TABLE_DATA`（本机实测，15 个实例 = 5 种 Mode × 3 组风扇/传感器）：
   - `FanTable_Data` = 10 档 RPM：`[1700, 1900, 2100, 2400, 2400, 3200, 3400, 3800, 4300, 5100]`
   - `SensorTable_Data`（温度阈值，**只读/固件固定**）：CPU(Fan1,Sensor1) `[37,41,44,47,127,127,127,127,127,127]`；GPU(Fan2,Sensor5) `[53,53,53,53,53,53,56,59,65,77]`；PCH(Fan1,Sensor4) `[65,65,65,65,65,68,71,75,80,90]`
   - `Mode` 字段：1=Quiet、2=Balanced、3=Performance、255=Custom、224=超能(Extreme?)；`DesignMaxFanSpeedNumber`：7/8/9/8/10
   - `CurrentFanMinSpeed`=1700、`CurrentFanMaxSpeed`=5100
3. `Fan_Set_Table` 输入：**64 字节** = `[FSTM=1][FSID=0][FSTL u32LE=0][FSS0..FSS9 各 u16 LE]`，FSS 是 **0-10 的档位索引**（fancontrol 实测：0=停转，1=档0≈1700RPM，10=档9≈5100RPM，2-9 线性映射档1-8）。
4. 写入流程（fancontrol 在 82RG 上验证过）：读当前 SmartFanMode → `SetSmartFanMode(255)` → `Fan_Set_Table` → 恢复原模式；曲线保留在 Custom 槽位，重新进入 Custom 时激活。
5. `Fan_Get_Table(fanId, sensorId)`（本机唯一另一方法）实测返回当前 EC 温度表（随本软件 EC 写入变化，如 `30 45 55 60 65 127...`）+ SensorTable 恒 0 —— 说明它映射 EC 0xC580 区域，不映射 RPM 表。
6. **本机 `Fan_Set_Table` 有效性未验证**（见第 7 节事故）。

参考代码位置：
- LLT：`Lib/Structs.cs` 的 `FanTable`（GetBytes 64 字节格式、GetMinimumFanTableAsync=`[1,1,1,1,1,1,1,1,3,5]`）、`Lib/Controllers/GodMode/GodModeControllerV1-V4.cs`（写表流程）、`Lib/System/Management/WMI.LenovoFanMethod.cs`（FanSetTableAsync）
- fancontrol（pjt222/fancontrol）：`scripts/test-fan-set-table.md`（实测结论与注意事项，含"step 0 停转"等）、`scripts/probe-set-table.ps1`、`src/platform/lenovo.rs`（encode_fan_table_bytes、写前切 Custom 的事务脚本）

## 7. 事故记录（2026-09-05，重要）

**事件**：用户运行我准备的受控测试 `diag/run-test-fan-set-table.bat`（流程：记录模式→SetSmartFanMode(255)→Fan_Set_Table(step=8 测试表)→采样→写安全表→恢复模式）时，**切模式瞬间电脑黑屏关机**（用户描述"切到静音模式的一瞬间黑掉关机"，具体是脚本的切换还是用户手动 Fn+Q 不确定）。

**事后状态**：机器已重启可用；`Lenovo Fan Controller.exe` 已不运行；`Lenovo Legion Toolkit.exe` 当时/现在在运行；联想官方服务也在运行。

**最可能原因（推断）**：测试时 **Lenovo Fan Controller GUI（我启动的修复版）+ Lenovo Legion Toolkit + 联想服务 + 测试脚本四方并发**响应 SmartFanMode 切换：脚本切 255 触发 GUI 的 PowerModeListener→GUI 同步写 EC（ApplyFanCurveToEC），同时脚本写 WMI 表、LLT 也在响应模式事件 → EC/固件状态机竞态 → 保护性关机。次要可能：Fan_Set_Table 参数/时序在本机型有未知副作用；或旧版 GUI 历史错误写入（0xC560/0xC570/0xC551）污染 EC 状态被模式切换放大。

**Custom 槽位表当前状态未知**：可能残留测试表（step=8 → 进 Custom 模式风扇会到约 3800 RPM）或安全表（step=1 → 最低档）。**处理建议**：正常模式下无影响；若要复位，在**关闭所有联想相关软件**后单独跑一个只写"安全档位表"的最小脚本，或先观察 LLT 内自定义模式风扇表现再决定。

## 8. 下一步建议（按顺序）

1. **确认机器健康**：重启后各模式风扇正常；用 `diag/run-verify-llt-api.bat`（只读）复查读数。
2. **单独验证 Fan_Set_Table**（GUI 和 LLT 全部关闭时）：把 `diag/test-fan-set-table.ps1` 的测试档位从 8 降到 **3**，加"taskkill Lenovo Fan Controller / Lenovo Legion Toolkit"前置，重跑一次确认本机有效性；失败则回退方向（考虑温度表 EC 直写是否其实有效，或仅做读修复收尾）。
3. **决定 UI 语义**（需用户拍板）：
   - 方案 A（改动小）：UI 保留自由曲线，写 Custom 槽时把 (temp,rpm) 插值+量化到 10 档索引；
   - 方案 B（贴合固件）：新平台 UI 改为编辑 10 档速度表 + 展示固件固定温度阈值（类似 LLT GodMode 风扇表）。
4. **实现写侧**：`ApplyFanCurveToEC` 新平台分支 → 仅在 Custom 模式（或切 Custom）时调用 `Fan_Set_Table`；其他模式仅保存配置并提示"曲线在自定义模式生效"；旧平台保留 EC 直写。同时决定是否完全禁用新平台的 EC 直写（0xC541/0xC551/温度表）。
5. **收尾**：重新编译、GUI 全程验证（读数、曲线编辑、Custom 模式风扇跟随）、`git add/commit` 并推送 fork（`git remote -v` 已指向用户 fork；改动均未提交）。

## 9. 诊断工具清单（diag/ 目录）

| 文件 | 用途 | 运行方式 |
|---|---|---|
| `ECProbe/`（C#，net8.0-windows） | PawnIO EC 扫描：芯片ID、全 RAM dump（0xC000-0xCFFF）、变化字节采样、候选分析；`--dump-only` 快速 dump | `~/dotnet/dotnet.exe diag/ECProbe/bin/Release/net8.0-windows/ECProbe.dll --dump-only`（自动 UAC 提权，但依赖 dotnet 参数——**优先用 bat**） |
| `ECProbe2/`（C#） | 集成 LibreHardwareMonitorLib 的时间线采样（已编译未运行） | 同上路径 ECProbe2.dll |
| `run-diag-admin.bat` | ECProbe 的可靠提权运行器（当前=--dump-only） | 用户双击/PS 运行，UAC 点是 |
| `verify-llt-api.ps1` + `run-verify-llt-api.bat` | 只读验证 LLT WMI 传感器接口 | bat 自提权 |
| `probe-fan-table.ps1` + `run-probe-fan-table.bat` | 只读 dump LENOVO_FAN_TABLE_DATA 全部实例 | bat 自提权 |
| `test-fan-set-table.ps1` + `run-test-fan-set-table.bat` | **写测试（有事故前科，见第 7 节，慎用！）** | 关闭所有联想软件后 bat 自提权 |
| `probe-wmi.ps1` | 早期 WMI 方法枚举（非管理员受限） | pwsh |

**实测数据文件**：
- `diag/ECProbe/bin/Release/net8.0-windows/out/ec-dump-full.txt`（EC RAM 全 dump）
- `.../out/analysis.txt`（变化字节/RPM/温度候选分析）
- `.../out/ecprobe.log`
- `diag/verify-llt-api.out.txt`、`diag/probe-fan-table.out.txt`（WMI 实测快照）
- `/tmp/LLT`（LLT 源码，仅 Lib 部分）、`/tmp/LHM-LLT`（LHM fork）

## 10. 其他零散事实

- GUI 配置文件目录：`<exe旁>/Config/`（当前只有 Suggested，无用户配置）→ 触发 LoadConfig 默认曲线路径；备份在 `Documents/LegionFanController/Backups/*_backup_v2.txt`（已被旧版写入垃圾，仅存档）。
- 注册表：`HKCU\Software\LegionFanController`（LegionGeneration 已可存 0）。
- 本机 git 分支 `main`，与 origin 同步，工作区有未提交改动（上述代码 + diag/ 目录新增）。
- `DetectLegionGenByRegisters()` 已改为恒返回 0（保留占位）。
