# ACE68 Air / StrafeLab 研究记录

研究日期：2026-09-23。结论：**当前设备连续 Hall 可读，初期被动探测“不支持”的判断已推翻。**

## 枚举：3 interfaces / 7 collections

实际产品名 `Ace 68 Air-II`，VID `41E4`，PID `2120`，固件 `0x0117`。

| Interface / collection | Usage page / usage | Input IDs | Input / Output / Feature 字节数 |
|---|---|---|---|
| MI_00 | 0001 / 0006 keyboard | 0 | 9 / 2 / 0 |
| MI_01 | 0001 / 0000 proprietary | 0 | 65 / 65 / 0 |
| MI_02 col01 | 0001 / 0006 keyboard | 1 | 16 / 0 / 0 |
| MI_02 col02 | 0001 / 0002 mouse | 2 | 6 / 0 / 0 |
| MI_02 col03 | 000C / 0001 consumer | 3 | 3 / 0 / 0 |
| MI_02 col04 | 0001 / 0080 system | 4 | 3 / 0 / 0 |
| MI_02 col05 | 0001 / 000C | 5 | 2 / 0 / 0 |

长度来自 Windows HIDP caps，包含报告编号槽；MI_01 的 WebHID payload 是 64 字节，reportId=0。描述符由 hidapi 根据 Windows preparsed data 重建，不声称是 USB 总线逐字节捕获的原始描述符。完整 caps/重建描述符保存在研究工作目录；发布研究证据只保留必要的 WASD 数据。

所有 collection 均枚举并尝试读取，Windows 对受保护键盘/鼠标 collection 的读取错误单独记录。第一轮 180 秒被动捕获没有键程流；末段连接官方驱动时收到 MI_01 的命令回复，证明该读通道可用。它不是 vendor usage page，因此只搜索 `0xFFxx` 会漏掉。

## 官方驱动与抓包

从[官方 M HUB 下载页](https://www.mchose.store/pages/mchose-hub)找到当前[网页驱动](https://www.mchose.com.cn/)。对顶层页面和设备 iframe 安装 `HIDDevice.open/close/sendReport/sendFeatureReport/receiveFeatureReport` 以及 `inputreport` hook。用户手动授权连接设备，未进行固件升级。

分析的官方 bundle：

- `/cizhou/CZ_SHARED_DATA/main.51a87bccd7c58d7775eb.js`
- `/cizhou/_next/static/chunks/2233-6a9cbfd61bf2c9df.js`
- `/cizhou/_next/static/chunks/1833-6562fe1f092107cf.js`
- `/cizhou/_next/static/chunks/1288-d74b464bce618e3b.js`

`getFuncConfig(profile)` 使用命令 05，64 字节每 profile；`openDebug` 对配置字节 7 的 bit 3 置位，再由命令 06 写回。`closeDebug` 清除此位。固件收到开关后主动发送变化报告，不需要轮询每个按键。

连接与读取触发/RT 配置的 WebHID 流量已抓取。该设备没有 Feature report，官方实际交互也没有用 Feature report。监测开关开启/关闭已实际复现；触发行程/RT 写入函数、校准 A8/A9/B1 在源码中分析，**未为了抓包去重写用户的触发行程或执行硬件重新校准**。官方页面初始化自身可能发送 A9 结束校准及其正常的自定义区握手；StrafeLab 实现只允许 03/04/05/06。

请求 payload：`55 command 00 checksum size offsetLo offsetHi 00 data...`，补齐 64 字节。checksum 为从 size 开始的字节和 mod 256；回复是 AA。Windows HidSharp 写入时在最前加 `00`，总计 65 字节。当前 profile 从命令 04 回复的数据字节 0 取得。配置按 56+8 字节分块读写，并读回验证。

流数据格式（不含 Windows 的前置 report ID 字节）：

| Offset | 含义 |
|---|---|
| 0 | A0，payload 标志，不是 Report ID |
| 1..3 | 键定义：type=10，code1=00，code2=USB key code |
| 3 | A=04、D=07、W=1A、S=16 |
| 4..5 | 大端传感值；与键程相关，未将其未经证明地当作绝对 ADC 电压 |
| 6..7 | 大端固件键程单位；官方按 100 单位/mm 显示 |
| 10 | 官方校准状态显示字段；FF 对应完成状态 |

[HallEffectAnalogMapper](https://github.com/Richard121292/HallEffectAnalogMapper) 的 Jet75 路径读取同类 A0 包的 3/4/5 字节，是有效线索。该项目含虚拟手柄输出；这里只阅读协议相关代码，未运行其输入生成功能。ACE68 实现使用官方界面实际消费的 6/7 字节作为已换算键程，不套用 Jet75 的经验 ADC 映射。

## 受控实验与独立 C# 验证

用户通过 USB 有线连接，依次手动缓慢按压 A/D/W/S：浅按、半程、到底、释放并重复。开启 debug 后获得连续变化值，完全释放回到 0。

| 键 | WebHID 开启监测后的原始捕获：不同键程值 | 独立 C# 捕获报告数 | C# 不同值 |
|---|---:|---:|---:|
| A | 93 | 66 | 30 |
| D | 81 | 30 | 16 |
| W | 71 | 12 | 8 |
| S | 72 | 43 | 17 |

四键均覆盖 0–341。独立 C# 总计 151 个 WASD 报告，官方网页已离开设备页面，退出后恢复监测位且恢复 journal 已清除。WebHID 开关实验恢复后，64 字节功能配置与最初备份逐字节一致。

以上证明“有连续键程通道”，不证明机械位移有 0.01 mm 的绝对精度，也不证明 Hall 调试流具有键盘标称轮询率。底端报告 341 的显示值为 3.41 mm；不擅自减 1 或把它解释为标称总行程发生变化。

## 开源路线选择

- [Luk-Krn/mchose_ace68](https://github.com/Luk-Krn/mchose_ace68)、[Kingdoofy/mchose-ace68-he-3837](https://github.com/Kingdoofy/mchose-ace68-he-3837)、[ACE68 Air SignalRGB](https://github.com/qiuyitao1528/mchose-ace68-air-signalrgb)：提供设备/协议线索，但不同 PID 的灯光包不直接照搬至 2120。
- [Rupas1k/source2-demo](https://github.com/Rupas1k/source2-demo)：研究过 Rust 原生工具路线，本机缺 Windows linker；未交付未验证的提取器。
- [LaihoE/demoparser](https://github.com/LaihoE/demoparser)：选用有 Windows 预编译包的 demoparser2，通过独立本地进程解析；真实 test_demo.dem 已贯通 Python→JSONL→C#。
- [Valve GSI 文档](https://developer.valvesoftware.com/wiki/Counter-Strike:_Global_Offensive_Game_State_Integration)、[CS2GSI C# 项目](https://github.com/antonpup/CounterStrike2GSI)：GSI 用作比赛上下文，不冒充速度传感器。

灯光仍未验证，默认无灯光写入。完整移动引擎状态无法只靠普通 GSI 取得；模型限制、同步/校准门槛见 README。
