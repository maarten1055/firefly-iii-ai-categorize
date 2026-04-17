const mount = document.getElementById('mount');
const diagnosticsSummary = document.getElementById('diagnostics-summary');
const diagnosticsChecks = document.getElementById('diagnostics-checks');
const diagnosticsButton = document.getElementById('run-diagnostics');
const jobStatusSummary = document.getElementById('job-status-summary');
const liveStatus = document.getElementById('live-status');
const jobsById = new Map();
const pendingFillJobIds = new Set();

const socket = new signalR.HubConnectionBuilder()
	.withUrl('/hubs/jobs')
	.withAutomaticReconnect()
	.build();

const setLiveStatus = (text, stateClass) => {
	liveStatus.textContent = text;
	liveStatus.className = `status-pill ${stateClass}`;
};

const startSocket = async () => {
	try {
		await socket.start();
		setLiveStatus('Realtime connected', 'live');
	} catch {
		setLiveStatus('Realtime reconnecting', 'offline');
		window.setTimeout(startSocket, 2000);
	}
};

socket.onreconnecting(() => {
	setLiveStatus('Realtime reconnecting', 'offline');
});

socket.onreconnected(() => {
	setLiveStatus('Realtime connected', 'live');
});

socket.onclose(() => {
	setLiveStatus('Realtime disconnected', 'offline');
	window.setTimeout(startSocket, 2000);
});

diagnosticsButton.addEventListener('click', async () => {
	diagnosticsButton.disabled = true;
	diagnosticsButton.textContent = 'Checking...';
	diagnosticsSummary.textContent = 'Running live checks...';

	try {
		const response = await fetch('/api/diagnostics');
		const diagnostics = await response.json();

		diagnosticsSummary.textContent = diagnostics.ok
			? 'All configured integrations responded successfully.'
			: 'One or more checks failed.';

		diagnosticsChecks.innerHTML = diagnostics.checks.map(renderCheck).join('');
	} catch (error) {
		diagnosticsSummary.textContent = `Diagnostics failed to load: ${error.message}`;
		diagnosticsChecks.innerHTML = '';
	} finally {
		diagnosticsButton.disabled = false;
		diagnosticsButton.textContent = 'Run checks';
	}
});

socket.on('jobs', jobs => {
	jobsById.clear();
	jobs.forEach(job => jobsById.set(job.id, job));
	renderJobs();
});

socket.on('job created', event => {
	jobsById.set(event.job.id, event.job);
	renderJobs();
});

socket.on('job updated', event => {
	jobsById.set(event.job.id, event.job);
	renderJobs();
});

mount.addEventListener('click', async event => {
	const button = event.target.closest('[data-fill-missing-job-id]');
	if (!button) {
		return;
	}

	const jobId = button.getAttribute('data-fill-missing-job-id');
	if (!jobId || pendingFillJobIds.has(jobId)) {
		return;
	}

	pendingFillJobIds.add(jobId);
	renderJobs();

	try {
		const response = await fetch(`/api/jobs/${encodeURIComponent(jobId)}/fill-missing`, {
			method: 'POST'
		});
		const result = await response.json();

		if (!response.ok) {
			throw new Error(result.error || 'Could not update the missing value.');
		}

		if (result.job?.id) {
			jobsById.set(result.job.id, result.job);
		}
	} catch (error) {
		const job = jobsById.get(jobId);
		if (job) {
			job.data = {
				...(job.data ?? {}),
				error: error.message,
			};
			jobsById.set(jobId, job);
		}
	} finally {
		pendingFillJobIds.delete(jobId);
		renderJobs();
	}
});

const renderJobs = () => {
	const jobs = Array.from(jobsById.values())
		.sort((left, right) => new Date(right.created) - new Date(left.created));

	mount.innerHTML = jobs.map(renderJob).join('');
	renderJobStatusSummary(jobs);
};

const renderJobStatusSummary = jobs => {
	if (jobs.length === 0) {
		jobStatusSummary.innerHTML = '<span class="status-pill">No jobs yet</span>';
		return;
	}

	const counts = {
		queued: 0,
		in_progress: 0,
		partial: 0,
		finished: 0,
		failed: 0,
	};

	jobs.forEach(job => {
		counts[job.status] = (counts[job.status] ?? 0) + 1;
	});

	jobStatusSummary.innerHTML = ['queued', 'in_progress', 'partial', 'finished', 'failed']
		.map(status => `<span class="status-pill ${status}">${formatStatus(status)}: ${counts[status] ?? 0}</span>`)
		.join('');
};

const formatStatus = status => {
	switch (status) {
		case 'in_progress':
			return 'In progress';
		case 'partial':
			return 'Partial';
		case 'finished':
			return 'Finished';
		case 'failed':
			return 'Failed';
		case 'queued':
		default:
			return 'Queued';
	}
};

const canFillMissing = job => {
	const hasCategory = Boolean(job.data?.category);
	const hasBudget = Boolean(job.data?.budget);
	return job.status === 'partial' && hasCategory !== hasBudget;
};

const escapeHtml = value => String(value ?? '')
	.replace(/&/g, '&amp;')
	.replace(/</g, '&lt;')
	.replace(/>/g, '&gt;')
	.replace(/"/g, '&quot;')
	.replace(/'/g, '&#39;');

const renderJob = job => {
	const partialField = job.data?.category ? 'budget' : 'category';
	const canFill = canFillMissing(job);
	const isUpdating = pendingFillJobIds.has(job.id);

	return `<article class="job ${escapeHtml(job.status)}" data-job-id="${escapeHtml(job.id)}">
		<div><strong>ID:</strong> <span>${escapeHtml(job.id)}</span></div>
		<div><strong>Status:</strong> <span class="status-pill ${escapeHtml(job.status)}">${escapeHtml(formatStatus(job.status))}</span></div>
		<div><strong>Created:</strong>
			<time>${Intl.DateTimeFormat(undefined, {
				dateStyle: 'medium',
				timeStyle: 'medium'
			}).format(new Date(job.created))}</time>
		</div>
		<div><strong>Destination name:</strong> <span>${escapeHtml(job.data?.destinationName || '')}</span></div>
		<div><strong>Description:</strong> <span>${escapeHtml(job.data?.description || '')}</span></div>
		<div><strong>Guessed category:</strong> <span>${job.data?.category ? escapeHtml(job.data.category) : '<em>Not yet classified</em>'}</span></div>
		<div><strong>Guessed budget:</strong> <span>${job.data?.budget ? escapeHtml(job.data.budget) : '<em>Not yet classified</em>'}</span></div>
		${canFill ? `<div class="job-actions"><span class="muted">Only the ${escapeHtml(partialField)} is still missing.</span><button class="button secondary" type="button" data-fill-missing-job-id="${escapeHtml(job.id)}" ${isUpdating ? 'disabled' : ''}>${isUpdating ? 'Updating...' : `Fill missing ${escapeHtml(partialField)} with same value`}</button></div>` : ''}
		${job.data?.manualUpdate ? `<div class="muted">${escapeHtml(job.data.manualUpdate)}</div>` : ''}
		${job.data?.error ? `<div class="error-box"><strong>Error:</strong> <span>${escapeHtml(job.data.error)}</span></div>` : ''}
		${job.data?.prompt ? `<div><strong>Prompt:</strong><br>
			<details>
				<summary>Show</summary>
				<pre>${escapeHtml(job.data.prompt)}</pre>
			</details>
		</div>` : ''}
		${job.data?.response ? `<div><strong>Model response:</strong>
			<details>
				<summary>Show</summary>
				<pre>${escapeHtml(job.data.response)}</pre>
			</details>
		</div>` : ''}
	</article>`;
};

const renderCheck = check => {
	const details = check.ok
		? Object.entries(check.details)
			.map(([key, value]) => `<div><strong>${escapeHtml(key)}:</strong> <span>${escapeHtml(value)}</span></div>`)
			.join('')
		: `<div><strong>Error:</strong> <span>${escapeHtml(check.error)}</span></div>`;

	return `<article class="check ${check.ok ? 'ok' : 'error'}">
		<div><strong>${escapeHtml(check.name)}</strong> <span>${check.ok ? 'OK' : 'Failed'}</span></div>
		${details}
	</article>`;
};

setLiveStatus('Realtime connecting', 'offline');
startSocket();