<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, fmtDate, fmtPhone, scoreClass, SIGNAL_LABELS, signalLabel, STATUSES, type LeadPage, type ServiceLine } from '../api'

const route = useRoute()
const f = reactive({
  q: '', state: '', status: '', min_score: 0,
  signal_type: (route.query.signal_type as string) ?? '',
  service_line: (route.query.service_line as string) ?? '',
  sort: '-score', page: 1, page_size: 25,
})
const data = ref<LeadPage>()
const error = ref('')
const loading = ref(false)

async function load() {
  loading.value = true
  try { data.value = await api.leads({ ...f }); error.value = '' } catch (e) { error.value = String(e) }
  loading.value = false
}
let timer: number | undefined
watch(() => [f.q, f.state, f.status, f.min_score, f.signal_type, f.service_line], () => {
  f.page = 1
  clearTimeout(timer)
  timer = window.setTimeout(load, 250)
})
watch(() => [f.sort, f.page], load)
const lines = ref<ServiceLine[]>([])
const lineLabel = (k: string) => lines.value.find((l) => l.key === k)?.label ?? k
onMounted(async () => {
  load()
  try { lines.value = await api.serviceLines() } catch { /* filter just stays empty */ }
})

const sortBy = (col: string) => (f.sort = f.sort === `-${col}` ? col : `-${col}`)
const arrow = (col: string) => (f.sort === `-${col}` ? '▼' : f.sort === col ? '▲' : '')
const pages = () => Math.max(1, Math.ceil((data.value?.total ?? 0) / f.page_size))
const SIGNAL_TYPES = Object.keys(SIGNAL_LABELS)
</script>

<template>
  <h1>Leads <span v-if="data" class="muted">({{ data.total }})</span></h1>
  <form class="filters" @submit.prevent>
    <input v-model="f.q" type="search" placeholder="Search name, DOT or MC" aria-label="Search" />
    <input v-model="f.state" maxlength="2" placeholder="State" aria-label="State" class="w-state" />
    <select v-model="f.status" aria-label="Status">
      <option value="">All statuses</option>
      <option v-for="s in STATUSES" :key="s" :value="s">{{ s }}</option>
    </select>
    <select v-model="f.service_line" aria-label="Service line">
      <option value="">All service lines</option>
      <option v-for="l in lines" :key="l.key" :value="l.key">{{ l.label }}</option>
    </select>
    <select v-model="f.signal_type" aria-label="Signal type">
      <option value="">All signals</option>
      <option v-for="t in SIGNAL_TYPES" :key="t" :value="t">{{ signalLabel(t) }}</option>
    </select>
    <label class="range">Min score {{ f.min_score }}
      <input v-model.number="f.min_score" type="range" min="0" max="100" step="5" />
    </label>
  </form>
  <p v-if="error" class="error">{{ error }}</p>

  <div class="table-wrap" :class="{ loading }">
    <table class="data">
      <thead>
        <tr>
          <th><button class="th" @click="sortBy('name')">Company {{ arrow('name') }}</button></th>
          <th>DOT</th>
          <th><button class="th" @click="sortBy('state')">State {{ arrow('state') }}</button></th>
          <th><button class="th" @click="sortBy('signals')">Signals {{ arrow('signals') }}</button></th>
          <th>Pitch</th>
          <th><button class="th" @click="sortBy('score')">Score {{ arrow('score') }}</button></th>
          <th>Contact</th>
          <th>Status</th>
          <th><button class="th" @click="sortBy('updated_at')">Updated {{ arrow('updated_at') }}</button></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="l in data?.items" :key="l.id" @click="$router.push(`/leads/${l.id}`)" class="clickable">
          <td><RouterLink :to="`/leads/${l.id}`" @click.stop>{{ l.name }}</RouterLink>
            <div class="muted small">{{ l.fleet_size ?? '?' }} power units</div></td>
          <td class="mono">{{ l.dot_number }}</td>
          <td>{{ l.state }}</td>
          <td><span class="muted small">{{ l.signal_count }} ·</span>
            <span v-for="t in l.signal_types" :key="t" class="chip">{{ signalLabel(t) }}</span></td>
          <td class="small"><div v-for="k in l.service_lines.slice(0, 3)" :key="k" class="pitch-line">{{ lineLabel(k) }}</div>
            <span v-if="l.service_lines.length > 3" class="muted">+{{ l.service_lines.length - 3 }} more</span></td>
          <td><span :class="['score-pill', scoreClass(l.score)]">{{ l.score }}</span></td>
          <td class="small"><b v-if="l.person">{{ l.person }}</b><div>{{ l.phone ? fmtPhone(l.phone) : '' }}</div><div class="muted">{{ l.email }}</div></td>
          <td><span class="status">{{ l.status }}</span></td>
          <td class="small">{{ fmtDate(l.updated_at) }}</td>
        </tr>
        <tr v-if="data && !data.items.length"><td colspan="9" class="muted">No leads match these filters.</td></tr>
      </tbody>
    </table>
  </div>
  <nav class="pager" v-if="data">
    <button :disabled="f.page <= 1" @click="f.page--">Previous</button>
    <span>Page {{ f.page }} of {{ pages() }}</span>
    <button :disabled="f.page >= pages()" @click="f.page++">Next</button>
  </nav>
</template>
