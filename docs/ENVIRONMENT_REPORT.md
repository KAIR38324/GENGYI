# 本机环境和验证状态

记录日期：2026-11-17。用途：给云端开发者明确目标和已有证据；不是要求云端复制整套CAD安装。

## 已实查的事实

| 项目 | 结果 |
| --- | --- |
| 目标平台 | Windows x64，AutoCAD 2024简体中文 |
| CAD版本 | `acad.exe` 文件版本 `R24.3.61.0.0` |
| 安装位置 | `D:\AUTOCAD\AutoCAD 2024\`；仅作本机配置参考，不硬编码为所有用户路径 |
| .NET目标 | AutoCAD2024使用.NET Framework4.8，见下方Autodesk官方兼容表 |
| 托管API引用 | 安装目录中 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll` 均存在，版本 `24.3.61.0.0` |
| 核心控制台 | `accoreconsole.exe` 存在；存在不等于已成功运行或完成许可/加载验证 |
| 编译工具 | VS2022 Build Tools的MSBuild和Roslyn `csc.exe` 存在 |
| 现代.NET SDK | `dotnet --info` 报告未安装SDK；已装若干.NET运行时，不能把运行时当SDK |
| net48引用程序集 | 标准Reference Assemblies目录未发现可用目标包；尚未安装/下载补齐 |
| Python | 本机Python3.13；原有零依赖测试可运行 |
| 天正建筑 | 用户要求与其共存；本轮未核实天正具体版本/加载配置，最终共存验证由用户本地执行 |

## 已执行和未执行

- 已运行 `python -m unittest discover -s tests -v`：16项通过、0项失败。
- 已运行 `python tools/reference_calculations.py --people 600 --reserve 0`；其输出是名义计算与带条件估算，不是已验证CAD方案。
- 已实际查看仓库10张原始图片；没有原始设备DWG/DXF块定义，用户确认目前只有图片资料。
- 实现启动后被用户切换云端开发而中断；本地只留下未完成的 `tests/DomainTests.cs` 草稿。本次云端交接不包含该草稿，不宣称新C#测试红绿已完成。
- 尚无生产C#插件、已编译DLL、设备核对样张、可安装包、CAD交互结果、撤销/保存重开结果或天正共存结果。
- 本轮没有更改CAD安全信任设置，没有把本机Autodesk程序集上传到仓库。

## 云端与本地的分工

云端负责源码、独立几何/容量测试、样张、构建脚本、在合法引用可用时的编译与打包，以及可执行的本地验收说明。保持一个轻量插件工程，独立测试不载入CAD DLL。

用户本地负责在AutoCAD2024与天正环境中加载插件，核对设备样式、弹窗焦点、点选/拖动、原生编辑同步、取消、撤销重做、保存重开及UCS/单位。没有本地实测的项目应标“待本地验收”，不阻止云端继续可执行的工作，也不伪报通过。

## API调查中应保留的未决事实

- 点输入返回坐标与当前UCS之间的关系不能仅凭一般坐标说明推断。调研中出现互相矛盾的解释，**尚未完成GetPoint/GetCorner的旋转UCS实测**；接手者应查官方具体API并给出本地测试，不能冻结未经证实的正向/逆向变换公式。
- Xrecord的普通文本DXF值与XData字符串编码不同；持久化应使用对应API支持的普通文本并合理分段，不能混淆两者。具体长度、Unicode往返、撤销行为仍需测试。
- 临时预览清理、无模式弹窗的CAD命令上下文、一次确认的撤销粒度、复制后的实例身份与数据库事件生命周期都需编译和宿主验证。不要把建议API名称当已运行证据。

## 官方技术依据

- [Autodesk AutoCAD2024 Application Compatibility](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)
- [Autodesk BlockReference](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockReference.html)（类型能力参考；编译以2024安装引用为准）
- [Autodesk Handle Object Events](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-E30279D1-E4B5-48A4-A3D8-9CEC83BD0967.htm)
- [Autodesk Lock and Unlock a Document](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-A2CD7540-69C5-4085-BCE8-2A8ACE16BFDD.htm)

后续实现应核对2024对应文档和实际引用程序集；不把较新版本示例直接当作2024兼容性证明。
