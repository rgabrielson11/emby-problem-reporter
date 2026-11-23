define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'b7e3c2a4-6f1d-4e8a-9c55-2d7f0a9e41c3';
    // Matches the ReportStatus enum (serialized as a number or a name depending on the server).
    var statusNames = ['Waiting', 'Replaced', 'Flagged', 'Gave up', 'Dry run', 'Failed'];

    function norm(id) { return (id || '').replace(/-/g, '').toLowerCase(); }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]; });
    }

    function num(page, sel, fallback) {
        var v = parseInt(page.querySelector(sel).value, 10);
        return isNaN(v) ? fallback : v;
    }

    function statusName(s) {
        if (typeof s === 'number') return statusNames[s] || s;
        return s === 'GaveUp' ? 'Gave up' : s === 'DryRun' ? 'Dry run' : s;
    }

    function renderReports(page, config) {
        var list = (config.Reports || []).slice().sort(function (a, b) { return b.ReportedAtUnix - a.ReportedAtUnix; }).slice(0, 50);
        var el = page.querySelector('#reportList');
        if (!list.length) { el.innerHTML = '<div class="fieldDescription">None yet.</div>'; return; }
        el.innerHTML = '<table style="width:100%;border-collapse:collapse;">' +
            '<tr style="text-align:left;"><th>When</th><th>Who</th><th>What</th><th>Status</th><th>Details</th></tr>' +
            list.map(function (r) {
                var when = r.ReportedAtUnix ? new Date(r.ReportedAtUnix * 1000).toLocaleString() : '';
                var detail = [r.Note, r.NewRelease ? 'New: ' + r.NewRelease : '', r.SeerrIssueId ? 'Seerr issue #' + r.SeerrIssueId : '']
                    .filter(function (x) { return x; }).join(' · ');
                return '<tr style="border-top:1px solid rgba(128,128,128,.3);vertical-align:top;">' +
                    '<td style="padding:.3em .6em .3em 0;white-space:nowrap;">' + escapeHtml(when) + '</td>' +
                    '<td style="padding:.3em .6em .3em 0;">' + escapeHtml(r.UserName) + '</td>' +
                    '<td style="padding:.3em .6em .3em 0;">' + escapeHtml(r.Title) + '</td>' +
                    '<td style="padding:.3em .6em .3em 0;white-space:nowrap;">' + escapeHtml(statusName(r.Status)) + '</td>' +
                    '<td style="padding:.3em 0;" class="fieldDescription">' + escapeHtml(detail) + '</td></tr>';
            }).join('') + '</table>';
    }

    function loadPage(page, config, users) {
        renderReports(page, config);
        var excluded = (config.ExcludedUserIds || []).map(norm);
        page.querySelector('#userList').innerHTML = users.map(function (u) {
            var checked = excluded.indexOf(norm(u.Id)) >= 0 ? ' checked' : '';
            return '<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="chkUser" data-id="' + u.Id + '"' + checked + ' /><span>' + escapeHtml(u.Name) + '</span></label></div>';
        }).join('');

        page.querySelector('#chkDryRun').checked = config.DryRun !== false;
        page.querySelector('#txtPlaylistName').value = config.PlaylistName || 'Report a Problem';
        page.querySelector('#numLimit').value = config.DailyLimitPerUser;
        page.querySelector('#numMaxEps').value = config.MaxEpisodesPerReport;
        page.querySelector('#numGiveUp').value = config.GiveUpAfterDays;
        page.querySelector('#txtRadarrUrl').value = config.RadarrUrl || '';
        page.querySelector('#txtRadarrKey').value = config.RadarrApiKey || '';
        page.querySelector('#txtSonarrUrl').value = config.SonarrUrl || '';
        page.querySelector('#txtSonarrKey').value = config.SonarrApiKey || '';
        page.querySelector('#txtSeerrUrl').value = config.SeerrUrl || '';
        page.querySelector('#txtSeerrKey').value = config.SeerrApiKey || '';
        loading.hide();
    }

    function onSubmit(e) {
        e.preventDefault();
        loading.show();
        var form = this;
        // Re-read first so reports logged since the page opened aren't overwritten.
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DryRun = form.querySelector('#chkDryRun').checked;
            config.PlaylistName = form.querySelector('#txtPlaylistName').value.trim() || 'Report a Problem';
            config.ExcludedUserIds = Array.prototype.slice.call(form.querySelectorAll('.chkUser'))
                .filter(function (c) { return c.checked; })
                .map(function (c) { return norm(c.getAttribute('data-id')); });
            config.DailyLimitPerUser = Math.max(0, num(form, '#numLimit', 5));
            config.MaxEpisodesPerReport = Math.max(1, num(form, '#numMaxEps', 3));
            config.GiveUpAfterDays = Math.max(0, num(form, '#numGiveUp', 14));
            config.RadarrUrl = form.querySelector('#txtRadarrUrl').value.trim();
            config.RadarrApiKey = form.querySelector('#txtRadarrKey').value.trim();
            config.SonarrUrl = form.querySelector('#txtSonarrUrl').value.trim();
            config.SonarrApiKey = form.querySelector('#txtSonarrKey').value.trim();
            config.SeerrUrl = form.querySelector('#txtSeerrUrl').value.trim();
            config.SeerrApiKey = form.querySelector('#txtSeerrKey').value.trim();
            ApiClient.updatePluginConfiguration(pluginId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('form').addEventListener('submit', onSubmit);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loading.show();
        var page = this.view;
        Promise.all([ApiClient.getPluginConfiguration(pluginId), ApiClient.getUsers()]).then(function (r) {
            loadPage(page, r[0], r[1] || []);
        });
    };

    return View;
});
