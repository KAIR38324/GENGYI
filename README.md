# GENGYI｜CAD 更衣设施一键布置插件开发交接

> **最新开发入口（2026-11-17）**：已决定转为云端开发，CAD验收在用户本地进行。保持一个轻量插件，**使用按需弹窗，不做侧边栏，须与天正建筑共存**。先读 [云端交接与已确认决策](docs/CLOUD_DEVELOPMENT_HANDOFF.md)、[云端启动任务](prompts/CLOUD_CODEX_START.md)、[竞品调研](docs/RESEARCH_LAYOUT_TOOLS.md)及[本机环境记录](docs/ENVIRONMENT_REPORT.md)。这些最新决定覆盖下文旧交接中冲突的界面、候选和保存建议。

首版完整支持 **换鞋间、一更、二更三种房间模式**，可在男女子房间间切换。依据原方案分别布置 S 形双面鞋柜、12门个人衣柜和工装衣架，支持人数估算、矩形内多方案、默认正交拖动及各房间独立实时统计。

**本仓库当前交付的是原始证据、工程规则、参考计算器和本地 Codex 开发任务；尚不是已完成的 CAD 插件。** 原方案 10 张图片已按原字节归档。尚无用户原始 DWG/DXF 块定义；本地开发时可直接在用户 CAD 中导入原块，算法与交互开发无需因此停工。

## 本地 Codex 从这里开始

把下列文字交给已打开本仓库的本地 Codex：

> 请先读 `AGENTS.md`、`prompts/LOCAL_CODEX_START.md` 和 `docs/00_REQUIREMENTS.md`，查看 `references/original_images` 的原始图片，再按交接任务开发可在我的 CAD 中实际使用的更衣柜一键布置插件。先识别本机 CAD 品牌、完整版本和可用 SDK；已有规则不用重复询问。首版完成换鞋间、一更、二更的房间切换、各自自动排布、原图块接入、拖动和实时统计。仓库中的旧 F-R3 图纸是已被用户否定图块的反例，不得当作原图块库。持续开发、验证并报告真实完成状态。

完整任务：[LOCAL_CODEX_START.md](prompts/LOCAL_CODEX_START.md)。

## 已明确的容量与尺寸

| 组件 | 每侧列数 × 每柜层数 | 上下柜数量 | 两侧合计储格 | 人数 | 外形 L×D×H（mm） |
|---|---:|---:|---:|---:|---|
| S12 | 4×3 | 1 | 24 | 12 | 1000×600×600，长度为推算的定制模数 |
| S18 | 6×3 | 1 | 36 | 18 | 1500×600×600，长度为推算的定制模数 |
| S24 | 8×3 | 1 | 48 | 24 | 2000×600×600，用户与原参数共同确认 |
| S48 | 8×3 | 2 | 96 | 48 | 2000×600×1200，直接叠放的名义高度 |

每人占脏、净两格。**S24 两面合计 48 格只服务 24 人。** 单格名义分配空间为 `250×300×200 mm`；这是模数分配，不能作为内净尺寸或门板加工尺寸。原参数图的 `260×168 mm` 门尺寸与 250 mm 列距冲突，已列为待核项。

横排和竖向连接均可由鞋柜组成；连接柜本身在两侧可达、格口可用时计容。R600 四分之一弧形角件独立列项，默认不计储鞋容量。双层柜按 48 人存储计，但不能自动把 1200 mm 顶面当作原有 600 mm 跨坐座面。

## 核心设计要求

- 保留原图 S 形、横竖柜连接及圆弧转角的外观与组织方式。
- 其他边界柜距选定矩形边界 **1000 mm**；本仓库将其作为默认最小净距和优先贴合目标，不擅自套用于所有柜间净距。
- 脏净两侧不能绕端直通，转换应通过已定义的跨柜位置。仅看起来像 S 形不算验证通过。
- 四周留 1000 mm 的孤立 S 形柜阵可能绕行。必须结合真实端部封闭/固定边界与入口、出口种子检查；缺少信息时标“卫生分隔待核”，不能自动通过，也不擅加应急走道。
- 人数输入区分在册、当班、备用比例。当前项目为 **600 人在册、300 人/班、男女 1:1**。备用默认 0%，历史 10% 只作为可选参数。
- 不加淋浴，不添加妇卫等候、妇卫处理、洗衣附属间等用户未要求的功能。
- 输出真实块参照、清晰分层、模型空间标注；无多余填充、无额外纸空间出图布局。

## 文件导航

| 路径 | 用途 |
|---|---|
| [原始图片索引](references/ORIGINAL_IMAGES.md) | 10 张原图、设备参数和对应说明 |
| [00_REQUIREMENTS](docs/00_REQUIREMENTS.md) | 需求优先级、项目约束、待核项 |
| [01_SOURCE_AND_BLOCK_STYLE](docs/01_SOURCE_AND_BLOCK_STYLE.md) | 原图证据与图块复用要求 |
| [02_SHOE_GEOMETRY_AND_TOPOLOGY](docs/02_SHOE_GEOMETRY_AND_TOPOLOGY.md) | S 形连接、净距、不可绕端规则 |
| [03_CAPACITY_AND_AREA](docs/03_CAPACITY_AND_AREA.md) | 人数、柜数、面积及衣架计算 |
| [04_CAD_PLUGIN_ARCHITECTURE](docs/04_CAD_PLUGIN_ARCHITECTURE.md) | 本地 CAD 插件架构和交互 |
| [05_ACCEPTANCE_TESTS](docs/05_ACCEPTANCE_TESTS.md) | 可执行验收清单 |
| [06_LEGACY_REVIEW](docs/06_LEGACY_REVIEW.md) | 历次错误及回归依据 |
| [07_ROOM_MODES](docs/07_ROOM_MODES.md) | 首版三种房间模式、男女切换与独立状态 |
| [设备目录](data/equipment_catalog.json) | 机器可读参数及证据级别 |
| [项目默认值](data/project_defaults.json) | 最新人数、边界、交互及容量口径 |
| [房间模式](data/room_modes.json) | 三种算法、五个默认房间与独立参数 |
| [规则清单](data/rules.json) | 有编号的硬约束、假设与校核状态 |
| [参考计算器](tools/reference_calculations.py) | 纯 Python 零依赖模数/容量/估算参考 |
| [原块映射模板](data/block_mapping.template.json) | 本地原图块接入与来源状态 |
| [旧稿隔离区](references/legacy/README.md) | 历史几何和审核，只用于查错 |

## 运行参考计算

```bash
python tools/reference_calculations.py --people 600 --reserve 0
python -m unittest discover -s tests -v
```

计算器给出模块、柜数、名义储量及带条件的 S 形柜阵包络估算；**不声称已生成可用 CAD 排布，也不把柜体投影面积当房间面积**。真正的候选必须通过图块、门扇、两侧操作、卫生分隔和边界检查。

## 来源与状态

参数优先级：2026-11-17 用户最新说明 → 本项目其他已确认要求 → 原图与参数 → 有标识的推算 → 历史假设。图片哈希见 [image_manifest.json](references/image_manifest.json)。技术资料的官方链接见架构文档。原始图片保留上传字节，未重新生成或替换。
