# StrafeLab 1.0

> 当前为持续完善中的可运行版本。发布后的审查发现了录制片段关联、统计和 Demo 匹配问题，详见 [已知问题与验证边界](KNOWN-ISSUES.md)。Hall 读取已经实机验证，真实比赛的 Demo 配对校准仍待验收。

Windows x64 本地急停分析工具。主程序为 C# / .NET 8 WPF；发布包包含 .NET 运行时和 Demo 解析所需的 Python / demoparser2。运行时不需要联网，也不需要安装 Python。

## 直接运行

1. 解压整个发布目录，双击 `StrafeLab.exe`，保留所有 DLL、`demo-python`、`demo-runtime` 文件夹。
2. ACE68 Air 保持 USB 有线模式，关闭 M HUB 的设备连接。程序会自动尝试 Hall；界面收到 W/A/S/D 的有效报告后显示“Hall 连续键程已验证”。未连接、未知固件、通信失败时，Raw Input 继续工作。
3. 程序会定位 Steam 库并安装自己的 GSI 配置，然后启动/重启 CS2。未自动找到时用“手动选择 CS2 cfg”，选择 `<CS2>/game/csgo/cfg`。现有同名配置先备份为 `.bak`，其它游戏配置不修改。
4. 默认勾选“GSI 自动记录每局”。CS2 在前台、GSI 确认正在操控自己的角色时记录；切出、死亡/观战时暂停。地图切换、gameover 或 GSI 长时间断开时结束保存。也可手动开始、停止。
5. 在游戏外检查输入，先勾选“桌面诊断模式”，再开始记录。诊断会话不参加 Demo 校准。记录中不能切换模式。
6. 在 CS2 自己下载对应的比赛 Demo，或手动用游戏的 `record` / `stop` 命令录制；StrafeLab 不向游戏发送命令。程序扫描本地 `.dem`，结束后和以后启动时自动重试；历史窗口也支持手动选择文件。

## 数据与界面

- WASD 四个实时键程条，显示固件报告的毫米换算值。
- A→D、D→A、W→S、S→W 的 gap、overlap、反向键持键时长、Mouse1 相对切换时刻。
- 地面移动估算、速度曲线、严格低移速窗口，带置信度筛选。
- GSI 武器、玩家身份、地图、回合、生命状态；只绑定 `127.0.0.1`，使用本机随机 token。
- 每 5 秒原子保存，正常关闭也等待保存完成。会话位于 `%LOCALAPPDATA%/StrafeLab/sessions`。
- 历史窗口显示跨局 gap 趋势、各方向中位数/P95、持键统计，可以导出 CSV。
- JSON 保留输入、Hall、GSI、物理样本、同步/校准结果。默认只保存游戏内有效记录，桌面诊断需要显式打开。

## Hall 已实测可用

本次实机为 USB 名称 **Ace 68 Air-II**，`VID 41E4 / PID 2120`，固件字节 `17 01`（协议值 `0x0117`）。这对应用户当前连接的设备；不根据驱动里的营销标签推断普通版/电竞版。

关键通道是 **MI_01 / Usage Page 0x0001 / Usage 0x0000 / Report ID 0**。开启官方 `debugMode` 位后，收到 `0xA0` 开头的变化报告。当前实现只对这组已验证 VID/PID/固件启用监测，不对其它型号猜命令。

程序需要通过 HID 功能配置命令暂时打开监测位：先保存原值，仅修改配置字节 7 的 bit 3，正常退出时读回并恢复该位。其它配置位保留；不修改触发行程、RT、SOCD、校准数据、灯光或固件。异常退出/拔线时留下 `hall-debug-recovery.json`，同一设备重新连接并启动程序后恢复。不要在监测期间同时用其它驱动修改键盘配置。

原始键程 0–341，按官方 `100 单位/mm` 换算为 0–3.41 mm。这里是固件给出的键程值，不是独立测量的机械位移精度。报告是随变化上报，不能把它的速率等同于键盘的扫描率/8 kHz 键盘轮询率。HID 监测对游戏时的延迟影响尚未做对照实测。

**数字触发始终使用 Raw Input。侧栏可关闭 Hall 监测并恢复原监测位；重新勾选可重新连接。** Hall 深度不通过固定阈值变成按键边沿，避免错误模拟 RT。

## 指标定义与实际边界

- gap：旧方向松开 → 新方向按下的空档，候选最大 500 ms。
- overlap：新方向按下后，两键同时保持按下，直到其中一键首次释放。尚未释放显示 `…`。
- reverse hold：新方向实际按下 → 实际释放。中途结束记录的未完成样本保留为空。
- timing：Mouse1 按下减去最近 500 ms 内的反向切换时间。Mouse1 是开枪意图，不等同于每一发子弹；持续扫射的所有子弹只存在 Demo 事件中。
- 输入使用独立 Raw Input 线程与 QPC 微秒时间戳。这是应用接收时刻，不是键盘硬件/游戏 subtick 的原生时间戳。消息排队延迟较大时降低置信度。
- 模型是 128 Hz 最大子步长的地面 friction / acceleration 近似，带武器速度基线。它不是完整 CS2 引擎复刻，无法从普通 GSI 获知碰撞、接地、视角和开镜的完整状态。跳跃、蹲伏、鼠标转向、未知开镜状态等降低模型置信度。
- 界面的低移速窗口使用保守的 34 u/s 阈值，**不代表后坐力、开镜、跳跃误差已恢复，也不保证子弹精确命中**。武器速度基线不是自动获取的服务器参数。
- 默认假定 WASD、Shift 慢走、Ctrl 蹲伏、Space 跳跃、Mouse1 开火。自定义绑定、游戏内聊天/菜单消耗输入等可能使物理估算不成立。

## Demo 同步与校准

Demo 使用独立进程中的 `demoparser2 0.42.0`，只读取本地文件。只认 `PBDEMS2`，默认 64 tick/s；特殊 tickrate、暂停/时间跳跃导致的非线性时间关系可能被拒绝。

同步以本机 SteamID 过滤玩家，使用 Mouse1 / weapon_fire 的 burst onset，以及 GSI freezetime→live、重生和 Demo 对应事件。候选 offset 投票、单调一对一匹配、MAD 剔除、仿射时钟拟合；要求至少 6 个锚点、2 类事件、跨度 30 秒、残差 ≤50 ms、时钟倍率偏差 ≤0.2%，且没有接近的竞争匹配。

校准更严格：同步残差 ≤25 ms，限制在锚点覆盖时间内；只用同一玩家、接地、存活、未开镜、未蹲/慢走、正常移动类型、未受减速影响、视角稳定且本地输入可信的相邻速度样本。过滤明显碰撞/修正，至少 200 对样本，并同时包含足量滑行和加速。拟合 acceleration/friction 后用时间上后 30% 的样本验证；改进不足 10% 或参数异常则不应用。成功参数用于后续新会话，已有历史物理曲线不伪装成重新模拟的真值。

程序不会替你下载受账户保护的比赛 Demo。没有对应文件或可信锚点时保留原模型，这是正常结果。

## 开发与验证

```powershell
dotnet restore .\StrafeLab.sln
dotnet test .\StrafeLab.sln
dotnet run --project .\src\StrafeLab
```

完整离线发布（构建阶段需要联网、.NET SDK，以及带 pip 的 Python；终端用户运行不需要这些）：

```powershell
.\tools\prepare-offline-runtime.ps1
.\tools\publish.ps1
python .\tools\package-release.py
```

准备脚本下载校验过的 Python 3.12.10 embeddable，并按 `tools/demo-requirements.txt` 安装固定版本的 Windows 解析依赖到项目 `work/`。发布脚本不把会话、token、完整抓包或浏览器日志放入发布包。ZIP 包含文件清单，外部 `SHA256SUMS.txt` 可检查下载完整性。

开发环境可运行 `tools/install-demo-parser.ps1` 安装解析依赖。可用 `STRAFELAB_PYTHON`、`STRAFELAB_DEMOPARSER_PATH` 指定已有运行时；`STRAFELAB_DATA_DIR` 可指定独立数据目录。完整发布包已包含所需依赖。

研究与验证细节见 `RESEARCH.md`、`VERIFICATION.md`。`evidence/` 包含去除设备路径的 HID 描述符清单、仅 WASD 的受控抓包 CSV、独立 C# 样本和验证摘要。

本程序不生成游戏输入、不自动急停、不读取 CS2 内存、不注入游戏。灯光反馈尚未对当前固件验证，未启用。
