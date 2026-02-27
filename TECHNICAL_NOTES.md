# REPO_Active 技术说明（当前版本）

适用版本：`5.1.0`
源码根目录：`C:\Users\Home\Documents\GitHub\REPO_Active`

## 1. 目标与边界

本模组的核心目标：
- 通过游戏原生 `ExtractionPoint.OnClick()` 进行远程激活。
- 保持与原生交互一致的结果（广播、标记、奖励链路）。
- 支持手动（`F3`）与自动激活两种模式。
- 支持两种发现模式：`DiscoverAllPoints=true/false`。

当前边界：
- 不再包含独立日志模块与日志文件写入代码（发行纯净版）。
- 不在代码里强制“仅主机可激活”；是否能激活由游戏原生链路本身决定。

## 2. 代码结构

关键文件：
- `src/REPO_Active/Plugin.cs`
- `src/REPO_Active/Runtime/ExtractionPointScanner.cs`
- `src/REPO_Active/Reflection/ExtractionPointInvoker.cs`

职责划分：
- `Plugin.cs`：配置读取、生命周期、轮询驱动、激活流程编排。
- `ExtractionPointScanner.cs`：提取点扫描、状态读取、发现集合维护、路径规划、激活态检测。
- `ExtractionPointInvoker.cs`：反射调用 `OnClick()`。

## 3. 配置项（当前）

配置文件：`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate`（bool）
  - `false`：手动模式，仅 `F3` 触发。
  - `true`：自动模式，按定时流程触发。
- `ActivateNearest`（KeyCode）
  - 手动激活按键，默认 `F3`。
- `DiscoverAllPoints`（bool）
  - `true`：全图默认已发现。
  - `false`：按玩家位置半径动态发现。

## 4. 运行流程

### 4.1 场景加载

`OnSceneLoaded` 会重置本局状态：
- 自动模式计时器与准备时间。
- 发现计时器。
- 扫描器内部缓存（发现集合、激活标记、路径缓存、anchor 缓存）。
- 玩家缓存与发现轮询间隔基线。

### 4.2 发现流程

每个发现轮询周期执行 `DiscoveryTick()`：
- 先刷新提取点列表。
- `DiscoverAllPoints=true`：直接把所有点加入已发现集合。
- `DiscoverAllPoints=false`：
  - 先等待 `AUTO_READY_BUFFER=30s` 缓冲，避免进局早期数据不稳定。
  - 优先使用主机可见的所有玩家位置做半径发现。
  - 如果玩家列表暂时取不到，退化为参考位置（相机/本地玩家）发现。

半径：`DISCOVER_RADIUS=20m`。

### 4.3 激活流程（手动与自动共用）

统一由 `ActivateNearest()` 执行：
1. 刷新提取点。
2. 若任意点处于“激活中”（非 Idle 且非 Completed-like），直接阻塞新激活。
3. 计算参考位置并捕获 spawn 锚点。
4. 根据发现模式形成候选集。
5. 规划队列并选取首个目标。
6. 调用 `ExtractionPoint.OnClick()`。
7. 若状态已进入非 Idle/非 Complete，立即 `MarkActivated`；否则延迟短轮询补标记。

自动模式只是在 `Update()` 中按间隔调用这条链路：
- 先满足 30 秒准备缓冲。
- `AUTO_INTERVAL=5s` 周期触发。

## 5. 排序与规划规则

核心在 `ExtractionPointScanner`：

### 5.1 路径度量

全部基于 NavMesh 路径长度：
- `TryGetPathLength(from,to)` 使用 `NavMesh.CalculatePath`。
- 对端点先 `NavMesh.SamplePosition`（3m，失败再 10m）提升可达性。

### 5.2 锚点规则

全局锚点由 `TryGetGlobalAnchorsNoCache` 计算：
- `firstAnchor`：距离 spawn 最近的点。
- `tailAnchor`：在剩余点中距离 spawn 最近的点。

### 5.3 DiscoverAll=true

使用 `BuildPlanDiscoverAllFixedAnchors`：
- 优先固定全局 first/tail（若可用）。
- 中间点对全排列做 DFS，计算全局最短路径。
- 受 `skipActivated` 与 completed 状态过滤。

### 5.4 DiscoverAll=false

使用 `BuildStage1PlannedList` + 尾点策略：
- first/tail 采用本局缓存锚点（首次由最近规则确定）。
- 中间点同样做全排列 DFS 最短路。
- 在 `Plugin.ShouldHoldTailPointActivation` 中，若还有未完成的非尾点，尾点被锁定不自动触发。

## 6. 状态判定标准

状态读取：`currentState`（反射字段/属性兜底）。

- Idle-like：名称包含 `Idle`。
- Completed-like：名称包含以下任一关键字（忽略大小写）：
  - `success`
  - `complete`
  - `submitted`
  - `finish`
  - `done`

并发保护：
- 只要扫描到任一点状态为“非 Idle 且非 Completed-like”，视为激活进行中，阻塞新请求。

## 7. 缓存策略

### 7.1 本局缓存

在 `ResetForNewRound()` 清空：
- 发现集合 `_discovered`
- 激活标记 `_activatedIds`
- spawn 路径缓存 `_spawnPathCache`
- 点间路径缓存 `_edgePathCache`
- 锚点 `_firstAnchorId/_lastAnchorId`

### 7.2 玩家缓存

`Plugin.TryGetAllPlayerPositionsHost()`：
- 对玩家列表做哈希，列表变化时重建缓存。
- 保存最近坐标用于发现降级链路。
- 当玩家进出房间（Harmony patch `NetworkManager.OnPlayerEnteredRoom/OnPlayerLeftRoom`）时刷新间隔与缓存。

## 8. 打包与发布

打包脚本：`scripts/pack.ps1`

功能：
- 校验 `manifest.json` 与 `PluginVersion` 一致。
- 校验 `website_url` 必须为 `https://`。
- `dotnet build -c Release`。
- 校验 `README.md` 必须是 UTF-8 无 BOM。
- 输出 zip：
  - 测试版：`REPO_Active_test_r2modman.zip`
  - 发行版：`REPO_Active_release_r2modman.zip`

打包内容：
- `manifest.json`
- `README.md`
- `icon.png`
- `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## 9. 已知风险点

- 游戏更新若改动 `ExtractionPoint` 类型名、`OnClick` 签名或 `currentState`，会导致反射失效。
- 状态字符串命名若变更，会影响 Idle/Complete 判定。
- NavMesh 在个别地图上若出现不可达边，可能导致候选路径被过滤，进而改变排序结果。
- 发现模式下，进局初期数据时序异常时仍可能出现短暂“已发现不足”，30s 缓冲已用于降低该风险。

## 10. 建议回归测试

每次发布前最少覆盖：
1. 单人 + `DiscoverAll=true` + 手动模式：连续 `F3` 完整激活。
2. 单人 + `DiscoverAll=true` + 自动模式：按规则完成全队列。
3. 单人 + `DiscoverAll=false`：发现多个点后，验证尾点锁定与释放。
4. 多人（主机）+ `DiscoverAll=false`：任意玩家靠近可触发发现。
5. 跨局测试：上一局结束后新关卡状态能正确重置，不继承旧缓存。