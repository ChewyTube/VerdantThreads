# VerdantThreads 文档索引

> 整理日期：2026-08-11　|　命名规范：全部 `UPPER_SNAKE_CASE.md`（活跃文档无日期后缀，归档文档带日期或 `_OLD`/`_HISTORY`）。
> 分工：`docs/` 根 = 项目认知；`design/` = 设计依据；`status/` = 当前任务；`archive/` = 已完成/过期的历史记录。

## 项目认知（docs/）

| 文档 | 说明 |
|------|------|
| [PROJECT_UNDERSTANDING.md](PROJECT_UNDERSTANDING.md) | **项目认知总览**——基于全部源码逐文件阅读整理的代码现状（架构/数据模型/管线/存档/坑位），与 GDD（目标）互补 |

## 设计依据（design/）

| 文档 | 说明 |
|------|------|
| [GAME_DESIGN.md](design/GAME_DESIGN.md) | 游戏设计文档（GDD）——**项目最高优先级设计依据**。孟德尔遗传 → DNA 的科技探索主线、豌豆 7 对性状、核心循环 |
| [PEA_RENDERING.md](design/PEA_RENDERING.md) | 豌豆渲染方案（十字面片 vs 程序化几何，决策已定案，批1 已落地） |
| [TAG_SYSTEM.md](design/TAG_SYSTEM.md) | 批2 豌豆记录与标签系统设计定案（双重标签/采摘/Genome/背包存档） |

## 当前任务（status/）

| 文档 | 说明 |
|------|------|
| [TODO_LIST.md](status/TODO_LIST.md) | **当前任务列表**——已完成项 + 基因系统路线图（Step 0~3）+ 批2（2a~2i）+ 待办 |

## 归档（archive/，已完成/过期的历史记录）

| 文档 | 说明 |
|------|------|
| [REVIEW.md](archive/REVIEW.md) | 代码审查报告（P0-P3 清单 + 修改计划，阶段一~三全部完成） |
| [VIEW_DISTANCE_PLAN.md](archive/VIEW_DISTANCE_PLAN.md) | 视距提升计划（lineOfSight 6→12，①~⑦ 全部落地） |
| [PLAN_2026-08-10.md](archive/PLAN_2026-08-10.md) | 2026-08-10 重构工作单（已全部完成） |
| [TODO_HISTORY.md](archive/TODO_HISTORY.md) | 历史任务总表（视距/审查/重构项全绿记录，内容已全部完成） |
| [PROJECT_STATUS_2026-08-09.md](archive/PROJECT_STATUS_2026-08-09.md) | 2026-08-09 项目状态快照（过期，最新认知见 PROJECT_UNDERSTANDING.md） |
| [AGENTS_OLD.md](archive/AGENTS_OLD.md) | AGENTS.md 旧版备份（内容过时，工具链约定以根目录 AGENTS.md 为准） |

## 根目录（保留原位）

- `AGENTS.md` — 编辑器/工具链约定、代码布局、坑位清单（工具链自动加载，必须留在根目录）
- `README.md` — 仓库入口（指向本文档索引）
