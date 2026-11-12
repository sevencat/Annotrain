# 进度

状态取值：`未开始` / `进行中` / `阻塞` / `完成`。
每完成一项就更新本表，并在「日志」追加一行。

## 里程碑

| 里程碑 | 归属 | 状态 | 开始 | 完成 | 备注 |
| --- | --- | --- | --- | --- | --- |
| M0 骨架 | 两条线 | 完成 | 2026-11-12 | 2026-11-12 | 两个 sln + Core 契约 |
| M1 存储抽象 | 两条线 | 未开始 | | | 地基：IBlobStore + 格式接口 |
| M2 标注闭环 | Label | 未开始 | | | 迁移 TinyLabel 画布 |
| M3 标注 Web | Label | 未开始 | | | 迁移 TinyLabel.Server |
| M4 训练引擎 | Train | 未开始 | | | 迁移 OmniTensorSharp |
| M5 训练桌面 | Train | 未开始 | | | 队列/参数/日志/产物 |
| M6 检测验证 | Train | 未开始 | | | 推理与评估 |
| M7 训练 Web | Train | 未开始 | | | 任务与日志流 |
| M8 打磨 | 两条线 | 未开始 | | | 打包与优化 |

## 任务明细

| 编号 | 里程碑 | 任务 | 状态 | 备注 |
| --- | --- | --- | --- | --- |
| B-1 | M0 | `Sevencat.Label.sln` + 5 项目 | 完成 | Core/Label/Label.App/Label.Web/Tests |
| B-2 | M0 | `Sevencat.Train.sln` + 5 项目 | 完成 | Core/Train.App/Train.Engine/Train.Web/Tests |
| B-3 | M0 | 集中包版本与构建属性 | 完成 | net10、Avalonia 12.1.5、Material 3.21.0 |
| B-4 | M0 | 两个 exe 引导 + Material 主题 | 完成 | 启动验证通过 |
| B-5 | M0 | DI、配置、日志基础设施 | 完成 | 配置 + NLog（异步文件/调试器/界面缓冲） |
| S-0 | M1 | `IBlobStore` + `LocalBlobStore` 存储后端 | 完成 | file:// 先行；`BlobStoreRegistry` 按 scheme 分发，s3:// 留工厂接口 |
| S-1 | M1 | Core 模型迁移（文档/形状/图像/坐标） | 完成 | AnnotationDocument/Shape/ImageRef/BoxConvert |
| S-2 | M1 | 标注存储接口与 codec 接口 | 完成 | 单图 / 数据集级分开；`CodecAnnotationStore` 只依赖 IBlobStore |
| S-3 | M1 | YOLO txt / LabelMe codec（VOC 本期不做） | 完成 | codec 在 `Core/Annotation/Formats`，标注端与训练端共用；VOC 仅探测 |
| S-4 | M1 | COCO 数据集级 codec | 完成 | `CocoDatasetCodec` + `CocoDatasetStore` |
| S-5 | M1 | 数据集探测接口 + YOLO/COCO 实现 | 完成 | 三形态探测 + yolo/coco Provider + `DatasetStoreResolver` 统一打开 |
| S-6 | M1 | 坐标换算与往返精度单测 | 进行中 | `BoxConvert` 已就绪，codec 往返有覆盖；独立坐标用例待补 |
| S-7 | M1 | `tinylabel` 形态（DB provider + DatabaseAnnotationStore） | 完成 | 表结构与 TinyLabel 最新版对齐（**主键自增 long**）；`images.Split` 缺失时自动补列；多项目库需指定 projectId |
| S-8 | M1 | 形态转换（DB→YOLO/COCO、YOLO↔COCO） | 进行中 | DB→YOLO / RF-DETR 已通并有单测；YOLO↔COCO、附加脚本产物待补 |
| S-9 | M1 | 自动检测 + 项目类型落库（Format/DescriptorPath/DetectedAt） | 进行中 | 探测与歧义选择已就绪；项目表落库在 M3 接入 |
| S-10 | M1 | 端到端模拟流程（生成图 → 导入标注 → 训练 → 导出 → 验证） | 完成 | `Sevencat.Train.App --simulate <dir>`；训练阶段是干跑，M4 换 `IDatasetTrainingRunner` 实现 |
| S-11 | M1 | 反向导入：现有 YOLO / RF-DETR 数据集 → 标注项目（图片 + 标注 + train/val） | 完成 | `DatasetImportService`（Core）；标注端 `LabelDatasetImportService` + `--import` CLI + 主窗口导入面板 |
| S-12 | M1 | 项目浏览：打开项目、按 split 列图、预览与叠框 | 完成 | `LabelProjectBrowser`（读库即关库）；`Label.App --open <目录>` 直接打开，导入后自动打开并选中第一张 |
| L-1 | M2 | 画布迁移（AnnotationCanvas/LabelPalette） | 未开始 | 接 ImageFacade |
| L-2 | M2 | 主窗口 VM 与视图迁移 | 未开始 | 去掉远端逻辑 |
| L-3 | M2 | 撤销重做 + 原子保存 | 未开始 | |
| L-4 | M2 | 数据集浏览器（虚拟化+缩略图） | 未开始 | |
| W-1 | M3 | ServerHost 类库化 | 未开始 | 由 Label.Avalonia 启动 |
| W-2 | M3 | 项目/图片/统计 API 迁移 | 未开始 | 走 Core 接口 |
| W-3 | M3 | 内嵌页面与导出 | 未开始 | gzip 静态资源 |
| W-4 | M3 | 桌面端服务面板 | 未开始 | 端口/状态/打开浏览器 |
| W-5 | M3 | 项目管理：格式字段、自动检测、重新检测 | 未开始 | 与 S-9 共用探测实现 |
| E-1 | M4 | Core/NN/Models/IO/Export 迁移 | 未开始 | 改命名空间 |
| E-2 | M4 | Training（Trainer/Checkpoint/EMA）迁移 | 未开始 | |
| E-3 | M4 | Data 层改造为 IDatasetStore | 未开始 | 关键任务 |
| E-4 | M4 | 模型结构与导出单测迁移 | 未开始 | 参数数量/键名/ONNX |
| T-1 | M5 | 训练会话（YOLO/DETR）迁移 | 未开始 | |
| T-2 | M5 | 任务队列与启停/续训 | 未开始 | 单任务独占 GPU |
| T-3 | M5 | 参数表单与预设 | 未开始 | |
| T-4 | M5 | 日志/指标曲线 | 未开始 | |
| T-5 | M5 | 产物管理（safetensors/onnx） | 未开始 | |
| D-1 | M6 | Predictor 迁移与单图推理 | 未开始 | |
| D-2 | M6 | 目录/摄像头推理与可视化 | 未开始 | |
| D-3 | M6 | GT 对比与漏检误检统计 | 未开始 | |
| D-4 | M6 | 验证集批量评估 | 未开始 | |
| TW-1 | M7 | 训练 Web API（任务/日志 SSE/指标） | 未开始 | |
| TW-2 | M7 | 内嵌页面（列表/详情/曲线） | 未开始 | |
| TW-3 | M7 | 鉴权与产物下载 | 未开始 | |
| P-1 | M8 | 打包与发布 | 未开始 | 单文件/安装包 |
| P-2 | M8 | 性能与异常兜底 | 未开始 | |

## 日志

| 日期 | 内容 | 影响 |
| --- | --- | --- |
| 2026-11-12 | 初始化文档：README、需求、计划、进度、杂项 | 项目立项，尚无代码 |
| 2026-11-12 | 程序集统一 `Sevencat.` 前缀；Web 改为宿主内嵌；图像栈定为 `Sevencat.ImageFacade(.SkiaSharp)` 最新版 | 由单宿主改为模块化 |
| 2026-11-12 | 确定代码来源：标注参考 TinyLabel、训练参考 OmniTensorSharp，可直接复制 | 训练路线改为纯 C#（TorchSharp），删除 Python 依赖 |
| 2026-11-12 | 拆成两个解决方案 `Sevencat.Label.sln` / `Sevencat.Train.sln`，共享 `src/Sevencat.Core` | 项目数 7；检测不单独建项目 |
| 2026-11-12 | 统一存储模型：图片/标注/描述都走 `IBlobStore`（file:// 先行，s3:// 预留），两端共用 | M1 增加 S-0；禁止业务层直接 File IO |
| 2026-11-12 | 明确四种数据集形态：yolo / coco(RF-DETR) / tinylabel(目录+SQLite) / flat，与存储后端正交 | M1 增加 S-7、S-8；训练端需直接吃 tinylabel |
| 2026-11-12 | `tinylabel` 的 train/val 改为 `images.Split` 字段表示；VOC codec 只留接口不实现 | 需给 TinyLabel 表结构加列（可空，SyncStructure 自动加） |
| 2026-11-12 | 增加自动格式检测与项目类型落库（projects.Format/DescriptorPath/DetectedAt），VOC 可识别只读 | M1 加 S-9；M3 加 W-5 |
| 2026-11-12 | M0 完成：两个 sln 各 5 项目、Avalonia 12.1.5 + Material 3.21.0、DI/配置/日志，两 exe 启动并写日志 | 可进入 M1 存储抽象 |
| 2026-11-12 | 统一可执行程序命名：`Sevencat.Label.Avalonia` → `Sevencat.Label.App`，`Sevencat.Train` → `Sevencat.Train.App` | 程序集/命名空间/引用/sln 同步更新 |
| 2026-11-12 | 日志后端改为 NLog：异步文件（按天归档、保留 14 天）+ DEBUG 调试器 + `LogBufferTarget` 供界面面板；`nlog.config` 放用户配置目录可覆盖默认规则 | 删除自研 `AppLogger`/`AppLoggerProvider`；DI 内 `AddNLog()` |
| 2026-11-12 | 数据集格式探测落地：按 `StorageLocation` 取 `IBlobStore`，只读文件头识别 yolo/coco/voc/tinylabel/flat，多命中返回 `Ambiguous` | 新增探测单测；平铺同名判定改为哈希索引，避免 O(n²) |
| 2026-11-12 | 数据集链路串起来：`AddAnnotrainDatasets()` 注册存储/图像信息源/探测/三个 Provider/`DatasetStoreResolver`；新增平铺形态 `FlatDatasetStore`；探测结果带 `Candidates`，歧义选定后取该形态自己的入口文件 | Label/Flat 复用 Label 侧 codec（`AddAnnotrainLabel()`）；新增 4 个打开链路单测 |
| 2026-11-12 | 新增端到端模拟流程：`SyntheticImageGenerator`（纯托管 RGB 画布 + 自写 PNG 编码，画正方形/三角形/圆形并输出真值）→ `SyntheticLabelingSession`（平铺图导入成项目、写标注，±2px 抖动模拟手标）→ `SummaryTrainingRunner`（干跑：全量装载 + 越界校验 + 摘要 JSON）→ 导出 YOLO 再从导出目录训练一遍 → 用真值反查 | 命令行 `Sevencat.Train.App --simulate <dir> [--count/--width/--height/--no-export]`，实测 6 张图/11 框通过，最大偏差 2.04px；新增 `HeaderImageInfoSource`（PNG/JPEG/BMP/GIF 头解析）作为 `BlobImageInfoSource` 兜底，训练端无 SkiaSharp 也能算归一化坐标 |
| 2026-11-12 | 重新核对 `F:\code\TinyLabel` 最新库结构并订正实现：主键全部改为**自增 long**（projects/images/project_classes 是 identity，annotations.ImageId 为 long 主键），`CreatedBy/LockedBy/AssigneeId/RemarkBy/UpdatedBy` 都是 long 用户 Id；`images.State` 取值 pending/labeled/reviewed/excluded/skipped；users/project_members/settings/autolabel_schemes 属于服务端，桌面端不建模 | 之前按字符串主键写的那套会打不开既有库；测试造库改用 `ExecuteIdentity()` |
| 2026-11-12 | 落地导出：`DatasetExportService` + `YoloDatasetExporter` / `CocoDatasetExporter`，产物对齐 TinyLabel（`data.yaml` 写绝对 `path`、`classes.txt`、RF-DETR 用 `train`/`valid` 目录），拒绝写回源根，报告含跳过/丢弃计数 | 新增 3 个导出用例（YOLO、RF-DETR、同根拒绝） |
| 2026-11-12 | 落地 `tinylabel` 形态：实体（projects/images/annotations/project_classes）+ `TinyLabelDatabase`（WAL、缺 `images.Split` 自动补列）+ `TinyLabelDatasetStore(Provider)`（split 取 `images.Split`，类别取 `project_classes.OrderIndex`，标注取 `annotations.DocJson` 过 labelme codec）+ `DatabaseAnnotationStore`（乐观锁走 `images.Revision`）；`IDatasetStoreProvider` 增加按 `datasetId` 打开的重载 | 标注端唯一可打开形态已通；多项目库抛 `DatasetProjectSelectionException`，也可显式传 projectId；新增 3 个 DB 用例 |
| 2026-11-12 | 文档补齐三种形态的存储布局（YOLO 的 `data.yaml` + images/labels、RF-DETR 的 `_annotations.coco.json` 字段语义、tinylabel 四张表），并把原「自动检测」小节顺延为 5.2 | 存储约定先于实现固定，S-7 按此落库 |
| 2026-11-12 | 形态收敛为三种：**标准 YOLO / 标准 RF-DETR(COCO) / 图片目录+SQLite**。删除平铺 `FlatDatasetStore` 与 VOC 相关代码/测试/枚举值；标注端只打开 `tinylabel`，YOLO/COCO 仅作导出目标（`LabelDatasetPolicy`），训练端三种都能读 | `DatasetFormat` 只剩 Yolo/Coco/TinyLabelDb；S-7 成为标注端唯一的打开路径，优先级提到最前 |
| 2026-11-12 | `YoloTxtCodec`/`LabelmeCodec`/路径规则从 `Sevencat.Label` 移到 `Core/Annotation/Formats`，由 `AddAnnotrainDatasets()` 注册，`AddAnnotrainLabel()` 删除；VOC 本期不做 | 修掉训练端平铺数据集「静默无框」：Train 侧只依赖 Core 也能解标注；新增训练侧平铺用例 |
| 2026-11-12 | README 加 banner 与作者图；`docs/image_ico.png` 生成 `assets/app.ico`（16–256 共 7 档）+ `assets/app.png`，两个 exe 用 `ApplicationIcon`、窗口用 AvaloniaResource | `UseWin32()` 不带渲染/排版后端，必须同时 `UseSkia().UseHarfBuzz()`，否则启动即崩 |
| 2026-11-12 | 平台收紧为 Windows x64：`Directory.Build.props` 固定 `RuntimeIdentifier=win-x64`、`PlatformTarget=x64`；Avalonia 由 `UsePlatformDetect()` 改 `UseWin32()`；`PlatformGuard` 在 `Main` 最早期校验非 win-x64 弹窗退出 | 产出目录变为 `bin/<cfg>/net10.0/win-x64`，不再带 `.so`；F-6.4 改为只支持 Windows |
| 2026-11-12 | 新增反向导入：`DatasetImportService` 读现有 YOLO（`data.yaml`）或 RF-DETR（扫 `train/valid/test` 下的 COCO json）数据集，图片复制到 `projects/{Id}/{split}/`，标注转 labelme JSON，split 落 `images.Split`；`valid`→`val`，多 split 类别取并集并重映射 classId，报告含每个 split 的图数与告警 | 标注端不再只能导出：`LabelDatasetImportService` 做策略校验（只允许 YOLO / COCO 作来源），新增 `--import` 命令行与主窗口「从现有数据集导入」面板；实测把 `--simulate` 导出的 6 图 / 11 框导回项目库，得到 train 5 / val 1、3 类，计数与源一致 |
| 2026-11-12 | 标注端补上「打开项目 + 浏览」：`LabelProjectBrowser` 用 `DatasetStoreResolver` 打开 tinylabel 后一次性装载图片清单即关库（`ImageRef` 保留，字节按需读），界面左侧按 split 列图、右侧预览并用 `Viewbox + Canvas` 按原图像素坐标叠框；导入完成后自动打开目标项目并选中第一张 | 新增 `--open <项目目录>` 直接进项目；VM 的 `SelectedImage` 统一驱动预览（用户点击与程序预设同一路径）；实测打开导入项目：train 5 / val 1，首图 320×240 / 2 框，位图解码成功 |
