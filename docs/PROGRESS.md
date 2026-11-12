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
| M4 训练引擎 | Train | 进行中 | 2026-11-12 | | 代码已迁入，数据层接口化首版完成 |
| M5 训练桌面 | Train | 进行中 | 2026-11-12 | | 单页训练已能跑真训练，队列/产物管理未做 |
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
| S-10 | M1 | 端到端模拟流程（生成图 → 导入标注 → 训练 → 导出 → 验证） | 完成 | `Sevencat.Train.App --simulate <dir>`；模拟流程仍用干跑实现（`SummaryTrainingRunner`），真训练引擎由 `AddAnnotrainTrainingEngine()` 覆盖注册 |
| S-11 | M1 | 反向导入：现有 YOLO / RF-DETR 数据集 → 标注项目（图片 + 标注 + train/val） | 完成 | `DatasetImportService`（Core）；标注端 `LabelDatasetImportService` + `--import` CLI + 主窗口导入面板 |
| S-12 | M1 | 项目管理 + 项目明细：左栏项目列表、右栏图片表格 | 完成 | `TinyLabelProjectCatalog` 列项目与统计；`LabelProjectBrowser` 按项目 Id 装载明细；导入目标固定为当前数据目录 |
| S-13 | M2 | 单图标注对话框（画布 + 保存） | 完成 | `AnnotationCanvas`（矩形/选中/拖动/删除/缩放/适应窗口）+ `AnnotationWindow`；保存走 `DatabaseAnnotationStore`（乐观锁） |
| S-14 | M1 | 新建项目（库里插一行，不建库） | 完成 | `TinyLabelProjectService.CreateAsync` 走 `TinyLabelWorkspace.RequireDatabase`：缺库直接报错；无库时界面先确认「新建数据库」再建项目 |
| S-15 | M2 | AI 标注：ONNX(YOLO) 推理 + 候选确认 | 完成 | `YoloOnnxRunner` / `YoloOutputDecoder`（Core，letterbox + NMS + 类别映射）+ `AutoLabelOptionsWindow`；候选虚线显示，接受全部 / ≥0.5 / 丢弃 |
| S-16 | M2 | 项目编辑 / 删除 / 结束（归档） | 完成 | `TinyLabelProjectService.UpdateAsync/DeleteAsync/SetClosedAsync`；项目列表按「未结束优先 + 创建时间倒序」 |
| S-17 | M2 | 图片清单升级：分页 + 状态 / 版本 / 备注列 | 完成 | `TinyLabelImageService.QueryAsync`（每页 50 张）+ 运行时表格；保存标注自动置 `labeled` |
| S-18 | M2 | 图片入库：导入图片 / 导入文件夹 / 扫描目录 | 完成 | `TinyLabelImageService.ImportFilesAsync/ImportFolderAsync`（sha256 去重、复制或只登记） |
| S-19 | M2 | 类别管理（追加 / 改名 / 受保护的删除） | 完成 | `TinyLabelClassService` + `ClassEditorWindow`；`OrderIndex` 即 YOLO classId，不重排 |
| S-20 | M2 | 数据根目录与配置入库 | 完成 | `AppDataPaths`（`exe/data`，`--data` 覆盖）+ `SettingsStore` 改存 `data/settings.db`（迁移旧 JSON） |
| S-21 | M2 | 多语言（中文 / English） | 完成 | `Localizer`（Core，双字典 + 索引器通知）+ 两端 `LocExtension`；右上角下拉切换，语言存 `settings.db`；Core 异常消息与数据集形态名也走同一张表（`Text.Get/Format`） |
| L-1 | M2 | 画布迁移（AnnotationCanvas/LabelPalette） | 进行中 | 已落自写精简版（矩形 / 选中 / 拖动 / 删除 / 缩放）；未接 ImageFacade，多边形/圆/线待补 |
| L-2 | M2 | 主窗口 VM 与视图迁移 | 进行中 | 按 TinyLabel 服务端首页重排：左项目管理 + 右概览/工具栏/表格/分页；缩略图与设置页未做 |
| L-3 | M2 | 撤销重做 + 原子保存 | 未开始 | 保存已走 `DatabaseAnnotationStore` 乐观锁，撤销未做 |
| L-4 | M2 | 数据集浏览器（虚拟化+缩略图） | 进行中 | 项目列表 + 图片表格已通；虚拟化 / 缩略图未做 |
| W-1 | M3 | ServerHost 类库化 | 未开始 | 由 Label.Avalonia 启动 |
| W-2 | M3 | 项目/图片/统计 API 迁移 | 未开始 | 走 Core 接口 |
| W-3 | M3 | 内嵌页面与导出 | 未开始 | gzip 静态资源 |
| W-4 | M3 | 桌面端服务面板 | 未开始 | 端口/状态/打开浏览器 |
| W-5 | M3 | 项目管理：格式字段、自动检测、重新检测 | 未开始 | 与 S-9 共用探测实现 |
| T-1 | M5 | 训练主界面（单页：工具栏 / 曲线 / 状态） | 完成 | `TrainingChart` 自绘 loss + mAP；干跑阶段图表显示占位提示 |
| T-2 | M6 | 检测验证对话框（选图 → ONNX 推理 → 叠加显示） | 完成 | 复用 `YoloOnnxRunner`；模型路径记入 `AppSettings`，从工具栏进入 |
| E-1 | M4 | Core/NN/Models/IO/Export 迁移 | 完成 | 全量复制到 `Sevencat.Train.Engine`（Core/Data/NN/Models/IO/Export/Training/Sessions），命名空间改为 `Sevencat.Train.Engine.*` |
| E-2 | M4 | Training（Trainer/Checkpoint/EMA）迁移 | 完成 | Trainer/EMA/Checkpoint/CosineSchedule/损失/指标全部就位 |
| E-3 | M4 | Data 层改造为 IDatasetStore | 进行中 | 新增 `YoloDatasetTrainingRunner`：样本走 `IDatasetStore`、图片字节走 `IBlobStore`；Path 版 `YoloDataset`/`CocoDataset` 仍保留给 Sessions 参考实现 |
| E-4 | M4 | 模型结构与导出单测迁移 | 未开始 | 参数数量/键名/ONNX |
| T-1 | M5 | 训练会话（YOLO/DETR）迁移 | 完成 | 代码已迁入 `Sevencat.Train.Engine/Sessions`；界面走 runner 而非直接持有会话 |
| T-2 | M5 | 任务队列与启停/续训 | 未开始 | 单任务独占 GPU |
| T-3 | M5 | 参数表单与预设 | 进行中 | epochs/batch/imgsz/输出目录已接真训练；模型规模/家族/学习率/设备未上界面 |
| T-4 | M5 | 日志/指标曲线 | 完成 | `TrainingRunOptions.Progress/Log` → 曲线按 epoch 追加，日志回填面板 |
| T-5 | M5 | 产物管理（safetensors/onnx） | 进行中 | 训练产出 best/model/model-ema/optimizer.bin/train.json；界面未做登记与打开目录 |
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
| 2026-11-12 | 训练端改为单页布局：工具栏（数据集 / epochs·batch·imgsz / 输出目录 / 开始·停止 / 检测）、中间自绘 `TrainingChart`（loss + mAP，无数据时占位提示）、底部进度条 + 状态 + 日志；检测验证收进工具栏：选图 → `YoloOnnxRunner` 推理 → `DetectWindow` 把框叠在图上并列出类别与置信度 | 不照抄 OmniTensorSharp 的多 tab；`--open <数据集>` 启动即加载；实测 YOLO 数据集打开正常，`--simulate` 干跑仍为 11 框通过 |
| 2026-11-12 | 补齐标注端缺的功能：新建空项目（`TinyLabelProjectService` + `ProjectEditorWindow`）、标注窗口「保存并继续」自动领下一张待标注图、AI 标注（`Microsoft.ML.OnnxRuntime` 加载 YOLO `.onnx`，letterbox → 解码 → NMS → 类别映射，候选以黄色虚线显示，接受全部 / ≥0.5 / 丢弃）；模型路径与置信度写入 `AppSettings`，启动自动回到上次数据目录 | 对齐 TinyLabel 的「AI 方案设置 + AI 预标注 + 候选确认」；`YoloOutputDecoder` 5 个单测覆盖坐标反变换、阈值、同类别 NMS、类别映射、转置输出；实测 4 图项目打开标注窗口稳定 |
| 2026-11-12 | 标注端改为「左项目管理 / 右项目明细」两栏：`TinyLabelProjectCatalog` 直接读库列出项目（图数 / 已标数 / split），`LabelProjectBrowser` 按项目 Id 装载；右侧图片表格每行带「标注」按钮，顶部「开始标注」作用于选中行；新增自写精简画布 `AnnotationCanvas`（矩形 / 选中 / 拖动 / 删除 / 滚轮缩放 / 适应窗口）与 `AnnotationWindow` 标注对话框，保存走 `DatabaseAnnotationStore` | 交互对齐 TinyLabel 的「列表 → 点进一张 → 标完关闭」；导入按钮改为弹窗，目标固定当前数据目录；新增 `--annotate <数据目录>` 直接进首图标注；实测 4 图项目打开标注窗口进程稳定、画布渲染正常 |
| 2026-11-12 | 标注端补上「打开项目 + 浏览」：`LabelProjectBrowser` 用 `DatasetStoreResolver` 打开 tinylabel 后一次性装载图片清单即关库（`ImageRef` 保留，字节按需读），界面左侧按 split 列图、右侧预览并用 `Viewbox + Canvas` 按原图像素坐标叠框；导入完成后自动打开目标项目并选中第一张 | 新增 `--open <项目目录>` 直接进项目；VM 的 `SelectedImage` 统一驱动预览（用户点击与程序预设同一路径）；实测打开导入项目：train 5 / val 1，首图 320×240 / 2 框，位图解码成功 |
| 2026-11-12 | 澄清「新建」语义 + 多语言：`TinyLabelWorkspace` 区分数据目录初始化与新建项目（`CreateAsync` 不再建库，缺库抛错；打开目录也不建库，无库时点新建先弹「新建数据库」确认）；新增 `Localizer`（中英双字典、索引器通知、语言存 `settings.db`）+ 两端 `LocExtension`，标注端全部界面文案（主窗/项目/导入/导出/类别/标注/AI 设置）与训练端工具栏/状态切到本地化 | UI 自动化实测：切换到 English 后整屏英文（`Annotrain Labeler` / `Open data folder…` / `No annotrain.db in this folder…`），切回中文正常；无库目录启动后 `annotrain.db` 不生成，点「新建项目」弹出「新建数据库」确认，确认后建库并进入项目对话框；新增本地化完整性与「新建不建库」测试，Label 测试 54/54 通过 |
| 2026-11-12 | 标注主界面按 TinyLabel 服务端首页重排（左项目管理 / 右项目概览 + 图片清单 + 分页），工具栏顺序为 导入图片… → 导入数据集… → 导入文件夹… → 扫描目录… → 导出数据集… → 打开目录 → 类别管理；补齐后端：项目 编辑/删除/结束、图片分页查询与状态/版本/备注、导入图片/文件夹/扫描目录（sha256 去重）、类别追加/改名/受保护删除；保存标注自动置 `labeled` | 数据根目录改为 `exe/data`（`--data` 可覆盖），配置从 JSON 迁到 `data/settings.db`（启动自动迁移旧配置），日志移到 `data/logs`；UI 自动化实测：新建项目 → 列表出现；导入 YOLO 数据集 → 表格 3 行（路径/已标注/已标注/rev 1/—）+ 分页「第 1 页 / 共 1 页」+ 底部库路径；Label 测试 51/51、Train 测试 3/3 通过 |
| 2026-11-12 | 修「新建」不生效：根因是未打开数据目录时 `OnCreateProject` 只写一行不显眼的状态就返回（标注端上次数据目录又被训练端覆盖导致启动后没有工作区）。改为「新建/导入」先就地选目录并打开再继续；`OpenLastWorkspaceAsync` 对目录不存在给出明确提示；`AppSettings` 拆出 `LastTrainDataDirectory`，训练端不再覆盖标注端的 `LastDataDirectory` | UI 自动化实测：有工作区时新建 → 对话框 → 创建 → 列表出现并选中新项目（同一工作区连续新建 2 个均正常）；无工作区时点击新建 → 弹出目录选择；新增 `ProjectCreateTests` 覆盖「建库 → 探测 → 浏览器打开」数据链路，Label 测试 49/49 通过 |
| 2026-11-12 | 训练引擎落地：OmniTensorSharp 的 Core/Data/NN/Models/IO/Export/Training/Train.App 训练会话共 97 个文件复制到 `Sevencat.Train.Engine`（`Core|Data|NN|Models|IO|Export|Training|Sessions`），命名空间统一为 `Sevencat.Train.Engine.*`；新增 `YoloDatasetTrainingRunner` 把 `IDatasetStore` 样本 → 归一化标签 → 批次 → 训练循环，图片字节走 `IBlobStore.OpenReadAsync`，产出 best/model/model-ema/optimizer.bin/train.json；图像后端统一由 exe 入口注册（`ImageRuntime.Use(new SkiaBackend())`），引擎只依赖 ImageFacade 抽象、不引任何编解码实现 | 训练程序 `AddAnnotrainTrainingEngine()` 替换干跑实现；工具栏 epochs/batch/imgsz 生效，`TrainingProgress` 驱动曲线、日志回填面板；新增 1 个真训练冒烟测试（4 图 / 1 epoch / 64px，约 1s 通过），测试进程同样注册 SkiaSharp |
| 2026-11-12 | Core 抛错文案接多语言：新增 `Text`（`Localizer` 门面，`Get` / `Format`）与 47 条 `Error.*`、5 条 `Dataset.Format.*` 文案；25 个 Core 文件的 56 处中文异常消息（存储越界/后端未注册、模型缺失、标注与 COCO codec、TinyLabel 工作区/项目/图片/类别/库、导入导出、数据集探测歧义与不支持的形态）改为查表；项目名与类别名的校验文案复用界面的 `Project.NameRequired/NameTooLong`、`Classes.NameRequired`（后者统一为「类别名不能为空」） | 英文界面里「导入失败：{0}」「打开数据集失败：{0}」不会再夹半句中文；新增 `ExceptionMessages_FollowCurrentLanguage` 用例，Label 测试 55/55、Train 测试 3/3 通过；仍未本地化的只剩日志模板、导入/导出 `Summary` 与探测结论文案 |
