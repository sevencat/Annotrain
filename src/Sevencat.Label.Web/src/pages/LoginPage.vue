<template>
  <q-page class="flex flex-center login-page">
    <q-card class="login-card q-pa-lg" flat bordered>
      <div class="text-h5 text-weight-bold">Annotrain</div>
      <div class="text-caption text-grey-6 q-mb-lg">Web 标注端 · 与桌面端共用同一套任务</div>

      <q-form greedy @submit.prevent="submit">
        <q-input
          v-model="userName"
          label="账号"
          autofocus
          outlined
          dense
          :rules="[(v) => !!v || '请填账号']"
          class="q-mb-sm"
        />
        <q-input
          v-model="password"
          label="密码"
          type="password"
          outlined
          dense
          :rules="[(v) => !!v || '请填密码']"
          class="q-mb-md"
        />

        <q-btn
          type="submit"
          color="primary"
          class="full-width"
          unelevated
          :loading="busy"
          label="登录"
        />
      </q-form>
    </q-card>
  </q-page>
</template>

<script setup lang="ts">
import { useQuasar } from 'quasar'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { HttpError } from '@/services/http'
import { useAuthStore } from '@/stores/auth'

const $q = useQuasar()
const router = useRouter()
const auth = useAuthStore()

const userName = ref(auth.lastUserName)
const password = ref('')
const busy = ref(false)

async function submit(): Promise<void> {
  if (busy.value) return
  busy.value = true
  try {
    await auth.login(userName.value.trim(), password.value)
    await router.replace('/projects')
  } catch (error) {
    const message = error instanceof HttpError
      ? (error.isNetwork ? '连不上服务器，检查网络' : error.message)
      : (error as Error).message
    $q.notify({ type: 'negative', message, timeout: 4000 })
  } finally {
    busy.value = false
  }
}
</script>

<style scoped>
.login-page {
  background: #f5f5f7;
}

.login-card {
  width: 380px;
  max-width: calc(100vw - 32px);
  border-radius: 12px;
}
</style>
