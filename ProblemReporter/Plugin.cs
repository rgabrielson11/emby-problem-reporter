using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace ProblemReporter
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Log what would happen (Seerr issue, blocklist, delete, search) without changing anything. Playlists are still created and emptied.</summary>
        public bool DryRun { get; set; } = true;

        public string PlaylistName { get; set; } = "Report a Problem";
        /// <summary>Emby user IDs (Guid "N") who don't get a playlist.</summary>
        public string[] ExcludedUserIds { get; set; } = Array.Empty<string>();

        public string RadarrUrl { get; set; } = "";
        public string RadarrApiKey { get; set; } = "";
        public string SonarrUrl { get; set; } = "";
        public string SonarrApiKey { get; set; } = "";
        public string SeerrUrl { get; set; } = "";
        public string SeerrApiKey { get; set; } = "";

        /// <summary>Replacements per non-admin user per 24 hours. Reports past this still open a Seerr issue but don't replace anything. 0 = unlimited.</summary>
        public int DailyLimitPerUser { get; set; } = 5;
        /// <summary>More episodes than this from one show in a single report (a whole season or series) are flagged instead of replaced.</summary>
        public int MaxEpisodesPerReport { get; set; } = 3;
        /// <summary>Stop waiting for a replacement after this many days; the Seerr issue stays open.</summary>
        public int GiveUpAfterDays { get; set; } = 14;

        /// <summary>Show each user a one-time message about the playlist the next time they open an Emby app (only when dry run is off).</summary>
        public bool AnnounceEnabled { get; set; } = true;
        public string AnnouncementHeader { get; set; } = "New: Report a Problem";
        public string AnnouncementText { get; set; } = DefaultAnnouncement;
        /// <summary>Users who have already been shown the announcement.</summary>
        public string[] AnnouncedUserIds { get; set; } = Array.Empty<string>();

        public const string DefaultAnnouncement =
            "If a movie or episode has a problem, you can now get it fixed yourself.\n\n" +
            "Open it, then choose \u22EF (More) \u2192 Add to Playlist \u2192 Report a Problem.\n\n" +
            "Use it when, for example:\n" +
            "\u2022 The picture freezes, stutters, breaks up or looks low quality\n" +
            "\u2022 The audio is out of sync, missing, crackly or in the wrong language\n" +
            "\u2022 It's the wrong movie or episode, or it cuts off early\n" +
            "\u2022 It won't play at all\n\n" +
            "It leaves the playlist within a minute and a fresh copy downloads automatically, usually within a few hours. " +
            "Please report single movies or episodes, not whole seasons.";

        public List<ProblemReport> Reports { get; set; } = new List<ProblemReport>();
    }

    public enum ReportStatus { Waiting, Replaced, Flagged, GaveUp, DryRun, Failed }

    public class ProblemReport
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string Title { get; set; }
        /// <summary>"movie" or "episode".</summary>
        public string Kind { get; set; }
        public long EmbyItemId { get; set; }
        public string EmbyFolder { get; set; }
        public int ArrItemId { get; set; }          // Radarr movie id or Sonarr series id
        public int[] EpisodeIds { get; set; } = Array.Empty<int>();
        public int OldFileId { get; set; }
        public string OldFileName { get; set; }
        public bool Blocklisted { get; set; }
        public int SeerrIssueId { get; set; }
        public long ReportedAtUnix { get; set; }
        public long ReplacedSeenAtUnix { get; set; }
        public string NewRelease { get; set; }
        public ReportStatus Status { get; set; }
        public string Note { get; set; }
    }

    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Problem Reporter";
        public override string Description => "Users add a bad movie or episode to their 'Report a Problem' playlist; it opens a Seerr issue, blocklists the release in Radarr/Sonarr and downloads a replacement.";
        public override Guid Id => new Guid("b7e3c2a4-6f1d-4e8a-9c55-2d7f0a9e41c3");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "problemreporter",
                    EmbeddedResourcePath = "ProblemReporter.Configuration.config.html",
                    MenuSection = "server",
                    MenuIcon = "report_problem"
                },
                new PluginPageInfo
                {
                    Name = "problemreporterjs",
                    EmbeddedResourcePath = "ProblemReporter.Configuration.config.js"
                }
            };
        }
    }
}
