import type { RouteRecordRaw } from 'vue-router'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/projects' },
  { path: '/login', component: () => import('@/pages/LoginPage.vue') },
  { path: '/projects', component: () => import('@/pages/ProjectsPage.vue') },
  { path: '/workbench', component: () => import('@/pages/WorkbenchPage.vue') },

  // 兜底回项目列表（hash 路由下刷新子路径不会打到服务端）
  { path: '/:catchAll(.*)*', redirect: '/projects' },
]

export default routes
