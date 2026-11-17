# AutoCAD 2024 开发与加载

本轮用户已指定 CAD 2024，当前按 **AutoCAD 2024 Windows x64** 建立开发目标。对应 API R24.3、插件目标 .NET Framework 4.8；本机具体补丁和产品名称由 `GYENV` 读取。Linux 工作区可编译 DLL，真实 CAD 加载必须在 Windows 宿主执行。

当前 v0.2.1 包含三模式核心与 31 个 CAD 命令，使用流程见源码中的 `docs/09_USER_GUIDE.md`（安装包内为 `USAGE.md`）。已通过纯代码测试和 API 编译，但没有在 Windows CAD 内执行加载、图块视觉比对、拖动或撤销验收。

## 工具与依赖

| 用途 | 配置 |
| --- | --- |
| 编译器 | .NET SDK 10.0.404，见根目录 global.json；只用于构建，不是插件运行时 |
| 插件运行时 | AutoCAD 2024 自带/要求的 .NET Framework 4.8 |
| CAD 编译引用 | Autodesk 发布的 AutoCAD.NET 24.3.0（传递 Core、Model 同版本） |
| Framework 编译引用 | Microsoft.NETFramework.ReferenceAssemblies 1.0.3 |
| 几何布尔与连通性 | NetTopologySuite 2.6.0，netstandard2.0 核心 |
| 图纸状态序列化 | Newtonsoft.Json 13.0.4；不启用 TypeNameHandling |
| Python 参考计算及检查 | Python 3.10+，仅标准库，无 pip 依赖 |
| 依赖锁 | src/Gengyi.Cad.AutoCAD2024/packages.lock.json；日常恢复使用 --locked-mode |

从 [Microsoft 官方下载页](https://dotnet.microsoft.com/download/dotnet/10.0)安装 global.json 指定的 SDK。已有 SDK 可直接使用；仓库内 `.dotnet/dotnet` 也会被开发脚本优先识别。SDK 并不替代 Windows 上的 .NET Framework 或 CAD 安装。所有 Python 环境、NuGet 缓存与构建产物均留在当前仓库的忽略目录中，不需要全局 pip 安装。

## 在仓库根目录执行

```text
python tools/dev.py setup
python tools/dev.py check
python tools/dev.py doctor
python tools/dev.py build
python tools/dev.py package
```

Windows 可将 `python` 替换为 `py -3`；不需要激活虚拟环境或调整 PowerShell 执行策略。`setup` 建立 `.venv`，后续也可使用 Windows 的 `.venv\Scripts\python.exe` 或 Linux 的 `.venv/bin/python` 执行相同命令。

`check` 运行原有 Python 参考测试、C# 核心回归、解析项目 JSON，并核对 10 张原图的 SHA256 和字节数。安装 `tools/requirements-checks.txt` 后还会用 JSON Schema 检查三模式导出；CI 安装这一检查依赖。参考计算本身仍为零依赖，且可直接运行：

```text
python tools/reference_calculations.py --people 600 --reserve 0
```

`build` 使用锁定依赖生成 `src/Gengyi.Cad.AutoCAD2024/bin/Release/net48/Gengyi.Cad.AutoCAD2024.dll`。首次配置时锁文件已生成，日常不自动更新依赖。确需升级时先修改项目版本、执行 `dotnet restore src/Gengyi.Cad.AutoCAD2024/Gengyi.Cad.AutoCAD2024.csproj --force-evaluate`，检查锁文件差异，再重新验证。

`package` 先构建，输出 `artifacts/Gengyi.AutoCAD2024.zip`。压缩包包含 `Gengyi.bundle`、本项目与必要运行时 DLL、清单、指南、实测报告及第三方许可，不包含 Autodesk API DLL、公司设备块或用户图纸。必须保留全部运行时 DLL，不能只拷贝主 DLL。更新已加载的 DLL 后需重新启动 CAD。

## 在 Windows AutoCAD 2024 验证

1. 解压插件包。选择固定的本地开发目录保存 `Gengyi.bundle`，例如 `C:\CADDev\Gengyi.bundle`。
2. 保持 CAD 正常安全加载机制。在 AutoCAD 的“选项 → 文件 → 可信任的位置”中配置该插件 DLL 所在的开发目录，按组织既有规则加载。脚本不更改 `SECURELOAD`、`TRUSTEDPATHS` 或系统策略。
3. 打开测试图纸，执行 `NETLOAD`，选择 `Gengyi.bundle\Contents\Windows\Gengyi.Cad.AutoCAD2024.dll`。
4. 执行 `GYENV`，在命令历史（F2）查看产品、ACADVER、VERNUM 补丁构建、平台、CLR、API、插件版本、INSUNITS 和 UCS→WCS 矩阵。预期主版本为 24.3，进程为 64 位。CLR 的版本号不代表 .NET Framework 的补丁号。
5. 记录输出及实际加载结果到本地忽略目录 `local_private/`。宿主验证需要确认命令执行成功，且图元、单位、UCS 和安全加载设置没有被修改。
6. 执行 `GYSETUP`，按 `USAGE.md` 完成三个模式、房间切换、取消、一次撤销和保存重开的实际验收。`GYENV` 是只读检查，后续布置命令才会修改授权房间的块和项目数据。

持久安装时将完整 `Gengyi.bundle` 放到 `%APPDATA%\Autodesk\ApplicationPlugins\`。v0.2.1 清单只声明 Windows x64/R24.3，显式启用启动加载，并保留全部31个命令的按需加载后备；不在复制目录期间即时加载。启动加载不自动打开面板。自动发现及启动加载仍待真实宿主核查，开发调试也可使用上述明确的 `NETLOAD` 步骤。

`GYENV` 是只读诊断：不会生成块、扫描公司图纸、修改图层或将图纸上传。单位未设置或不是毫米时仅提示后续布置需要单位转换/校准，不自动改图纸单位。

## 当前实现与验证边界

`src/Gengyi.Core` 使用毫米与独立轮廓，完成容量、三模式候选、碰撞、边距、柜链连续性、封闭、人员宽度与入口可达检查。`src/Gengyi.Cad.AutoCAD2024` 负责真实块、侧栏、UCS/单位、临时绘图、事务、图纸扩展字典、原生编辑后同步。`tests/Gengyi.Core.Checks` 可在 Linux 直接执行，完全不引用 CAD DLL。纯核心检查不能替代真实宿主操作验收。

搜索是受时间/候选数量预算约束的模板枚举和障碍位置搜索，不是全局最优化证明；平移副本不算不同方案。R600 重建采用真实原生圆弧，几何核心使用 64 段弦逼近，最大弦误差约 0.046 mm；普通弧形输入按 0.05 mm 弦误差采样，人员缓冲使用 32 段/象限。微米级浮点误差与安装缝分别处理，10 mm 开口有回归用例。

默认 1000 mm 预排通道、600 mm 人员宽度、100 mm 架段间距是可编辑规划假设，不是厂家/规范验证值。一更二更不会继承鞋柜 1000 mm 边距。实际门扇/更衣动作和吊挂条件须在参数面板显式设置、核对后才能得到有效容量；勾选确认代表用户现场核对，不是软件自动证明人体工效或结构承载。

原始 DWG/DXF 块仍不在仓库中。`GYBLOCK` 在当前图中标定原块，`GYIMPORT` 从用户选定文件隔离导入；来源快照哈希只在本地计算。`GYREBUILD` 明确标记图片重建，R600 默认容量为 0。原图块样式是否一致只能在宿主内比对，不能由编译或截图摘要证明。

## 官方依据

- [Autodesk：AutoCAD 2024 Managed .NET 兼容表](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm)：24.3、Framework 4.8 及安全加载要求。
- [Autodesk：AutoCAD.NET 24.3.0](https://www.nuget.org/packages/AutoCAD.NET/24.3.0)：2024 托管 API 编译引用；不随本插件重新打包。

本次实际结果见 [environment-report.md](environment-report.md)。
