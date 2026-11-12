import type { ApiConflict, ApiError } from '@/types/api'

/**
 * 极薄的 fetch 封装：
 * - 一律走同源相对路径：页面由服务端自己托管，开发期由 Vite 代理 `/api`，都没有跨域；
 * - 统一带 `Authorization: Bearer`；
 * - 统一解析服务端的 `ApiError` / `ApiConflict`；
 * - 401 直接清 token，让路由守卫把人送回登录页。
 */

const TOKEN_KEY = 'annotrain.token'

export class HttpError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly conflict: ApiConflict | null = null,
  ) {
    super(message)
    this.name = 'HttpError'
  }

  /** 网络层失败（没拿到响应）时 status 为 0。 */
  get isNetwork(): boolean {
    return this.status === 0
  }
}

export const session = {
  get token(): string {
    return localStorage.getItem(TOKEN_KEY) ?? ''
  },
  set token(value: string) {
    if (value) localStorage.setItem(TOKEN_KEY, value)
    else localStorage.removeItem(TOKEN_KEY)
  },

  clear(): void {
    localStorage.removeItem(TOKEN_KEY)
  },
}

/**
 * 图片地址：`<img>` 带不了请求头，所以走 `?token=`。
 * 服务端只对 `/content` 与 `/thumb` 的 GET 放行 query token（见 TokenAuthMiddleware）。
 */
export function imageUrl(imageId: number, kind: 'content' | 'thumb', size = 1024): string {
  const query = kind === 'thumb' ? `?size=${size}&token=${encodeURIComponent(session.token)}` : `?token=${encodeURIComponent(session.token)}`
  return `/api/images/${imageId}/${kind}${query}`
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

export async function http<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body !== undefined && init.body !== null && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  if (session.token) headers.set('Authorization', `Bearer ${session.token}`)

  let response: Response
  try {
    response = await fetch(path, { ...init, headers })
  } catch (error) {
    throw new HttpError(0, 'network', `连不上服务器：${(error as Error).message}`)
  }

  if (response.status === 401) {
    // 登录接口本身也返回 401，这种情况下服务端消息（账号或密码错误）更有用
    const text = await response.text()
    const body = text ? parseJson(text) : null
    session.token = ''
    throw new HttpError(401, 'unauthorized', (body as ApiError | null)?.message ?? '登录已过期，请重新登录')
  }

  // 204（没有待标注图片等）没有响应体
  if (response.status === 204) return undefined as T

  const text = await response.text()
  const body = text ? parseJson(text) : null

  if (!response.ok) {
    const conflict = response.status === 409 ? (body as ApiConflict | null) : null
    const apiError = (conflict?.error ?? body) as ApiError | null
    throw new HttpError(
      response.status,
      apiError?.code ?? 'error',
      apiError?.message ?? `请求失败（HTTP ${response.status}）`,
      conflict,
    )
  }

  return body as T
}
