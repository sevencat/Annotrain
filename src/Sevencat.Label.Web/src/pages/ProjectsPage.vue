<template>
  <q-page class="projects-page">
    <q-toolbar class="bg-grey-10 text-white">
      <q-toolbar-title class="text-subtitle1">
        Annotrain
        <span class="text-grey-5 text-caption q-ml-sm">{{ auth.user?.displayName || auth.user?.userName }}</span>
      </q-toolbar-title>
      <q-btn flat dense icon="refresh" :loading="loading" @click="reload">
        <q-tooltip>刷新</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="logout" @click="logout">
        <q-tooltip>退出登录</q-tooltip>
      </q-btn>
    </q-toolbar>

    <div class="q-pa-md">
      <div v-if="error" class="text-negative q-mb-md">{{ error }}</div>

      <div v-if="!loading && projects.length === 0" class="empty">
        没有可参与的项目（受限项目需要管理员把你加进成员名单）
      </div>

      <div class="row q-col-gutter-md">
        <div v-for="item in projects" :key="item.id" class="col-12 col-sm-6 col-md-4">
          <q-card flat bordered class="project-card" @click="open(item.id)">
            <q-card-section>
              <div class="row items-center no-wrap">
                <div class="col">
                  <div class="text-subtitle1 text-weight-medium ellipsis">{{ item.name }}</div>
                  <div class="text-caption text-grey-6 ellipsis">
                    {{ item.description || '（无备注）' }}
                  </div>
                </div>
                <q-badge v-if="item.isClosed" color="grey">已结束</q-badge>
              </div>

              <div class="row q-gutter-md q-mt-md text-caption text-grey-7">
                <span>图片 {{ item.imageCount }}</span>
                <span>已标 {{ item.labeledCount }}</span>
                <span v-if="item.isRestricted">成员 {{ item.memberCount }}</span>
                <span v-else>公开</span>
              </div>
            </q-card-section>
            <q-inner-loading :showing="opening === item.id" />
          </q-card>
        </div>
      </div>
    </div>
  </q-page>
</template>

<script setup lang="ts">
import { useQuasar } from 'quasar'
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useProjectStore } from '@/stores/project'

const $q = useQuasar()
const router = useRouter()
const auth = useAuthStore()
const project = useProjectStore()

const loading = ref(false)
const error = ref('')
const opening = ref<number | null>(null)

const projects = computed(() => project.projects)

async function reload(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    await project.loadProjects()
  } catch (err) {
    error.value = (err as Error).message
  } finally {
    loading.value = false
  }
}

async function open(projectId: number): Promise<void> {
  if (opening.value !== null) return
  opening.value = projectId
  try {
    await project.open(projectId)
    await router.push({ path: '/workbench', query: { project: projectId } })
  } catch (err) {
    $q.notify({ type: 'negative', message: (err as Error).message })
  } finally {
    opening.value = null
  }
}

async function logout(): Promise<void> {
  auth.logout()
  project.reset()
  await router.replace('/login')
}

onMounted(() => {
  void reload()
})
</script>

<style scoped>
.projects-page {
  background: #f5f5f7;
  min-height: 100vh;
}

.project-card {
  cursor: pointer;
  border-radius: 10px;
  transition: box-shadow 0.15s ease;
}

.project-card:hover {
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.12);
}

.empty {
  padding: 48px 0;
  text-align: center;
  color: rgba(0, 0, 0, 0.45);
}
</style>
