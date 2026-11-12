# 杂项

技术选型、命名约定、参考代码复制清单、待决问题、踩坑记录。

## 1. 技术选型与版本

| 项 | 版本/方案 | 状态 | 说明 |
| --- | --- | --- | --- |
| Avalonia | 12.1.5 | 已用 | M0 已验证可构建、可启动；最新为 12.1.6 |
| Material.Avalonia | 3.21.0 | 已用 | 与 Avalonia 12.1.5 无冲突（依赖 Material.Styles） |
| Sevencat.ImageFacade / .SkiaSharp | 0.3.0 | 已引用 | 界面渲染与训练数据管线共用 SkiaSharp 后端（exe 入口 `ImageRuntime.Use` 注册），不用 Sdcb 软后端 |
| 训练底座 | TorchSharp 0.107 + libtorch-cpu 2.10 | 已引用 | 纯 C#，无 Python；引擎在 `Sevencat.Train.Engine`，CUDA 换 `libtorch-cuda-12.8-win-x64` |
| Web 服务 | ASP.NET Core Minimal API（类库内嵌） | 已定 | 直接沿用 TinyLabel.Server 的 `ServerHost` 方案 |
| Web 页面 | gzip 内嵌静态资源 | 已定 | 沿用 TinyLabel 的 `WebPageEndpoints`，发布不带 wwwroot |
| 数据库 | SQLite（项目/用户/任务元数据） | 已定 | 沿用 TinyLabel.Server 的 `ServerDatabase` |
| MVVM | CommunityToolkit.Mvvm | 拟用 | 与 TinyLabel.App 保持一致 |

## 2. 解决方案与命名约定

- 两个解决方案：`Sevencat.Label.sln`、`Sevencat.Train.sln`，都可包含 `src/Sevencat.Core`（同一份源码，不做 CRLF/路径复制）。
- `Sevencat.<模块>` = 核心层，不含 UI 依赖：`Sevencat.Core`、`Sevencat.Label`、`Sevencat.Train.Engine`。
- 可执行程序一律 `Sevencat.<模块>.App`：`Sevencat.Label.App`（标注）、`Sevencat.Train.App`（训练，含业务与界面）。
- `Sevencat.<模块>.Web` = Web 类库，由对应的桌面程序进程内启动。
- 模块间只通过 `Sevencat.Core` 通信；`Sevencat.Label` 不得引用 `Sevencat.Train.*`，反之亦然。
- 检测不建项目：推理实现放 `Sevencat.Train.Engine`，页面放 `Sevencat.Train.App`。
- 新增格式的落点：`Sevencat.Core` 定义接口，`Sevencat.Label` 或 `Sevencat.Train.Engine` 实现并在 DI 注册。

## 3. 参考代码复制清单

原则：**先复制、跑通、再抽象**。复制时只改命名空间，不顺手重构，避免丢行为。

### 3.1 来自 `F:\code\TinyLabel\src`

| 源文件 | 目标 | 处理 |
| --- | --- | --- |
| `TinyLabel.Shared/AnnotationDocument.cs`、`Shape.cs`、`Geometry.cs`、`IdGenerator.cs`、`TinyLabelInfo.cs` | `Sevencat.Core` | 改命名空间；`TinyLabel` 元数据字段改名 `annotrain` |
| `TinyLabel.Shared/Storage.cs` | `Sevencat.Core` | 保留 `IAnnotationStore` / `AnnotationEnvelope` / 并发异常签名 |
| `TinyLabel.Shared/AtomicFile.cs` | `Sevencat.Core` | 原样 |
| `TinyLabel.Shared/LabelmeSerializer.cs` | `Sevencat.Label` | 改造成 `IImageAnnotationCodec` 实现 |
| `TinyLabel.Shared/LocalFileAnnotationStore.cs` | `Sevencat.Label` | 改为按 codec 分派，路径规则可配 |
| `TinyLabel.Shared/ImageModels.cs`、`ImageContracts.cs`、`ImageLoader.cs`、`IImageSource.cs`、`ThumbnailService.cs` | `Sevencat.Label` | 解码换成 `Sevencat.ImageFacade` 后端 |
| `TinyLabel.Shared/AnnotationEditor.cs` | `Sevencat.Label` | 原样（撤销/重做） |
| `TinyLabel.Shared/NaturalStringComparer.cs` | `Sevencat.Label` | 原样 |
| `TinyLabel.Annotation/AnnotationCanvas.cs`、`LabelPalette.cs` | `Sevencat.Label.App` | 改命名空间，接 SkiaSharp 渲染 |
| `TinyLabel.App/ViewModels/*`、`Views/*` | `Sevencat.Label.App` | 去掉远端工作区逻辑后再搬 |
| `TinyLabel.App/Infrastructure/*` | `Sevencat.Label.App` | 原样（配置、路径、DI 扩展） |
| `TinyLabel.App/Services/BitmapConverter.cs`、`ThemeService.cs` | `Sevencat.Label.App` | 原样 |
| `TinyLabel.Server/ServerHost.cs`、`ServerOptions.cs`、`NLogConfig.cs` | `Sevencat.Label.Web` | 类库化，由桌面端调用 |
| `TinyLabel.Server/Data/*`、`Endpoints/*`、`Security/*`、`Services/*`、`Web/*` | `Sevencat.Label.Web` | 服务逻辑改走 Core 接口，不直接读文件 |
| `TinyLabel.Shared/ProjectContracts.cs`、`PagingContracts.cs`、`StatsContracts.cs`、`AuthContracts.cs` | `Sevencat.Label.Web` | 作为 API DTO |
| `TinyLabel.Shared/MaskContour.cs`、`MaskRasterizer.cs`、`App/Services/MaskConverter.cs` | 不迁移 | 分割功能，首版不做 |
| `TinyLabel.Server/ViewModels/*`、`Views/*` | 不迁移 | 原为 Avalonia 管理端，改为网页 |

### 3.2 来自 `F:\code\OmniTensorSharp`

| 源文件 | 目标 | 处理 |
| --- | --- | --- |
| `src/OmniTensorSharp.Core/*`（`ComputeDevice`、`TorchInterop`、`Rng`、`Scheduling/*`） | `Sevencat.Train.Engine` | 改命名空间 |
| `src/OmniTensorSharp.NN/*` | `Sevencat.Train.Engine` | 原样 |
| `src/OmniTensorSharp.Models/*`（Yolo / RfDetr / SimpleDetr） | `Sevencat.Train.Engine` | 原样，键名对齐 ultralytics |
| `src/OmniTensorSharp.IO/*`（Onnx / SafeTensors） | `Sevencat.Train.Engine` | 原样 |
| `src/OmniTensorSharp.Export/*` | `Sevencat.Train.Engine` | 原样 |
| `src/OmniTensorSharp.Training/*`（`Trainer`、`Checkpoint`、`Ema`、`TrainControl`） | `Sevencat.Train.Engine` | 原样，进度回调接 `IProgress<TrainProgress>` |
| `src/OmniTensorSharp.Data/*` | `Sevencat.Train.Engine` | **改造**：`YoloDataset`/`CocoDataset`/`DatasetLocator` 收进 `IDatasetStore` 实现，训练侧只吃 `DatasetSample` |
| `src/OmniTensorSharp.Train.App/Training/*Session*.cs`、`DetectionDatasetSource.cs` | `Sevencat.Train.App` | 业务编排，数据集参数改为接口注入 |
| `src/OmniTensorSharp.Train.App/Prediction/*` | `Sevencat.Train.Engine` | 原样（`IObjectPredictor` 已有，直接复用） |
| `src/OmniTensorSharp.Train.App/TrainingPage.axaml*`、`PredictPage.axaml*` | `Sevencat.Train.App` | UI 页面，事件处理改为调用业务层 |
| `tools/OmniTensorSharp.Cli` | 不迁移 | 用测试与界面替代；调试需要时再考虑 |

## 4. 存储接口化的具体做法

现状问题：

- TinyLabel：`LocalFileAnnotationStore` 直接 `LabelmeSerializer`，格式写死；`IAnnotationStore` 是对的，但只有 LabelMe 一个实现。
- OmniTensorSharp：`DatasetLocator` 是静态类，`YoloDataset` / `CocoDataset` 是具体类，`YoloTrainingSession` 直接 `new YoloDataset(...)`。

改造步骤：

0. **先做存储后端**：`IBlobStore`（读/写/区间读/列举/删除）+ `LocalBlobStore`，把 `AtomicFile` 收进去。
   参考代码里所有 `File.Exists` / `File.ReadAllText` / `Directory.EnumerateFiles` 都换成 `IBlobStore`，
   这样以后接 S3 只需新增一个实现，标注端与训练端同时受益。
1. 把 `DatasetLocator.Detect` 的返回类型提为 `DatasetProbe` 记录，静态方法包进 `IDatasetProbe` 实现（先用 `DefaultDatasetProbe`，行为不变）。
2. 给 `YoloDataset` / `CocoDataset` 各写一个 `IDatasetStoreProvider` 适配器；原来的 concrete 类降级为内部实现（`internal`）。
3. 训练会话只接收 `IDatasetStore` + split 名，内部把 `DatasetSample` 转成 YOLO 的归一化 cxcywh 或 DETR 的归一化 cxcywh/xyxy。
4. 标注侧 `IAnnotationStore` 保留签名：DB 形态走 `DatabaseAnnotationStore`；LabelMe / YOLO 各自实现 `IImageAnnotationCodec`（导出用），COCO 走 `IDatasetAnnotationCodec`。
5. 所有实现注册到 DI，UI 与 Web 只注入接口。

## 5. 统一存储模型：形态 × 后端

标注端和训练端**必须遵守同一套存储模型**，否则以后换成对象存储要改两遍。

两层正交：

- **形态**（`IDatasetStoreProvider`）：yolo / coco(RF-DETR) / tinylabel(图片目录 + SQLite)。
- **后端**（`IBlobStore`）：file / s3 / http。

```text
IBlobStore（字节级：Exists / Stat / OpenRead / OpenReadRange / Write / Delete / List）
   ├── LocalBlobStore   file://   原子写 = tmp + replace
   ├── S3BlobStore      s3://     原子写 = 单对象 PUT；大图 = Range GET（未实现）
   └── HttpBlobStore    http(s):// 只读 + Range（未实现）
        ▲
        ├── IImageSource        （图片字节 → 解码 → SKBitmap）
        ├── IAnnotationStore    （标注文件字节 → codec → AnnotationDocument）
        └── IDatasetStore       （列举 + 描述文件 → DatasetSample）
```

形态与后端是两个维度，组合关系：

| 形态 | 图片位置 | 标注位置 | 描述/索引 | 后端限制 |
| --- | --- | --- | --- | --- |
| `yolo` | `images/{split}` | `labels/{split}/*.txt` | `data.yaml` | file / s3 |
| `coco`（RF-DETR） | `train/valid/test` | `{split}/_annotations.coco.json` | 同 json | file / s3 |
| `tinylabel` | 项目目录 / 扫描根 | SQLite `annotations` 表 | `projects`/`images`/`project_classes` | DB 必须本地；图片可 file / s3 |

约束：

- `StorageLocation(scheme, root)` 描述一个后端入口；图片、标注、描述可以各指向不同后端。
- `tinylabel` 形态没有"标注文件"，`IAnnotationStore` 换成 `DatabaseAnnotationStore`（走 FreeSql），
  与 `YoloTxtCodec` 无关；导出 YOLO/COCO 时才把表里的 JSON 过一遍 codec。
- 训练端吃 `tinylabel` 时，split 直接取 `images.Split`（NULL = 未分配，不参与训练），不做隐式划分。
- 除 `LocalBlobStore` 内部，代码里不允许出现 `File.*` / `Directory.*` 调用；codec 接收 `Stream`，不接收路径。
- `OpenReadRangeAsync` 必须存在：本地是 `Seek`，远端是 `Range` 头；不然后面大图标注会很痛。
- 列举结果允许缓存（远端 list 有延迟且可能不一致），缓存键 = location + prefix + 更新时间。
- 写入语义只要求"覆盖写 + 完成后可见"，不要求 rename，避免被本地文件系统语义绑架。

### 5.1 三种形态的存储布局

三种形态各自对应一棵固定的目录树，探测、打开、导出都围绕它们；括号里是 `IDatasetStore` 的取值来源。

#### 1. 标准 YOLO（`yolo`）

```text
<root>/
├── data.yaml                     # 入口（DatasetProbe.DescriptorPath）
├── images/
│   ├── train/*.jpg
│   └── val/*.jpg
└── labels/
    ├── train/*.txt               # 与图片同名
    └── val/*.txt
```

```yaml
# data.yaml
path: .                           # 数据集根；本项目按相对 root 处理，允许省略
train: images/train
val: images/val
test: images/test                 # 可选
names:                            # 顺序即 classId，必须从 0 连续
  0: cat
  1: dog
```

- 类别 ← `names`；`Splits` ← `train` / `val` / `test` 里配置了的项。
- 标注一图一个 txt，每行 `<classId> <cx> <cy> <w> <h>`，归一化到 `[0,1]`；空文件表示无框。
- 图片与标注分目录，靠把路径里的 `images/` 换成 `labels/` 定位（`YoloAnnotationPathRule`）。
- 只认根目录的 `data.yaml` / `.yml`，且必须同时有 `names` 与 `train`/`val` 才算命中。

#### 2. 标准 RF-DETR（`coco`）

```text
<root>/
├── train/
│   ├── _annotations.coco.json    # 入口，一个 json 管整个 split
│   └── *.jpg
├── valid/
│   ├── _annotations.coco.json
│   └── *.jpg
└── test/…

# 也接受 Roboflow 的变体：json 在 annotations/ 下，图片在 images/ 下
<root>/annotations/instances_train.json + <root>/images/*.jpg
```

```json
{
  "images":      [{"id": 1, "file_name": "a.jpg", "width": 640, "height": 480}],
  "annotations": [{"id": 1, "image_id": 1, "category_id": 0,
                   "bbox": [10, 20, 100, 60], "area": 6000, "iscrowd": 0}],
  "categories":  [{"id": 0, "name": "cat"}]
}
```

- `bbox` 是**绝对像素 `[x, y, w, h]`**（左上角 + 宽高），写回时同样输出 xywh；内部一律转成像素 `xyxy`。
- 类别 ← `categories`（按 `id` 映射，不要求连续）；某张图没出现在 `annotations` 里即为无框。
- 图片位置 = json 所在目录 + `file_name`；`Splits` ← 找到的 json 所在子目录名。
- 因为「一个 json 管整个数据集」，它走 `IDatasetAnnotationCodec`（数据集级），不是单图 codec；
  探测时只读 json 前 8KB，确认同时出现 `"images"` 与 `"annotations"`。

#### 3. 图片目录 + SQLite（`tinylabel`）

```text
<data>/
├── annotrain.db                  # 入口（SQLite + WAL）
└── projects/
    └── {projectId}/…             # 图片目录（导入模式是副本，扫描模式引用原图）
```

表结构与 `F:\code\TinyLabel\src\TinyLabel.Server\Data` 一致（2026-11 版）；**主键是 SQLite 自增 `long`，不是字符串**：

| 表 | 关键列（类型） | 说明 |
| --- | --- | --- |
| `projects` | `Id`(PK,自增 `long`)、`Name`、`Description`、`StorageMode`(`import`/`scan`)、`CreatedBy`(`long`)、`CreatedAt`、`ClosedAt` | 一行一个项目；`projects/{Id}/` 是图片目录 |
| `images` | `Id`(PK,自增 `long`)、`ProjectId`(`long`)、`FileName`、`RelPath`、`FilePath`、`Width`、`Height`、`FileSizeBytes`、`Sha256`、`State`、`Revision`、`LockedBy`/`LockExpiresAt`、`AssigneeId`、`Remark`/`RemarkBy`/`RemarkAt`、`CreatedAt`、`UpdatedAt`、**`Split`** | 二进制不进库；用户相关列都是 `long` 用户 Id；`Split` 为 Annotrain 新增，`train`/`val`/`test`/NULL |
| `annotations` | `ImageId`(PK,`long`，与图片一对一)、`ProjectId`、`Revision`、`DocJson`、`UpdatedBy`(`long`)、`UpdatedAt` | `DocJson` = labelme JSON 原文，一图一行 |
| `project_classes` | `Id`(PK,自增 `long`)、`ProjectId`、`Name`、`OrderIndex`、`Color` | `OrderIndex` 即 classId，只增不复用（删类别做逻辑删除） |

`images.State` 取值：`pending` / `labeled` / `reviewed` / `excluded` / `skipped`。

服务端专属表（Annotrain 桌面端**不建模、不创建**，TinyLabel.Server 启动时自己 `SyncStructure` 补齐）：
`users`、`project_members`、`settings`（键值配置）、`autolabel_schemes`。
旧版残留已由服务端清理：`reviews` 表废弃、`users.Role` 删除、`images.ReviewComment/ReviewedBy/ReviewedAt` 删除。

- 类别 ← `project_classes`（按 `OrderIndex` 排序）；`Splits` ← `images.Split` 去重，NULL 视为未分配、默认不参与训练。
- 标注读写走 `DatabaseAnnotationStore`，用 `images.Revision` + `annotations.Revision` 做乐观锁，不碰文件。
- 标注端**只打开这一种形态**；导出 YOLO / RF-DETR 时把 `DocJson` 交给 `LabelmeCodec` 解析，再用目标形态的 codec 写出去。
- DB 必须落本地（WAL 需要随机写），图片位置可以另指 `file://` 或远端。
- 已落地：`TinyLabelDatabase`（新库建四张表 + 索引 + WAL；打开旧库时缺 `images.Split` 自动 `ALTER TABLE` 补列）、
  `TinyLabelDatasetStoreProvider`（多项目库要么库里只有一个项目，要么显式传 projectId）、
  `DatabaseAnnotationStore`（`images.Revision` + `annotations.Revision` 同步递增做乐观锁，`UpdatedBy` 固定 0）。

#### 导出产物（与 TinyLabel 现有导出保持一致）

| 目标 | 目录结构 | 额外文件 |
| --- | --- | --- |
| `yolo` | `images/{train,val,test}/*` + `labels/{train,val,test}/*.txt` | `data.yaml`（`path` 写**目标目录绝对路径**，`train`/`val`/`test` 指向对应 `images/*`，`names` 顺序 = classId）、`classes.txt` |
| `coco`（RF-DETR） | `train/*`、`valid/*`（源 `val` 落成 `valid`）、`test/*`；图片与 `_annotations.coco.json` 同级 | `classes.txt` |

- 导出目标不能与源数据集根相同（同根直接拒绝，避免就地覆盖）。
- 归一化框写 6 位小数；越界框会被 clamp 后仍为空则丢弃，并在报告里记 `BoxesDropped`。
- 报告包含 `ImagesExported` / `ImagesSkipped` / `BoxesExported` / `BoxesDropped` / `Warnings`，界面直接展示。

### 5.2 自动检测与项目类型

用户给一个目录/文件，先判断是什么格式；判断结果必须落到项目记录里，下次直接打开。

探测规则（只读文件名 + 文件头，禁止全量解析）：

| 形态 | 强特征 | 弱特征 |
| --- | --- | --- |
| `yolo` | 根目录有 `data.yaml`/`.yml` 且含 `names` + `train` | `images/` 与 `labels/` 成对、子目录有 `train`/`val` |
| `coco` | json 前 8KB 同时出现 `"images"` 与 `"annotations"` | `annotations/*.json`、`train/_annotations.coco.json` |
| `tinylabel` | `*.db` 且能连上并看到 `projects`/`images`/`annotations` 表 | 目录含 `projects/{id}` 结构 |

项目表新增字段（`projects`）：

```csharp
[Column(StringLength = 16, IsNullable = false)]
public string Format { get; set; } = "tinylabel";    // yolo / coco / tinylabel

[Column(StringLength = 512, IsNullable = true)]
public string? DescriptorPath { get; set; }         // data.yaml / COCO json / db 文件

[Column(IsNullable = true)]
public DateTime? DetectedAt { get; set; }
```

约定：

- `Format`（数据格式）与已有的 `StorageMode`（Import 复制 / Scan 引用）正交，两者都要存。
- 新建项目：自动检测 → 多候选让用户选 → 写入 `Format` + `DescriptorPath`。
- 打开项目：直接用库里的 `Format` 定位，不重扫；只有用户点"重新检测"才探测，结果不一致给提示。
- 标注端只把 `tinylabel` 作为可编辑项目打开；YOLO / RF-DETR 目录可**导出**，也可**导入**成项目（复制一份，源目录不动），不提供"打开即编辑"（见 `LabelDatasetPolicy`）。
- 检测逻辑放在 `Sevencat.Core` 的 `IDatasetProbe` 实现里，标注端与训练端共用同一份。

## 6. 设计备忘

- 坐标内部统一原图像素 `float`，显示与写盘时才换算。
- 「目录布局」「单图标注格式」「数据集级标注格式」「存储后端」是四个正交维度，不要合并。
- 探测顺序：显式配置 > 描述文件 > 扩展名 > 结构特征；探测只读文件头（远端只读前 8KB）。
- 原子写盘：`xxx.tmp` → `File.Replace` / `Move(overwrite)`，失败保留原文件（`AtomicFile` 已实现，收进 `LocalBlobStore`）。
- 训练检查点同样原子写，避免中断产生半截 safetensors。
- 单任务独占 GPU：同进程内串行执行，队列不在多线程上并行跑两个 `Trainer`。

## 7. 待决问题

| 编号 | 问题 | 倾向 | 状态 |
| --- | --- | --- | --- |
| Q1 | 两个 Web 是否都用 ASP.NET Core Minimal API | 是，复用 TinyLabel.Server 已跑通的方案 | 已定 |
| Q2 | 检测是否单独建项目 | 不建，推理在 Engine、页面在 Train | 已定 |
| Q3 | 是否支持远程训练（SSH / HTTP） | 不做，纯本地进程内 | 已定（不做） |
| Q4 | 是否迁移分割（Mask）相关代码 | 不迁移 | 已定（不做） |
| Q5 | 大图是否需要瓦片（tiling）标注 | 依实际图像尺寸决定 | 未定 |
| Q6 | 标注历史版本 / 审计 | 首版不做，仅依赖 revision 乐观锁 | 未定 |
| Q7 | 训练 Web 与桌面端同时操作同一任务 | 后端单一任务队列，接口层加互斥 | 未定 |
| Q8 | S3 实现方式 | 首版不实现；届时二选一：AWSSDK.S3（全功能但重）或 MinIO/自写 SigV4（轻量） | 未定（延后） |
| Q9 | 远端数据集是否本地缓存 | 图片缩略图必须缓存；原始图按 Range 直读 | 未定 |
| Q10 | `tinylabel` 形态的 train/val 划分 | 已定：`images` 表加 `Split` 列（train/val/test/NULL），不再靠推断 | 已定 |
| Q11 | 是否支持 VOC / 平铺两种形态 | 已定：**本期都不做**；`DatasetFormat` 只保留 yolo / coco / tinylabel | 已定 |

### Q10 结论：`tinylabel` 的 train/val 用数据库字段表示

原 TinyLabel 的 `ImageEntity` 只有 `State`（标注状态）、`Revision`、`LockedBy`、`AssigneeId`、`Remark`，没有 train/val 字段；一个项目里的图片平铺在一起，训练端无法判断哪些算验证集。

**决定：改表，加字段。**

```csharp
// images 表新增
[Column(StringLength = 16, IsNullable = true)]
public string? Split { get; set; }        // "train" / "val" / "test" / null(未分配)

// 取值常量
public static class DatasetSplits
{
    public const string Train = "train";
    public const string Val = "val";
    public const string Test = "test";
}
```

约定：

- 与 `State` 正交：`State` 管审核质量，`Split` 管用途；不作互相推断。
- 可空 + 默认 NULL，旧库由 FreeSql `CodeFirst.SyncStructure` 自动加列，不需要手工迁移脚本。
- 训练端 `OpenSplit("train")` 只认 `Split='train'`；**NULL 的图默认不参与训练**，界面上提示"未分配"，并提供批量分配（按目录 / 按比例 / 按状态）的辅助操作。
- 导出 YOLO / COCO 时直接用该字段决定落哪个目录 / 哪个 json。
- 标注端按 Split 过滤图片列表；Web 端（`ProjectEndpoints`）提供设置 Split 的接口。
- 不引入 `split` 表，单列足够；将来要"多套划分"再说。

## 8. 参考

- `F:\code\TinyLabel`：标注画布、标注模型、Web 服务与内嵌静态页方案
- `F:\code\OmniTensorSharp`：TorchSharp 训练、模型结构、ONNX 导出、推理
- Ultralytics YOLO 数据集格式与 state_dict 键名约定
- RF-DETR / Roboflow COCO 目录约定
- Avalonia 12 迁移说明、Material.Avalonia 仓库

## 9. 踩坑记录

按时间追加，格式：`日期 - 现象 - 原因 - 处理`。

- 2026-11-12 - 初始化文档，暂无记录。
- 2026-11-12 - `App.axaml` 里写 `<material:MaterialTheme>` 报 `AVLN2000: Unable to resolve type MaterialTheme` - 类型实际在 `Material.Styles.Themes`（程序集 `Material.Styles`），不在 `Material.Avalonia` - 改为 `xmlns:themes="clr-namespace:Material.Styles.Themes;assembly=Material.Styles"` + `<themes:MaterialTheme BaseTheme="Light" PrimaryColor="Blue" SecondaryColor="Teal" />`；MaterialTheme 自带 Fluent 样式，不必再加 `FluentTheme`。
- 2026-11-12 - `dotnet new sln` 生成的是 `.slnx` 而不是 `.sln` - .NET 10 SDK 默认新格式 - 加 `--format sln`。
- 2026-11-12 - `dotnet sln add` 一次传多个 csproj 时报"解决方案文件夹 src 已包含文件名为 X 的项目"并回滚，项目全部丢失 - 批量添加时反复重写 sln 的副作用 - 改为直接手写 `.sln`（5 个项目，标准 GUID 模板），`dotnet sln list` 验证正常。
- 2026-11-12 - 找不到 NuGet 包缓存目录 - 全局包目录被改到 `E:\program\nuget\cache` - 用 `dotnet nuget locals global-packages --list` 查实际路径，排查包内容时按该路径找。
- 2026-11-12 - 检查 Material.Avalonia 用法时访问 raw.githubusercontent.com 被重置 - 网络限制 - 直接读包内 `README.md`（`<全局包目录>\material.avalonia\<版本>\README.md`）最可靠。
