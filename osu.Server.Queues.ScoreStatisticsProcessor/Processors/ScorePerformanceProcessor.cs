// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using NUnit.Framework;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using BeatmapManager = osu.Game.Beatmaps.BeatmapManager;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the performance points of scores.
    /// </summary>
    public class ScorePerformanceProcessor : IProcessor, IDisposable
    {
        private static readonly bool check_client_version = Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION") != "0";

        public const int ORDER = 0;

        private BuildStore? buildStore;

        public int Order => ORDER;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public bool Verbose { get; set; }

        private static readonly bool write_legacy_score_pp = Environment.GetEnvironmentVariable("WRITE_LEGACY_SCORE_PP") != "0";

        private HeadlessGameHost host;
        private OsuGame osu;
        private BeatmapManager? beatmapManager;

        public ScorePerformanceProcessor()
        {
            host = new HeadlessLocal(@"lazer");
            LoadOsuIntoHost(host);
            beatmapManager = osu.Dependencies.Get<BeatmapManager>();
            Assert.NotNull(beatmapManager, "BeatmapManager should not be null at this point.");
        }

        public void Dispose()
        {
            host.Exit();
        }

        public void waitForOrAssert(Func<bool> result, string failureMessage, int timeout = 60000)
        {
            Task task = Task.Run(() =>
            {
                while (!result()) Thread.Sleep(200);
            });

            Assert.True(task.Wait(timeout), failureMessage);
        }

        public OsuGameBase LoadOsuIntoHost(GameHost host, bool withBeatmap = false)
        {
            osu = new OsuGame();
            Task.Factory.StartNew(() => host.Run(osu), TaskCreationOptions.LongRunning)
                .ContinueWith(t => Assert.Fail($"Host threw exception {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);

            waitForOrAssert(() => osu.IsLoaded, @"osu! failed to start in a reasonable amount of time");

            bool ready = false;
            // wait for two update frames to be executed. this ensures that all components have had a change to run LoadComplete and hopefully avoid
            // database access (GlobalActionContainer is one to do this).
            host.UpdateThread.Scheduler.Add(() => host.UpdateThread.Scheduler.Add(() => ready = true));

            waitForOrAssert(() => ready, @"osu! failed to start in a reasonable amount of time");

            return osu;
        }


        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            ProcessScoreAsync(score, conn, transaction).Wait();
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }

        /// <summary>
        /// Processes the raw PP value of all scores from a specified user.
        /// </summary>
        /// <param name="userId">The user to process all scores of.</param>
        /// <param name="rulesetId">The ruleset for which scores should be processed.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of scores which had their performance values updated.</returns>
        public async Task<int> ProcessUserScoresAsync(uint userId, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null, CancellationToken cancellationToken = default)
        {
            var scores = (await connection.QueryAsync<SoloScore>("SELECT * FROM scores WHERE `user_id` = @UserId AND `ruleset_id` = @RulesetId", new
            {
                UserId = userId,
                RulesetId = rulesetId
            }, transaction: transaction)).ToArray();

            if (!scores.Any())
                return 0;

            int totalUpdated = 0;

            foreach (SoloScore score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (await ProcessScoreAsync(score, connection, transaction))
                    totalUpdated++;
            }

            return totalUpdated;
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="scoreId">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(ulong scoreId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var score = await connection.QuerySingleOrDefaultAsync<SoloScore>("SELECT * FROM scores WHERE `id` = @ScoreId", new
            {
                ScoreId = scoreId
            }, transaction: transaction);

            if (score == null)
            {
                await Console.Error.WriteLineAsync($"Could not find score ID {scoreId}.");
                return;
            }

            await ProcessScoreAsync(score, connection, transaction);
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task<bool> ProcessScoreAsync(SoloScore score, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            // Usually checked via "RunOnFailedScores", but this method is also used by the CLI batch processor.
            if (!score.passed)
                return false;

            buildStore ??= new BuildStore();

            score.beatmap ??= await BeatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction);

            if (score.beatmap is not Beatmap beatmap)
                return false;

            // TODO: will fail for newly ranked beatmaps for up to one minute (beatmap store purge).
            // if (!BeatmapStore.IsBeatmapValidForPerformance(beatmap, score.ruleset_id))
            //     return false;

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
            Mod[] mods = score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray();

            // if (!AllModsValidForPerformance(score, mods))
            //     return false;

            // Performance needs to be allowed for the build.
            // legacy scores don't need a build id
            // if (check_client_version && score.legacy_score_id == null && (score.build_id == null || (await buildStore.GetBuildAsync(score.build_id.Value, connection, transaction))?.allow_performance != true))
            //     return false;

            waitForOrAssert(() => osu.IsLoaded, @"osu! failed to start in a reasonable amount of time");

            bool ready = false;
            // wait for two update frames to be executed. this ensures that all components have had a change to run LoadComplete and hopefully avoid
            // database access (GlobalActionContainer is one to do this).
            host.UpdateThread.Scheduler.Add(() => host.UpdateThread.Scheduler.Add(() => ready = true));

            waitForOrAssert(() => ready, @"osu! failed to start in a reasonable amount of time");

            Assert.NotNull(beatmapManager,"BeatmapManager should not be null at this point.");
            DifficultyAttributes difficultyAttributes;
            difficultyAttributes = await BeatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, connection, transaction, beatmapManager);

            PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(score.ToScoreInfo(), difficultyAttributes);

            if (performanceAttributes == null)
                return false;

            if (score.pp != null && Math.Abs(score.pp.Value - performanceAttributes.Total) < 0.1)
                return false;

            if (Verbose)
            {
                Console.WriteLine(
                    $"{score.id.ToString(),-12}: {score.pp ?? -1,-4:N2} -> {performanceAttributes.Total,-4:N2} "
                    + $"({performanceAttributes.Total - (score.pp ?? 0),-5:+#,0.00;-#,0.00;+#,0.00})"
                    + (score.is_legacy_score ? " LEGACY" : string.Empty));
            }

            score.pp = performanceAttributes.Total;

            if (score.is_legacy_score && write_legacy_score_pp)
            {
                var helper = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);
                await connection.ExecuteAsync($"UPDATE scores SET pp = @Pp WHERE id = @ScoreId; UPDATE {helper.HighScoreTable} SET pp = @Pp WHERE score_id = @LegacyScoreId", new
                {
                    ScoreId = score.id,
                    LegacyScoreId = score.legacy_score_id,
                    Pp = score.pp
                }, transaction: transaction);
            }
            else
            {
                await connection.ExecuteAsync("UPDATE scores SET pp = @Pp WHERE id = @ScoreId", new
                {
                    ScoreId = score.id,
                    Pp = score.pp,
                }, transaction: transaction);
            }

            return true;
        }

        /// <summary>
        /// Checks whether all mods in a given array are valid to give PP for.
        /// </summary>
        public static bool AllModsValidForPerformance(SoloScore score, Mod[] mods)
        {
            IEnumerable<Mod> modsToCheck = mods;

            // Classic mod is only allowed on legacy scores.
            if (score.is_legacy_score)
                modsToCheck = mods.Where(mod => mod is not ModClassic);

            return modsToCheck.All(m => m.Ranked);
        }

    }

    public class HeadlessLocal : HeadlessGameHost {

        public HeadlessLocal(string? gameName = null, HostOptions? options = null, bool realtime = true) : base(gameName, options, realtime)
        {
        }

        // public override IEnumerable<string> UserStoragePaths => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create).Yield();
        public override IEnumerable<string> UserStoragePaths => new string[1] { "/mnt/md127/" };

    }

}
