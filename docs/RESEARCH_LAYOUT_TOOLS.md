# 车位及工程设施自动布置工具调研

调研日期：2026-11-17。目的：为GENGYI借鉴成熟的参数输入、图面布置、人工调整和统计校核方式。依据为官方产品页、教程和帮助文档，未安装这些产品实测；页面声称的能力不等于已独立验证的算法、性能或兼容性。

## 1. 重点案例

| 案例 | 官方资料可支持的能力 | 适合借鉴 | 证据边界 |
| --- | --- | --- | --- |
| [ParkCAD / Transoft](https://www.transoftsolutions.com/civil-and-transportation/software/site-and-parking-design/parkcad/) | 在边界内生成车位与通道，禁布区、多布局优化、排端点/入口调整及统计 | 柜组作为可调整对象；候选比较；生成之后继续专业编辑 | 官方产品功能说明；未实测当前版本，也不能直接推导为任意原生命令都保留统计语义 |
| [TestFit Parking Solver](https://www.testfit.io/product/parking-solver) | 自动布局，手动编辑车位/通道/入口，实时数量与目标比，方案保存，DXF等导出 | 参数、图面和容量一起反馈；自动与手动衔接 | 产品页有说明；输出格式不证明其为AutoCAD内插件 |
| [浩辰CAD建筑车位布置](https://www.gstarcad.com/support/1357/) | 设置宽深、角度、排数/样式，点起终点生成直线排列，或选曲线及一侧布置；数量标注/编号 | 简短的CAD操作链，利用已有图元直接布置 | 教程证明批量绘图辅助；未证明整房优化、自动避柱、多候选或拖后全量重算；本轮未逐张读取其受限截图 |
| [C.A.T.S. Sprinkler](https://cats-software.com/sprinkler-3/) | 引用底图，按区域和设备参数自动布置；手动Copy/Move，编号、检查、碰撞与材料统计 | 设备库—工程规则—批量生成—人工编辑—校核—统计 | 使用自有工程对象；不能作为输出普通原生块的证明；未证实所有校核均在移动过程中实时完成 |
| [杉帝XCAD立体停车库](https://www.layoutcad.com/110/?cid=110) | 官方描述场地DWG+厂商产品库+布局算法生成二维与三维方案 | 先确定真实设备型号和产品库，再做排布 | 缺停车专用操作教程；柱网明确列为后续能力；分析/效率宣传不能全部当作已交付事实 |

## 2. 最直观的交互示例

[Autodesk：Adding parking to residential designs](https://www.autodesk.com/learn/ondemand/tutorial/adding-parking-to-residential-designs)提供了TestFit在Forma中的逐步图解：画区域 → 修改车位尺寸、通道宽、转弯半径 → 图面更新 → 拖动入口 → 调主轴/每排数量 → 右侧数量实时更新。

- [参数和图面截图](https://files.upskill-dev.autodesk.com/public/learn-one/forma/img/SPCMKR_M04U02_add_parking_006.webp)
- [图面和数量反馈截图](https://files.upskill-dev.autodesk.com/public/learn-one/forma/img/SPCMKR_M04U02_add_parking_008.webp)

该案例是Forma扩展，不能称作AutoCAD插件。其右侧面板只是调研证据；**用户随后明确GENGYI使用弹窗，不要侧边栏**，不能照抄该界面容器。

## 3. 对原生编辑的关键发现

[ParkCAD官方FAQ](https://helpdesk.transoftsolutions.com/hc/en-us/articles/235457508-Can-I-use-AutoCAD-tools-on-the-ParkCAD-elements-I-have-created)说明：用AutoCAD工具修改ParkCAD对象几何前需先炸开，炸开后ParkCAD不再识别。FAQ没有逐项说明MOVE/COPY/ERASE，且未标适用版本；不能扩大为“所有原生命令均不支持”。

结论：GENGYI的“原生块编辑后继续正确统计”需要自行设计和本地验收，不能凭竞品宣传当作已解决。建议数据库事件只标脏，安全时机重读图纸；复制、炸开、撤销重做都要有反例。

AutoCAD关联阵列可参考[官方交互示例](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DidYouKnow/files/GUID-11F6539E-DFA2-4DC9-8146-50C95799675C.htm)的整体参数与局部例外，但[BCOUNT官方说明](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-2C2991B4-779F-4FEE-9E55-8B0D6B62DEF8.htm)指出其不统计关联阵列内部块，不能直接拿BCOUNT充当插件容量统计。

## 4. 吸收后的项目结论

1. 保留“参数输入→图面约束→少量候选→柜组/单块调整→校核统计→确认落图”的短流程，容器采用轻量弹窗。
2. 图块外观、物理轮廓/操作面、容量规则分开保存。鞋柜型号、角件、衣柜、挂架来自有来源的设备模板，不任意缩放。
3. 候选显示有效容量、缺口、占用与问题，不把更密或超配当成更好方案。
4. 规则模板和有限尝试足以作为轻量首版的求解方式；复杂例外由设计人员调整。独立沿墙/沿线补摆入口暂缓。
5. 原生编辑、撤销、保存恢复与天正共存优先做可重复的本地验证；不以自定义对象或界面平台扩大架构。
6. 停车位计数不能代替更衣设施防绕端、脏净分侧、跨柜、开门与穿脱衣校核。这些仍按本仓库业务上下文实现。

另检索到Layoutio，但调研当日其官网列出的公开发布日期为2026-12-15，DXF仍属后续计划，未纳入成熟工具的主要依据。此类页面状态会变化，后续引用需重新核实。
