<p align="center">
  <img src="docs/image_banner.jpg" alt="Annotrain" width="820">
</p>

# Annotrain

面向 **YOLOv8 / YOLO11 / RF-DETR** 的「标注 → 训练 → 导出 → 检测验证」桌面工作台：**纯 C#，不依赖 Python**。

> 定位：个人 / 小团队自用的模型迭代工具。
> 检测只用于验证训练出来的模型效果，**不作为对外推理服务，也不作为供其他系统集成的 SDK**。

## 四个关键点

### 1. 不依赖 Python

- 训练、推理、导出全在 .NET 进程内完成：**TorchSharp（LibTorch）** 承担前向 / 反向 / 优化器，**Microsoft.ML.OnnxRuntime** 承担 ONNX 推理与验证。
- 没有 Python 环境、没有 `pip`、没有 `ultralytics`；拷走 exe 目录就能跑（绿色部署）。
- 训练循环、EMA、损失、数据增强、letterbox、NMS、ONNX 导出全部是 C# 代码，调试只需 dotnet 与 IDE。

### 2. 标注和项目管理直接在数据库里

- 项目 / 类别 / 图片 / 标注 / 状态 / 版本 / 备注全部存在一个 SQLite 文件 `annotrain.db` 里（TinyLabel 表结构），**不散落成成百上千个 json 文件**。
- 配置也落库（`settings.db` 的 `settings` 表），换机器只拷 `data` 目录即可。
- 好处很直接：项目列表、进度、待标注队列、训练用数据集都是一条 SQL 能查出来的；标注端与训练端读写同一份库，**不需要中间导出再训练**。
- 需要外部格式时随时导出成 YOLO / COCO —— 库是主存储，目录格式只是出口。

### 3. 训练不依赖固定目录格式

- 训练入口统一抽象成 `IDatasetStore`：**YOLO 目录 / COCO（RF-DETR）目录 / SQLite 标注库** 三种形态都能直接训练。
- 训练端打开数据集自动探测形态（`data.yaml` / `_annotations.coco.json` / `annotrain.db`），也可以手动指定：
  `数据集 [YOLO] [RF-DETR] [TinyLabel]`。TinyLabel 会列出库里的项目让你选。
- 模型可选 `YOLOv8` / `YOLOv11` / `RF-DETR`；训练完可**导出 ONNX**（检测直接用）或**导出 safetensors + `model.json`**（架构、类别、输入尺寸）。

### 4. Web 标注 / Web 训练管理（进行中）

- 标注端已经内置 Web 服务：`http://127.0.0.1:5180`（登录、项目、图片、标注、统计都能在浏览器里做）。
- 规划：训练端 Web 页面（任务列表、实时日志、指标曲线、远程启停），之后桌面端只做「本地壳 + 离线兜底」，
  标注、训练排队与结果查看都在网页上完成。

## 界面截图

<table>
  <tr>
    <td width="50%"><img src="docs/screens/label-main.png" alt="标注端主界面"><br>
      <sub><b>标注端</b>：左侧项目（直接读 SQLite）、右侧缩略图 / 标注状态 / 进度</sub></td>
    <td width="50%"><img src="docs/screens/label-canvas.png" alt="标注画布"><br>
      <sub><b>标注画布</b>：拖拽画框、选中改标签、右侧形状列表，保存即写回 <code>annotations.DocJson</code></sub></td>
  </tr>
  <tr>
    <td colspan="2"><img src="docs/screens/train-main.png" alt="训练端主界面"><br>
      <sub><b>训练端</b>：数据集（YOLO / RF-DETR / TinyLabel）、模型（YOLOv8 / YOLO11 / RF-DETR）、
      训练参数、ONNX / safetensors 导出、LiveCharts 双轴曲线（mAP 左 / loss 右）</sub></td>
  </tr>
</table>

仓库用**两个解决方案**分别管两条线，互不编译依赖，只共享 `Sevencat.Core`：

| 解决方案 | 产出 | 组成 |
| --- | --- | --- |
| `Sevencat.Label.sln` | 标注桌面应用 `Sevencat.Label.App.exe` | 业务 + 程序 + Web |
| `Sevencat.Train.sln` | 训练/验证桌面应用 `Sevencat.Train.exe` | 业务 + 引擎 + Web |

## 技术路线

- **纯 C# 训练**：TorchSharp（LibTorch）承担计算；训练循环、EMA、损失、数据增强、ONNX 导出全在进程内，不用 Python。
- **代码复用**，不重写：标注端的画布 `AnnotationCanvas`、标注模型、原子写盘、Minimal API 服务参考 TinyLabel 项目；
  训练侧的网络结构、Trainer、EMA、导出与推理参考 OmniTensorSharp 项目。复制时只改命名空间，并做**存储层接口化**。
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

导入完就是普通标注项目：左侧项目列表会选中它，右侧表格列出全部图片。
`--format` 只在探测同时命中多种形态时才需要。

## 数据与配置放在哪

默认全在 **exe 同级 `data`** 目录，整个程序是绿色的（拷走 exe 目录即带走数据）：

| 路径 | 内容 |
| --- | --- |
| `data/annotrain.db` | 项目 / 类别 / 图片 / 标注（SQLite，TinyLabel 表结构） |
| `data/settings.db` | 配置（`settings` 表，键 `app` → 整份设置的 JSON） |
| `data/logs/*.log` | 运行日志 |
| `data/projects/{项目Id}/` | 导入图片的副本 |

命令行可用 `--data <目录>` 换一个数据根目录（标注端与训练端都支持）。
首次启动会把旧的 `%APPDATA%\Sevencat.Annotrain\settings.json` 迁移进 `settings.db`。

### 进程级设置：环境变量 / `.env`

库位置、数据目录、Web 端口这类「改完要重启」的设置不进界面，也不写回库，
需要时在 **exe 同级** 建一个 `.env`，一行一个 `KEY=VALUE`（`#` 开头是注释，可写 `export KEY=VALUE`）：

```ini
ANNOTRAIN_DATA=D:\annotrain
ANNOTRAIN_DB=D:\annotrain\custom.db
ANNOTRAIN_LABEL_PORT=5280
```

| 键 | 作用 | 优先级 |
| --- | --- | --- |
| `ANNOTRAIN_DATA` | 数据根目录 | `--data` > 它 > 默认 `data` |
| `ANNOTRAIN_DB` | 库文件（可在数据目录之外） | `--db` > 它 > `<数据目录>/annotrain.db` |
| `ANNOTRAIN_LABEL_PORT` | 标注 Web 端端口（地址恒为 `http://127.0.0.1:该端口`） | 它 > 默认 `5180` |

命令行永远压过环境变量；进程里已经存在的环境变量不会被 `.env` 覆盖（CI / 系统变量优先）。
`.env` 只看 exe 同级、不翻父目录；地址写错会直接启动失败，原因记在 `data/logs/label.log`。

**「新建项目」不建数据库**：它只往当前数据目录**已有**的 `annotrain.db` 里插一行；
数据目录还没有库时，点新建会先问「要不要在这里建一个空库」，确认后才建库（只建表、不建项目）。
新建数据库的另一条正常路径是「导入数据集…」。

## 多语言

界面支持中文 / English，右上角下拉切换，选择存在 `settings.db`（`Language` 字段，空值跟随系统语言）。
文案表在 `Sevencat.Core/Localization/Localizer.cs`（中英两份字典，缺键降级到英文再降级到键名）。
XAML 里用 `{l:Loc Label.NewProject}`，代码里用 `Localizer.Instance.Format(...)`；切换语言时界面即时刷新。

## 标注程序界面

参照 TinyLabel 服务端首页（左项目管理 + 右图片清单）：

- **左侧「项目管理」**：新建项目 / 编辑项目 / 删除项目 / 刷新 / 结束项目（重新打开）。
  列表按「未结束优先 + 创建时间倒序」，每行显示创建日期、图片数与已处理/待处理数。
- **右侧「项目概览」**：项目名、备注、进度条、进行中/已结束徽章；工具栏依次是
  **导入图片… → 导入数据集… → 导入文件夹… → 扫描目录… → 导出数据集… → 打开目录 → 类别管理 → 开始标注**。
- **图片清单**：路径 / 标注 / 状态 / 版本 / 备注 五列 + 行尾「标注」按钮，底部翻页（每页 50 张）。
  状态列跟着库里的 `images.State` 走：保存标注后自动从 `未标注` 变 `已标注`。
- **标注窗口**：左键拖拽画矩形；点选后可拖动、`Delete` 删除；滚轮缩放、`适应窗口` 复位；
  标签从项目类别里选，也可以直接输入新名字；「保存」写回 `annotations.DocJson` 并刷新表格，
  「保存并继续」存完自动领下一张还没框的图。
- **AI 标注**：先「AI 设置」选一个 YOLO 导出的 `.onnx` 和置信度（记在用户配置里），
  再点「AI 标注」跑推理。结果先显示成黄色虚线候选框，确认后「接受全部」/「只接受 ≥0.5」/「丢弃」，
  接受才会变成正式框并需要保存。模型 class id 按项目类别顺序映射。

AI 推理走 `Microsoft.ML.OnnxRuntime`（CPU），仍然是纯 C#、不装 Python。

命令行走同一个流程：

```powershell
Sevencat.Label.App.exe --open <数据目录>        # 打开目录，列出项目
Sevencat.Label.App.exe --annotate <数据目录>    # 打开目录并直接进第一张图的标注窗口
```

## 训练程序界面

单页，不切 tab：

- **数据集**：`YOLO` / `RF-DETR` / `TinyLabel` 三个入口。TinyLabel 选 `annotrain.db` → 列出库里的项目 → 选一个训练；
  文件框停在上次用过的库目录，项目 Id 记进配置，重启自动恢复上次项目。
- **控制**：开始训练 / 暂停 / 继续 / 结束 / 检测…；右侧只读显示 epochs / batch / imgsz，点「训练参数」进对话框改。
- **输出目录**：`[设置] [打开]` + 产物 `best.safetensors`；「打开」直接在资源管理器里打开（目录不存在会先建）。
- **模型**：`YOLOv8` / `YOLOv11` / `RF-DETR`。YOLO 走 `YoloDatasetTrainingRunner`；RF-DETR 走 `DetrDatasetTrainingRunner`
  （先把数据集导出成临时 YOLO 目录，再交给 DETR 会话，输入固定 224）。
- **导出**：`导出 ONNX` → `{输出目录}/export/best.onnx`；`导出 safetensors` → `best.safetensors` + `model.json`
  （架构 / 家族 / 类别 / 输入输出尺寸 / best 指标）。导出过 ONNX 后，检测按钮会**优先用这份模型**。
- **图表**：LiveCharts 双 Y 轴（mAP 左轴 0~1、loss 右轴），逐 epoch 追加，没数据时显示提示文字。

训练产物写在输出目录：`best.safetensors` / `model.safetensors` / `model-ema.safetensors` /
`optimizer.bin`（续训用）/ `train.json`（超参与每轮指标）。RF-DETR 产物同名，架构记成 `rfdetr-detect-N`。

### 检测验证

点「检测…」：选一张图片 → ONNX 推理 → 弹对话框，结果框直接叠在图上，右侧列出类别与置信度。
模型顺序：**最近导出的 `best.onnx`** → 设置里的 `AutoLabelModelPath` → 弹框手选。

推理与标注端的 AI 标注共用 `YoloOnnxRunner`（letterbox + 解码 + NMS）。
> 目前检测只解析 YOLO 输出；RF-DETR 导出的 `boxes / logits` 需要专门的 DETR 后处理（规划中）。

```powershell
Sevencat.Train.App.exe --open <数据集目录>   # 启动即打开数据集
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
| 3 训练（直接读库） | 真训练入口按 `images.Split` 装载数据（YOLO / RF-DETR 由 Family 分派） | `training-db/` |
| 4 导出 + 训练 | 导出 YOLO（`data.yaml` / `images/` / `labels/` / `classes.txt`），再从导出目录训练一遍 | `export-yolo/`、`training-yolo/` |
| 5 验证 | 用生成时的真值反查标注与导出结果 | `simulation-report.txt` |

退出码 `0` 表示验证通过；`2` 表示有缺失 / 多余 / 超容差。模拟流程里现在也走真训练入口
（`IDatasetTrainingRunner`，按 Family 分派 YOLO / RF-DETR），想快速跑通用 `--epochs 1`。同一套流程也有单测：
`tests/Sevencat.Train.Engine.Tests/SimulationPipelineTests.cs`。

## 解决方案结构

```text
Sevencat.Label.sln
  src/Sevencat.Core/              跨解决方案契约：存储接口、公共模型、坐标与图像抽象、格式 codec
  src/Sevencat.Label/             标注业务：标注入库、编辑器逻辑、原子写盘、项目浏览与导入
  src/Sevencat.Label.App/         标注程序（exe）：画布、交互、ViewModel、窗口
  src/Sevencat.Label.Server/      标注 Web 服务端（类库）：Minimal API + 内嵌页面 + 数据集统计
  src/Sevencat.Label.Web/         标注 Web 前端（Quasar/Vue 3 + TS）：单文件构建，产物内嵌进 Label.Server
  tests/Sevencat.Label.Tests/     格式读写、坐标换算、目录探测

Sevencat.Train.sln
  src/Sevencat.Core/              （同一份源码，两个解决方案都包含）
  src/Sevencat.Train.App/         训练程序（exe）：单页主界面（工具栏 / 曲线 / 状态）+ 检测验证对话框
  src/Sevencat.Train.Engine/      训练引擎：模型结构、数据管线、Trainer、导出、推理
  src/Sevencat.Train.Web/         训练 Web：任务列表、实时日志、指标
  tests/Sevencat.Train.Engine.Tests/  模型结构、损失、导出、数据集接口
```

命名约定：

- `Sevencat.<模块>` 是业务层。`Sevencat.Core`、`Sevencat.Train.Engine`、`Sevencat.Label` **不得引用 Avalonia**。
- 可执行程序统一叫 `Sevencat.<模块>.App`：`Sevencat.Label.App`、`Sevencat.Train.App`；它们可以引用 Avalonia。
- Web 后端都是类库（`Sevencat.Label.Server` / `Sevencat.Train.Web`），由各自程序进程内启动 HTTP 服务，随主程序启停，不单独部署；前端源码单独放（`src/Sevencat.Label.Web` 是 Quasar 工程），构建成单文件页面后内嵌进后端。
- 检测（推理验证）不单独建项目：推理在 `Sevencat.Train.Engine`，页面在 `Sevencat.Train.App`。

## 快速开始

```powershell
# 标注（项目管理 / 项目明细 / 标注对话框）
dotnet build Sevencat.Label.sln
dotnet run --project src/Sevencat.Label.App

# 训练（训练 / 检测验证）
dotnet build Sevencat.Train.sln
dotnet run --project src/Sevencat.Train.App

# 测试
dotnet test Sevencat.Label.sln
dotnet test Sevencat.Train.sln
```

数据、配置与日志默认位于 exe 同级的 `data\`（`annotrain.db`、`settings.db`、`logs\`），可用 `--data <目录>` 覆盖。

### 标注 Web 端

标注程序启动时会在进程内拉起标注 Web 服务，只绑本机：<http://127.0.0.1:5180>（顶栏也有「打开」按钮）。
换端口在 exe 同级 `.env` 里写 `ANNOTRAIN_LABEL_PORT=5280`（地址由端口拼，主机固定回环），顶栏显示的地址与真正监听的是同一个值。

- 账号存在 `data\settings.db` 的 `web_users` 表；首次启动自动建 `admin`，初始密码写在 `data\web-admin.txt`
  （桌面端「用户管理」里能看到账号列表）。
- 令牌是 HMAC 自签的，密钥在 `data\web-token.key`，删掉即让所有浏览器下线。
- `/api/*` 需要 `Authorization: Bearer <token>`，图片走 `?token=`；页面本身不设防，数据全靠 API。
- 改前端：`cd src/Sevencat.Label.Web && npm run build:server`，再重新 `dotnet build`（页面被 gzip 内嵌进程序集）。

## 当前状态

- **标注端**：项目管理、图片导入 / 数据集导入、画布标注、AI 标注、导出 YOLO / COCO、Web 端都已可用；
  数据全在 `data/annotrain.db`，配置在 `data/settings.db`。
- **训练端**：YOLOv8 / YOLO11 / RF-DETR 训练已接入（TorchSharp），开始 / 暂停 / 继续 / 结束可用；
  产物支持 safetensors 与 ONNX 导出，检测直接用导出的模型；曲线用 LiveCharts，界面为 Material 主题。
- **规划中**：训练端 Web（任务队列 / 远程监控 / 日志与曲线）、RF-DETR 的 ONNX 后处理与自动标注、数据集版本对比。

技术栈：.NET 10、Avalonia 12.1.5、Material.Avalonia 3.21、TorchSharp（LibTorch）、Microsoft.ML.OnnxRuntime、
FreeSql + SQLite、LiveCharts 2.1、NLog、CommunityToolkit.Mvvm。

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
