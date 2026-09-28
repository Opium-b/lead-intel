<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, CHANNEL_LABELS, signalLabel, statusLabel, STATUSES, type Analytics, type Channel } from '../api'

const data = ref<Analytics>()
const error = ref('')
const applying = ref('')
const applied = ref(false)

const load = async () => {
  try { data.value = await api.analytics() } catch (e) { error.value = String(e) }
}
onMounted(load)

const pct = (r: number | null) => (r === null ? '—' : `${Math.round(r * 100)}%`)
const decided = computed(() => (data.value ? data.value.totals.won + data.value.totals.lost : 0))
const signals = computed(() => (data.value?.by_signal ?? []).filter((r) => r.worked > 0))

async function apply(key: string, weight: number) {
  applying.value = key
  try {
    await api.updateRule(key, weight)
    await api.reprocess()
    applied.value = true
    await load()
  } catch (e) { error.value = String(e) } finally { applying.value = '' }
}
</script>

<template>
  <h1>Analytics</h1>
  <p class="muted">What actually turns into deals, from the statuses you log. Won = Qualified or Converted; lost = Declined or Disqualified.</p>
  <p v-if="error" class="error">{{ error }}</p>

  <template v-if="data">
    <section class="tiles">
      <div class="tile"><span class="num">{{ data.totals.leads }}</span><span>Leads</span></div>
      <div class="tile"><span class="num">{{ data.totals.worked }}</span><span>Worked (contacted in any way)</span></div>
      <div class="tile"><span class="num">{{ data.totals.won }}</span><span>Won</span></div>
      <div class="tile"><span class="num">{{ data.totals.lost }}</span><span>Lost</span></div>
      <div class="tile"><span class="num">{{ pct(data.totals.win_rate) }}</span><span>Win rate (won ÷ won + lost)</span></div>
    </section>

    <p v-if="decided < data.thresholds.min_decided_total" class="card muted">
      Only {{ decided }} lead(s) won or lost so far. Keep logging outcomes on lead pages — weight suggestions appear
      after {{ data.thresholds.min_decided_total }} decided leads, with at least {{ data.thresholds.min_decided_with_signal }} per signal.
    </p>

    <section class="card" v-if="data.suggestions.length">
      <h2>Suggested score weights</h2>
      <p class="muted small">Based on how often leads with each signal were won compared to your average. Applying re-scores every lead.</p>
      <table class="data">
        <thead><tr><th>Rule</th><th>Current</th><th>Suggested</th><th>Why</th><th></th></tr></thead>
        <tbody>
          <tr v-for="s in data.suggestions" :key="s.key">
            <td>{{ s.label }}</td><td class="mono">{{ s.current }}</td><td class="mono"><b>{{ s.suggested }}</b></td>
            <td class="small muted">{{ s.reason }}</td>
            <td><button :disabled="!!applying" @click="apply(s.key, s.suggested)">{{ applying === s.key ? 'Applying…' : 'Apply' }}</button></td>
          </tr>
        </tbody>
      </table>
      <p v-if="applied" class="small">Applied — scores are being recalculated in the background.</p>
    </section>

    <div class="grid-2">
      <section class="card">
        <h2>Funnel</h2>
        <table class="plain">
          <tr v-for="s in STATUSES" :key="s"><td>{{ statusLabel(s) }}</td><td class="mono num-cell">{{ data.funnel[s] ?? 0 }}</td></tr>
        </table>
      </section>

      <section class="card">
        <h2>Does the score predict wins?</h2>
        <table class="data">
          <thead><tr><th>Score</th><th>Leads</th><th>Won</th><th>Lost</th><th>Win rate</th></tr></thead>
          <tbody>
            <tr v-for="b in data.by_score_band" :key="b.band">
              <td class="mono">{{ b.band }}</td><td>{{ b.leads }}</td><td>{{ b.won }}</td><td>{{ b.lost }}</td>
              <td><span class="rate"><span class="bar" :style="{ width: `${(b.win_rate ?? 0) * 100}%` }"></span></span>{{ pct(b.win_rate) }}</td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>

    <section class="card">
      <h2>Win rate by signal</h2>
      <p v-if="!signals.length" class="muted">No worked leads yet.</p>
      <table v-else class="data">
        <thead><tr><th>Signal</th><th>Leads</th><th>Worked</th><th>Won</th><th>Lost</th><th>Win rate</th><th title="Win rate vs your average, adjusted for small samples. Above 1 = better than average.">Lift</th></tr></thead>
        <tbody>
          <tr v-for="r in signals" :key="r.type">
            <td>{{ signalLabel(r.type) }}</td><td>{{ r.leads }}</td><td>{{ r.worked }}</td><td>{{ r.won }}</td><td>{{ r.lost }}</td>
            <td><span class="rate"><span class="bar" :style="{ width: `${(r.win_rate ?? 0) * 100}%` }"></span></span>{{ pct(r.win_rate) }}</td>
            <td class="mono">{{ r.lift.toFixed(2) }}</td>
          </tr>
        </tbody>
      </table>
    </section>

    <section class="card">
      <h2>Contact channels</h2>
      <p v-if="!data.by_channel.length" class="muted">No contacts logged with a channel yet. Pick “Via” when you log a contact.</p>
      <table v-else class="data">
        <thead><tr><th>Channel</th><th>Attempts</th><th>No answer</th><th>Won</th><th>Lost</th></tr></thead>
        <tbody>
          <tr v-for="c in data.by_channel" :key="c.channel">
            <td>{{ CHANNEL_LABELS[c.channel as Channel] ?? c.channel }}</td><td>{{ c.attempts }}</td>
            <td>{{ c.no_answer }}</td><td>{{ c.won }}</td><td>{{ c.lost }}</td>
          </tr>
        </tbody>
      </table>
    </section>
  </template>
</template>
