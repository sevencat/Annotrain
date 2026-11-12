# 开发计划

两条线各自独立编译：`Sevencat.Label.sln` 与 `Sevencat.Train.sln`，共享 `src/Sevencat.Core`。
原则：**先抽存储接口，再搬参考代码**；先让标注闭环可用，再上训练；Web 各自最后做。

## 里程碑总览

| 里程碑 | 主题 | 交付物 | 状态 |
| --- | --- | --- | --- |
| M0 | 骨架 | 两个 sln 可编译、可启动；Core 契约、DI、配置、日志、主题 | 完成（2026-11-12） |
| M1 | 存储抽象 | `IBlobStore` 后端 + Core 接口 + YOLO/COCO/LabelMe codec + 三种形态探测与 Provider + 单测 | 进行中 |
| M2 | 标注闭环 | 迁移 TinyLabel 画布与 VM，标注可读可存 | 未开始 |
| M3 | 标注 Web | 数据集浏览、标注查看、统计、导出 | 未开始 |
| M4 | 训练引擎 | 迁移 OmniTensorSharp 模型/训练/导出，数据管线接口化 | 未开始 |
| M5 | 训练桌面 | 任务队列、参数表单、日志与曲线、产物管理 | 未开始 |
| M6 | 检测验证 | 推理预测器、可视化、GT 对比、批量评估 | 未开始 |
| M7 | 训练 Web | 任务列表、SSE 日志、指标、产物下载 | 未开始 |
| M8 | 打磨 | 打包、性能、异常兜底、i18n | 未开始 |

## M0 骨架

任务：
1. 建两个解决方案与项目：
   - `Sevencat.Label.sln`：`Sevencat.Core`、`Sevencat.Label`、`Sevencat.Label.App`(exe)、`Sevencat.Label.Web`、`Sevencat.Label.Tests`
   - `Sevencat.Train.sln`：`Sevencat.Core`、`Sevencat.Train.App`(exe)、`Sevencat.Train.Engine`、`Sevencat.Train.Web`、`Sevencat.Train.Engine.Tests`
2. `Directory.Build.props` / `Directory.Packages.props`：`net10.0`、nullable、集中包版本（Avalonia 12.1.5、Material.Avalonia 最新、Sevencat.ImageFacade(.SkiaSharp) 最新、TorchSharp）。
3. 两个 exe 的 Avalonia 引导（PlatformDetect、主题、主窗口壳）+ Material 主题令牌。
4. DI 与配置（JSON，用户目录）+ 日志（文件 + 内存缓冲）。

验收：两个 sln 各自 `dotnet build` 通过，两个 exe 能启动空窗口，日志面板有输出。

## M1 存储抽象（两条线的地基）

任务：
1. **存储后端**：`IBlobStore` / `IBlobStoreFactory` / `StorageLocation` / `StorageKey` + `LocalBlobStore`
   （`AtomicFile` 收进来，实现 `Exists` / `Stat` / `OpenRead` / `OpenReadRange` / `Write` / `Delete` / `List`）。
   首版只实现 `file://`，`s3://` 留注册点。
2. 在 `Sevencat.Core` 落定模型：`AnnotationDocument`、`Shape`、`ImageRef`、`DatasetSample`、`DatasetProbe`、`ClassCatalog`（**直接搬 TinyLabel.Shared 的定义**，只改命名空间）。
3. 接口：`IAnnotationStore`、`IImageAnnotationCodec`、`IDatasetAnnotationCodec`、`IDatasetProbe`、`IDatasetStoreProvider`、`IDatasetStore`、`IDatasetSplit`；全部基于 `IBlobStore`，不出现路径读写。
4. 实现（标注侧）：`DatabaseAnnotationStore`（读写 SQLite，标注 JSON 存 `annotations.DocJson`）；`YoloTxtCodec`、`LabelmeCodec`、`CocoDatasetCodec` 放在 **Core**（`Annotation/Formats`、`Dataset/`），由 `AddAnnotrainDatasets()` 统一注册——导出与导入用同一批 codec。`VocXmlCodec` **本期不做**。
5. 实现（训练侧）：`YoloDatasetStoreProvider`、`CocoDatasetStoreProvider`、`TinyLabelDatasetStoreProvider`（图片目录 + SQLite，搬 TinyLabel.Server 的实体与查询，`images` 表加 `Split` 列），对外只暴露接口。标注端只打开 `tinylabel`；YOLO / COCO 做导出目标，也支持反向导入成项目（复制图片与标注、带上 train/val）。
6. 形态转换：`tinylabel` → YOLO / COCO（导出与交付必需）、YOLO ↔ COCO，统一走 codec，产出转换报告。
7. 格式探测：`IDatasetProbe` 实现，保持"只读文件头"的性能约束（远端只读前 8KB）；三种形态的特征判定 + 歧义候选 + 结果缓存。
8. 项目类型落库：`projects` 加 `Format` / `DescriptorPath` / `DetectedAt`，新建走自动检测、打开直接用库中值、提供重新检测。
9. 坐标换算工具（原图像素 ↔ 归一化 ↔ COCO xywh）+ 单测。

验收：
- 同一份数据用 YOLO / COCO / LabelMe 三种入口读进来，框误差 ≤ 1e-4；
- `DatasetLocator` 的 YOLO/COCO/歧义判定与原实现一致（原测试可搬）；
- 全局搜索 `Sevencat.Label` / `Sevencat.Train.Engine` 里没有 `File.` / `Directory.` 直接调用（只允许出现在 `LocalBlobStore`）。

## M2 标注闭环

任务：
1. 从 `TinyLabel.Annotation` 复制 `AnnotationCanvas.cs`、`LabelPalette.cs`，改命名空间接入 ImageFacade/SkiaSharp 后端。
2. 从 `TinyLabel.App` 复制 `MainWindowViewModel` 及其依赖的 ViewModels、Views、`Infrastructure/*`、`Services/*`；去掉远端模式（`RemoteWorkspaceService`）首版不接。
3. `AnnotationEditor`（撤销/重做）+ `AtomicFile`（原子写）原样迁移。
4. 图像加载：`ImageLoader` / `IImageSource` / `ThumbnailService` 迁移，走 `Sevencat.ImageFacade`。
5. 数据集浏览器：目录树、图片列表虚拟化、缩略图缓存。

验收：可打开 YOLO 数据集，标注 1000 张图不卡顿、不丢数据，保存结果能被训练引擎直接读取。

## M3 标注 Web

任务：
1. 搬 `TinyLabel.Server` 的 `ServerHost` / `ServerOptions` / `Data`（SQLite）/ `Security` 到 `Sevencat.Label.Web`，改为类库，由 `Sevencat.Label.App` 启动。
2. 搬 `Endpoints`（Auth / Project / Image / Stats）与 `WebPageEndpoints`（gzip 内嵌静态页）。
3. 数据集浏览、标注查看与统计页面走 `IAnnotationStore` / `IDatasetStore` 接口，不重复实现解析。
4. `ProjectService` 里与 Export 相关的逻辑对接 `IImageAnnotationCodec`。
5. 桌面端加"服务"面板：端口、状态、打开浏览器。

验收：标注程序运行时浏览器能浏览数据集并导出，关闭程序后服务停止。

## M4 训练引擎

任务：
1. 整体搬 `OmniTensorSharp.Core` / `NN` / `Models` / `IO` / `Export` / `Training` 到 `Sevencat.Train.Engine`（改命名空间；`TorchInterop`、`ComputeDevice` 保留）。
2. `Data` 部分改造：`YoloDataset` / `CocoDataset` / `DatasetLocator` 收敛到 M1 的接口实现，训练侧只拿 `DatasetSample`。
3. 确认模型与 ultralytics 权重互转、ONNX 导出与 onnxruntime 交叉验证（原项目的验证脚本可继续用）。
4. 单测迁移：参数数量锁定、state_dict 键名、损失数值、导出结构。

验收：`omnits` 等价的命令行或测试入口能完成一次 YOLOv8 训练（小数据集、少 epoch），产出 safetensors + onnx。

## M5 训练桌面

任务：
1. 搬 `OmniTensorSharp.Train.App` 的 `TrainingPage` / `YoloTrainingSession` / `DetrTrainingSession` / `DetectionDatasetSource` 到 `Sevencat.Train.App`。
2. 任务队列：排队 / 运行 / 停止 / 续训，单任务独占 GPU。
3. 参数表单与预设；数据集选择走 `IDatasetStoreProvider`。
4. 日志与指标：`TrainProgress` → 曲线；训练记录持久化。
5. 产物管理：safetensors / onnx / 日志文件登记与打开目录。

验收：在界面上选数据集、配参数、跑完一次 YOLO11 训练并看到曲线与产物。

## M6 检测验证

任务：
1. 搬 `Prediction/*`（`IObjectPredictor`、`YoloPredictor`、`DetrPredictor`、`PredictorFactory`）到 `Sevencat.Train.Engine`。
2. 单图 / 目录推理与可视化（阈值、NMS 联动）。
3. GT 对比与漏检/误检统计。
4. 验证集批量评估，输出 mAP 与每类 AP。

验收：加载刚训练出的检查点，单图推理结果与训练日志中的验证指标一致（同一阈值下）。

## M7 训练 Web

任务：
1. `Sevencat.Train.Web` 新建：Minimal API（任务 CRUD、启停、日志 SSE、指标 JSON、产物下载），由 `Sevencat.Train.App` 启动。
2. 静态页面（可复用 TinyLabel.Server 的内嵌页方案）：任务列表、详情、曲线、日志。
3. 鉴权复用 TinyLabel 的 Token 中间件。

验收：浏览器启动训练并实时看到日志与曲线，桌面端同步显示同一任务状态。

## M8 打磨

- 打包（单文件 / 安装包）、启动耗时与内存、全局异常与崩溃日志、i18n。

## 风险与依赖

- Avalonia 12.1.5 与 Material.Avalonia 最新版的实际兼容性需在 M0 首日验证，不通则临时降级 11.x。
- TorchSharp 原生包体积大（libtorch），发布包会显著变胖；CUDA 版需用户自行替换原生包（见 OmniTensorSharp `docs/backends.md`）。
- TinyLabel 的画布、VM 与 Server 服务层耦合较紧（`MainWindowViewModel` 51KB、`ProjectService` 71KB），迁移时先照搬、再按接口切分，不要边搬边重构。
- `Sevencat.Core` 被两个 sln 同时引用，改动需同时验证两边编译；避免在 Core 引入任一方向的重依赖。
