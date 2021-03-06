﻿using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Heroes.ReplayParser;
using Newtonsoft.Json;

namespace Heroesprofile.Uploader.Common
{
    public class Uploader : IUploader
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
#if DEBUG
        const string HeroesProfileApiEndpoint = "https://api.heroesprofile.com/api";
        const string HotsAPIApiEndpoint = "http://hotsapi.local/api/v1";

#else
        const string HeroesProfileApiEndpoint = "https://api.heroesprofile.com/api";
        const string HotsAPIApiEndpoint = "https://hotsapi.net/api/v1";
#endif

        public bool UploadToHotslogs { get; set; }

        /// <summary>
        /// New instance of replay uploader
        /// </summary>
        public Uploader()
        {

        }

        /// <summary>
        /// Upload replay
        /// </summary>
        /// <param name="file"></param>
        public async Task Upload(Replay replay_results, ReplayFile file)
        {
            file.UploadStatus = UploadStatus.InProgress;
            if (file.Fingerprint != null && await CheckDuplicate(file.Fingerprint)) {
                _log.Debug($"File {file} marked as duplicate");
                file.UploadStatus = UploadStatus.Duplicate;
            } else {
                file.UploadStatus = await Upload(replay_results, file.Fingerprint, file.Filename);
            }
        }

        /// <summary>
        /// Upload replay
        /// </summary>
        /// <param name="file">Path to file</param>
        /// <returns>Upload result</returns>
        public async Task<UploadStatus> Upload(Replay replay_results, string fingerprint, string file)
        {
            //I am having issues with the request being too large due to the replay_json object.  Might try compressing it and then decompressing it on the laravel side
            //I am having a hard time getting it compressed though.  I tmight be because my code is sending everything but the file as get.  So need to send the json object
            //through post, along with the file, but not sure how to do that.

            //string replay_json = JsonConvert.SerializeObject(ToJson(replay_results));

            try {
                string response;
                using (var client = new WebClient()) {
                    //var bytes = await client.UploadFileTaskAsync($"{HeroesProfileApiEndpoint}/upload?fingerprint={fingerprint}&data={replay_json}", file);

                    var bytes = await client.UploadFileTaskAsync($"{HeroesProfileApiEndpoint}/upload?fingerprint={fingerprint}", file);
                    response = Encoding.UTF8.GetString(bytes);
                }


                //Try upload to HotsApi as well
                string hotsapiResponse;
                try {
                    using (var client = new WebClient()) {
                        var bytes = await client.UploadFileTaskAsync($"{HotsAPIApiEndpoint}/upload?uploadToHotslogs={UploadToHotslogs}", file);
                        hotsapiResponse = Encoding.UTF8.GetString(bytes);
                    }
                }
                catch {

                }


                dynamic json = JObject.Parse(response);
                if ((bool)json.success) {
                    if (Enum.TryParse<UploadStatus>((string)json.status, out UploadStatus status)) {
                        _log.Debug($"Uploaded file '{file}': {status}");
                        return status;
                    } else {
                        _log.Error($"Unknown upload status '{file}': {json.status}");
                        return UploadStatus.UploadError;
                    }
                } else {
                    _log.Warn($"Error uploading file '{file}': {response}");
                    return UploadStatus.UploadError;
                }
            }
            catch (WebException ex) {
                if (await CheckApiThrottling(ex.Response)) {
                    return await Upload(replay_results, fingerprint, file);
                }
                _log.Warn(ex, $"Error uploading file '{file}'");
                return UploadStatus.UploadError;
            }
        }

        /// <summary>
        /// Check replay fingerprint against database to detect duplicate
        /// </summary>
        /// <param name="fingerprint"></param>
        private async Task<bool> CheckDuplicate(string fingerprint)
        {
            try {
                string response;
                using (var client = new WebClient()) {
                    response = await client.DownloadStringTaskAsync($"{HeroesProfileApiEndpoint}/replays/fingerprints/{fingerprint}");
                }
                dynamic json = JObject.Parse(response);
                return (bool)json.exists;
            }
            catch (WebException ex) {
                if (await CheckApiThrottling(ex.Response)) {
                    return await CheckDuplicate(fingerprint);
                }
                _log.Warn(ex, $"Error checking fingerprint '{fingerprint}'");
                return false;
            }
        }

        /// <summary>
        /// Mass check replay fingerprints against database to detect duplicates
        /// </summary>
        /// <param name="fingerprints"></param>
        private async Task<string[]> CheckDuplicate(IEnumerable<string> fingerprints)
        {
            try {
                string response;
                using (var client = new WebClient()) {
                    response = await client.UploadStringTaskAsync($"{HeroesProfileApiEndpoint}/replays/fingerprints", String.Join("\n", fingerprints));
                }
                dynamic json = JObject.Parse(response);
                return (json.exists as JArray).Select(x => x.ToString()).ToArray();
            }
            catch (WebException ex) {
                if (await CheckApiThrottling(ex.Response)) {
                    return await CheckDuplicate(fingerprints);
                }
                _log.Warn(ex, $"Error checking fingerprint array");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Mass check replay fingerprints against database to detect duplicates
        /// </summary>
        public async Task CheckDuplicate(IEnumerable<ReplayFile> replays)
        {
            var exists = new HashSet<string>(await CheckDuplicate(replays.Select(x => x.Fingerprint)));
            replays.Where(x => exists.Contains(x.Fingerprint)).Map(x => x.UploadStatus = UploadStatus.Duplicate);
        }

        /// <summary>
        /// Get minimum HotS client build supported by HotsApi
        /// </summary>
        public async Task<int> GetMinimumBuild()
        {
            //We likely want to track which replays arn't supported by HotsApi so that we don't send them to HotsApi, 
            //but I would like to change this so that it doesn't prevent replays uploading to our own storage, as we can support any replay build


            try {
                using (var client = new WebClient()) {
                    var response = await client.DownloadStringTaskAsync($"{HeroesProfileApiEndpoint}/replays/hotsapi-min-build");
                    if (!int.TryParse(response, out int build)) {
                        _log.Warn($"Error parsing minimum build: {response}");
                        return 0;
                    }
                    return 0;
                }
            }
            catch (WebException ex) {
                if (await CheckApiThrottling(ex.Response)) {
                    return await GetMinimumBuild();
                }
                _log.Warn(ex, $"Error getting minimum build");
                return 0;
            }
        }

        /// <summary>
        /// Check if Hotsapi request limit is reached and wait if it is
        /// </summary>
        /// <param name="response">Server response to examine</param>
        private async Task<bool> CheckApiThrottling(WebResponse response)
        {
            if (response != null && (int)(response as HttpWebResponse).StatusCode == 429) {
                _log.Warn($"Too many requests, waiting");
                await Task.Delay(10000);
                return true;
            } else {
                return false;
            }
        }
        /*
        public static object ToJson(Replay replay)
        {
            var obj = new {
                mode = replay.GameMode.ToString(),
                region = replay.Players[0].BattleNetRegionId,
                date = replay.Timestamp,
                length = replay.ReplayLength,
                map = replay.Map,
                map_short = replay.MapAlternativeName,
                version = replay.ReplayVersion,
                version_major = replay.ReplayVersionMajor,
                version_build = replay.ReplayBuild,
                bans = replay.TeamHeroBans,
                draft_order = replay.DraftOrder,
                team_experience = replay.TeamPeriodicXPBreakdown,
                players = from p in replay.Players
                          select new {
                              battletag_name = p.Name,
                              battletag_id = p.BattleTag,
                              blizz_id = p.BattleNetId,
                              account_level = p.AccountLevel,
                              hero = p.Character,
                              hero_level = p.CharacterLevel,
                              hero_level_taunt = p.HeroMasteryTiers,
                              team = p.Team,
                              winner = p.IsWinner,
                              silenced = p.IsSilenced,
                              party = p.PartyValue,
                              talents = p.Talents.Select(t => t.TalentName),
                              score = p.ScoreResult,
                              staff = p.IsBlizzardStaff,
                              announcer = p.AnnouncerPackAttributeId,
                              banner = p.BannerAttributeId,
                              skin_title = p.SkinAndSkinTint,
                              hero_skin = p.SkinAndSkinTintAttributeId,
                              mount_title = p.MountAndMountTint,
                              mount = p.MountAndMountTintAttributeId,
                              spray_title = p.Spray,
                              spray = p.SprayAttributeId,
                              voice_line_title = p.VoiceLine,
                              voice_line = p.VoiceLineAttributeId,
                          }
            };
            return obj;
        }
        */
    }
}
