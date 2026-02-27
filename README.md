# REPO_Active v5.1.0

This is a practical mod for REPO:
it supports **remote extraction-point activation** using the game's native `ExtractionPoint.OnClick()` logic.
It keeps full in-game feedback (broadcast + marker + reward), and provides stable route planning, manual control, and optional auto mode.
This mod reduces map-running pressure and improves overall gameplay flow.
**TL;DR: the mod guarantees the last point is the one closest to spawn, which makes full submission routes easier to finish and return. Middle extraction points are ordered by global shortest-path planning, like having a reliable teammate pre-plan and guide your route.**
**It supports `manual/auto modes`, whether to `discover all extraction points by default`, activation only among `player-discovered extraction points` (to preserve exploration gameplay), and even `remote activation in other players' rooms` (an accidental but potentially useful behavior).**

## Extraction Point States
- **Extraction point**: a map point that can be opened.
- **Not activated**: the point has not been opened yet.
- **Activated**: the point is currently open and active.
- **Submitted/Completed**: this point is finished and no longer joins later activation steps.

## Features

- **Remote activation with native behavior**: uses the same OnClick logic as in-game interaction, with results equivalent to pressing the extraction-point activation button manually.
- **Predictable order**: queue planning is based on NavMesh path distance to reduce backtracking. Rule: lock the spawn-nearest point as first, lock the nearest-to-spawn among remaining points as last, order middle points by inter-point optimal path, and auto-replan when unexpected activation happens.
- **Safety first**: when a point is already active, the mod will not start another activation.
- **Discovery filter**: when `DiscoverAllPoints=false`, only discovered points are eligible for activation.
- **Multiplayer host flow**: discovery logic can combine all players' positions and is applied by the host.
- **Manual-mode exploration planning**: the spawn-nearest remaining point is locked as the final activation target (regardless of discovery); it is only allowed when it is the only point left on the map.

## Fixed Issues
- Fixed the common "random activation / wrong order" problem. The old behavior used straight-line coordinate distance, which could produce wrong activation order.
- Route ordering is now more reasonable: it uses **actual path distance**, not only linear distance.
- If a point is already active, the mod will not force-open a new one, avoiding conflicts.
- This version should be the stable complete version.

## Current Activation Rules
1. Build candidate points (all points or discovered-only).
2. Generate one activation queue.
3. Activate only the first point in the queue each time.
4. If the actual activated point is not the target point, replan the queue immediately.
5. If any point is currently active, do not send a new activation request.

## Hotkey
- `F3`: in manual mode, activate the next point in the queue.

## Configuration
Config file: `BepInEx\\config\\angelcomilk.repo_active.cfg`

- `AutoActivate`: whether to auto-activate extraction points.
- `ActivateNearest`: manual activation hotkey (default `F3`).
- `DiscoverAllPoints`: whether all extraction points are discovered by default.
- `EnforceHostAuthority`: in multiplayer, whether only the host can activate extraction points. If disabled, remote activation can be used in other players' rooms (not the original design intent, but potentially useful). In that case, non-host cannot use other players' positions to mark discovery.

## Installation (r2modman)
1. Import the zip.
2. Confirm DLL path:
   `BepInEx\\plugins\\REPO_Active\\REPO_Active.dll`

## Author
**AngelcoMilk - Angel Cotton**
---

# REPO_Active v5.1.0

这是一个给 REPO 用的实用模组：
它可以**远程激活提取点**，而且走的是游戏原生 `ExtractionPoint.OnClick()` 逻辑。
保留完整游戏反馈（广播 + 标记 + 奖励），并提供稳定的规划顺序、手动控制与可选自动模式。
这个模组可以帮助你减少跑图负担，并显著提升整体游戏体验。
**省流：该模组保证最后一个点为距离出生点最近 —— 方便跑全部提交完成回家，中间提取点按照全图最短路程规划激活顺序（能达到相当于有一个可靠的队友帮你提前规划路线并导航，不需要再偌大的地图里苦恼了）**
**有`自动手动模式`，是否`默认发现所有提取点`，只在有`玩家发现过的提取点中进行激活`（是否保留这种探索的独特乐趣），甚至可以`在他人的房间里按照最优路径激活提取点`（这个是设计中意外的功能，并非初衷，但是也许有人会想要）**

## 提取点状态
- **提取点**：地图上可以开启的撤离点。
- **未激活**：还没开启的撤离点。
- **已激活**：已经开启的撤离点（当前生效中）。
- **已提交/已完成**：该撤离点已经提交完成，不再参与后续激活。

## 功能

- **远程激活但仍保持原生体验**：使用与游戏内交互一致的 OnClick 逻辑，效果与玩家手动按下激活提取点按钮一致。
- **顺序可预期**：采用基于 NavMesh 路径距离的队列规划以减少折返；规则为：固定出生点最近提取点为首位、固定其余点中距离出生点最近者为末位，中间点按点间最优路径排序，并在发生非预期激活时自动重排。
- **安全优先**：当已有提取点处于激活中时，不会启动新的激活。
- **发现过滤**：当 `DiscoverAllPoints=false` 时，仅已发现的点参与激活。
- **多人友好（主机）**：发现逻辑可结合所有玩家位置，由主机统一生效。
- **手动模式探索规划**：距离出生点最近的提取点会的排序会固定在最后激活 (无论是否发现，只有当地图上只剩这个末尾提取点时才会进入激活)

## 修复的问题
- 修复了“乱激活、跳顺序”的常见问题 —— 因为之前是按照提取点的坐标进行直线计算的，所以会出现激活顺序问题。
- 现版本路线排序更合理：按**实际路径距离**排，不只看直线远近。
- 场上已有激活点时，不会强行再开新点，避免冲突。
- 该版本应该是完美的版本啦

## 现在的激活规则
1. 先找候选点（全图或仅已发现）。
2. 生成一个“激活队列”。
3. 每次只激活队列第一个点。
4. 如果实际激活的不是目标点，马上重排队列。
5. 只要有点正在激活中，就先不发新激活。

## 快捷键
- `F3`：手动模式下，激活队列里的下一个点。

## 配置说明
配置文件：`BepInEx\\config\\angelcomilk.repo_active.cfg`

- `AutoActivate`：是否自动激活提取点。
- `ActivateNearest`：手动激活按键（默认 `F3`）。
- `DiscoverAllPoints`：是否默认全图提取点已发现。
- `EnforceHostAuthority`：多人下是否仅主机可执行提取点激活 —— 如果关闭该限制，将可以在他人的房间中远程激活提取点 —— 这个不是本意，不过有可能带来帮助就保留了（当然，在这个情况下不能使用其他玩家的位置标记提取点是否发现 —— 因为你不是主机~）。

## 安装（r2modman）
1. 导入 zip。
2. 确认 DLL 在：
   `BepInEx\\plugins\\REPO_Active\\REPO_Active.dll`

## 作者
**AngelcoMilk-天使棉**
