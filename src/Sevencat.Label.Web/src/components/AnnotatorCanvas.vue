<template>
  <div ref="host" class="annotator-host" :class="{ 'is-panning': panning }">
    <KStage
      v-if="size.width > 0 && size.height > 0"
      ref="stageRef"
      :config="stageConfig"
      @wheel="onWheel"
      @mousedown="onMouseDown"
      @mousemove="onMouseMove"
      @mouseup="onMouseUp"
      @mouseleave="onMouseUp"
      @click="onStageClick"
      @dblclick="onDblClick"
      @contextmenu="onContextMenu"
    >
      <KLayer :config="layerConfig">
        <KImage v-if="image" :config="imageConfig" />

        <template v-for="(shape, index) in shapes" :key="index">
          <KRect
            v-if="shape.shape_type === 'rectangle'"
            :config="rectConfig(shape, index)"
            @mousedown="onShapeMouseDown(index, $event)"
          />
          <KCircle
            v-else-if="shape.shape_type === 'circle'"
            :config="circleConfig(shape, index)"
            @mousedown="onShapeMouseDown(index, $event)"
          />
          <KLine
            v-else
            :config="polyConfig(shape, index)"
            @mousedown="onShapeMouseDown(index, $event)"
          />
        </template>

        <!-- 选中形状的顶点手柄 -->
        <KRect
          v-for="(point, index) in vertexHandles"
          :key="`v${index}`"
          :config="vertexConfig(point, index)"
          @dragend="onVertexDragEnd(index, $event)"
          @mousedown="onVertexMouseDown"
        />

        <!-- 绘制中的预览 -->
        <KRect v-if="draftRect" :config="draftRect" />
        <KCircle v-if="draftCircle" :config="draftCircle" />
        <KLine v-if="draftLine" :config="draftLine" />
      </KLayer>
    </KStage>

    <div v-if="!imageUrl" class="annotator-empty">
      <q-icon name="image" size="48px" class="q-mb-sm" />
      <div>{{ hint }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import Konva from 'konva'
import { Circle as KCircle, Image as KImage, Layer as KLayer, Line as KLine, Rect as KRect, Stage as KStage } from 'vue-konva'
import { computed, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch } from 'vue'
import {
  boundsOf,
  clonePoints,
  normalizePoint,
  rectPoints,
  type LabelmeShape,
  type Point,
} from '@/services/labelme'
import type { ToolKind } from '@/types/api'

/**
 * Konva 画布：世界坐标就是图片像素坐标（左上角 0,0，x 向右、y 向下），
 * 视图变换（缩放/平移）放在 Stage 上，图形坐标始终是图片像素，跟 labelme 数据一致。
 *
 * 绘制交互自己实现（矩形拖拽、圆拖拽、多段线点击加点），不依赖外部绘制插件，
 * 半径/尺寸都是像素，不存在「球面米」那类单位换算问题。
 */

const props = defineProps<{
  imageUrl: string
  width: number
  height: number
  shapes: LabelmeShape[]
  selected: number | null
  tool: ToolKind
  label: string
  colorOf: (label: string) => string
  allowOutOfBounds?: boolean
  hint?: string
}>()

const emit = defineEmits<{
  create: [shape: LabelmeShape]
  update: [index: number, shape: LabelmeShape]
  select: [index: number | null]
  cursor: [x: number, y: number]
  zoom: [percent: number]
}>()

const host = ref<HTMLDivElement>()
const stageRef = ref<{ getStage?: () => Konva.Stage; getNode?: () => Konva.Stage } | null>(null)

const MIN_SCALE = 0.02
const MAX_SCALE = 16
/** 顶点手柄的屏幕尺寸（会除以缩放，保证看起来一样大）。 */
const HANDLE_SIZE = 10

const size = reactive({ width: 0, height: 0 })
const view = reactive({ scale: 1, x: 0, y: 0 })

const image = shallowRef<HTMLImageElement | null>(null)
const panning = ref(false)
const spaceHeld = ref(false)

/** 拖拽中的矩形/圆。 */
type Gesture = { kind: 'rect' | 'circle'; start: Point; current: Point }
const gesture = ref<Gesture | null>(null)

/** 多边形/折线已经点下的顶点。 */
const pending = ref<Point[]>([])
/** 鼠标当前位置，用来画多段线的橡皮筋。 */
const hoverPoint = ref<Point | null>(null)

/** 拖动顶点时的本地顶点（提交前不写 props）。 */
const editPoints = ref<Point[] | null>(null)
let draggingVertex = false

let panStart: { pointer: Point; origin: Point } | null = null
/** 按下位置，用来判断这次操作算不算「点击」。 */
let pressAt: Point | null = null
/** 本次按下后是否移动过：拖动结束不该再算一次点击。 */
let pointerMoved = false
let resizeObserver: ResizeObserver | null = null

const stageConfig = computed(() => ({
  width: size.width,
  height: size.height,
  scaleX: view.scale,
  scaleY: view.scale,
  x: view.x,
  y: view.y,
}))

const layerConfig = computed(() => ({ listening: true }))

const imageConfig = computed(() => ({
  image: image.value,
  x: 0,
  y: 0,
  width: props.width,
  height: props.height,
  listening: false,
}))

function stage(): Konva.Stage | null {
  const instance = stageRef.value
  if (!instance) return null
  return instance.getStage?.() ?? instance.getNode?.() ?? null
}

/** 屏幕坐标 → 图片像素坐标。 */
function toWorld(pointer: Point): Point {
  return [(pointer[0] - view.x) / view.scale, (pointer[1] - view.y) / view.scale]
}

/** 指针位置（图片像素）；拿不到 Stage 时返回 null。 */
function pointerWorld(): Point | null {
  const position = stage()?.getPointerPosition()
  return position ? toWorld([position.x, position.y]) : null
}

function clampPoint(point: Point): Point {
  if (props.allowOutOfBounds) return point
  return normalizePoint(point, Math.max(1, props.width), Math.max(1, props.height), false)
}

function emitZoom(): void {
  emit('zoom', Math.round(view.scale * 100))
}

function fit(): void {
  const w = Math.max(1, props.width)
  const h = Math.max(1, props.height)
  if (size.width <= 0 || size.height <= 0) return
  const scale = Math.min(size.width / w, size.height / h) * 0.96
  view.scale = scale
  view.x = (size.width - w * scale) / 2
  view.y = (size.height - h * scale) / 2
  emitZoom()
}

/** 以画布中心为锚点缩放，避免缩放时图跑出视野。 */
function zoomTo(target: number): void {
  const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, target))
  if (next === view.scale) return
  const cx = size.width / 2
  const cy = size.height / 2
  view.x = cx - (cx - view.x) * (next / view.scale)
  view.y = cy - (cy - view.y) * (next / view.scale)
  view.scale = next
  emitZoom()
}

function onWheel(event: Konva.KonvaEventObject<WheelEvent>): void {
  event.evt.preventDefault()
  const pointer = stage()?.getPointerPosition()
  if (!pointer) return

  const factor = event.evt.deltaY > 0 ? 1 / 1.15 : 1.15
  const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, view.scale * factor))
  if (next === view.scale) return

  // 以光标为锚点
  view.x = pointer.x - (pointer.x - view.x) * (next / view.scale)
  view.y = pointer.y - (pointer.y - view.y) * (next / view.scale)
  view.scale = next
  emitZoom()
}

function loadImage(url: string): void {
  image.value = null
  if (!url) return
  const element = new window.Image()
  element.onload = () => {
    image.value = element
    if (size.width > 0 && size.height > 0) fit()
  }
  element.src = url
}

/** #rrggbb → rgba()，填充要半透明但描边不能跟着淡。 */
function withAlpha(color: string, alpha: number): string {
  if (!color.startsWith('#')) return color
  const hex = color.slice(1)
  const full = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex
  const value = Number.parseInt(full, 16)
  if (Number.isNaN(value)) return color
  const r = (value >> 16) & 255
  const g = (value >> 8) & 255
  const b = value & 255
  return `rgba(${r},${g},${b},${alpha})`
}

/** 所有图形共用的描边/填充配置。 */
function shapeStyle(shape: LabelmeShape, index: number): Record<string, unknown> {
  const color = props.colorOf(shape.label)
  const selected = index === props.selected
  const isLine = shape.shape_type === 'line'
  return {
    stroke: color,
    strokeWidth: selected ? 3 : 2,
    strokeScaleEnabled: false,
    dash: selected ? [6, 3] : undefined,
    fill: isLine ? undefined : withAlpha(color, 0.12),
    fillEnabled: !isLine,
    hitStrokeWidth: 12,
    listening: props.tool === 'select',
  }
}

function pointsOf(shape: LabelmeShape, index: number): Point[] {
  if (index === props.selected && editPoints.value) return editPoints.value
  return shape.points
}

function rectConfig(shape: LabelmeShape, index: number): Record<string, unknown> {
  const points = pointsOf(shape, index)
  const a = points[0] ?? [0, 0]
  const b = points[1] ?? a
  return {
    ...shapeStyle(shape, index),
    x: Math.min(a[0], b[0]),
    y: Math.min(a[1], b[1]),
    width: Math.abs(b[0] - a[0]),
    height: Math.abs(b[1] - a[1]),
  }
}

function circleConfig(shape: LabelmeShape, index: number): Record<string, unknown> {
  const points = pointsOf(shape, index)
  const center = points[0] ?? [0, 0]
  const edge = points[1] ?? center
  return {
    ...shapeStyle(shape, index),
    x: center[0],
    y: center[1],
    radius: Math.hypot(edge[0] - center[0], edge[1] - center[1]),
  }
}

function polyConfig(shape: LabelmeShape, index: number): Record<string, unknown> {
  const points = pointsOf(shape, index)
  return {
    ...shapeStyle(shape, index),
    points: points.flat(),
    closed: shape.shape_type === 'polygon',
    lineJoin: 'round',
  }
}

/** 顶点手柄：只给选中的形状显示，绘制模式下隐藏。 */
const vertexHandles = computed<Point[]>(() => {
  if (props.tool !== 'select' || props.selected == null) return []
  const shape = props.shapes[props.selected]
  if (!shape) return []
  return editPoints.value ?? shape.points
})

function vertexConfig(point: Point, index: number): Record<string, unknown> {
  const sizePx = HANDLE_SIZE / view.scale
  const shape = props.selected == null ? null : props.shapes[props.selected]
  const color = shape ? props.colorOf(shape.label) : '#1e88e5'
  return {
    x: point[0],
    y: point[1],
    width: sizePx,
    height: sizePx,
    offsetX: sizePx / 2,
    offsetY: sizePx / 2,
    fill: '#ffffff',
    stroke: color,
    strokeWidth: 2 / view.scale,
    strokeScaleEnabled: false,
    draggable: !draggingVertex,
    name: `vertex-${index}`,
    listening: true,
  }
}

const DRAFT_COLOR = '#1e88e5'

const draftRect = computed<Record<string, unknown> | null>(() => {
  const g = gesture.value
  if (!g || g.kind !== 'rect') return null
  const [a, b] = rectPoints(g.start, g.current)
  return {
    x: Math.min(a![0], b![0]),
    y: Math.min(a![1], b![1]),
    width: Math.abs(b![0] - a![0]),
    height: Math.abs(b![1] - a![1]),
    stroke: DRAFT_COLOR,
    strokeWidth: 2,
    strokeScaleEnabled: false,
    dash: [6, 3],
    fill: withAlpha(DRAFT_COLOR, 0.1),
    listening: false,
  }
})

const draftCircle = computed<Record<string, unknown> | null>(() => {
  const g = gesture.value
  if (!g || g.kind !== 'circle') return null
  return {
    x: g.start[0],
    y: g.start[1],
    radius: Math.hypot(g.current[0] - g.start[0], g.current[1] - g.start[1]),
    stroke: DRAFT_COLOR,
    strokeWidth: 2,
    strokeScaleEnabled: false,
    dash: [6, 3],
    fill: withAlpha(DRAFT_COLOR, 0.1),
    listening: false,
  }
})

const draftLine = computed<Record<string, unknown> | null>(() => {
  const points = pending.value
  if (points.length === 0) return null
  const tail = hoverPoint.value ?? points[points.length - 1]!
  return {
    points: [...points, tail].flat(),
    stroke: DRAFT_COLOR,
    strokeWidth: 2,
    strokeScaleEnabled: false,
    dash: [6, 3],
    closed: false,
    listening: false,
  }
})

function baseShape(): Omit<LabelmeShape, 'points' | 'shape_type'> {
  return { label: props.label, group_id: null, description: '', flags: {} }
}

function gestureShape(g: Gesture): LabelmeShape | null {
  if (g.kind === 'rect') {
    const [a, b] = rectPoints(g.start, g.current)
    if (Math.abs(b![0] - a![0]) < 2 || Math.abs(b![1] - a![1]) < 2) return null
    return { ...baseShape(), points: [a!, b!], shape_type: 'rectangle' }
  }
  const radius = Math.hypot(g.current[0] - g.start[0], g.current[1] - g.start[1])
  if (radius < 2) return null
  return {
    ...baseShape(),
    points: [g.start, [g.start[0] + radius, g.start[1]]],
    shape_type: 'circle',
  }
}

function finishPolyline(candidates?: Point[]): void {
  const points = candidates ?? pending.value.slice()
  const min = props.tool === 'line' ? 2 : 3
  gesture.value = null
  pending.value = []
  if (points.length < min) return
  emit('create', {
    ...baseShape(),
    points,
    shape_type: props.tool === 'line' ? 'line' : 'polygon',
  })
}

function onMouseDown(event: Konva.KonvaEventObject<MouseEvent>): void {
  const stageInstance = stage()
  const position = stageInstance?.getPointerPosition()
  if (!stageInstance || !position) return

  pointerMoved = false
  pressAt = [position.x, position.y]

  const middle = event.evt.button === 1
  const panOnEmpty = props.tool === 'select' && event.target === stageInstance
  if (middle || spaceHeld.value || panOnEmpty) {
    panStart = { pointer: [view.x, view.y], origin: [position.x, position.y] }
    panning.value = true
    return
  }

  if (event.evt.button !== 0) return
  if (props.tool === 'rect' || props.tool === 'circle') {
    const point = clampPoint(toWorld([position.x, position.y]))
    gesture.value = { kind: props.tool, start: point, current: point }
  }
}

function onMouseMove(event: Konva.KonvaEventObject<MouseEvent>): void {
  const position = stage()?.getPointerPosition()
  if (!position) return

  if (pressAt && Math.hypot(position.x - pressAt[0], position.y - pressAt[1]) > 2) {
    pointerMoved = true
  }

  if (panStart) {
    view.x = panStart.pointer[0] + (position.x - panStart.origin[0])
    view.y = panStart.pointer[1] + (position.y - panStart.origin[1])
    emitZoom()
    return
  }

  const point = toWorld([position.x, position.y])
  emit('cursor', point[0], point[1])

  if (props.tool === 'polygon' || props.tool === 'line') {
    hoverPoint.value = clampPoint(point)
  }
  if (gesture.value) {
    gesture.value = { ...gesture.value, current: clampPoint(point) }
  }
  void event
}

function onMouseUp(): void {
  pressAt = null
  if (panning.value) {
    panning.value = false
    panStart = null
    return
  }
  const g = gesture.value
  if (!g) return
  gesture.value = null
  const shape = gestureShape(g)
  if (shape) emit('create', shape)
}

function onStageClick(event: Konva.KonvaEventObject<MouseEvent>): void {
  if (pointerMoved) return

  if (props.tool === 'polygon' || props.tool === 'line') {
    const point = pointerWorld()
    if (point) pending.value = [...pending.value, clampPoint(point)]
    return
  }

  if (props.tool === 'select' && event.target === stage()) emit('select', null)
}

function onDblClick(): void {
  if (props.tool !== 'polygon' && props.tool !== 'line') return
  // 双击前已经落过两次 click，末尾是同一个点，去掉重复的那个
  const points = pending.value.slice()
  while (
    points.length >= 2 &&
    Math.hypot(
      points[points.length - 1]![0] - points[points.length - 2]![0],
      points[points.length - 1]![1] - points[points.length - 2]![1],
    ) < 3
  ) {
    points.pop()
  }
  finishPolyline(points)
}

function onContextMenu(event: Konva.KonvaEventObject<PointerEvent>): void {
  event.evt.preventDefault()
  gesture.value = null
  pending.value = []
}

function onShapeMouseDown(index: number, event: Konva.KonvaEventObject<MouseEvent>): void {
  event.cancelBubble = true
  if (props.tool !== 'select') return
  emit('select', index)
}

function onVertexMouseDown(event: Konva.KonvaEventObject<MouseEvent>): void {
  event.cancelBubble = true
  draggingVertex = true
}

function onVertexDragEnd(index: number, event: Konva.KonvaEventObject<DragEvent>): void {
  draggingVertex = false
  const shapeIndex = props.selected
  if (shapeIndex == null) return
  const shape = props.shapes[shapeIndex]
  if (!shape) return

  const node = event.target as Konva.Rect
  const points = clonePoints(shape.points)
  points[index] = clampPoint([node.x(), node.y()])

  const next = shape.shape_type === 'rectangle'
    ? (() => {
        const [a, b] = rectPoints(points[0]!, points[1] ?? points[0]!)
        return [a!, b!]
      })()
    : points

  editPoints.value = next
  emit('update', shapeIndex, { ...shape, points: next })
}

function onKeyDown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    gesture.value = null
    pending.value = []
    if (props.tool === 'select') emit('select', null)
    return
  }

  if (event.key === 'Enter' && pending.value.length > 0) {
    finishPolyline()
    return
  }

  if (event.code !== 'Space' || event.repeat || spaceHeld.value) return
  const target = event.target as HTMLElement | null
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return
  event.preventDefault()
  spaceHeld.value = true
  host.value?.classList.add('is-panning')
}

function onKeyUp(event: KeyboardEvent): void {
  if (event.code !== 'Space' || !spaceHeld.value) return
  spaceHeld.value = false
  if (!panning.value) host.value?.classList.remove('is-panning')
}

let fitted = false

function maybeFit(): void {
  if (fitted || !image.value || size.width <= 0 || size.height <= 0) return
  fitted = true
  fit()
}

watch(
  () => props.imageUrl,
  (url) => {
    fitted = false
    loadImage(url)
  },
)

watch(
  () => props.tool,
  () => {
    gesture.value = null
    pending.value = []
    editPoints.value = null
    host.value?.classList.toggle('is-panning', spaceHeld.value)
  },
)

watch(
  () => props.selected,
  () => {
    editPoints.value = null
  },
)

watch(
  () => [props.width, props.height] as const,
  () => {
    if (!fitted) maybeFit()
  },
)

onMounted(() => {
  const element = host.value
  if (element) {
    const rect = element.getBoundingClientRect()
    size.width = Math.round(rect.width)
    size.height = Math.round(rect.height)

    resizeObserver = new ResizeObserver(() => {
      const next = element.getBoundingClientRect()
      size.width = Math.round(next.width)
      size.height = Math.round(next.height)
      maybeFit()
    })
    resizeObserver.observe(element)
  }

  loadImage(props.imageUrl)
  window.addEventListener('keydown', onKeyDown)
  window.addEventListener('keyup', onKeyUp)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
  window.removeEventListener('keydown', onKeyDown)
  window.removeEventListener('keyup', onKeyUp)
  image.value = null
})

defineExpose({
  zoomIn: () => zoomTo(view.scale * 1.25),
  zoomOut: () => zoomTo(view.scale / 1.25),
  fit,
  actualSize: () => zoomTo(1),
  centerOf: (shape: LabelmeShape) => {
    const box = boundsOf(shape.points)
    view.x = size.width / 2 - ((box.minX + box.maxX) / 2) * view.scale
    view.y = size.height / 2 - ((box.minY + box.maxY) / 2) * view.scale
  },
})
</script>

<style scoped>
.annotator-host {
  position: relative;
  width: 100%;
  height: 100%;
  background: #1b1b1f;
  overflow: hidden;
  cursor: default;
}

.annotator-host.is-panning {
  cursor: grabbing;
}

.annotator-empty {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  color: rgba(255, 255, 255, 0.55);
  font-size: 14px;
  pointer-events: none;
}
</style>
