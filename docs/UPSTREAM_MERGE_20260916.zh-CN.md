# 2026-09-16 上游合并与 Gemini 免费池诊断

本轮基线为 ADVANCED `7f83675`，上游范围 `9e489fe..e5d6524`，共 7 个提交。用户截图来自朋友电脑；未读取该机实时日志或调用其 API。

## 合并取舍

| 上游内容 | ADVANCED 中的处理 |
| --- | --- |
| 失败响应不再产生 50% 伪读数 | 合并统一 WeatherResponseParser；保留教师有效性校验、来源元数据与 ONNX 回退编排。 |
| 模型请求参数兼容性 | 合并 Lean/Bare/Gemini2 profile、8192 输出预算与 MAX_TOKENS 检测。HTTP 400 后在下一轮或下一次检查使用更简单 profile，避免改变每次访问一个 HTTP 请求的约束。 |
| 模型 metadata 输出限制 | 保留独立解析/钳制工具；不增加启动 metadata 请求，避免离线/限额情况下额外延迟。 |
| 测试当前分析服务 | 合并工厂和测试按钮，工厂支持免费池、付费槽位与从节点接管配置；合成图像只用于手动测试，不改变安全状态或写入数据集。 |
| 提供商健康状态与相同帧诊断 | 结合 ADVANCED 的 AnalysisProvenance 统计连续在线失败、记录相同帧和外部 ASCOM Safe/Unsafe 切换；计划性本地检查不计为失败。 |
| 预览启动、意外断流重连 | 原生窗口只在已加载且有 PresentationSource 时创建；保持现有单终端共享视频会话与暂停后端互斥规则。意外断流按 2、4、8、16、30 秒上限重连，显式停止取消重连。 |
| 上游版本号与仓库元数据 | 保留 ADVANCED 标识和发布流程，本次合并不覆盖为 1.15.5.0。 |

## 单模型 RPD 与模型池

检查现有实现后确认：配额电路以 API key 指纹和精确模型名隔离；单个模型返回 RPD 后仍继续下一项。发现的问题在聚合诊断：某模型的每日额度信息可能覆盖其他模型的参数拒绝或不可用，从而产生“整个免费池额度用尽”的误导。

修改后：

1. 3.5 Lite 额度耗尽时，立即继续尝试 3.1 Lite。
2. 后续检查只跳过尚未恢复的模型，其他模型仍可访问。
3. 混合失败显示逐模型结果，不将单个 RPD 的次日恢复时间作为整个池的恢复时间。
4. 只有所有模型均在配额暂停时显示最早可重试时间；均为每日限额时另标记 `free_pool_daily_quota`。
5. 默认顺序为 3.5 Flash Lite → 3.1 Flash Lite → 3.8 Flash → 3.7 Flash → 3.6 Flash → 3.5 Flash → 3 Flash，默认仍为两轮。

## 验证与现场边界

自动化测试使用模拟 HTTP，覆盖单模型日额度不阻塞后继、跨检查隔离、混合配额与 HTTP 400、全部日额度耗尽、请求 profile 在下一轮生效且被记住、普通 Gemini 单请求、截断/非法响应、旧模型顺序迁移。上游适配后的离线测试及新增模型池测试共 61 项通过；已有 DatasetSmoke 完整回归通过。Release 构建通过，编译没有错误，保留项目既有的兼容性/可空性警告。

真实摄像头断流恢复、朋友电脑的实际额度/网络情况没有在本轮远程验证。现场排查应读取从 `Gemini Free pool cycle` 开始至成功或 `pool exhausted` 结束的完整日志，逐项核对 3.1 Lite 的 HTTP 状态；单张旧提示截图不足以确认原因。

参考：[上游提交](https://github.com/michelebergo/nina.plugin.aiweather/commit/e5d6524)、[Gemini 3.8 Flash 文档](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash)、[Gemini 配额文档](https://ai.google.dev/gemini-api/docs/rate-limits)。
