<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, fmtDateTime, scoreClass, signalLabel, type Overview } from '../api'

const data = ref<Overview>()
const error = ref('')
onMounted(async () => {
  try { data.value = await api.overview() } catch (e) { error.value = String(e) }
})

const describe = (e: Overview['recent_activity'][number]) => {
  switch (e.event_type) {
    case 'CREATED': return `New lead · ${(e.meta.signals ?? []).map(signalLabel).join(', ')}`
    case 'SCORE_CHANGED': return `Score ${e.meta.from} → ${e.meta.to}`
    case 'STATUS_CHANGED': return `Status ${e.meta.from} → ${e.meta.to}`
    case 'SIGNALS_ADDED': return `${e.meta.count} new signal(s)`
    case 'NOTE': return `Note: ${e.meta.text}`
    default: return e.event_type
  }
}
</script>

<template>
  <h1>Overview</h1>
  <p v-if="error" class="error">{{ error }}</p>
  <template v-if="data">
    <section class="tiles">
      <div class="tile"><span class="num">{{ data.companies }}</span><span>Companies discovered</span></div>
      <div class="tile"><span class="num">{{ data.new_signals_7d }}</span><span>New signals (7 days)</span></div>
      <RouterLink class="tile" to="/leads"><span class="num">{{ data.leads }}</span><span>Leads</span></RouterLink>
      <div class="tile"><span class="num">{{ data.high_score_leads }}</span><span>High-score leads (≥ {{ data.high_score_threshold }})</span></div>
      <div class="tile"><span class="num">{{ data.qualified_leads }}</span><span>Qualified leads</span></div>
      <div class="tile"><span class="num">{{ data.leads_today }}</span><span>Leads discovered today</span></div>
    </section>

    <div class="grid-2">
      <section class="card">
        <h2>Recent activity</h2>
        <p v-if="!data.recent_activity.length" class="muted">No activity yet. Run a collection to discover leads.</p>
        <ul class="feed">
          <li v-for="e in data.recent_activity" :key="e.id">
            <span :class="['score-pill', scoreClass(e.score)]">{{ e.score }}</span>
            <div>
              <RouterLink :to="`/leads/${e.lead_id}`">{{ e.company }}</RouterLink>
              <div class="muted small">{{ describe(e) }} · {{ fmtDateTime(e.created_at) }}</div>
            </div>
          </li>
        </ul>
      </section>
      <section class="card">
        <h2>Signals by type</h2>
        <table class="plain">
          <tr v-for="(n, t) in data.signals_by_type" :key="t">
            <td><RouterLink :to="{ path: '/leads', query: { signal_type: t } }">{{ signalLabel(String(t)) }}</RouterLink></td>
            <td class="right">{{ n }}</td>
          </tr>
        </table>
        <h2 class="mt">Last collection run</h2>
        <p v-if="data.last_run" class="small">
          #{{ data.last_run.id }} · <b>{{ data.last_run.status }}</b> · {{ fmtDateTime(data.last_run.started_at) }}<br />
          <span class="muted">{{ data.last_run.stats.companies ?? 0 }} companies, {{ data.last_run.stats.events ?? 0 }} records</span>
        </p>
        <p v-else class="muted small">Never run.</p>
      </section>
    </div>
  </template>
</template>
