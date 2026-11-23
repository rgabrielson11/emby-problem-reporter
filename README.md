# Problem Reporter — Emby plugin

Lets any Emby user report a bad movie or episode from any Emby app, and gets it replaced automatically.

Emby plugins can't add buttons to Emby's apps, so reporting goes through something every app already has: **Add to Playlist**.

## How users report

Every user gets a private playlist called **Report a Problem**. To report something, open the movie or episode, then ⋯ → **Add to Playlist** → **Report a Problem**.

Within about 15 seconds the plugin takes the item back out of the playlist and handles it. If the user is watching in an app that shows server messages, they get a short "Problem reported" message.

## Telling users about it

Once dry run is off, each user gets a one-time message the next time they open an Emby app that can show server messages (most apps can) and nothing is playing. It explains how to report, with examples:

- picture freezing, stuttering or low quality
- audio out of sync, missing, crackly or in the wrong language
- the wrong movie or episode, or one that cuts off early
- something that won't play

You can edit the wording on the settings page, turn the message off, or use **Show it again to everyone**. Each user sees it once. The settings page shows how many users have seen it so far.

## What happens to a report

For a **movie** (Radarr) or **single episode** (Sonarr):

1. **Seerr issue.** A Video issue is opened in Seerr as the Seerr user linked to that Emby account. If the account isn't linked, it's opened as the admin, with the Emby user's name in the text.
2. **Blocklist.** The download that produced the current file is marked failed, so Radarr/Sonarr blocklist that release and won't grab it again. Files that were imported by hand have no download to blocklist, so this step is skipped for them.
3. **Delete and search.** The file is deleted (it goes to Radarr/Sonarr's recycle bin if one is set), the movie or episode is set to monitored, and a search starts.
4. **Resolve.** Every 5 minutes the plugin checks for a new file. Once the new file is in Emby (or an hour after Radarr/Sonarr imports it), the plugin comments on the Seerr issue with the new release name and resolves it. Seerr then notifies the user.

For a multi-episode file, every episode in that file is re-searched.

## Safety checks

These open a Seerr issue for you to look at **instead of** deleting anything:

- More than 3 episodes of one show reported at once (a whole season or series).
- A season or series added to the playlist as a whole.
- The movie or show can't be found in Radarr/Sonarr, or it has no file there.
- The file Emby has isn't the file Radarr/Sonarr has (compared by file name).
- A non-admin user has already had 5 replacements in the last 24 hours.
- Anything going wrong partway through.

Other checks:

- Reporting something that's already being replaced does nothing except tell the user.
- If no replacement turns up after 14 days, the plugin comments on the Seerr issue and leaves it open.

## Settings

Dashboard → Plugins → Problem Reporter:

| Setting | Default |
|---|---|
| Dry run (log only; playlists still made and emptied; no announcement) | **on** |
| One-time announcement to each user (title and text editable) | on |
| Playlist name | Report a Problem |
| Users who don't get a playlist | none |
| Replacements per non-admin user per day (0 = no limit) | 5 |
| Most episodes of one show replaced from one report | 3 |
| Stop waiting for a replacement after | 14 days |
| Radarr / Sonarr / Seerr URL and API key | — |

The settings page also lists the last 50 reports with their status: Waiting, Replaced, Flagged, Gave up or Dry run.

Logs go to the Emby server log, prefixed `ProblemReporter:` (the logger name).

## Building

Same as the other plugins: copy Emby's assemblies out of the running container, then build with the .NET SDK container.

```sh
mkdir -p lib
for d in MediaBrowser.Common MediaBrowser.Controller MediaBrowser.Model Emby.Media.Model; do
  docker cp EmbyServer:/system/$d.dll lib/
done

docker run --rm -v "$PWD":/work -w /work/ProblemReporter \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -o /work/out
```

Copy `out/ProblemReporter.dll` into Emby's `plugins/` folder and restart Emby.

Built against Emby Server 4.10 and Seerr 3.5. Filing issues as another user needs the Seerr admin API key.
