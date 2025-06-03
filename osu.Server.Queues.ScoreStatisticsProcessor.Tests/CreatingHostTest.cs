
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Beatmaps;
using Xunit;
using Xunit.Sdk;
namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class HeadlessLocal : HeadlessGameHost {

        public HeadlessLocal(string? gameName = null, HostOptions? options = null, bool realtime = true) : base(gameName, options, realtime)
        {
        }

        // public override IEnumerable<string> UserStoragePaths => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create).Yield();
        public override IEnumerable<string> UserStoragePaths => new string[1] { "/mnt/md127/" };

    }

    public class CreatingHostTest
    {
        [Fact]
        public async void HostTest()
        {
            // var hostOptions = new HostOptions {
            //     PortableInstallation = false
            // };

            using (HeadlessGameHost host = new HeadlessLocal(@"lazer"))
            {
                try
                {
                    var game = LoadOsuIntoHost(host);
                    var manager = game.Dependencies.Get<BeatmapManager>();
                    Assert.NotNull(manager);
                    var beatmapsets = manager.QueryBeatmapSet(b => b.DateAdded > new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
                    var beatmap = manager.QueryBeatmap(b => b.MD5Hash == "e04b2139f9cfd2ca3bbfac68ca0c30f1");
                    Assert.Equal(beatmap.OnlineID, 555237);
                    var working = manager.GetWorkingBeatmap(beatmap);
                    Assert.Equal(working.Beatmap.Difficulty.ApproachRate,4.5);
                }
                finally
                {
                    host.Exit();
                }
            }
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
            var osu = new OsuGame();
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

    }
}
