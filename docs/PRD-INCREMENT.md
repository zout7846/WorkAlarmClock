# WorkAlarmClock 工作日记桌宠 — 增量需求文档（PRD-INCREMENT）

| 项目信息 | 内容 |
|---|---|
| 文档语言 | 简体中文（代码标识符英文） |
| 文档版本 | v1.1 增量（相对 v1.0） |
| 上游文档 | docs/PRD.md（v1.0）、docs/ARCHITECTURE.md（v1.0 §4 数据模型 / §5 接口 / §9 共享约定 / T01–T05）、docs/CHECKLIST.md（v1.0 验收） |
| 范围 | **仅描述变更部分**；未提及的 v1 需求（R-01 拖拽贴边、R-02 时钟、R-03 表情、R-04 作息、R-05 状态机、R-06 锁屏、R-07 空闲、R-08 手动、R-09 弱提醒、R-10 强提醒、R-11 事项、R-15 托盘、R-18 统计、R-20 自启……）维持 v1 不变 |
| 目标用户 | 不变（中文上班族，个人电脑自用） |

> 本增量文档与 v1 PRD 共同构成完整需求。所有"默认建议"在用户未提出反对时即按默认执行。

---

## 一、增量概述（5 个变更域与需求映射）

| 变更域 | v1 原需求 | 增量动作 | 新 ID |
|---|---|---|---|
| ① 本地数据库改用 SQLite | R-14 持久化（JSON 文件） | **替换**底层存储；功能性验收保持 | R-25（新增，替换 R-14 底层） |
| ② 取消"点击桌宠消失"，改全局热键 + 点击互动 | R-01 桌宠交互 | **修改**点击行为（移除隐藏）+ **新增**热键模块 | R-26（改 R-01 部分）、R-22（新增热键） |
| ③ 汇报导出 Markdown → 纯 txt，栏位重排 | R-12 生成、R-13 落盘、R-19 预览 | **替换**格式与栏位顺序、第三栏占位留空 | R-27（改 R-12/R-13/R-19） |
| ④ 设置页布局优化 + 时间选择改下拉框 | R-16 设置面板 | **修改**布局 + **新增**时间下拉控件 | R-28（改 R-16）、R-24（新增时间下拉） |
| ⑤ 允许必要依赖 + IOC 容器 | R-17 零第三方依赖 | **替换**技术约束（允许官方包 + 正式 IOC） | R-17'（替换 R-17）、R-23（新增 IOC 架构约束） |

**关键架构结论**：v1 的"零第三方依赖"硬约束被放宽为"允许必要的微软官方轻量包"——仅 `Microsoft.Data.Sqlite` 与 `Microsoft.Extensions.DependencyInjection`；**仍禁止任何社区第三方包**（不引入 CommunityToolkit.Mvvm、MahApps、Hardcodet.NotifyIcon 等）。自写 MVVM 基类、XAML 矢量桌宠、P/Invoke 锁屏/空闲检测、WinForms NotifyIcon 均保持不变。

**增量需求池编号**：R-22（热键）、R-23（IOC）、R-24（时间下拉）、R-25（SQLite）、R-26（点击互动改 R-01）、R-27（txt 汇报改 R-12/13/19）、R-28（设置重构改 R-16）、R-29（技术约束替换 R-17）。

---

## 二、各变更域详述

### 变更域 ①：本地数据库改用 SQLite（R-25，替换 R-14 底层）

**变更说明**
将 `Services/StorageService.cs`（v1 用 `System.Text.Json` 读写 `%LOCALAPPDATA%/WorkAlarmClock/data/YYYY-MM-DD.json` 与 `settings.json`）替换为 SQLite 存储，使用 `Microsoft.Data.Sqlite`（微软官方轻量包）。v1 数据模型全部落入数据库：
- `AppSettings`（设置）—— 落到库的 `settings` 表；
- `DayRecord`（含 `StateTransition` 流水、`WorkTaskItem`、`ReportRecord`、`ReminderState`）—— 落到 `day_records` / `state_transitions` / `work_tasks` / `report_records` / `reminder_states` 表（见下表）。
- `ReplayCalculator` 重算逻辑**保持**，仅数据源由 JSON 文件改为 `state_transitions` 表按时间升序读取。

**表结构设计（SQLite，字段表，非代码）**

| 表名 | 列 | 类型/约束 | 说明 |
|---|---|---|---|
| settings | key / value | TEXT PK / TEXT | 单行 key='app'，value 存 AppSettings 的 JSON（用 System.Text.Json 序列化）；字段增减无需 ALTER |
| schema_version | version | INTEGER NOT NULL | 单行，当前 = 1，供后续迁移 |
| day_records | date(PK) / manual_ended | TEXT / INTEGER 默认0 | 当日是否手动结束 |
| state_transitions | id(PK AUTOINCR) / date / time / from_state / to_state / source / note | TEXT(索引 date,time) | 只追加流水；time 存 ISO8601 "O"，字典序即时间序 |
| work_tasks | id(PK) / date / timestamp / text | TEXT(索引 date) | 今日事项；id 用 Guid |
| report_records | id(PK AUTOINCR) / date / generated_at / file_path / tomorrow_plan / risks | TEXT(索引 date) | 每次生成日报落一条 |
| reminder_states | date(PK) / weak_fired / weak_ignored / weak_deferred_until / strong_fired / strong_confirmed / strong_repeat_count | INTEGER/TEXT | 弱/强提醒触发状态持久化（跨重启不重复轰炸） |

**验收标准**
1. 首启自动建表（`CREATE TABLE IF NOT EXISTS` 幂等）；`PRAGMA journal_mode=WAL` 开启。
2. 每次业务写入走事务（`BEGIN/COMMIT`），强杀进程重启后当日流水无重复、时长与 v1 一致（Replay 幂等）。
3. 跨天启动：按 `date` 分区；切换日期上下文即"归档"（数据始终在库内，不再搬文件）；提醒状态重置。
4. 统计 `LoadWeek` 从 `state_transitions` 重放，与当日 Replay 算法同源（同 v1 CHECKLIST R-18 标准）。
5. 启动落位：当日无 `state_transitions` 则插入初始落位行（Source=Schedule）；六场景落位标准不变。

**对旧代码/旧数据的影响与迁移策略**
- 旧代码：`IStorageService` 接口（v1 §5）的 `LoadDay/SaveDay/LoadSettings/SaveSettings/LoadWeek/EnsureArchive` 实现整体改写；`DayRecord`、`WorkTaskItem` 等模型类保留作内存模型，不再担任序列化根（改由 SQL 读写映射）；`ReminderState` 由"内存态"升级为"库表持久化"（新增能力，修复 v1 重启重复提醒问题）。
- 旧 JSON 数据迁移（**默认建议：自动一次性迁移，不丢旧数据**）：首次以 SQLite 启动时，扫描 `%LOCALAPPDATA%/WorkAlarmClock/data/*.json` 与 `settings.json`，解析为内存模型后写入 SQLite；成功后把旧 json 移入 `data_legacy_backup/` 子目录**保留（不删除）**并写迁移标记（`schema_version` 旁置 `migration_done=1`）。迁移失败则记录 ERROR 日志、保留旧 json 不动、SQLite 以新库运行（历史丢失，但旧 json 仍在供手动处理）。**是否自动迁移列为待确认问题。**
- `%LOCALAPPDATA%/WorkAlarmClock/` 目录保留（db 文件 `workalarm.db` 亦置于此，与 v1 logs 同在）。

---

### 变更域 ②：取消"点击桌宠消失"，改全局热键 + 点击互动（R-26 改 R-01 / R-22 新增）

**变更说明**
- **移除** v1 中"单击/点击桌宠使其消失/隐藏"的行为（v1 表现为点击收缩隐藏等交互）。
- **新增**单击桌宠的互动反馈：播放一段互动 Storyboard 动画（角色跳一下 / 眨眼）+ 气泡"嗨~"，桌宠始终可见、不隐藏。
- **保留**双击桌宠打开主面板（v1 R-01 双击行为不变）。
- **新增全局热键模块**：通过 `SetWindowsHookEx(WH_KEYBOARD_LL, …)` P/Invoke（user32.dll）监听低层键盘事件，在 WPF UI 线程（利用既有消息泵，不新建消息循环）回调判定组合键；用户可在设置中自定义每个功能键/组合键；触发功能共 5 项：开始工作、开始休息、结束当天、生成汇报、显示/隐藏桌宠。

**验收标准**
1. R-26：单击桌宠任意位置播放互动动画 + 气泡"嗨~"，桌宠保持可见；**不存在**任何"点击→隐藏"路径；双击仍打开主面板。
2. R-22：全局钩子下，5 项功能在任意前台窗口下均可由热键触发；热键响应 ≤200ms，不卡 UI 线程；应用退出（`App.Exit`）时 `UnhookWindowsHookEx` 卸载钩子。
3. 设置面板可自定义 5 个功能的组合键（如 `Ctrl+Alt+W` = 开始工作）；组内不可重复绑定；至少含一个修饰键（禁止裸主键占全局）。
4. "显示/隐藏桌宠"热键可切换 `PetWindow` 可见性；托盘菜单亦提供等效项。
5. 钩子注册/卸载与 §IOC 生命周期一致，无句柄泄漏。

**对旧代码/旧数据的影响与迁移策略**
- 旧代码：`PetWindow` 单击事件处理从"切换隐藏"改为"播放互动动画"；新增 `Services/HotkeyService.cs`（实现 IDisposable，持有钩子句柄）与 P/Invoke 声明（`SetWindowsHookEx`/`UnhookWindowsHookEx`/`CallNextHookEx`/`LowLevelKeyboardProc`/KBDLLHOOKSTRUCT）。
- 旧数据：`AppSettings` 新增 `HotkeyBindings`（5 个字段，存 VirtualKeyCode+Modifiers 或 `"Ctrl+Alt+W"` 字符串）；旧 settings.json（无此字段）加载时以默认值（见默认建议）补全，向后兼容。
- 默认热键建议（未反对即启用）：开始工作 `Ctrl+Alt+W`、开始休息 `Ctrl+Alt+R`、结束当天 `Ctrl+Alt+E`、生成汇报 `Ctrl+Alt+G`、显示/隐藏桌宠 `Ctrl+Alt+S`。

---

### 变更域 ③：工作汇报导出 Markdown → 纯 txt，栏位重排（R-27 改 R-12/R-13/R-19）

**变更说明**
- 输出文件由 `.md` 改为 `.txt`，规避 Markdown 特殊字符（如 `*`、`#`、反引号）导致的渲染/粘贴错乱。
- **固定 6 栏顺序**（不再自由模板）：① 工作和休息时间 ② 日期 ③ 工作汇报 ④ 今日工作 ⑤ 明日计划 ⑥ 问题和分析。
- 栏位映射：① = 工作时间段/时长 + 休息时长 + 午休段（整合 `DayRecord` Replay 结果）；② = 日期；③ = **"工作汇报："标题 + 空行占位**（仅占位，正文由用户事后补充，**不新增"今日总结"输入框**）；④ = 今日工作事项 `Tasks` 列表（带 HH:mm）；⑤ = 明日计划；⑥ = 问题和分析（即原 `Risks`）。
- 文件名/目录策略沿用 v1：可配置目录，默认 `文档/WorkAlarmClock/Reports/YYYY-MM-DD.txt`。
- 预览面板（原 R-19）改为纯文本预览，可编辑"明日计划 / 问题和分析"两栏后重新生成（同 v1 R-19 的可编辑区）。

**验收标准**
1. 生成落盘 `.txt`；内容按①–⑥固定顺序；第三栏为"工作汇报："标题后接空行，正文为空。
2. 各栏内容映射正确：①含"工作 Xh Ym / 休息 Xh Ym / 午休 HH:mm-HH:mm"；④为编号+HH:mm 事项列表（空则占位"无"）；⑤⑥取自可编辑区（默认空）。
3. 默认路径 `YYYY-MM-DD.txt`；目录自动创建；"复制到剪贴板""打开目录"保留（同 v1 R-13）。
4. 预览面板为纯文本，所见即落盘内容；编辑⑤⑥后重新生成落盘一致。
5. **不再生成 `.bak`**；同日重复生成直接覆盖（覆盖/保留策略见待确认）。

**对旧代码/旧数据的影响与迁移策略**
- 旧代码：`IReportService.RenderMarkdown` 改为 `RenderText`（6 栏固定）；移除 7 个占位符模板与"模板多行编辑"入口（R-12 自定义占位符能力降级）；`ReportRecord` 的 `FilePath` 后缀 `.txt`。
- 旧数据：v1 旧 `.md` 日报文件保留在 `Reports/`（不强制转换）；新生成均为 `.txt`。设置中"模板编辑"入口移除或改为可选"开场白/页脚"单行文本（默认建议：移除自由模板编辑，仅保留可选页脚）。

---

### 变更域 ④：设置页布局优化 + 时间选择改下拉框（R-28 改 R-16 / R-24 新增时间下拉）

**变更说明**
- 重构 `Views/SettingsWindow.xaml`：分组更清晰（作息 / 提醒 / 检测 / 外观 / 存储 / 热键-新增），视觉更紧凑现代；保留草稿-应用两段式与校验逻辑（v1 §9.2 校验规则 8 条保持）。
- **所有时间输入**（上班 / 下班 / 午休起止）由文本框改为下拉框（ComboBox），零依赖自实现：列出 `HH:mm` 选项，小时 0–23，分钟粒度默认 **30 分钟**（00/30），可扩展 15 分钟粒度。
- 新增"热键"分组（承载变更域②的 5 个绑定）。

**验收标准**
1. R-24：全部时间选择均为 ComboBox（无文本框时间输入）；鼠标/键盘可选中；分钟粒度默认 30 分钟；输入受控（不出现非法时间字符串）。
2. R-28：设置页分组清晰、紧凑；原有全部设置项（含透明度滑条即时生效、汇报目录浏览、开机自启、声音、空闲阈值、工作日等）保留可用；时间区间校验（下班>上班、午休区间合法）仍生效（下拉框天然限制格式，区间关系仍查）。
3. 非法组合（如午休早于上班）保存时顶部红字提示且禁用保存（同 v1）。

**对旧代码/旧数据的影响与迁移策略**
- 旧代码：`SettingsWindow.xaml` 重写；新增时间 ComboBox 控件（UserControl 或内联），绑定 `TimeOnly`；移除时间 `TextBox` 及其校验转换。
- 旧数据：settings.json 中时间仍存 `TimeOnly` 字符串（如 "09:00"），下拉框反序列化为选中项；过渡期若旧值粒度非 30 分钟（用户曾手填 09:41），下拉框需能"接受非标准值并回显"（默认建议：加载时把非档位值吸附到最近档位，或允许下拉含该精确值）——列为细节可由架构师定，默认建议"吸附最近 30 分钟档"。

---

### 变更域 ⑤：允许必要依赖 + IOC 容器（R-17' 替换 R-17 / R-23 新增）

**变更说明**
- v1 技术约束"零第三方依赖"**由 R-17' 替换为"允许必要依赖 + 使用 IOC"**：仅引入两个微软官方包 `Microsoft.Data.Sqlite`、`Microsoft.Extensions.DependencyInjection`；其余（MVVM 基类、托盘、P/Invoke、XAML 矢量）维持自写/框架自带。
- **新增 IOC 架构约束（R-23）**：用 `ServiceCollection` + `ServiceProvider` 重构 `Services/AppServices.cs`（v1 的"静态属性 + 工厂方法服务定位器"），改为"容器构建 + 服务解析"；所有无状态/全应用唯一服务以 **Singleton** 注册；重点控制内存：事件订阅随服务 Dispose 可控释放、托盘图标/句柄在 Dispose 释放、`state_transitions` 等集合天然按天有界、DI 容器**不持有 View/ViewModel**。

**验收标准**
1. R-17'：`dotnet list package` 仅含 `Microsoft.Data.Sqlite` 与 `Microsoft.Extensions.DependencyInjection`（及框架自带共享框架程序集）；**无任何社区第三方包**；`dotnet build` 0 警告 0 错误；自写 MVVM 基类保留。
2. R-23：`AppServices` 改为 `BuildServiceProvider()` 后通过 `IServiceProvider.GetRequiredService<T>()` 解析；服务注册集中在 `ConfigureServices`；退出时 `ServiceProvider.Dispose()`。
3. 内存控制：托盘 `NotificationService`、空闲监视等实现 `IDisposable`，退出时 `Dispose` 释放 NotifyIcon/DestroyIcon/退订 `SystemEvents` 与 `ClockTicker`；集合无无界增长（内存仅保留当日+必要窗口）；View/ViewModel 不进容器。
4. 与 v1 §9.3 "ViewModel 不入容器"约定一致；热键/SQLite 服务经容器解析。

**对旧代码/旧数据的影响与迁移策略**
- 旧代码：`AppServices` 整体重构（T05 任务项）；所有 `AppServices.Xxx` 静态访问点改为经 `IServiceProvider` 解析；服务类添加接口（若尚未有）以便容器注册。
- 旧数据：无数据层影响（设置仍由 SQLite 的 `settings` 表承载，迁移见变更域①）。

---

## 三、增量需求池（R-22 ~ R-29）

| ID | v1 关联 | 类型 | 优先级 | 验收标准摘要 |
|---|---|---|---|---|
| R-22 | — | 新增 | P0 | 全局 WH_KEYBOARD_LL 钩子 + 设置自定义 5 功能组合键（开始工作/开始休息/结束当天/生成汇报/显示隐藏桌宠）；不卡 UI；退出卸载；冲突提示 |
| R-23 | R-17 部分 | 新增 | P0 | ServiceCollection/ServiceProvider 重构 AppServices；服务正确生命周期（多数单例）；事件/句柄可控释放；容器不持有视图 |
| R-24 | R-16 部分 | 新增 | P0 | 时间选择全为 ComboBox（零依赖自实现，小时 0–23，分钟默认 30 粒度）；校验保留 |
| R-25 | R-14 底层 | 新增(替换) | P0 | SQLite 表结构（settings/day_records/state_transitions/work_tasks/report_records/reminder_states/schema_version）；事务原子写 + WAL；建表/迁移幂等；ReplayCalculator 保持；旧 JSON 一次性迁移归档 |
| R-26 | R-01 部分 | 修改 | P0 | 单击桌宠播放互动动画 + 气泡"嗨~"，不再隐藏；双击开主面板保留 |
| R-27 | R-12/R-13/R-19 | 修改(替换) | P0 | 导出 `.txt`；固定 6 栏顺序；③"工作汇报:"占位留空；映射正确；无 `.bak`；预览改纯文本可编辑⑤⑥ |
| R-28 | R-16 | 修改 | P0 | 设置页重构分组紧凑；时间改下拉（R-24）；校验/全部设置项保留；新增热键分组 |
| R-29 | R-17 | 替换 | P0 | 技术约束由"零依赖"改为"允许官方必要包（Sqlite + DI）+ 正式 IOC"；禁止社区第三方包；自写 MVVM/矢量桌宠保留 |

> 所有 P0；本增量无 P1/P2 项（原 P1 R-19 的预览改造并入 R-27 仍为 P0）。

---

## 四、待确认问题（附默认建议）

1. **热键冲突时谁优先？**
   默认建议：本应用用全局 LL 钩子自身不抢占系统热键（可与其他全局热键软件共存）；仅做"组内不可重复绑定 + 至少一修饰键"校验；若用户设定的组合与常见系统键（如 Win 组合）冲突则提示改键。**不主动检测/拦截其他程序冲突**，避免误伤。

2. **SQLite 文件损坏如何恢复？**
   默认建议：启动时检测 `database disk image is malformed` 等 → 把损坏库备份为 `workalarm.db.corrupt` → 新建空库重建表继续运行；当日数据可能丢失，历史在 `.corrupt` 中可手动用 `sqlite3` 导出恢复。不内置自动修复（个人工具成本收益低）。

3. **旧 JSON 数据是否自动迁移？**
   默认建议：**自动一次性迁移** + 旧 json 归档 `data_legacy_backup/`（不删除）；迁移失败记日志、保留原 json、SQLite 新库运行（历史丢失但旧文件可手动处理）。备选：只读兼容（StorageService 双读，复杂度高，不推荐）。

4. **同日重复生成 txt 是否保留 `.bak`？**
   默认建议：**不再保留**（用户明确"不再 .md/.bak 那套"），同日直接覆盖写 `.txt`。若担心误覆盖，备选"保留上一版为 `.prev`"——需用户确认才启用。

5. **汇报"模板自定义"能力是否完全移除？**
   默认建议：固定 6 栏 txt，**移除自由占位符模板编辑入口**；仅保留可选"页脚/开场白"单行文本（默认空）。若用户希望保留部分可定制，则降级为"仅栏目标题文字可改"。

---

## 五、对 v1 文档的修订指引（供架构师/工程师/QA 参考）

- **ARCHITECTURE.md §4 数据模型**：`DayRecord` 等仍为内存模型；新增"SQLite 表结构"小节（见本文件变更域①表）；`ReminderState` 由内存态升级为持久化表。
- **ARCHITECTURE.md §5 接口**：`IStorageService` 实现改写（SQLite）；`IReportService.RenderMarkdown` 改 `RenderText`；新增 `IHotkeyService`。
- **ARCHITECTURE.md §9 共享约定**：JSON 约定（§9.7 等）降级为"settings 表的 value 列用 JSON"；"零 NuGet 清单"（§10）改为"允许官方必要包清单（Sqlite + DI）"。
- **R-17 约束**：在 ARCHITECTURE/CHECKLIST 中由"零第三方依赖"更新为 R-29（允许官方包 + IOC）。
- **CHECKLIST.md**：v1 全绿项保持不变；新增 R-22~R-29 增量验收段（含：db 首启建表 + 旧 json 迁移、热键全局触发 + 退出卸载、txt 6 栏顺序、时间全下拉、IOC 容器构建 + 内存释放验证）。

---
*本增量 PRD 仅描述相对 v1 的变更；未提及项一律沿用 v1 PRD/ARCHITECTURE/CHECKLIST。不产出任何代码。*
