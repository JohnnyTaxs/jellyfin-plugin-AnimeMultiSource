using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Feeds AnimeSchedule.net air dates into Jellyfin's own "Upcoming" tab, which lists virtual
    // future episodes (IsVirtualItem = true) carrying a PremiereDate. Mirrors the approach the
    // official jellyfin-plugin-tvdb uses (TvdbMissingEpisodeProvider): a hosted service that
    // reacts to IProviderManager.RefreshCompleted rather than polling on a timer, so it re-checks
    // a series every time it's refreshed - the same trigger the rest of this plugin relies on.
    // AnimeScheduleRefreshTask covers the gap between refreshes (delayed/rescheduled episodes)
    // on a fixed schedule instead.
    public class AnimeScheduleMissingEpisodeProvider : IHostedService
    {
        private readonly IProviderManager _providerManager;
        private readonly AnimeScheduleUpcomingEpisodeUpdater _updater;
        private readonly ILogger<AnimeScheduleMissingEpisodeProvider> _logger;
        private readonly ConcurrentQueue<Series> _pending = new();
        private readonly ConcurrentDictionary<Guid, byte> _queued = new();
        private readonly SemaphoreSlim _signal = new(0);
        private CancellationTokenSource? _shutdown;
        private Task? _worker;

        public AnimeScheduleMissingEpisodeProvider(
            IProviderManager providerManager,
            ILibraryManager libraryManager,
            ILocalizationManager localization,
            ILogger<AnimeScheduleMissingEpisodeProvider> logger)
        {
            _providerManager = providerManager;
            _updater = new AnimeScheduleUpcomingEpisodeUpdater(libraryManager, localization, logger);
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _shutdown = new CancellationTokenSource();
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _worker = Task.Run(() => ProcessQueueAsync(_shutdown.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted -= OnRefreshCompleted;
            _shutdown?.Cancel();
            // Wake the worker so it observes cancellation even when the queue is empty.
            _signal.Release();

            if (_worker != null)
            {
                try
                {
                    await _worker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            _shutdown?.Dispose();
            _shutdown = null;
            _worker = null;
        }

        private void OnRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
        {
            if (e.Argument is not Series series)
            {
                return;
            }

            // RefreshCompleted is raised inline from Jellyfin's refresh/scan path. Do not block
            // that path on AnimeSchedule HTTP calls: queue the series and let the worker handle it.
            if (_queued.TryAdd(series.Id, 0))
            {
                _pending.Enqueue(series);
                _signal.Release();
            }
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                while (_pending.TryDequeue(out var series))
                {
                    _queued.TryRemove(series.Id, out _);

                    try
                    {
                        await _updater.RefreshSeriesAsync(series, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update AnimeSchedule upcoming episodes for {SeriesName}", series.Name);
                    }
                }
            }
        }
    }
}
