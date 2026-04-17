using FireflyCategorizer.DotNet.Models;

namespace FireflyCategorizer.DotNet.Services;

public sealed class JobStore
{
    private readonly Dictionary<string, JobRecord> _jobs = new();

    public event Action? Changed;

    public IReadOnlyList<JobRecord> GetJobsSnapshot()
    {
        lock (_jobs)
        {
            return _jobs.Values
                .OrderByDescending(job => job.Created)
                .ToList();
        }
    }

    public JobRecord? GetJob(string id)
    {
        lock (_jobs)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    public async Task<JobRecord> CreateJobAsync(JobData data, CancellationToken cancellationToken = default)
    {
        var job = new JobRecord
        {
            Data = data,
        };

        lock (_jobs)
        {
            _jobs[job.Id] = job;
        }

        await BroadcastAsync("job created", job, cancellationToken);
        return job;
    }

    public async Task UpdateJobDataAsync(string id, JobData data, CancellationToken cancellationToken = default)
    {
        JobRecord? job;
        lock (_jobs)
        {
            if (!_jobs.TryGetValue(id, out job))
            {
                return;
            }

            job.Data = data;
        }

        await BroadcastAsync("job updated", job!, cancellationToken);
    }

    public async Task SetJobInProgressAsync(string id, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, "in_progress", null, cancellationToken);
    }

    public async Task SetJobFinishedAsync(string id, string status = "finished", CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, status, null, cancellationToken);
    }

    public async Task SetJobFailedAsync(string id, string error, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, "failed", error, cancellationToken);
    }

    private async Task UpdateStatusAsync(string id, string status, string? error, CancellationToken cancellationToken)
    {
        JobRecord? job;
        lock (_jobs)
        {
            if (!_jobs.TryGetValue(id, out job))
            {
                return;
            }

            job.Status = status;
            if (error is not null)
            {
                job.Data.Error = error;
            }
        }

        await BroadcastAsync("job updated", job!, cancellationToken);
    }

    private async Task BroadcastAsync(string eventName, JobRecord job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Changed?.Invoke();
        await Task.CompletedTask;
    }
}