using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;

namespace ProblemReporter
{
    /// <summary>
    /// Keeps a private "Report a Problem" playlist for every user and watches them. Anything a user adds
    /// is taken out again and reported: a Seerr issue in their name, the release blocklisted in Radarr/Sonarr,
    /// the file deleted and a replacement searched for. When the replacement lands in Emby the issue is resolved.
    /// </summary>
    public class ReportWatcher : IServerEntryPoint
    {
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CheckPendingEvery = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan WaitForEmbyAfterImport = TimeSpan.FromMinutes(60);

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IPlaylistManager _playlists;
        private readonly ISessionManager _sessions;
        private readonly ILogger _log;
        private readonly Radarr _radarr;
        private readonly Sonarr _sonarr;
        private readonly Seerr _seerr;

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Timer _pollTimer;
        private Timer _pendingTimer;
        private readonly Dictionary<long, long> _playlistByUser = new Dictionary<long, long>();
        private readonly HashSet<long> _createFailedLogged = new HashSet<long>();
        private readonly Dictionary<string, DateTime> _sessionFirstSeen = new Dictionary<string, DateTime>();

        public ReportWatcher(ILibraryManager library, IUserManager users, IPlaylistManager playlists,
            ISessionManager sessions, IHttpClient http, IJsonSerializer json, ILogManager logManager)
        {
            _library = library;
            _users = users;
            _playlists = playlists;
            _sessions = sessions;
            _log = logManager.GetLogger("ProblemReporter");
            var h = new Http(http, json, _log);
            _radarr = new Radarr(h);
            _sonarr = new Sonarr(h);
            _seerr = new Seerr(h);
        }

        private static PluginConfiguration Cfg => Plugin.Instance?.Configuration ?? new PluginConfiguration();
        private static void Save() => Plugin.Instance?.SaveConfiguration();
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public void Run()
        {
            _pollTimer = new Timer(_ => Tick(PollAsync), null, TimeSpan.FromSeconds(30), PollEvery);
            _pendingTimer = new Timer(_ => Tick(CheckPendingAsync), null, TimeSpan.FromMinutes(1), CheckPendingEvery);
            _log.Info("started{0}", Cfg.DryRun ? " (dry run)" : "");
        }

        private void Tick(Func<CancellationToken, Task> work)
        {
            if (!Gate.Wait(0)) return;   // previous run still going; skip this tick
            Task.Run(async () =>
            {
                try { await work(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.ErrorException("run failed", ex); }
                finally { Gate.Release(); }
            });
        }

        // ── Playlists ─────────────────────────────────────────────────────────────────────

        private IEnumerable<User> TargetUsers(PluginConfiguration cfg)
        {
            var excluded = new HashSet<string>((cfg.ExcludedUserIds ?? Array.Empty<string>()).Select(Norm));
            return _users.GetUserList(new UserQuery())
                .Where(u => !(u.Policy?.IsDisabled ?? false) && !excluded.Contains(Norm(u.Id.ToString("N"))));
        }

        private static string Norm(string id) => (id ?? "").Replace("-", "").Trim().ToLowerInvariant();

        private BaseItem FindPlaylist(User user, string name)
        {
            if (_playlistByUser.TryGetValue(user.InternalId, out var id))
            {
                var cached = _library.GetItemById(id);
                if (cached is Playlist && string.Equals(cached.Name, name, StringComparison.OrdinalIgnoreCase)) return cached;
                _playlistByUser.Remove(user.InternalId);
            }
            var pl = _library.GetItemList(new InternalItemsQuery
            {
                User = user,
                IncludeItemTypes = new[] { "Playlist" },
                Name = name,
                Recursive = true
            }).Where(p => p is Playlist && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
              .OrderBy(p => p.DateCreated).FirstOrDefault();
            if (pl != null) _playlistByUser[user.InternalId] = pl.InternalId;
            return pl;
        }

        private async Task<BaseItem> EnsurePlaylist(User user, PluginConfiguration cfg)
        {
            var pl = FindPlaylist(user, cfg.PlaylistName);
            if (pl != null) return pl;

            // Emby won't create an empty playlist, so create it with one placeholder video and take that straight out.
            var placeholder = _library.GetItemList(new InternalItemsQuery
            {
                User = user,
                IncludeItemTypes = new[] { "Movie", "Episode" },
                Recursive = true,
                Limit = 1,
                IsVirtualItem = false
            }).FirstOrDefault();
            if (placeholder == null)
            {
                if (_createFailedLogged.Add(user.InternalId))
                    _log.Warn("{0} can't see any movies or episodes; no playlist made", user.Name);
                return null;
            }

            try
            {
                await _playlists.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = cfg.PlaylistName,
                    ItemIdList = new[] { placeholder.InternalId },
                    MediaType = "Video",
                    User = user,
                    IsPublic = false
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_createFailedLogged.Add(user.InternalId))
                    _log.ErrorException("couldn't create playlist for {0}", ex, user.Name);
                return null;
            }

            for (int i = 0; pl == null && i < 5; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                pl = FindPlaylist(user, cfg.PlaylistName);
            }
            if (pl == null) return null;

            var entries = Entries(pl);
            if (entries.Count > 0)
                await _playlists.RemoveFromPlaylist(pl.InternalId, entries.Select(e => e.Entry).ToArray()).ConfigureAwait(false);
            _log.Info("created \"{0}\" playlist for {1}", cfg.PlaylistName, user.Name);
            return pl;
        }

        private List<(long Entry, BaseItem Item)> Entries(BaseItem playlist)
        {
            var list = new List<(long, BaseItem)>();
            foreach (var it in _library.GetItemList(new InternalItemsQuery { ListIds = new[] { playlist.InternalId } }))
            {
                long entry = 0;
                try { entry = it.ListItemEntryId; } catch { }
                if (entry == 0) entry = it.InternalId;
                list.Add((entry, it));
            }
            return list;
        }

        private async Task PollAsync(CancellationToken ct)
        {
            var cfg = Cfg;
            try { await AnnounceAsync(cfg, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Warn("announcement failed: {0}", ex.Message); }
            foreach (var user in TargetUsers(cfg).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var pl = await EnsurePlaylist(user, cfg).ConfigureAwait(false);
                if (pl == null) continue;
                var entries = Entries(pl);
                if (entries.Count == 0) continue;

                // Take everything out first so a slow Radarr/Sonarr can't cause a second report of the same thing.
                await _playlists.RemoveFromPlaylist(pl.InternalId, entries.Select(e => e.Entry).ToArray()).ConfigureAwait(false);
                var items = entries.Select(e => e.Item).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
                _log.Info("{0} reported {1} item(s): {2}", user.Name, items.Count, string.Join(", ", items.Select(Describe)));

                try { await HandleBatch(user, items, cfg, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.ErrorException("handling {0}'s report failed", ex, user.Name); }
                Save();
            }
        }

        // ── One-time announcement ──────────────────────────────────────────────────────────

        /// <summary>
        /// Shows each user the announcement once, the first time they're in an Emby app that can display
        /// messages and isn't playing anything. Waits 20 seconds into the session so the app has finished loading.
        /// </summary>
        private async Task AnnounceAsync(PluginConfiguration cfg, CancellationToken ct)
        {
            if (!cfg.AnnounceEnabled || cfg.DryRun || string.IsNullOrWhiteSpace(cfg.AnnouncementText)) return;
            var done = new HashSet<string>((cfg.AnnouncedUserIds ?? Array.Empty<string>()).Select(Norm));
            var targets = TargetUsers(cfg).ToDictionary(u => u.InternalId);
            var now = DateTime.UtcNow;
            var live = new HashSet<string>();
            bool changed = false;

            foreach (var s in _sessions.Sessions.ToList())
            {
                if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                live.Add(s.Id);
                if (!_sessionFirstSeen.ContainsKey(s.Id)) _sessionFirstSeen[s.Id] = now;
                if (!targets.TryGetValue(s.UserInternalId, out var user)) continue;
                var key = Norm(user.Id.ToString("N"));
                if (done.Contains(key)) continue;
                if (now - _sessionFirstSeen[s.Id] < TimeSpan.FromSeconds(20)) continue;
                if (s.NowPlayingItem != null) continue;
                if (now - s.LastActivityDate > TimeSpan.FromMinutes(5)) continue;
                if (!(s.SupportedCommands ?? Array.Empty<string>()).Contains("DisplayMessage", StringComparer.OrdinalIgnoreCase)) continue;

                try
                {
                    await _sessions.SendMessageCommand(null, s.Id, new MessageCommand
                    {
                        Header = cfg.AnnouncementHeader,
                        Text = cfg.AnnouncementText,
                        TimeoutMs = null      // stays up until dismissed
                    }, ct).ConfigureAwait(false);
                    done.Add(key);
                    changed = true;
                    _log.Info("showed the Report a Problem announcement to {0} ({1})", user.Name, s.Client);
                }
                catch (Exception ex) { _log.Debug("couldn't show announcement to {0}: {1}", user.Name, ex.Message); }
            }

            foreach (var gone in _sessionFirstSeen.Keys.Where(k => !live.Contains(k)).ToList()) _sessionFirstSeen.Remove(gone);
            if (changed)
            {
                cfg.AnnouncedUserIds = done.ToArray();
                Save();
            }
        }

        // ── Reports ───────────────────────────────────────────────────────────────────────

        private async Task HandleBatch(User user, List<BaseItem> items, PluginConfiguration cfg, CancellationToken ct)
        {
            foreach (var group in items.OfType<Episode>().GroupBy(e => e.SeriesId))
            {
                var eps = group.ToList();
                if (eps.Count > Math.Max(1, cfg.MaxEpisodesPerReport))
                    await Flag(user, eps[0], $"{eps.Count} episodes of {eps[0].SeriesName} were reported at once (a whole season or show?). Not replacing them automatically.", tv: true, ct).ConfigureAwait(false);
                else
                    foreach (var ep in eps) await Guarded(user, ep, () => ReportEpisode(user, ep, cfg, ct), ct).ConfigureAwait(false);
            }
            foreach (var movie in items.OfType<Movie>())
                await Guarded(user, movie, () => ReportMovie(user, movie, cfg, ct), ct).ConfigureAwait(false);
            foreach (var other in items.Where(i => !(i is Movie) && !(i is Episode)))
            {
                if (other is Series || other is Season)
                    await Flag(user, other, $"A whole {(other is Season ? "season" : "show")} was reported. Not replacing it automatically.", tv: true, ct).ConfigureAwait(false);
                else
                    _log.Info("ignoring {0} ({1}); only movies and episodes can be reported", other.Name, other.GetType().Name);
            }
        }

        private async Task Guarded(User user, BaseItem item, Func<Task> work, CancellationToken ct)
        {
            if (Cfg.Reports.Any(r => r.EmbyItemId == item.InternalId && r.Status == ReportStatus.Waiting))
            {
                _log.Info("{0} is already being replaced; ignoring repeat report from {1}", Describe(item), user.Name);
                await Notify(user, "Already on it", $"{Describe(item)} is already being replaced.", ct).ConfigureAwait(false);
                return;
            }
            try { await work().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.ErrorException("report of {0} failed", ex, Describe(item));
                await Flag(user, item, $"Automatic replacement failed: {ex.Message}", item is Episode, ct).ConfigureAwait(false);
            }
        }

        private bool OverLimit(User user, PluginConfiguration cfg)
        {
            if (cfg.DailyLimitPerUser <= 0 || (user.Policy?.IsAdministrator ?? false)) return false;
            var since = Now - 86400;
            var key = Norm(user.Id.ToString("N"));
            var used = cfg.Reports.Count(r => r.UserId == key && r.ReportedAtUnix >= since &&
                (r.Status == ReportStatus.Waiting || r.Status == ReportStatus.Replaced || r.Status == ReportStatus.GaveUp));
            return used >= cfg.DailyLimitPerUser;
        }

        private ProblemReport NewReport(User user, BaseItem item, string kind) => new ProblemReport
        {
            UserId = Norm(user.Id.ToString("N")),
            UserName = user.Name,
            Title = Describe(item),
            Kind = kind,
            EmbyItemId = item.InternalId,
            EmbyFolder = string.IsNullOrEmpty(item.Path) ? null : Path.GetDirectoryName(item.Path),
            ReportedAtUnix = Now
        };

        private static bool SameFile(string embyPath, string arrPath) =>
            !string.IsNullOrEmpty(embyPath) && !string.IsNullOrEmpty(arrPath) &&
            string.Equals(Path.GetFileName(embyPath.Replace('\\', '/')), Path.GetFileName(arrPath.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);

        /// <summary>The "grabbed" history entry for the download that produced this file, or null (manual import, history pruned).</summary>
        private static ArrHistory GrabFor(List<ArrHistory> history, int fileId)
        {
            var import = history.Where(h => h.eventType == "downloadFolderImported" && h.data != null &&
                                            h.data.TryGetValue("fileId", out var f) && f == fileId.ToString())
                                .OrderByDescending(h => h.date).FirstOrDefault();
            if (string.IsNullOrEmpty(import?.downloadId)) return null;
            return history.Where(h => h.eventType == "grabbed" && h.downloadId == import.downloadId)
                          .OrderByDescending(h => h.date).FirstOrDefault();
        }

        private async Task ReportMovie(User user, Movie movie, PluginConfiguration cfg, CancellationToken ct)
        {
            var r = NewReport(user, movie, "movie");
            RadarrMovie rm = null;
            if (int.TryParse(movie.GetProviderId(MetadataProviders.Tmdb), out var tmdb) && tmdb > 0)
                rm = await _radarr.FindByTmdb(tmdb, ct).ConfigureAwait(false);
            var imdb = movie.GetProviderId(MetadataProviders.Imdb);
            if (rm == null && !string.IsNullOrEmpty(imdb))
                rm = await _radarr.FindByImdb(imdb, ct).ConfigureAwait(false);

            if (rm == null) { await Flag(user, movie, "Couldn't find this movie in Radarr.", false, ct).ConfigureAwait(false); return; }
            if (!rm.hasFile || rm.movieFile == null) { await Flag(user, movie, "Radarr doesn't have a file for this movie.", false, ct).ConfigureAwait(false); return; }
            if (!SameFile(movie.Path, rm.movieFile.relativePath))
            {
                await Flag(user, movie, $"The file Emby played ({Path.GetFileName(movie.Path)}) isn't the one Radarr has ({Path.GetFileName(rm.movieFile.relativePath)}), so nothing was deleted.", false, ct).ConfigureAwait(false);
                return;
            }
            if (OverLimit(user, cfg)) { await Flag(user, movie, $"{user.Name} is over the daily limit of {cfg.DailyLimitPerUser} replacements.", false, ct).ConfigureAwait(false); return; }

            r.ArrItemId = rm.id;
            r.OldFileId = rm.movieFile.id;
            r.OldFileName = rm.movieFile.sceneName ?? Path.GetFileName(rm.movieFile.relativePath);
            var grab = GrabFor(await _radarr.History(rm.id, ct).ConfigureAwait(false), rm.movieFile.id);

            var tmdbForSeerr = rm.tmdbId > 0 ? rm.tmdbId : tmdb;
            r.SeerrIssueId = await OpenIssue(user, false, tmdbForSeerr, null, null,
                IssueText(user, r, grab != null), cfg, ct).ConfigureAwait(false);

            if (cfg.DryRun)
            {
                r.Status = ReportStatus.DryRun;
                r.Note = $"Would delete Radarr file {r.OldFileId}, {(grab != null ? $"blocklist \"{grab.sourceTitle}\"" : "not blocklist (no download history)")}, and search.";
                _log.Info("[dry run] {0}: {1}", r.Title, r.Note);
            }
            else
            {
                await _radarr.DeleteFile(r.OldFileId, ct).ConfigureAwait(false);
                if (grab != null)
                {
                    try { await _radarr.MarkFailed(grab.id, ct).ConfigureAwait(false); r.Blocklisted = true; }
                    catch (ApiException ex) { r.Note = $"Couldn't blocklist: {ex.Message}"; }
                }
                if (!rm.monitored) await _radarr.Monitor(rm.id, ct).ConfigureAwait(false);
                await _radarr.Search(rm.id, ct).ConfigureAwait(false);
                r.Status = ReportStatus.Waiting;
                _log.Info("{0}: deleted {1}{2}, searching for a replacement", r.Title, r.OldFileName, r.Blocklisted ? " and blocklisted it" : "");
            }
            Cfg.Reports.Add(r);
            await Notify(user, "Problem reported", $"Thanks. {r.Title} is being replaced; you'll get a Seerr notification when it's back.", ct).ConfigureAwait(false);
        }

        private async Task ReportEpisode(User user, Episode ep, PluginConfiguration cfg, CancellationToken ct)
        {
            var r = NewReport(user, ep, "episode");
            var series = ep.Series;
            SonarrSeries ss = null;
            if (series != null && int.TryParse(series.GetProviderId(MetadataProviders.Tvdb), out var tvdb) && tvdb > 0)
                ss = await _sonarr.FindByTvdb(tvdb, ct).ConfigureAwait(false);
            int.TryParse(series?.GetProviderId(MetadataProviders.Tmdb), out var stmdb);
            var simdb = series?.GetProviderId(MetadataProviders.Imdb);
            if (ss == null && (stmdb > 0 || !string.IsNullOrEmpty(simdb)))
                ss = await _sonarr.FindBy(s => (stmdb > 0 && s.tmdbId == stmdb) ||
                                               (!string.IsNullOrEmpty(simdb) && string.Equals(s.imdbId, simdb, StringComparison.OrdinalIgnoreCase)), ct).ConfigureAwait(false);
            if (ss == null) { await Flag(user, ep, "Couldn't find this show in Sonarr.", true, ct).ConfigureAwait(false); return; }

            if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue)
            { await Flag(user, ep, "Emby doesn't know this episode's season/episode number.", true, ct).ConfigureAwait(false); return; }

            var all = await _sonarr.Episodes(ss.id, ct).ConfigureAwait(false);
            var se = all.FirstOrDefault(e => e.seasonNumber == ep.ParentIndexNumber.Value && e.episodeNumber == ep.IndexNumber.Value);
            if (se == null || !se.hasFile || se.episodeFileId <= 0)
            { await Flag(user, ep, "Sonarr doesn't have a file for this episode.", true, ct).ConfigureAwait(false); return; }

            var file = await _sonarr.EpisodeFile(se.episodeFileId, ct).ConfigureAwait(false);
            if (!SameFile(ep.Path, file?.relativePath ?? file?.path))
            {
                await Flag(user, ep, $"The file Emby played ({Path.GetFileName(ep.Path)}) isn't the one Sonarr has ({Path.GetFileName(file?.relativePath ?? "")}), so nothing was deleted.", true, ct).ConfigureAwait(false);
                return;
            }
            if (OverLimit(user, cfg)) { await Flag(user, ep, $"{user.Name} is over the daily limit of {cfg.DailyLimitPerUser} replacements.", true, ct).ConfigureAwait(false); return; }

            // A multi-episode file covers several episodes; all of them need re-searching.
            var inFile = all.Where(e => e.episodeFileId == se.episodeFileId).Select(e => e.id).ToArray();
            r.ArrItemId = ss.id;
            r.EpisodeIds = inFile.Length > 0 ? inFile : new[] { se.id };
            r.OldFileId = se.episodeFileId;
            r.OldFileName = file.sceneName ?? Path.GetFileName(file.relativePath);
            var grab = GrabFor(await _sonarr.History(se.id, ct).ConfigureAwait(false), se.episodeFileId);

            r.SeerrIssueId = await OpenIssue(user, true, ss.tmdbId > 0 ? ss.tmdbId : stmdb, se.seasonNumber, se.episodeNumber,
                IssueText(user, r, grab != null), cfg, ct).ConfigureAwait(false);

            if (cfg.DryRun)
            {
                r.Status = ReportStatus.DryRun;
                r.Note = $"Would delete Sonarr file {r.OldFileId} ({r.EpisodeIds.Length} episode(s)), {(grab != null ? $"blocklist \"{grab.sourceTitle}\"" : "not blocklist (no download history)")}, and search.";
                _log.Info("[dry run] {0}: {1}", r.Title, r.Note);
            }
            else
            {
                await _sonarr.DeleteFile(r.OldFileId, ct).ConfigureAwait(false);
                if (grab != null)
                {
                    try { await _sonarr.MarkFailed(grab.id, ct).ConfigureAwait(false); r.Blocklisted = true; }
                    catch (ApiException ex) { r.Note = $"Couldn't blocklist: {ex.Message}"; }
                }
                await _sonarr.Monitor(r.EpisodeIds, ct).ConfigureAwait(false);
                await _sonarr.Search(r.EpisodeIds, ct).ConfigureAwait(false);
                r.Status = ReportStatus.Waiting;
                _log.Info("{0}: deleted {1}{2}, searching for a replacement", r.Title, r.OldFileName, r.Blocklisted ? " and blocklisted it" : "");
            }
            Cfg.Reports.Add(r);
            await Notify(user, "Problem reported", $"Thanks. {r.Title} is being replaced; you'll get a Seerr notification when it's back.", ct).ConfigureAwait(false);
        }

        private static string IssueText(User user, ProblemReport r, bool canBlocklist) =>
            $"Reported from Emby by {user.Name} (Report a Problem playlist).\nFile: {r.OldFileName}\n" +
            (Cfg.DryRun ? "[Dry run: nothing was changed.]" :
             $"Deleted it{(canBlocklist ? ", blocklisted the release" : "")} and started a search for a replacement. This issue resolves itself when the new file is in Emby.");

        /// <summary>Report something that won't be replaced automatically: a Seerr issue for the admin, nothing deleted.</summary>
        private async Task Flag(User user, BaseItem item, string reason, bool tv, CancellationToken ct)
        {
            var cfg = Cfg;
            var r = NewReport(user, item, tv ? "episode" : "movie");
            r.Status = ReportStatus.Flagged;
            r.Note = reason;

            int tmdb = 0; int? season = null, episode = null;
            BaseItem idSource = item;
            if (item is Episode ep) { idSource = ep.Series; season = ep.ParentIndexNumber; episode = ep.IndexNumber; }
            else if (item is Season s) { idSource = s.Series; season = s.IndexNumber; }
            int.TryParse(idSource?.GetProviderId(MetadataProviders.Tmdb), out tmdb);

            r.SeerrIssueId = await OpenIssue(user, tv, tmdb, season, episode,
                $"Reported from Emby by {user.Name} (Report a Problem playlist).\nFile: {Path.GetFileName(item.Path ?? "")}\nNeeds a look: {reason}", cfg, ct).ConfigureAwait(false);
            cfg.Reports.Add(r);
            _log.Warn("flagged {0} from {1}: {2}", r.Title, user.Name, reason);
            await Notify(user, "Problem reported", $"Thanks. {r.Title} has been passed on to be looked at.", ct).ConfigureAwait(false);
        }

        private async Task<int> OpenIssue(User user, bool tv, int tmdbId, int? season, int? episode, string message, PluginConfiguration cfg, CancellationToken ct)
        {
            if (!Seerr.Configured) return 0;
            try
            {
                var mediaId = await _seerr.MediaId(tv, tmdbId, ct).ConfigureAwait(false);
                if (mediaId == 0)
                {
                    _log.Warn("Seerr doesn't track {0} {1}; no issue opened", tv ? "show" : "movie", tmdbId);
                    return 0;
                }
                var seerrUser = await _seerr.UserFor(user.Id, ct).ConfigureAwait(false);
                if (seerrUser == 0) message = $"(Emby user {user.Name} isn't linked in Seerr.)\n" + message;
                if (cfg.DryRun)
                {
                    _log.Info("[dry run] would open Seerr issue on media {0} as Seerr user {1}{2}", mediaId,
                        seerrUser == 0 ? "admin" : seerrUser.ToString(), season.HasValue ? $" (S{season}E{episode})" : "");
                    return 0;
                }
                var id = await _seerr.CreateIssue(mediaId, seerrUser, message, season, episode, ct).ConfigureAwait(false);
                _log.Info("opened Seerr issue {0}", id);
                return id;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn("couldn't open Seerr issue: {0}", ex.Message);
                return 0;
            }
        }

        // ── Waiting for replacements ──────────────────────────────────────────────────────

        private async Task CheckPendingAsync(CancellationToken ct)
        {
            var cfg = Cfg;
            bool changed = false;
            foreach (var r in cfg.Reports.Where(x => x.Status == ReportStatus.Waiting).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string newName = null;
                    if (r.Kind == "movie")
                    {
                        var m = await _radarr.Get(r.ArrItemId, ct).ConfigureAwait(false);
                        if (m != null && m.hasFile && m.movieFile != null && m.movieFile.id != r.OldFileId)
                            newName = Path.GetFileName(m.movieFile.relativePath);
                    }
                    else if (r.EpisodeIds.Length > 0)
                    {
                        var e = await _sonarr.Episode(r.EpisodeIds[0], ct).ConfigureAwait(false);
                        if (e != null && e.hasFile && e.episodeFileId > 0 && e.episodeFileId != r.OldFileId)
                        {
                            var f = await _sonarr.EpisodeFile(e.episodeFileId, ct).ConfigureAwait(false);
                            newName = Path.GetFileName(f?.relativePath ?? "");
                        }
                    }

                    if (newName != null)
                    {
                        if (r.ReplacedSeenAtUnix == 0) { r.ReplacedSeenAtUnix = Now; r.NewRelease = newName; changed = true; }
                        var inEmby = InEmby(r, newName);
                        if (inEmby || Now - r.ReplacedSeenAtUnix > WaitForEmbyAfterImport.TotalSeconds)
                        {
                            await Finish(r, ct).ConfigureAwait(false);
                            changed = true;
                        }
                    }
                    else if (cfg.GiveUpAfterDays > 0 && Now - r.ReportedAtUnix > cfg.GiveUpAfterDays * 86400L)
                    {
                        r.Status = ReportStatus.GaveUp;
                        r.Note = $"No replacement after {cfg.GiveUpAfterDays} days.";
                        if (r.SeerrIssueId > 0)
                            await TrySeerr(() => _seerr.Comment(r.SeerrIssueId, $"No replacement found after {cfg.GiveUpAfterDays} days. Leaving this open for a manual look.", ct)).ConfigureAwait(false);
                        _log.Warn("gave up waiting for {0}", r.Title);
                        changed = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Warn("checking {0} failed: {1}", r.Title, ex.Message); }
            }

            if (cfg.Reports.Count > 300)
            {
                var keep = cfg.Reports.Where(x => x.Status == ReportStatus.Waiting)
                    .Concat(cfg.Reports.Where(x => x.Status != ReportStatus.Waiting).OrderByDescending(x => x.ReportedAtUnix).Take(250));
                cfg.Reports = keep.OrderBy(x => x.ReportedAtUnix).ToList();
                changed = true;
            }
            if (changed) Save();
        }

        /// <summary>Radarr/Sonarr rename within the same folder, so the new file should appear next to where the old one was.</summary>
        private bool InEmby(ProblemReport r, string fileName)
        {
            if (string.IsNullOrEmpty(r.EmbyFolder)) return false;
            var sep = r.EmbyFolder.Contains("\\") ? "\\" : "/";
            var item = _library.FindByPath(r.EmbyFolder.TrimEnd('/', '\\') + sep + fileName, false);
            return item != null;
        }

        private async Task Finish(ProblemReport r, CancellationToken ct)
        {
            r.Status = ReportStatus.Replaced;
            _log.Info("{0} replaced with {1}", r.Title, r.NewRelease);
            if (r.SeerrIssueId > 0)
            {
                await TrySeerr(() => _seerr.Comment(r.SeerrIssueId, $"Replaced with {r.NewRelease}. It's ready to watch in Emby.", ct)).ConfigureAwait(false);
                await TrySeerr(() => _seerr.Resolve(r.SeerrIssueId, ct)).ConfigureAwait(false);
            }
            var user = _users.GetUserList(new UserQuery()).FirstOrDefault(u => Norm(u.Id.ToString("N")) == r.UserId);
            if (user != null) await Notify(user, "Replacement ready", $"{r.Title} has been replaced and is ready to watch.", ct).ConfigureAwait(false);
        }

        private async Task TrySeerr(Func<Task> call)
        {
            try { await call().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Warn("Seerr call failed: {0}", ex.Message); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────

        private async Task Notify(User user, string header, string text, CancellationToken ct)
        {
            try
            {
                foreach (var s in _sessions.Sessions.Where(s => s.UserInternalId == user.InternalId && s.SupportsRemoteControl))
                {
                    await _sessions.SendMessageCommand(null, s.Id, new MessageCommand { Header = header, Text = text, TimeoutMs = 8000 }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _log.Debug("couldn't message {0}: {1}", user.Name, ex.Message); }
        }

        internal static string Describe(BaseItem i)
        {
            if (i is Episode e)
                return $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}";
            return i.ProductionYear.HasValue ? $"{i.Name} ({i.ProductionYear})" : i.Name;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _pollTimer?.Dispose();
            _pendingTimer?.Dispose();
        }
    }
}
