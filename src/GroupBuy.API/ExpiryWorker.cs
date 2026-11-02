using GroupBuy.Application;
namespace GroupBuy.API;
public sealed class ExpiryWorker(IServiceScopeFactory scopes, ILogger<ExpiryWorker> logger, WorkerHealthState health, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { health.Started(clock.GetUtcNow()); await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ICampaignExpiryProcessor>().Process(stoppingToken); health.Completed(clock.GetUtcNow()); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { health.Failed(ex.GetType().Name); logger.LogError("Expiry batch failed ({ErrorType}); retrying next interval", ex.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        } while (!stoppingToken.IsCancellationRequested);
        health.Stopped();
    }
}
