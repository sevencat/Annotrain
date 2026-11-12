/**
 * labelme 文档的读写（与服务端 `LabelmeSerializer` 对齐）。
 *
 * 关键约定：
 * - 矩形固定两个点：`[左上, 右下]`；
 * - 圆固定两个点：`[圆心, 圆周一点]`（桌面端同款语义，半径 = 两点距离）；
 * - 掩码：`points` 是外接框，`mask` 是 base64 PNG（本轮 Web 端暂不生成，但读得懂、能原样回传）；
 * - `annotrain` 是项目私有字段（Core 的 `AnnotrainInfo.MetadataNamespace`），Web 端**原样保留**，不要重建。
 */

export type ShapeType = 'rectangle' | 'polygon' | 'circle' | 'line' | 'mask'

export type Point = [number, number]

export interface LabelmeShape {
  label: string
  points: Point[]
  group_id: number | null
  description: string
  shape_type: ShapeType
  flags: Record<string, boolean>
  id?: string
  mask?: string
}

export interface AnnotrainMeta {
  id?: string
  revision?: number
  updatedAt?: string
  updatedBy?: string
}

export interface LabelmeDoc {
  version: string
  flags: Record<string, boolean>
  shapes: LabelmeShape[]
  imagePath?: string
  imageData?: string | null
  imageHeight: number
  imageWidth: number
  annotrain?: AnnotrainMeta
  [key: string]: unknown
}

/** 与 Core 的 `LabelmeCodec` 默认版本一致；不一致也没关系，服务端会按自己的版本重写。 */
export const LABELME_VERSION = '5.0.1'

export function newShapeId(): string {
  return Math.random().toString(16).slice(2, 10)
}

export function emptyDoc(width: number, height: number, imagePath = ''): LabelmeDoc {
  return {
    version: LABELME_VERSION,
    flags: {},
    shapes: [],
    imagePath,
    imageHeight: Math.max(1, Math.round(height)),
    imageWidth: Math.max(1, Math.round(width)),
  }
}

/** 解析服务端返回的 docJson；字段缺失时补默认值，坏 JSON 直接抛错（不要静默吞掉）。 */
export function parseDoc(json: string, width: number, height: number): LabelmeDoc {
  const raw = JSON.parse(json) as Partial<LabelmeDoc> | null
  if (!raw || typeof raw !== 'object') {
    return emptyDoc(width, height)
  }

  const shapes = Array.isArray(raw.shapes) ? raw.shapes : []
  return {
    ...raw,
    version: raw.version ?? LABELME_VERSION,
    flags: raw.flags ?? {},
    imageHeight: Math.max(1, Math.round(raw.imageHeight ?? height)),
    imageWidth: Math.max(1, Math.round(raw.imageWidth ?? width)),
    shapes: shapes
      .filter((s) => s && Array.isArray(s.points) && s.points.length > 0)
      .map((s) => ({
        label: s.label ?? '',
        points: s.points.map((p) => [Number(p[0]), Number(p[1])] as Point),
        group_id: s.group_id ?? null,
        description: s.description ?? '',
        shape_type: (s.shape_type ?? 'polygon') as ShapeType,
        flags: s.flags ?? {},
        ...(s.id ? { id: s.id } : {}),
        ...(s.mask ? { mask: s.mask } : {}),
      })),
  }
}

export function stringifyDoc(doc: LabelmeDoc): string {
  // 与服务端一样不带 imageData（原图单独走 /content，避免 JSON 里塞几 MB base64）
  const { imageData: _ignored, ...rest } = doc
  return JSON.stringify(rest)
}

export function clonePoints(points: Point[]): Point[] {
  return points.map(([x, y]) => [x, y] as Point)
}

/** 补齐/裁剪到图内；`allowOutOfBounds` 为 false 时夹到 `[0, w-1] × [0, h-1]`。 */
export function normalizePoint(p: Point, width: number, height: number, allowOutOfBounds: boolean): Point {
  if (allowOutOfBounds) return [p[0], p[1]]
  return [clamp(p[0], 0, width - 1), clamp(p[1], 0, height - 1)]
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

/** 两点归一成矩形的「左上 + 右下」，保证 x0<=x1、y0<=y1。 */
export function rectPoints(a: Point, b: Point): [Point, Point] {
  return [
    [Math.min(a[0], b[0]), Math.min(a[1], b[1])],
    [Math.max(a[0], b[0]), Math.max(a[1], b[1])],
  ]
}

export function boundsOf(points: Point[]): { minX: number; minY: number; maxX: number; maxY: number } {
  const xs = points.map((p) => p[0])
  const ys = points.map((p) => p[1])
  return {
    minX: Math.min(...xs),
    minY: Math.min(...ys),
    maxX: Math.max(...xs),
    maxY: Math.max(...ys),
  }
}

export function moveShape(shape: LabelmeShape, dx: number, dy: number): LabelmeShape {
  return { ...shape, points: shape.points.map(([x, y]) => [x + dx, y + dy] as Point) }
}

/** 形状中心，用于复制时偏移与列表定位。 */
export function centerOf(shape: LabelmeShape): Point {
  const b = boundsOf(shape.points)
  return [(b.minX + b.maxX) / 2, (b.minY + b.maxY) / 2]
}
