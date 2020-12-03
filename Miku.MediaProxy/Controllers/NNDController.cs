﻿using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NNDD.Entities;
using NNDD.Entities.ResultEntities;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Serialization;

namespace NNDD.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class NNDController : ControllerBase
    {
        private readonly ILogger<NNDController> _logger;
        private readonly HttpClientManager _httpClientManager;

        //Taken from youtube-dl (and converted vor dotnet regex)
        private readonly Regex _nndRegex =
            new Regex("https?://(?:www\\.|secure\\.|sp\\.)?nicovideo\\.jp/watch/(?<id>(?:[a-z]{2})?[0-9]+)");

        public NNDController(ILogger<NNDController> logger, HttpClientManager httpClientManager)
        {
            this._logger = logger;
            this._httpClientManager = httpClientManager;
        }

        [Route("{id}/info")]
        public async Task<TrackInfo> GetNNDVideoInfoAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("no video ID specified");
            var c = _httpClientManager.GetManagedHttpClient();
            TrackInfo info = default;
            try
            {
                var watchData = await GetWatchDataAsync(c, id);

                info = new TrackInfo
                {
                    Author = watchData.OwnerField.Nickname,
                    AuthorUrl = new Uri($"https://www.nicovideo.jp/user/{watchData.OwnerField.Id}"),
                    AuthorIconUrl = watchData.OwnerField.IconUrl,
                    DirectUrl = new Uri($"https://{this.HttpContext.Request.Host.Value}/nnd/{id}"),
                    IsLive = id.StartsWith("lv"),
                    Length = watchData.Video.Duration.HasValue ? watchData.Video.Duration.Value : 0,
                    ThumbnailUrl = watchData.Video.LargeThumbnailUrl,
                    Title = watchData.Video.Title,
                    TrackUrl = new Uri($"https://www.nicovideo.jp/watch/{id}"),
                    UploadDate = DateTimeOffset.Parse(watchData.Video.PostedDateTime)
                };
            }
            finally
            {
                c.ChangeStatus();
            }
            return info;
        }

        [Route("{id}")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task GetNNDVideoAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("no video ID specified");
            var c = _httpClientManager.GetManagedHttpClient();

            await DoLoginAsync(c, "EMAIL/TEL", "PASSWORD");

            try
            {
                long? readBytes = 0;
                while (true)
                {
                    var watchData = await GetWatchDataAsync(c, id);
                    var videoSession = await GetVideoSessionAsync(c, watchData);

                    if (readBytes != 0)
                        c.Client.DefaultRequestHeaders.Range = new RangeHeaderValue(readBytes, null);

                    var videoStream = await c.Client.GetAsync(videoSession.DataField.Session.ContentUri, HttpCompletionOption.ResponseHeadersRead);

                    if (HttpContext.Response.ContentType != videoStream.Content.Headers.ContentType.MediaType)
                        HttpContext.Response.ContentType = videoStream.Content.Headers.ContentType.MediaType;

                    readBytes = await WriteToOutputAsync(videoStream, HttpContext.Response.Body, readBytes);

                    if (readBytes == null
                        || (videoStream.Content.Headers.ContentRange != null
                            && videoStream.Content.Headers.ContentRange?.Length == readBytes)
                        || videoStream.Content.Headers.ContentLength == readBytes)
                    {
                        _logger.LogInformation("Finished proxy for " + id);
                        c.ChangeStatus();
                        return;
                    }
                }
            }
            finally
            {
                c.ChangeStatus();   
            }
            
        }

        private async Task<long?> WriteToOutputAsync(HttpResponseMessage c, Stream outputStream, long? readBytes = 0)
        {
            try
            {
                using (var stream = await c.Content.ReadAsStreamAsync())
                {
                    int read;
                    byte[] buffer = new byte[8192];
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await outputStream.WriteAsync(buffer, 0, read);
                        readBytes += Convert.ToInt64(read);
                    }
                }
            }
            catch (IOException ex) 
            {
                _logger.LogInformation("Expected NND I/O Error:\n" + ex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Unexpected error:\n" + ex);
                return null; 
            }
            return readBytes;
        }

        private async Task<VideoAPIData> GetVideoSessionAsync(ManagedHttpClient c, WatchData watchData)
        {
            var session = new SessionJson(watchData, watchData.Video.DmcInfo.SessionApi.Audios.First(), watchData.Video.DmcInfo.SessionApi.Videos.Last());
            var sessionTxt = JsonSerializer.Serialize(session);
            var url = watchData.Video.DmcInfo.SessionApi.Urls.First().UrlField + "?_format=json";
            var vidData = new StringContent(sessionTxt, Encoding.UTF8, "application/json");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = vidData;
                var resp = await c.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                return await JsonSerializer.DeserializeAsync<VideoAPIData>(await resp.Content.ReadAsStreamAsync());
            }
        }

        private async Task<WatchData> GetWatchDataAsync(ManagedHttpClient usedClient, string url)
        {
            var apiData = await usedClient.Client.GetStringAsync($"https://www.nicovideo.jp/watch/{url}");
            var doc = new HtmlDocument();
            doc.LoadHtml(apiData);
            var element = doc.DocumentNode.SelectSingleNode("//*[@id='js-initial-watch-data']");
            var json = element.Attributes.FirstOrDefault(x => x.Name == "data-api-data");
            return JsonSerializer.Deserialize<WatchData>(HttpUtility.HtmlDecode(json.Value));
        }

        private async Task DoLoginAsync(ManagedHttpClient usedClient, string email, string password)
        {
            var sb = new StringContent($"mail={email}&password={password}&site=nicometro");
            sb.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            var res = await usedClient.Client.PostAsync("https://account.nicovideo.jp/login/redirector", sb);
            var login = await res.Content.ReadAsStringAsync();
            var reader = new StringReader(login);
            var serializer = new XmlSerializer(typeof(LoginData.Nicovideo_user_response));
            var loginobj = (serializer.Deserialize(reader)) as LoginData.Nicovideo_user_response;
            var cookie = new Cookie("user_session", loginobj.Session_key, "/", "nicovideo.jp");
            usedClient.CookieContainer.Add(new Uri("http://api.ce.nicovideo.jp"), cookie);
        }
    }
}