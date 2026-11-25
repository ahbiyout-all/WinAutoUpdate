const STORAGE_KEY = 'ua_admin_token';
const HEARTBEAT_SECONDS = 15;

let adminToken = localStorage.getItem(STORAGE_KEY) || '';
let snapshot = null;
let filterText = '';
let controller = null;
let reconnectTimer = null;
let backoffMs = 1000;
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

function isTerminal(state) {
  return ['completed', 'failed', 'cancelled'].includes(String(state).toLowerCase());
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
    ['등록된 PC', `${data.agentCount ?? 0}`, `${data.onlineCount ?? 0}대 온라인`, 'blue'],
    ['작업 중', `${data.activeJobCount ?? 0}`, `${data.busyCount ?? 0}대 실행 중`, 'amber'],
    ['확인 필요', `${data.attentionCount ?? 0}`, 'Maestro 작업', 'red'],
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
  const active = jobs.filter((job) =>
    !isTerminal(job.state) && matchesFilter(`${job.machineName} ${job.jobId}`));

  if (active.length === 0) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty">진행 중인 작업이 없습니다.</td></tr>`;
    return;
  }

  for (const job of active) {
    const [label, tone] = jobStateInfo(job.state);
    const attention = job.requiresMaestroAttention ? chip('Maestro 확인', 'red') : '';
    const tr = el('tr');
    tr.innerHTML = `
      <td>
        <div class="strong">${escapeHtml(job.machineName)}</div>
        <div class="muted small mono">${escapeHtml(job.jobId)}</div>
      </td>
      <td>
        ${chip(label, tone)} ${job.isDryRun ? chip('DRY RUN', 'purple') : ''}
        <div class="muted small">${escapeHtml(job.phase || '')}</div>
      </td>
      <td>
        ${progressBar(job.progressPercent)}
        <div class="muted small">${job.progressPercent || 0}% · ${job.completedItems || 0}/${job.totalItems || 0}</div>
      </td>
      <td>
        ${job.rebootCount || 0}/${job.maxRebootCount || 0}
        <div>${attention}</div>
      </td>
      <td>
        <div class="small">${escapeHtml(formatTime(job.updatedAt))}</div>
        <div class="muted small">${escapeHtml(ago(job.updatedAt))}</div>
      </td>
      <td>
        <button class="btn btn-mini" data-job-id="${escapeHtml(job.jobId)}">상세</button>
      </td>`;
    tbody.appendChild(tr);
  }
}

function renderAgents(agents) {
  const tbody = $('#agents-body');
  tbody.innerHTML = '';
  const visible = agents.filter((agent) => matchesFilter(agent.machineName));

  if (visible.length === 0) {
    tbody.innerHTML = `<tr><td colspan="5" class="empty">표시할 에이전트가 없습니다.</td></tr>`;
    return;
  }

  for (const agent of visible) {
    const state = agent.online ? agent.state : 'offline';
    const [label, tone] = stateInfo(state);
    const current = agent.hasActiveJob
      ? `${chip('작업 중', 'blue')} ${progressBar(agent.progressPercent)}
         <div class="muted small">${escapeHtml(agent.phase || '')}</div>`
      : `<span class="muted">-</span>`;
    const maestro = agent.maestroInstalled
      ? chip('설치됨', 'green')
      : agent.maestroDetectionFailed ? chip('확인 실패', 'red') : chip('없음', 'gray');
    const reboot = agent.rebootPending ? `<div class="muted small amber-text">재부팅 대기</div>` : '';
    const tr = el('tr');
    tr.innerHTML = `
      <td>
        <div class="strong">${escapeHtml(agent.machineName)}</div>
        <div class="muted small mono">${escapeHtml(agent.agentId)}</div>
      </td>
      <td>${chip(label, tone)}</td>
      <td>${current}</td>
      <td>${maestro}${reboot}</td>
      <td>
        <div class="small">${escapeHtml(formatTime(agent.lastSeen))}</div>
        <div class="muted small">${escapeHtml(ago(agent.lastSeen))}</div>
      </td>`;
    tbody.appendChild(tr);
  }
}

function renderSnapshot(data) {
  snapshot = data;
  renderStats(data);
  renderActiveJobs(data.jobs || []);
  renderAgents(data.agents || []);
  $('#last-update').textContent = `마지막 수신: ${formatTime(data.serverTime)}`;
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

    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let boundary;
      while ((boundary = buffer.indexOf('\n\n')) !== -1) {
        const frame = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        handleFrame(frame);
      }
    }
  } catch (error) {
    if (error.name === 'AbortError') return;
    setConnectionState('disconnected', '연결 끊김');
    setError(`실시간 스트림 오류: ${error.message}. 재연결합니다.`);
  }

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

wireEvents();
connect();
