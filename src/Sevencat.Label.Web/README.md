# Annotrain Web 标注端

Quasar（Vue 3 + TypeScript + Vite）单文件构建的前端，移植自 TinyLabel 的 `web/`；由
`src/Sevencat.Label.Server` 内嵌托管，默认地址 `http://127.0.0.1:5180`
（见 `LabelWebModule.DefaultUrl`，桌面端顶栏显示的也是它）。

## 开发

需要 Node ≥ 22（引擎声明见 `package.json` 的 `engines`）。

```bash
npm install
npm run dev        # http://localhost:9100
```

开发期 `/api` 会被代理到 `http://127.0.0.1:5180`（`ANNOTRAIN_API` 可覆盖），
服务端与前端同源，不用处理 CORS。

> 服务端接口（M3 / W-2）还没落地：现在 `npm run dev` 能起页面，但登录与项目列表会代理失败。

## 构建

```bash
npm run build         # 单文件产物 dist/spa/index.html（JS/CSS 全部内联）
npm run build:server  # 再压成 gzip，写进 ../Sevencat.Label.Server/WebUI/index.html.gz
```

服务端把 `WebUI/index.html.gz` 作为 `EmbeddedResource` 编进程序集，运行期按
`Accept-Encoding` 决定发压缩体还是明文，所以发布产物只有 exe + data，没有 `wwwroot`。
改完前端要重新 `dotnet build` / 重启服务端才生效。

生成物 `../Sevencat.Label.Server/WebUI/index.html.gz` 随仓库提交（约 336 KB）：
这样只 clone 仓库、不装 Node 也能 `dotnet build` 出带页面的 exe；改了前端记得重跑
`npm run build:server` 再提交。调试时也可以给 `ServerOptions.WebPageDirectory` 指到
`dist/spa`，服务端改读磁盘，省掉重新编译。

## 与桌面端共用的约定

- labelme 文档里的私有元数据键是 `annotrain`（Core 的 `AnnotrainInfo.MetadataNamespace`），读到时原样保留；
- 标注文档版本默认 `5.0.1`（Core `LabelmeCodec`），形状类型 `rectangle` / `polygon` / `circle` / `line` / `mask`；
- 浏览器本地键：`annotrain.token` / `annotrain.user` / `annotrain.userName`；
- 图片走 `/api/images/{id}/content|thumb?token=...`（`<img>` 带不了请求头）。
