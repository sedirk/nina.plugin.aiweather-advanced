# Sky Sentinel 1.36.0.1

Sky Sentinel is the independently named successor to this repository's AI Weather Advanced fork.
The modified version is maintained by **sedirk** and derives from **AI Weather by Michele Bergo**.
The repository URL is unchanged. Original attribution and MPL-2.0 / historical MIT notices remain in the package.
This is not a release or endorsement by the upstream author.

## Changes

- Independent plugin GUID: `83bb4fd5-b304-4655-8fb5-cf01195ad34f`.
- Independent assembly: `NINA.Plugin.SkySentinel.dll`; safety monitor: **Sky Sentinel Safety Monitor** (`SkySentinelSafetyMonitor`).
- Independent settings group: `SkySentinel.Properties.Settings`, with a one-time local import from recognized legacy settings.
- Includes the upstream 1.16.0.0 integration and cancellable RTSP first-frame startup fix previously tested locally as 1.36.0.0.
- Restores the upstream **single native video window** implementation. Removes extra startup covers, nested child windows, clipping, snapshot-gated reveal and surface-promotion/recovery machinery.
- Keeps **complete-frame aspect-ratio fit**: the full image remains visible, with unused space when aspect ratios differ. It does not restore upstream's fill-and-crop behavior.
- Keeps one shared preview/analysis RTSP session per terminal, independent primary/replica previews, local takeover, cancellation, current-frame checks and stalled-stream recovery. No changes to weather thresholds, model weights or roof control.

The preview is no longer held behind a custom mask until a second snapshot succeeds. Native rendering now follows upstream behavior; this is not a guarantee about every graphics driver or disconnected-camera startup screen.

## Upgrade from this repository's old Advanced fork

1. Close NINA. Back up its profile and local `user.config` before replacing files.
2. Move the **old Advanced plugin's whole folder** outside `%LOCALAPPDATA%\NINA\Plugins\`. Do not put the old and new assemblies together or delete an unrelated upstream installation.
3. Extract the release ZIP into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Sky Sentinel\` so `NINA.Plugin.SkySentinel.dll` is directly in that folder.
4. Start NINA and check **Options → Plugins → Sky Sentinel**. Recognized settings from `AIWeather.Properties.Settings` (or the older `AllSkyCameraPlugin.Properties.Settings`) are imported once, including credentials, thresholds, role and standby configuration. The legacy group is not edited; a timestamped `user.config.before-skysentinel-*.bak` stays beside the local config. These files can contain secrets: do not publish them.
5. An existing Sky Sentinel settings group always wins, even for deliberately empty fields. A failed or malformed import is not silently marked complete. New installations do not scan for unrelated old datasets.
6. Select **Sky Sentinel Safety Monitor** in NINA's equipment list. Profiles still pointing to `AIWeatherSafetyMonitor` need this one-time selection. Saved sequences containing the old plugin's Start/Stop Monitoring instructions must have those instructions replaced by the Sky Sentinel equivalents; this release does not silently rewrite sequence files. NINA 3.3 expressions using explicit `AIWeather_` prefixes should use `SkySentinel_` instead. Unprefixed symbol names are unchanged.
7. Confirm the camera, configured/not-configured secret indicators, node role, thresholds and connections before unattended operation. Do not interpret a migrated configuration as a successful hardware safety test.

### Files and compatibility

- Existing custom dataset paths are kept. The previous default `%LOCALAPPDATA%\NINA\AIWeather\dataset\v1` is retained explicitly when present during a legacy import; samples are not copied or deleted.
- Fresh-install data defaults use `%LOCALAPPDATA%\NINA\SkySentinel\`. New diagnostic captures/decision archives use this independent folder; old archives remain untouched.
- Fresh-install status-file default is `Documents\N.I.N.A\skysentinel_conditions.txt`. Explicit custom paths are kept; an enabled legacy integration using the former default is retained as `sky_conditions.txt`.
- The LAN protocol is not renamed or changed by this identity migration. Upgrade each computer separately and verify its own camera access and settings. No automatic leader election or extra permissions are introduced.
- Keep the old plugin/config backups outside the scan path for rollback. To roll back, close NINA, move Sky Sentinel out, restore the old plugin folder and the previous profile/config as needed, then start NINA. Do not overwrite newer intentional settings without reviewing the backup.
- Publishing here does not submit a plugin to NINA's official catalog. The workflow's official-manifest PR remains opt-in and does not touch the upstream plugin's entry.
- The workflow prepares a draft release; the maintainer verifies the archive and installation before publishing it.

## Validation and package scope

- Offline NUnit regression: **253 passed, 0 failed, 3 skipped**. The complete DatasetSmoke suite also passed. Coverage includes identity, typed settings import, credentials preservation, non-overwrite behavior, malformed input, dataset paths, aspect fit, startup cancellation and existing capture/cluster/model-pool behavior.
- These checks do not consume live model quota or operate a roof. They do not replace real-camera first-frame testing on each deployment.
- Windows **x64 only**. Package guards enforce at most 180 MiB compressed and 260 MiB unpacked, excluding other operating systems/architectures, build/test outputs, training datasets, user settings and secrets.
- The bundled ONNX model is unchanged. Exact release ZIP size and SHA-256 are available from the release asset and generated manifest.

---

## 中文摘要与升级提醒

从本版起正式改名 **Sky Sentinel**，作者 **sedirk**，使用独立 GUID、DLL、设备 ID、设置组和高级序列指令身份，同时保留原作者 **Michele Bergo** 的版权与致谢。仓库地址不变。

预览层回归上游单层原生窗口，删除多层遮罩和等待截图才显露画面的状态机。**保留完整画面等比适配，不裁边**，同时保留已经修复的可取消首帧启动、同一终端共享预览/分析视频流、主从双端预览及断流恢复。原生窗口的启动显示遵循上游实现，不再通过更多遮罩隐藏它。

关闭 NINA 后，将旧 Advanced 插件整个目录移到插件扫描目录外留作回滚，再把新包解压到 `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Sky Sentinel\`。不要把两个 DLL 混在同一目录。

首次加载会将旧设置组中的配置和凭据导入新的独立设置组，先备份本机配置，旧组不改动；如果已有 Sky Sentinel 设置则不覆盖。原数据集路径会保留，不搬运、不删除样本。备份文件含有本机私密信息，不能公开上传。

设备列表需改选 **Sky Sentinel 安全监视器**。旧序列中的开始/停止监控指令需要重新添加 Sky Sentinel 版本；NINA 3.3 表达式的显式 `AIWeather_` 前缀改为 `SkySentinel_`。发布包本身不会擅自重写用户序列。安装本机时可在备份后单独迁移当前配置里的安全监视器选择。

本次不修改天气安全阈值、模型权重或屋顶控制。请在无人值守前检查迁移后的节点模式、相机/API 配置和安全设备连接，再现场验证首帧、停止/重试与断流恢复。离线测试不能替代实际相机与硬件联锁验收。
