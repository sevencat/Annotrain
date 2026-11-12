import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '@/services/api'
import { session } from '@/services/http'
import type { UserDto } from '@/types/api'

const USER_KEY = 'annotrain.user'
const NAME_KEY = 'annotrain.userName'

function readUser(): UserDto | null {
  try {
    return JSON.parse(localStorage.getItem(USER_KEY) ?? 'null') as UserDto | null
  } catch {
    return null
  }
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<UserDto | null>(readUser())
  const token = ref(session.token)
  const lastUserName = ref(localStorage.getItem(NAME_KEY) ?? '')

  const loggedIn = computed(() => token.value.length > 0)

  async function login(userName: string, password: string): Promise<void> {
    const response = await api.login(userName, password)

    token.value = response.token
    session.token = response.token
    user.value = response.user
    lastUserName.value = userName
    localStorage.setItem(USER_KEY, JSON.stringify(response.user))
    localStorage.setItem(NAME_KEY, userName)
  }

  function logout(): void {
    session.clear()
    token.value = ''
    user.value = null
    localStorage.removeItem(USER_KEY)
  }

  return { user, token, loggedIn, lastUserName, login, logout }
})
