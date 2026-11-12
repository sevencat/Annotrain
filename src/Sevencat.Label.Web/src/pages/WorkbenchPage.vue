<template>
  <div class="wb column">
    <!-- 工具栏 -->
    <div class="wb-bar row items-center no-wrap q-px-sm q-gutter-xs">
      <q-btn flat dense round icon="arrow_back" @click="back">
        <q-tooltip>回项目列表</q-tooltip>
      </q-btn>

      <div class="text-subtitle2 ellipsis" style="max-width: 220px">
        {{ project.current?.name || '未进入项目' }}
      </div>
      <q-badge color="primary" outline>待标注 {{ project.pending }}</q-badge>

      <q-separator vertical class="q-mx-xs" />

      <q-btn-toggle
        v-model="tool"
        dense
        unelevated
        toggle-color="primary"
        color="grey-3"
        text-color="grey-9"
        :options="toolOptions"
      />

      <q-separator vertical class="q-mx-xs" />

      <q-btn flat dense icon="undo" :disable="!task.canUndo" @click="task.undo()">
        <q-tooltip>撤销 Ctrl+Z</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="redo" :disable="!task.canRedo" @click="task.redo()">
        <q-tooltip>重做 Ctrl+Y</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="content_copy" :disable="task.selected === null" @click="task.duplicateSelected()">
        <q-tooltip>复制 Ctrl+D</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="delete" :disable="task.selected === null" @click="task.deleteSelected()">
        <q-tooltip>删除 Del</q-tooltip>
      </q-btn>

      <q-separator vertical class="q-mx-xs" />

      <q-btn flat dense icon="save" :loading="task.busy" @click="save">
        <q-tooltip>保存 Ctrl+S</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="skip_next" :loading="task.busy" @click="task.claimNext()">
        <q-tooltip>下一张</q-tooltip>
      </q-btn>
      <q-btn flat dense icon="cloud_upload" :loading="task.busy" @click="uploadAndNext">
        上传并继续
      </q-btn>
      <q-btn flat dense icon="block" :disable="!task.image" @click="skip">跳过</q-btn>
      <q-btn flat dense icon="assignment_return" :disable="!task.image" @click="returnToServer">退回</q-btn>

      <q-space />

      <q-btn flat dense icon="zoom_in" @click="canvas?.zoomIn()" />
      <q-btn flat dense icon="zoom_out" @click="canvas?.zoomOut()" />
      <q-btn flat dense icon="fit_screen" @click="canvas?.fit()">
        <q-tooltip>适应窗口 F</q-tooltip>
      </q-btn>
    </div>

    <!-- 备注条（驳回原因 / 跳过原因） -->
    <div v-if="task.remark" class="remark-bar row items-center q-px-md">
      <q-icon name="sticky_note_2" size="18px" class="q-mr-sm" />
      <span class="ellipsis">{{ task.remark }}</span>
    </div>

    <!-- 画布 + 右侧面板 -->
    <div class="row col no-wrap" style="min-height: 0">
      <div class="col relative-position" style="min-width: 0">
        <AnnotatorCanvas
          ref="canvas"
          :image-url="task.fullUrl"
          :width="task.image?.width ?? 0"
          :height="task.image?.height ?? 0"
          :shapes="task.shapes"
          :selected="task.selected"
          :tool="task.tool"
          :label="task.currentLabel"
          :color-of="project.colorOf"
          :hint="task.emptyHint"
          class="fit"
          @create="task.addShape($event)"
          @update="(index, shape) => task.updateShape(index, shape)"
          @select="task.select($event)"
          @cursor="(x, y) => { cursor.x = x; cursor.y = y }"
          @zoom="(value) => (zoomPercent = value)"
        />
      </div>

      <aside class="side-panel column">
        <div class="panel-title">标签（{{ project.classes.length }}）</div>
        <div class="panel-body">
          <q-list dense>
            <q-item
              v-for="item in project.classes"
              :key="item.orderIndex"
              clickable
              :active="item.name === task.currentLabel"
              active-class="bg-primary text-white"
              @click="task.applyLabel(item.name)"
            >
              <q-item-section avatar style="min-width: 24px">
                <span class="dot" :style="{ background: item.color || project.colorOf(item.name) }" />
              </q-item-section>
              <q-item-section>{{ item.name }}</q-item-section>
            </q-item>
          </q-list>
          <div v-if="project.classes.length === 0" class="text-caption text-grey-6 q-pa-sm">
            项目还没配类别，先在服务端加类别再来标
          </div>
        </div>

        <q-separator />

        <div class="panel-title">形状（{{ task.shapes.length }}）</div>
        <div class="panel-body">
          <q-list dense>
            <q-item
              v-for="(shape, index) in task.shapes"
              :key="shape.id ?? index"
              clickable
              :active="index === task.selected"
              active-class="bg-blue-1"
              @click="pickShape(index)"
            >
              <q-item-section avatar style="min-width: 24px">
                <span class="dot" :style="{ background: project.colorOf(shape.label) }" />
              </q-item-section>
              <q-item-section>
                <q-item-label class="ellipsis">{{ shape.label }}</q-item-label>
                <q-item-label caption>{{ shapeTypeText(shape) }}</q-item-label>
              </q-item-section>
              <q-item-section side>
                <q-btn flat dense round size="sm" icon="close" @click.stop="selectAndDelete(index)" />
              </q-item-section>
            </q-item>
          </q-list>
          <div v-if="task.shapes.length === 0" class="text-caption text-grey-6 q-pa-sm">
            画布上画一个就出现在这里
          </div>
        </div>

        <q-separator />

        <div class="panel-title">备注（跳过/退回时带出去）</div>
        <div class="q-pa-sm">
          <q-input v-model="task.remark" dense outlined type="textarea" autogrow placeholder="可留空" />
        </div>
      </aside>
    </div>

    <!-- 状态栏 -->
    <div class="wb-status row items-center no-wrap q-px-sm">
      <span class="ellipsis" style="max-width: 46%">{{ task.image?.relPath || '—' }}</span>
      <q-separator vertical class="q-mx-sm" />
      <span>{{ task.image ? `${task.image.width}×${task.image.height}` : '—' }}</span>
      <q-separator vertical class="q-mx-sm" />
      <span>{{ zoomPercent }}%</span>
      <q-separator vertical class="q-mx-sm" />
      <span>{{ Math.round(cursor.x) }}, {{ Math.round(cursor.y) }}</span>
      <q-separator vertical class="q-mx-sm" />
      <span>形状 {{ task.shapes.length }}</span>
      <q-separator vertical class="q-mx-sm" />
      <span>r{{ task.revision }}</span>
      <q-space />
      <span :class="task.dirty ? 'text-warning' : 'text-grey-7'">{{ task.dirty ? '未保存' : '已保存' }}</span>
      <q-separator vertical class="q-mx-sm" />
      <span class="ellipsis" style="max-width: 38%">{{ task.status }}</span>
    </div>

    <!-- 冲突对话框 -->
    <q-dialog v-model="conflictOpen" persistent>
      <q-card style="min-width: 380px">
        <q-card-section class="text-subtitle1">保存冲突</q-card-section>
        <q-card-section class="q-pt-none text-body2">
          {{ task.conflict?.message || '这张图已被别人改过，服务端版本更新。' }}
          <div v-if="task.conflict?.conflict?.current" class="text-caption text-grey-7 q-mt-sm">
            服务端当前：r{{ task.conflict.conflict.current.revision }} ·
            {{ task.conflict.conflict.current.assigneeName || '未分配' }}
          </div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat label="取消" @click="conflictOpen = false" />
          <q-btn flat label="重新加载" @click="reload" />
          <q-btn color="primary" unelevated label="覆盖保存" @click="overwrite" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- 跳过原因：下拉选一条常用的，也可以自己写 -->
    <q-dialog v-model="skipOpen">
      <q-card style="min-width: 400px">
        <q-card-section class="text-subtitle1">跳过这张图</q-card-section>
        <q-card-section class="q-pt-none">
          <div class="text-caption text-grey-7 q-mb-sm">
            跳过会置为「已跳过」，管理员重置前谁也领不到；原因会写进图片备注。
          </div>
          <q-select
            v-model="skipReason"
            :options="SKIP_PRESETS"
            dense
            outlined
            use-input
            fill-input
            hide-selected
            new-value-mode="add-unique"
            input-debounce="0"
            label="跳过原因"
            hint="下拉选一条，也可以直接输入"
          />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat label="取消" @click="skipOpen = false" />
          <q-btn color="primary" unelevated label="确认跳过" @click="confirmSkip" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<script setup lang="ts">
import { useQuasar } from 'quasar'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AnnotatorCanvas from '@/components/AnnotatorCanvas.vue'
import type { LabelmeShape } from '@/services/labelme'
import { useProjectStore } from '@/stores/project'
import { useTaskStore } from '@/stores/task'
import type { ToolKind } from '@/types/api'

const $q = useQuasar()
const route = useRoute()
const router = useRouter()
const project = useProjectStore()
const task = useTaskStore()

const canvas = ref<InstanceType<typeof AnnotatorCanvas> | null>(null)
const cursor = ref({ x: 0, y: 0 })
const zoomPercent = ref(100)
const conflictOpen = ref(false)

/** 跳过原因：预设几条常用的，允许自己写。 */
const SKIP_PRESETS = [
  '图片模糊，看不清',
  '目标太小，无法辨认',
  '图片损坏，打不开',
  '与本项目无关',
  '重复图片',
  '需要管理员处理',
]
const skipOpen = ref(false)
const skipReason = ref('')

const toolOptions = [
  { label: '选择', value: 'select' },
  { label: '矩形', value: 'rect' },
  { label: '多边形', value: 'polygon' },
  { label: '圆', value: 'circle' },
  { label: '线', value: 'line' },
]

const tool = computed<ToolKind>({
  get: () => task.tool,
  set: (value) => task.setTool(value),
})

const SHAPE_TEXT: Record<string, string> = {
  rectangle: '矩形',
  polygon: '多边形',
  circle: '圆',
  line: '线',
  mask: '掩码',
}

function shapeTypeText(shape: LabelmeShape): string {
  return SHAPE_TEXT[shape.shape_type] ?? shape.shape_type
}

function pickShape(index: number): void {
  task.select(index)
  const shape = task.shapes[index]
  if (shape) canvas.value?.centerOf(shape)
}

function selectAndDelete(index: number): void {
  task.select(index)
  task.deleteSelected()
}

async function save(): Promise<void> {
  const ok = await task.save()
  if (ok) $q.notify({ type: 'positive', message: '已保存', timeout: 1200 })
}

async function uploadAndNext(): Promise<void> {
  const ok = await task.save()
  if (!ok) return
  await task.claimNext()
}

function skip(): void {
  skipReason.value = task.remark || ''
  skipOpen.value = true
}

async function confirmSkip(): Promise<void> {
  skipOpen.value = false
  task.remark = skipReason.value.trim()
  await task.skip()
}

async function returnToServer(): Promise<void> {
  await task.returnToServer()
}

async function reload(): Promise<void> {
  await task.reloadFromServer()
  conflictOpen.value = false
}

async function overwrite(): Promise<void> {
  const ok = await task.overwrite()
  conflictOpen.value = !ok
  if (ok) $q.notify({ type: 'positive', message: '已覆盖保存' })
}

async function back(): Promise<void> {
  await task.leave()
  await router.replace('/projects')
}

function onKeydown(event: KeyboardEvent): void {
  const target = event.target as HTMLElement | null
  if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
    return
  }

  if (event.ctrlKey || event.metaKey) {
    switch (event.key.toLowerCase()) {
      case 's':
        event.preventDefault()
        void save()
        return
      case 'z':
        event.preventDefault()
        task.undo()
        return
      case 'y':
        event.preventDefault()
        task.redo()
        return
      case 'd':
        event.preventDefault()
        task.duplicateSelected()
        return
      case '0':
        event.preventDefault()
        canvas.value?.actualSize()
        return
      default:
        return
    }
  }

  switch (event.key.toLowerCase()) {
    case 's':
      task.setTool('select')
      break
    case 'r':
      task.setTool('rect')
      break
    case 'p':
      task.setTool('polygon')
      break
    case 'c':
      task.setTool('circle')
      break
    case 'l':
      task.setTool('line')
      break
    case 'f':
      canvas.value?.fit()
      break
    case 'escape':
      task.setTool('select')
      break
    case 'delete':
    case 'backspace':
      event.preventDefault()
      task.deleteSelected()
      break
  }
}

watch(
  () => task.conflict,
  (value) => {
    conflictOpen.value = Boolean(value)
  },
)

onMounted(async () => {
  const projectId = Number(route.query.project ?? 0)
  if (!projectId) {
    await router.replace('/projects')
    return
  }

  if (!project.current || project.current.id !== projectId) {
    try {
      await project.open(projectId)
    } catch (error) {
      $q.notify({ type: 'negative', message: (error as Error).message })
      await router.replace('/projects')
      return
    }
  }

  if (!task.image) await task.claimNext()
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  void task.leave()
})
</script>

<style scoped>
/* 浅色工作台：配色跟项目列表、桌面端（PanelBackground #fff / PanelBorder #e3e3ea / Canvas #f2f3f7）一致 */
.wb {
  position: fixed;
  inset: 0;
  background: #f5f5f7;
  color: #1f2329;
}

.wb-bar {
  background: #ffffff;
  min-height: 48px;
  border-bottom: 1px solid #e3e3ea;
}

.remark-bar {
  background: #fff7e0;
  color: #8a6100;
  min-height: 30px;
  font-size: 13px;
  gap: 4px;
  border-bottom: 1px solid #f0e0b8;
}

.side-panel {
  width: 280px;
  background: #ffffff;
  border-left: 1px solid #e3e3ea;
  min-height: 0;
}

.panel-title {
  padding: 8px 12px;
  font-size: 12px;
  color: #5f6368;
  background: #f5f5f7;
  border-bottom: 1px solid #ececf2;
}

.panel-body {
  flex: 0 1 auto;
  overflow: auto;
  max-height: 34vh;
}

.panel-body :deep(.q-item__label--caption) {
  color: #8a8f98;
}

.dot {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.wb-status {
  min-height: 26px;
  background: #ffffff;
  border-top: 1px solid #e3e3ea;
  font-size: 12px;
  color: #5f6368;
  gap: 6px;
}
</style>
