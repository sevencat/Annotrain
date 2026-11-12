import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '@/services/api'
import type { ProjectClassDto, ProjectDto } from '@/types/api'

/** 类别没配颜色时的兜底调色板（按 orderIndex 取模）。 */
const FALLBACK_COLORS = ['#1e88e5', '#43a047', '#e53935', '#fb8c00', '#8e24aa', '#00acc1', '#c0ca33', '#6d4c41']

export const useProjectStore = defineStore('project', () => {
  const projects = ref<ProjectDto[]>([])
  const current = ref<ProjectDto | null>(null)
  const classes = ref<ProjectClassDto[]>([])
  const pending = ref(0)

  async function loadProjects(): Promise<void> {
    projects.value = await api.projects()
  }

  async function open(projectId: number): Promise<void> {
    const detail = await api.projectDetail(projectId)
    current.value = detail.project
    classes.value = detail.classes
    await refreshStats()
  }

  async function refreshStats(): Promise<void> {
    if (!current.value) return
    const stats = await api.stats(current.value.id)
    pending.value = stats.pending
  }

  function colorOf(label: string): string {
    const found = classes.value.find((c) => c.name === label)
    if (found?.color) return found.color
    const index = classes.value.findIndex((c) => c.name === label)
    return FALLBACK_COLORS[Math.max(0, index) % FALLBACK_COLORS.length]!
  }

  function reset(): void {
    current.value = null
    classes.value = []
    pending.value = 0
  }

  return { projects, current, classes, pending, loadProjects, open, refreshStats, colorOf, reset }
})
