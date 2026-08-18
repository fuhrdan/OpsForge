const $ = id => document.getElementById(id);
let selectedAgentId = null;
let currentPreview = null;
let currentUser = null;
let inventory = [];
let latestAgents = [];
let latestMaintenance = [];
let csrfToken = sessionStorage.getItem('opsforgeCsrf') || '';
let forcedPasswordChange = false;
let lastAnalyticsFetch = 0;
let lastNodeHistoryFetch = 0;

function esc(v) {
  return String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
}
function ago(v) {
  if (!v) return '—';
  const s = Math.max(0, Math.floor((Date.now() - new Date(v).getTime()) / 1000));
  return s < 5 ? 'now' : s < 60 ? `${s}s ago` : s < 3600 ? `${Math.floor(s/60)}m ago` : s < 86400 ? `${Math.floor(s/3600)}h ago` : `${Math.floor(s/86400)}d ago`;
}
function dur(s) {
  s = Math.max(0, Math.round(Number(s || 0)));
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s/60)}m ${s%60}s`;
  if (s < 86400) return `${Math.floor(s/3600)}h ${Math.floor((s%3600)/60)}m`;
  return `${Math.floor(s/86400)}d ${Math.floor((s%86400)/3600)}h`;
}
function pct(v, digits = 2) { return `${Number(v || 0).toFixed(digits)}%`; }
function friendly(e) {
  const t = String(e?.message || e || 'Unknown error');
  try { return JSON.parse(t).error || t; } catch { return t; }
}
function roleRank(role) { return ({viewer:1, operator:2, administrator:3})[String(role || '').toLowerCase()] || 0; }
function can(role) { return currentUser && roleRank(currentUser.role) >= roleRank(role); }
function localDate(v) { return v ? new Date(v).toLocaleString() : '—'; }
function dateInputValue(date) {
  const d = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return d.toISOString().slice(0,16);
}
function availabilityClass(v, target = 99.9) {
  v = Number(v || 0);
  return v >= target ? 'availability-good' : v >= Math.max(95, target - 1) ? 'availability-warn' : 'availability-bad';
}

async function requestJson(url, options = {}) {
  const headers = {...(options.headers || {})};
  if (options.body !== undefined && options.body !== null) headers['Content-Type'] = 'application/json';
  if (options.method && options.method !== 'GET' && csrfToken && !headers['X-OpsForge-Enrollment-Token']) headers['X-OpsForge-CSRF'] = csrfToken;
  const r = await fetch(url, {
    cache: 'no-store', credentials: 'same-origin', ...options, headers,
    body: options.body !== undefined && options.body !== null && typeof options.body !== 'string' ? JSON.stringify(options.body) : options.body
  });
  const text = await r.text();
  if (!r.ok) { const err = new Error(text || `${r.status} ${r.statusText}`); err.status = r.status; throw err; }
  return text ? JSON.parse(text) : null;
}
const getJson = url => requestJson(url);
const postJson = (url, body = null, headers = {}) => requestJson(url, {method:'POST', body, headers});

function showLogin(message = '') { $('loginOverlay').classList.remove('hidden'); $('appMain').classList.add('hidden'); $('loginError').textContent = message; }
function hideLogin() { $('loginOverlay').classList.add('hidden'); $('appMain').classList.remove('hidden'); }
function showPasswordChange(forced = false) { forcedPasswordChange = forced; $('passwordOverlay').classList.remove('hidden'); $('passwordError').textContent = forced ? 'The bootstrap/temporary password must be replaced before NOC access is allowed.' : ''; }
function hidePasswordChange() { $('passwordOverlay').classList.add('hidden'); forcedPasswordChange = false; $('passwordForm').reset(); }

async function bootstrap() {
  initializeMaintenanceDates();
  try {
    const health = await getJson('/api/health');
    $('serverStatus').textContent = `Server ${health.version} · schema ${health.schemaVersion}`;
    $('serverStatus').className = 'status-pill good';
  } catch {
    $('serverStatus').textContent = 'Server unavailable'; $('serverStatus').className = 'status-pill bad'; showLogin('OpsForge.Server is not responding.'); return;
  }
  try {
    const session = await getJson('/api/auth/me');
    currentUser = session.user;
    if (!csrfToken) { showLogin('Sign in again in this browser tab to establish a CSRF-protected control session.'); return; }
    hideLogin(); updateRoleUi();
    if (currentUser.mustChangePassword) showPasswordChange(true); else await refresh(true);
  } catch { currentUser = null; showLogin(); }
}

async function login(event) {
  event.preventDefault(); $('loginError').textContent = '';
  try {
    const session = await requestJson('/api/auth/login', {method:'POST', body:{username:$('loginUsername').value.trim(), password:$('loginPassword').value}});
    currentUser = session.user; csrfToken = session.csrfToken; sessionStorage.setItem('opsforgeCsrf', csrfToken);
    hideLogin(); updateRoleUi();
    if (currentUser.mustChangePassword) showPasswordChange(true); else await refresh(true);
  } catch (e) { $('loginError').textContent = friendly(e); }
}
async function logout() { try { await postJson('/api/auth/logout'); } catch {} sessionStorage.removeItem('opsforgeCsrf'); csrfToken=''; currentUser=null; location.reload(); }
async function changePassword(event) {
  event.preventDefault();
  const next = $('newPassword').value;
  if (next !== $('confirmPassword').value) { $('passwordError').textContent = 'New-password confirmation does not match.'; return; }
  try {
    await postJson('/api/auth/change-password', {currentPassword:$('currentPassword').value, newPassword:next});
    hidePasswordChange(); const session = await getJson('/api/auth/me'); currentUser = session.user; updateRoleUi(); await refresh(true);
  } catch (e) { $('passwordError').textContent = friendly(e); }
}
function updateRoleUi() {
  if (!currentUser) return;
  $('currentUser').textContent = `${currentUser.displayName || currentUser.username} · ${currentUser.role}`;
  $('currentUser').className = `status-pill ${can('administrator') ? 'good' : 'neutral'}`;
  document.querySelectorAll('[data-min-role]').forEach(el => el.classList.toggle('hidden', !can(el.dataset.minRole)));
  $('killDemo').title = can('operator') ? '' : 'Operator role required';
  $('previewRestart').title = can('operator') ? '' : 'Operator role required';
}

async function refresh(forceAnalytics = false) {
  if (!currentUser || currentUser.mustChangePassword) return;
  try {
    const urls = ['/api/health','/api/security/status','/api/agents','/api/agent-inventory','/api/primary-incidents','/api/incidents','/api/timeline','/api/commands','/api/topology','/api/operator-summary','/api/maintenance'];
    const [health,security,agents,inv,primaries,incidents,timeline,commands,topology,summary,maintenance] = await Promise.all(urls.map(getJson));
    latestAgents = agents; inventory = inv; latestMaintenance = maintenance;
    $('serverStatus').textContent = `Server ${health.version} · schema ${health.schemaVersion}`; $('serverStatus').className = 'status-pill good';
    renderSecurity(security);
    if (!selectedAgentId && agents.length) selectedAgentId = agents[0].heartbeat.agentId;
    if (selectedAgentId && !agents.some(a => a.heartbeat.agentId === selectedAgentId) && agents.length) selectedAgentId = agents[0].heartbeat.agentId;
    renderMetrics(summary);
    renderInventory(inv);
    renderAgentSelector(agents);
    renderMaintenance(maintenance);
    renderTopology(topology);
    renderPrimary(primaries);
    renderAgent(agents.find(a => a.heartbeat.agentId === selectedAgentId) || agents[0] || null);
    renderSignals(incidents); renderTimeline(timeline); renderCommands(commands);
    const agent = agents.find(a => a.heartbeat.agentId === selectedAgentId) || agents[0] || null;
    const controllable = can('operator') && !!agent && (agent.heartbeat.monitoredProcesses || []).some(p => String(p.name).toLowerCase() === 'opsforge.demoservice');
    $('killDemo').disabled = !controllable; $('previewRestart').disabled = !controllable;
    populateMaintenanceTargets(inv);

    const now = Date.now();
    if (forceAnalytics || now - lastAnalyticsFetch > 15000) { await refreshAnalytics(); lastAnalyticsFetch = now; }
    if (selectedAgentId && (forceAnalytics || now - lastNodeHistoryFetch > 15000)) { await refreshNodeHistory(); lastNodeHistoryFetch = now; }
    if (can('administrator')) {
      const [users,audit] = await Promise.all([getJson('/api/auth/users'), getJson('/api/audit')]); renderUsers(users); renderAudit(audit);
    }
  } catch (e) {
    if (e.status === 401) { showLogin('Session expired. Sign in again.'); return; }
    if (e.status === 403 && friendly(e).toLowerCase().includes('password change')) { showPasswordChange(true); return; }
    console.error(e);
  }
}

async function refreshAnalytics() {
  if (!currentUser) return;
  const hours = Number($('analyticsRange').value || 24);
  const target = Number($('slaTarget').value || 99.9);
  try { renderAnalytics(await getJson(`/api/reliability?hours=${encodeURIComponent(hours)}&slaTarget=${encodeURIComponent(target)}`)); }
  catch (e) { console.error('analytics', e); }
}
async function refreshNodeHistory() {
  if (!selectedAgentId) return;
  const hours = Number($('analyticsRange').value || 24);
  try { renderNodeHistory(await getJson(`/api/agents/${encodeURIComponent(selectedAgentId)}/history?hours=${encodeURIComponent(hours)}`)); }
  catch (e) { console.error('history', e); }
}

function renderAnalytics(a) {
  const target = Number(a.slaTargetPercent || 99.9);
  $('fleetAvailability').textContent = pct(a.fleetAvailabilityPercent, 3);
  $('fleetAvailability').className = availabilityClass(a.fleetAvailabilityPercent, target);
  $('fleetAvailabilitySub').textContent = `${dur(a.downtimeSeconds)} downtime · ${a.rangeHours}h window`;
  $('errorBudget').textContent = pct(a.errorBudgetRemainingPercent, 1);
  $('errorBudget').className = Number(a.errorBudgetRemainingPercent) > 50 ? 'availability-good' : Number(a.errorBudgetRemainingPercent) > 0 ? 'availability-warn' : 'availability-bad';
  $('errorBudgetSub').textContent = `Target ${pct(target, target % 1 ? 2 : 1)}`;
  $('analyticsIncidents').textContent = a.primaryIncidentsOpened;
  $('analyticsResolved').textContent = `${a.primaryIncidentsResolved} resolved`;
  $('analyticsMttr').textContent = dur(a.averageMttrSeconds);
  $('maintenanceExcluded').textContent = dur(a.maintenanceExcludedSeconds);
  $('activeMaintenanceMetric').textContent = `${a.activeMaintenanceWindows} active windows`;
  $('availabilityLegend').textContent = `SLA ${pct(target, target % 1 ? 2 : 1)}`;
  renderLineChart('availabilityChart', a.timeline || [], [{key:'availabilityPercent', cls:'chart-line-green'}], 0, 100, target);
  renderLineChart('resourceChart', a.timeline || [], [{key:'cpuAveragePercent', cls:'chart-line-cyan'},{key:'memoryAveragePercent', cls:'chart-line-violet'}], 0, 100, null);
  renderIncidentTrend(a.incidentTrend || []);
  renderReliabilityTable(a.agents || [], target);
}

function renderLineChart(id, points, series, minY, maxY, target = null) {
  const el = $(id);
  if (!points || points.length < 1) { el.className='chart-surface empty-state'; el.textContent='Collecting history…'; return; }
  el.className = id === 'nodeHistoryChart' ? 'chart-surface mini-chart' : 'chart-surface';
  const w=520,h=190,padL=34,padR=10,padT=12,padB=24,innerW=w-padL-padR,innerH=h-padT-padB;
  const x=i => padL + (points.length === 1 ? innerW/2 : i * innerW/(points.length-1));
  const y=v => padT + (maxY - Math.max(minY,Math.min(maxY,Number(v||0)))) * innerH/(maxY-minY);
  const grid=[0,.25,.5,.75,1].map(k=>{const yy=padT+k*innerH;const val=maxY-k*(maxY-minY);return `<line class="chart-grid-line" x1="${padL}" y1="${yy}" x2="${w-padR}" y2="${yy}"/><text class="chart-axis-label" x="2" y="${yy+3}">${Math.round(val)}</text>`;}).join('');
  const lines=series.map(s=>`<polyline class="${s.cls}" points="${points.map((p,i)=>`${x(i).toFixed(1)},${y(p[s.key]).toFixed(1)}`).join(' ')}"/>`).join('');
  const targetLine=target === null ? '' : `<line class="chart-sla" x1="${padL}" y1="${y(target).toFixed(1)}" x2="${w-padR}" y2="${y(target).toFixed(1)}"/>`;
  const startLabel = points[0]?.timestampUtc ? new Date(points[0].timestampUtc).toLocaleString([], {month:'short',day:'numeric',hour:'numeric'}) : '';
  const endLabel = points[points.length-1]?.timestampUtc ? new Date(points[points.length-1].timestampUtc).toLocaleString([], {month:'short',day:'numeric',hour:'numeric'}) : '';
  el.innerHTML=`<svg class="chart-svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-label="Historical chart">${grid}${targetLine}${lines}<text class="chart-axis-label" x="${padL}" y="${h-5}">${esc(startLabel)}</text><text class="chart-axis-label" x="${w-padR-72}" y="${h-5}">${esc(endLabel)}</text></svg>`;
}
function renderIncidentTrend(points) {
  const el=$('incidentTrendChart');
  if(!points.length || !points.some(p=>Number(p.opened||0)+Number(p.resolved||0)>0)){el.className='chart-surface empty-state';el.textContent='No primary incidents in this window.';return;}
  el.className='chart-surface'; const w=520,h=190,pad=28,innerW=w-pad*2,innerH=h-45,max=Math.max(1,...points.map(p=>Math.max(Number(p.opened||0),Number(p.resolved||0))));
  const groupW=innerW/points.length,barW=Math.max(3,Math.min(18,groupW*.28));
  const bars=points.map((p,i)=>{const cx=pad+i*groupW+groupW/2;const oh=Number(p.opened||0)/max*innerH;const rh=Number(p.resolved||0)/max*innerH;return `<rect class="chart-bar-open" x="${(cx-barW-1).toFixed(1)}" y="${(h-25-oh).toFixed(1)}" width="${barW.toFixed(1)}" height="${oh.toFixed(1)}"/><rect class="chart-bar-resolved" x="${(cx+1).toFixed(1)}" y="${(h-25-rh).toFixed(1)}" width="${barW.toFixed(1)}" height="${rh.toFixed(1)}"/>`;}).join('');
  el.innerHTML=`<svg class="chart-svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none"><line class="chart-grid-line" x1="${pad}" y1="${h-25}" x2="${w-pad}" y2="${h-25}"/>${bars}<text class="chart-axis-label" x="${pad}" y="${h-7}">Opened</text><text class="chart-axis-label" x="${pad+50}" y="${h-7}">Resolved</text></svg>`;
}
function renderReliabilityTable(items,target) {
  const el=$('reliabilityTable');
  if(!items.length){el.className='table-wrap empty-state';el.textContent='No enrolled nodes with reliability history yet.';return;}
  el.className='table-wrap';
  el.innerHTML=`<table class="reliability-table"><thead><tr><th>Node</th><th>Availability</th><th>CPU avg / peak</th><th>Memory avg / peak</th><th>Probe success</th><th>Probe latency</th><th>Incidents</th><th>MTTR</th><th>Maintenance excluded</th></tr></thead><tbody>${items.map(a=>`<tr><td><strong>${esc(a.displayName)}</strong><small>${esc(a.site||'—')} · ${esc(a.environmentName||'—')}</small></td><td class="${availabilityClass(a.availabilityPercent,target)}">${pct(a.availabilityPercent,3)}</td><td>${pct(a.cpuAveragePercent,1)} / ${pct(a.cpuPeakPercent,1)}</td><td>${pct(a.memoryAveragePercent,1)} / ${pct(a.memoryPeakPercent,1)}</td><td>${pct(a.probeSuccessPercent,2)}</td><td>${Number(a.probeAverageLatencyMs||0).toFixed(0)} ms</td><td>${a.incidentsOpened}</td><td>${dur(a.averageMttrSeconds)}</td><td>${dur(a.maintenanceExcludedSeconds)}</td></tr>`).join('')}</tbody></table>`;
}
function renderNodeHistory(history) {
  $('nodeHistoryRange').textContent=`${history.rangeHours}h`;
  const points=history.points||[];
  renderLineChart('nodeHistoryChart',points,[{key:'cpuPercent',cls:'chart-line-cyan'},{key:'memoryUsedPercent',cls:'chart-line-violet'}],0,100,null);
}

function renderSecurity(s) {
  const label=s.agentMtlsEnabled?'HTTPS + mTLS READY':s.https?'HTTPS ACTIVE':'LOOPBACK HTTP';
  $('securityTransport').textContent=label; $('securityTransport').className=`status-pill ${s.https?'good':'neutral'}`;
  $('securityDetails').innerHTML=`<strong>Named user sessions + RBAC active</strong> · roles: ${esc((s.roles||[]).join(' / '))}<br><span>${esc(s.transportGuidance)}</span><br><small>Agent enrollment secret: ${esc(s.enrollmentTokenLocation)} · Bootstrap administrator: ${esc(s.bootstrapAdminLocation)}</small>`;
}
function renderMetrics(s) {
  $('agentsOnline').textContent=`${s.agentsOnline} / ${s.agentsTotal}`;
  $('primaryIncidentCount').textContent=s.activePrimaryIncidents;
  $('unownedIncidentCount').textContent=s.unownedPrimaryIncidents;
  $('ackIncidentCount').textContent=s.acknowledgedPrimaryIncidents;
  $('actionableSignalCount').textContent=s.actionableSignals;
  $('maintenanceMutedCount').textContent=s.maintenanceMutedIncidents;
}

function renderMaintenance(items) {
  const el=$('maintenanceList');
  if(!items.length){el.className='maintenance-list empty-state';el.textContent='No maintenance windows.';return;}
  el.className='maintenance-list'; const now=Date.now();
  el.innerHTML=items.map(m=>{const active=m.activeNow;const future=!m.cancelled&&new Date(m.startUtc).getTime()>now;const state=m.cancelled?'CANCELLED':active?'ACTIVE':future?'UPCOMING':'COMPLETE';return `<article class="maintenance-card ${active?'active-maintenance':''} ${m.cancelled?'cancelled':''}"><div><span>${state} · ${esc(m.agentId==='*'?'ALL AGENTS':m.agentId)}</span><strong>${esc(m.name)}</strong><small>${esc(m.reason||'No reason supplied')}</small></div><div><span>Created by</span><strong>${esc(m.createdBy)}</strong><small>${ago(m.createdUtc)}</small></div><div><span>Window</span><strong>${esc(localDate(m.startUtc))}</strong><small>through ${esc(localDate(m.endUtc))}</small></div><div>${can('operator')&&!m.cancelled&&new Date(m.endUtc).getTime()>now?`<button class="secondary compact" data-cancel-maintenance="${esc(m.maintenanceId)}">Cancel</button>`:''}</div></article>`;}).join('');
  el.querySelectorAll('[data-cancel-maintenance]').forEach(b=>b.onclick=()=>cancelMaintenance(b.dataset.cancelMaintenance));
}
function initializeMaintenanceDates() {
  const start=new Date(); start.setSeconds(0,0); start.setMinutes(Math.ceil(start.getMinutes()/5)*5);
  const end=new Date(start.getTime()+60*60*1000);
  $('maintenanceStart').value=dateInputValue(start); $('maintenanceEnd').value=dateInputValue(end);
}
function populateMaintenanceTargets(items) {
  const select=$('maintenanceAgent'); const current=select.value||'*';
  select.innerHTML='<option value="*">All agents</option>'+items.filter(a=>!a.revoked).map(a=>`<option value="${esc(a.agentId)}">${esc(a.displayName||a.agentId)} · ${esc(a.agentId)}</option>`).join('');
  select.value=[...select.options].some(o=>o.value===current)?current:'*';
}
async function createMaintenance() {
  if(!can('operator'))return;
  const start=new Date($('maintenanceStart').value),end=new Date($('maintenanceEnd').value);
  if(!$('maintenanceName').value.trim()){alert('Maintenance name is required.');return;}
  if(Number.isNaN(start.getTime())||Number.isNaN(end.getTime())||end<=start){alert('Choose a valid start and end time.');return;}
  try {await postJson('/api/maintenance',{name:$('maintenanceName').value.trim(),agentId:$('maintenanceAgent').value,reason:$('maintenanceReason').value.trim(),startUtc:start.toISOString(),endUtc:end.toISOString()});$('maintenanceName').value='';$('maintenanceReason').value='';initializeMaintenanceDates();await refresh(true);}catch(e){alert(`Maintenance scheduling failed: ${friendly(e)}`);}
}
async function cancelMaintenance(id) {if(!can('operator'))return;if(!confirm('Cancel this maintenance window?'))return;try{await postJson(`/api/maintenance/${encodeURIComponent(id)}/cancel`);await refresh(true);}catch(e){alert(`Cancel failed: ${friendly(e)}`);}}

function renderPrimary(items) {
  const el=$('primaryIncidentList');
  if(!items.length){el.className='primary-list empty-state';el.textContent='No correlated incidents. The operator queue is quiet.';return;}
  el.className='primary-list';
  el.innerHTML=items.slice(0,20).map(i=>{
    const maintenance=i.active&&i.maintenanceSuppressed;
    const owner=i.ownerUsername?`${i.ownerDisplayName||i.ownerUsername} (${i.ownerUsername})`:'Unassigned';
    const actions=i.active&&can('operator')?`<div class="incident-actions">${!i.acknowledged?`<button class="secondary compact" data-ack="${esc(i.id)}">Acknowledge</button>`:''}${i.ownerUsername!==currentUser.username?`<button class="primary compact" data-take="${esc(i.id)}">Take ownership</button>`:''}${i.ownerUsername?`<button class="secondary compact" data-release="${esc(i.id)}">Release owner</button>`:''}<span class="owner-chip">Owner: ${esc(owner)}</span>${i.acknowledged?`<span class="owner-chip">Ack: ${esc(i.acknowledgedBy)}</span>`:''}</div>`:`<div class="incident-actions"><span class="owner-chip">Owner: ${esc(owner)}</span>${i.acknowledged?`<span class="owner-chip">Ack: ${esc(i.acknowledgedBy)}</span>`:''}</div>`;
    return `<article class="primary-incident ${i.active?'active-primary':'resolved-primary'} ${maintenance?'maintenance-primary':''}"><div class="primary-head"><div><div class="eyebrow">PRIMARY INCIDENT · ${i.active?'ACTIVE':'RESOLVED'}</div><h3>${esc(i.title)}</h3></div><div class="primary-badges"><span class="status-pill ${maintenance?'warn':i.active?'bad':'good'}">${maintenance?'MAINTENANCE':i.active?'CRITICAL':'RESOLVED'}</span><span class="confidence">${esc(i.confidence)} · ${Math.round(Number(i.confidenceScore||0)*100)}%</span></div></div><p>${esc(i.summary)}</p>${maintenance?`<div class="suppression-note">Muted by maintenance window: ${esc(i.maintenanceWindowName)}</div>`:''}<div class="diagnosis-grid"><div><span>Probable root cause</span><strong>${esc(i.probableRootCause)}</strong></div><div><span>Blast radius</span><strong>${esc(i.blastRadius)}</strong></div><div><span>${i.active?'Elapsed':'MTTR'}</span><strong>${dur(i.durationSeconds)}</strong></div><div><span>Ownership</span><strong>${esc(owner)}</strong></div></div><div class="signal-row">${(i.signals||[]).map(s=>`<div class="signal-chip ${String(s.role).toLowerCase().includes('root')?'root-chip':''}"><strong>${esc(s.signalType)}</strong><span>${esc(s.role)}</span><small>${esc(s.target)}</small></div>`).join('')}</div>${actions}<div class="incident-footer"><span>Updated ${ago(i.lastSeenUtc)}</span><a href="/api/primary-incidents/${encodeURIComponent(i.id)}/report" target="_blank">Primary report ↗</a></div></article>`;
  }).join('');
  el.querySelectorAll('[data-ack]').forEach(b=>b.onclick=()=>ackIncident(b.dataset.ack));
  el.querySelectorAll('[data-take]').forEach(b=>b.onclick=()=>takeIncident(b.dataset.take));
  el.querySelectorAll('[data-release]').forEach(b=>b.onclick=()=>releaseIncident(b.dataset.release));
}
async function ackIncident(id){const note=prompt('Optional acknowledgement note:','')??'';try{await postJson(`/api/primary-incidents/${encodeURIComponent(id)}/acknowledge`,{note});await refresh(true);}catch(e){alert(`Acknowledge failed: ${friendly(e)}`);}}
async function takeIncident(id){if(!currentUser)return;const note=prompt('Optional ownership note:','Taking ownership for investigation.')??'';try{await postJson(`/api/primary-incidents/${encodeURIComponent(id)}/assign`,{ownerUsername:currentUser.username,note});await refresh(true);}catch(e){alert(`Assignment failed: ${friendly(e)}`);}}
async function releaseIncident(id){const note=prompt('Optional release note:','')??'';try{await postJson(`/api/primary-incidents/${encodeURIComponent(id)}/unassign`,{note});await refresh(true);}catch(e){alert(`Release failed: ${friendly(e)}`);}}

function renderAgentSelector(agents) {
  const select=$('agentSelect');
  select.innerHTML=agents.length?agents.map(a=>`<option value="${esc(a.heartbeat.agentId)}">${esc(a.heartbeat.displayName||a.heartbeat.machineName)} · ${a.online?'online':'offline'}</option>`).join(''):'<option>No agents</option>';
  if(selectedAgentId)select.value=selectedAgentId;
}
function renderAgent(a) {
  if(!a){$('machineName').textContent='Waiting for authenticated agent…';$('hostDetails').innerHTML='';$('nodeHistoryChart').className='chart-surface mini-chart empty-state';$('nodeHistoryChart').textContent='No node selected.';return;}
  const h=a.heartbeat; $('machineName').textContent=h.displayName||h.machineName; $('nodeHistoryTitle').textContent=`${h.displayName||h.machineName} · CPU / memory trend`;
  $('hostDetails').innerHTML=`<div><span>Agent ID</span><strong>${esc(h.agentId)}</strong></div><div><span>Machine</span><strong>${esc(h.machineName)}</strong></div><div><span>Site / environment</span><strong>${esc(h.site||'—')} · ${esc(h.environmentName||'—')}</strong></div><div><span>Last heartbeat</span><strong>${ago(a.lastSeenUtc)}</strong></div>`;
  meter('cpu',h.cpuPercent); meter('memory',h.memoryUsedPercent);
  $('processList').innerHTML=(h.monitoredProcesses||[]).map(p=>row(p.name,p.running?`Running · PID ${p.processId}`:'Not running',p.running)).join('')||'<div class="muted">No monitored processes.</div>';
  $('serviceList').innerHTML=(h.monitoredServices||[]).map(s=>row(s.displayName||s.name,s.exists?s.status:'Not found',s.exists&&String(s.status).toLowerCase()==='running')).join('')||'<div class="muted">No monitored services.</div>';
  $('probeList').innerHTML=(h.probes||[]).map(p=>row(`${p.type} · ${p.id}`,`${p.success?'Healthy':'Failed'} · ${p.latencyMs} ms · ${p.target}`,p.success)).join('')||'<div class="muted">No probes.</div>';
}
function meter(prefix,v){v=Math.max(0,Math.min(100,Number(v||0)));$(`${prefix}Bar`).value=v;$(`${prefix}Label`).textContent=`${v.toFixed(1)}%`;}
function row(t,d,h){return `<div class="health-row"><div><strong>${esc(t)}</strong><small>${esc(d)}</small></div><span class="dot ${h?'dot-good':'dot-bad'}"></span></div>`;}

function renderTopology(t) {
  $('topologySummary').textContent=`${t.agentsOnline}/${t.agentsTotal} agents · ${t.failedNodes} failed · ${t.suppressedSignalCount} suppressed`;
  $('topologySummary').className=`status-pill ${t.failedNodes?'bad':'good'}`;
  const hosts=(t.nodes||[]).filter(n=>n.kind==='host');
  if(!hosts.length){$('topologyCanvas').className='topology-canvas empty-state';$('topologyCanvas').textContent='Waiting for authenticated agents…';$('dependencyPaths').innerHTML='';return;}
  $('topologyCanvas').className='topology-canvas';
  $('topologyCanvas').innerHTML=hosts.map(h=>{const childIds=(t.edges||[]).filter(e=>e.fromNodeId===h.id&&!e.crossAgent).map(e=>e.toNodeId);const children=childIds.map(id=>t.nodes.find(n=>n.id===id)).filter(Boolean);return `<article class="topology-host ${h.health==='failed'?'topology-failed':''}"><div class="topology-host-head"><div><small>${esc(h.site||'Unassigned')} · ${esc(h.environmentName||'')}</small><strong>${esc(h.label)}</strong></div><span class="dot ${h.health==='healthy'?'dot-good':'dot-bad'}"></span></div><div class="topology-components">${children.map(n=>`<div class="topology-component ${n.health==='failed'?'component-failed':''}"><span>${esc(n.kind)}</span><strong>${esc(n.label)}</strong><small>${esc(n.detail)}</small></div>`).join('')}</div></article>`;}).join('');
  const cross=(t.edges||[]).filter(e=>e.crossAgent),impacts=t.impacts||[];
  $('dependencyPaths').innerHTML=(cross.length?cross.map(e=>{const a=t.nodes.find(n=>n.id===e.fromNodeId),b=t.nodes.find(n=>n.id===e.toNodeId);return `<div class="path-card"><span>CROSS-NODE</span><strong>${esc(a?.label||e.fromNodeId)} → ${esc(b?.label||e.toNodeId)}</strong><small>${esc(e.label)}</small></div>`;}).join(''):'<div class="muted">No cross-agent dependencies discovered yet.</div>')+(impacts.length?`<div class="impact-wrap">${impacts.slice(0,5).map(i=>`<div class="impact-card"><span>BLAST RADIUS</span><strong>${esc(i.rootLabel)}</strong><small>Impacts ${i.affectedLabels.length}: ${esc(i.affectedLabels.join(' → '))}</small></div>`).join('')}</div>`:'');
}
function renderSignals(items){$('incidentList').innerHTML=items.length?items.slice(0,80).map(i=>`<div class="event-card ${i.active?'event-bad':'event-good'} ${i.suppressed?'suppressed-signal':''}"><div class="event-time">${i.active?'ACTIVE':'RESOLVED'} · ${i.maintenanceSuppressed?'MAINTENANCE MUTED · ':i.suppressed?'SUPPRESSED DERIVATIVE · ':''}${ago(i.lastSeenUtc)}</div><strong>${esc(i.title)}</strong><p>${esc(i.evidence)}</p>${i.suppressed?`<div class="suppression-note">↳ ${esc(i.suppressionReason)}</div>`:''}<small>${esc(i.category)} · ${i.active?dur(i.durationSeconds):`MTTR ${dur(i.durationSeconds)}`} · <a href="/api/incidents/${encodeURIComponent(i.id)}/report" target="_blank">signal report ↗</a></small></div>`).join(''):'<div class="empty-state">No signals yet.</div>';}
function renderTimeline(items){$('timelineList').innerHTML=items.length?items.slice(0,120).map(e=>`<div class="event-card ${eventClass(e.eventType)}"><div class="event-time">${new Date(e.timestampUtc).toLocaleTimeString()} · ${ago(e.timestampUtc)}</div><strong>${esc(e.title)}</strong><p>${esc(e.detail)}</p><small>${esc(e.sourceType)} · ${esc(e.eventType)}</small></div>`).join(''):'<div class="empty-state">No events yet.</div>';}
function eventClass(t){t=String(t||'').toLowerCase();return t.includes('failed')?'event-bad':(t==='resolved'||t==='verified'||t==='acknowledged')?'event-good':(t==='reassessed'||t==='scheduled'||t==='assigned')?'event-warn':'';}
function renderCommands(items){$('commandList').innerHTML=items.length?items.map(c=>`<div class="command"><div><strong>${esc(c.type)}</strong><small>${esc(c.target)} · ${esc(c.agentId)} · requested by ${esc(c.requestedBy||'system')}</small></div><div><span class="command-stage">${esc(c.verifiedUtc?'Verified':c.verificationStatus||'Queued')}</span><small>${esc(c.resultMessage||c.verificationMessage||'Waiting for agent…')}</small></div><span>${ago(c.createdUtc)}</span></div>`).join(''):'<div class="empty-state">No remediation commands yet.</div>';}

async function injectFailure(){if(!selectedAgentId||!can('operator'))return;try{await postJson(`/api/agents/${encodeURIComponent(selectedAgentId)}/chaos/kill-demo`);await refresh(true);}catch(e){alert(`Failure injection failed: ${friendly(e)}`);}}
async function previewRestart(){if(!selectedAgentId||!can('operator'))return;try{currentPreview=await postJson(`/api/agents/${encodeURIComponent(selectedAgentId)}/remediations/restart-demo/preview`);renderPreview();}catch(e){alert(`Preview failed: ${friendly(e)}`);}}
function renderPreview(){const panel=$('previewPanel');if(!currentPreview){panel.classList.add('hidden');return;}panel.classList.remove('hidden');$('previewSummary').textContent=currentPreview.summary;$('previewGrid').innerHTML=`<div><span>Agent</span><strong>${esc(currentPreview.agentId)}</strong></div><div><span>Action</span><strong>${esc(currentPreview.action)} · ${esc(currentPreview.target)}</strong></div><div><span>Risk</span><strong>${esc(currentPreview.risk)}</strong></div><div><span>Verification plan</span><strong>${esc(currentPreview.verificationPlan)}</strong></div>`;}
async function executePreview(){if(!currentPreview||!can('operator'))return;try{await postJson(`/api/remediations/${encodeURIComponent(currentPreview.previewToken)}/execute`);currentPreview=null;renderPreview();await refresh(true);}catch(e){alert(`Execution failed: ${friendly(e)}`);}}

function renderInventory(items) {
  const el=$('inventoryList'); if(!items.length){el.className='inventory-list empty-state';el.textContent='No enrolled agents yet.';return;} el.className='inventory-list';
  el.innerHTML=items.map(a=>`<article class="inventory-card ${a.revoked?'inventory-revoked':''}"><div><span class="status-pill ${a.online?'good':a.revoked?'bad':'neutral'}">${esc(String(a.status||'unknown').toUpperCase())}</span><strong>${esc(a.displayName||a.agentId)}</strong><small>${esc(a.agentId)} · ${esc(a.site||'Unassigned')} · ${esc(a.environmentName||'')}</small></div><div><span>API credential</span><strong>${esc(a.credentialFingerprint)}</strong><small>${a.lastSeenUtc?`Last seen ${ago(a.lastSeenUtc)}`:'Awaiting first heartbeat'}</small></div><div><span>mTLS identity</span><strong>${esc(a.clientCertificateThumbprint||'Not bound')}</strong><small>${esc(a.machineName||'Not reported')} · ${esc(a.lastIpAddress||'No IP recorded')}</small></div><div class="inventory-actions"><button class="secondary compact" data-history="${esc(a.agentId)}">History</button>${can('administrator')&&!a.revoked?`<button class="secondary compact" data-rotate="${esc(a.agentId)}">Rotate key</button><button class="secondary compact" data-bind="${esc(a.agentId)}">Bind cert</button><button class="danger compact" data-revoke="${esc(a.agentId)}">Revoke</button>`:''}</div></article>`).join('');
  el.querySelectorAll('[data-history]').forEach(b=>b.onclick=()=>showAgentHistory(b.dataset.history));
  el.querySelectorAll('[data-rotate]').forEach(b=>b.onclick=()=>rotateKey(b.dataset.rotate));
  el.querySelectorAll('[data-bind]').forEach(b=>b.onclick=()=>bindCertificate(b.dataset.bind));
  el.querySelectorAll('[data-revoke]').forEach(b=>b.onclick=()=>revokeAgent(b.dataset.revoke));
}
async function showAgentHistory(agentId){try{const h=await getJson(`/api/agent-inventory/${encodeURIComponent(agentId)}/history`);$('agentHistory').innerHTML=h.length?`<strong>${esc(agentId)}:</strong> `+h.slice(0,12).map(x=>`${esc(x.status)} ${ago(x.timestampUtc)}`).join(' · '):`No inventory events for ${esc(agentId)}.`;}catch(e){alert(`History failed: ${friendly(e)}`);}}
async function rotateKey(agentId){if(!confirm(`Rotate API key for ${agentId}? The old key stops working immediately.`))return;try{const r=await postJson(`/api/agent-inventory/${encodeURIComponent(agentId)}/rotate-key`);showOneTimeCredential(r,'Credential rotated');await refresh();}catch(e){alert(`Rotation failed: ${friendly(e)}`);}}
async function bindCertificate(agentId){const thumbprint=prompt(`Client certificate thumbprint for ${agentId}:`,'');if(!thumbprint)return;try{await postJson(`/api/agent-inventory/${encodeURIComponent(agentId)}/bind-certificate`,{thumbprint});await refresh();}catch(e){alert(`Certificate bind failed: ${friendly(e)}`);}}
async function revokeAgent(agentId){if(!confirm(`Revoke ${agentId}? Its current credential will stop working.`))return;try{await postJson(`/api/agent-inventory/${encodeURIComponent(agentId)}/revoke`);await refresh();}catch(e){alert(`Revocation failed: ${friendly(e)}`);}}

async function enrollAgent(){const agentId=$('enrollAgentId').value.trim();if(!agentId){alert('Agent ID is required.');return;}try{const r=await postJson('/api/admin/enrollment/agents',{agentId,displayName:$('enrollDisplayName').value.trim(),site:$('enrollSite').value.trim(),environmentName:$('enrollEnvironment').value.trim(),clientCertificateThumbprint:$('enrollCertificateThumbprint').value.trim()});showOneTimeCredential(r,'Agent enrolled');await refresh();}catch(e){alert(`Enrollment failed: ${friendly(e)}`);}}
function showOneTimeCredential(r,title){const cfg={agentId:r.agentId,displayName:'',site:'',environmentName:'lab',serverUrl:location.origin,apiKey:r.apiKey,enrollmentToken:'',credentialsFile:'',allowInsecureRemoteHttp:false,useClientCertificate:true,clientCertificatePfx:'',clientCertificatePassword:'',heartbeatSeconds:3,monitoredProcesses:[],monitoredServices:['EventLog'],healthChecks:[]};const el=$('enrollmentResult');el.classList.remove('hidden');el.innerHTML=`<div class="section-title-row"><div><div class="eyebrow">${esc(title.toUpperCase())}</div><h3>Save this credential now</h3></div><span class="status-pill good">${esc(r.credentialFingerprint)}</span></div><p>${esc(r.note)}</p><label>One-time API key<textarea readonly>${esc(r.apiKey)}</textarea></label><p><strong>Bound client certificate:</strong> ${esc(r.clientCertificateThumbprint||'none yet')}</p><label>Starter agent configuration<textarea readonly>${esc(JSON.stringify(cfg,null,2))}</textarea></label>`;}

function renderUsers(users){const el=$('userList');el.innerHTML=users.map(u=>`<article class="inventory-card"><div><span class="status-pill ${u.enabled?'good':'bad'}">${u.enabled?'ENABLED':'DISABLED'}</span><strong>${esc(u.displayName)}</strong><small>${esc(u.username)}</small></div><div><span>Role</span><strong>${esc(u.role)}</strong><small>${u.mustChangePassword?'Password change required':'Password established'}</small></div><div><span>Last login</span><strong>${u.lastLoginUtc?esc(localDate(u.lastLoginUtc)):'Never'}</strong><small>Created ${esc(localDate(u.createdUtc))}</small></div><div class="inventory-actions"><button class="secondary compact" data-reset-user="${u.userId}">Reset password</button><button class="secondary compact" data-toggle-user="${u.userId}" data-enabled="${u.enabled?'1':'0'}">${u.enabled?'Disable':'Enable'}</button></div></article>`).join('');el.querySelectorAll('[data-reset-user]').forEach(b=>b.onclick=()=>resetUser(Number(b.dataset.resetUser)));el.querySelectorAll('[data-toggle-user]').forEach(b=>b.onclick=()=>toggleUser(Number(b.dataset.toggleUser),b.dataset.enabled!=='1'));}
async function createUser(){const username=$('newUsername').value.trim(),displayName=$('newDisplayName').value.trim(),role=$('newRole').value;if(!username){alert('Username is required.');return;}try{const r=await postJson('/api/auth/users',{username,displayName,role});showUserCredential(r);$('newUsername').value='';$('newDisplayName').value='';await refresh();}catch(e){alert(`Create user failed: ${friendly(e)}`);}}
function showUserCredential(r){const el=$('userCredentialResult');el.classList.remove('hidden');el.innerHTML=`<div class="eyebrow">TEMPORARY PASSWORD · SHOWN ONCE</div><h3>${esc(r.user.displayName||r.user.username)}</h3><label>Username<input readonly value="${esc(r.user.username)}"/></label><label>Temporary password<input readonly value="${esc(r.temporaryPassword)}"/></label><p>${esc(r.note)}</p>`;}
async function resetUser(userId){if(!confirm('Reset this user password and revoke existing sessions?'))return;try{const r=await postJson(`/api/auth/users/${userId}/reset-password`);$('userCredentialResult').classList.remove('hidden');$('userCredentialResult').innerHTML=`<div class="eyebrow">RESET PASSWORD · SHOWN ONCE</div><h3>${esc(r.username)}</h3><label>Temporary password<input readonly value="${esc(r.temporaryPassword)}"/></label><p>${esc(r.note)}</p>`;await refresh();}catch(e){alert(`Reset failed: ${friendly(e)}`);}}
async function toggleUser(userId,enabled){try{await postJson(`/api/auth/users/${userId}/enabled`,{enabled});await refresh();}catch(e){alert(`User update failed: ${friendly(e)}`);}}
function renderAudit(items){$('auditList').innerHTML=items.length?items.slice(0,150).map(a=>`<div class="event-card ${a.outcome==='failed'?'event-bad':'event-good'}"><div class="event-time">${esc(a.actorUsername)} · ${esc(a.actorRole)} · ${ago(a.timestampUtc)}</div><strong>${esc(a.action)} → ${esc(a.target)}</strong><p>${esc(a.detail)}</p><small>${esc(a.outcome)} · ${esc(a.remoteIpAddress)} · ${esc(localDate(a.timestampUtc))}</small></div>`).join(''):'<div class="empty-state">No audit events yet.</div>';}

$('loginForm').addEventListener('submit',login);
$('passwordForm').addEventListener('submit',changePassword);
$('logout').onclick=logout;
$('changePassword').onclick=()=>showPasswordChange(false);
$('createUser').onclick=createUser;
$('enrollAgent').onclick=enrollAgent;
$('createMaintenance').onclick=createMaintenance;
$('refreshAnalytics').onclick=async()=>{lastAnalyticsFetch=0;lastNodeHistoryFetch=0;await refresh(true);};
$('analyticsRange').addEventListener('change',async()=>{lastAnalyticsFetch=0;lastNodeHistoryFetch=0;await refresh(true);});
$('slaTarget').addEventListener('change',async()=>{lastAnalyticsFetch=0;await refreshAnalytics();});
$('agentSelect').addEventListener('change',async e=>{selectedAgentId=e.target.value;lastNodeHistoryFetch=0;renderAgent(latestAgents.find(a=>a.heartbeat.agentId===selectedAgentId)||null);await refreshNodeHistory();});
$('killDemo').onclick=injectFailure;
$('previewRestart').onclick=previewRestart;
$('executePreview').onclick=executePreview;
$('cancelPreview').onclick=()=>{currentPreview=null;renderPreview();};

bootstrap();
setInterval(()=>{if(currentUser&&!currentUser.mustChangePassword&&$('loginOverlay').classList.contains('hidden'))refresh(false);},3000);
