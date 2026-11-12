import type {
  AnnotationDto,
  ImageDto,
  LoginResponse,
  ProjectDetailDto,
  ProjectDto,
  ProjectStatsDto,
  UserDto,
} from '@/types/api'
import { HttpError, http, imageUrl } from './http'

/**
 * 服务端 HTTP API 的薄封装，路径与 Core 的接口约定一一对应（服务端由 M3 落地）。
 * Web 端只用「标注员远端流程」这一段，管理类接口刻意不封装。
 */
export const api = {
  login(userName: string, password: string): Promise<LoginResponse> {
    return http<LoginResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ userName, password }),
    })
  },

  me(): Promise<UserDto> {
    return http<UserDto>('/api/auth/me')
  },

  projects(): Promise<ProjectDto[]> {
    return http<ProjectDto[]>('/api/projects')
  },

  projectDetail(projectId: number): Promise<ProjectDetailDto> {
    return http<ProjectDetailDto>(`/api/projects/${projectId}`)
  },

  stats(projectId: number): Promise<ProjectStatsDto> {
    return http<ProjectStatsDto>(`/api/projects/${projectId}/stats`)
  },

  /**
   * 领取下一张：服务端挑图 + 绑分配 + 加租约一次完成。
   * 没有待标注的图时服务端返回 204，这里归一成 `null`。
   */
  nextImage(projectId: number, afterImageId?: number | null): Promise<ImageDto | null> {
    const suffix = afterImageId ? `?after=${afterImageId}` : ''
    return http<ImageDto | undefined>(`/api/projects/${projectId}/next-image${suffix}`, {
      method: 'POST',
    }).then((dto) => dto ?? null)
  },

  /** 占锁/续约：租约 5 分钟，Web 端每 4 分钟静默续一次。 */
  claim(imageId: number): Promise<ImageDto> {
    return http<ImageDto>(`/api/images/${imageId}/claim`, { method: 'POST' })
  },

  /** 只解写锁、不改状态；切图或关页时尽力而为。 */
  release(imageId: number): Promise<void> {
    return http<void>(`/api/images/${imageId}/release`, { method: 'POST' })
  },

  /** 读标注；该图还没有标注时返回 `null`（服务端是 404）。 */
  async annotation(imageId: number): Promise<AnnotationDto | null> {
    try {
      return await http<AnnotationDto>(`/api/images/${imageId}/annotation`)
    } catch (error) {
      if (error instanceof HttpError && error.status === 404) return null
      throw error
    }
  },

  saveAnnotation(imageId: number, revision: number, docJson: string): Promise<AnnotationDto> {
    return http<AnnotationDto>(`/api/images/${imageId}/annotation`, {
      method: 'PUT',
      body: JSON.stringify({ revision, docJson }),
    })
  },

  skip(imageId: number, remark?: string): Promise<ImageDto> {
    return http<ImageDto>(`/api/images/${imageId}/skip`, {
      method: 'POST',
      body: JSON.stringify({ remark: remark || null }),
    })
  },

  returnImage(imageId: number, remark?: string): Promise<ImageDto> {
    return http<ImageDto>(`/api/images/${imageId}/return`, {
      method: 'POST',
      body: JSON.stringify({ remark: remark || null }),
    })
  },

  imageUrl,
}
