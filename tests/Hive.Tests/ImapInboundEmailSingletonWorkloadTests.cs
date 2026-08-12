using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Hive.Actors.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class ImapInboundEmailSingletonWorkloadTests
{
    [Fact]
    public async Task Source_actor_starts_one_poll_and_does_not_overlap_the_next_interval()
    {
        using var system = ActorSystem.Create($"hive-imap-source-{Guid.NewGuid():N}");
        var poller = new RecordingPoller();
        var processor = new RecordingProcessor();
        var replyProcessor = new RecordingReplyProcessor();
        system.ActorOf(ImapInboundEmailSourceActor.Props(
            poller,
            processor,
            replyProcessor,
            TimeSpan.FromHours(1),
            "occupant-replies",
            "INBOX",
            NullLogger.Instance));

        await WaitForAsync(
            () => poller.PollCount == 1
                && processor.ProcessCount == 1
                && replyProcessor.ProcessCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, poller.PollCount);
        Assert.Equal(1, processor.ProcessCount);
        Assert.Equal(1, replyProcessor.ProcessCount);
        await system.Terminate();
    }

    [Fact]
    public async Task Enabled_connectors_workload_materializes_stable_singleton_and_polls_once()
    {
        var port = GetFreeTcpPort();
        var systemName = $"hive-imap-singleton-{Guid.NewGuid():N}";
        var system = ActorSystem.Create(
            systemName,
            ConfigurationFactory.ParseString($$"""
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                akka.remote.dot-netty.tcp.port = {{port}}
                akka.cluster.roles = ["{{NodeRoleNames.Connectors}}"]
                """));
        try
        {
            var cluster = Cluster.Get(system);
            cluster.Join(cluster.SelfAddress);
            await WaitForAsync(
                () => cluster.SelfMember.Status == MemberStatus.Up,
                TimeSpan.FromSeconds(20));
            var poller = new RecordingPoller();
            var workload = new ImapInboundEmailSingletonWorkload(
                system,
                poller,
                NoopProcessor.Instance,
                NoopReplyProcessor.Instance,
                Options.Create(new ImapInboundEmailOptions
                {
                    Enabled = true,
                    SourceId = "occupant-replies",
                    Mailbox = "INBOX",
                    PollInterval = TimeSpan.FromHours(1),
                    ClusterUpTimeout = TimeSpan.FromSeconds(10),
                }),
                NullLogger<ImapInboundEmailSingletonWorkload>.Instance);

            await workload.StartAsync(CancellationToken.None);
            await WaitForAsync(() => poller.PollCount == 1, TimeSpan.FromSeconds(20));

            Assert.Equal(NodeRoleNames.Connectors, workload.Role);
            Assert.Equal(
                ImapInboundEmailSingletonIdentity.SingletonManagerName,
                workload.Manager!.Path.Name);
            Assert.Equal(
                ImapInboundEmailSingletonIdentity.ProxyName,
                workload.Proxy!.Path.Name);

            var manager = workload.Manager;
            var proxy = workload.Proxy;
            await workload.StartAsync(CancellationToken.None);
            Assert.Same(manager, workload.Manager);
            Assert.Same(proxy, workload.Proxy);
            Assert.Equal(1, poller.PollCount);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Disabled_source_does_not_touch_cluster_or_poller()
    {
        using var system = ActorSystem.Create($"hive-imap-disabled-{Guid.NewGuid():N}");
        var poller = new RecordingPoller();
        var workload = new ImapInboundEmailSingletonWorkload(
            system,
            poller,
            NoopProcessor.Instance,
            NoopReplyProcessor.Instance,
            Options.Create(new ImapInboundEmailOptions
            {
                Enabled = false,
            }),
            NullLogger<ImapInboundEmailSingletonWorkload>.Instance);

        await workload.StartAsync(CancellationToken.None);

        Assert.Null(workload.Manager);
        Assert.Null(workload.Proxy);
        Assert.Equal(0, poller.PollCount);
        await system.Terminate();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RecordingPoller : IImapInboundEmailPoller
    {
        private int _pollCount;

        public int PollCount => Volatile.Read(ref _pollCount);

        public Task<ImapInboundEmailPollResult> PollAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pollCount);
            return Task.FromResult(new ImapInboundEmailPollResult(
                true,
                0,
                0,
                new ImapInboundEmailCheckpoint(
                    "occupant-replies",
                    "INBOX",
                    1,
                    0)));
        }
    }

    private sealed class NoopProcessor : IInboundOccupantEmailProcessor
    {
        public static NoopProcessor Instance { get; } = new();

        public Task<InboundOccupantEmailProcessingResult> ProcessPendingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InboundOccupantEmailProcessingResult(0, 0, 0, 0, 0));
    }

    private sealed class RecordingProcessor : IInboundOccupantEmailProcessor
    {
        private int _processCount;

        public int ProcessCount => Volatile.Read(ref _processCount);

        public Task<InboundOccupantEmailProcessingResult> ProcessPendingAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _processCount);
            return Task.FromResult(new InboundOccupantEmailProcessingResult(0, 0, 0, 0, 0));
        }
    }

    private sealed class NoopReplyProcessor : IInboundOccupantEmailReplyProcessor
    {
        public static NoopReplyProcessor Instance { get; } = new();

        public Task<InboundOccupantEmailReplyProcessingResult> ProcessAcceptedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InboundOccupantEmailReplyProcessingResult(0, 0, 0, 0, 0));
    }

    private sealed class RecordingReplyProcessor : IInboundOccupantEmailReplyProcessor
    {
        private int _processCount;

        public int ProcessCount => Volatile.Read(ref _processCount);

        public Task<InboundOccupantEmailReplyProcessingResult> ProcessAcceptedAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _processCount);
            return Task.FromResult(
                new InboundOccupantEmailReplyProcessingResult(0, 0, 0, 0, 0));
        }
    }
}
