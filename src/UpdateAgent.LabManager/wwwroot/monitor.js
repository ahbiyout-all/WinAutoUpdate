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
    const wrap = el('span', 'filter-state');
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
    wrap.appendChild(label);
    wrap.appendChild(select);
    filterInput.insertAdjacentElement('afterend', wrap);
  }
  updateConnectionMeta();
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
    // 오프라인은 PC 상태 필터다. 작업에는 오프라인 상태가 없으므로 표시하지 않는다.
    return kind === 'agent' && (record?.online === false || isStale(record?.lastSeen, snapshotNow()));
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
    const stale = isStale(agent.lastSeen, snapshotNow()) ? chip('오래됨', 'amber') : '';
    const tr = el('tr');
    tr.innerHTML = `
      <td>
        <div class="strong">${escapeHtml(valueText(agent.machineName))}</div>
        <div class="muted small mono">${escapeHtml(valueText(agent.agentId))}</div>
      </td>
      <td>${chip(label, tone)}</td>
      <td>${current}</td>
      <td>${maestro}${reboot}</td>
      <td>
        <div class="small">${escapeHtml(timeText(agent.lastSeen))} ${stale}</div>
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
    filterText = event.target.value.trim().toLowerCase();
    if (snapshot) {
      renderActiveJobs(snapshot.jobs || []);
      renderAgents(snapshot.agents || []);
    }
  });
  // 텍스트 검색과 상태 선택은 작업 표와 에이전트 표에 함께 적용된다.
  $('#state-filter')?.addEventListener('change', (event) => {
    stateFilter = event.target.value;
    if (snapshot) {
      renderActiveJobs(snapshot.jobs || []);
      renderAgents(snapshot.agents || []);
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
connect();
