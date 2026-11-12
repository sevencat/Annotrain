using System.ComponentModel;
using System.Globalization;

namespace Sevencat.Core.Localization;

/// <summary>可选语言。</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// 进程级文案表（中 / 英）。界面用 XAML 的 <c>Loc</c> 扩展绑定 <see cref="Localizer"/> 的索引器，
/// 切换语言时发 <c>Item[]</c> 变更通知，所有绑定自动刷新；代码里用 <see cref="Format"/> 拼带参数的文案。
/// <para>
/// 这里不用 resx：字典是编译期常量，AOT/裁剪友好，也不会因为缺卫星程序集退回英文。
/// 缺键时按「当前语言 → 英文 → 键名」降级，界面上能一眼看出漏翻的键。
/// </para>
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>默认语言（跟随系统时取这个）。</summary>
    public const string DefaultLanguage = "zh";

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        // ── 通用 ──
        ["Common.Refresh"] = "刷新",
        ["Common.Cancel"] = "取消",
        ["Common.Create"] = "创建",
        ["Common.Save"] = "保存",
        ["Common.Delete"] = "删除",
        ["Common.Close"] = "关闭",
        ["Common.Confirm"] = "确定",
        ["Common.Browse"] = "浏览…",
        ["Common.Language"] = "语言",

        // ── 标注主界面 ──
        ["Label.Title"] = "Annotrain 标注",
        ["Label.Running"] = "运行中",
        ["Label.OpenDataDir"] = "打开数据目录…",
        ["Label.PickDataDir"] = "选择数据目录（放 annotrain.db 的目录）",
        ["Label.Projects"] = "项目管理",
        ["Label.ProjectsSummary"] = "共 {0} 个项目 · {1} 个进行中",
        ["Label.NewProject"] = "新建项目",
        ["Label.EditProject"] = "编辑项目",
        ["Label.DeleteProject"] = "删除项目",
        ["Label.ToggleClosed"] = "结束项目 / 重新打开",
        ["Label.NoProject"] = "未选择项目",
        ["Label.InProgress"] = "进行中",
        ["Label.Closed"] = "已结束",
        ["Label.ProjectStats"] = "图片 {0} 张 · 已处理 {1} 张 · 待处理 {2} 张",
        ["Label.ImportImages"] = "导入图片…",
        ["Label.ImportDataset"] = "导入数据集…",
        ["Label.ImportFolder"] = "导入文件夹…",
        ["Label.ScanFolder"] = "扫描目录…",
        ["Label.ExportDataset"] = "导出数据集…",
        ["Label.OpenFolder"] = "打开目录",
        ["Label.ManageClasses"] = "类别管理 ({0})",
        ["Label.StartAnnotation"] = "开始标注",
        ["Label.ColumnPath"] = "路径",
        ["Label.ColumnLabel"] = "标注",
        ["Label.ColumnState"] = "状态",
        ["Label.ColumnRevision"] = "版本",
        ["Label.ColumnRemark"] = "备注",
        ["Label.ColumnActions"] = "操作",
        ["Label.Annotate"] = "标注",
        ["Label.PrevPage"] = "上一页",
        ["Label.NextPage"] = "下一页",
        ["Label.PageOf"] = "第 {0} 页 / 共 {1} 页",
        ["Label.TotalImages"] = "共 {0} 张",
        ["Label.Log"] = "日志",
        ["Label.NoProjectHint"] = "这个数据目录里还没有项目：用「新建项目」或「导入数据集…」建一个",
        ["Label.NoDatabase"] = "这个目录还没有 annotrain.db：先「导入数据集…」建库，或换一个数据目录",
        ["Label.PickDataDirFirst"] = "先选择数据目录：新项目会建在那个数据库里",
        ["Label.DataDir"] = "数据目录：{0}",
        ["Label.ProjectOpened"] = "项目「{0}」：{1} 张，当前第 {2}/{3} 页",
        ["Label.NoImages"] = "这个项目还没有图片：用「导入图片…」「导入文件夹…」或「扫描目录…」加进来",
        ["Label.ImageCount"] = "共 {0} 张",
        ["Label.PickRowFirst"] = "先选一行图片再标注",
        ["Label.PickProjectFirst"] = "先选择一个项目",
        ["Label.NoMorePending"] = "这一页没有待标注的图片了，可以翻下一页",
        ["Label.AnnotateFailed"] = "打开标注窗口失败：{0}",
        ["Label.ServicesUnavailable"] = "服务容器不可用，标注窗口打不开",
        ["Label.ProjectCreated"] = "已新建项目「{0}」",
        ["Label.ProjectCreateFailed"] = "新建项目失败：{0}",
        ["Label.ProjectSaved"] = "已保存项目「{0}」",
        ["Label.ProjectSaveFailed"] = "编辑项目失败：{0}",
        ["Label.ProjectDeleted"] = "已删除项目「{0}」",
        ["Label.ProjectDeleteFailed"] = "删除项目失败：{0}",
        ["Label.ProjectClosed"] = "已结束项目「{0}」",
        ["Label.ProjectReopened"] = "已重新打开项目「{0}」",
        ["Label.ProjectToggleFailed"] = "操作失败：{0}",
        ["Label.ProjectsLoadFailed"] = "读取项目列表失败：{0}",
        ["Label.LoadingProjects"] = "正在读取项目列表…",
        ["Label.OpenProjectFailed"] = "打开项目失败：{0}",
        ["Label.Importing"] = "正在导入…",
        ["Label.ImportFailed"] = "导入失败：{0}",
        ["Label.ImportingImages"] = "正在导入图片…",
        ["Label.ImportingFolder"] = "正在导入文件夹…",
        ["Label.ScanningFolder"] = "正在扫描目录…",
        ["Label.ImportImagesAction"] = "导入图片",
        ["Label.ImportFolderAction"] = "导入文件夹",
        ["Label.ScanFolderAction"] = "扫描目录",
        ["Label.Exporting"] = "正在导出数据集…",
        ["Label.ExportAction"] = "导出数据集",
        ["Label.ImportResult"] = "导入 {0} 张，跳过 {1} 张",
        ["Label.ImportDone"] = "导入完成：{0} 张图 / {1} 个类别",
        ["Label.ExportResult"] = "已导出 {0} 张图 / {1} 个框 → {2}",
        ["Label.Ready"] = "就绪",
        ["Label.ImportImagesTitle"] = "导入图片（可多选）",
        ["Label.ImportFolderTitle"] = "导入文件夹：图片会复制进项目目录",
        ["Label.ScanFolderTitle"] = "扫描目录：只登记原目录里的图片，不复制文件",
        ["Label.ImagesFileType"] = "图片",

        // ── 状态 ──
        ["State.Pending"] = "未标注",
        ["State.Labeled"] = "已标注",
        ["State.Reviewed"] = "已通过",
        ["State.Excluded"] = "已排除",
        ["State.Skipped"] = "已跳过",
        ["State.NoSplit"] = "未划分",
        ["State.Empty"] = "—",

        // ── 项目对话框 ──
        ["Project.NewTitle"] = "新建项目",
        ["Project.EditTitle"] = "编辑项目",
        ["Project.NewHint"] = "新项目建在当前数据目录的 annotrain.db 里；图片可以之后导入或扫描。",
        ["Project.EditHint"] = "改完保存即可；图片、标注与类别索引都不动。",
        ["Project.Name"] = "项目名",
        ["Project.NamePlaceholder"] = "例如：路面试跑 第一批",
        ["Project.Note"] = "备注",
        ["Project.NotePlaceholder"] = "可留空；导出 YOLO 的备注也会读这里",
        ["Project.NameRequired"] = "项目名不能为空",
        ["Project.NameTooLong"] = "项目名不能超过 {0} 个字符",

        // ── 删除确认 ──
        ["Confirm.DeleteTitle"] = "删除项目",
        ["Confirm.DeleteMessage"] = "确定删除「{0}」？图片、标注、类别都会一起删掉，不可恢复。",
        ["Confirm.NewDatabaseTitle"] = "新建数据库",
        ["Confirm.NewDatabaseMessage"] = "这个数据目录还没有 annotrain.db，要在这里建一个空库吗？（只建库，不建项目）",

        // ── 导出对话框 ──
        ["Export.Title"] = "导出数据集",
        ["Export.Hint"] = "把当前项目导出成训练能直接吃的形态；图片按 train/val 复制到目标目录。",
        ["Export.Format"] = "格式",
        ["Export.FormatYolo"] = "YOLO（data.yaml + images/labels）",
        ["Export.FormatCoco"] = "COCO（RF-DETR：_annotations.coco.json）",
        ["Export.Destination"] = "目标目录",
        ["Export.DestinationPlaceholder"] = "选一个空目录",
        ["Export.PickDestination"] = "先选目标目录",
        ["Export.PickTitle"] = "选择导出目录",

        // ── 类别管理 ──
        ["Classes.Title"] = "类别管理",
        ["Classes.Hint"] = "顺序就是导出 YOLO 的 classId，只能追加，不能重排；已有标注用到的类别不能删除。",
        ["Classes.Count"] = "共 {0} 个类别",
        ["Classes.Name"] = "类别名",
        ["Classes.NamePlaceholder"] = "类别名，例如：helmet",
        ["Classes.Add"] = "新增",
        ["Classes.Rename"] = "重命名",
        ["Classes.Remove"] = "删除",
        ["Classes.PickFirst"] = "先在列表里选一个类别",
        ["Classes.NameRequired"] = "类别名不能为空",
        ["Classes.Added"] = "已新增「{0}」（classId {1}）",
        ["Classes.Renamed"] = "已改名为「{0}」",
        ["Classes.Removed"] = "已删除「{0}」",
        ["Classes.LoadFailed"] = "读取类别失败：{0}",

        // ── 标注窗口 ──
        ["Annotate.Title"] = "标注",
        ["Annotate.Select"] = "选择",
        ["Annotate.Rectangle"] = "矩形",
        ["Annotate.FitWindow"] = "适应窗口",
        ["Annotate.Label"] = "标签",
        ["Annotate.AISettings"] = "AI 设置",
        ["Annotate.AIRun"] = "AI 标注",
        ["Annotate.DeleteSelected"] = "删除选中",
        ["Annotate.SaveAndNext"] = "保存并继续",
        ["Annotate.DeleteHint"] = "先点选一个框，再删除",
        ["Annotate.Hint"] = "{0} · {1}×{2} · {3} 个框 · 左键拖拽画框，选中后 Delete 删除 · AI 模型：{4}",
        ["Annotate.NoModel"] = "未设置 AI 模型",
        ["Annotate.LoadFailed"] = "加载失败：{0}",
        ["Annotate.SaveFailed"] = "保存失败：{0}",
        ["Annotate.AcceptAll"] = "接受全部",
        ["Annotate.AcceptConfident"] = "只接受 ≥{0}",
        ["Annotate.Discard"] = "丢弃",
        ["Annotate.Candidates"] = "AI 候选 {0} 个",
        ["Annotate.NoCandidatesHint"] = "没有候选：调低置信度再试，或确认模型类别与项目类别一致",
        ["Annotate.CandidateHint"] = "候选是黄色虚线框，确认后点「接受全部」或「只接受 ≥0.5」",
        ["Annotate.Accepted"] = "接受 {0} 个 AI 框，记得保存",
        ["Annotate.AIRunning"] = "AI 推理中…",
        ["Annotate.AINoModel"] = "先点「AI 设置」选一个 .onnx 模型",
        ["Annotate.AIFailed"] = "AI 标注失败：{0}",
        ["Annotate.SettingsSaveFailed"] = "设置保存失败：{0}",

        // ── AI 设置 ──
        ["AutoLabel.Title"] = "AI 标注设置",
        ["AutoLabel.Description"] = "用 YOLO 导出的 .onnx 模型做预标注。结果先作为候选框显示，确认后才落成正式标注。",
        ["AutoLabel.ModelLabel"] = "模型文件（.onnx）",
        ["AutoLabel.ModelPlaceholder"] = "训练导出的 best.onnx",
        ["AutoLabel.ConfidenceLabel"] = "置信度阈值（越低杂框越多）",
        ["AutoLabel.ClassHint"] = "模型输出的 class id 按项目类别顺序映射；类别数不一致时会显示为 class_N。",
        ["AutoLabel.PickTitle"] = "选择 ONNX 模型",
        ["AutoLabel.FileType"] = "ONNX 模型",
        ["AutoLabel.NeedModel"] = "请选择 .onnx 模型文件",
        ["AutoLabel.OnnxOnly"] = "只支持 .onnx 模型",

        // ── 导入数据集 ──
        ["Import.Title"] = "导入数据集",
        ["Import.Header"] = "把现有的 YOLO / RF-DETR 数据集导入当前数据目录",
        ["Import.Target"] = "目标：{0}（导入成一个新项目）",
        ["Import.Source"] = "源数据集目录",
        ["Import.SourcePlaceholder"] = "含 data.yaml 或 COCO json 的目录",
        ["Import.Format"] = "形态",
        ["Import.Auto"] = "自动识别",
        ["Import.ProjectName"] = "项目名",
        ["Import.NamePlaceholder"] = "留空取源目录名",
        ["Import.Action"] = "导入",
        ["Import.PickTitle"] = "选择数据集目录",
        ["Import.NeedSource"] = "请先选择源数据集目录",
        ["Import.Ambiguous"] = "这个目录同时符合多种格式，请在上面选一下形态",
        ["Import.NotImportable"] = "「{0}」不能作为导入来源，请选 YOLO 或 RF-DETR",
        ["Import.DetectFailed"] = "识别失败：{0}",

        // ── 训练端 ──
        ["Train.Title"] = "Annotrain 训练",
        ["Train.OpenDataset"] = "打开数据集…",
        ["Train.Start"] = "开始训练",
        ["Train.Stop"] = "停止",
        ["Train.Detect"] = "检测…",
        ["Train.DetectTip"] = "选一张图片，用 ONNX 模型检测后弹窗显示叠加结果",
        ["Train.OutputDir"] = "输出目录",
        ["Train.Ready"] = "就绪",
        ["Train.NoDataset"] = "未选择数据集",
        ["Train.Opening"] = "正在打开数据集…",
        ["Train.Classes"] = "{0} 类",
        ["Train.DatasetStats"] = "{0} 张图 / {1} 个框",
        ["Train.DatasetOpened"] = "已打开数据集：{0}",
        ["Train.OpenFailed"] = "打开数据集失败：{0}",
        ["Train.NeedDataset"] = "先选择数据集",
        ["Train.AlreadyRunning"] = "训练已在进行",
        ["Train.Training"] = "训练中…",
        ["Train.Started"] = "开始训练：{0}",
        ["Train.Stopped"] = "训练已停止",
        ["Train.Failed"] = "训练失败：{0}",
        ["Train.Stopping"] = "正在停止…",
        ["Train.ChartRunning"] = "训练中：每个 epoch 结束后刷新 loss 与 mAP",
        ["Train.ChartIdle"] = "开始训练后显示 loss / mAP 曲线",

        // ── Core 抛错（异常消息也查同一张表，切语言后对话框里的原因跟着变）──
        ["Error.PathOutsideRoot"] = "路径越界：{0}",
        ["Error.StorageBackendUnknown"] = "未注册的存储后端：{0}//",
        ["Error.DirectoryUnknown"] = "无法确定目录：{0}",
        ["Error.NoTrainableSplit"] = "没有可训练的 split：去标注端给图片分配 train/val",
        ["Error.ImageBackendMissing"] = "尚未注册图像后端，请先调用 ImageRuntime.Use(...)",
        ["Error.ConcurrencyConflict"] = "标注已被修改：期望版本 {0}，实际版本 {1}",
        ["Error.ImageSizeUnknown"] = "图片尺寸未知，无法写 YOLO 标签",
        ["Error.LabelFileEmpty"] = "标注文件为空",
        ["Error.LabelFileInvalidJson"] = "标注文件不是合法的 JSON",
        ["Error.LabelFileNull"] = "标注文件内容为 null",
        ["Error.ImageNotInDatabase"] = "数据库里没有这张图：{0}",
        ["Error.YoloDescriptorMissing"] = "YOLO 数据集缺少 data.yaml",
        ["Error.YoloNamesMissing"] = "data.yaml 里没有 names：{0}",
        ["Error.CocoDescriptorMissing"] = "COCO 数据集缺少 json 描述文件",
        ["Error.CocoFileEmpty"] = "COCO 标注文件为空",
        ["Error.CocoInvalidJson"] = "COCO 标注文件不是合法 JSON",
        ["Error.CocoFileNull"] = "COCO 标注文件内容为 null",
        ["Error.DatabaseRequiresLocalFolder"] = "数据库形态只支持本地目录：SQLite 的 WAL 需要本地随机写",
        ["Error.DatabaseRequiresLocalFile"] = "数据库形态只支持本地 file:// 数据集根：SQLite 的 WAL 需要本地随机写",
        ["Error.DatabaseMissing"] = "数据目录里没有 {0}：先「打开数据目录…」选一个已有库，或用「导入数据集…」建库。",
        ["Error.MultiProject"] = "数据库里有 {0} 个项目，请先指定：{1}",
        ["Error.ProjectNotFound"] = "项目不存在：{0}",
        ["Error.FolderNotFound"] = "目录不存在：{0}",
        ["Error.ProjectIdNotNumeric"] = "项目 Id 必须是数字：{0}",
        ["Error.DetectMissingDbPath"] = "探测结果缺少 .db 文件路径",
        ["Error.DatabaseNoProject"] = "数据库里没有项目：{0}",
        ["Error.DatabaseNoProjectId"] = "数据库里没有项目 {0}",
        ["Error.DatabaseFileMissing"] = "数据库不存在：{0}",
        ["Error.InvalidDatabase"] = "不是有效的 TinyLabel 数据库（缺 images 表）：{0}",
        ["Error.ClassExists"] = "类别已存在：{0}",
        ["Error.ClassNotFound"] = "类别不存在：{0}",
        ["Error.ClassInUse"] = "类别「{0}」已经有标注在用，不能删除；可以改名。",
        ["Error.SourceHasNoImages"] = "源数据集没有可导入的图片：{0}",
        ["Error.SourceAlreadyImageDatabase"] = "源数据集已经是「图片目录 + 数据库」形态，直接打开即可，无需导入",
        ["Error.DescriptorMissingForFormat"] = "探测没有在 {0} 找到该形态的描述文件（{1}）",
        ["Error.ImportTargetNotLocal"] = "导入目标必须是本地目录：SQLite 的 WAL 需要本地随机写",
        ["Error.ImportTargetSameAsSource"] = "导入目标不能与源数据集根目录相同，否则会覆盖原数据",
        ["Error.FormatAmbiguous"] = "数据集同时命中 {0} 种格式（{1}），请先指定形态",
        ["Error.NoExporter"] = "没有可用的导出器：{0}",
        ["Error.ExportTargetSameAsSource"] = "导出目标不能与源数据集根目录相同，请换一个目录",
        ["Error.ExportNoSplit"] = "源数据集没有可导出的 split（图片都没分配 train/val/test）",
        ["Error.ModelFileNotFound"] = "找不到模型文件",
        ["Error.FormatNotSupported"] = "暂不支持打开「{0}」形态的数据集{1}",
        ["Error.DetailSuffix"] = "：{0}",
        ["Error.ListSeparator"] = "、",

        // ── 数据集形态名（异常消息与探测结论里会拼进去）──
        ["Dataset.Format.Yolo"] = "YOLO 布局",
        ["Dataset.Format.Coco"] = "RF-DETR (COCO)",
        ["Dataset.Format.TinyLabelDb"] = "图片目录 + 数据库",
        ["Dataset.Format.Ambiguous"] = "多种格式",
        ["Dataset.Format.Unknown"] = "未识别",
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // ── Common ──
        ["Common.Refresh"] = "Refresh",
        ["Common.Cancel"] = "Cancel",
        ["Common.Create"] = "Create",
        ["Common.Save"] = "Save",
        ["Common.Delete"] = "Delete",
        ["Common.Close"] = "Close",
        ["Common.Confirm"] = "OK",
        ["Common.Browse"] = "Browse…",
        ["Common.Language"] = "Language",

        // ── Label main window ──
        ["Label.Title"] = "Annotrain Labeler",
        ["Label.Running"] = "Running",
        ["Label.OpenDataDir"] = "Open data folder…",
        ["Label.PickDataDir"] = "Pick the data folder (the one holding annotrain.db)",
        ["Label.Projects"] = "Projects",
        ["Label.ProjectsSummary"] = "{0} projects · {1} in progress",
        ["Label.NewProject"] = "New project",
        ["Label.EditProject"] = "Edit project",
        ["Label.DeleteProject"] = "Delete project",
        ["Label.ToggleClosed"] = "Close / reopen project",
        ["Label.NoProject"] = "No project selected",
        ["Label.InProgress"] = "In progress",
        ["Label.Closed"] = "Closed",
        ["Label.ProjectStats"] = "{0} images · {1} done · {2} pending",
        ["Label.ImportImages"] = "Import images…",
        ["Label.ImportDataset"] = "Import dataset…",
        ["Label.ImportFolder"] = "Import folder…",
        ["Label.ScanFolder"] = "Scan folder…",
        ["Label.ExportDataset"] = "Export dataset…",
        ["Label.OpenFolder"] = "Open folder",
        ["Label.ManageClasses"] = "Classes ({0})",
        ["Label.StartAnnotation"] = "Start labeling",
        ["Label.ColumnPath"] = "Path",
        ["Label.ColumnLabel"] = "Label",
        ["Label.ColumnState"] = "State",
        ["Label.ColumnRevision"] = "Rev",
        ["Label.ColumnRemark"] = "Note",
        ["Label.ColumnActions"] = "Actions",
        ["Label.Annotate"] = "Label",
        ["Label.PrevPage"] = "Previous",
        ["Label.NextPage"] = "Next",
        ["Label.PageOf"] = "Page {0} of {1}",
        ["Label.TotalImages"] = "{0} images",
        ["Label.Log"] = "Log",
        ["Label.NoProjectHint"] = "No project in this data folder yet: use \"New project\" or \"Import dataset…\"",
        ["Label.NoDatabase"] = "No annotrain.db in this folder: import a dataset first, or pick another data folder",
        ["Label.PickDataDirFirst"] = "Pick a data folder first: the new project will be created in that database",
        ["Label.DataDir"] = "Data folder: {0}",
        ["Label.ProjectOpened"] = "{0}: {1} images, page {2}/{3}",
        ["Label.NoImages"] = "No image in this project yet: use \"Import images…\", \"Import folder…\" or \"Scan folder…\"",
        ["Label.ImageCount"] = "{0} images",
        ["Label.PickRowFirst"] = "Select an image row first",
        ["Label.PickProjectFirst"] = "Select a project first",
        ["Label.NoMorePending"] = "No pending image on this page, try the next page",
        ["Label.AnnotateFailed"] = "Cannot open the label window: {0}",
        ["Label.ServicesUnavailable"] = "Service container unavailable",
        ["Label.ProjectCreated"] = "Project \"{0}\" created",
        ["Label.ProjectCreateFailed"] = "Cannot create project: {0}",
        ["Label.ProjectSaved"] = "Project \"{0}\" saved",
        ["Label.ProjectSaveFailed"] = "Cannot save project: {0}",
        ["Label.ProjectDeleted"] = "Project \"{0}\" deleted",
        ["Label.ProjectDeleteFailed"] = "Cannot delete project: {0}",
        ["Label.ProjectClosed"] = "Project \"{0}\" closed",
        ["Label.ProjectReopened"] = "Project \"{0}\" reopened",
        ["Label.ProjectToggleFailed"] = "Operation failed: {0}",
        ["Label.ProjectsLoadFailed"] = "Cannot load projects: {0}",
        ["Label.LoadingProjects"] = "Loading projects…",
        ["Label.OpenProjectFailed"] = "Cannot open project: {0}",
        ["Label.Importing"] = "Importing…",
        ["Label.ImportFailed"] = "Import failed: {0}",
        ["Label.ImportingImages"] = "Importing images…",
        ["Label.ImportingFolder"] = "Importing folder…",
        ["Label.ScanningFolder"] = "Scanning folder…",
        ["Label.ImportImagesAction"] = "Import images",
        ["Label.ImportFolderAction"] = "Import folder",
        ["Label.ScanFolderAction"] = "Scan folder",
        ["Label.Exporting"] = "Exporting dataset…",
        ["Label.ExportAction"] = "Export dataset",
        ["Label.ImportResult"] = "Imported {0}, skipped {1}",
        ["Label.ImportDone"] = "Imported {0} images / {1} classes",
        ["Label.ExportResult"] = "Exported {0} images / {1} boxes → {2}",
        ["Label.Ready"] = "Ready",
        ["Label.ImportImagesTitle"] = "Import images (multi-select)",
        ["Label.ImportFolderTitle"] = "Import folder: images are copied into the project",
        ["Label.ScanFolderTitle"] = "Scan folder: register images in place, no copy",
        ["Label.ImagesFileType"] = "Images",

        // ── State ──
        ["State.Pending"] = "Unlabeled",
        ["State.Labeled"] = "Labeled",
        ["State.Reviewed"] = "Approved",
        ["State.Excluded"] = "Excluded",
        ["State.Skipped"] = "Skipped",
        ["State.NoSplit"] = "No split",
        ["State.Empty"] = "—",

        // ── Project dialog ──
        ["Project.NewTitle"] = "New project",
        ["Project.EditTitle"] = "Edit project",
        ["Project.NewHint"] = "The project is created inside annotrain.db of the current data folder; images can be imported later.",
        ["Project.EditHint"] = "Only the name and note change; images, labels and class ids stay untouched.",
        ["Project.Name"] = "Name",
        ["Project.NamePlaceholder"] = "e.g. Road trial batch 1",
        ["Project.Note"] = "Note",
        ["Project.NotePlaceholder"] = "Optional; also used as the YOLO export note",
        ["Project.NameRequired"] = "Name is required",
        ["Project.NameTooLong"] = "Name must be at most {0} characters",

        // ── Delete confirm ──
        ["Confirm.DeleteTitle"] = "Delete project",
        ["Confirm.DeleteMessage"] = "Delete \"{0}\"? Images, labels and classes are removed permanently.",
        ["Confirm.NewDatabaseTitle"] = "Create database",
        ["Confirm.NewDatabaseMessage"] = "This data folder has no annotrain.db yet. Create an empty database here? (no project is created)",

        // ── Export dialog ──
        ["Export.Title"] = "Export dataset",
        ["Export.Hint"] = "Export the project into a training-ready layout; images are copied into train/val.",
        ["Export.Format"] = "Format",
        ["Export.FormatYolo"] = "YOLO (data.yaml + images/labels)",
        ["Export.FormatCoco"] = "COCO (RF-DETR: _annotations.coco.json)",
        ["Export.Destination"] = "Destination folder",
        ["Export.DestinationPlaceholder"] = "Pick an empty folder",
        ["Export.PickDestination"] = "Pick a destination folder first",
        ["Export.PickTitle"] = "Pick the export folder",

        // ── Classes ──
        ["Classes.Title"] = "Classes",
        ["Classes.Hint"] = "Order is the YOLO classId: only append, never reorder; classes used by labels cannot be deleted.",
        ["Classes.Count"] = "{0} classes",
        ["Classes.Name"] = "Class name",
        ["Classes.NamePlaceholder"] = "Class name, e.g. helmet",
        ["Classes.Add"] = "Add",
        ["Classes.Rename"] = "Rename",
        ["Classes.Remove"] = "Delete",
        ["Classes.PickFirst"] = "Select a class first",
        ["Classes.NameRequired"] = "Enter a class name first",
        ["Classes.Added"] = "Added \"{0}\" (classId {1})",
        ["Classes.Renamed"] = "Renamed to \"{0}\"",
        ["Classes.Removed"] = "Deleted \"{0}\"",
        ["Classes.LoadFailed"] = "Cannot load classes: {0}",

        // ── Annotation window ──
        ["Annotate.Title"] = "Label",
        ["Annotate.Select"] = "Select",
        ["Annotate.Rectangle"] = "Rectangle",
        ["Annotate.FitWindow"] = "Fit",
        ["Annotate.Label"] = "Label",
        ["Annotate.AISettings"] = "AI settings",
        ["Annotate.AIRun"] = "AI label",
        ["Annotate.DeleteSelected"] = "Delete selected",
        ["Annotate.SaveAndNext"] = "Save and next",
        ["Annotate.DeleteHint"] = "Select a box first, then delete",
        ["Annotate.Hint"] = "{0} · {1}×{2} · {3} boxes · drag to draw, Delete to remove · AI model: {4}",
        ["Annotate.NoModel"] = "no model set",
        ["Annotate.LoadFailed"] = "Load failed: {0}",
        ["Annotate.SaveFailed"] = "Save failed: {0}",
        ["Annotate.AcceptAll"] = "Accept all",
        ["Annotate.AcceptConfident"] = "Accept ≥{0}",
        ["Annotate.Discard"] = "Discard",
        ["Annotate.Candidates"] = "{0} AI candidates",
        ["Annotate.NoCandidatesHint"] = "No candidate: lower the confidence, or make sure the model classes match the project classes",
        ["Annotate.CandidateHint"] = "Candidates are dashed yellow boxes; use \"Accept all\" or \"Accept ≥0.5\"",
        ["Annotate.Accepted"] = "Accepted {0} AI boxes, remember to save",
        ["Annotate.AIRunning"] = "Running AI inference…",
        ["Annotate.AINoModel"] = "Pick a .onnx model in \"AI settings\" first",
        ["Annotate.AIFailed"] = "AI labeling failed: {0}",
        ["Annotate.SettingsSaveFailed"] = "Cannot save settings: {0}",

        // ── AI settings ──
        ["AutoLabel.Title"] = "AI labeling settings",
        ["AutoLabel.Description"] = "Pre-label with a .onnx exported from YOLO. Results are shown as candidates and only become real boxes after confirmation.",
        ["AutoLabel.ModelLabel"] = "Model file (.onnx)",
        ["AutoLabel.ModelPlaceholder"] = "Exported best.onnx",
        ["AutoLabel.ConfidenceLabel"] = "Confidence threshold (lower = more boxes)",
        ["AutoLabel.ClassHint"] = "Model class ids map to the project class order; mismatched counts show as class_N.",
        ["AutoLabel.PickTitle"] = "Pick an ONNX model",
        ["AutoLabel.FileType"] = "ONNX model",
        ["AutoLabel.NeedModel"] = "Pick a .onnx model file",
        ["AutoLabel.OnnxOnly"] = "Only .onnx models are supported",

        // ── Import dataset ──
        ["Import.Title"] = "Import dataset",
        ["Import.Header"] = "Import an existing YOLO / RF-DETR dataset into the current data folder",
        ["Import.Target"] = "Target: {0} (imported as a new project)",
        ["Import.Source"] = "Source dataset folder",
        ["Import.SourcePlaceholder"] = "Folder with data.yaml or a COCO json",
        ["Import.Format"] = "Format",
        ["Import.Auto"] = "Auto detect",
        ["Import.ProjectName"] = "Project name",
        ["Import.NamePlaceholder"] = "Empty = source folder name",
        ["Import.Action"] = "Import",
        ["Import.PickTitle"] = "Pick the dataset folder",
        ["Import.NeedSource"] = "Pick the source dataset folder first",
        ["Import.Ambiguous"] = "This folder matches several formats; pick one above",
        ["Import.NotImportable"] = "\"{0}\" cannot be imported; choose YOLO or RF-DETR",
        ["Import.DetectFailed"] = "Detect failed: {0}",

        // ── Trainer ──
        ["Train.Title"] = "Annotrain Trainer",
        ["Train.OpenDataset"] = "Open dataset…",
        ["Train.Start"] = "Start training",
        ["Train.Stop"] = "Stop",
        ["Train.Detect"] = "Detect…",
        ["Train.DetectTip"] = "Pick an image, run the ONNX model and show the overlay",
        ["Train.OutputDir"] = "Output",
        ["Train.Ready"] = "Ready",
        ["Train.NoDataset"] = "No dataset selected",
        ["Train.Opening"] = "Opening dataset…",
        ["Train.Classes"] = "{0} classes",
        ["Train.DatasetStats"] = "{0} images / {1} boxes",
        ["Train.DatasetOpened"] = "Dataset opened: {0}",
        ["Train.OpenFailed"] = "Cannot open dataset: {0}",
        ["Train.NeedDataset"] = "Pick a dataset first",
        ["Train.AlreadyRunning"] = "Training is already running",
        ["Train.Training"] = "Training…",
        ["Train.Started"] = "Training started: {0}",
        ["Train.Stopped"] = "Training stopped",
        ["Train.Failed"] = "Training failed: {0}",
        ["Train.Stopping"] = "Stopping…",
        ["Train.ChartRunning"] = "Training: loss / mAP refresh after every epoch",
        ["Train.ChartIdle"] = "The loss / mAP curve shows up once training starts",

        // ── Core exceptions ──
        ["Error.PathOutsideRoot"] = "Path escapes the root: {0}",
        ["Error.StorageBackendUnknown"] = "Unregistered storage backend: {0}//",
        ["Error.DirectoryUnknown"] = "Cannot resolve the directory: {0}",
        ["Error.NoTrainableSplit"] = "No trainable split: assign train/val to images in the labeler",
        ["Error.ImageBackendMissing"] = "No image backend registered; call ImageRuntime.Use(...) first",
        ["Error.ConcurrencyConflict"] = "Annotation changed: expected revision {0}, actual {1}",
        ["Error.ImageSizeUnknown"] = "Image size unknown; cannot write YOLO labels",
        ["Error.LabelFileEmpty"] = "Annotation file is empty",
        ["Error.LabelFileInvalidJson"] = "Annotation file is not valid JSON",
        ["Error.LabelFileNull"] = "Annotation file content is null",
        ["Error.ImageNotInDatabase"] = "This image is not in the database: {0}",
        ["Error.YoloDescriptorMissing"] = "YOLO dataset has no data.yaml",
        ["Error.YoloNamesMissing"] = "data.yaml has no names: {0}",
        ["Error.CocoDescriptorMissing"] = "COCO dataset has no json descriptor",
        ["Error.CocoFileEmpty"] = "COCO annotation file is empty",
        ["Error.CocoInvalidJson"] = "COCO annotation file is not valid JSON",
        ["Error.CocoFileNull"] = "COCO annotation file content is null",
        ["Error.DatabaseRequiresLocalFolder"] = "The database format only supports a local folder: SQLite WAL needs local random writes",
        ["Error.DatabaseRequiresLocalFile"] = "The database format only supports a local file:// dataset root: SQLite WAL needs local random writes",
        ["Error.DatabaseMissing"] = "No {0} in this data folder: use \"Open data folder…\" to pick an existing one, or \"Import dataset…\" to create it",
        ["Error.MultiProject"] = "The database holds {0} projects; pick one: {1}",
        ["Error.ProjectNotFound"] = "Project not found: {0}",
        ["Error.FolderNotFound"] = "Folder not found: {0}",
        ["Error.ProjectIdNotNumeric"] = "Project id must be numeric: {0}",
        ["Error.DetectMissingDbPath"] = "The detection result has no .db file path",
        ["Error.DatabaseNoProject"] = "No project in the database: {0}",
        ["Error.DatabaseNoProjectId"] = "No project {0} in the database",
        ["Error.DatabaseFileMissing"] = "Database does not exist: {0}",
        ["Error.InvalidDatabase"] = "Not a valid TinyLabel database (images table missing): {0}",
        ["Error.ClassExists"] = "Class already exists: {0}",
        ["Error.ClassNotFound"] = "Class not found: {0}",
        ["Error.ClassInUse"] = "Class \"{0}\" is used by labels and cannot be deleted; rename it instead",
        ["Error.SourceHasNoImages"] = "The source dataset has no importable image: {0}",
        ["Error.SourceAlreadyImageDatabase"] = "The source dataset is already an image-folder + database; open it directly instead of importing",
        ["Error.DescriptorMissingForFormat"] = "No descriptor for that format found in {0} ({1})",
        ["Error.ImportTargetNotLocal"] = "The import target must be a local folder: SQLite WAL needs local random writes",
        ["Error.ImportTargetSameAsSource"] = "The import target must differ from the source dataset root, otherwise the original data is overwritten",
        ["Error.FormatAmbiguous"] = "The dataset matches {0} formats ({1}); pick one",
        ["Error.NoExporter"] = "No exporter available: {0}",
        ["Error.ExportTargetSameAsSource"] = "The export target must differ from the source dataset root; pick another folder",
        ["Error.ExportNoSplit"] = "The source dataset has no exportable split (no image assigned to train/val/test)",
        ["Error.ModelFileNotFound"] = "Model file not found",
        ["Error.FormatNotSupported"] = "Opening a \"{0}\" dataset is not supported{1}",
        ["Error.DetailSuffix"] = ": {0}",
        ["Error.ListSeparator"] = ", ",

        // ── Dataset format names (embedded in exception messages) ──
        ["Dataset.Format.Yolo"] = "YOLO layout",
        ["Dataset.Format.Coco"] = "RF-DETR (COCO)",
        ["Dataset.Format.TinyLabelDb"] = "image folder + database",
        ["Dataset.Format.Ambiguous"] = "several formats",
        ["Dataset.Format.Unknown"] = "unrecognized",
    };

    private static readonly Lazy<Localizer> LazyInstance = new(() => new Localizer());

    private string _language = DefaultLanguage;

    private Localizer()
    {
    }

    /// <summary>进程级单例。</summary>
    public static Localizer Instance => LazyInstance.Value;

    /// <summary>全部文案键（中文表是基准，英文表必须全覆盖）。</summary>
    public static IReadOnlyCollection<string> Keys => Chinese.Keys;

    /// <summary>取某个键在指定语言下的文案（测试 / 导出用）。</summary>
    public static string Get(string key, string language)
    {
        Dictionary<string, string> table = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

        return table.TryGetValue(key, out string? value) ? value : key;
    }

    /// <summary>可选语言（顺序即界面下拉顺序）。</summary>
    public static IReadOnlyList<LanguageOption> Supported { get; } =
    [
        new("zh", "中文"),
        new("en", "English"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言变化（代码里要重算的文案订阅它）。</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>当前语言代码（<c>zh</c> / <c>en</c>）。</summary>
    public string Language => _language;

    /// <summary>XAML 绑定入口：<c>{l:Loc Label.NewProject}</c>。</summary>
    public string this[string key] => Get(key);

    /// <summary>按系统语言推断默认值（非中文系统给英文）。</summary>
    public static string DetectSystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh"
            : "en";

    /// <summary>取一条文案（缺键降级：当前语言 → 英文 → 键名）。</summary>
    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        Dictionary<string, string> current = _language == "en" ? English : Chinese;

        if (current.TryGetValue(key, out string? text))
        {
            return text;
        }

        return English.TryGetValue(key, out string? fallback) ? fallback : key;
    }

    /// <summary>取一条带占位符的文案（<see cref="string.Format(string,object?[])"/> 语法）。</summary>
    public string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>切换语言；同一语言重复调用无副作用。</summary>
    public void SetLanguage(string? code)
    {
        string normalized = string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        if (string.Equals(normalized, _language, StringComparison.Ordinal))
        {
            return;
        }

        _language = normalized;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
