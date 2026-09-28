<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { auth } from './api'

const token = ref(auth.get())
const input = ref('')
const login = () => { auth.set(input.value.trim()); token.value = auth.get(); input.value = '' }
const logout = () => { auth.clear(); token.value = null }
const onLogout = () => (token.value = null)
onMounted(() => window.addEventListener('leadintel:logout', onLogout))
onUnmounted(() => window.removeEventListener('leadintel:logout', onLogout))
</script>

<template>
  <header class="topbar">
    <span class="brand">Lead Intel</span>
    <nav v-if="token">
      <RouterLink to="/" exact-active-class="active">Overview</RouterLink>
      <RouterLink to="/leads" active-class="active">Leads</RouterLink>
      <RouterLink to="/analytics" active-class="active">Analytics</RouterLink>
    </nav>
    <button v-if="token" class="link" @click="logout">Sign out</button>
  </header>
  <main>
    <form v-if="!token" class="card login" @submit.prevent="login">
      <h2>Sign in</h2>
      <label for="token">API token</label>
      <input id="token" v-model="input" type="password" autocomplete="current-password" required />
      <button type="submit">Continue</button>
    </form>
    <RouterView v-else />
  </main>
</template>
