using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;

namespace ProblemReporter
{
    // DTO property names match the Radarr/Sonarr/Seerr JSON (lower camel case).
    public class ArrFile { public int id { get; set; } public string relativePath { get; set; } public string path { get; set; } public string sceneName { get; set; } }
    public class RadarrMovie { public int id { get; set; } public string title { get; set; } public int tmdbId { get; set; } public string imdbId { get; set; } public bool hasFile { get; set; } public bool monitored { get; set; } public ArrFile movieFile { get; set; } }
    public class SonarrSeries { public int id { get; set; } public string title { get; set; } public int tvdbId { get; set; } public int tmdbId { get; set; } public string imdbId { get; set; } }
    public class SonarrEpisode { public int id { get; set; } public int seriesId { get; set; } public int seasonNumber { get; set; } public int episodeNumber { get; set; } public bool hasFile { get; set; } public bool monitored { get; set; } public int episodeFileId { get; set; } }
    public class ArrHistory { public int id { get; set; } public string eventType { get; set; } public string downloadId { get; set; } public string sourceTitle { get; set; } public DateTime date { get; set; } public int episodeId { get; set; } public Dictionary<string, string> data { get; set; } }
    public class ArrHistoryPage { public List<ArrHistory> records { get; set; } }
    public class SeerrMediaInfo { public int id { get; set; } }
    public class SeerrTitle { public int id { get; set; } public SeerrMediaInfo mediaInfo { get; set; } }
    public class SeerrUser { public int id { get; set; } public string jellyfinUserId { get; set; } public string displayName { get; set; } }
    public class SeerrUserPage { public List<SeerrUser> results { get; set; } }
    public class SeerrIssue { public int id { get; set; } }

    public class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
    }

    /// <summary>Thin JSON-over-HTTP helper shared by the Radarr, Sonarr and Seerr calls.</summary>
    public class Http
    {
        private readonly IHttpClient _http;
        private readonly IJsonSerializer _json;
        private readonly ILogger _log;

        public Http(IHttpClient http, IJsonSerializer json, ILogger log)
        {
            _http = http;
            _json = json;
            _log = log;
        }

        public static string Clean(string url) => (url ?? "").Trim().TrimEnd('/');

        public async Task<T> Send<T>(string method, string baseUrl, string apiKey, string path, object body, CancellationToken ct) where T : class
        {
            if (string.IsNullOrEmpty(Clean(baseUrl)) || string.IsNullOrEmpty(apiKey))
                throw new ApiException($"not configured ({baseUrl})");

            var opts = new HttpRequestOptions
            {
                Url = Clean(baseUrl) + path,
                CancellationToken = ct,
                TimeoutMs = 60000,
                LogRequest = false,
                LogResponse = false,
                LogErrorResponseBody = true,
                EnableDefaultUserAgent = true
            };
            opts.RequestHeaders["X-Api-Key"] = apiKey;
            opts.AcceptHeader = "application/json";
            if (body != null)
            {
                opts.RequestContent = _json.SerializeToString(body).AsMemory();
                opts.RequestContentType = "application/json";
            }

            HttpResponseInfo resp;
            try
            {
                resp = await _http.SendAsync(opts, method).ConfigureAwait(false);
            }
            catch (HttpException ex)
            {
                throw new ApiException($"{method} {path} failed: {(ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : ex.Message)}");
            }

            using (resp)
            {
                if ((int)resp.StatusCode >= 400)
                    throw new ApiException($"{method} {path} returned {(int)resp.StatusCode}");
                if (typeof(T) == typeof(string) || resp.Content == null) return null;
                using (var ms = new MemoryStream())
                {
                    await resp.Content.CopyToAsync(ms).ConfigureAwait(false);
                    if (ms.Length == 0) return null;
                    ms.Position = 0;
                    return _json.DeserializeFromStream<T>(ms);
                }
            }
        }

        public Task<T> Get<T>(string baseUrl, string apiKey, string path, CancellationToken ct) where T : class
            => Send<T>("GET", baseUrl, apiKey, path, null, ct);
    }

    public class Radarr
    {
        private readonly Http _h;
        public Radarr(Http h) { _h = h; }
        private static PluginConfiguration C => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        public async Task<RadarrMovie> FindByTmdb(int tmdbId, CancellationToken ct)
            => (await _h.Get<List<RadarrMovie>>(C.RadarrUrl, C.RadarrApiKey, $"/api/v3/movie?tmdbId={tmdbId}", ct).ConfigureAwait(false))?.FirstOrDefault();

        public async Task<RadarrMovie> FindByImdb(string imdbId, CancellationToken ct)
            => (await _h.Get<List<RadarrMovie>>(C.RadarrUrl, C.RadarrApiKey, "/api/v3/movie", ct).ConfigureAwait(false))
                ?.FirstOrDefault(m => string.Equals(m.imdbId, imdbId, StringComparison.OrdinalIgnoreCase));

        public Task<RadarrMovie> Get(int id, CancellationToken ct)
            => _h.Get<RadarrMovie>(C.RadarrUrl, C.RadarrApiKey, $"/api/v3/movie/{id}", ct);

        public async Task<List<ArrHistory>> History(int movieId, CancellationToken ct)
            => await _h.Get<List<ArrHistory>>(C.RadarrUrl, C.RadarrApiKey, $"/api/v3/history/movie?movieId={movieId}", ct).ConfigureAwait(false) ?? new List<ArrHistory>();

        public Task DeleteFile(int fileId, CancellationToken ct)
            => _h.Send<string>("DELETE", C.RadarrUrl, C.RadarrApiKey, $"/api/v3/moviefile/{fileId}", null, ct);

        public Task MarkFailed(int historyId, CancellationToken ct)
            => _h.Send<string>("POST", C.RadarrUrl, C.RadarrApiKey, $"/api/v3/history/failed/{historyId}", null, ct);

        public Task Monitor(int movieId, CancellationToken ct)
            => _h.Send<string>("PUT", C.RadarrUrl, C.RadarrApiKey, "/api/v3/movie/editor", new { movieIds = new[] { movieId }, monitored = true }, ct);

        public Task Search(int movieId, CancellationToken ct)
            => _h.Send<string>("POST", C.RadarrUrl, C.RadarrApiKey, "/api/v3/command", new { name = "MoviesSearch", movieIds = new[] { movieId } }, ct);
    }

    public class Sonarr
    {
        private readonly Http _h;
        public Sonarr(Http h) { _h = h; }
        private static PluginConfiguration C => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        public async Task<SonarrSeries> FindByTvdb(int tvdbId, CancellationToken ct)
            => (await _h.Get<List<SonarrSeries>>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/series?tvdbId={tvdbId}", ct).ConfigureAwait(false))?.FirstOrDefault();

        public async Task<SonarrSeries> FindBy(Func<SonarrSeries, bool> match, CancellationToken ct)
            => (await _h.Get<List<SonarrSeries>>(C.SonarrUrl, C.SonarrApiKey, "/api/v3/series", ct).ConfigureAwait(false))?.FirstOrDefault(match);

        public Task<SonarrSeries> GetSeries(int id, CancellationToken ct)
            => _h.Get<SonarrSeries>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/series/{id}", ct);

        public async Task<List<SonarrEpisode>> Episodes(int seriesId, CancellationToken ct)
            => await _h.Get<List<SonarrEpisode>>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/episode?seriesId={seriesId}", ct).ConfigureAwait(false) ?? new List<SonarrEpisode>();

        public Task<SonarrEpisode> Episode(int id, CancellationToken ct)
            => _h.Get<SonarrEpisode>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/episode/{id}", ct);

        public Task<ArrFile> EpisodeFile(int id, CancellationToken ct)
            => _h.Get<ArrFile>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/episodefile/{id}", ct);

        public async Task<List<ArrHistory>> History(int episodeId, CancellationToken ct)
            => (await _h.Get<ArrHistoryPage>(C.SonarrUrl, C.SonarrApiKey, $"/api/v3/history?episodeId={episodeId}&pageSize=100&sortKey=date&sortDirection=descending", ct).ConfigureAwait(false))?.records ?? new List<ArrHistory>();

        public Task DeleteFile(int fileId, CancellationToken ct)
            => _h.Send<string>("DELETE", C.SonarrUrl, C.SonarrApiKey, $"/api/v3/episodefile/{fileId}", null, ct);

        public Task MarkFailed(int historyId, CancellationToken ct)
            => _h.Send<string>("POST", C.SonarrUrl, C.SonarrApiKey, $"/api/v3/history/failed/{historyId}", null, ct);

        public Task Monitor(int[] episodeIds, CancellationToken ct)
            => _h.Send<string>("PUT", C.SonarrUrl, C.SonarrApiKey, "/api/v3/episode/monitor", new { episodeIds, monitored = true }, ct);

        public Task Search(int[] episodeIds, CancellationToken ct)
            => _h.Send<string>("POST", C.SonarrUrl, C.SonarrApiKey, "/api/v3/command", new { name = "EpisodeSearch", episodeIds }, ct);
    }

    public class Seerr
    {
        private readonly Http _h;
        private List<SeerrUser> _users;
        private DateTime _usersAt = DateTime.MinValue;
        public Seerr(Http h) { _h = h; }
        private static PluginConfiguration C => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        public static bool Configured => !string.IsNullOrWhiteSpace(C.SeerrUrl) && !string.IsNullOrWhiteSpace(C.SeerrApiKey);

        /// <summary>Seerr's internal media id for a title, or 0 if Seerr doesn't track it.</summary>
        public async Task<int> MediaId(bool tv, int tmdbId, CancellationToken ct)
        {
            if (tmdbId <= 0) return 0;
            var t = await _h.Get<SeerrTitle>(C.SeerrUrl, C.SeerrApiKey, $"/api/v1/{(tv ? "tv" : "movie")}/{tmdbId}", ct).ConfigureAwait(false);
            return t?.mediaInfo?.id ?? 0;
        }

        /// <summary>Seerr user linked to this Emby user, or 0.</summary>
        public async Task<int> UserFor(Guid embyUserId, CancellationToken ct)
        {
            if (_users == null || DateTime.UtcNow - _usersAt > TimeSpan.FromMinutes(30))
            {
                _users = (await _h.Get<SeerrUserPage>(C.SeerrUrl, C.SeerrApiKey, "/api/v1/user?take=1000", ct).ConfigureAwait(false))?.results ?? new List<SeerrUser>();
                _usersAt = DateTime.UtcNow;
            }
            var want = embyUserId.ToString("N");
            return _users.FirstOrDefault(u => string.Equals((u.jellyfinUserId ?? "").Replace("-", ""), want, StringComparison.OrdinalIgnoreCase))?.id ?? 0;
        }

        public async Task<int> CreateIssue(int mediaId, int userId, string message, int? season, int? episode, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                ["issueType"] = 1,   // Video
                ["message"] = message,
                ["mediaId"] = mediaId
            };
            if (userId > 0) body["userId"] = userId;
            if (season.HasValue) body["problemSeason"] = season.Value;
            if (episode.HasValue) body["problemEpisode"] = episode.Value;
            var issue = await _h.Send<SeerrIssue>("POST", C.SeerrUrl, C.SeerrApiKey, "/api/v1/issue", body, ct).ConfigureAwait(false);
            return issue?.id ?? 0;
        }

        public Task Comment(int issueId, string message, CancellationToken ct)
            => _h.Send<string>("POST", C.SeerrUrl, C.SeerrApiKey, $"/api/v1/issue/{issueId}/comment", new { message }, ct);

        public Task Resolve(int issueId, CancellationToken ct)
            => _h.Send<string>("POST", C.SeerrUrl, C.SeerrApiKey, $"/api/v1/issue/{issueId}/resolved", null, ct);
    }
}
