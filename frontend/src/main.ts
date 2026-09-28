import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import './style.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: () => import('./views/OverviewView.vue') },
    { path: '/leads', component: () => import('./views/LeadsView.vue') },
    { path: '/leads/:id', component: () => import('./views/LeadDetailView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
  ],
})

createApp(App).use(router).mount('#app')
