# 需求

## 1. 目标与边界

- 目标：YOLOv8 / YOLO11 / RF-DETR 的「标注、训练、检测验证」闭环，纯 C# 实现，不依赖 Python 运行时。
- 边界：
  - 检测只用于验证模型效果（单图、目录批量、摄像头），不追求吞吐与部署形态。
  - 不做对外推理服务、不做 SDK、不做多租户。
  - 分割、关键点、OBB 暂不实现（参考代码里的 Mask 部分不迁移），接口保留扩展位。
- 交付形态：两个独立桌面应用，各自带内嵌 Web 管理。

## 2. 功能需求

### 2.1 存储抽象层（最关键）

参考代码里的存储都是"具体类写死"（TinyLabel 的 `LocalFileAnnotationStore` 绑死 LabelMe，OmniTensorSharp 的 `YoloDataset` / `CocoDataset` 直接被训练会话 new）。
本项目的硬性要求：**业务、画布、训练器一律不直接 new 具体格式类、不直接拼路径**。

| 编号 | 需求 |
| --- | --- |
| F-1.1 | 数据集目录组织可插拔，**首期只做三种**：标准 YOLO（`data.yaml` + `images/labels`）、标准 RF-DETR（COCO：`annotations/*.json`、`train/_annotations.coco.json`）、图片目录 + SQLite（类别与标注都在库里）。平铺目录、Pascal VOC **本期不做**。 |
| F-1.2 | 标注格式可插拔：首版实现 YOLO txt、COCO json、LabelMe json（LabelMe 作为 DB 里标注 JSON 的存法）；**Pascal VOC xml 本期不做**。 |
| F-1.3 | 单图格式与数据集级格式**分开抽象**：YOLO/LabelMe 是一图一文件，COCO 是整个数据集一个 json，不能强行合成一个接口。 |
| F-1.4 | 目录探测走接口，只读文件头/文件名，不解析全量标注（几万张图的 COCO 不能在这里卡住）。 |
| F-1.5 | 类别表来源可配：`data.yaml` / `classes.txt` / COCO `categories` / 手工维护，支持导出。 |
| F-1.6 | 训练引擎只消费统一模型 `DatasetSample`（图片路径 + 原始宽高 + 原图像素 xyxy 框 + classId），格式差异在 Loader 内消化。 |
| F-1.7 | 新增格式 = 新增实现类 + DI 注册，不改画布、训练器与既有实现。 |
| F-1.8 | 标注端与训练端共用同一套契约（`Sevencat.Core`），避免同一个数据集两处解析逻辑不一致。 |
| F-1.9 | **图片与描述（标注文件、`data.yaml`、COCO json）走同一套存储后端抽象**：`IBlobStore`（字节级），本地文件只是其中一种实现。 |
| F-1.10 | 存储位置用统一 URI：`file://`、`s3://`、`http(s)://`；数据集根目录、标注目录、图片目录都可指向任意后端，也允许混搭（图片在 S3、标注在本地）。 |
| F-1.11 | 远端后端要能按区间读取（大图分块）+ 列举前缀；本地后端必须支持原子写。 |
| F-1.12 | 除 `LocalBlobStore` 内部外，**任何代码不得直接调用 `File.*` / `Directory.*`**；格式 codec、数据集 loader、画布、训练器一律只依赖接口。 |
| F-1.13 | 首版只实现 `file://`；`s3://` 等实现留出接口与注册点，不写空壳代码。 |

### 2.1.1 首期必须支持的三种存储形态

「形态」决定图片与标注怎么组织，「后端」决定字节存在哪里，两者正交。同一形态可跑在 `file://` 或 `s3://` 上。

| 形态 | Provider Id | 图片 | 标注 | 描述/索引 | 后端 |
| --- | --- | --- | --- | --- | --- |
| YOLO 布局 | `yolo` | `images/{train,val,test}/` | `labels/{train,val,test}/*.txt` | `data.yaml`（`path/train/val/names`） | file / s3 |
| RF-DETR（COCO/Roboflow） | `coco` | `train/` `valid/` `test/` 或 `images/` | `{split}/_annotations.coco.json` 或 `annotations/instances_*.json` | 同上 json | file / s3 |
| TinyLabel 目录 + 数据库 | `tinylabel` | 项目目录（导入副本）或扫描根（只读引用） | SQLite `annotations` 表（JSON 文本） | `projects` / `images` / `project_classes` 表 | DB 本地 + 图片 file/s3 |

| 编号 | 需求 |
| --- | --- |
| F-1.14 | 三种形态各有一个 `IDatasetStoreProvider`；探测时按特征判定，多个命中则让用户选（沿用原 `Ambiguous` 语义）。 |
| F-1.15 | `tinylabel` 形态沿用 FreeSql + SQLite（WAL），图片二进制不进库，只存相对路径 + sha256；标注仍是 JSON 文本。DB 位置与图片位置分开配置。`images` 表**增加 `Split` 列**（`train`/`val`/`test`/NULL），与 `State` 正交。 |
| F-1.16 | 训练端必须能直接消费 `yolo` / `coco` / `tinylabel` 三种形态：`tinylabel` 的 train/val 直接取 `images.Split`，NULL 视为未分配，默认不参与训练并在界面提示。 |
| F-1.17 | 形态之间可转换：`tinylabel` → YOLO / COCO 为必需（训练与交付都要），YOLO ↔ COCO 为应交由同一套 codec 完成的常见操作。 |
| F-1.18 | 标注端打开哪种形态，`IAnnotationStore` 就落到同一形态（文件式走 codec，DB 式走表），不允许"读 DB 写 txt"这类错配。 |

### 2.1.2 自动检测与项目类型

项目表要记住"这个项目是什么格式"，不能每次打开都全盘扫描；同时新建项目时必须能自动识别。

| 编号 | 需求 |
| --- | --- |
| F-1.19 | **自动检测**：用户选一个目录，系统探测出形态（`yolo` / `coco` / `tinylabel`），返回格式、入口文件、候选列表与一句人话说明。 |
| F-1.20 | **项目类型落库**：`projects` 表增加 `Format` 字段（`yolo` / `coco` / `tinylabel`，即用户可理解的"项目类型"），新建/导入时把检测结果写进去。 |
| F-1.21 | 检测出多个候选（歧义）或识别失败时，必须让用户选/手工指定，不许静默猜一个。 |
| F-1.22 | 项目打开时优先用库里存的 `Format` 直接定位，不再重扫；提供"重新检测"入口，结果与库中不一致时提示而不是偷偷改。 |
| F-1.23 | 检测结果可缓存：`DescriptorPath`（`data.yaml` / COCO json / DB 文件）与 `DetectedAt` 一并写入项目，供下次直接打开。 |
| F-1.24 | **端侧边界**：标注端**只打开** `tinylabel` 形态（图片目录 + DB）作为可编辑项目；YOLO / RF-DETR 在标注端可作为**导出目标**，也可**导入**成项目（复制图片与标注，连 train/val 一起搬，源目录只读）。训练端三种形态都能读（只认 `IDatasetStoreProvider`），不受此限制。 |

### 2.2 标注

| 编号 | 需求 |
| --- | --- |
| F-2.1 | 画布：缩放、平移、框的创建/选中/移动/缩放/删除，类别切换与颜色映射（复用 TinyLabel `AnnotationCanvas`）。 |
| F-2.2 | 快捷键齐全（新建、删除、复制、上下切换图片、保存、撤销/重做）。 |
| F-2.3 | 自动保存：切图、失焦、定时保存；原子写盘（临时文件 + 替换），失败保留原文件并提示。 |
| F-2.4 | 大图与大批量：4K 以上图像流畅缩放；数千张图的虚拟化列表与缩略图异步缓存。 |
| F-2.5 | 坐标内部统一为**原图像素浮点**，写盘时按格式换算（YOLO 归一化 cxcywh、COCO 绝对 xywh、VOC xmin/ymin/xmax/ymax），往返不丢精度。 |
| F-2.6 | 乐观锁：保留 TinyLabel 的 `Revision` 语义，写盘冲突给出可读错误。 |
| F-2.7 | 预留辅助标注入口（模型预标注），只定义接口，首版不实现。 |

### 2.3 训练（纯 C#）

| 编号 | 需求 |
| --- | --- |
| F-3.1 | 模型族：YOLOv8 detect、YOLO11 detect、RF-DETR。网络结构、state_dict 键名与 ultralytics / rfdetr 对齐（直接复制 OmniTensorSharp 的 `Models`）。 |
| F-3.2 | 计算后端：TorchSharp + libtorch，CPU 为正确性基线，CUDA 可选；CUDA 不可用时给出明确指引而非崩溃。 |
| F-3.3 | 数据管线：`IDatasetStore` 取样本 → letterbox → 翻转/HSV/mosaic 增强 → 批次张量；训练器不感知 YOLO/COCO 差异。 |
| F-3.4 | 训练控制：epochs、imgsz、batch、lr、预训练权重、设备、增强开关；支持预设保存；可暂停/停止。 |
| F-3.5 | 指标：逐 epoch 的 loss（box/cls/dfl）、mAP50、mAP50-95、每类 AP，写日志并绘曲线。 |
| F-3.6 | 产物：`*.safetensors` 检查点（可续训）与 `*.onnx` 前向图，记录到训练记录中。 |
| F-3.7 | 任务队列：排队 / 运行 / 停止 / 断点续训；同一时刻只跑一个任务以独占 GPU。 |
| F-3.8 | 训练异常（OOM、数据缺失、CUDA 错误）不得让进程崩溃，需捕获并落日志。 |

### 2.4 检测验证

| 编号 | 需求 |
| --- | --- |
| F-4.1 | 加载本项目的 safetensors 检查点或 ONNX，对单图 / 目录 / 摄像头推理（复用 OmniTensorSharp `IObjectPredictor`）。 |
| F-4.2 | 可视化：框、类别、置信度；阈值与 NMS 参数可调。 |
| F-4.3 | 与真值叠加对比（GT 与预测同色系区分），输出漏检/误检统计。 |
| F-4.4 | 批量跑验证集，汇总 mAP 与每类 AP，结果可导出。 |
| F-4.5 | 可选：用外部 ONNX Runtime 交叉校验，确认导出图正确性（仅开发期手段，不作为运行时依赖）。 |

### 2.5 Web 管理（内嵌）

两个模块各有一个 Web 类库，在各自桌面程序进程内启动，随主程序启停，复用同一套业务对象。

| 编号 | 需求 |
| --- | --- |
| F-5.0 | 内嵌运行：端口可配，默认仅监听回环；可显式开放局域网；桌面端显示服务状态并可打开浏览器。 |
| F-5.1 | 标注 Web（后端 `Sevencat.Label.Server`，前端 `src/Sevencat.Label.Web`）：数据集/项目列表、图片预览、标注查看与简单修改、类别管理、统计、导出数据集。 |
| F-5.2 | 训练 Web（`Sevencat.Train.Web`）：任务列表与详情、实时日志流（SSE）、指标曲线、启动/停止、产物下载。 |
| F-5.3 | 简单鉴权（单用户 token / 基础认证），复用 TinyLabel 的 Token 方案。 |
| F-5.4 | 页面以静态资源内嵌到程序集，发布产物不额外带 `wwwroot` 目录（沿用 TinyLabel 的 gzip 内嵌方式）。 |
| F-5.5 | Web 与桌面端共享同一个 `IDatasetStore` / `IAnnotationStore` 实例来源，不出现两份数据集状态。 |
| F-5.6 | 项目管理：新建时自动检测格式并允许改写 `Format`，提供"重新检测"；列表能按项目类型筛选。 |

### 2.6 通用

| 编号 | 需求 |
| --- | --- |
| F-6.1 | 配置持久化（JSON，用户配置目录），支持多份工作区配置。 |
| F-6.2 | 结构化日志 + 界面内日志面板。 |
| F-6.3 | 明/暗主题；文案集中管理（先中文）。 |
| F-6.4 | 平台：**只支持 Windows x64**（`win-x64`，框架依赖）。构建固定 RID + `PlatformTarget=x64`，Avalonia 只用 Win32 后端；运行期非 win-x64 直接提示并退出，不做 Linux / macOS / arm64 适配。 |

## 3. 非功能需求

- 性能：8K 单图打开 < 1s（缩略图另算）；1000 张图目录滚动不掉帧；标注操作无可感延迟。
- 稳定性：训练 OOM、文件占用、磁盘写满均不崩溃，给出可读错误。
- 可扩展：新增标注格式或数据集格式 = 新增实现类 + 注册。
- 数据安全：标注写盘原子化；训练检查点写盘同样原子化，不产生半截文件。
- 可测试：格式读写、坐标换算、目录探测、模型结构和导出是单测重点。

## 4. 接口草案（`Sevencat.Core`）

```csharp
// ===== 存储后端（标注与训练共用，最先落地）=====

// 字节级存储：本地文件 / S3 / HTTP。上下两层（标注、训练）全部建立在它之上。
public interface IBlobStore
{
    string Scheme { get; }                          // "file" / "s3" / "http"
    Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default);
    Task<BlobInfo?> StatAsync(StorageKey key, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(StorageKey key, CancellationToken ct = default);
    // 大图/大文件分块：S3 走 Range GET，本地走 Seek
    Task<Stream> OpenReadRangeAsync(StorageKey key, long offset, long length, CancellationToken ct = default);
    // 覆盖写，实现负责原子可见（本地 tmp+replace，S3 单对象 PUT）
    Task WriteAsync(StorageKey key, Stream content, CancellationToken ct = default);
    Task DeleteAsync(StorageKey key, CancellationToken ct = default);
    IAsyncEnumerable<BlobEntry> ListAsync(StorageKey prefix, CancellationToken ct = default);
}

public interface IBlobStoreFactory
{
    bool CanHandle(string uri);                     // file:// / s3:// / http://
    IBlobStore Create(StorageLocation location);
}

public sealed record StorageLocation(string Scheme, string Root);
public sealed record StorageKey(StorageLocation Location, string RelativePath);

// ===== 标注侧 =====

// 单图标注格式：YOLO txt / LabelMe json（导出与 DB 内 JSON 都用它）
public interface IImageAnnotationCodec
{
    string Id { get; }                                  // "yolo" / "labelme"
    IReadOnlyCollection<string> Extensions { get; }
    AnnotationDocument Read(Stream stream, ImageRef image, out IReadOnlyList<string> warnings);
    void Write(Stream stream, AnnotationDocument doc, AnnotationWriteContext context);
}

// 数据集级标注格式：COCO（一个 json 管全部图片）
public interface IDatasetAnnotationCodec
{
    string Id { get; }                                  // "coco"
    DatasetAnnotations Read(string datasetRoot);
    void Write(string datasetRoot, DatasetAnnotations annotations);
}

// 标注存取：签名沿用 TinyLabel，实现改为「按 codec 分派 + 走 IBlobStore」
public interface IAnnotationStore
{
    Task<AnnotationEnvelope?> LoadAsync(ImageRef image, CancellationToken ct = default);
    Task<AnnotationEnvelope> SaveAsync(ImageRef image, AnnotationDocument doc,
        int? expectedRevision = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(ImageRef image, CancellationToken ct = default);
}

// 像素来源：沿用 TinyLabel 的三方法形态，底层改为 IBlobStore（远端走 Range GET）
public interface IImageSource : IAsyncDisposable
{
    ImageInfo Info { get; }
    Task<SKBitmap> GetPreviewAsync(int maxSide, CancellationToken ct = default);
    Task<SKBitmap> GetRegionAsync(PixelRect region, CancellationToken ct = default);
}

// ===== 训练侧 =====

// 探测：把 OmniTensorSharp 的静态 DatasetLocator 抽成接口（远端列举是 IO，必须异步）
public interface IDatasetProbe
{
    Task<DatasetProbe> DetectAsync(StorageLocation location, CancellationToken ct = default);
}

public enum DatasetFormat
{
    Unknown = 0,
    Yolo = 1,          // data.yaml + images/labels
    Coco = 2,          // RF-DETR（COCO / Roboflow）
    TinyLabelDb = 3,   // 图片目录 + SQLite（类别与标注在库里）
    Ambiguous = 9,     // 多个命中，需用户选
}

// 一种存储形态的提供者：能否处理 + 打开（三种形态各一个实现）
public interface IDatasetStoreProvider
{
    string Id { get; }                  // "yolo" / "coco" / "tinylabel"
    DatasetFormat Format { get; }
    DatasetStoreCapabilities Capabilities { get; }   // 可写？天然含 split？随机访问？
    bool CanOpen(DatasetProbe probe);
    Task<IDatasetStore> OpenAsync(DatasetProbe probe, CancellationToken ct = default);
}

// 标注存储的提供者：与数据集形态配对，禁止读 DB 写 txt 这种错配
public interface IAnnotationStoreProvider
{
    string Id { get; }                  // "file" / "tinylabel-db"
    bool CanOpen(DatasetProbe probe);
    IAnnotationStore Open(DatasetProbe probe);
}

// 打开后的数据集：只描述结构，不读像素
public interface IDatasetStore
{
    string Id { get; }
    DatasetFormat Format { get; }
    StorageLocation Root { get; }             // 描述文件所在（file:// 或 s3://）
    StorageLocation? ImageLocation { get; }   // tinylabel 形态下 DB 与图片目录可分离
    ClassCatalog Classes { get; }
    IReadOnlyList<string> Splits { get; }
    IDatasetSplit OpenSplit(string split);
}

public interface IDatasetSplit
{
    string Name { get; }
    // 列举本身是 IO（远端是网络请求），所以异步；一次加载成列表便于训练期反复遍历
    Task<IReadOnlyList<DatasetSample>> LoadSamplesAsync(CancellationToken ct = default);
}

public sealed record DatasetSample(
    string ImagePath, int Width, int Height, IReadOnlyList<BoxAnnotation> Boxes);

public sealed record BoxAnnotation(int ClassId, float X1, float Y1, float X2, float Y2); // 原图像素

// ===== 训练与推理执行 =====

public interface ITrainingSession
{
    string Id { get; }                  // "yolo" / "rfdetr"
    Task RunAsync(TrainingRequest request, IProgress<TrainProgress> progress, CancellationToken ct);
}

public interface IObjectPredictor
{
    string Id { get; }                  // "yolo" / "rfdetr"
    IReadOnlyList<Detection> Predict(string imagePath, PredictOptions options);
}
```

详细模型（`AnnotationDocument`、`DatasetProbe`、`TrainingRequest`、`TrainProgress`）在 M1 落定，尽量沿用参考代码已有的类型定义以减少改动。

## 5. 存储后端约束（为将来 S3 留口）

- `Sevencat.Core` 里只有 `IBlobStore` 一套 IO 抽象，标注与训练**共用**，不允许各写一套。
- 首版实现 `LocalBlobStore`（`file://`），把 `File.*` / `Directory.*` 关在它内部。
- 远端注意事项（现在只需接口能表达，不实现）：
  - 列举可能很慢且不保证一致性 → 数据集列举结果要可缓存（按 mtime/prefixed 列表）；
  - 大图必须支持 Range 读取，否则整图下载会拖垮标注流畅度；
  - 写入用单对象 PUT 保证原子可见，不要"先写 tmp 再 rename"（S3 无 rename）；
  - 图片、标注、描述文件允许分布在不同后端，`StorageLocation` 逐个指定即可。
- `tinylabel` 形态是唯一带数据库的形态：SQLite（WAL）必须落在可随机读写的位置，不能直接放 S3；
  需要远端时用"DB 本地 + 图片 `s3://`"的混合配置（`IDatasetStore.ImageLocation` 已为此留位）。
- 形态转换（`tinylabel` → YOLO/COCO、YOLO ↔ COCO）统一走 codec，不做一次性脚本；转换过程要能报告丢弃/异常样本。

