using FireflyCategorizer.DotNet.Services;
using Microsoft.AspNetCore.SignalR;

namespace FireflyCategorizer.DotNet.Hubs;

public sealed class JobsHub : Hub
{
    private readonly JobStore _jobStore;

    public JobsHub(JobStore jobStore)
    {
        _jobStore = jobStore;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("jobs", _jobStore.GetJobsSnapshot());
        await base.OnConnectedAsync();
    }
}