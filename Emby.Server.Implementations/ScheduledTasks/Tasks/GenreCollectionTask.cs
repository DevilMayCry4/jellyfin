using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Automatically create collections based on movie genres.
/// </summary>
public class GenreCollectionTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<GenreCollectionTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreCollectionTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="logger">The logger.</param>
    public GenreCollectionTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILocalizationManager localization,
        ILogger<GenreCollectionTask> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _localization = localization;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Auto Collections from Genres";

    /// <inheritdoc />
    public string Key => "GenreCollections";

    /// <inheritdoc />
    public string Description => "Automatically create collections based on movie genres.";

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var genreMoviesMap = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IncludeItemTypes = [BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true
        }).OfType<Movie>();

        foreach (var movie in movies)
        {
            foreach (var genre in movie.Genres)
            {
                if (genreMoviesMap.TryGetValue(genre, out var movieList))
                {
                    movieList.Add(movie.Id);
                }
                else
                {
                    genreMoviesMap[genre] = new HashSet<Guid> { movie.Id };
                }
            }
        }

        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true
        }).OfType<BoxSet>().ToList();

        int totalGenres = genreMoviesMap.Count;
        int current = 0;

        // 1. Process genres that should have collections
        foreach (var (genre, movieIds) in genreMoviesMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var boxSet = boxSets.FirstOrDefault(b => string.Equals(b.Name, genre, StringComparison.OrdinalIgnoreCase));

            if (movieIds.Count >= 2)
            {
                if (boxSet is null)
                {
                    _logger.LogInformation("Creating collection '{Genre}' with {Count} movies", genre, movieIds.Count);
                    try
                    {
                        boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = genre
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating collection '{Genre}'", genre);
                        continue;
                    }
                }

                // Sync items: Add missing movies
                var currentItemIds = boxSet.LinkedChildren
                    .Select(c => c.ItemId)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToHashSet();

                var moviesToAdd = movieIds.Where(id => !currentItemIds.Contains(id)).ToList();
                if (moviesToAdd.Count > 0)
                {
                    _logger.LogInformation("Adding {Count} movies to collection '{Genre}'", moviesToAdd.Count, genre);
                    await _collectionManager.AddToCollectionAsync(boxSet.Id, moviesToAdd).ConfigureAwait(false);
                }

                // Sync items: Remove movies that no longer have this genre
                var moviesToRemove = currentItemIds.Where(id => !movieIds.Contains(id)).ToList();
                if (moviesToRemove.Count > 0)
                {
                    _logger.LogInformation("Removing {Count} movies from collection '{Genre}'", moviesToRemove.Count, genre);
                    await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, moviesToRemove).ConfigureAwait(false);
                }
            }
            else if (boxSet is not null)
            {
                // Genre has < 2 movies, delete existing collection
                _logger.LogInformation("Removing collection '{Genre}' because it has only {Count} movie(s)", genre, movieIds.Count);
                _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true });
            }

            current++;
            progress.Report(100D * current / Math.Max(1, totalGenres));
        }

        // 2. Clean up orphan boxsets that might have been genre-based but the genre is gone
        // Re-fetch boxsets because some might have been deleted
        boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true
        }).OfType<BoxSet>().ToList();

        foreach (var boxSet in boxSets)
        {
            if (boxSet.LinkedChildren.Length <= 1)
            {
                // If it's empty or has 1 item, and it's not in our current genre map (meaning no movies have this genre anymore)
                // or it was already handled above. This is a safety check.
                if (!genreMoviesMap.ContainsKey(boxSet.Name))
                {
                    _logger.LogInformation("Removing orphan/empty collection '{CollectionName}'", boxSet.Name);
                    _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true });
                }
            }
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }];
    }
}
