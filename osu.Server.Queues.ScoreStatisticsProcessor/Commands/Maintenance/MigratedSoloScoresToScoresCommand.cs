// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("migrate-solo-scores", Description = "Migrate scores from `solo_scores` to `scores` table.")]
    public class MigrateSoloScoresCommand
    {
        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        /// <summary>
        /// The score ID to start migrating from.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = await DatabaseAccess.GetConnectionAsync(cancellationToken);
            using var dbInsert = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            using var insertCommand = dbInsert.CreateCommand();
            insertCommand.CommandText =
                "INSERT INTO scores (`user_id`, `ruleset_id`, `beatmap_id`, `has_replay`, `preserve`, `ranked`, `rank`, `passed`, `accuracy`, `max_combo`, `total_score`, `data`, `pp`, `legacy_score_id`, `legacy_total_score`, `ended_at`, `unix_updated_at`, `build_id`) VALUES (@user_id,  @ruleset_id, @beatmap_id, false, true, false, @rank, @passed, @accuracy, @max_combo, @total_score, @data, null, null, 0, @created_at, unix_timestamp(@created_at), @build_id)";

            var paramUserId = insertCommand.Parameters.Add("user_id", DbType.UInt32);
            var paramRulesetId = insertCommand.Parameters.Add("ruleset_id", DbType.UInt16);
            var paramBeatmapId = insertCommand.Parameters.Add("beatmap_id", MySqlDbType.UInt24);
            var paramRank = insertCommand.Parameters.Add("rank", MySqlDbType.VarChar);
            var paramPassed = insertCommand.Parameters.Add("passed", DbType.Boolean);
            var paramAccuracy = insertCommand.Parameters.Add("accuracy", DbType.Boolean);
            var paramMaxCombo = insertCommand.Parameters.Add("max_combo", MySqlDbType.UInt24);
            var paramTotalScore = insertCommand.Parameters.Add("total_score", MySqlDbType.UInt24);
            var paramData = insertCommand.Parameters.Add("data", MySqlDbType.JSON);
            var paramCreatedAt = insertCommand.Parameters.Add("created_at", MySqlDbType.Timestamp);
            var paramBuildId = insertCommand.Parameters.Add("build_id", MySqlDbType.UInt24);

            await insertCommand.PrepareAsync(cancellationToken);

            string startFrom = StartId == null ? string.Empty : $" AND score_id >= {StartId}";

            foreach (dynamic score in db.Query($"SELECT * FROM solo_scores s JOIN multiplayer_score_links_old l ON (id = score_id and s.user_id = l.user_id) {startFrom}", buffered: false))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Console.WriteLine($"Processing score {score.id}...");

                dynamic origData = JsonConvert.DeserializeObject(score.data);

                string newData = JsonConvert.SerializeObject(origData.mods != null
                    ? new
                    {
                        mods = origData.mods,
                        statistics = origData.statistics,
                        maximum_statistics = origData.maximum_statistics,
                        migrated_solo_score_id = score.id,
                    }
                    : new
                    {
                        statistics = origData.statistics,
                        maximum_statistics = origData.maximum_statistics,
                        migrated_solo_score_id = score.id,
                    });

                paramUserId.Value = score.user_id;
                paramRulesetId.Value = score.ruleset_id;
                paramBeatmapId.Value = score.beatmap_id;
                paramRank.Value = origData.rank.ToString();
                paramPassed.Value = origData.passed == "True";
                paramAccuracy.Value = (float)origData.accuracy;
                paramMaxCombo.Value = (int)origData.max_combo;
                paramTotalScore.Value = (int)origData.total_score;
                paramData.Value = newData;
                paramCreatedAt.Value = score.created_at;
                paramBuildId.Value = (int?)origData.build_id;

                if (DryRun)
                {
                    Console.WriteLine(
                        $"score:\n\n"
                        + $"UserId = {score.user_id}\n"
                        + $"RulesetId = {score.ruleset_id}\n"
                        + $"BeatmapId = {score.beatmap_id}\n"
                        + $"Rank = {origData.rank}\n"
                        + $"Passed = {origData.passed}\n"
                        + $"Accuracy = {origData.accuracy}\n"
                        + $"MaxCombo = {origData.max_combo}\n"
                        + $"TotalScore = {origData.total_score}\n"
                        + $"Data = {newData}\n"
                        + $"CreatedAt = {score.created_at}\n"
                        + $"BuildId = {origData.build_id}\n"
                    );

                    Console.WriteLine();

                    Console.WriteLine(
                        $"link:\n\n"
                        + $"userId = {score.user_id}\n"
                        + $"playlistItemId = {score.playlist_item_id}\n"
                        + $"scoreId = INSERT_ID\n"
                    );
                }
                else
                {
                    await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                    long insertId = insertCommand.LastInsertedId;

                    long linkInsertId = await dbInsert.QuerySingleAsync<long>(
                        "INSERT INTO multiplayer_score_links (user_id, playlist_item_id, score_id) VALUES (@userId, @playlistItemId, @scoreId); SELECT LAST_INSERT_ID();",
                        new
                        {
                            userId = score.user_id,
                            playlistItemId = score.playlist_item_id,
                            scoreId = insertId,
                        });

                    Console.WriteLine($"Inserted score {insertId} link {linkInsertId}");
                }
            }

            Console.WriteLine("Finished.");
            return 0;
        }
    }
}
