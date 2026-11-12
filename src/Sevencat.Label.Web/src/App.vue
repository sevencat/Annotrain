<template>
  <!--
    QPage / QPageContainer 必须放在 QLayout 里，否则页面会被渲染成空注释节点（表现为白屏）。
    工作台自己用 position: fixed 铺满窗口，放进 page-container 也不受影响。
  -->
  <q-layout view="hHh lpR fFf">
    <q-page-container>
      <router-view />
    </q-page-container>
  </q-layout>
</template>
