const STORAGE_KEY = 'ua_admin_token';
let adminToken = localStorage.getItem(STORAGE_KEY) || '';
let agents = [];
let jobs = [];
let audit = [];
let overview = {};
let selected = new Set();
let openJobId = null;
let lastError = '';
let pendingJobId = null;
let osFilter = 'all';
let agentPolicies = {};
let startCapability = null;
let agentPolicyCapability = null;
let agentPolicyTargetId = null;
let savedPolicySelection = { withOverride: 0, total: 0 };

const OS_GROUPS = [
  { id: 'windows-11', label: 'Windows 11', tone: 'blue' },
  { id: 'windows-10', label: 'Windows 10 ESU', tone: 'amber' },
  { id: 'ltsc', label: 'LTSC / IoT LTSC', tone: 'purple' },
  { id: 'server', label: 'Windows Server', tone: 'green' },
  { id: 'server-core', label: 'Server Core', tone: 'gray' },
  { id: 'unknown', label: '미확인 OS', tone: 'gray' },
];

const POLICY_PRESETS = {
  custom: {
    label: '사용자 설정',
    hint: '체크박스를 직접 지정했습니다.',
    values: null,
  },
  standard: {
    label: '표준',
    hint: '보안 · 중요 · 권장 업데이트를 설치합니다. 드라이버는 제외되고 로그온으로 일시중지하지 않습니다.',
    values: {
      security: true, critical: true, important: true, optional: false,
      drivers: false, definitions: false, feature: false, overlay: true,
      pauseUser: false, rebootNever: false, maxReboots: 5,
    },
  },
  'security-only': {
    label: '보안 중심',
    hint: '보안 · 중요 업데이트만 설치하고 재부팅 횟수를 줄입니다. 로그온으로 일시중지하지 않습니다.',
    values: {
      security: true, critical: true, important: false, optional: false,
      drivers: false, definitions: false, feature: false, overlay: true,
      pauseUser: false, rebootNever: false, maxReboots: 3,
    },
  },
  'server-safe': {
    label: '서버 안전',
    hint: '정의 업데이트를 포함하고 자동 재부팅과 오버레이를 끕니다. 로그온으로 일시중지하지 않습니다.',
    values: {
      security: true, critical: true, important: true, optional: false,
      drivers: false, definitions: true, feature: false, overlay: false,
      pauseUser: false, rebootNever: true, maxReboots: 5,
    },
  },
  full: {
    label: '전체',
    hint: '선택 · 드라이버 · 정의 업데이트까지 모두 포함합니다. 로그온으로 일시중지하지 않습니다.',
    values: {
      security: true, critical: true, important: true, optional: true,
      drivers: true, definitions: true, feature: false, overlay: true,
      pauseUser: false, rebootNever: false, maxReboots: 5,
    },
  },
};

const POLICY_IDS = {
  security: 'security',
  critical: 'critical',
  important: 'important',
  optional: 'optional',
  drivers: 'drivers',
  definitions: 'definitions',
  feature: 'feature',
  overlay: 'overlay',
  pauseUser: 'pause-user',
  rebootNever: 'reboot-never',
  maxReboots: 'max-reboots',
};

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

function isActiveJobState(state) {
  return !['completed', 'failed', 'cancelled'].includes(String(state || '').toLowerCase());
}

// ---------------- OS 구분 / 업데이트 옵션 ----------------

function osGroup(os) {
  const productName = String(os?.productName || '');
  const id = os?.groupId || (
    os?.isServerCore ? 'server-core'
      : os?.isServer ? 'server'
        : os?.isLtsc ? 'ltsc'
          : os?.isWindows10 ? 'windows-10'
            : (os?.build || 0) >= 22000 || /Windows 11/i.test(productName) ? 'windows-11'
              : 'unknown'
  );
  return OS_GROUPS.find((group) => group.id === id) || OS_GROUPS[OS_GROUPS.length - 1];
}

function visibleAgents() {
  return osFilter === 'all'
    ? agents
    : agents.filter((record) => osGroup(record.agent.os).id === osFilter);
}

function renderOsFilter() {
  const host = $('#os-filter');
  if (!host) return;
  const counts = new Map();
  for (const record of agents) {
    const id = osGroup(record.agent.os).id;
    counts.set(id, (counts.get(id) || 0) + 1);
  }
  const entries = [{ id: 'all', label: `전체 ${agents.length}`, tone: 'blue' }]
    .concat(OS_GROUPS.filter((group) => counts.has(group.id) || group.id === osFilter)
      .map((group) => ({ ...group, label: `${group.label} ${counts.get(group.id) || 0}` })));
  host.innerHTML = entries.map((entry) =>
    `<button type="button" class="filter-chip${osFilter === entry.id ? ' active' : ''}" data-os-filter="${entry.id}">${escapeHtml(entry.label)}</button>`
  ).join('');
  host.querySelectorAll('button').forEach((button) => {
    button.addEventListener('click', () => {
      osFilter = button.dataset.osFilter;
      renderOsFilter();
      renderAgents();
    });
  });
}

function capabilitiesFor(records) {
  const groups = new Map();
  let supportsOverlayAny = false;
  let supportsOverlayAll = true;
  let supportsFeatureAny = false;
  let supportsFeatureAll = true;
  for (const record of records) {
    const os = record.agent.os || {};
    const group = osGroup(os);
    groups.set(group.id, (groups.get(group.id) || 0) + 1);
    const overlay = os.supportsOverlay ?? !os.isServerCore;
    if (overlay) supportsOverlayAny = true;
    else supportsOverlayAll = false;
    const feature = os.supportsFeatureUpdates ?? (!os.isServer && !os.isLtsc && !os.isWindows10);
    if (feature) supportsFeatureAny = true;
    else supportsFeatureAll = false;
  }
  if (records.length === 0) {
    supportsOverlayAny = false;
    supportsFeatureAny = false;
    supportsFeatureAll = false;
  }
  // supportsOverlay: 적어도 한 대라면 UI에서 선택 가능(서버가 OS별로 자동 조정).
  return {
    count: records.length, groups,
    supportsOverlay: supportsOverlayAny, supportsOverlayAll,
    supportsFeature: supportsFeatureAll, supportsFeatureAny,
  };
}

function renderOsMix(selector, capability) {
  const host = $(selector);
  if (!host) return;
  const parts = [...capability.groups.entries()].map(([id, count]) => {
    const group = OS_GROUPS.find((item) => item.id === id) || OS_GROUPS[OS_GROUPS.length - 1];
    return chip(`${group.label} ${count}대`, group.tone);
  });
  if (!capability.supportsOverlayAll) {
    parts.push('<div class="muted small">Server Core: 전체화면 오버레이가 OS별로 자동 비활성화됩니다.</div>');
  }
  if (!capability.supportsFeature) {
    const text = capability.supportsFeatureAny
      ? '선택된 OS 중 일부는 기능 업데이트를 지원하지 않습니다.'
      : '선택된 OS는 기능 업데이트를 지원하지 않습니다.';
    parts.push(`<div class="muted small">${text}</div>`);
  }
  host.innerHTML = parts.join(' ');
}

const fieldSel = (prefix, key) => `#${prefix}${POLICY_IDS[key]}`;
const capabilityForPrefix = (prefix) => prefix === 'p-' ? startCapability : agentPolicyCapability;

function readPolicyForm(prefix) {
  const checked = (key) => !!$(fieldSel(prefix, key))?.checked;
  return {
    includeSecurity: checked('security'),
    includeCritical: checked('critical'),
    includeImportant: checked('important'),
    includeOptional: checked('optional'),
    includeDrivers: checked('drivers'),
    includeDefinitionUpdates: checked('definitions'),
    // 기능 업데이트는 2차 승인 워크플로에서만 사용한다.
    includeFeatureUpdates: false,
    maxRebootCount: Number($(fieldSel(prefix, 'maxReboots'))?.value) || 5,
    maxRuntimeHours: 12,
    pauseOnUserLogon: checked('pauseUser'),
    rebootPolicy: checked('rebootNever') ? 'never' : 'auto',
    rebootDelaySeconds: 60,
    shutdownAfterComplete: false,
    overlayEnabled: checked('overlay'),
    requireMaestroManualConfirmation: true,
    verifyRestoreMarkerAfterReboot: true,
  };
}

function writePolicyForm(prefix, policy) {
  const set = (key, value) => {
    const input = $(fieldSel(prefix, key));
    if (input && input.type === 'checkbox') input.checked = !!value;
  };
  set('security', policy.includeSecurity);
  set('critical', policy.includeCritical);
  set('important', policy.includeImportant);
  set('optional', policy.includeOptional);
  set('drivers', policy.includeDrivers);
  set('definitions', policy.includeDefinitionUpdates);
  set('feature', policy.includeFeatureUpdates);
  set('overlay', policy.overlayEnabled);
  set('pauseUser', policy.pauseOnUserLogon);
  set('rebootNever', policy.rebootPolicy === 'never');
  const maxReboots = $(fieldSel(prefix, 'maxReboots'));
  if (maxReboots) maxReboots.value = policy.maxRebootCount ?? 5;
}

function applyPolicyCapabilities(prefix, capability) {
  capability = capability || {
    supportsOverlay: true, supportsOverlayAll: true,
    supportsFeature: true, supportsFeatureAny: true,
  };
  const overlay = $(fieldSel(prefix, 'overlay'));
  if (overlay) {
    overlay.disabled = !capability.supportsOverlay;
    if (!capability.supportsOverlay) overlay.checked = false;
  }
  const feature = $(fieldSel(prefix, 'feature'));
  if (feature) {
    feature.disabled = true;
    feature.checked = false;
    feature.parentElement.title = capability.supportsFeature
      ? '기능 업데이트는 2차 승인 워크플로에서만 사용합니다.'
      : (capability.supportsFeatureAny
        ? '선택된 OS 중 일부는 기능 업데이트를 지원하지 않습니다.'
        : '선택된 OS는 기능 업데이트를 지원하지 않습니다.');
  }
}

function updatePresetHint(prefix, id, capability) {
  const hint = $(`#${prefix}preset-hint`);
  if (!hint) return;
  const preset = POLICY_PRESETS[id] || POLICY_PRESETS.custom;
  let text = preset.hint;
  if (capability && (!capability.supportsOverlayAll || !capability.supportsFeature)) {
    text = `${preset.hint} · 선택된 OS에서 지원되지 않는 옵션은 OS별로 자동 조정됩니다.`;
  }
  hint.textContent = text;
}

function markCustom(prefix) {
  const select = $(`#${prefix}preset`);
  if (select) select.value = 'custom';
  updatePresetHint(prefix, 'custom', capabilityForPrefix(prefix));
}

function setPolicyPreset(prefix, id, capability) {
  const preset = POLICY_PRESETS[id];
  if (!preset?.values) return;
  const values = preset.values;
  writePolicyForm(prefix, {
    includeSecurity: values.security,
    includeCritical: values.critical,
    includeImportant: values.important,
    includeOptional: values.optional,
    includeDrivers: values.drivers,
    includeDefinitionUpdates: values.definitions,
    includeFeatureUpdates: values.feature,
    overlayEnabled: values.overlay,
    pauseOnUserLogon: values.pauseUser,
    rebootPolicy: values.rebootNever ? 'never' : 'auto',
    maxRebootCount: values.maxReboots,
  });
  applyPolicyCapabilities(prefix, capability);
  const select = $(`#${prefix}preset`);
  if (select) select.value = id;
  updatePresetHint(prefix, id, capability);
}

function wirePolicyForm(prefix) {
  const select = $(`#${prefix}preset`);
  select?.addEventListener('change', () => {
    const id = select.value;
    if (id === 'custom') updatePresetHint(prefix, id, capabilityForPrefix(prefix));
    else setPolicyPreset(prefix, id, capabilityForPrefix(prefix));
  });
  for (const key of Object.keys(POLICY_IDS)) {
    $(fieldSel(prefix, key))?.addEventListener('change', () => markCustom(prefix));
  }
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
  if (Number.isNaN(date.getTime())) return '';
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

  const rows = visibleAgents();
  if (rows.length === 0) {
    tbody.innerHTML = `<tr><td colspan="8" class="empty">선택한 OS 필터에 해당하는 PC가 없습니다.</td></tr>`;
    $('#select-all').checked = false;
    $('#selected-count').textContent = selected.size > 0 ? `${selected.size}대 선택됨` : '';
    return;
  }

  for (const record of rows) {
    const agent = record.agent;
    const telemetry = record.telemetry || {};
    const online = record.online;
    const state = online ? telemetry.state || 'idle' : 'offline';
    const [label, tone] = stateInfo(state);
    const os = agent.os || {};
    // 종료된 작업은 "현재 작업"이 아니다. 최근 작업 목록에서만 표시한다.
    const job = isActiveJobState(telemetry.currentJob?.state) ? telemetry.currentJob : null;

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
      <div class="muted mono">${escapeHtml(agent.ipAddress || 'IP 주소 없음')}</div>`;
    tr.appendChild(nameCell);

    const stateCell = el('td');
    stateCell.innerHTML = chip(label, tone) +
      (telemetry.userLoggedOn ? ` ${chip('사용자 로그온', 'amber')}` : '') +
      (telemetry.wuBusyProbeFailed
        ? ` ${chip('WU 확인 실패', 'red')}`
        : telemetry.wuBusy ? ` ${chip('WU 사용 중', 'red')}` : '');
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
    const group = osGroup(os);
    const policyInfo = agentPolicies[agent.agentId];
    osCell.innerHTML = `<div class="small">${chip(group.label, group.tone)}</div>
      <div class="small">${escapeHtml(os.shortName || os.productName || '알 수 없음')}</div>
      <div class="muted small">${escapeHtml(os.versionString || '')}</div>
      ${policyInfo?.hasOverride ? chip('개별 옵션', 'purple') + ' ' : ''}
      ${agent.maestroInstalled ? '<span class="small amber-text">Maestro 감지됨</span>' : ''}
      ${telemetry.maestro?.detectionFailed ? ` ${chip('Maestro 확인 실패', 'red')}` : ''}`;
    tr.appendChild(osCell);

    const freeCell = el('td');
    // 0MB는 실제 값(디스크 부족)이므로 물음표로 바꾸지 않는다.
    const freeMb = Number(telemetry.freeDiskMb);
    const freeText = Number.isFinite(freeMb) && freeMb >= 0
      ? `${(freeMb / 1024).toFixed(1)} GB`
      : '확인 불가';
    const laptopText = agent.isLaptop
      ? '노트북'
      : agent.laptopDetectionFailed ? '노트북 여부 확인 불가' : '데스크톱';
    freeCell.innerHTML = `<div class="small">${escapeHtml(freeText)}</div>
      <div class="muted small">${escapeHtml(laptopText)}</div>`;
    tr.appendChild(freeCell);

    const seenCell = el('td');
    seenCell.innerHTML = `<div class="small">${online ? '온라인' : '오프라인'}</div>
      <div class="muted small">${escapeHtml(ago(record.lastSeen))}</div>`;
    tr.appendChild(seenCell);

    const actionCell = el('td');
    actionCell.className = 'actions';
    actionCell.appendChild(actionButton('WoL', () => wakeAgent(agent), !online));
    actionCell.appendChild(actionButton('옵션', () => openAgentPolicyDialog(agent)));
    actionCell.appendChild(actionButton('업데이트', () => openStartDialog([agent.agentId])));
    actionCell.appendChild(actionButton('해제', () => unlockAgent(agent)));
    actionCell.appendChild(actionButton('재부팅', () => rebootAgent(agent)));
    actionCell.appendChild(actionButton('종료', () => shutdownAgent(agent)));
    tr.appendChild(actionCell);

    tbody.appendChild(tr);
  }

  const selectAll = $('#select-all');
  selectAll.checked = rows.length > 0 && rows.every((a) => selected.has(a.agent.agentId));
  $('#selected-count').textContent = selected.size > 0 ? `${selected.size}대 선택됨` : '';
}

function actionButton(label, handler, disabled = false, style = '') {
  const button = el('button', `btn btn-mini${style ? ` btn-${style}` : ''}`, label);
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

  for (const record of jobs) {
    const job = record.summary;
    const [label, tone] = jobStateInfo(job.state);
    const tr = el('tr');

    const pcCell = el('td');
    pcCell.innerHTML = `<div class="strong">${escapeHtml(record.machineName || job.agentName || '')}</div>
      <div class="muted small mono">${escapeHtml(job.jobId)}</div>`;
    tr.appendChild(pcCell);

    const stateCell = el('td');
    stateCell.innerHTML = `${chip(label, tone)}
      ${job.isDryRun ? ` ${chip('DRY RUN', 'purple')}` : ''}
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
      actionCell.appendChild(actionButton('강제 중단', () => jobCommand(job, 'force-stop'), false, 'danger'));
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

  for (const entry of audit) {
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
    const [agentsData, policiesData, jobsData, overviewData, auditData] = await Promise.all([
      api('/api/admin/agents'),
      api('/api/admin/agents/policies'),
      api('/api/admin/jobs?limit=60'),
      api('/api/admin/overview'),
      api('/api/admin/audit?limit=60'),
    ]);
    agents = agentsData || [];
    agentPolicies = Object.fromEntries((policiesData || []).map((item) => [item.agentId, item]));
    jobs = jobsData || [];
    overview = overviewData || {};
    audit = auditData || [];
    setError('');
    renderOverview();
    renderOsFilter();
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
  const target = job.agentName || job.jobId;
  if (action === 'force-stop') {
    const ok = confirm(
      `[강제 중단] ${target} 작업을 지금 중단할까요?\n\n` +
      '· 예약된 재부팅이 취소되고 남은 업데이트는 설치되지 않습니다.\n' +
      '· 학생 PC의 입력 잠금 오버레이가 즉시 해제됩니다.\n' +
      '· 이미 설치를 시작한 항목은 안전한 지점까지 완료될 수 있습니다.\n' +
      '  작업 상태는 체크인에서 취소됨으로 표시됩니다.'
    );
    if (!ok) return;
  } else {
    const label = action === 'pause' ? '일시중지' : action === 'resume' ? '재개' : '취소';
    if (!confirm(`${target} 작업을 ${label}할까요?`)) return;
  }
  await api(`/api/admin/jobs/${encodeURIComponent(job.jobId)}/${action}`, { method: 'POST' });
  toast(action === 'force-stop' ? '강제 중단 명령을 보냈습니다.' : `${jobActionLabel(action)} 명령을 보냈습니다.`, 'success');
  await refresh();
}

function jobActionLabel(action) {
  return action === 'pause' ? '일시중지' : action === 'resume' ? '재개' : '취소';
}

async function submitStart() {
  // 저장된 PC별 옵션을 쓰면 정책을 보내지 않는다(null). 서버가 개별 > 기본 순으로 해석한다.
  const useSaved = !!$('#p-use-saved-policy')?.checked;
  const policy = useSaved ? null : readPolicyForm('p-');

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
        saveAsAgentPolicy: !useSaved && $('#p-save-policy').checked,
        maestroConfirmed: true,
        confirmedBy: 'teacher',
        note,
      }),
    });

    if (result.warnings && result.warnings.length > 0) {
      toast(result.warnings.join(' / '), 'error');
    }
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

function updateSavedPolicyUi() {
  const useSaved = $('#p-use-saved-policy');
  const save = $('#p-save-policy');
  const hint = $('#p-saved-policy-hint');
  if (save) save.disabled = !!useSaved?.checked;
  if (!hint) return;

  const { withOverride, total } = savedPolicySelection;
  if (useSaved?.checked) {
    hint.textContent = withOverride > 0
      ? `저장된 옵션 사용: 선택한 ${total}대 중 ${withOverride}대에 저장된 값을 적용합니다.`
      : `저장된 옵션 사용: 저장된 값이 없어 선택한 ${total}대에 서버 기본 정책을 적용합니다.`;
  } else if (total > 0) {
    hint.textContent = withOverride > 0
      ? `선택한 ${total}대 중 ${withOverride}대에 저장된 옵션이 있습니다.`
      : '선택한 PC에 저장된 개별 옵션이 없습니다.';
  } else {
    hint.textContent = '';
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

  const records = agents.filter((a) => selected.has(a.agent.agentId));
  // 감지 실패도 Maestro가 있을 수 있으므로 확인 절차를 건너뛰지 않는다.
  const anyMaestro = records.some((a) =>
    a.agent.maestroInstalled || a.agent.maestroDetectionFailed ||
    a.telemetry?.maestro?.detectionFailed === true);
  $('#maestro-block').classList.toggle('hidden', !anyMaestro);
  if (anyMaestro) $('#p-maestro-confirm').checked = false;
  else $('#p-maestro-confirm').checked = true;

  startCapability = capabilitiesFor(records);
  renderOsMix('#start-os-mix', startCapability);
  applyPolicyCapabilities('p-', startCapability);
  const allServer = records.length > 0 && [...startCapability.groups.keys()]
    .every((id) => id === 'server' || id === 'server-core');
  setPolicyPreset('p-', allServer ? 'server-safe' : 'standard', startCapability);
  savedPolicySelection = {
    withOverride: records.filter((r) => !!agentPolicies[r.agent.agentId]?.hasOverride).length,
    total: records.length,
  };
  $('#p-use-saved-policy').checked = false;
  $('#p-save-policy').checked = false;
  updateSavedPolicyUi();

  $('#start-target-count').textContent = `${selected.size}대`;
  $('#start-modal').classList.remove('hidden');
}

async function openAgentPolicyDialog(agent) {
  try {
    const data = await api(`/api/admin/agents/${encodeURIComponent(agent.agentId)}/policy`);
    agentPolicyTargetId = agent.agentId;
    $('#ap-agent-name').textContent = agent.machineName || agent.agentId;
    $('#ap-source').textContent = data.source || '기본 정책';
    agentPolicyCapability = capabilitiesFor([agent]);
    renderOsMix('#ap-os-mix', agentPolicyCapability);
    // 먼저 정책 값을 쓰고, 그 다음 OS 제한으로 비활성화·해제한다(역순이면 꺼진 옵션이 다시 켜질 수 있음).
    writePolicyForm('ap-', data.policy || {});
    applyPolicyCapabilities('ap-', agentPolicyCapability);
    $('#ap-preset').value = 'custom';
    updatePresetHint('ap-', 'custom', agentPolicyCapability);
    $('#agent-policy-modal').classList.remove('hidden');
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function saveAgentPolicy() {
  if (!agentPolicyTargetId) return;
  const button = $('#ap-save');
  button.disabled = true;
  try {
    await api(`/api/admin/agents/${encodeURIComponent(agentPolicyTargetId)}/policy`, {
      method: 'PUT',
      body: JSON.stringify({ policy: readPolicyForm('ap-') }),
    });
    toast('이 PC의 업데이트 옵션을 저장했습니다.', 'success');
    $('#agent-policy-modal').classList.add('hidden');
    await refresh();
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    button.disabled = false;
  }
}

async function resetAgentPolicy() {
  if (!agentPolicyTargetId) return;
  if (!confirm('이 PC의 개별 옵션을 삭제하고 서버 기본 정책으로 되돌릴까요?')) return;
  try {
    await api(`/api/admin/agents/${encodeURIComponent(agentPolicyTargetId)}/policy`, {
      method: 'DELETE',
    });
    toast('기본 정책으로 되돌렸습니다.', 'success');
    $('#agent-policy-modal').classList.add('hidden');
    await refresh();
  } catch (error) {
    toast(error.message, 'error');
  }
}

function renderJobDetail(data) {
  const job = data.summary;
  const [label, tone] = jobStateInfo(job.state);
  $('#detail-title').textContent = `${data.machineName || job.agentName || ''} · ${job.jobId}`;

  const summary = $('#detail-summary');
  summary.innerHTML = `
    <div class="detail-row"><span>상태</span><span>${chip(label, tone)} ${job.isDryRun ? ` ${chip('DRY RUN', 'purple')}` : ''} ${escapeHtml(job.phase || '')}</span></div>
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
      row.innerHTML = `<div class="item-kb mono">${escapeHtml(item.kbArticleIds || 'KB 번호 없음')}</div>
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

const REPOSITORY_URL = 'https://github.com/ahbiyout-all/WinAutoUpdate';
const RELEASES_URL = 'https://github.com/ahbiyout-all/WinAutoUpdate/releases';
const SAFE_EXTERNAL_HOSTS = new Set([
  'github.com',
  'www.github.com',
  'api.github.com',
  'objects.githubusercontent.com',
  'githubusercontent.com',
  'raw.githubusercontent.com',
]);

// 서버가 정규화해도 브라우저에서 한 번 더 검사한다. http/https + GitHub 호스트만 허용한다.
function safeExternalUrl(value, fallback) {
  try {
    const parsed = new URL(value || fallback, window.location.href);
    if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return fallback;
    if (!SAFE_EXTERNAL_HOSTS.has(parsed.hostname.toLowerCase())) return fallback;
    if (parsed.protocol === 'http:') return fallback;
    return parsed.href;
  } catch {
    return fallback;
  }
}

function setExternalAnchor(anchor, value, fallback) {
  anchor.href = safeExternalUrl(value, fallback);
  anchor.target = '_blank';
  anchor.rel = 'noopener noreferrer';
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

  setExternalAnchor($('#update-github-link'), info.repositoryUrl, REPOSITORY_URL);
  setExternalAnchor($('#update-release-link'), info.releaseUrl, RELEASES_URL);

  const download = $('#update-download');
  const assetUrl = safeExternalUrl(info.assetUrl, safeExternalUrl(info.releaseUrl, RELEASES_URL));
  setExternalAnchor(download, assetUrl, RELEASES_URL);
  download.textContent = assetUrl === RELEASES_URL ? '릴리스 열기' : '업데이트 내려받기';

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

  // 인증된 경우에만 서버 토큰 상태를 조회한다. 실제 토큰 값은 응답에 포함하지 않는다.
  const loadStatus = async () => {
    if (!adminToken || dialog.classList.contains('hidden')) return;
    try {
      const info = await api('/api/admin/token');
      status.classList.remove('hidden');
      if (info.isPlaceholder) {
        status.className = 'token-status warning';
        status.textContent = '현재 기본(샘플) 토큰 사용 중 - 실제 토큰으로 변경해야 서버가 시작됩니다';
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
      for (const record of visibleAgents()) selected.add(record.agent.agentId);
    } else {
      for (const record of visibleAgents()) selected.delete(record.agent.agentId);
    }
    renderAgents();
  });
  $('#start-button').addEventListener('click', () => openStartDialog([]));
  $('#monitor-button').addEventListener('click', () => window.open('/monitor.html', '_blank'));
  $('#start-close').addEventListener('click', () => $('#start-modal').classList.add('hidden'));
  $('#start-cancel').addEventListener('click', () => $('#start-modal').classList.add('hidden'));
  $('#start-submit').addEventListener('click', submitStart);
  $('#p-use-saved-policy')?.addEventListener('change', updateSavedPolicyUi);
  $('#ap-close').addEventListener('click', () => $('#agent-policy-modal').classList.add('hidden'));
  $('#ap-cancel').addEventListener('click', () => $('#agent-policy-modal').classList.add('hidden'));
  $('#ap-save').addEventListener('click', saveAgentPolicy);
  $('#ap-reset').addEventListener('click', resetAgentPolicy);
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
wirePolicyForm('p-');
wirePolicyForm('ap-');
wireEvents();
setupUpdateDialog();
pendingJobId = new URLSearchParams(location.search).get('job');
refresh();
consumeDeepLink();
setInterval(refresh, 5000);
