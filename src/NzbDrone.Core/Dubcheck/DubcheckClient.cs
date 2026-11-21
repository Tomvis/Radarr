using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Dubcheck
{
    // Tomvis (MEDIA-39): client for the dubcheck sidecar ($DUBCHECK_URL). Fail-open: any error,
    // timeout or open breaker is Unknown, so custom formats score as without dubcheck.
    public interface IDubcheckClient
    {
        DubVerdict Check(ReleaseInfo release, Movie movie, Language language);
        void Prefetch(IEnumerable<ReleaseInfo> releases, SearchCriteriaBase searchCriteria);
    }

    public class DubcheckClient : IDubcheckClient, IHandle<ApplicationStartedEvent>
    {
        private const int BreakerThreshold = 3;

        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PrefetchTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
        private static readonly TimeSpan BreakerOpenFor = TimeSpan.FromMinutes(5);

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        private readonly IHttpClient _httpClient;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;
        private readonly string _url;
        private readonly ConcurrentDictionary<(string, int), (DubVerdict Verdict, DateTime At)> _cache = new();
        private readonly object _breakerLock = new();
        private int _failures;
        private DateTime _openUntil = DateTime.MinValue;

        public DubcheckClient(IHttpClient httpClient, IQualityProfileService qualityProfileService, Logger logger)
        {
            _httpClient = httpClient;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
            _url = Environment.GetEnvironmentVariable("DUBCHECK_URL")?.TrimEnd('/');
        }

        public void Handle(ApplicationStartedEvent message)
        {
            DubcheckSpecification.Client = this;
            _logger.Info(_url == null ? "Dubcheck disabled (DUBCHECK_URL unset)" : "Dubcheck at {0}", _url);
        }

        public DubVerdict Check(ReleaseInfo release, Movie movie, Language language)
        {
            if (_url == null || release?.Guid == null || movie == null)
            {
                return DubVerdict.Unknown;
            }

            var profile = GetProfile(movie);

            if (profile == null || !WantedLanguages(profile).Contains(language))
            {
                return DubVerdict.Unknown;
            }

            var key = (release.Guid, language.Id);

            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            {
                return hit.Verdict;
            }

            var body = new
            {
                release = ReleaseBody(release),
                movie = MovieBody(movie),
                profile = profile.Name,
                language = language.Name
            };

            var response = Post("/v1/check", body, CheckTimeout);

            if (response == null)
            {
                return DubVerdict.Unknown;
            }

            var verdict = JsonSerializer.Deserialize<CheckResponse>(response, JsonOptions)?.Verdict switch
            {
                "has" => DubVerdict.Has,
                "lacks" => DubVerdict.Lacks,
                _ => DubVerdict.Unknown
            };

            _cache[key] = (verdict, DateTime.UtcNow);

            return verdict;
        }

        public void Prefetch(IEnumerable<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
        {
            var movie = searchCriteria?.Movie;

            if (_url == null || movie == null)
            {
                return;
            }

            var profile = GetProfile(movie);

            if (profile == null)
            {
                return;
            }

            var list = releases.Where(r => r?.Guid != null).Select(ReleaseBody).ToList();

            foreach (var language in WantedLanguages(profile))
            {
                var body = new { releases = list, movie = MovieBody(movie), profile = profile.Name, language = language.Name };
                var response = Post("/v1/prefetch", body, PrefetchTimeout);

                _logger.Debug("Dubcheck prefetch {0} ({1}): {2}", movie.Title, language.Name, response ?? "failed");
            }
        }

        private QualityProfile GetProfile(Movie movie)
        {
            try
            {
                return movie.QualityProfile ?? _qualityProfileService.Get(movie.QualityProfileId);
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Dubcheck: no profile for {0}", movie.Title);
                return null;
            }
        }

        // Languages of the Dubcheck conditions this profile gives a non-zero score.
        private static List<Language> WantedLanguages(QualityProfile profile)
        {
            return (profile.FormatItems ?? new List<Profiles.ProfileFormatItem>())
                .Where(i => i.Score != 0 && i.Format?.Specifications != null)
                .SelectMany(i => i.Format.Specifications.OfType<DubcheckSpecification>())
                .Select(s => (Language)s.Value)
                .Distinct()
                .ToList();
        }

        private static object ReleaseBody(ReleaseInfo r)
        {
            return new
            {
                guid = r.Guid,
                title = r.Title,
                indexer = r.Indexer,
                indexerId = r.IndexerId,
                downloadUrl = r.DownloadUrl,
                infoUrl = r.InfoUrl,
                commentUrl = r.CommentUrl,
                description = r.Description,
                attrs = r.IndexerAttributes ?? new Dictionary<string, string>(),
                size = r.Size,
                protocol = r.DownloadProtocol.ToString().ToLowerInvariant()
            };
        }

        private static object MovieBody(Movie m)
        {
            var meta = m.MovieMetadata?.Value;

            return new
            {
                tmdbId = meta?.TmdbId ?? m.TmdbId,
                imdbId = meta?.ImdbId ?? m.ImdbId,
                title = meta?.Title ?? m.Title,
                year = meta?.Year ?? m.Year,
                originalLanguage = meta?.OriginalLanguage?.Name,
                genres = meta?.Genres ?? new List<string>()
            };
        }

        private string Post(string path, object body, TimeSpan timeout)
        {
            lock (_breakerLock)
            {
                if (DateTime.UtcNow < _openUntil)
                {
                    return null;
                }
            }

            try
            {
                var request = new HttpRequestBuilder(_url + path).Post().Build();
                request.Headers.ContentType = "application/json";
                request.SetContent(JsonSerializer.Serialize(body, JsonOptions));
                request.RequestTimeout = timeout;

                var response = _httpClient.Execute(request);

                lock (_breakerLock)
                {
                    _failures = 0;
                }

                return response.Content;
            }
            catch (Exception e)
            {
                lock (_breakerLock)
                {
                    if (++_failures >= BreakerThreshold)
                    {
                        _openUntil = DateTime.UtcNow + BreakerOpenFor;
                        _failures = 0;
                        _logger.Warn("Dubcheck unreachable ({0}); skipping it for {1} min", e.Message, BreakerOpenFor.TotalMinutes);
                    }
                }

                return null;
            }
        }

        private class CheckResponse
        {
            public string Verdict { get; set; }
        }
    }
}
