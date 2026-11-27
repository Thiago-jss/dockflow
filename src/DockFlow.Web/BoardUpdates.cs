using Microsoft.AspNetCore.SignalR;

namespace DockFlow.Web;

public interface IBoardClient
{
    Task Invalidate();
}

public sealed class BoardHub : Hub<IBoardClient>
{
    internal static string Group(Guid tenant) => $"tenant:{tenant:N}";

    public override Task OnConnectedAsync()
    {
        if (Identity.ActorFor(Context.User) is not { } actor) { Context.Abort(); return Task.CompletedTask; }
        return Groups.AddToGroupAsync(Context.ConnectionId, Group(actor.TenantId), Context.ConnectionAborted);
    }
}

public sealed class BoardUpdates(IHubContext<BoardHub, IBoardClient> hub, ILogger<BoardUpdates> log)
{
    public async Task CommittedAsync(Guid tenant)
    {
        try { await hub.Clients.Group(BoardHub.Group(tenant)).Invalidate().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception) { log.LogWarning("Falha ao notificar o painel; a atualização periódica continua ativa."); }
    }
}
