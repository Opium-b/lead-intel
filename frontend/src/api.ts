export type LeadStatus = 'NEW' | 'REVIEWED' | 'CONTACTED' | 'QUALIFIED' | 'DISQUALIFIED' | 'CONVERTED'
export const STATUSES: LeadStatus[] = ['NEW', 'REVIEWED', 'CONTACTED', 'QUALIFIED', 'DISQUALIFIED', 'CONVERTED']

export interface LeadRow {
  id: number
  company_id: number
  name: string
  dot_number: string | null
  state: string | null
  fleet_size: number | null
  score: number
  status: LeadStatus
  signal_count: number
  signal_types: string[]
  service_lines: string[]
  phone: string | null
  email: string | null
  person: string | null
  updated_at: string
}
export interface LeadPage { items: LeadRow[]; total: number; page: number; page_size: number }

export interface Signal {
  id: number
  type: string
  description: string
  severity: 'low' | 'medium' | 'high' | 'critical'
  source: string
  source_url: string | null
  observed_at: string | null
  detected_by: string
  origin: string
  evidence: Record<string, unknown>
  created_at: string
}
export interface Contact { id: number; type: string; value: string; label: string | null; source: string; source_url: string | null }
export interface LeadEvent { id: number; event_type: string; meta: Record<string, any>; created_at: string }
export interface Pitch { key: string; label: string; services: string[]; strength: number; reasons: { type: string; count: number }[] }
export interface ServiceLine { key: string; label: string; services: string[] }
export interface BreakdownItem { key: string; label: string; points: number; reason: string }
export interface Company {
  id: number; name: string; dba_name: string | null; dot_number: string | null; mc_number: string | null
  state: string | null; city: string | null; location: string | null; fleet_size: number | null
  drivers: number | null; operating_status: string | null; website: string | null; added_at: string | null
  attributes: Record<string, string>
}
export interface LeadDetail {
  id: number; score: number; score_breakdown: BreakdownItem[]; pitch: Pitch[]; status: LeadStatus
  scored_at: string | null; notified_at: string | null; created_at: string; updated_at: string
  company: Company; signals: Signal[]; contacts: Contact[]; events: LeadEvent[]
}
export interface Overview {
  companies: number; new_signals_7d: number; leads: number; qualified_leads: number
  high_score_leads: number; high_score_threshold: number; leads_today: number
  signals_by_type: Record<string, number>
  recent_activity: (LeadEvent & { lead_id: number; company: string; score: number })[]
  last_run: { id: number; status: string; started_at: string; finished_at: string | null; stats: Record<string, unknown> } | null
}

const TOKEN_KEY = 'leadintel.token'
export const auth = {
  get: () => { try { return localStorage.getItem(TOKEN_KEY) } catch { return null } },
  set: (t: string) => { try { localStorage.setItem(TOKEN_KEY, t) } catch { /* private mode */ } },
  clear: () => { try { localStorage.removeItem(TOKEN_KEY) } catch { /* private mode */ } },
}

export class Unauthorized extends Error {}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api${path}`, {
    method,
    headers: { Authorization: `Bearer ${auth.get() ?? ''}`, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (res.status === 401) {
    auth.clear()
    window.dispatchEvent(new Event('leadintel:logout'))
    throw new Unauthorized()
  }
  if (!res.ok) throw new Error(`${res.status}: ${(await res.text()).slice(0, 200)}`)
  return res.json()
}

export const api = {
  overview: () => request<Overview>('GET', '/stats/overview'),
  leads: (params: Record<string, string | number | undefined>) => {
    const qs = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => [k, String(v)]))
    return request<LeadPage>('GET', `/leads?${qs}`)
  },
  serviceLines: () => request<ServiceLine[]>('GET', '/service-lines'),
  lead: (id: number) => request<LeadDetail>('GET', `/leads/${id}`),
  setStatus: (id: number, status: LeadStatus) => request<LeadDetail>('PATCH', `/leads/${id}`, { status }),
  addNote: (id: number, text: string) => request<LeadEvent>('POST', `/leads/${id}/notes`, { text }),
}

export const fmtDate = (s: string | null) => (s ? new Date(s.length === 10 ? s + 'T00:00' : s).toLocaleDateString() : '—')
export const fmtDateTime = (s: string) => new Date(s).toLocaleString()
export const fmtPhone = (p: string) => (p.length === 10 ? `(${p.slice(0, 3)}) ${p.slice(3, 6)}-${p.slice(6)}` : p)
export const SIGNAL_LABELS: Record<string, string> = {
  NEW_CARRIER: 'New carrier', NEW_AUTHORITY: 'New operating authority', INSURANCE_CANCELLATION: 'Insurance cancellation',
  INSURANCE_RENEWAL: 'Insurance renewal window', AUTHORITY_REVOKED: 'Authority revoked',
  OUT_OF_SERVICE: 'Out-of-service order', HIGH_OOS_RATE: 'High out-of-service rate', REPEATED_VIOLATIONS: 'Repeated violations',
  CRASH: 'Crash', HOS_VIOLATIONS: 'Hours-of-service violations', MAINTENANCE_VIOLATIONS: 'Maintenance violations',
  UNSAFE_DRIVING: 'Unsafe driving', DRIVER_FITNESS: 'Driver fitness violations', DRUG_ALCOHOL: 'Drug & alcohol violations',
  HAZMAT_VIOLATIONS: 'Hazmat violations', HAZMAT_CARRIER: 'Hazmat carrier', STALE_MCS150: 'Overdue MCS-150 update',
  INSPECTION_VIOLATION: 'Inspection violation', INACTIVE_STATUS: 'Inactive USDOT status',
}
export const signalLabel = (t: string) => SIGNAL_LABELS[t] ?? t.toLowerCase().replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase())
export const scoreClass = (s: number) => (s >= 70 ? 'score-high' : s >= 40 ? 'score-mid' : 'score-low')
