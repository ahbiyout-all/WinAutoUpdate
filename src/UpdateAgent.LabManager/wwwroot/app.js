const STORAGE_KEY = 'ua_admin_token';
// 설치 파일에 들어가는 공개 샘플 값이다. 실제 배포 토큰은 절대 여기에 넣지 않는다.
const SAMPLE_ADMIN_TOKEN = 'sample-admin-token-replace-before-pilot';
let adminToken = localStorage.getItem(STORAGE_KEY) || '';
let agents = [];
let jobs = [];
let audit = [];
let overview = {};
let selected = new Set();
let openJobId = null;
let lastError = '';
let pendingJobId = null;

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

async function api(path, options = {}) {
  const headers = Object.assign(
    { 'X-Admin-Token': adminToken },
    options.body ? { 'Content-Type': 'application/json' } : {},
    options.headers || {}
  );
  const response = await fetch(path, Object.assign({}, options, { headers }));
  if (response.status === 401) {
    setError('관리자 토큰이 올바르지 않습니다.');
    throw new Error('unauthorized');
  }
  if (!response.ok) {
    let message = `요청 실패 (${response.status})`;
    try {
      const body = await response.json();
      message = body.error || message;
    } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return await response.json();
}

function setError(message) {
  lastError = message || '';
  const box = $('#error-box');
  if (!box) return;
  if (!message) {
    box.classList.add('hidden');
    box.textContent = '';
  } else {
    box.classList.remove('hidden');
    box.textContent = message;
  }
}

function toast(message, kind = 'info') {
  const host = $('#toast-host');
  const item = el('div', `toast toast-${kind}`, message);
  host.appendChild(item);
  setTimeout(() => item.classList.add('show'), 10);
  setTimeout(() => {
    item.classList.remove('show');
    setTimeout(() => item.remove(), 250);
  }, 3800);
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
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

function ago(value) {
  if (!value) return '';
  const date = new Date(value);
  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return `${seconds}초 전`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}분 전`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)}시간 전`;
  return `${Math.round(seconds / 86400)}일 전`;
}

// ---------------- 렌더링 ----------------

function renderOverview() {
  const host = $('#stats');
  host.innerHTML = '';
  const cards = [
    ['등록된 PC', `${overview.agentCount ?? 0}`, `${overview.onlineCount ?? 0}대 온라인`, 'blue'],
    ['작업 중', `${overview.activeJobs ?? 0}`, `${overview.busyCount ?? 0}대 실행 중`, 'amber'],
    ['완료', `${overview.completedJobs ?? 0}`, '누적 작업', 'green'],
    ['실패', `${overview.failedJobs ?? 0}`, '확인 필요', 'red'],
  ];
  for (const [label, value, sub, tone] of cards) {
    const card = el('div', `stat stat-${tone}`);
    card.innerHTML = `<div class="stat-label">${escapeHtml(label)}</div>
      <div class="stat-value">${escapeHtml(value)}</div>
      <div class="stat-sub">${escapeHtml(sub)}</div>`;
    host.appendChild(card);
  }
}

function renderAgents() {
  const tbody = $('#agents-body');
  tbody.innerHTML = '';

  if (agents.length === 0) {
    tbody.innerHTML = `<tr><td colspan="8" class="empty">등록된 PC가 없습니다. 학생 PC에 서비스를 설치하고 LabManager 주소를 설정하세요.</td></tr>`;
    return;
  }

  for (const record of agents) {
    const agent = record.agent;
    const telemetry = record.telemetry || {};
    const online = record.online;
    const state = online ? telemetry.state || 'idle' : 'offline';
    const [label, tone] = stateInfo(state);
    const os = agent.os || {};
    const job = telemetry.currentJob;

    const tr = el('tr');
    if (selected.has(agent.agentId)) tr.classList.add('selected');

    const check = document.createElement('input');
    check.type = 'checkbox';
    check.checked = selected.has(agent.agentId);
    check.addEventListener('change', () => {
      if (check.checked) selected.add(agent.agentId);
      else selected.delete(agent.agentId);
      renderAgents();
    });

    const checkCell = el('td');
    checkCell.appendChild(check);
    tr.appendChild(checkCell);

    const nameCell = el('td');
    nameCell.innerHTML = `<div class="strong">${escapeHtml(agent.machineName)}</div>
      <div class="muted mono">${escapeHtml(agent.ipAddress || agent.agentId.slice(0, 13))}</div>`;
    tr.appendChild(nameCell);

    const stateCell = el('td');
    stateCell.innerHTML = chip(label, tone) +
      (telemetry.userLoggedOn ? ` ${chip('사용자 로그온', 'amber')}` : '') +
      (telemetry.wuBusy ? ` ${chip('WU 사용 중', 'red')}` : '');
    tr.appendChild(stateCell);

    const jobCell = el('td');
    if (job) {
      const [jobLabel, jobTone] = jobStateInfo(job.state);
      jobCell.innerHTML = `${chip(jobLabel, jobTone)}
        <div class="progress mini"><div style="width:${Math.max(0, Math.min(100, job.progressPercent || 0))}%"></div></div>
        <div class="muted small">${escapeHtml(job.phase || '')}</div>`;
    } else {
      jobCell.innerHTML = '<span class="muted">-</span>';
    }
    tr.appendChild(jobCell);

    const osCell = el('td');
    osCell.innerHTML = `<div class="small">${escapeHtml(os.shortName || os.productName || '알 수 없음')}</div>
      <div class="muted small">${escapeHtml(os.versionString || '')}</div>
      ${agent.maestroInstalled ? '<div class="small amber-text">Maestro 감지됨</div>' : ''}`;
    tr.appendChild(osCell);

    const freeCell = el('td');
    const freeGb = telemetry.freeDiskMb ? (telemetry.freeDiskMb / 1024).toFixed(1) : '?';
    freeCell.innerHTML = `<div class="small">${escapeHtml(String(freeGb))} GB</div>
      <div class="muted small">${escapeHtml(agent.isLaptop ? '노트북' : '데스크톱')}</div>`;
    tr.appendChild(freeCell);

    const seenCell = el('td');
    seenCell.innerHTML = `<div class="small">${online ? '온라인' : '오프라인'}</div>
      <div class="muted small">${escapeHtml(ago(record.lastSeen))}</div>`;
    tr.appendChild(seenCell);

    const actionCell = el('td');
    actionCell.className = 'actions';
    actionCell.appendChild(actionButton('WoL', () => wakeAgent(agent), !online));
    actionCell.appendChild(actionButton('업데이트', () => openStartDialog([agent.agentId])));
    actionCell.appendChild(actionButton('해제', () => unlockAgent(agent)));
    actionCell.appendChild(actionButton('재부팅', () => rebootAgent(agent)));
    actionCell.appendChild(actionButton('종료', () => shutdownAgent(agent)));
    tr.appendChild(actionCell);

    tbody.appendChild(tr);
  }

  const selectAll = $('#select-all');
  selectAll.checked = agents.length > 0 && agents.every((a) => selected.has(a.agent.agentId));
  $('#selected-count').textContent = selected.size > 0 ? `${selected.size}대 선택됨` : '';
}

function actionButton(label, handler, disabled = false) {
  const button = el('button', 'btn btn-mini', label);
  button.disabled = disabled;
  button.addEventListener('click', async () => {
    try {
      await handler();
    } catch (error) {
      toast(error.message || '오류가 발생했습니다.', 'error');
    }
  });
  return button;
}

function renderJobs() {
  const tbody = $('#jobs-body');
  tbody.innerHTML = '';

  if (jobs.length === 0) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty">작업 기록이 없습니다.</td></tr>`;
    return;
  }

  for (const record of jobs.slice(0, 40)) {
    const job = record.summary;
    const [label, tone] = jobStateInfo(job.state);
    const tr = el('tr');

    const pcCell = el('td');
    pcCell.innerHTML = `<div class="strong">${escapeHtml(record.machineName || job.agentName || '')}</div>
      <div class="muted small mono">${escapeHtml(job.jobId)}</div>`;
    tr.appendChild(pcCell);

    const stateCell = el('td');
    stateCell.innerHTML = `${chip(label, tone)}
      <div class="muted small">${escapeHtml(job.phase || '')}</div>`;
    tr.appendChild(stateCell);

    const progressCell = el('td');
    progressCell.innerHTML = `<div class="progress"><div style="width:${Math.max(0, Math.min(100, job.progressPercent || 0))}%"></div></div>
      <div class="muted small">${job.progressPercent || 0}% · 설치 ${job.installedItems || 0}/${job.totalItems || 0}</div>`;
    tr.appendChild(progressCell);

    const rebootCell = el('td');
    rebootCell.innerHTML = `<div class="small">${job.rebootCount || 0}/${job.maxRebootCount || 0}</div>
      <div class="muted small">${job.requiresMaestroAttention ? 'Maestro 확인 필요' : ''}</div>`;
    tr.appendChild(rebootCell);

    const timeCell = el('td');
    timeCell.innerHTML = `<div class="small">${escapeHtml(formatTime(job.updatedAt))}</div>
      <div class="muted small">${escapeHtml(ago(job.updatedAt))}</div>`;
    tr.appendChild(timeCell);

    const actionCell = el('td');
    actionCell.className = 'actions';
    actionCell.appendChild(actionButton('상세', () => openJobDetail(job.jobId)));
    if (!job.isTerminal) {
      if (job.state === 'paused') {
        actionCell.appendChild(actionButton('재개', () => jobCommand(job, 'resume')));
      } else {
        actionCell.appendChild(actionButton('일시중지', () => jobCommand(job, 'pause')));
      }
      actionCell.appendChild(actionButton('취소', () => jobCommand(job, 'cancel')));
    }
    if (job.requiresMaestroAttention) {
      actionCell.appendChild(actionButton('Maestro 확인', () => openJobDetail(job.jobId)));
    }
    tr.appendChild(actionCell);

    tbody.appendChild(tr);
  }
}

function renderAudit() {
  const host = $('#audit-list');
  host.innerHTML = '';
  if (audit.length === 0) {
    host.innerHTML = '<div class="empty">감사 로그가 없습니다.</div>';
    return;
  }

  for (const entry of audit.slice(0, 25)) {
    const row = el('div', 'audit-row');
    row.innerHTML = `<div class="audit-time">${escapeHtml(formatTime(entry.timestamp))}</div>
      <div class="audit-main"><span class="mono small">${escapeHtml(entry.action)}</span>
      <span class="muted small"> ${escapeHtml(entry.target)}</span>
      <div class="small">${escapeHtml(entry.detail)}</div></div>`;
    host.appendChild(row);
  }
}

// ---------------- 데이터 ----------------

async function refresh() {
  try {
    const [agentsData, jobsData, overviewData, auditData] = await Promise.all([
      api('/api/admin/agents'),
      api('/api/admin/jobs?limit=60'),
      api('/api/admin/overview'),
      api('/api/admin/audit?limit=60'),
    ]);
    agents = agentsData || [];
    jobs = jobsData || [];
    overview = overviewData || {};
    audit = auditData || [];
    setError('');
    renderOverview();
    renderAgents();
    renderJobs();
    renderAudit();
    if (openJobId) await refreshJobDetail(openJobId, true);
    // 첫 성공 이후 백그라운드에서 GitHub Releases를 확인한다(30분 간격).
    maybeCheckForUpdates(false);
  } catch (error) {
    if (error.message !== 'unauthorized') {
      setError(`서버 연결 오류: ${error.message}`);
    }
  }
}

async function refreshJobDetail(jobId, silent = false) {
  try {
    const detail = await api(`/api/admin/jobs/${encodeURIComponent(jobId)}`);
    openJobId = jobId;
    renderJobDetail(detail);
    if (!silent) $('#detail-modal').classList.remove('hidden');
  } catch (error) {
    if (!silent) toast(error.message, 'error');
  }
}

// 모니터의 작업 상세 링크(/?job=<id>)로 열었을 때 해당 작업 상세를 띄운다.
function consumeDeepLink() {
  if (!pendingJobId || !adminToken) return;
  const jobId = pendingJobId;
  pendingJobId = null;
  refreshJobDetail(jobId);
}

// ---------------- 동작 ----------------

async function wakeAgent(agent) {
  const result = await api(`/api/admin/agents/${encodeURIComponent(agent.agentId)}/wake`, {
    method: 'POST',
  });
  const sent = (result.results || []).filter((r) => r.sent).length;
  toast(sent > 0 ? `${agent.machineName}: WoL 패킷 ${sent}건 전송` : '전송할 MAC 주소가 없거나 실패했습니다.', sent > 0 ? 'success' : 'error');
}

async function unlockAgent(agent) {
  await api(`/api/admin/agents/${encodeURIComponent(agent.agentId)}/unlock`, { method: 'POST' });
  toast(`${agent.machineName}: 입력 잠금 해제 명령을 보냈습니다.`, 'success');
}

async function rebootAgent(agent) {
  if (!confirm(`${agent.machineName} PC를 60초 후 재부팅할까요?\n저장하지 않은 학생 작업이 사라질 수 있습니다.`)) return;
  await api(`/api/admin/agents/${encodeURIComponent(agent.agentId)}/reboot`, {
    method: 'POST',
    body: JSON.stringify({ delaySeconds: 60, reason: '교사 요청에 의한 재부팅' }),
  });
  toast(`${agent.machineName}: 재부팅 명령을 보냈습니다.`, 'success');
}

async function shutdownAgent(agent) {
  if (!confirm(`${agent.machineName} PC를 60초 후 종료할까요?`)) return;
  await api(`/api/admin/agents/${encodeURIComponent(agent.agentId)}/shutdown`, {
    method: 'POST',
    body: JSON.stringify({ delaySeconds: 60, reason: '교사 요청에 의한 종료' }),
  });
  toast(`${agent.machineName}: 종료 명령을 보냈습니다.`, 'success');
}

async function jobCommand(job, action) {
  const label = action === 'pause' ? '일시중지' : action === 'resume' ? '재개' : '취소';
  if (!confirm(`${job.agentName || job.jobId} 작업을 ${label}할까요?`)) return;
  await api(`/api/admin/jobs/${encodeURIComponent(job.jobId)}/${action}`, { method: 'POST' });
  toast(`${label} 명령을 보냈습니다.`, 'success');
  await refresh();
}

async function submitStart() {
  const policy = {
    includeSecurity: $('#p-security').checked,
    includeCritical: $('#p-critical').checked,
    includeImportant: $('#p-important').checked,
    includeOptional: $('#p-optional').checked,
    includeDrivers: $('#p-drivers').checked,
    includeDefinitionUpdates: $('#p-definitions').checked,
    includeFeatureUpdates: false,
    maxRebootCount: Number($('#p-max-reboots').value) || 5,
    maxRuntimeHours: 12,
    pauseOnUserLogon: $('#p-pause-user').checked,
    rebootPolicy: $('#p-reboot-never').checked ? 'never' : 'auto',
    rebootDelaySeconds: 60,
    shutdownAfterComplete: false,
    overlayEnabled: $('#p-overlay').checked,
    requireMaestroManualConfirmation: true,
    verifyRestoreMarkerAfterReboot: true,
  };

  const agentIds = [...selected];
  if (agentIds.length === 0) {
    toast('대상 PC를 선택하세요.', 'error');
    return;
  }

  if (!$('#p-maestro-confirm').checked) {
    toast('Maestro 복원 보호 해제 확인이 필요합니다.', 'error');
    return;
  }

  const note = $('#p-note').value.trim();
  const button = $('#start-submit');
  button.disabled = true;
  try {
    const result = await api('/api/admin/jobs/start', {
      method: 'POST',
      body: JSON.stringify({
        agentIds,
        policy,
        maestroConfirmed: true,
        confirmedBy: 'teacher',
        note,
      }),
    });

    if (result.errors && result.errors.length > 0) {
      toast(result.errors.join(' / '), 'error');
    }
    if (result.jobIds && result.jobIds.length > 0) {
      toast(`${result.jobIds.length}대에 작업을 배정했습니다.`, 'success');
      $('#start-modal').classList.add('hidden');
      selected.clear();
      await refresh();
    }
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    button.disabled = false;
  }
}

function openStartDialog(agentIds) {
  if (agentIds && agentIds.length > 0) {
    for (const id of agentIds) selected.add(id);
    renderAgents();
  }
  if (selected.size === 0) {
    toast('먼저 대상 PC를 선택하세요.', 'error');
    return;
  }

  const anyMaestro = agents.some(
    (a) => selected.has(a.agent.agentId) && a.agent.maestroInstalled
  );
  $('#maestro-block').classList.toggle('hidden', !anyMaestro);
  if (anyMaestro) $('#p-maestro-confirm').checked = false;
  else $('#p-maestro-confirm').checked = true;

  $('#start-target-count').textContent = `${selected.size}대`;
  $('#start-modal').classList.remove('hidden');
}

function renderJobDetail(data) {
  const job = data.summary;
  const [label, tone] = jobStateInfo(job.state);
  $('#detail-title').textContent = `${data.machineName || job.agentName || ''} · ${job.jobId}`;

  const summary = $('#detail-summary');
  summary.innerHTML = `
    <div class="detail-row"><span>상태</span><span>${chip(label, tone)} ${escapeHtml(job.phase || '')}</span></div>
    <div class="detail-row"><span>진행</span><span>${job.progressPercent || 0}%</span></div>
    <div class="progress"><div style="width:${Math.max(0, Math.min(100, job.progressPercent || 0))}%"></div></div>
    <div class="detail-row"><span>항목</span><span>전체 ${job.totalItems || 0} · 설치 ${job.installedItems || 0} · 실패 ${job.failedItems || 0}</span></div>
    <div class="detail-row"><span>재부팅</span><span>${job.rebootCount || 0}/${job.maxRebootCount || 0}회</span></div>
    <div class="detail-row"><span>메시지</span><span>${escapeHtml(job.message || '')}</span></div>
    <div class="detail-row"><span>시작</span><span>${escapeHtml(formatTime(job.startedAt))}</span></div>
    <div class="detail-row"><span>갱신</span><span>${escapeHtml(formatTime(job.updatedAt))}</span></div>
    ${job.lastError ? `<div class="alert-inline">${escapeHtml(job.lastError)}</div>` : ''}
  `;

  const items = $('#detail-items');
  items.innerHTML = '';
  const detailItems = (data.detail && data.detail.items) || [];
  if (detailItems.length === 0) {
    items.innerHTML = '<div class="empty">항목 정보가 아직 없습니다.</div>';
  } else {
    for (const item of detailItems) {
      const row = el('div', 'item-row');
      const itemState = String(item.state || '').toLowerCase();
      row.innerHTML = `<div class="item-kb mono">${escapeHtml(item.kbArticleIds || 'KB-')}</div>
        <div class="item-title">${escapeHtml(item.title || '')}</div>
        <div class="item-state state-${escapeHtml(itemState)}">${escapeHtml(itemState)}</div>
        ${item.error ? `<div class="item-error">${escapeHtml(item.error)}</div>` : ''}`;
      items.appendChild(row);
    }
  }

  const checks = $('#detail-checks');
  checks.innerHTML = '';
  const preflight = (data.detail && data.detail.preflightChecks) || [];
  if (preflight.length === 0) {
    checks.innerHTML = '<div class="empty">사전 점검 정보가 없습니다.</div>';
  } else {
    for (const check of preflight) {
      const status = String(check.status || '').toLowerCase();
      const row = el('div', `check-row check-${status}`);
      row.innerHTML = `<div class="check-name">${escapeHtml(check.name)}</div>
        <div class="check-msg">${escapeHtml(check.message)}</div>`;
      checks.appendChild(row);
    }
  }

  renderMaestro(data);
}

function renderMaestro(data) {
  const host = $('#detail-maestro');
  const checklist = data.maestro;
  if (!checklist) {
    host.innerHTML = '<div class="empty">Maestro 확인 정보가 없습니다.</div>';
    return;
  }

  const verified = checklist.markerStatus === 'markerVerified';
  const failed = checklist.markerStatus === 'verificationFailed';

  host.innerHTML = `
    <div class="detail-row"><span>보호 해제 확인</span><span>${checklist.protectionSuspendedConfirmed ? '완료' : '대기'}</span></div>
    <div class="detail-row"><span>현재 상태 저장</span><span>${checklist.stateSavedConfirmed ? '완료' : '대기'}</span></div>
    <div class="detail-row"><span>보호 재활성화</span><span>${checklist.protectionRestoredConfirmed ? '완료' : '대기'}</span></div>
    <div class="detail-row"><span>복원 마커</span><span>${verified ? '검증됨' : failed ? '검증 실패' : '미검증'}</span></div>
    ${checklist.markerMessage ? `<div class="muted small">${escapeHtml(checklist.markerMessage)}</div>` : ''}
  `;

  if (checklist.markerStatus === 'verificationFailed') {
    host.innerHTML += `<div class="alert-inline">재부팅 후 마커가 사라졌습니다. Maestro 복원이 아직 켜져 있을 가능성이 높습니다. 먼저 보호를 해제한 뒤 다시 작업하세요.</div>`;
  }

  const actions = el('div', 'row gap wrap');
  if (!checklist.protectionSuspendedConfirmed) {
    actions.appendChild(actionButton('보호 해제 확인', async () => {
      await api(`/api/admin/jobs/${encodeURIComponent(data.summary.jobId)}/maestro/protection-suspended`, { method: 'POST' });
      toast('보호 해제 확인을 기록했습니다.', 'success');
      await refreshJobDetail(data.summary.jobId);
    }));
  }

  const job = data.summary;
  if (job.isTerminal) {
    actions.appendChild(actionButton('상태 저장 + 보호 재활성화 확인', async () => {
      await api(`/api/admin/jobs/${encodeURIComponent(job.jobId)}/maestro/restored`, {
        method: 'POST',
        body: JSON.stringify({ stateSaved: true, protectionRestored: true }),
      });
      toast('Maestro 복원 보호 재설정을 기록했습니다.', 'success');
      await refreshJobDetail(job.jobId);
    }));
  }

  host.appendChild(actions);

  if (data.beforeStartMessages && data.beforeStartMessages.length > 0) {
    const list = el('ul', 'guide-list');
    for (const message of data.beforeStartMessages) list.appendChild(el('li', '', message));
    host.appendChild(el('div', 'section-label', '작업 전 확인'));
    host.appendChild(list);
  }

  if (data.afterCompleteMessages && data.afterCompleteMessages.length > 0 && job.isTerminal) {
    const list = el('ul', 'guide-list');
    for (const message of data.afterCompleteMessages) list.appendChild(el('li', '', message));
    host.appendChild(el('div', 'section-label', '작업 후 확인'));
    host.appendChild(list);
  }
}

// ---------------- GitHub 업데이트 확인 ----------------

const UPDATE_CHECK_INTERVAL_MS = 30 * 60 * 1000;
const UPDATE_DISMISS_KEY = 'ua_dismissed_update_version';

let updateInfo = null;
let lastUpdateCheck = 0;
let updateCheckInFlight = false;

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatDate(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString('ko-KR', { year: 'numeric', month: '2-digit', day: '2-digit' });
}

function applyUpdateInfo(info) {
  if (!info) return;

  $('#version-text').textContent = `v${info.currentVersion || '?'}`;
  $('#version-button').title = info.checkSucceeded
    ? `현재 v${info.currentVersion} · 최신 v${info.latestVersion}`
    : `현재 v${info.currentVersion} · 업데이트 확인 실패`;

  $('#version-dot').classList.toggle('hidden', !info.updateAvailable);

  if (!info.updateAvailable) {
    $('#update-modal').classList.add('hidden');
    return;
  }

  $('#update-current').textContent = `v${info.currentVersion}`;
  $('#update-latest').textContent = `v${info.latestVersion}`;

  const meta = [];
  if (info.releaseName) meta.push(info.releaseName);
  if (info.publishedAt) meta.push(`${formatDate(info.publishedAt)} 배포`);
  if (info.assetName) {
    const size = formatBytes(info.assetSizeBytes);
    meta.push(size ? `${info.assetName} · ${size}` : info.assetName);
  }
  $('#update-meta').textContent = meta.join(' · ');

  $('#update-github-link').href = info.repositoryUrl || 'https://github.com/ahbiyout-all/WinAutoUpdate';
  $('#update-release-link').href = info.releaseUrl || 'https://github.com/ahbiyout-all/WinAutoUpdate/releases';

  const download = $('#update-download');
  download.href = info.assetUrl || info.releaseUrl || 'https://github.com/ahbiyout-all/WinAutoUpdate/releases';
  download.textContent = info.assetUrl ? '업데이트 내려받기' : '릴리스 열기';

  $('#update-notes').textContent = (info.releaseNotes || '').trim();

  if (sessionStorage.getItem(UPDATE_DISMISS_KEY) !== info.latestVersion) {
    $('#update-modal').classList.remove('hidden');
  }
}

async function maybeCheckForUpdates(force) {
  if (!adminToken || updateCheckInFlight) return;

  const now = Date.now();
  if (!force && now - lastUpdateCheck < UPDATE_CHECK_INTERVAL_MS) return;

  updateCheckInFlight = true;
  lastUpdateCheck = now;
  try {
    const info = await api(`/api/admin/update${force ? '?force=true' : ''}`);
    updateInfo = info;
    applyUpdateInfo(info);
  } catch {
    // 토큰/네트워크 오류는 refresh()에서 이미 표시한다.
  } finally {
    updateCheckInFlight = false;
  }
}

function dismissUpdate() {
  if (updateInfo?.latestVersion) {
    sessionStorage.setItem(UPDATE_DISMISS_KEY, updateInfo.latestVersion);
  }
  $('#update-modal').classList.add('hidden');
}

function setupUpdateDialog() {
  $('#update-close').addEventListener('click', dismissUpdate);
  $('#update-later').addEventListener('click', dismissUpdate);
  $('#update-download').addEventListener('click', () => {
    // 다운로드를 시작한 뒤 팝업은 닫아 둔다.
    setTimeout(dismissUpdate, 300);
  });
  $('#update-modal').addEventListener('click', (event) => {
    if (event.target === $('#update-modal')) dismissUpdate();
  });

  $('#version-button').addEventListener('click', async () => {
    if (updateInfo?.updateAvailable) {
      $('#update-modal').classList.remove('hidden');
      return;
    }

    await maybeCheckForUpdates(true);

    if (updateInfo?.updateAvailable) {
      $('#update-modal').classList.remove('hidden');
    } else if (updateInfo && !updateInfo.checkSucceeded) {
      toast(`업데이트 확인 실패: ${updateInfo.error || 'GitHub에 연결할 수 없습니다.'}`, 'error');
    } else {
      toast(`최신 버전을 사용 중입니다 (v${updateInfo?.currentVersion || '?'})`, 'success');
    }
  });
}

// ---------------- 초기화 ----------------

function setupTokenDialog() {
  const dialog = $('#token-modal');
  const input = $('#token-input');
  const newInput = $('#new-token-input');
  const confirmInput = $('#new-token-confirm');
  const status = $('#token-status');

  $('#sample-token-text').textContent = SAMPLE_ADMIN_TOKEN;

  // 인증된 경우에만 서버 토큰 상태를 조회한다. 실제 토큰 값은 응답에 포함하지 않는다.
  const loadStatus = async () => {
    if (!adminToken || dialog.classList.contains('hidden')) return;
    try {
      const info = await api('/api/admin/token');
      status.classList.remove('hidden');
      if (info.isPlaceholder) {
        status.className = 'token-status warning';
        status.textContent = '현재 기본(샘플) 토큰 사용 중 - 파일럿 전에 반드시 변경하세요.';
      } else {
        status.className = 'token-status ok';
        status.textContent = info.hasRuntimeOverride
          ? '사용자 지정 토큰 사용 중 (대시보드에서 변경됨).'
          : '사용자 지정 토큰 사용 중.';
      }
    } catch {
      status.classList.add('hidden');
    }
  };

  const save = () => {
    const value = input.value.trim();
    if (!value) {
      toast('토큰을 입력하세요.', 'error');
      return;
    }
    adminToken = value;
    localStorage.setItem(STORAGE_KEY, value);
    dialog.classList.add('hidden');
    setError('');
    refresh();
    consumeDeepLink();
    maybeCheckForUpdates(true);
    loadStatus();
  };

  const changeServerToken = async () => {
    const value = newInput.value.trim();
    if (value.length < 8) {
      toast('새 토큰은 8자 이상이어야 합니다.', 'error');
      return;
    }
    if (value !== confirmInput.value.trim()) {
      toast('새 토큰과 확인 값이 일치하지 않습니다.', 'error');
      return;
    }
    if (!adminToken) {
      toast('먼저 현재 토큰을 저장하세요.', 'error');
      return;
    }
    try {
      await api('/api/admin/token', {
        method: 'POST',
        body: JSON.stringify({ newToken: value }),
      });
      adminToken = value;
      localStorage.setItem(STORAGE_KEY, value);
      input.value = value;
      newInput.value = '';
      confirmInput.value = '';
      toast('서버 관리자 토큰이 변경되었습니다.', 'success');
      await loadStatus();
      refresh();
    } catch (error) {
      toast(`토큰 변경 실패: ${error.message}`, 'error');
    }
  };

  $('#token-use-sample').addEventListener('click', () => {
    input.value = SAMPLE_ADMIN_TOKEN;
    input.focus();
  });
  $('#token-save').addEventListener('click', save);
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') save();
  });
  $('#token-change').addEventListener('click', changeServerToken);
  $('#token-cancel').addEventListener('click', () => dialog.classList.add('hidden'));
  $('#token-button').addEventListener('click', () => {
    input.value = adminToken;
    dialog.classList.remove('hidden');
    loadStatus();
  });

  if (!adminToken) {
    input.value = '';
    dialog.classList.remove('hidden');
    $('#token-cancel').classList.add('hidden');
  }
}

function wireEvents() {
  $('#refresh-button').addEventListener('click', () => refresh());
  $('#select-all').addEventListener('change', (event) => {
    if (event.target.checked) {
      for (const record of agents) selected.add(record.agent.agentId);
    } else {
      selected.clear();
    }
    renderAgents();
  });
  $('#start-button').addEventListener('click', () => openStartDialog([]));
  $('#monitor-button').addEventListener('click', () => window.open('/monitor.html', '_blank'));
  $('#start-close').addEventListener('click', () => $('#start-modal').classList.add('hidden'));
  $('#start-cancel').addEventListener('click', () => $('#start-modal').classList.add('hidden'));
  $('#start-submit').addEventListener('click', submitStart);
  $('#detail-close').addEventListener('click', () => {
    $('#detail-modal').classList.add('hidden');
    openJobId = null;
  });
  $('#detail-modal').addEventListener('click', (event) => {
    if (event.target === $('#detail-modal')) {
      $('#detail-modal').classList.add('hidden');
      openJobId = null;
    }
  });
}

setupTokenDialog();
wireEvents();
setupUpdateDialog();
pendingJobId = new URLSearchParams(location.search).get('job');
refresh();
consumeDeepLink();
setInterval(refresh, 5000);
