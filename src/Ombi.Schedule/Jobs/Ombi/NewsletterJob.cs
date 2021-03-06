﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ombi.Api.TheMovieDb;
using Ombi.Api.TheMovieDb.Models;
using Ombi.Api.TvMaze;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Notifications;
using Ombi.Notifications.Models;
using Ombi.Notifications.Templates;
using Ombi.Settings.Settings.Models;
using Ombi.Settings.Settings.Models.Notifications;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Schedule.Jobs.Ombi
{
    public class NewsletterJob : HtmlTemplateGenerator, INewsletterJob
    {
        public NewsletterJob(IPlexContentRepository plex, IEmbyContentRepository emby, IRepository<RecentlyAddedLog> addedLog,
            IMovieDbApi movieApi, ITvMazeApi tvApi, IEmailProvider email, ISettingsService<CustomizationSettings> custom,
            ISettingsService<EmailNotificationSettings> emailSettings, INotificationTemplatesRepository templateRepo,
            UserManager<OmbiUser> um, ISettingsService<NewsletterSettings> newsletter)
        {
            _plex = plex;
            _emby = emby;
            _recentlyAddedLog = addedLog;
            _movieApi = movieApi;
            _tvApi = tvApi;
            _email = email;
            _customizationSettings = custom;
            _templateRepo = templateRepo;
            _emailSettings = emailSettings;
            _newsletterSettings = newsletter;
            _userManager = um;
            _emailSettings.ClearCache();
            _customizationSettings.ClearCache();
            _newsletterSettings.ClearCache();
        }

        private readonly IPlexContentRepository _plex;
        private readonly IEmbyContentRepository _emby;
        private readonly IRepository<RecentlyAddedLog> _recentlyAddedLog;
        private readonly IMovieDbApi _movieApi;
        private readonly ITvMazeApi _tvApi;
        private readonly IEmailProvider _email;
        private readonly ISettingsService<CustomizationSettings> _customizationSettings;
        private readonly INotificationTemplatesRepository _templateRepo;
        private readonly ISettingsService<EmailNotificationSettings> _emailSettings;
        private readonly ISettingsService<NewsletterSettings> _newsletterSettings;
        private readonly UserManager<OmbiUser> _userManager;

        public async Task Start(NewsletterSettings settings, bool test)
        {
            if (!settings.Enabled)
            {
                return;
            }
            var template = await _templateRepo.GetTemplate(NotificationAgent.Email, NotificationType.Newsletter);
            if (!template.Enabled)
            {
                return;
            }

            var emailSettings = await _emailSettings.GetSettingsAsync();
            if (!ValidateConfiguration(emailSettings))
            {
                return;
            }

            var customization = await _customizationSettings.GetSettingsAsync();

            // Get the Content
            var plexContent = _plex.GetAll().Include(x => x.Episodes).AsNoTracking();
            var embyContent = _emby.GetAll().Include(x => x.Episodes).AsNoTracking();

            var addedLog = _recentlyAddedLog.GetAll();
            var addedPlexMovieLogIds = addedLog.Where(x => x.Type == RecentlyAddedType.Plex && x.ContentType == ContentType.Parent).Select(x => x.ContentId);
            var addedEmbyMoviesLogIds = addedLog.Where(x => x.Type == RecentlyAddedType.Emby && x.ContentType == ContentType.Parent).Select(x => x.ContentId);

            var addedPlexEpisodesLogIds = addedLog.Where(x => x.Type == RecentlyAddedType.Plex && x.ContentType == ContentType.Episode).Select(x => x.ContentId);
            var addedEmbyEpisodesLogIds = addedLog.Where(x => x.Type == RecentlyAddedType.Emby && x.ContentType == ContentType.Episode).Select(x => x.ContentId);

            // Filter out the ones that we haven't sent yet
            var plexContentMoviesToSend = plexContent.Where(x => x.Type == PlexMediaTypeEntity.Movie && !addedPlexMovieLogIds.Contains(x.Id));
            var embyContentMoviesToSend = embyContent.Where(x => x.Type == EmbyMediaType.Movie && !addedEmbyMoviesLogIds.Contains(x.Id));

            var plexEpisodesToSend = _plex.GetAllEpisodes().Include(x => x.Series).Where(x => !addedPlexEpisodesLogIds.Contains(x.Id)).AsNoTracking();
            var embyEpisodesToSend = _emby.GetAllEpisodes().Include(x => x.Series).Where(x => !addedEmbyEpisodesLogIds.Contains(x.Id)).AsNoTracking();

            var body = string.Empty;
            if (test)
            {
                var plexm = plexContent.Where(x => x.Type == PlexMediaTypeEntity.Movie).OrderByDescending(x => x.AddedAt).Take(10);
                var embym = embyContent.Where(x => x.Type == EmbyMediaType.Movie).OrderByDescending(x => x.AddedAt).Take(10);
                var plext = _plex.GetAllEpisodes().Include(x => x.Series).OrderByDescending(x => x.Series.AddedAt).Take(10);
                var embyt = _emby.GetAllEpisodes().Include(x => x.Series).OrderByDescending(x => x.AddedAt).Take(10);
                body = await BuildHtml(plexm, embym, plext, embyt);
            }
            else
            {
                body = await BuildHtml(plexContentMoviesToSend, embyContentMoviesToSend, plexEpisodesToSend, embyEpisodesToSend);
                if (body.IsNullOrEmpty())
                {
                    return;
                }

            }

            if (!test)
            {
                // Get the users to send it to
                var users = await _userManager.GetUsersInRoleAsync(OmbiRoles.RecievesNewsletter);
                if (!users.Any())
                {
                    return;
                }
                var emailTasks = new List<Task>();
                foreach (var user in users)
                {
                    if (user.Email.IsNullOrEmpty())
                    {
                        continue;
                    }

                    var messageContent = ParseTemplate(template, customization, user);
                    var email = new NewsletterTemplate();

                    var html = email.LoadTemplate(messageContent.Subject, messageContent.Message, body, customization.Logo);

                    emailTasks.Add(_email.Send(
                        new NotificationMessage { Message = html, Subject = messageContent.Subject, To = user.Email },
                        emailSettings));
                }

                // Now add all of this to the Recently Added log
                var recentlyAddedLog = new HashSet<RecentlyAddedLog>();
                foreach (var p in plexContentMoviesToSend)
                {
                    recentlyAddedLog.Add(new RecentlyAddedLog
                    {
                        AddedAt = DateTime.Now,
                        Type = RecentlyAddedType.Plex,
                        ContentType = ContentType.Parent,
                        ContentId = p.Id
                    });

                }

                foreach (var p in plexEpisodesToSend)
                {
                    recentlyAddedLog.Add(new RecentlyAddedLog
                    {
                        AddedAt = DateTime.Now,
                        Type = RecentlyAddedType.Plex,
                        ContentType = ContentType.Episode,
                        ContentId = p.Id
                    });
                }

                foreach (var e in embyContentMoviesToSend)
                {
                    if (e.Type == EmbyMediaType.Movie)
                    {
                        recentlyAddedLog.Add(new RecentlyAddedLog
                        {
                            AddedAt = DateTime.Now,
                            Type = RecentlyAddedType.Emby,
                            ContentType = ContentType.Parent,
                            ContentId = e.Id
                        });
                    }
                }

                foreach (var p in embyEpisodesToSend)
                {
                    recentlyAddedLog.Add(new RecentlyAddedLog
                    {
                        AddedAt = DateTime.Now,
                        Type = RecentlyAddedType.Emby,
                        ContentType = ContentType.Episode,
                        ContentId = p.Id
                    });
                }
                await _recentlyAddedLog.AddRange(recentlyAddedLog);

                await Task.WhenAll(emailTasks.ToArray());
            }
            else
            {
                var admins = await _userManager.GetUsersInRoleAsync(OmbiRoles.Admin);
                foreach (var a in admins)
                {
                    if (a.Email.IsNullOrEmpty())
                    {
                        continue;
                    }
                    var messageContent = ParseTemplate(template, customization, a);

                    var email = new NewsletterTemplate();

                    var html = email.LoadTemplate(messageContent.Subject, messageContent.Message, body, customization.Logo);

                    await _email.Send(
                        new NotificationMessage { Message = html, Subject = messageContent.Subject, To = a.Email },
                        emailSettings);
                }
            }
        }

        public async Task Start()
        {
            var newsletterSettings = await _newsletterSettings.GetSettingsAsync();
            await Start(newsletterSettings, false);
        }

        private NotificationMessageContent ParseTemplate(NotificationTemplates template, CustomizationSettings settings, OmbiUser username)
        {
            var resolver = new NotificationMessageResolver();
            var curlys = new NotificationMessageCurlys();

            curlys.SetupNewsletter(settings, username);

            return resolver.ParseMessage(template, curlys);
        }

        private async Task<string> BuildHtml(IQueryable<PlexServerContent> plexContentToSend, IQueryable<EmbyContent> embyContentToSend, IQueryable<PlexEpisode> plexEpisodes, IQueryable<EmbyEpisode> embyEp)
        {
            var sb = new StringBuilder();

            var plexMovies = plexContentToSend.Where(x => x.Type == PlexMediaTypeEntity.Movie);
            var embyMovies = embyContentToSend.Where(x => x.Type == EmbyMediaType.Movie);
            if (plexMovies.Any() || embyMovies.Any())
            {
                sb.Append("<h1>New Movies:</h1><br /><br />");
                await ProcessPlexMovies(plexMovies, sb);
                await ProcessEmbyMovies(embyMovies, sb);
            }

            if (plexEpisodes.Any() || embyEp.Any())
            {
                sb.Append("<h1>New Episodes:</h1><br /><br />");
                await ProcessPlexTv(plexEpisodes, sb);
                await ProcessEmbyTv(embyEp, sb);
            }

            return sb.ToString();
        }

        private async Task ProcessPlexMovies(IQueryable<PlexServerContent> plexContentToSend, StringBuilder sb)
        {
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            var ordered = plexContentToSend.OrderByDescending(x => x.AddedAt);
            foreach (var content in ordered)
            {
                if (content.TheMovieDbId.IsNullOrEmpty())
                {
                    // Maybe we should try the ImdbId?
                    if (content.ImdbId.HasValue())
                    {
                        var findResult = await _movieApi.Find(content.ImdbId, ExternalSource.imdb_id);

                        var movieId = findResult.movie_results?[0]?.id ?? 0;
                        content.TheMovieDbId = movieId.ToString();
                    }
                }

                int.TryParse(content.TheMovieDbId, out var movieDbId);
                var info = await _movieApi.GetMovieInformationWithExtraInfo(movieDbId);
                if (info == null)
                {
                    continue;
                }
                try
                {
                    CreateMovieHtmlContent(sb, info);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
                finally
                {
                    EndLoopHtml(sb);
                }
            }
        }

        private async Task ProcessEmbyMovies(IQueryable<EmbyContent> embyContent, StringBuilder sb)
        {
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            var ordered = embyContent.OrderByDescending(x => x.AddedAt);
            foreach (var content in ordered)
            {
                int.TryParse(content.ProviderId, out var movieDbId);
                var info = await _movieApi.GetMovieInformationWithExtraInfo(movieDbId);
                if (info == null)
                {
                    continue;
                }
                try
                {
                    CreateMovieHtmlContent(sb, info);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
                finally
                {
                    EndLoopHtml(sb);
                }
            }
        }

        private void CreateMovieHtmlContent(StringBuilder sb, MovieResponseDto info)
        {
            AddImageInsideTable(sb, $"https://image.tmdb.org/t/p/original{info.PosterPath}");

            sb.Append("<tr>");
            TableData(sb);

            Href(sb, $"https://www.imdb.com/title/{info.ImdbId}/");
            var releaseDate = string.Empty;
            try
            {
                releaseDate = $"({DateTime.Parse(info.ReleaseDate).Year})";
            }
            catch (Exception)
            {
                // Swallow, couldn't parse the date
            }
            Header(sb, 3, $"{info.Title} {releaseDate}");
            EndTag(sb, "a");

            if (info.Genres.Any())
            {
                AddParagraph(sb,
                    $"Genre: {string.Join(", ", info.Genres.Select(x => x.Name.ToString()).ToArray())}");
            }

            AddParagraph(sb, info.Overview);
        }

        private async Task ProcessPlexTv(IQueryable<PlexEpisode> plexContent, StringBuilder sb)
        {
            var series = new List<PlexServerContent>();
            foreach (var plexEpisode in plexContent)
            {
                var alreadyAdded = series.FirstOrDefault(x => x.Key == plexEpisode.Series.Key);
                if (alreadyAdded != null)
                {
                    var episodeExists = alreadyAdded.Episodes.Any(x => x.Key == plexEpisode.Key);
                    if (!episodeExists)
                    {
                        alreadyAdded.Episodes.Add(plexEpisode);
                    }
                }
                else
                {
                    plexEpisode.Series.Episodes = new List<PlexEpisode> { plexEpisode };
                    series.Add(plexEpisode.Series);
                }
            }

            var orderedTv = series.OrderByDescending(x => x.AddedAt);
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            foreach (var t in orderedTv)
            {
                try
                {
                    if (!t.HasTvDb)
                    {
                        // We may need to use themoviedb for the imdbid or their own id to get info
                        if (t.HasTheMovieDb)
                        {
                            int.TryParse(t.TheMovieDbId, out var movieId);
                            var externals = await _movieApi.GetTvExternals(movieId);
                            if (externals == null || externals.tvdb_id <= 0)
                            {
                                continue;
                            }
                            t.TvDbId = externals.tvdb_id.ToString();
                        }
                        // WE could check the below but we need to get the moviedb and then perform the above, let the metadata job figure this out.
                        //else if(t.HasImdb)
                        //{
                        //    // Check the imdbid
                        //    var externals = await _movieApi.Find(t.ImdbId, ExternalSource.imdb_id);
                        //    if (externals?.tv_results == null || externals.tv_results.Length <= 0)
                        //    {
                        //        continue;
                        //    }
                        //    t.TvDbId = externals.tv_results.FirstOrDefault()..ToString();
                        //}

                    }

                    int.TryParse(t.TvDbId, out var tvdbId);
                    var info = await _tvApi.ShowLookupByTheTvDbId(tvdbId);
                    if (info == null)
                    {
                        continue;
                    }
                    var banner = info.image?.original;
                    if (!string.IsNullOrEmpty(banner))
                    {
                        banner = banner.Replace("http", "https"); // Always use the Https banners
                    }
                    AddImageInsideTable(sb, banner);

                    sb.Append("<tr>");
                    sb.Append(
                        "<td align=\"center\" style=\"font-family: sans-serif; font-size: 14px; vertical-align: top;\" valign=\"top\">");
                    
                    var title = $"{t.Title} ({t.ReleaseYear})";

                    Href(sb, $"https://www.imdb.com/title/{info.externals.imdb}/");
                    Header(sb, 3, title);
                    EndTag(sb, "a");

                    // Group by the season number
                    var results = t.Episodes.GroupBy(p => p.SeasonNumber,
                        (key, g) => new
                        {
                            SeasonNumber = key,
                            Episodes = g.ToList()
                        }
                    );

                    // Group the episodes
                    foreach (var epInformation in results.OrderBy(x => x.SeasonNumber))
                    {
                        var orderedEpisodes = epInformation.Episodes.OrderBy(x => x.EpisodeNumber).ToList();
                        var epSb = new StringBuilder();
                        for (var i = 0; i < orderedEpisodes.Count; i++)
                        {
                            var ep = orderedEpisodes[i];
                            if (i < orderedEpisodes.Count - 1)
                            {
                                epSb.Append($"{ep.EpisodeNumber},");
                            }
                            else
                            {
                                epSb.Append($"{ep.EpisodeNumber}");
                            }

                        }
                        AddParagraph(sb, $"Season: {epInformation.SeasonNumber}, Episode: {epSb}");
                    }

                    if (info.genres.Any())
                    {
                        AddParagraph(sb, $"Genre: {string.Join(", ", info.genres.Select(x => x.ToString()).ToArray())}");
                    }

                    AddParagraph(sb, info.summary);
                }
                catch (Exception e)
                {
                    //Log.Error(e);
                }
                finally
                {
                    EndLoopHtml(sb);
                }
            }
            sb.Append("</table><br /><br />");

        }

        private async Task ProcessEmbyTv(IQueryable<EmbyEpisode> embyContent, StringBuilder sb)
        {
            var series = new List<EmbyContent>();
            foreach (var episode in embyContent)
            {
                var alreadyAdded = series.FirstOrDefault(x => x.EmbyId == episode.Series.EmbyId);
                if (alreadyAdded != null)
                {
                    alreadyAdded.Episodes.Add(episode);
                }
                else
                {
                    episode.Series.Episodes = new List<EmbyEpisode>
                    {
                        episode
                    };
                    series.Add(episode.Series);
                }
            }
            var orderedTv = series.OrderByDescending(x => x.AddedAt);
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            foreach (var t in orderedTv)
            {
                try
                {
                    int.TryParse(t.ProviderId, out var tvdbId);
                    var info = await _tvApi.ShowLookupByTheTvDbId(tvdbId);
                    if (info == null)
                    {
                        continue;
                    }
                    var banner = info.image?.original;
                    if (!string.IsNullOrEmpty(banner))
                    {
                        banner = banner.Replace("http", "https"); // Always use the Https banners
                    }
                    AddImageInsideTable(sb, banner);

                    sb.Append("<tr>");
                    sb.Append(
                        "<td align=\"center\" style=\"font-family: sans-serif; font-size: 14px; vertical-align: top;\" valign=\"top\">");

                    Href(sb, $"https://www.imdb.com/title/{info.externals.imdb}/");
                    Header(sb, 3, t.Title);
                    EndTag(sb, "a");

                    // Group by the season number
                    var results = t.Episodes?.GroupBy(p => p.SeasonNumber,
                        (key, g) => new
                        {
                            SeasonNumber = key,
                            Episodes = g.ToList()
                        }
                    );

                    // Group the episodes
                    foreach (var epInformation in results.OrderBy(x => x.SeasonNumber))
                    {
                        var orderedEpisodes = epInformation.Episodes.OrderBy(x => x.EpisodeNumber).ToList();
                        var epSb = new StringBuilder();
                        for (var i = 0; i < orderedEpisodes.Count; i++)
                        {
                            var ep = orderedEpisodes[i];
                            if (i < orderedEpisodes.Count - 1)
                            {
                                epSb.Append($"{ep.EpisodeNumber},");
                            }
                            else
                            {
                                epSb.Append($"{ep.EpisodeNumber}");
                            }

                        }
                        AddParagraph(sb, $"Season: {epInformation.SeasonNumber}, Episode: {epSb}");
                    }

                    if (info.genres.Any())
                    {
                        AddParagraph(sb, $"Genre: {string.Join(", ", info.genres.Select(x => x.ToString()).ToArray())}");
                    }

                    AddParagraph(sb, info.summary);
                }
                catch (Exception e)
                {
                    //Log.Error(e);
                }
                finally
                {
                    EndLoopHtml(sb);
                }
            }
            sb.Append("</table><br /><br />");
        }

        private void EndLoopHtml(StringBuilder sb)
        {
            //NOTE: BR have to be in TD's as per html spec or it will be put outside of the table...
            //Source: http://stackoverflow.com/questions/6588638/phantom-br-tag-rendered-by-browsers-prior-to-table-tag
            sb.Append("<hr />");
            sb.Append("<br />");
            sb.Append("<br />");
            sb.Append("</td>");
            sb.Append("</tr>");
        }

        protected bool ValidateConfiguration(EmailNotificationSettings settings)
        {
            if (!settings.Enabled)
            {
                return false;
            }
            if (settings.Authentication)
            {
                if (string.IsNullOrEmpty(settings.Username) || string.IsNullOrEmpty(settings.Password))
                {
                    return false;
                }
            }
            if (string.IsNullOrEmpty(settings.Host) || string.IsNullOrEmpty(settings.AdminEmail) || string.IsNullOrEmpty(settings.Port.ToString()))
            {
                return false;
            }

            return true;
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _plex?.Dispose();
                _emby?.Dispose();
                _newsletterSettings?.Dispose();
                _customizationSettings?.Dispose();
                _emailSettings.Dispose();
                _recentlyAddedLog.Dispose();
                _templateRepo?.Dispose();
                _userManager?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}