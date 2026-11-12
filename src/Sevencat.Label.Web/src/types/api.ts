/**
 * 服务端 DTO 的 TypeScript 镜像。
 *
 * 源头是 Core 的模型（`src/Sevencat.Core/Model`），服务端用 System.Text.Json 的 Web 默认设置（camelCase），
 * 所以这里字段全小驼峰。**改契约时两边一起改**，这里没有代码生成兜底。
 */

export interface UserDto {
  id: number
  userName: string
  displayName: string
}

export interface LoginResponse {
  token: string
  expiresAt: string
  user: UserDto
}

export interface ProjectDto {
  id: number
  name: string
  description: string
  storageMode: string
  createdAt: string
  imageCount: number
  labeledCount: number
  excludedCount: number
  closedAt: string | null
  memberCount: number
  isClosed: boolean
  isRestricted: boolean
}

export interface ProjectClassDto {
  name: string
  orderIndex: number
  color: string
}

export interface ProjectDetailDto {
  project: ProjectDto
  classes: ProjectClassDto[]
}

export interface UserWorkloadDto {
  userId: number
  displayName: string
  assigned: number
  labeled: number
  reviewed: number
}

export interface ProjectStatsDto {
  total: number
  pending: number
  labeled: number
  reviewed: number
  excluded: number
  unassigned: number
  skipped: number
  workload: UserWorkloadDto[]
}

export interface ImageDto {
  id: number
  projectId: number
  fileName: string
  relPath: string
  width: number
  height: number
  fileSizeBytes: number
  sha256: string
  state: string
  revision: number
  locked: boolean
  lockedBy: number | null
  lockExpiresAt: string | null
  updatedAt: string
  assigneeId?: number | null
  assigneeName?: string | null
  remark?: string | null
  hasAnnotation?: boolean
}

export interface AnnotationDto {
  imageId: number
  revision: number
  docJson: string
  updatedAt: string
  updatedBy: number
}

export interface ApiError {
  code: string
  message: string
}

/** 409 冲突体：`error` 说明原因，`current` 是服务端最新图片状态。 */
export interface ApiConflict {
  error: ApiError
  current: ImageDto | null
}

/** 与 `ImageStateText.Display` 保持一致，别自己另翻一套。 */
const STATE_TEXT: Record<string, string> = {
  pending: '未标注',
  labeled: '已标注',
  reviewed: '已通过',
  excluded: '已排除',
  skipped: '已跳过',
}

export function stateText(state: string | undefined | null): string {
  if (!state) return ''
  return STATE_TEXT[state] ?? state
}

/** 前端工具栏的绘制类型；`mask` 留给后续掩码画笔。 */
export type ToolKind = 'select' | 'rect' | 'polygon' | 'circle' | 'line'
