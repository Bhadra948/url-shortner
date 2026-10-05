const state = { links: [], selectedCode: null, runs: [], activeRun: null, scenarios: [] };
const byId = (id) => document.getElementById(id);
const escapeHtml = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);

async function request(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) }
  });
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.detail || body?.title || `Request failed (${response.status})`);
  return body;
}

function setConnection(connected) {
  const status = byId("connection-status");
  status.classList.toggle("is-connected", connected);
  status.classList.toggle("is-error", !connected);
  status.innerHTML = `<span class="status-dot"></span>${connected ? "API connected" : "API unavailable"}`;
}

function notify(message) {
  const toast = byId("toast");
  toast.textContent = message;
  toast.classList.add("is-visible");
  window.clearTimeout(notify.timeout);
  notify.timeout = window.setTimeout(() => toast.classList.remove("is-visible"), 2800);
}

function shortUrl(code) { return `${window.location.origin}/${encodeURIComponent(code)}`; }

async function refreshLinks() {
  state.links = await request("/api/links");
  byId("link-count").textContent = state.links.length;
  byId("click-count").textContent = state.links.reduce((sum, link) => sum + link.totalClicks, 0);
  renderLinks();
  if (state.selectedCode && state.links.some((link) => link.code === state.selectedCode)) {
    await loadAnalytics(state.selectedCode);
  }
}

function renderLinks() {
  const tbody = byId("links-table");
  if (!state.links.length) {
    tbody.innerHTML = '<tr><td colspan="4" class="empty-state">No links yet. Create one to get started.</td></tr>';
    return;
  }
  tbody.innerHTML = state.links.map((link) => `<tr>
    <td><a class="link-code" href="${escapeHtml(shortUrl(link.code))}" target="_blank" rel="noreferrer">/${escapeHtml(link.code)}</a></td>
    <td class="target-cell" title="${escapeHtml(link.targetUrl)}">${escapeHtml(link.targetUrl)}</td>
    <td class="click-count">${link.totalClicks}</td>
    <td><div class="row-actions"><button class="mini-button" data-copy="${escapeHtml(link.code)}" type="button">Copy</button><button class="mini-button" data-analytics="${escapeHtml(link.code)}" type="button">Inspect</button></div></td>
  </tr>`).join("");
  tbody.querySelectorAll("[data-copy]").forEach((button) => button.addEventListener("click", async () => {
    await navigator.clipboard.writeText(shortUrl(button.dataset.copy));
    notify("Short URL copied.");
  }));
  tbody.querySelectorAll("[data-analytics]").forEach((button) => button.addEventListener("click", () => loadAnalytics(button.dataset.analytics)));
}

async function loadAnalytics(code) {
  state.selectedCode = code;
  const analytics = await request(`/api/links/${encodeURIComponent(code)}/analytics`);
  const maxClicks = Math.max(1, ...analytics.dailyClicks.map((day) => day.clicks));
  const daily = analytics.dailyClicks;
  const chart = daily.length ? daily.map((day) => `<div class="chart-column" title="${day.clicks} clicks"><div class="chart-bar" style="height:${Math.max(4, Math.round(day.clicks / maxClicks * 77))}px"></div><span class="chart-day">${escapeHtml(day.date.slice(5))}</span></div>`).join("") : '<p class="analytics-link">No clicks in the recent 7-day window.</p>';
  const referrers = Object.entries(analytics.topReferrers).map(([host, count]) => `<li><span>${escapeHtml(host)}</span><strong>${count}</strong></li>`).join("");
  byId("analytics-content").innerHTML = `<p class="analytics-link">${escapeHtml(shortUrl(code))}</p>
    <div class="analytics-totals"><div><strong>${analytics.totalClicks}</strong><span>ALL CLICKS</span></div><div><strong>${analytics.clicksLast24Hours}</strong><span>LAST 24 HOURS</span></div></div>
    <p class="chart-title">DAILY CLICKS / LAST 7 DAYS</p><div class="chart">${chart}</div>
    <p class="chart-title" style="margin-top:20px">TOP REFERRERS</p>${referrers ? `<ul class="referrer-list">${referrers}</ul>` : '<p class="analytics-link">No referrer data recorded.</p>'}`;
}

async function refreshMetrics() {
  const metrics = await request("/api/orchestration/metrics");
  byId("metric-success").textContent = `${Math.round(metrics.successRate * 100)}%`;
  byId("metric-retries").innerHTML = `${metrics.retryCount} <small>/</small> ${metrics.rollbackCount}`;
  byId("metric-frequencies").textContent = `${Math.round(metrics.retryFrequency * 100)}% / ${Math.round(metrics.rollbackFrequency * 100)}% of runs`;
  byId("metric-latency").innerHTML = `${Math.round(metrics.averageEndToEndLatencyMilliseconds)} <small>ms</small>`;
  byId("metric-mttr").innerHTML = `${Math.round(metrics.averageMttrMilliseconds)} <small>ms</small>`;
}

async function refreshRuns() {
  state.runs = await request("/api/orchestration/runs");
  const picker = byId("run-picker");
  picker.innerHTML = state.runs.length
    ? state.runs.map((run) => `<option value="${run.id}">${escapeHtml(run.scenario)} / v${run.planVersion} / ${escapeHtml(run.status)} / ${escapeHtml(run.id.slice(0, 8))}</option>`).join("")
    : '<option value="">No runs yet</option>';
  if (state.activeRun) {
    const active = state.runs.find((run) => run.id === state.activeRun.id);
    if (active) await selectRun(active.id);
  }
}

async function selectRun(runId) {
  if (!runId) return;
  state.activeRun = await request(`/api/orchestration/runs/${runId}`);
  byId("run-picker").value = runId;
  renderRun();
  await loadAudit(runId);
}

function renderRun() {
  const run = state.activeRun;
  byId("run-empty").hidden = true;
  byId("run-detail").hidden = false;
  byId("run-title").textContent = `${run.scenario[0].toUpperCase()}${run.scenario.slice(1)} scenario`;
  byId("run-version").textContent = ` / PLAN V${run.planVersion}`;
  const badge = byId("run-status");
  badge.textContent = run.status;
  badge.dataset.state = run.status;
  byId("run-timing").textContent = run.completedAtUtc ? `Completed ${new Date(run.completedAtUtc).toLocaleTimeString()}` : `Started ${new Date(run.startedAtUtc).toLocaleTimeString()}`;
  byId("replan-requirement").value = run.requirement;

  const stages = run.stages;
  byId("stage-list").innerHTML = stages.map((stage) => {
    const deps = stage.definition.dependsOn.length ? `Depends on ${stage.definition.dependsOn.join(", ")}` : stage.definition.entryGate;
    const meta = [`attempts ${stage.attempts}`, stage.definition.exitGate, stage.usedFallback ? "fallback used" : ""].filter(Boolean).join(" / ");
    const status = stage.status === "AwaitingApproval" ? "Awaiting human approval" : stage.status === "AwaitingClarification" ? "Awaiting clarification" : stage.status;
    return `<article class="stage-card ${stage.definition.id === "release-readiness" || stage.definition.id === "validation" ? "is-wide" : ""}">
      <div class="stage-topline"><h3 class="stage-title">${escapeHtml(stage.definition.title)}</h3><span class="stage-state" data-state="${escapeHtml(stage.status)}">${escapeHtml(status)}</span></div>
      <p class="stage-deps">${escapeHtml(deps)}${stage.definition.requiresHumanApproval ? " / approval required" : ""}</p>
      ${stage.output ? `<p class="stage-output">${escapeHtml(stage.output)}</p>` : ""}
      ${stage.failure ? `<p class="stage-output stage-failure">${escapeHtml(stage.failure)}</p>` : ""}
      <div class="stage-meta">${escapeHtml(meta)}</div>
    </article>`;
  }).join("");

  const action = byId("human-action");
  action.hidden = true;
  const clarification = stages.find((stage) => stage.status === "AwaitingClarification");
  const approval = stages.find((stage) => stage.status === "AwaitingApproval");
  if (clarification) {
    action.hidden = false;
    action.innerHTML = `<h3>Clarification needed</h3><p>${escapeHtml(clarification.failure)}</p><label for="clarification-answer">Product or engineering decision</label><textarea id="clarification-answer" rows="3" maxlength="4000" placeholder="State measurable targets and constraints"></textarea><div class="action-row"><button id="submit-clarification" class="button button-secondary" type="button">Resolve and continue</button></div>`;
    byId("submit-clarification").addEventListener("click", async () => {
      await runAction(`/api/orchestration/runs/${run.id}/clarification`, { answer: byId("clarification-answer").value, actor: "workbench-user" });
    });
  } else if (approval) {
    action.hidden = false;
    action.innerHTML = `<h3>Human approval required</h3><p>Review validation, policy outcomes, and the change record. Approval records a decision; deployment is not performed.</p><label for="approval-decision">Decision rationale</label><input id="approval-decision" maxlength="500" value="Reviewed validation and release readiness."><div class="action-row"><button id="approve-release" class="button button-primary" type="button">Approve readiness</button><button id="reject-release" class="button button-danger" type="button">Reject release</button></div>`;
    const sendApproval = async (approve) => runAction(`/api/orchestration/runs/${run.id}/approval`, { approve, decision: byId("approval-decision").value, actor: "workbench-reviewer" });
    byId("approve-release").addEventListener("click", () => sendApproval(true));
    byId("reject-release").addEventListener("click", () => sendApproval(false));
  }
  byId("safe-stop").disabled = ["Succeeded", "Failed", "SafeStopped"].includes(run.status);
}

async function loadAudit(runId) {
  const response = await request(`/api/orchestration/runs/${runId}/audit`);
  byId("audit-integrity").textContent = response.hashChainValid ? "HASH CHAIN VERIFIED" : "HASH CHAIN INVALID";
  byId("audit-integrity").classList.toggle("is-invalid", !response.hashChainValid);
  byId("audit-list").innerHTML = response.events.slice(-12).reverse().map((event) => `<li><span class="audit-time">${new Date(event.atUtc).toLocaleTimeString()}</span><span class="audit-actor">${escapeHtml(event.actor)}</span><span class="audit-action">${escapeHtml(event.action)}${event.stageId ? ` / ${escapeHtml(event.stageId)}` : ""}</span></li>`).join("");
}

async function runAction(path, body) {
  try {
    state.activeRun = await request(path, { method: "POST", body: JSON.stringify(body) });
    await refreshRuns();
    await refreshMetrics();
    notify("Workflow state updated.");
  } catch (error) { notify(error.message); }
}

async function loadScenarios() {
  state.scenarios = await request("/api/orchestration/scenarios");
  updateScenarioDescription();
}

function updateScenarioDescription() {
  const scenario = state.scenarios.find((item) => item.id === byId("scenario").value);
  if (!scenario) return;
  byId("scenario-purpose").textContent = scenario.purpose;
  byId("requirement").value = scenario.requirement;
}

byId("create-link-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(byId("create-link-form"));
  const body = { targetUrl: form.get("targetUrl"), customAlias: form.get("customAlias") || null };
  const expiry = form.get("expiresAtUtc");
  if (expiry) body.expiresAtUtc = new Date(expiry).toISOString();
  const message = byId("create-message");
  try {
    const created = await request("/api/links", { method: "POST", body: JSON.stringify(body) });
    message.classList.remove("is-error");
    message.textContent = `Created ${created.shortUrl}`;
    byId("create-link-form").reset();
    await refreshLinks();
    await loadAnalytics(created.code);
    notify("Short link created.");
  } catch (error) { message.classList.add("is-error"); message.textContent = error.message; }
});

byId("refresh-links").addEventListener("click", () => refreshLinks().catch((error) => notify(error.message)));
byId("scenario").addEventListener("change", updateScenarioDescription);
byId("run-picker").addEventListener("change", (event) => selectRun(event.target.value).catch((error) => notify(error.message)));
byId("start-run-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const requestBody = {
    scenario: form.get("scenario"),
    requirement: form.get("requirement"),
    injectTransientFailure: form.has("injectTransientFailure"),
    injectDocumentationFailure: form.has("injectDocumentationFailure"),
    injectValidationFailure: form.has("injectValidationFailure")
  };
  try {
    const run = await request("/api/orchestration/runs", { method: "POST", body: JSON.stringify(requestBody) });
    state.activeRun = run;
    await refreshRuns();
    await refreshMetrics();
    notify("Workflow run started.");
  } catch (error) { byId("run-message").textContent = error.message; byId("run-message").classList.add("is-error"); }
});
byId("replan-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const run = state.activeRun;
  if (!run) return;
  await runAction(`/api/orchestration/runs/${run.id}/replan`, {
    requirement: byId("replan-requirement").value,
    rationale: byId("replan-rationale").value,
    actor: "workbench-user"
  });
});
byId("safe-stop").addEventListener("click", async () => {
  if (!state.activeRun) return;
  await runAction(`/api/orchestration/runs/${state.activeRun.id}/safe-stop`, {
    reason: "Operator invoked safe-stop.", actor: "workbench-user"
  });
});
document.querySelectorAll(".view-tab").forEach((tab) => tab.addEventListener("click", () => {
  document.querySelectorAll(".view-tab").forEach((item) => {
    const active = item === tab;
    item.classList.toggle("is-active", active);
    item.setAttribute("aria-selected", String(active));
  });
  document.querySelectorAll(".view-pane").forEach((pane) => {
    const active = pane.id === `${tab.dataset.view}-view`;
    pane.classList.toggle("is-visible", active);
    pane.hidden = !active;
  });
}));

async function initialize() {
  try {
    await request("/health");
    setConnection(true);
    await Promise.all([refreshLinks(), loadScenarios(), refreshRuns(), refreshMetrics()]);
    if (state.runs.length) await selectRun(state.runs[0].id);
  } catch (error) {
    setConnection(false);
    notify(error.message);
  }
}

initialize();