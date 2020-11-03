﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using F23.StringSimilarity;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Wabbajack.Common;
using Wabbajack.Lib.Validation;

namespace Wabbajack.Lib.Downloaders
{
    // IPS4 is the site used by LoversLab, VectorPlexus, etc. the general mechanics of each site are the 
    // same, so we can fairly easily abstract the state.
    // Pass in the state type via TState
    public abstract class AbstractIPS4Downloader<TDownloader, TState> : AbstractNeedsLoginDownloader, IDownloader
        where TState : AbstractIPS4Downloader<TDownloader, TState>.State<TDownloader>, new()
        where TDownloader : IDownloader
    {
        protected AbstractIPS4Downloader(Uri loginUri, string encryptedKeyName, string cookieDomain, string loginCookie = "ips4_member_id")
            : base(loginUri, encryptedKeyName, cookieDomain, loginCookie)
        {
        }

        public async Task<AbstractDownloadState?> GetDownloaderState(dynamic archiveINI, bool quickMode)
        { 
            Uri url = DownloaderUtils.GetDirectURL(archiveINI);
            return await GetDownloaderStateFromUrl(url, quickMode);
        }

        public async Task<AbstractDownloadState?> GetDownloaderStateFromUrl(Uri url, bool quickMode)
        {
            var absolute = true;
            if (url == null || url.Host != SiteURL.Host) return null;

            if (url.PathAndQuery.StartsWith("/applications/core/interface/file/attachment"))
            {
                return new TState
                {
                    IsAttachment = true,
                    FullURL = url.ToString()
                };
            }

            if (url.PathAndQuery.StartsWith("/index.php?"))
            {
                var id2 = HttpUtility.ParseQueryString(url.Query)["r"];
                var parsed = HttpUtility.ParseQueryString(url.Query);
                var name = parsed[null].Split("/", StringSplitOptions.RemoveEmptyEntries).Last();
                return new TState
                {
                    FullURL = url.AbsolutePath,
                    FileID = id2,
                    FileName = name
                };
            }

            if (url.PathAndQuery.StartsWith("/files/getdownload"))
            {
                return new TState
                {
                    FullURL = url.ToString(), 
                    IsAttachment = true
                };
            }
           
            if (!url.PathAndQuery.StartsWith("/files/file/"))
            {
                if (string.IsNullOrWhiteSpace(url.Query)) return null;
                if (!url.Query.Substring(1).StartsWith("/files/file/")) return null;
                absolute = false;
            }

            var id = HttpUtility.ParseQueryString(url.Query)["r"];
            var file = absolute
                ? url.AbsolutePath.Split('/').Last(s => s != "")
                : url.Query.Substring(1).Split('/').Last(s => s != "");
            
            return new TState
            {
                FullURL = url.AbsolutePath,
                FileID = id,
                FileName = file
            };
        }
        
        public class State<TStateDownloader> : AbstractDownloadState, IMetaState 
            where TStateDownloader : IDownloader
        {
            public string FullURL { get; set; } = string.Empty;
            public bool IsAttachment { get; set; }
            public string FileID { get; set; } = string.Empty;

            public string FileName { get; set; } = string.Empty;
            
            // from IMetaState
            public Uri URL => IsAttachment ? new Uri("https://www.wabbajack.org/") : new Uri($"{Site}/files/file/{FileName}");
            public string? Name { get; set; }
            public string? Author { get; set; }
            public string? Version { get; set; }
            public Uri? ImageURL { get; set; }
            public virtual bool IsNSFW { get; set; }
            public string? Description { get; set; }

            private static bool IsHTTPS => Downloader.SiteURL.AbsolutePath.StartsWith("https://");
            private static string URLPrefix => IsHTTPS ? "https://" : "http://";

            [JsonIgnore]
            public static string Site => string.IsNullOrWhiteSpace(Downloader.SiteURL.Query)
                ? $"{URLPrefix}{Downloader.SiteURL.Host}"
                : Downloader.SiteURL.ToString();

            public static AbstractNeedsLoginDownloader Downloader => (AbstractNeedsLoginDownloader)(object)DownloadDispatcher.GetInstance<TDownloader>();

            [JsonIgnore]
            public override object[] PrimaryKey
            {
                get
                {
                    return string.IsNullOrWhiteSpace(FileID)
                        ? IsAttachment 
                            ? new object[] {Downloader.SiteURL, IsAttachment, FullURL}
                            : new object[] {Downloader.SiteURL, FileName}
                        : new object[] {Downloader.SiteURL, FileName, FileID};
                }
            }

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                return true;
            }

            public override async Task<bool> Download(Archive a, AbsolutePath destination, WorkQueue queue)
            {
                var (isValid, istream) = await ResolveDownloadStream(a, false);
                if (!isValid) return false;
                using var stream = istream!;
                await using var fromStream = await stream.Content.ReadAsStreamAsync();
                await using var toStream = await destination.Create();
                await fromStream.CopyToAsync(toStream);
                return true;
            }

            private async Task<(bool, HttpResponseMessage?)> ResolveDownloadStream(Archive a, bool quickMode)
            {
                TOP:
                string url;
                if (IsAttachment)
                {
                    url = FullURL;
                }
                else
                {
                    var csrfURL = string.IsNullOrWhiteSpace(FileID)
                        ? $"{Site}/files/file/{FileName}/?do=download"
                        : $"{Site}/files/file/{FileName}/?do=download&r={FileID}";
                    var html = await Downloader.AuthedClient.GetStringAsync(csrfURL);

                    var pattern = new Regex("(?<=csrfKey=).*(?=[&\"\'])|(?<=csrfKey: \").*(?=[&\"\'])");
                    var matches = pattern.Matches(html).Cast<Match>();
                    
                    var csrfKey = matches.Where(m => m.Length == 32).Select(m => m.ToString()).FirstOrDefault();

                    if (csrfKey == null)
                    {
                        Utils.Log($"Returning null from IPS4 Downloader because no csrfKey was found");
                        return (false, null);
                    }

                    var sep = Site.EndsWith("?") ? "&" : "?";
                    url = FileID == null
                        ? $"{Site}/files/file/{FileName}/{sep}do=download&confirm=1&t=1&csrfKey={csrfKey}"
                        : $"{Site}/files/file/{FileName}/{sep}do=download&r={FileID}&confirm=1&t=1&csrfKey={csrfKey}";
                }

                var streamResult = await Downloader.AuthedClient.GetAsync(url);
                if (streamResult.StatusCode != HttpStatusCode.OK)
                {
                    Utils.ErrorThrow(new InvalidOperationException(), $"{Downloader.SiteName} servers reported an error for file: {FileID}");
                }

                var contentType = streamResult.Content.Headers.ContentType;

                if (contentType.MediaType != "application/json")
                {
                    var headerVar = a.Size == 0 ? "1" : a.Size.ToString();
                    long headerContentSize = 0;
                    if (streamResult.Content.Headers.Contains("Content-Length"))
                    {
                        headerVar = streamResult.Content.Headers.GetValues("Content-Length").FirstOrDefault();
                        if (headerVar != null)
                            long.TryParse(headerVar, out headerContentSize);
                    }
                    
                    if (a.Size != 0 && headerContentSize != 0 && a.Size != headerContentSize)
                    {
                        Utils.Log($"Bad Header Content sizes {a.Size} vs {headerContentSize}");
                        return (false, null);
                    }

                    return (true, streamResult);
                }

                // Sometimes LL hands back a json object telling us to wait until a certain time
                var times = (await streamResult.Content.ReadAsStringAsync()).FromJsonString<WaitResponse>();
                var secs = times.Download - times.CurrentTime;
                for (int x = 0; x < secs; x++)
                {
                    if (quickMode) return (true, default);
                    Utils.Status($"Waiting for {secs} at the request of {Downloader.SiteName}", Percent.FactoryPutInRange(x, secs));
                    await Task.Delay(1000);
                }
                streamResult.Dispose();
                Utils.Status("Retrying download");
                goto TOP;
            }

            private class WaitResponse
            {
                [JsonProperty("download")]
                public int Download { get; set; }
                [JsonProperty("currentTime")]
                public int CurrentTime { get; set; }
            }

            public override async Task<bool> Verify(Archive a)
            {
                var (isValid, stream) = await ResolveDownloadStream(a, true);
                if (!isValid) return false;
                if (stream == null)
                    return false;

                stream.Dispose();
                return true;
            }
            
            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<TDownloader>();
            }

            public override async Task<(Archive? Archive, TempFile NewFile)> FindUpgrade(Archive a, Func<Archive, Task<AbsolutePath>> downloadResolver, WorkQueue queue)
            {
                var files = await GetFilesInGroup();
                var nl = new Levenshtein();

                foreach (var newFile in files.OrderBy(f => nl.Distance(a.Name.ToLowerInvariant(), f.Name.ToLowerInvariant())))
                {
                    /*
                    var existing = await downloadResolver(newFile);
                    if (existing != default) return (newFile, new TempFile());*/

                    var tmp = new TempFile();
                    await DownloadDispatcher.PrepareAll(new[] {newFile.State});
                    if (await newFile.State.Download(newFile, tmp.Path, queue))
                    {
                        newFile.Size = tmp.Path.Size;
                        newFile.Hash = await tmp.Path.FileHashAsync();
                        return (newFile, tmp);
                    }

                    await tmp.DisposeAsync();
                }
                return default;

            }

            public override async Task<bool> ValidateUpgrade(Hash srcHash, AbstractDownloadState newArchiveState)
            {
                return !string.IsNullOrWhiteSpace(FileID);
            }

            public async Task<List<Archive>> GetFilesInGroup()
            {
                var others = await Downloader.AuthedClient.GetHtmlAsync($"{Site}/files/file/{FileName}?do=download");

                var pairs = others.DocumentNode.SelectNodes("//a[@data-action='download']")
                    .Select(item => (item.GetAttributeValue("href", ""),
                        item.ParentNode.ParentNode.SelectNodes("//div//h4//span").First().InnerText));
                
                List<Archive> archives = new List<Archive>();
                foreach (var (url, name) in pairs)
                {
                    var urlDecoded = HttpUtility.HtmlDecode(url);
                    var ini = new[] {"[General]", $"directURL={urlDecoded}"};
                    var state = (AbstractDownloadState)(await DownloadDispatcher.ResolveArchive(
                        string.Join("\n", ini).LoadIniString(), false));
                    if (state == null) continue;
                    
                    archives.Add(new Archive(state) {Name = name});
                    
                }

                return archives;
            }

            public override string GetManifestURL(Archive a)
            {
                return IsAttachment ? FullURL : $"{Site}/files/file/{FileName}/?do=download&r={FileID}";
            }

            public override string[] GetMetaIni()
            {
                if (IsAttachment)
                    return new[] {"[General]", $"directURL={FullURL}"};

                if (FileID == null)
                    return new[] {"[General]", $"directURL={Site}/files/file/{FileName}"};

                if (Site.EndsWith("?"))
                {
                    return new[]
                    {
                        "[General]", $"directURL={Site}/files/file/{FileName}&do=download&r={FileID}&confirm=1&t=1"
                    };
                        
                }

                return new[]
                {
                    "[General]", $"directURL={Site}/files/file/{FileName}/?do=download&r={FileID}&confirm=1&t=1"
                };

            }

            public virtual async Task<bool> LoadMetaData()
            {
                return false;
            }
        }
    }
}
