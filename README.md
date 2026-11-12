<p align="center">
  <img src="docs/image_banner.jpg" alt="Annotrain" width="820">
</p>

# Annotrain

面向 **YOLOv8 / YOLO11 / RF-DETR** 的「标注 → 训练 → 检测验证」工作台，纯 C#，不依赖 Python 运行时。

> 定位：个人 / 小团队自用的模型迭代工具。
> 检测只用于验证训练出来的模型效果，**不作为对外推理服务，也不作为供其他系统集成的 SDK**。

仓库用**两个解决方案**分别管两条线，互不编译依赖，只共享 `Sevencat.Core`：

| 解决方案 | 产出 | 组成 |
| --- | --- | --- |
| `Sevencat.Label.sln` | 标注桌面应用 `Sevencat.Label.App.exe` | 业务 + 程序 + Web |
| `Sevencat.Train.sln` | 训练/验证桌面应用 `Sevencat.Train.exe` | 业务 + 引擎 + Web |

## 技术路线

- **纯 C# 训练**：TorchSharp（LibTorch）承担计算；训练循环、EMA、损失、数据增强、ONNX 导出全在进程内，不用 Python。
- **代码复用**，不重写：
  - 标注参考 `F:\code\TinyLabel\src`（画布 `AnnotationCanvas`、标注模型、原子写盘、Minimal API 服务）；
  - 训练参考 `F:\code\OmniTensorSharp`（YOLO/RF-DETR 网络、Trainer、导出、推理）。
  - 能复制的直接复制，只做命名空间改名与**存储层接口化**。
- **存储必须抽象**：YOLO / COCO / RF-DETR 的目录探测、标注读写、数据集读取全部收敛到接口；画布与训练器都不认识具体格式。
- **标注端与训练端共用一套存储**：图片、标注、描述文件都走同一个 `IBlobStore`（`file://` / 将来 `s3://`），
  除本地实现外代码里不出现 `File.*` / `Directory.*`。

支持的数据集形态（与后端正交，均可用 `file://` 或 `s3://`）：

| 形态 | 图片 | 标注 | 说明 |
| --- | --- | --- | --- |
| YOLO | `images/{split}` | `labels/{split}/*.txt` | `data.yaml` 描述 |
| COCO / RF-DETR | `train/valid/test` | `{split}/_annotations.coco.json` | 兼容 annotations/instances |
| TinyLabel 目录+DB | 项目目录 / 扫描根 | SQLite `annotations` 表 | DB 需本地，图片可远端；train/val 用 `images.Split` 字段 |

端侧边界：**标注端打开 `tinylabel`（图片目录 + DB）**；YOLO / RF-DETR 既可导出，也可**导入**成项目
（复制图片与标注，连 train/val 一起搬）。训练端三种形态都能读（只认 `IDatasetStoreProvider`）。
平铺目录与 Pascal VOC 本期不做。

三种形态的目录树、`data.yaml` / COCO json 字段、SQLite 表结构见 [MISC 5.1 三种形态的存储布局](docs/MISC.md)。

打开目录时自动探测格式，检测结果记在项目记录里（`Format` / `DescriptorPath`），下次直接打开。

## 导入现有数据集（YOLO / RF-DETR）

手上有现成的 YOLO / RF-DETR 数据集时，不必重新标：直接导进来继续改。

```powershell
# 标注程序里：主界面「从现有数据集导入」，选源目录 / 目标目录 / 项目名，点「开始导入」
# 或不开窗口走命令行：
Sevencat.Label.App.exe --import <源目录> --output <目标目录> [--name 项目名] [--format yolo|coco] [--split train,val]
```

| 源形态 | 识别依据 | 导入成什么 |
| --- | --- | --- |
| YOLO | 根目录 `data.yaml` 的 `train` / `val` / `test` | 每个 split 单独一个子目录，标签转 labelme JSON |
| RF-DETR (COCO) | `train/`、`valid/`、`test/` 下的 `_annotations.coco.json` | `valid` 归一成 `val`；多个 split 的类别取并集 |

产物在 `--output` 目录：`annotrain.db`（项目 / 类别 / 图片 / 标注）、`projects/{项目Id}/{split}/`（图片副本）、
`import-report.txt`（每个 split 的图数与告警）。源目录只读，导入目标不能与源相同。

导入完就是普通标注项目：界面会自动打开它，左侧按 split 列出全部图片（每行带尺寸与框数），
点一张就能看到图片和已有的框。之后可以继续标注、导出 YOLO / RF-DETR，或者直接给训练端按 `images.Split` 训练。
`--format` 只在探测同时命中多种形态时才需要。

打开一个已有项目（跳过导入）：

```powershell
Sevencat.Label.App.exe --open <项目目录>
```

## 模拟流程（不用准备数据）

不接真实数据也能把整条链路跑一遍：

```powershell
Sevencat.Train.App.exe --simulate %TEMP%\annotrain-sim-demo --count 20 --width 640 --height 480
```

| 步骤 | 做什么 | 产物（都在 `--simulate` 目录下） |
| --- | --- | --- |
| 1 生成 | 随机画正方形 / 三角形 / 圆形，**平铺输出且不带标注** | `flat/*.png` |
| 2 导入 + 标注 | 建数据库项目、写图片、把每个形状框出来（±2px 抖动模拟手标） | `dataset/annotrain.db`、`dataset/projects/1/*` |
| 3 训练（直接读库） | 训练程序按 `images.Split` 装载数据（当前是干跑：完整装载 + 校验 + 摘要） | `training-db/` |
| 4 导出 + 训练 | 导出 YOLO（`data.yaml` / `images/` / `labels/` / `classes.txt`），再从导出目录训练一遍 | `export-yolo/`、`training-yolo/` |
| 5 验证 | 用生成时的真值反查标注与导出结果 | `simulation-report.txt` |

退出码 `0` 表示验证通过；`2` 表示有缺失 / 多余 / 超容差。真实训练（YOLO / RF-DETR 引擎）在 M4 接入，
替换 `IDatasetTrainingRunner` 的实现即可，流程与验证都不用改。同一套流程也有单测：
`tests/Sevencat.Train.Engine.Tests/SimulationPipelineTests.cs`。

## 解决方案结构

```text
Sevencat.Label.sln
  src/Sevencat.Core/              跨解决方案契约：存储接口、公共模型、坐标与图像抽象、格式 codec
  src/Sevencat.Label/             标注业务：标注入库、编辑器逻辑、原子写盘、项目浏览与导入
  src/Sevencat.Label.App/         标注程序（exe）：画布、交互、ViewModel、窗口
  src/Sevencat.Label.Web/         标注 Web：Minimal API + 内嵌页面 + 数据集统计
  tests/Sevencat.Label.Tests/     格式读写、坐标换算、目录探测

Sevencat.Train.sln
  src/Sevencat.Core/              （同一份源码，两个解决方案都包含）
  src/Sevencat.Train.App/         训练程序（exe）：任务队列、参数、日志、检测验证页
  src/Sevencat.Train.Engine/      训练引擎：模型结构、数据管线、Trainer、导出、推理
  src/Sevencat.Train.Web/         训练 Web：任务列表、实时日志、指标
  tests/Sevencat.Train.Engine.Tests/  模型结构、损失、导出、数据集接口
```

命名约定：

- `Sevencat.<模块>` 是业务层。`Sevencat.Core`、`Sevencat.Train.Engine`、`Sevencat.Label` **不得引用 Avalonia**。
- 可执行程序统一叫 `Sevencat.<模块>.App`：`Sevencat.Label.App`、`Sevencat.Train.App`；它们可以引用 Avalonia。
- Web 项目都是类库，由各自程序进程内启动 HTTP 服务，随主程序启停，不单独部署。
- 检测（推理验证）不单独建项目：推理在 `Sevencat.Train.Engine`，页面在 `Sevencat.Train.App`。

## 快速开始

```powershell
# 标注（数据集浏览 / 标注）
dotnet build Sevencat.Label.sln
dotnet run --project src/Sevencat.Label.App

# 训练（训练 / 检测验证）
dotnet build Sevencat.Train.sln
dotnet run --project src/Sevencat.Train.App

# 测试
dotnet test Sevencat.Label.sln
dotnet test Sevencat.Train.sln
```

配置文件与日志位于 `%APPDATA%\Sevencat.Annotrain\`（`settings.json`、`logs\`）。

## 当前状态

M0（骨架）已完成：两个解决方案各 5 个项目，Avalonia 12.1.5 + Material.Avalonia 3.21.0 主题已接入，
DI / 配置 / 日志（NLog 按天落盘 + 界面日志面板）可用，两个桌面应用可启动。

M1（存储抽象）进行中：`IBlobStore`（本地后端 + 按 scheme 的注册表）、YOLO / LabelMe / COCO codec、
三种形态的格式探测与 Provider（yolo / coco / tinylabel）、统一打开入口 `DatasetStoreResolver` 已完成并有单测；
`tinylabel` 的 SQLite 读写（`TinyLabelDatasetStore` + `DatabaseAnnotationStore`，乐观锁 + 多项目选择）也已可用。
接下来是导出（DB → YOLO / RF-DETR）与项目表落库。

应用图标在 `assets/app.ico`（由 `docs/image_ico.png` 生成，含 16–256 多尺寸），两个 exe 与窗口都用它。

## 文档索引

- [需求](docs/REQUIREMENTS.md)
- [计划](docs/PLAN.md)
- [进度](docs/PROGRESS.md)
- [杂项](docs/MISC.md)

## 作者

<p align="center">
  <img src="docs/author.jpg" alt="作者" width="220">
</p>

## 许可

MIT，见 [LICENSE](LICENSE)。
