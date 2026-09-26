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


## 2026-09-26：统计 UI、同类项目与后台运行（1.5）

本轮读取开源源码，借鉴信息组织与分析思路，没有复制其应用代码，也没有执行其中的安装器或输入钩子。

| 项目 / 固定源码 | 核对结果 | StrafeLab 的取舍 |
| --- | --- | --- |
| [CS2StopReflex](https://github.com/PuddingTower/CS2StopReflex/tree/9bd669dc0572a6384b0e5323adfc0cf9888f520d) | A/D 按下与松开时差、近 50 次折线与箱线图、基于均值的参数建议 | 参考顺序分布与波动展示；保留同武器 / 姿态 / 初速分组和中位数，未把按键交接当成角色完全停住 |
| [CounterStrafeTestTools / StrafeLogic.cs](https://github.com/LolitaIceMia/CounterStrafeTestTools/blob/59e756538c4dc5a883ebb8dbe772ed5416455a37/CounterStrafeTest/Core/StrafeLogic.cs) | C# 按键边沿状态机，200 ms 时差筛选；StopTick 是按键交接结束时刻 | 不以其固定阈值替代 Demo 速度、输入连续性与置信度检查；符号口径不同，未混用 |
| [MagnetDebugLogic.cs](https://github.com/LolitaIceMia/CounterStrafeTestTools/blob/59e756538c4dc5a883ebb8dbe772ed5416455a37/CounterStrafeTest/Core/MagnetDebugLogic.cs) | 根据均值 ±5 ms、标准差等规则建议改变 RT / 死区 | 这些是启发式规则，未用作 ACE68 参数结论；继续记录真实方案、同类对局对照，不从时差推导最佳毫米数 |
| [cs-match-helper / player-api.ts](https://github.com/qianjiachun/cs-match-helper/blob/44ca1bd2a81baaaa5bba444b023b0f02a4065df6/src/platforms/perfect/player-api.ts) | rapidStopSuccessRate 从平台返回字段映射；当前 HUD 页面链接至独立站点 | 不能将平台急停率当成本地算法，也不引入平台账号依赖 |
| [CS Demo Manager / video-queue.ts](https://github.com/akiver/cs-demo-manager/blob/10fc2a92b2824d0705c338295e1080922d96ebfd/src/server/video-queue.ts) 与 [目录扫描](https://github.com/akiver/cs-demo-manager/blob/10fc2a92b2824d0705c338295e1080922d96ebfd/src/node/demo/find-demos-in-folders.ts) | 显式队列状态、取消机制、目录与压缩包去重处理 | 参考任务与采集分离；StrafeLab 使用自己的持久队列、文件租约、下载稳定检查。未采用其游戏服务器插件、录像渲染等功能 |
| [Awpy](https://github.com/pnxenopoulos/awpy) / [解析文档](https://awpy.readthedocs.io/en/latest/_modules/awpy/demo.html) | 展示按 tick / 回合组织事件并按有效比赛阶段筛选的方法；不同版本后端可能不同 | 参考分层处理，继续使用已在真实 Demo 验证的 demoparser2 0.42.0，不为 UI 重构更换解析后端 |
| [spicy/strafe-analyzer](https://github.com/spicy/strafe-analyzer) | 已弃用，面向旧 CS:GO / CS:S，使用进程内 DLL | 不符合本项目只观察边界，未采用 |

Windows 自启动使用微软文档中的 [HKCU Run](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)，命令指向固定安装路径并带 `--collector`，不需要管理员。只修改 StrafeLab 自己的值，改动前保存备份。

1.5 分离统计窗口与托盘采集。空闲只检查进程是否存在及文件元数据，不读取 CS2 内存、不注入、不产生按键。游戏开始后启动现有 Raw Input / Hall / GSI；正常退出等待保存并释放录制对象。游戏运行时暂停 Demo 解析；后台扫描以短生命周期进程完成有限批次，结束后释放内存。流式 JSON 写入减少大字符串分配，但录制期间仍保留本局数据，不声称恒定内存。
