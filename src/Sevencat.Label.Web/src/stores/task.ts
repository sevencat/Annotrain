import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '@/services/api'
import { HttpError, imageUrl } from '@/services/http'
import {
  clonePoints,
  emptyDoc,
  moveShape,
  newShapeId,
  parseDoc,
  stringifyDoc,
  type LabelmeDoc,
  type LabelmeShape,
  type Point,
} from '@/services/labelme'
import type { ImageDto, ToolKind } from '@/types/api'
import { useProjectStore } from './project'

/** 服务端租约 5 分钟，这里提前 1 分钟续，避免把图白送给别人。 */
const HEARTBEAT_MS = 4 * 60 * 1000
const HISTORY_LIMIT = 100

/**
 * 当前任务的全部状态：领图 → 标注 → 保存/跳过/退回。
 *
 * 与桌面端 `MainWindowViewModel` 的远端流程对齐，刻意只做「一张图」——
 * 单图单实例，切图即销毁，浏览器里没有多标签页并发需求。
 */
export const useTaskStore = defineStore('task', () => {
  const project = useProjectStore()

  const image = ref<ImageDto | null>(null)
  const revision = ref(0)
  const doc = ref<LabelmeDoc | null>(null)
  const shapes = ref<LabelmeShape[]>([])
  const selected = ref<number | null>(null)
  const tool = ref<ToolKind>('select')
  const currentLabel = ref('')
  const remark = ref('')
  const dirty = ref(false)
  const busy = ref(false)
  const loading = ref(false)
  const status = ref('')
  const conflict = ref<HttpError | null>(null)
  const undoStack = ref<string[]>([])
  const redoStack = ref<string[]>([])

  const canUndo = computed(() => undoStack.value.length > 0)
  const canRedo = computed(() => redoStack.value.length > 0)
  const selectedShape = computed(() =>
    selected.value === null ? null : (shapes.value[selected.value] ?? null),
  )
  const thumbUrl = computed(() => (image.value ? imageUrl(image.value.id, 'thumb', 1024) : ''))
  const fullUrl = computed(() => (image.value ? imageUrl(image.value.id, 'content') : ''))
  const emptyHint = computed(() => {
    if (image.value) return ''
    return project.current ? '点「下一张」领取待标注图片' : '尚未进入项目'
  })

  let heartbeat: ReturnType<typeof setInterval> | undefined

  // ---------------------------------------------------------------- 历史

  function snapshot(): string {
    return JSON.stringify(shapes.value)
  }

  function pushHistory(): void {
    undoStack.value.push(snapshot())
    if (undoStack.value.length > HISTORY_LIMIT) undoStack.value.shift()
    redoStack.value = []
  }

  function restore(json: string): void {
    shapes.value = JSON.parse(json) as LabelmeShape[]
    selected.value = null
    dirty.value = true
  }

  function undo(): void {
    const previous = undoStack.value.pop()
    if (previous === undefined) return
    redoStack.value.push(snapshot())
    restore(previous)
  }

  function redo(): void {
    const next = redoStack.value.pop()
    if (next === undefined) return
    undoStack.value.push(snapshot())
    restore(next)
  }

  // ---------------------------------------------------------------- 编辑

  function setTool(next: ToolKind): void {
    tool.value = next
    if (next !== 'select') selected.value = null
  }

  function select(index: number | null): void {
    selected.value = index
  }

  function addShape(shape: LabelmeShape): void {
    pushHistory()
    const label = shape.label || currentLabel.value || project.classes[0]?.name || 'object'
    shapes.value.push({ ...shape, label, id: shape.id ?? newShapeId() })
    selected.value = shapes.value.length - 1
    dirty.value = true
    status.value = `已添加 ${label}`
  }

  /** 顶点拖动结束时调用；`recordHistory` 为 false 用于拖动过程中的实时预览。 */
  function updateShape(index: number, shape: LabelmeShape, recordHistory = true): void {
    if (!shapes.value[index]) return
    if (recordHistory) pushHistory()
    shapes.value[index] = shape
    dirty.value = true
  }

  function deleteSelected(): void {
    const index = selected.value
    if (index === null || !shapes.value[index]) return
    pushHistory()
    const [removed] = shapes.value.splice(index, 1)
    selected.value = null
    dirty.value = true
    status.value = `已删除 ${removed?.label ?? '形状'}`
  }

  function duplicateSelected(): void {
    const shape = selectedShape.value
    if (!shape) return
    pushHistory()
    const copy: LabelmeShape = { ...shape, points: clonePoints(shape.points), id: newShapeId() }
    shapes.value.push(moveShape(copy, 10, 10))
    selected.value = shapes.value.length - 1
    dirty.value = true
    status.value = `已复制 ${shape.label}`
  }

  function applyLabel(label: string): void {
    currentLabel.value = label
    const index = selected.value
    const shape = index === null ? null : shapes.value[index]
    if (!shape || shape.label === label) return
    pushHistory()
    shapes.value[index!] = { ...shape, label }
    dirty.value = true
  }

  function moveShapeBy(dx: number, dy: number): void {
    const shape = selectedShape.value
    if (!shape) return
    pushHistory()
    shapes.value[selected.value!] = moveShape(shape, dx, dy)
    dirty.value = true
  }

  function clearShapes(): void {
    if (shapes.value.length === 0) return
    pushHistory()
    shapes.value = []
    selected.value = null
    dirty.value = true
  }

  // ---------------------------------------------------------------- 任务流

  function clearTask(): void {
    stopHeartbeat()
    image.value = null
    doc.value = null
    shapes.value = []
    selected.value = null
    revision.value = 0
    remark.value = ''
    dirty.value = false
    conflict.value = null
    undoStack.value = []
    redoStack.value = []
  }

  function startHeartbeat(): void {
    stopHeartbeat()
    heartbeat = setInterval(() => {
      const id = image.value?.id
      if (!id) return
      // 续约失败不打断标注：真正保存时服务端还会再校验一次
      void api.claim(id).catch((error: unknown) => {
        if (error instanceof HttpError && error.code === 'claim_lost') {
          stopHeartbeat()
          status.value = '这张图已被他人接管，保存会被拒绝'
        }
      })
    }, HEARTBEAT_MS)
  }

  function stopHeartbeat(): void {
    if (heartbeat !== undefined) {
      clearInterval(heartbeat)
      heartbeat = undefined
    }
  }

  function loadDocument(next: ImageDto, docJson: string | null, nextRevision: number): void {
    const parsed = docJson
      ? parseDoc(docJson, next.width, next.height)
      : emptyDoc(next.width, next.height, next.relPath)

    image.value = next
    doc.value = parsed
    shapes.value = parsed.shapes
    revision.value = nextRevision
    remark.value = next.remark ?? ''
    selected.value = null
    dirty.value = false
    conflict.value = null
    undoStack.value = []
    redoStack.value = []
    currentLabel.value = currentLabel.value || parsed.shapes[0]?.label || project.classes[0]?.name || ''
    status.value = `已领取 ${next.relPath}（r${nextRevision}）`
  }

  /** 领取下一张；传 `after` 排除当前这张，避免跳过/退回后又领到同一张。 */
  async function claimNext(): Promise<boolean> {
    const current = project.current
    if (!current) return false
    if (busy.value) return false

    busy.value = true
    loading.value = true
    try {
      // 切图前先落盘，避免「标了半天点下一张」丢改动
      if (dirty.value && image.value) {
        const saved = await saveInternal()
        if (!saved) return false
      }

      const previous = image.value?.id ?? null
      if (image.value) await releaseQuietly(image.value.id)

      status.value = '正在领取…'
      const next = await api.nextImage(current.id, previous)
      if (!next) {
        clearTask()
        status.value = '暂时没有待标注的图片'
        return false
      }

      const annotation = await api.annotation(next.id)
      loadDocument(next, annotation?.docJson ?? null, annotation?.revision ?? next.revision)
      startHeartbeat()
      void project.refreshStats()
      return true
    } catch (error) {
      status.value = `领取失败：${(error as Error).message}`
      return false
    } finally {
      busy.value = false
      loading.value = false
    }
  }

  function buildDoc(): LabelmeDoc {
    const base = doc.value ?? emptyDoc(image.value?.width ?? 1, image.value?.height ?? 1)
    return {
      ...base,
      shapes: shapes.value,
      imageWidth: image.value?.width ?? base.imageWidth,
      imageHeight: image.value?.height ?? base.imageHeight,
      imagePath: image.value?.relPath ?? base.imagePath ?? '',
      version: base.version,
    }
  }

  async function saveInternal(): Promise<boolean> {
    const target = image.value
    if (!target) return false
    if (!dirty.value) {
      status.value = '没有改动'
      return true
    }

    status.value = '保存中…'
    try {
      const saved = await api.saveAnnotation(target.id, revision.value, stringifyDoc(buildDoc()))
      revision.value = saved.revision
      doc.value = { ...buildDoc(), annotrain: { ...(doc.value?.annotrain ?? {}), revision: saved.revision } }
      dirty.value = false
      status.value = `已保存 r${saved.revision}`
      return true
    } catch (error) {
      if (error instanceof HttpError && error.status === 409) {
        conflict.value = error
        status.value = '保存冲突：这张图已被改过'
        return false
      }
      status.value = `保存失败：${(error as Error).message}`
      return false
    }
  }

  async function save(): Promise<boolean> {
    if (busy.value) return false
    busy.value = true
    try {
      return await saveInternal()
    } finally {
      busy.value = false
    }
  }

  /** 冲突对话框「覆盖保存」：拿服务端最新 revision，把本地内容再发一次。 */
  async function overwrite(): Promise<boolean> {
    const latest = conflict.value?.conflict?.current
    if (latest) revision.value = latest.revision
    conflict.value = null
    dirty.value = true
    return save()
  }

  /** 冲突对话框「重新加载」：丢掉本地改动，回到服务端版本。 */
  async function reloadFromServer(): Promise<void> {
    const target = image.value
    if (!target) return
    conflict.value = null
    const annotation = await api.annotation(target.id)
    loadDocument(target, annotation?.docJson ?? null, annotation?.revision ?? target.revision)
    dirty.value = false
    status.value = '已重新加载服务端版本'
  }

  async function uploadAndNext(): Promise<void> {
    if (!image.value) return
    const ok = await save()
    if (!ok) return
    await claimNext()
  }

  async function skip(): Promise<void> {
    const target = image.value
    if (!target) return
    busy.value = true
    try {
      await api.skip(target.id, remark.value || undefined)
      stopHeartbeat()
      image.value = null
      dirty.value = false
      void project.refreshStats()
      status.value = '已跳过，管理员重置前谁也领不到'
      busy.value = false
      await claimNext()
    } catch (error) {
      status.value = `跳过失败：${(error as Error).message}`
      busy.value = false
    }
  }

  async function returnToServer(): Promise<void> {
    const target = image.value
    if (!target) return
    busy.value = true
    try {
      await api.returnImage(target.id, remark.value || undefined)
      clearTask()
      status.value = '已退回服务器，点「下一张」继续'
      void project.refreshStats()
    } catch (error) {
      status.value = `退回失败：${(error as Error).message}`
    } finally {
      busy.value = false
    }
  }

  async function releaseQuietly(imageId: number): Promise<void> {
    try {
      await api.release(imageId)
    } catch {
      // 尽力而为：租约本来就会自然过期
    }
  }

  /** 离开工作台（路由跳转/关页）时调用。 */
  async function leave(): Promise<void> {
    stopHeartbeat()
    if (image.value) {
      if (dirty.value) await saveInternal()
      await releaseQuietly(image.value.id)
    }
    clearTask()
  }

  // 有未保存改动时，关页前提示一次（浏览器不允许异步保存，只能拦）
  if (typeof window !== 'undefined') {
    window.addEventListener('beforeunload', (event) => {
      if (dirty.value) {
        event.preventDefault()
        event.returnValue = ''
      }
    })
  }

  return {
    image,
    revision,
    doc,
    shapes,
    selected,
    selectedShape,
    tool,
    currentLabel,
    remark,
    dirty,
    busy,
    loading,
    status,
    conflict,
    canUndo,
    canRedo,
    thumbUrl,
    fullUrl,
    emptyHint,
    setTool,
    select,
    addShape,
    updateShape,
    deleteSelected,
    duplicateSelected,
    applyLabel,
    moveShapeBy,
    clearShapes,
    undo,
    redo,
    claimNext,
    save,
    overwrite,
    reloadFromServer,
    uploadAndNext,
    skip,
    returnToServer,
    leave,
    clearTask,
  }
})

export type TaskStore = ReturnType<typeof useTaskStore>
export type { Point }
