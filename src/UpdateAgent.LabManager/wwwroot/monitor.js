const STORAGE_KEY = 'ua_admin_token';
const HEARTBEAT_SECONDS = 15;
const STALE_SECONDS = 90;
const ABSENT = '없음';
const UNKNOWN = '확인 불가';

let adminToken = localStorage.getItem(STORAGE_KEY) || '';
let snapshot = null;
let filterText = '';
let stateFilter = 'all';
let controller = null;
let reconnectTimer = null;
let backoffMs = 1000;
let reconnectAttempts = 0;
let lastSuccessAt = null;
const MAX_BACKOFF_MS = 30000;

// 필터는 URL/클립보드/창 간 메시지에만 실을 수 있다. 토큰·비밀 값은 어디에도 직렬화하지 않는다.
const FILTER_VALUE_MAX = 64;
const FILTER_PAYLOAD_MAX = 512;
const FILTER_DEBOUNCE_MS = 250;
const FILTER_MESSAGE_SKEW_MS = 60000;
const MONITOR_FILTER_CHANNEL = 'updateagent-monitor-filters';
const MONITOR_FILTER_STORAGE_KEY = 'ua_monitor_filters';
const MONITOR_STATE_IDS = ['all', 'busy', 'attention', 'offline'];
const SENSITIVE_KEY_PATTERN = /(?:token|secret|password|passwd|pwd|api[-_]?key|auth|credential|session)/i;
const UNKNOWN_FILTER_NOTICE = '필터 값 확인 불가: 알 수 없는 값은 무시하고 없음/전체로 표시합니다.';
const FILTER_SOURCE_ID = (() => {
  try {
    if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  } catch {}
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
})();

let filterBroadcast = null;
let filterTextTimer = null;
let pendingFilterText = null;
let monitorFilterUnknown = false;

const $ = (sel) => document.querySelector(sel);
const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
};

function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function stateInfo(state) {
  const map = {
    offline: ['오프라인', 'gray'],
    online: ['온라인', 'green'],
    idle: ['대기', 'green'],
    busy: ['작업 중', 'blue'],
    paused: ['일시중지', 'amber'],
    rebooting: ['재부팅', 'purple'],
    needsattention: ['확인 필요', 'red'],
    error: ['오류', 'red'],
  };
  return map[String(state).toLowerCase()] || [state, 'gray'];
}

function jobStateInfo(state) {
  const map = {
    pending: ['대기', 'gray'],
    preflight: ['사전 점검', 'blue'],
    scanning: ['검색 중', 'blue'],
    downloading: ['다운로드', 'blue'],
    installing: ['설치 중', 'blue'],
    pendingreboot: ['재부팅 대기', 'amber'],
    rebooting: ['재부팅 중', 'purple'],
    paused: ['일시중지', 'amber'],
    completed: ['완료', 'green'],
    failed: ['실패', 'red'],
    cancelled: ['취소됨', 'gray'],
  };
  return map[String(state).toLowerCase()] || [state, 'gray'];
}

function chip(label, tone) {
  return `<span class="chip chip-${tone}">${escapeHtml(label)}</span>`;
}

// 무결성/이탈 상태는 플래그가 없으면(true/false로 추측하지 않고) 확인 불가로 표시한다.
function integrityChips(agent) {
  const chips = [];
  if (agent.integrityUnavailable !== false) chips.push(chip('무결성 확인 불가', 'gray'));
  if (agent.configChanged === true) chips.push(chip('설정 변경', 'red'));
  if (agent.versionMismatch === true) chips.push(chip('버전 불일치', 'red'));
  else if (agent.versionUnknown !== false) chips.push(chip('버전 확인 불가', 'gray'));
  if (agent.departed === true) chips.push(chip('이탈', 'red'));
  return chips.join(' ');
}

function formatTime(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('ko-KR', {
    month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

function ago(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return `${seconds}초 전`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}분 전`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)}시간 전`;
  return `${Math.round(seconds / 86400)}일 전`;
}

function timeText(value) {
  return formatTime(value) || ABSENT;
}

function agoText(value) {
  return ago(value) || ABSENT;
}

function valueText(value) {
  return value === null || value === undefined || value === '' ? ABSENT : String(value);
}

function countSubText(value, suffix) {
  return value === null || value === undefined ? `${suffix} ${ABSENT}` : `${value}${suffix}`;
}

// 플래그가 아직 오지 않았으면 true/false로 추측하지 않는다.
function unknownFlag(value) {
  return value === null || value === undefined ? UNKNOWN : value;
}

function isStale(lastSeen, now) {
  if (!lastSeen) return false;
  const seen = new Date(lastSeen).getTime();
  const base = new Date(now || Date.now()).getTime();
  if (Number.isNaN(seen) || Number.isNaN(base)) return false;
  return (base - seen) / 1000 > STALE_SECONDS;
}

// 서버가 보낸 스냅샷 기준 시각을 우선 사용한다(구 버전은 serverTime/브라우저 시각).
function snapshotNow() {
  return snapshot?.snapshotAt || snapshot?.serverTime || Date.now();
}

function updateConnectionMeta() {
  const meta = $('#connection-meta');
  if (!meta) return;
  meta.textContent = `최종 갱신: ${lastSuccessAt ? formatTime(lastSuccessAt) : ABSENT} · 재연결: ${reconnectAttempts}회`;
}

// monitor.html 파일은 수정하지 않고 필요한 표시/필터 요소만 런타임에 추가한다.
function ensureMonitorControls() {
  const lastUpdate = $('#last-update');
  if (lastUpdate && !$('#connection-meta')) {
    const meta = el('span', 'muted small', '');
    meta.id = 'connection-meta';
    lastUpdate.insertAdjacentElement('afterend', meta);
  }

  const filterInput = $('#filter-input');
  if (filterInput && !$('#state-filter')) {
    const wrap = el('span', 'filter-state filter-share');
    const label = el('label', 'muted small', '상태 ');
    label.htmlFor = 'state-filter';
    const select = document.createElement('select');
    select.id = 'state-filter';
    select.className = 'filter-select';
    select.setAttribute('aria-label', '상태 필터');
    for (const [value, text] of [
      ['all', '전체'], ['busy', '작업 중'], ['attention', '확인 필요'], ['offline', '오프라인'],
    ]) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = text;
      select.appendChild(option);
    }
    const copy = el('button', 'btn btn-ghost', '필터 링크 복사');
    copy.type = 'button';
    copy.id = 'filter-link-copy';
    copy.addEventListener('click', copyFilterLink);
    const status = el('span', 'muted small', '');
    status.id = 'filter-link-status';
    const notice = el('div', 'muted small', '');
    notice.id = 'filter-notice';
    wrap.appendChild(label);
    wrap.appendChild(select);
    wrap.appendChild(copy);
    wrap.appendChild(status);
    filterInput.insertAdjacentElement('afterend', wrap);
    wrap.insertAdjacentElement('afterend', notice);
  }
  updateConnectionMeta();
  updateFilterNotice();
}

function updateFilterNotice() {
  const notice = $('#filter-notice');
  if (notice) notice.textContent = monitorFilterUnknown ? UNKNOWN_FILTER_NOTICE : '';
}

function setFilterLinkStatus(message, isError) {
  const status = $('#filter-link-status');
  if (status) status.textContent = message || '';
  if (isError) setError(message);
}


function isTerminal(state) {
  return ['completed', 'failed', 'cancelled'].includes(String(state).toLowerCase());
}

function matchesStateFilter(record, kind) {
  if (stateFilter === 'all') return true;
  const state = String(record?.state ?? '').toLowerCase();
  if (stateFilter === 'busy') {
    return kind === 'job'
      ? !isTerminal(state)
      : record?.hasActiveJob === true || state === 'busy';
  }
  if (stateFilter === 'attention') {
    return kind === 'job'
      ? record?.requiresMaestroAttention === true || state === 'needsattention' || state === 'error'
      : state === 'needsattention' || state === 'error' || record?.maestroDetectionFailed === true;
  }
  if (stateFilter === 'offline') {
    // 오프라인은 PC 상태 필터다. 이탈(오래된 체크인)도 같은 필터에 포함한다.
    return kind === 'agent' && (record?.online === false || record?.departed === true ||
      isStale(record?.lastSeen, snapshotNow()));
  }
  return true;
}

function progressBar(percent) {
  const value = Math.max(0, Math.min(100, percent || 0));
  return `<div class="progress"><div style="width:${value}%"></div></div>`;
}

function setConnectionState(kind, text) {
  const badge = $('#live-state');
  badge.classList.remove('connected', 'connecting', 'disconnected');
  badge.classList.add(kind);
  $('#live-state-text').textContent = text;
}

function setError(message) {
  const box = $('#error-box');
  if (!message) {
    box.classList.add('hidden');
    box.textContent = '';
    return;
  }
  box.classList.remove('hidden');
  box.textContent = message;
}

function showTokenBanner(show) {
  $('#token-banner').classList.toggle('hidden', !show);
}

function matchesFilter(value) {
  if (!filterText) return true;
  return String(value ?? '').toLowerCase().includes(filterText);
}

// ---------------- 필터 URL / 창 간 동기화 ----------------

function normalizeMonitorText(value) {
  const text = String(value ?? '').trim().toLowerCase();
  if (!text || text.length > FILTER_VALUE_MAX) return '';
  // 검색어는 PC 이름/작업 ID 수준의 문자만 허용하고 토큰 같은 문자열은 거부한다.
  if (!/^[\p{L}\p{N} ._-]+$/u.test(text) || SENSITIVE_KEY_PATTERN.test(text)) return '';
  return text;
}

function normalizeMonitorState(value) {
  const text = String(value ?? '').trim().toLowerCase();
  return MONITOR_STATE_IDS.includes(text) ? text : 'all';
}

function readMonitorFilters(search = location.search) {
  const params = new URLSearchParams(search);
  const texts = params.getAll('q');
  const states = params.getAll('state');
  const q = texts.map(normalizeMonitorText).find((value) => value !== '') || '';
  const state = states.map(normalizeMonitorState).find((value) => value !== 'all') || 'all';
  const unknown = texts.some((value) => {
    const text = String(value ?? '').trim().toLowerCase();
    return text !== '' && normalizeMonitorText(text) !== text;
  }) || states.some((value) => {
    const text = String(value ?? '').trim().toLowerCase();
    // 빈 값은 '없음'으로 보고 경고하지 않는다. 값이 있을 때만 허용 목록을 검사한다.
    return text !== '' && !MONITOR_STATE_IDS.includes(text);
  });
  return { q, state, unknown };
}

// job= 등 알 수 없는 파라미터는 보존하지만 토큰/비밀 키와 토큰 같은 값은 제거한다.
function safeFilterParams(search) {
  const source = new URLSearchParams(search || '');
  const safe = new URLSearchParams();
  for (const [key, value] of source.entries()) {
    if (SENSITIVE_KEY_PATTERN.test(key)) continue;
    if (SENSITIVE_KEY_PATTERN.test(value)) continue;
    if (value.length > FILTER_VALUE_MAX * 4) continue;
    if (adminToken && value === adminToken) continue;
    safe.append(key, value);
  }
  return safe;
}

function monitorFilterUrl(search = location.search) {
  const params = safeFilterParams(search);
  params.delete('q');
  params.delete('state');
  if (filterText) params.set('q', filterText);
  if (stateFilter !== 'all') params.set('state', stateFilter);
  const query = params.size ? `?${params.toString()}` : '';
  return `${location.pathname}${query}${location.hash || ''}`;
}

function writeMonitorUrl() {
  try {
    history.replaceState(history.state, '', monitorFilterUrl());
  } catch {}
}

function renderMonitorTables() {
  if (!snapshot) return;
  renderActiveJobs(snapshot.jobs || []);
  renderAgents(snapshot.agents || []);
}

function syncFilterControls() {
  const input = $('#filter-input');
  if (input && input.value !== filterText) input.value = filterText;
  const select = $('#state-filter');
  if (select && select.value !== stateFilter) select.value = stateFilter;
}

function buildFilterMessage() {
  return {
    type: 'filters',
    sourceId: FILTER_SOURCE_ID,
    at: Date.now(),
    q: filterText,
    state: stateFilter,
  };
}

function broadcastFilters() {
  if (!filterBroadcast) return;
  try {
    filterBroadcast.postMessage(buildFilterMessage());
  } catch {}
}

function saveFilterState() {
  try {
    const payload = {
      sourceId: FILTER_SOURCE_ID,
      at: Date.now(),
      q: filterText,
      state: stateFilter,
    };
    if (JSON.stringify(payload).length <= FILTER_PAYLOAD_MAX) {
      localStorage.setItem(MONITOR_FILTER_STORAGE_KEY, JSON.stringify(payload));
    }
  } catch {}
}

function applyMonitorFilters(parsed, options = {}) {
  // 원격/뒤로 가기 적용 중에는 로컬 디바운스 타이머가 낡은 값을 되살리지 못하게 한다.
  if (filterTextTimer) {
    clearTimeout(filterTextTimer);
    filterTextTimer = null;
  }
  pendingFilterText = null;
  filterText = normalizeMonitorText(parsed?.q);
  stateFilter = normalizeMonitorState(parsed?.state);
  monitorFilterUnknown = parsed?.unknown === true;
  syncFilterControls();
  updateFilterNotice();
  renderMonitorTables();
  if (options.writeUrl) writeMonitorUrl();
  if (options.broadcast) {
    saveFilterState();
    broadcastFilters();
  }
}

function applyRemoteFilterMessage(message) {
  if (!message || typeof message !== 'object' || message.type !== 'filters') return;
  let serialized;
  try { serialized = JSON.stringify(message); } catch { return; }
  if (typeof serialized !== 'string' || serialized.length > FILTER_PAYLOAD_MAX) return;
  // 같은 탭의 메시지는 되돌려 받아도 다시 적용/전파하지 않는다(에코 방지).
  if (!message.sourceId || message.sourceId === FILTER_SOURCE_ID) return;
  if (typeof message.at !== 'number' || !Number.isFinite(message.at) || message.at < 0 ||
      message.at > Date.now() + FILTER_MESSAGE_SKEW_MS) return;
  applyMonitorFilters(
    { q: message.q, state: message.state, unknown: false },
    { writeUrl: true, broadcast: false });
}

function initFilterSync() {
  if (typeof BroadcastChannel !== 'undefined') {
    try {
      filterBroadcast = new BroadcastChannel(MONITOR_FILTER_CHANNEL);
      filterBroadcast.onmessage = (event) => applyRemoteFilterMessage(event?.data);
    } catch {
      filterBroadcast = null;
    }
  }
  window.addEventListener('storage', (event) => {
    // BroadcastChannel을 쓸 수 있는 환경에서는 중복 적용을 막는다.
    if (filterBroadcast || !event || event.key !== MONITOR_FILTER_STORAGE_KEY || !event.newValue) return;
    if (event.newValue.length > FILTER_PAYLOAD_MAX) return;
    let payload;
    try { payload = JSON.parse(event.newValue); } catch { return; }
    applyRemoteFilterMessage(payload);
  });
}

async function copyFilterLink() {
  // 입력 직후 복사하면 디바운스 전 낡은 URL이 복사되지 않도록 보류 중인 검색어를 먼저 반영한다.
  if (filterTextTimer) {
    const pending = pendingFilterText;
    clearTimeout(filterTextTimer);
    filterTextTimer = null;
    pendingFilterText = null;
    applyMonitorFilters(
      { q: pending, state: stateFilter, unknown: false },
      { writeUrl: true, broadcast: true });
  }
  const url = monitorFilterUrl();
  const status = $('#filter-link-status');
  if (!navigator.clipboard?.writeText) {
    const guidance = '링크 복사 확인 불가: 클립보드를 사용할 수 없습니다. 주소창 URL을 직접 복사하세요.';
    setFilterLinkStatus(guidance, true);
    return;
  }
  try {
    await navigator.clipboard.writeText(url);
    setFilterLinkStatus('필터 링크를 복사했습니다.', false);
  } catch {
    setFilterLinkStatus('링크 복사 확인 불가: 브라우저가 복사를 거부했습니다. 주소창 URL을 직접 복사하세요.', true);
  }
}

// ---------------- 렌더링 ----------------

function renderStats(data) {
  const host = $('#stats');
  host.innerHTML = '';
  const cards = [
    ['등록된 PC', valueText(data.agentCount), countSubText(data.onlineCount, '대 온라인'), 'blue'],
    ['작업 중', valueText(data.activeJobCount), countSubText(data.busyCount, '대 실행 중'), 'amber'],
    ['확인 필요', valueText(data.attentionCount), 'Maestro 작업', 'red'],
    ['오래됨', valueText(data.staleCount), `체크인 ${STALE_SECONDS}초 초과`, 'amber'],
    ['접속 모니터', 'LIVE', `${HEARTBEAT_SECONDS}초 하트비트`, 'green'],
  ];
  for (const [label, value, sub, tone] of cards) {
    const card = el('div', `stat stat-${tone}`);
    card.innerHTML = `<div class="stat-label">${escapeHtml(label)}</div>
      <div class="stat-value">${escapeHtml(value)}</div>
      <div class="stat-sub">${escapeHtml(sub)}</div>`;
    host.appendChild(card);
  }
}

function renderActiveJobs(jobs) {
  const tbody = $('#active-jobs-body');
  tbody.innerHTML = '';
  const hiddenCount = Number(snapshot?.hiddenActiveJobCount || 0);
  const active = jobs.filter((job) =>
    !isTerminal(job.state) && matchesFilter(`${job.machineName} ${job.jobId}`) &&
    matchesStateFilter(job, 'job'));

  if (active.length === 0) {
    const hiddenText = hiddenCount > 0
      ? ` 표시되지 않은 활성 작업 ${hiddenCount}건은 확인 불가.` : '';
    tbody.innerHTML = `<tr><td colspan="6" class="empty">조건에 맞는 진행 중인 작업이 ${ABSENT}.${hiddenText}</td></tr>`;
    return;
  }

  for (const job of active) {
    const [label, tone] = jobStateInfo(job.state);
    const attentionFlag = unknownFlag(job.requiresMaestroAttention);
    const attention = attentionFlag === UNKNOWN
      ? chip(UNKNOWN, 'gray')
      : attentionFlag ? chip('Maestro 확인', 'red') : '';
    const dryRunFlag = unknownFlag(job.isDryRun);
    const dryRun = dryRunFlag === UNKNOWN ? chip(UNKNOWN, 'gray') : dryRunFlag ? chip('DRY RUN', 'purple') : '';
    const percentText = job.progressPercent === null || job.progressPercent === undefined
      ? ABSENT : `${job.progressPercent}%`;
    const items = job.completedItems === null || job.completedItems === undefined ||
      job.totalItems === null || job.totalItems === undefined
      ? ABSENT : `${job.completedItems}/${job.totalItems}`;
    const reboots = job.rebootCount === null || job.rebootCount === undefined ||
      job.maxRebootCount === null || job.maxRebootCount === undefined
      ? ABSENT : `${job.rebootCount}/${job.maxRebootCount}`;
    const tr = el('tr');
    tr.innerHTML = `
      <td>
        <div class="strong">${escapeHtml(valueText(job.machineName))}</div>
        <div class="muted small mono">${escapeHtml(valueText(job.jobId))}</div>
      </td>
      <td>
        ${chip(label, tone)} ${dryRun}
        <div class="muted small">${escapeHtml(valueText(job.phase))}</div>
      </td>
      <td>
        ${progressBar(job.progressPercent)}
        <div class="muted small">${escapeHtml(percentText)} · ${escapeHtml(items)}</div>
      </td>
      <td>
        ${escapeHtml(reboots)}
        <div>${attention}</div>
      </td>
      <td>
        <div class="small">${escapeHtml(timeText(job.updatedAt))}</div>
        <div class="muted small">${escapeHtml(agoText(job.updatedAt))}</div>
      </td>
      <td>
        <button class="btn btn-mini" data-job-id="${escapeHtml(job.jobId)}">상세</button>
      </td>`;
    tbody.appendChild(tr);
  }

  if (hiddenCount > 0) {
    const tr = el('tr');
    tr.innerHTML = `<td colspan="6" class="empty">표시되지 않은 활성 작업 ${hiddenCount}건은 확인 불가.</td>`;
    tbody.appendChild(tr);
  }
}

function renderAgents(agents) {
  const tbody = $('#agents-body');
  tbody.innerHTML = '';
  const visible = agents.filter((agent) =>
    matchesFilter(`${agent.machineName} ${agent.agentId}`) && matchesStateFilter(agent, 'agent'));

  if (visible.length === 0) {
    tbody.innerHTML = `<tr><td colspan="5" class="empty">조건에 맞는 에이전트가 ${ABSENT}.</td></tr>`;
    return;
  }

  for (const agent of visible) {
    const state = agent.online ? agent.state : 'offline';
    const [label, tone] = stateInfo(state ?? UNKNOWN);
    const hasJob = unknownFlag(agent.hasActiveJob);
    const current = hasJob === UNKNOWN
      ? chip(UNKNOWN, 'gray')
      : hasJob
        ? `${chip('작업 중', 'blue')} ${progressBar(agent.progressPercent)}
           <div class="muted small">${escapeHtml(valueText(agent.phase))}</div>`
        : `<span class="muted">${ABSENT}</span>`;
    const installed = unknownFlag(agent.maestroInstalled);
    const detectionFailed = unknownFlag(agent.maestroDetectionFailed);
    const maestro = installed === UNKNOWN || detectionFailed === UNKNOWN
      ? chip(UNKNOWN, 'gray')
      : installed ? chip('설치됨', 'green')
        : detectionFailed ? chip('확인 실패', 'red') : chip(ABSENT, 'gray');
    const rebootFlag = unknownFlag(agent.rebootPending);
    const reboot = rebootFlag === true ? '<div class="muted small amber-text">재부팅 대기</div>'
      : rebootFlag === UNKNOWN ? `<div class="muted small">재부팅 ${UNKNOWN}</div>` : '';
    const departed = agent.departed === true;
    const stale = departed ? chip('이탈', 'red')
      : isStale(agent.lastSeen, snapshotNow()) ? chip('오래됨', 'amber') : '';
    const departure = departed
      ? `<div class="muted small">${escapeHtml(agent.departureReason || '체크인 없음')}</div>` : '';
    const tr = el('tr');
    tr.innerHTML = `
      <td>
        <div class="strong">${escapeHtml(valueText(agent.machineName))}</div>
        <div class="muted small mono">${escapeHtml(valueText(agent.agentId))}</div>
      </td>
      <td>
        ${chip(label, tone)}
        <div class="small">${integrityChips(agent)}</div>
        <button class="btn btn-mini" data-baseline-agent-id="${escapeHtml(valueText(agent.agentId))}">기준 재설정</button>
      </td>
      <td>${current}</td>
      <td>${maestro}${reboot}</td>
      <td>
        <div class="small">${escapeHtml(timeText(agent.lastSeen))} ${stale}</div>
        ${departure}
        <div class="muted small">${escapeHtml(agoText(agent.lastSeen))}</div>
      </td>`;
    tbody.appendChild(tr);
  }
}

function renderSnapshot(data) {
  snapshot = data;
  renderStats(data);
  renderActiveJobs(data.jobs || []);
  renderAgents(data.agents || []);
  lastSuccessAt = data.snapshotAt || data.serverTime || new Date().toISOString();
  // 성공적으로 스냅샷을 받았으면 지수 백오프를 처음부터 다시 시작한다.
  backoffMs = 1000;
  $('#last-update').textContent = `마지막 수신: ${timeText(lastSuccessAt)}`;
  updateConnectionMeta();
}

// ---------------- 스트림 ----------------

function handleFrame(frame) {
  let event = 'message';
  let data = '';
  for (const line of frame.split('\n')) {
    if (line.startsWith(':')) continue;
    if (line.startsWith('event:')) event = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trimStart();
  }
  if (event === 'snapshot' && data) {
    try {
      renderSnapshot(JSON.parse(data));
    } catch {
      // 잘못된 프레임은 무시하고 다음 스냅샷을 기다린다.
    }
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectAttempts += 1;
  updateConnectionMeta();
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, backoffMs);
  backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF_MS);
}

async function connect() {
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (controller) controller.abort();

  if (!adminToken) {
    setConnectionState('disconnected', '토큰 필요');
    setError('');
    showTokenBanner(true);
    return;
  }

  showTokenBanner(false);
  setConnectionState('connecting', '연결 중');

  controller = new AbortController();
  try {
    const response = await fetch('/api/admin/live', {
      headers: { 'X-Admin-Token': adminToken },
      cache: 'no-store',
      signal: controller.signal,
    });

    if (response.status === 401) {
      setConnectionState('disconnected', '인증 실패');
      setError('관리자 토큰이 올바르지 않습니다. 아래에서 토큰을 다시 저장하세요.');
      showTokenBanner(true);
      return;
    }
    if (!response.ok || !response.body) {
      throw new Error(`스트림 연결 실패 (${response.status})`);
    }

    setError('');
    setConnectionState('connected', '실시간 연결');
    backoffMs = 1000;

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let endedNormally = false;

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        // 서버가 스트림을 정상 닫아도 연결을 유지하려면 여기서 재연결을 예약해야 한다.
        endedNormally = true;
        break;
      }
      buffer += decoder.decode(value, { stream: true });
      let boundary;
      while ((boundary = buffer.indexOf('\n\n')) !== -1) {
        const frame = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        handleFrame(frame);
      }
    }
    if (endedNormally) setError('');
  } catch (error) {
    if (error.name === 'AbortError') return;
    setConnectionState('disconnected', '연결 끊김');
    setError(`실시간 스트림 오류: ${error.message}. 재연결합니다.`);
  }

  // 401(인증 실패)과 토큰 누락은 위에서 조기 반환하므로 여기까지 오지 않는다.
  setConnectionState('connecting', '재연결 중');
  scheduleReconnect();
}

// ---------------- 이벤트 ----------------

function openDashboard(jobId) {
  const url = jobId ? `/?job=${encodeURIComponent(jobId)}` : '/';
  window.open(url, '_blank');
}

function wireEvents() {
  $('#dashboard-button').addEventListener('click', () => openDashboard());
  $('#reconnect-button').addEventListener('click', () => connect());
  $('#filter-input').addEventListener('input', (event) => {
    if (filterTextTimer) clearTimeout(filterTextTimer);
    const value = event.target.value;
    pendingFilterText = value;
    filterTextTimer = setTimeout(() => {
      filterTextTimer = null;
      pendingFilterText = null;
      applyMonitorFilters(
        { q: value, state: stateFilter, unknown: false },
        { writeUrl: true, broadcast: true });
    }, FILTER_DEBOUNCE_MS);
  });
  // 텍스트 검색과 상태 선택은 작업 표와 에이전트 표에 함께 적용된다.
  $('#state-filter')?.addEventListener('change', (event) => {
    applyMonitorFilters(
      { q: filterText, state: event.target.value, unknown: false },
      { writeUrl: true, broadcast: true });
  });
  window.addEventListener('popstate', () => {
    applyMonitorFilters(readMonitorFilters(location.search), { writeUrl: false, broadcast: true });
  });
  $('#agents-body').addEventListener('click', async (event) => {
    const button = event.target.closest?.('button[data-baseline-agent-id]');
    if (!button) return;
    try {
      const response = await fetch(
        `/api/admin/agents/${encodeURIComponent(button.dataset.baselineAgentId)}/integrity/baseline`,
        { method: 'POST', headers: { 'X-Admin-Token': adminToken } });
      if (response.status === 401) {
        setConnectionState('disconnected', '인증 실패');
        setError('관리자 토큰이 올바르지 않습니다. 아래에서 토큰을 다시 저장하세요.');
        showTokenBanner(true);
        return;
      }
      if (!response.ok) throw new Error(`기준 재설정 실패 (${response.status})`);
      setError('');
      connect();
    } catch (error) {
      setError(`기준 재설정 오류: ${error.message}`);
    }
  });
  $('#token-save').addEventListener('click', () => {
    const value = $('#token-input').value.trim();
    if (!value) return;
    adminToken = value;
    localStorage.setItem(STORAGE_KEY, value);
    $('#token-input').value = '';
    setError('');
    connect();
  });
  $('#token-input').addEventListener('keydown', (event) => {
    if (event.key === 'Enter') $('#token-save').click();
  });
  $('#active-jobs-body').addEventListener('click', (event) => {
    const button = event.target.closest('button[data-job-id]');
    if (button) openDashboard(button.dataset.jobId);
  });
}

ensureMonitorControls();
wireEvents();
initFilterSync();
applyMonitorFilters(readMonitorFilters(location.search), { writeUrl: false, broadcast: false });
connect();
