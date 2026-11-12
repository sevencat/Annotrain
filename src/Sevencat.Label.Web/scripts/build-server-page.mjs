// 把单文件构建产物压成 index.html.gz，写回服务端的内嵌资源目录。
// 用法：npm run build:server   （先 quasar build，再跑本脚本）
//
// 服务端（Sevencat.Label.Server.csproj）把 WebUI/index.html.gz 作为 EmbeddedResource 编进程序集，
// 运行期由页面端点读取：客户端支持 gzip 直接发压缩体，否则回退明文。
// 因此发布产物里不再需要 wwwroot 目录，部署只有 exe + data。
//
// 注意：改的是待编译的源文件，脚本跑完要重新构建服务端（dotnet build / dotnet run）才会生效。
import { gzipSync } from 'node:zlib'
import { copyFile, mkdir, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(here, '..')
const dist = join(webRoot, 'dist', 'spa')
const serverRoot = resolve(webRoot, '..', 'Sevencat.Label.Server')
const targetDir = join(serverRoot, 'WebUI')

const kb = (bytes) => `${(bytes / 1024).toFixed(1)} KB`

async function exists(path) {
  try {
    await stat(path)
    return true
  } catch {
    return false
  }
}

/** 清理历史遗留的 wwwroot 副本（内嵌后不再读取，留着只会让人误以为改那里有效）。 */
async function dropLegacyWwwRoot(dir, depth = 0) {
  if (depth > 5) return
  let entries
  try {
    entries = await readdir(dir, { withFileTypes: true })
  } catch {
    return
  }

  for (const entry of entries) {
    if (!entry.isDirectory()) continue
    const child = join(dir, entry.name)
    if (entry.name === 'wwwroot') {
      await rm(child, { recursive: true, force: true })
      console.log(`[build-server-page] 已移除旧副本 ${child}`)
    } else {
      await dropLegacyWwwRoot(child, depth + 1)
    }
  }
}

const source = join(dist, 'index.html')
if (!(await exists(source))) {
  console.error(`[build-server-page] 找不到构建产物：${source}\n先执行 npm run build`)
  process.exit(1)
}

const html = await readFile(source)
const gzip = gzipSync(html, { level: 9 })

await mkdir(targetDir, { recursive: true })
await writeFile(join(targetDir, 'index.html.gz'), gzip)

// favicon 由服务端直接按内嵌字节返回，同样内嵌一份
const favicon = join(dist, 'favicon.ico')
if (await exists(favicon)) {
  await copyFile(favicon, join(targetDir, 'favicon.ico'))
}

await rm(join(serverRoot, 'wwwroot'), { recursive: true, force: true })
await dropLegacyWwwRoot(join(serverRoot, 'bin'))

console.log(`[build-server-page] index.html   ${kb(html.length)}（仅参与压缩，不落盘）`)
console.log(`[build-server-page] gzip         ${kb(gzip.length)}`)
console.log(`[build-server-page] 输出目录     ${targetDir}`)
console.log('[build-server-page] 下一步：重新构建/重启服务端才会生效（dotnet run --project src/Sevencat.Label.Server）')
