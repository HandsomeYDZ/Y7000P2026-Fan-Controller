# 83F3 / Q6CN79WW 普通模式风扇控制核查（2026-09-05）

## 结论和验证范围

用户目标是保持静音、均衡、性能模式本身，分别调节风扇，而不是改用 Custom。

已在本机 **均衡模式（SmartFanMode=2）** 验证独立目标转速接口有效：两个风扇从 1900 RPM 升到 2600 RPM，整个测试模式保持 2。8 秒后请求恢复自动，转速逐步回落。因此旧 HANDOFF 中“普通模式完全不能调速”的结论过强。

代码已对模式 1/2/3 实现相同 WMI 控制路径。**静音和性能模式尚未进行硬件实测**；长期曲线控制和完整 WinUI 界面尚未人工验证。不能将均衡模式的短测写成“三个模式均验证通过”。

## 本机只读证据

- 型号：83F3；BIOS：Q6CN79WW。
- `LENOVO_CAPABILITY_DATA_00`：`0x04030001`、`0x04030002` 的 Capability 都是 7（valid/get/set 三位全部置位）；PCH 风扇 `0x04030004` 为 0，不写第三风扇。
- `LENOVO_FAN_TEST_DATA`：两个风扇 ID 为 1/2，最低都是 1700，最高都是 5300 RPM。
- `LENOVO_FAN_TABLE_DATA` 的 5100 RPM 是十档表最高档，与独立目标转速接口的上限不是同一概念。
- 原始快照：[modern-fan-state.json](modern-fan-state.json)。C# 应用适配层也完成管理员只读探测，日志为 `verify-target-adapter.log`。

## 本机 ACPI 离线分析

从 Windows 注册表 `HKLM\HARDWARE\ACPI` 复制已有固件表，没有运行 AML、扫描 EC RAM 或加载读写驱动。使用 ACPICA 官方 iASL 20260408 离线反汇编。

- DSDT AML SHA256：`2B46DC4E5550E1276930EC6AD9A300854244CA4558DFEF205CD74723E38793AF`
- iASL 官方压缩包 SHA256：`121F5E4F30B1DF63D09052294E4A605D4DEE2DFB9599FA24AF4AC6015DF02B70`，与 Intel 下载页一致。
- 本地文件：`diag/acpi/DSDT.aml`、`DSDT.dsl`，已在 Git 中忽略，不随代码提交整个固件和工具包。
- 单表反汇编有外部方法解析警告；全表解析存在重复 ACPI 对象。以下相关方法在 DSDT 内直接定义，仍以实际能力声明、Linux 驱动和实测交叉确认，不能把反汇编结果当成完整 EC 固件源码。

关键路径（当前 DSDT.dsl 行号）：

1. **61413–61425**：`WMAE` 的 SetFeatureValue 分支处理 CPU/GPU 的 IDs，计算 `RPM / 100`，调用 `LECR(0xD1, fanId, value, 2)`。本分支没有检查 Custom 模式。
2. **38504** 起：`LECR` 是串行的 EC 命令传输方法；WMI 由固件执行传输。本项目不自行操作这些端口。
3. **59965–60040**：`WMAB` 的 Fan_Get_Table 和 Fan_Set_Table 都使用 `F9F0…F9F9`。写表还会发送 `LECR(0xD0, 1, 1, 2)`。传入的 FSTM/FSID/FSTL 在此方法里被解析，但没有用于选择三个模式的独立槽位。
4. **36874、36912**：F9FT 是固定地址的 SystemMemory 区域，F9F0 从偏移 0x80 开始。因此“字节会变化”不能推出“写地址动态漂移”；旧 ITE 端口映射是否与该区域有对应关系，不能仅凭相似读数下定论。

不能把 Fan_Get_Table 读出的异常温度样数值认定为正确温度表：该方法返回的是 F9F0…9，而旧程序曾把温度数据写入相关旧地址；存在污染可能。黑屏原因仍未定，不能将“并发写导致关机”作为已证实因果关系。

## 独立 RPM 接口与恢复

接口：`LENOVO_OTHER_METHOD.SetFeatureValue(IDs, Value)`。

| IDs | 用途 | Value |
|---|---|---|
| 0x04030001 | CPU 风扇目标 | RPM，100 的倍数 |
| 0x04030002 | GPU 风扇目标 | RPM，100 的倍数 |
| 同上 | 请求恢复固件自动控制 | 0 |

**这里的 0 是 auto，不能和 Fan_Set_Table 的档位 0 混用。** 原事故脚本的“全最低档安全恢复”没有充分依据，已在入口处停用。

主要交叉参考：

- [Linux wmi-other.c](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/lenovo/wmi-other.c)：独立 fan target、100 RPM 量化、0=auto、能力与范围检查。
- [Linux wmi-capdata.h](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/lenovo/wmi-capdata.h)：Capability 的 valid/get/set 位定义。
- [LLT 风扇表封装](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/System/Management/WMI.LenovoFanMethod.cs)：用于区分旧十档表接口与本次目标 RPM 接口。

本机 SetFeatureValue 返回对象不含 ReturnValue 字段。第一轮脚本因此在 CPU 写入后提前中止；finally 已向两风扇发出 auto，但也因读取不存在的字段报错。随后独立恢复脚本再次请求 auto，连续五次测得两个风扇均为 1900 RPM，再修正脚本重测。**WMI 调用无异常不等于风扇已到目标**，AML 自身还会忽略底层 LECR 返回值；需看实际 RPM。

## 经用户批准的均衡模式短测

用户确认已退出 LLT 和 LenovoTray，并同意 2600 RPM / 8 秒测试。系统服务未停止；没有切换模式、写风扇表或直接写 EC 地址。

成功记录目录：`diag/normal-rpm-test-20260905-181057/`（本地原始 JSONL，不自动提交）。

| 阶段 | CPU / GPU RPM | 模式 |
|---|---|---|
| 基线 | 1900 / 1900 | 2 |
| 第 1 秒 | 2100 / 2100 | 2 |
| 第 2 秒 | 2400 / 2400 | 2 |
| 第 3–8 秒 | 2600 / 2600 | 2 |
| 请求自动后第 8 秒 | 2200 / 2300（继续回落） | 2 |

测试后 C# 只读复查：2100 / 2100 RPM，模式仍为 2。该次测试验证了升速与解除目标；没有进行低转速、高负载、睡眠或其他两档硬件实验。

## 软件行为和边界

- 83F3 完全不初始化 PawnIO；旧 EC 写入在事务入口和最底层字节写入处都拦截。未知芯片不得因 UI 选择或配置中的 generation 获得写权限。
- 新配置位于 exe 旁 `Config/fan_config_wmi_quiet.txt`、`fan_config_wmi_balanced.txt`、`fan_config_wmi_performance.txt`，与旧寄存器曲线文件隔离。
- 启动保持自动控制。先保存需要的模式配置，再点击 **Enable curves**；按钮变为 **Restore auto** 后可停止。
- 一次启用使用已保存配置的快照。Fn+Q 改变模式后，辅助进程选择对应已保存曲线；没有配置的模式和 Custom 保持自动。界面内切模式会先停止当前控制，完成后需再次 Enable curves；保存或重置也会停止会话。
- 两风扇使用 CPU/GPU/PCH 中最高曲线需求；PCH 使用 CPU 曲线，GPU 温度为 0 视为睡眠。这个版本不提供独立停转。
- 软件固定执行范围、单调性、100 RPM 量化校验，不允许关闭。CPU ≥90°C / GPU ≥85°C / PCH ≥80°C 或关键数据失效时，请求自动并结束会话。
- 控制辅助进程运行于 GUI 之外；超过 6 秒无 GUI 心跳、GUI 退出、异常、冲突程序启动，都会走自动恢复。GUI 在辅助进程异常退出后也会尝试恢复。单侧恢复失败仍会尝试另一风扇。
- 这些是软件恢复措施，不构成 EC 命令永不失败的保证；没有硬件级看门狗，不能覆盖整个系统卡死或两个进程同时被强制终止。仍需后续实机验证长期控制和其他模式。

## 已完成检查

- 主项目 Debug / x64 编译通过，0 错误；仍有原项目 nullable 等警告。
- 142 项检查通过，包括边界、单调性、传感器故障、旧 EC 写入阻断、时钟/心跳过期、双风扇恢复失败处理。
- 测试还启动真实辅助进程并注入假固件，验证失去心跳、主动停止、传感器故障后确实发出恢复动作，以及静音/性能曲线选择、进入 Custom 后解除目标；这些测试不访问真实 EC/WMI 硬件。
- C# 目标适配器的只读本机探测通过。

构建与离线测试：

```powershell
& 'C:\Users\Legion-Desktop\dotnet\dotnet.exe' build 'Lenovo Fan Controller.sln' -c Debug -p:Platform=x64
& 'C:\Users\Legion-Desktop\dotnet\dotnet.exe' run --project 'tests\FanSafety.Tests\FanSafety.Tests.csproj' -c Release
```

没有启动新 GUI 进行人工交互验证，没有提交或推送 Git。后续测试必须保留“只读/假固件/真实硬件”的区分，不能把编译通过记作硬件验证通过。
