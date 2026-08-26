# NINA 远程台主节点/从节点集群设计（AI Weather Advanced + RRCI Advanced）

状态：**冻结 v1，进入实现**  
日期：2026-08-26  
适用项目：`nina.plugin.aiweather-advanced`、`nina.plugin.rrci-advanced`

## 1. 目标与范围

同一局域网内可能有多台运行 NINA 的电脑，但全天相机、在线天气 API 和屋顶控制器都不应被每台电脑重复占用。本设计把两个插件都做成**一个安装包、一个驱动标识、在插件设置中选择工作模式**的形式：

- **独立模式（Standalone）**：保持单机行为，兼容现有用户。
- **主节点模式（Primary）**：唯一连接真实数据源或硬件，并向局域网发布状态；按策略接收从节点命令。
- **从节点模式（Replica）**：不打开真实数据源或硬件，只读取主节点状态；RRCI 从节点可在显式授权后向主节点提交高层命令。

本阶段不做自动选主、主节点自动接管、跨公网连接、云端中继或分布式共识。系统采用**显式角色、单写者、失联即故障安全**的原则。

## 2. 不可违反的系统约束

1. 同一插件不得拆成“主节点包”和“从节点包”。角色是同一个二进制内的持久化选项。
2. AI Weather 从节点不得打开 RTSP/HTTP/文件夹采集，不得调用 Gemini/OpenAI/Ollama/本地识别，也不得写教师—学生数据集。
3. RRCI 只有主节点可以创建 `RRCI.Dome` COM 对象并打开 COM6；从节点进程不得加载旧驱动。
4. 同一时刻只有一个屋顶硬件写入者。不支持自动主节点故障转移。
5. 未取得新鲜、完整、通过身份校验的状态时，一律按未知/不安全处理，绝不沿用上一次 Safe 或可动作状态。
6. 网络令牌不得写入日志、状态响应或异常文本；设备密码、RTSP 凭据同样不得通过集群协议传播。
7. RRCI 的远程开关命令默认关闭。即使启用，也不能替代真实的机械限位、防撞、雨水和断电保护。

## 3. 总体架构

```text
                    局域网（仅受信任网段）

 全天相机 ──> AI Weather Primary ──HTTP/JSON──> AI Weather Replica(s)
              采集 + 一次分析                  只读安全状态
              API/数据集唯一写入者             无相机、无模型、无数据集

 RRCI 控制器 <── RRCI.Dome <── RRCI Advanced Primary
   (COM6)                         │
                                  └──HTTP/JSON──> RRCI Advanced Replica(s)
                                      状态镜像 + 受控命令意图
```

两个插件采用相同的角色、认证、序列号、新鲜度和错误语义，但各自携带协议实现，不额外发布共享 DLL，以免一个插件升级影响另一个插件加载。

## 4. 公共角色与配置

### 4.1 角色枚举

协议和设置存储使用稳定英文值：

- `Standalone`
- `Primary`
- `Replica`

中文 UI 显示“独立模式 / 主节点 / 从节点”。修改角色后，已连接的设备必须先断开再重连；程序不得在已连接状态下悄悄把本地硬件换成网络代理。

### 4.2 公共配置

| 配置 | AI Weather 默认值 | RRCI Advanced 默认值 | 说明 |
|---|---:|---:|---|
| 监听地址 | `0.0.0.0` | `0.0.0.0` | 仅主节点使用 |
| 端口 | `18910` | `18911` | 避开 NINA Advanced API 的 1888 |
| 主节点地址 | `http://192.168.10.121:18910` | `http://192.168.10.121:18911` | 从节点使用，用户可改 |
| 共享令牌 | 空 | 空 | 主从模式必须设置；至少 16 字符 |
| 轮询间隔 | 5 秒 | 2 秒 | 仅从节点使用 |
| 传输失效时间 | 20 秒 | 10 秒 | 超过即失联；与分析年龄分开 |

主节点使用 `TcpListener` 实现最小 HTTP/1.1 服务，避免 `HttpListener` 在 Windows 上依赖管理员配置 URL ACL。服务只接受固定路径、固定方法、有限报文大小和 UTF-8 JSON。

### 4.3 身份认证

每个请求携带：

```http
Authorization: Bearer <shared-token>
```

服务端对令牌 UTF-8 字节做常量时间比较。令牌为空、太短、不匹配均返回 `401`。日志只记录来源地址、路径和结果，绝不记录请求头或令牌。局域网令牌不是传输加密；生产部署仍需 Windows 防火墙限制来源 IP。跨公网必须另加 VPN/TLS，本版本不直接暴露公网。

### 4.4 公共信封

每个状态响应至少包含：

```json
{
  "schemaVersion": 1,
  "product": "ai-weather-advanced",
  "nodeId": "machine-stable-id",
  "sessionId": "new-guid-per-primary-start",
  "sequence": 42,
  "generatedUtc": "2026-08-26T01:02:03.456Z"
}
```

- `sessionId` 在主服务每次启动时变化，避免从节点把旧主节点的序列号误当作新数据。
- `sequence` 在同一会话内严格递增。
- 从节点接受状态时同时记录**本机接收时刻**；传输新鲜度不得只相信主节点的系统时钟。
- `generatedUtc` 用于诊断和显示，发现两台电脑时钟偏差过大时应报警，但不作为唯一失效计时器。

### 4.5 通用端点

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `/api/v1/health` | 服务身份、协议版本和会话；不包含敏感状态 |
| `GET` | `/api/v1/status` | 当前产品状态 |

错误响应使用固定结构：`code`、`message`、`retryable`。未知路径返回 `404`；错误方法返回 `405`；超过大小限制返回 `413`。

## 5. AI Weather Advanced 设计

### 5.1 三种模式的行为

| 能力 | 独立 | 主节点 | 从节点 |
|---|:---:|:---:|:---:|
| 打开 RTSP/HTTP/文件夹 | 是 | 是 | **否** |
| 本地/在线天气分析 | 是 | 是 | **否** |
| 教师—学生数据集 | 是 | 是 | **否** |
| 本地 ASCOM 外部安全监视器 | 可选 | 可选 | 可选（只允许收紧） |
| 发布网络状态 | 否 | 是 | 否 |
| 读取主节点状态 | 否 | 否 | 是 |
| 向 NINA 提供 `ISafetyMonitor` | 是 | 是 | 是 |

### 5.2 天气状态结构

`GET /api/v1/status` 在公共信封外增加：

```json
{
  "connected": true,
  "monitoring": true,
  "isSafe": false,
  "safetyReason": "cloud-threshold",
  "weatherCondition": "MostlyCloudy",
  "cloudCoverage": 66.0,
  "confidence": 100.0,
  "rainDetected": false,
  "fogDetected": false,
  "provider": "Gemini",
  "model": "gemini-3.5-flash-lite",
  "analysisUtc": "2026-08-26T01:01:58.000Z",
  "analysisAgeSeconds": 5.4,
  "sourceFresh": true
}
```

`isSafe` 是主节点已经合并云量阈值、雨雾、分析有效期、太阳高度限制以及主节点本地外部安全监视器后的最终结果。主节点不发布 API 密钥、相机 URL、用户名、密码、站点坐标或教师原始输出。

第一版不通过协议传输实时视频或完整帧，避免重复带宽与不受控的图像保留；从节点预览显示远程结果和连接状态。后续若需要缩略图，必须作为独立、限速、可关闭端点设计。

### 5.3 从节点状态机

```text
连接/重连
   └─> WaitingForFreshStatus (Unsafe)
          ├─认证失败────────────> AuthenticationFailed (Unsafe)
          ├─网络失败/超时───────> Unreachable (Unsafe)
          └─收到新 session/sequence
                 └─> Synchronized
                        ├─状态新鲜且主节点 Safe ─> Safe
                        ├─主节点 Unsafe──────────> Unsafe
                        └─超过传输失效时间───────> Stale (Unsafe)
```

从节点每次 NINA 连接都清空旧状态，必须收到一次新响应才可能 Safe。相同 `(sessionId, sequence)` 可作为心跳维持传输连接，但只有主节点声明 `sourceFresh=true` 且 `isSafe=true` 才可能安全。主节点 Unsafe 永远不能被从节点改成 Safe；从节点自己的外部 ASCOM 安全监视器、网络新鲜度等条件只允许进一步变 Unsafe。

必须把两种时间明确区分：

1. **传输新鲜度**：从节点最近一次成功读到主节点的本机时间，默认 20 秒。
2. **分析新鲜度**：主节点最近一次成功分析的年龄，由主节点策略计算并发布。

网络正常但相机/分析已停滞时，传输仍可新鲜，但 `sourceFresh=false`、`isSafe=false`；网络断开时则由从节点自己的传输计时器立即收口。

### 5.4 生命周期

- 独立/主节点沿用现有采集、分析、数据集和预览路径。
- 主节点在 SafetyMonitor 成功连接后启动服务；断开或插件卸载先停止服务再释放采集资源。
- 从节点连接时只启动轮询客户端，不初始化 `UnifiedCaptureService`、模型或数据集写入器。
- 从节点设置页隐藏或禁用与相机、模型、数据集有关的执行性配置，并显示“这些设置仅供独立/主节点使用”。保留已有值，切回角色后可继续使用。
- 角色或网络配置在连接期间改变时提示重连，不进行热角色切换。

## 6. RRCI Advanced 设计

### 6.1 产品边界

`RRCI Advanced` 是新的 NINA `IEquipmentProvider<IDome>` 插件，提供一个 `IDome` 设备。它不是对旧 VB6 `RRCI.Dome` 源码的复制或重新打包：

- 独立/主节点通过已安装的 `RRCI.Dome` ProgID 创建 COM 驱动并调用它。
- 从节点只创建网络代理，绝不调用 `Type.GetTypeFromProgID("RRCI.Dome")`。
- 旧驱动仍负责实际串口协议、限位与硬件状态；Advanced 层负责唯一所有权、网络镜像、认证、幂等和跨节点安全门。

仓库和产品名使用 `nina.plugin.rrci-advanced` / `RRCI Advanced`，但一个仓库只产出一个插件包。

### 6.2 状态结构

`GET /api/v1/status` 增加：

```json
{
  "connected": true,
  "shutterStatus": "Open",
  "slewing": false,
  "atPark": false,
  "atHome": false,
  "azimuth": 0.0,
  "altitude": 0.0,
  "driverFollowing": false,
  "lastHardwareReadUtc": "2026-08-26T01:02:03.000Z",
  "hardwareFresh": true,
  "remoteCommandsEnabled": false
}
```

无法读取的能力使用 `null` 或稳定的 NotImplemented 行为，不得伪造硬件支持。

### 6.3 命令端点与幂等

| 方法 | 路径 | 动作 |
|---|---|---|
| `POST` | `/api/v1/commands/open` | 请求开屋顶 |
| `POST` | `/api/v1/commands/close` | 请求关屋顶 |
| `POST` | `/api/v1/commands/stop` | 停止屋顶动作 |
| `POST` | `/api/v1/replicas/heartbeat` | 上报从节点及其本地赤道仪安全状态 |

命令体：

```json
{
  "commandId": "caller-generated-guid",
  "expectedSessionId": "primary-session-guid",
  "observedSequence": 42,
  "requestedBy": "replica-node-id"
}
```

- `commandId` 在主节点保留有界结果缓存（最多 2048 条，并优先清理超过 24 小时的结果）；重试同一 ID 返回原结果，不重复驱动硬件。
- `expectedSessionId` 不匹配返回 `409`，避免主节点重启后执行旧队列命令。
- `observedSequence` 落后过多时拒绝，迫使从节点先刷新状态。
- 同一时间只执行一个屋顶动作。开/关重复请求若已处于目标状态，返回成功但不再次操作。
- `stop` 仍要求认证并与其他动作串行化；其能力受旧驱动支持情况限制。v1 不承诺网络 Stop 能抢占已经阻塞在旧 COM 驱动内部的调用，物理急停仍不可替代。

### 6.4 从节点注册与赤道仪心跳

RRCI Advanced 从节点通过 NINA 的 `ITelescopeMediator.GetInfo()` 读取本机赤道仪摘要，并定期向主节点发送：节点 ID、实例会话、连接、AtPark、AtHome、Slewing、Tracking 和本机采样时间。绝不允许从节点直接控制其他电脑的赤道仪。

主节点维护显式的“已登记从节点”列表和心跳有效期。关闭屋顶前，若启用了跨节点赤道仪安全门，则所有要求参与的节点必须：

- 心跳新鲜；
- 赤道仪状态可确定；
- 满足用户选择的收顶条件（默认要求 `AtPark=true` 且 `Slewing=false`）。

任何节点失联或状态未知都拒绝自动收顶，除非现场存在经过验证、独立于电脑的物理防撞互锁并由用户明确启用相应绕过策略。

### 6.5 关键安全事实

当前旧驱动配置曾显示 `Scope Safe=0`，而 NINA 配置中 `ParkMountBeforeShutterMove=false`。因此软件无法证明“网络失联但暴雨时强行关顶”和“等待赤道仪安全后再关顶”哪一个更安全。本插件不得把这个矛盾藏起来：

- **远程命令默认关闭**。
- **远程开顶默认关闭**。v1 不把 AI Weather 结果直接接入屋顶命令授权；即使用户手工打开高风险选项，也只表示接受一次明确的 NINA 远程请求，不能据此构建无人值守自动开顶。
- 关顶策略必须由用户按现场机械结构选择；没有物理互锁前，不宣传为无人值守最终安全方案。
- `Close` 被安全门拒绝时必须向 NINA 和日志返回具体阻塞节点，而不是静默成功。

### 6.6 `IDome` 映射

- `Connected`：独立/主节点表示旧驱动连接；从节点表示认证成功且状态新鲜。
- `ShutterStatus`、`Slewing`、`AtPark`、`AtHome`、`Azimuth`、`Altitude`：来自主节点的最新状态。
- `OpenShutter` / `CloseShutter` / `StopShutter`：独立模式直接调用旧驱动；主节点本地调用；从节点提交幂等命令。
- `SetupDialog`：打开 Advanced 设置，不透传从节点到旧驱动设置页。
- 未实现或旧驱动不支持的圆顶方位能力明确返回 `Can*=false` 或抛出 `NotSupportedException`，不做假实现。

## 7. 故障矩阵

| 故障 | AI Weather 从节点 | RRCI 从节点 | 主节点硬件/源 |
|---|---|---|---|
| 主节点进程退出 | 超时后 Unsafe | Connected=false，命令失败 | 不自动接管 |
| 网络断开 | Unsafe | 状态失效，命令失败 | 主节点继续本地运行 |
| 令牌错误 | Unsafe，明确认证失败 | 不连接、不发命令 | 记录匿名失败 |
| 主节点重启 | 新 session 前 Unsafe | 新 session 前不动作 | 清空命令去重上下文 |
| AI 相机/模型失败 | 主节点发布 Unsafe | 不适用 | AI 本地故障安全 |
| 旧 RRCI 驱动失联 | 不适用 | Connected=false | 禁止动作并发布错误 |
| 从节点 NINA 退出 | 不影响天气主节点 | 心跳过期，阻塞需要该节点确认的屋顶动作 | 不猜测赤道仪状态 |
| 两个主节点误配 | 客户端固定地址，不自动切换 | **禁止两者同时直连 COM6**；部署检查报警 | 人工纠正 |

## 8. 可观测性

两个插件都应显示：当前角色、本机节点 ID、主节点地址/监听端口、认证状态、主节点 session、最近成功通信、本机计算的新鲜度、最后错误。日志必须能区分：

- 本地源/硬件失败；
- 网络连接失败；
- 认证失败；
- 协议/版本不兼容；
- 状态过期；
- 命令被安全策略拒绝；
- 命令已接受、已完成或未知结果。

不得只写“连接失败”或把从节点网络心跳误称为一次新的天气分析。

## 9. 升级、兼容与部署

1. AI Weather 新增设置的默认角色是 `Standalone`，升级后现有行为不变。
2. RRCI Advanced 与旧 `RRCI.Dome` 并存，但同一台 NINA 只能选择并连接其中一个；主节点 Advanced 内部再连接旧驱动。
3. 主从两端 `schemaVersion` 主版本不兼容时拒绝同步并故障安全。
4. 先在不接真实设备的环境完成协议、认证、过期、幂等和安全门测试；再进行局域网双机只读测试；最后才在有人值守并确认屋顶/赤道仪安全时测试动作。
5. Windows 防火墙只开放主节点的对应端口，并限制来源为另一台 NINA 的固定 IP；不得为了方便开放整个公网配置文件。

## 10. 验收标准

### AI Weather

- 同一包内可选三种模式，默认独立模式无行为回归。
- 主节点只分析一次，从节点不产生 RTSP 连接、模型请求或数据集文件。
- 从节点在启动、认证失败、网络失联、主状态过期、协议不兼容时均为 Unsafe。
- 主节点恢复后无需重启 NINA，从节点自动恢复同步，但必须先收到新鲜状态。

### RRCI Advanced

- 同一包内可选三种模式；从节点运行期间进程内不创建 `RRCI.Dome`。
- 主节点为唯一旧驱动所有者，状态可被从节点读取。
- 命令默认禁用；启用后重复 `commandId` 不会重复操作。
- 旧 session、过期状态、未知赤道仪状态或安全门不满足时，动作被明确拒绝。
- 网络中断不会触发自动接管或补发历史动作。

## 11. 本轮实现和现场操作边界

本设计文档先于代码冻结。代码阶段允许修改项目、构建和运行纯软件模拟测试；**本轮未经用户再次明确授权，不安装插件、不重启 NINA、不连接 COM6、不发开关顶命令，也不改变另一台 NINA 的运行状态**。
