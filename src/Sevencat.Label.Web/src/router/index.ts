import { defineRouter } from '#q-app'
import {
  createMemoryHistory,
  createRouter,
  createWebHashHistory,
  createWebHistory,
} from 'vue-router'

import routes from './routes'

export default defineRouter((/* { store, ssrContext } */) => {
  const createHistory = import.meta.env.QUASAR_SERVER
    ? createMemoryHistory
    : (import.meta.env.QUASAR_VUE_ROUTER_MODE === 'history' ? createWebHistory : createWebHashHistory)

  const Router = createRouter({
    scrollBehavior: () => ({ left: 0, top: 0 }),
    routes,

    // Leave this as is and make changes in quasar.conf.js instead!
    // quasar.conf.js -> build -> vueRouterMode
    // quasar.conf.js -> build -> publicPath
    history: createHistory(import.meta.env.QUASAR_VUE_ROUTER_BASE),
  })

  // 未登录只允许看登录页；token 过期由 http.ts 清掉，下次跳转自然回登录
  Router.beforeEach((to) => {
    const hasToken = Boolean(localStorage.getItem('annotrain.token'))
    if (to.path === '/login') {
      return hasToken ? { path: '/projects' } : true
    }
    return hasToken ? true : { path: '/login' }
  })

  return Router
})
