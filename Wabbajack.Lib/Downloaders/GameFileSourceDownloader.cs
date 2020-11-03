﻿using System.Threading.Tasks;
using Newtonsoft.Json;
using Wabbajack.Common;
using Wabbajack.Common.Serialization.Json;
using Wabbajack.Lib.Validation;
using Game = Wabbajack.Common.Game;

namespace Wabbajack.Lib.Downloaders
{
    public class GameFileSourceDownloader : IDownloader
    {
        public async Task<AbstractDownloadState?> GetDownloaderState(dynamic archiveINI, bool quickMode)
        {
            var gameName = (string?)archiveINI?.General?.gameName;
            var gameFile = (string?)archiveINI?.General?.gameFile;

            if (gameName == null || gameFile == null)
                return null;

            if (!GameRegistry.TryGetByFuzzyName(gameName, out var game)) return null;

            var path = game.TryGetGameLocation();
            var filePath = path?.Combine(gameFile);
            
            if (!(filePath?.Exists ?? false))
                return null;

            var fp = filePath.Value;
            var hash = await fp.FileHashCachedAsync();

            return new State(game.InstalledVersion)
            {
                Game = game.Game, 
                GameFile = (RelativePath)gameFile,
                Hash = hash
            };
        }

        public async Task Prepare()
        {
        }

        [JsonName("GameFileSourceDownloader")]
        public class State : AbstractDownloadState
        {
            public Game Game { get; set; }
            public RelativePath GameFile { get; set; }
            public Hash Hash { get; set; }
            public string GameVersion { get; set; } = "";

            public State(string gameVersion)
            {
                GameVersion = gameVersion;
            }

            public State()
            {
                
            }

            [JsonIgnore]
            internal AbsolutePath SourcePath => Game.MetaData().GameLocation().Combine(GameFile);

            [JsonIgnore]
            public override object[] PrimaryKey { get => new object[] {Game, GameVersion ?? "0.0.0.0", GameFile}; }

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                return true;
            }

            public override async Task<bool> Download(Archive a, AbsolutePath destination, WorkQueue queue)
            {
                await using var src = await SourcePath.OpenRead();
                await using var dest = await destination.Create();
                var size = SourcePath.Size;
                await src.CopyToWithStatusAsync(size, dest, "Copying from Game folder");

                return true;
            }

            public override async Task<bool> Verify(Archive a)
            {
                return SourcePath.Exists && await SourcePath.FileHashCachedAsync() == Hash;
            }

            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<GameFileSourceDownloader>();
            }

            public override string? GetManifestURL(Archive a)
            {
                return null;
            }

            public override string[] GetMetaIni()
            {
                return new[] {"[General]", $"gameName={Game.MetaData().MO2ArchiveName}", $"gameFile={GameFile}"};
            }

        }
    }
}
