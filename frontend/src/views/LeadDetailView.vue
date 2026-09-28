<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, fmtDate, fmtDateTime, fmtPhone, scoreClass, signalLabel, STATUSES, type LeadDetail, type LeadStatus, type Signal } from '../api'

const props = defineProps<{ id: number }>()
const lead = ref<LeadDetail>()
const error = ref('')
const note = ref('')
const expanded = reactive<Record<string, boolean>>({})
const PREVIEW = 3

const load = async () => {
  try { lead.value = await api.lead(props.id) } catch (e) { error.value = String(e) }
}
onMounted(load)

const groups = computed(() => {
  const m = new Map<string, Signal[]>()
  for (const s of lead.value?.signals ?? []) m.set(s.type, [...(m.get(s.type) ?? []), s])
  return [...m.entries()]
})
const safer = computed(() => lead.value?.company.dot_number &&
  `https://safer.fmcsa.dot.gov/query.asp?searchtype=ANY&query_type=queryCarrierSnapshot&query_param=USDOT&query_string=${lead.value.company.dot_number}`)

async function setStatus(e: Event) {
  try { lead.value = await api.setStatus(props.id, (e.target as HTMLSelectElement).value as LeadStatus) } catch (err) { error.value = String(err) }
}
async function addNote() {
  if (!note.value.trim()) return
  try { await api.addNote(props.id, note.value); note.value = ''; await load() } catch (err) { error.value = String(err) }
}
const describe = (e: LeadDetail['events'][number]) => {
  const m = e.meta
  switch (e.event_type) {
    case 'CREATED': return `Lead created with score ${m.score} from signals: ${(m.signals ?? []).map(signalLabel).join(', ')}`
    case 'SCORE_CHANGED': return `Score changed ${m.from} → ${m.to}`
    case 'STATUS_CHANGED': return `Status changed ${m.from} → ${m.to}`
    case 'SIGNALS_ADDED': return `${m.count} new signal(s): ${(m.signals ?? []).slice(0, 3).map((s: any) => s.description).join('; ')}`
    case 'ENRICHED': return `Found ${m.contacts} new contact(s)${m.website ? ` · website ${m.website}` : ''}`
    case 'NOTE': return m.text
    default: return JSON.stringify(m)
  }
}
const evidenceValue = (v: unknown) => (Array.isArray(v) ? v.join(', ') : String(v))
</script>

<template>
  <p v-if="error" class="error">{{ error }}</p>
  <template v-if="lead">
    <RouterLink to="/leads" class="small">← Leads</RouterLink>
    <header class="detail-head">
      <div>
        <h1>{{ lead.company.name }}</h1>
        <p class="muted" v-if="lead.company.dba_name">DBA {{ lead.company.dba_name }}</p>
      </div>
      <div class="head-actions">
        <span :class="['score-big', scoreClass(lead.score)]">{{ lead.score }}<small>/100</small></span>
        <label>Status
          <select :value="lead.status" @change="setStatus">
            <option v-for="s in STATUSES" :key="s" :value="s">{{ s }}</option>
          </select>
        </label>
      </div>
    </header>

    <section class="card pitch" v-if="lead.pitch.length">
      <h2>🎯 What to pitch</h2>
      <div class="pitch-grid">
        <article v-for="(p, i) in lead.pitch" :key="p.key" :class="['pitch-item', { top: i < 3 }]">
          <h3>{{ p.label }}</h3>
          <p class="small">Because: {{ p.reasons.map((r) => `${r.count > 1 ? r.count + '× ' : ''}${signalLabel(r.type)}`).join(', ') }}</p>
          <details>
            <summary class="small">{{ p.services.length }} services</summary>
            <ul class="small services"><li v-for="sv in p.services" :key="sv">{{ sv }}</li></ul>
          </details>
        </article>
      </div>
    </section>

    <div class="grid-2">
      <section class="card">
        <h2>Company</h2>
        <dl class="kv">
          <dt>USDOT</dt><dd class="mono">{{ lead.company.dot_number ?? '—' }} <a v-if="safer" :href="safer" target="_blank" rel="noopener">SAFER snapshot ↗</a></dd>
          <dt>MC</dt><dd class="mono">{{ lead.company.mc_number ?? '—' }}</dd>
          <dt>Location</dt><dd>{{ lead.company.location ?? lead.company.state ?? '—' }}</dd>
          <dt>Fleet</dt><dd>{{ lead.company.fleet_size ?? '?' }} power units · {{ lead.company.drivers ?? '?' }} drivers</dd>
          <dt>USDOT status</dt><dd>{{ lead.company.operating_status === 'A' ? 'Active' : lead.company.operating_status ?? '—' }}</dd>
          <dt>Registered</dt><dd>{{ fmtDate(lead.company.added_at) }}</dd>
          <dt>Operation</dt><dd>{{ lead.company.attributes.classdef ?? '—' }}</dd>
          <dt>Website</dt><dd><a v-if="lead.company.website" :href="lead.company.website" target="_blank" rel="noopener">{{ lead.company.website }}</a><span v-else>—</span></dd>
        </dl>
      </section>

      <section class="card">
        <h2>Why this score</h2>
        <table class="plain breakdown">
          <tr v-for="b in lead.score_breakdown" :key="b.key">
            <td :class="['pts', b.points < 0 ? 'neg' : 'pos']">{{ b.points > 0 ? '+' : '' }}{{ b.points }}</td>
            <td><b>{{ b.label }}</b><div class="muted small">{{ b.reason }}</div></td>
          </tr>
        </table>
        <p class="muted small">Sum of matched rules, capped at 100. Scored {{ lead.scored_at ? fmtDateTime(lead.scored_at) : '—' }}.</p>

        <h2 class="mt">Contacts</h2>
        <p v-if="!lead.contacts.length" class="muted">None found.</p>
        <ul class="contacts">
          <li v-for="c in lead.contacts" :key="c.id">
            <a v-if="c.type === 'phone'" :href="`tel:${c.value}`">{{ fmtPhone(c.value) }}</a>
            <span v-else-if="c.type === 'fax'">{{ fmtPhone(c.value) }}</span>
            <span v-else-if="c.type === 'person' || c.type === 'address'">{{ c.value }}</span>
            <a v-else-if="c.type === 'email'" :href="`mailto:${c.value}`">{{ c.value }}</a>
            <a v-else :href="c.value" target="_blank" rel="noopener">{{ c.value }}</a>
            <div class="muted small">{{ c.label ?? c.type }} · source:
              <a v-if="c.source_url" :href="c.source_url" target="_blank" rel="noopener">{{ c.source }}</a><span v-else>{{ c.source }}</span></div>
          </li>
        </ul>
      </section>
    </div>

    <section class="card">
      <h2>Signals &amp; evidence <span class="muted">({{ lead.signals.length }})</span></h2>
      <div v-for="[type, list] in groups" :key="type" class="sig-group">
        <h3>{{ signalLabel(type) }} <span class="muted">× {{ list.length }}</span></h3>
        <article v-for="s in expanded[type] ? list : list.slice(0, PREVIEW)" :key="s.id" class="signal">
          <div class="signal-head">
            <span :class="['sev', s.severity]">{{ s.severity }}</span>
            <span>{{ s.description }}</span>
          </div>
          <div class="muted small">
            Observed {{ fmtDate(s.observed_at) }} · {{ s.origin === 'rule' ? 'Fact-based rule' : 'AI interpretation' }}: {{ s.detected_by }} ·
            source <a v-if="s.source_url" :href="s.source_url" target="_blank" rel="noopener">{{ s.source }} record ↗</a><span v-else>{{ s.source }}</span>
          </div>
          <details>
            <summary class="small">Evidence</summary>
            <dl class="kv small"><template v-for="(v, k) in s.evidence" :key="k"><dt>{{ k }}</dt><dd class="mono">{{ evidenceValue(v) }}</dd></template></dl>
          </details>
        </article>
        <button v-if="list.length > PREVIEW" class="link" @click="expanded[type] = !expanded[type]">
          {{ expanded[type] ? 'Show less' : `Show all ${list.length}` }}
        </button>
      </div>
    </section>

    <section class="card">
      <h2>Notes &amp; timeline</h2>
      <form class="note-form" @submit.prevent="addNote">
        <textarea v-model="note" rows="2" maxlength="5000" placeholder="Add a note…" aria-label="Note"></textarea>
        <button type="submit" :disabled="!note.trim()">Add note</button>
      </form>
      <ul class="timeline">
        <li v-for="e in lead.events" :key="e.id">
          <span class="ev-type">{{ e.event_type.replace('_', ' ') }}</span>
          <span>{{ describe(e) }}</span>
          <span class="muted small">{{ fmtDateTime(e.created_at) }}</span>
        </li>
      </ul>
    </section>
  </template>
</template>
