using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lnrpc;
using LNUnit.LND;
using LNUnit.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Routerrpc;
using Serilog;
using ServiceStack;
using ServiceStack.Text;
using Assert = NUnit.Framework.Assert;
using HostConfig = Docker.DotNet.Models.HostConfig;

namespace LNUnit.Fixtures;
 
//[TestFixture]
// [SingleThreaded]
public class PostgresLightningFixture : IDisposable
{
    
    [SetUp]
    public async Task PerTestSetUp()
    {
        Builder.CancelAllInterceptors();
        await Builder.NewBlock(); 
    }
    
    [Test]
    [NonParallelizable]
    public async Task InterceptorTest()
    {
        List<AddInvoiceResponse> invoices = new();
        for (var i = 0; i < 10; i++)
        {
            var invoice = await Builder.GeneratePaymentRequestFromAlias("alice", new Invoice
            {
                Memo = "This path is a trap, better have max_fees set",
                ValueMsat = 1000,
                Expiry = 60 * 60 * 24
            });
            invoices.Add(invoice);
        }

        //Apply HTLC hold to prevent payment from settling
        var htlcEvents = 0;
        List<LNDHTLCMonitor> monitors = new();
        foreach (var n in Builder.LNDNodePool.ReadyNodes.ToImmutableList())
            //This disposes so should use Clone of connection
            monitors.Add(new LNDHTLCMonitor(n.Clone(), htlc => { htlcEvents++; }));


        await Builder.DelayAllHTLCsOnAlias("alice", 1);
        await Builder.DelayAllHTLCsOnAlias("bob", 1);
        await Builder.DelayAllHTLCsOnAlias("carol", 1);
        await Task.Delay(1000);
        Assert.That(await Builder.IsInterceptorActiveForAlias("alice"));
        Assert.That((await Builder.GetInterceptor("bob")).InterceptCount == 0);
        Assert.That(await Builder.IsInterceptorActiveForAlias("bob"));
        Assert.That(await Builder.IsInterceptorActiveForAlias("carol"));
        await Task.Delay(5000);
        var ii = 0;
        foreach (var invoice in invoices)
        {
            ii++;
            ii.PrintDump();
            var payment = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitMsat = 1000000000000000, //plenty of money
                TimeoutSeconds = 20000,
                NoInflightUpdates = true
            });
            Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
        }

        Assert.That(await Builder.IsInterceptorActiveForAlias("alice"), "alice not intercepting");
        Assert.That((await Builder.GetInterceptor("bob")).InterceptCount >= 10, "bob not intercepting");
        Assert.That(await Builder.IsInterceptorActiveForAlias("bob"), "Bob not intercepting");
        Assert.That(await Builder.IsInterceptorActiveForAlias("carol"), "carol not intercepting");
        Assert.That(htlcEvents > 10); //just what it spit out didn't do math for that
    }
    
    public string DbContainerName { get; set; } = "postgres";
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private string _containerId;
    private string _ip;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        var builder = WebApplication.CreateBuilder(new string[] { });

        //  builder.Services.AddSingleton(builder.Configuration.GetAWSOptions());

        var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();
        loggerConfiguration.ReadFrom.Configuration(builder.Configuration);

        Log.Logger = loggerConfiguration.CreateLogger();
        builder.Host.UseSerilog(dispose: true); //Log.Logger is implicitly used
        builder.Services.Configure<List<LNDSettings>>(o =>
        {
            //    o.AddRange(lndSettings);
        });

        builder.Services.Configure<LNDNodePoolConfig>(c =>
        {
            // c.AddNode(new LNDNodeConnection(lndSettings.First()));
            //
            // foreach (var z in lndSettings.Skip(1))
            // {
            //     c.AddConnectionSettings(z);
            // }
            // c.UpdateReadyStatesPeriod(1);
        });

        builder.Services.AddTransient<LNDNodePool>();
        // Add services to the container.
        builder.Services.AddMemoryCache();
        builder.Services.AddControllers();
        builder.Services.AddOptions();
        _host = builder.Build();
        var host_running = _host.RunAsync();
        Builder = ActivatorUtilities.CreateInstance<LNUnitBuilder>(_host.Services);

        await StartPostgres();
        AddDb("alice");
        AddDb("bob");
        AddDb("carol");
        await _client.CreateDockerImageFromPath("../../../../Docker/lnd", ["custom_lnd", "custom_lnd:latest"]);
        await SetupNetwork("custom_lnd","latest", "/home/lnd/.lnd");
    }

    private void AddDb(string dbName)
    {
        using (NpgsqlConnection connection = new(DbConnectionString))
        {
            connection.Open();
            using var checkIfExistsCommand = new NpgsqlCommand($"SELECT 1 FROM pg_catalog.pg_database WHERE datname = '{dbName}'", connection);
            var result = checkIfExistsCommand.ExecuteScalar();

            if (result == null)
            {

                using var command = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", connection);
                command.ExecuteNonQuery();
            }
        }
        LNDConnectionStrings.Add(dbName,
            $"postgresql://superuser:superuser@{_ip}:5432/{dbName}?sslmode=disable");
    }

    public string DbConnectionStringLND { get; private set; }
    public string DbConnectionString { get; private set; }

    public Dictionary<string, string> LNDConnectionStrings = new();
    private WebApplication _host;

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Remove containers
        RemoveContainer(DbContainerName).Wait();
        RemoveContainer("miner").Wait();
        RemoveContainer("alice").Wait();
        RemoveContainer("bob").Wait();
        RemoveContainer("carol").Wait();
       
        Builder?.Destroy();
        _client.Dispose();
    }

    public LNUnitBuilder? Builder { get; private set; }


    public async Task SetupNetwork(string image = "lightninglabs/lnd", string tag = "daily-testing-only", string lndRoot = "/root/.lnd", bool pullImage = false)
    {
        await RemoveContainer("miner");
        await RemoveContainer("alice");
        await RemoveContainer("bob");
        await RemoveContainer("carol");

        Builder.AddBitcoinCoreNode();

        if (pullImage)
        {
            await _client.PullImageAndWaitForCompleted(image, tag);
        }
        
        Builder.AddPolarLNDNode("alice",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemoteName = "bob"
            }
        ], imageName: image, tagName: tag, pullImage: false, postgresDSN: LNDConnectionStrings["alice"]);

        Builder.AddPolarLNDNode("bob",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "alice"
            }
        ], imageName: image, tagName: tag, pullImage: false, postgresDSN: LNDConnectionStrings["bob"]);

        Builder.AddPolarLNDNode("carol",
        [
            // new()
            // {
            //     ChannelSize = 10_000_000, //10MSat
            //     RemotePushOnStart = 1_000_000, // 1MSat
            //     RemoteName = "alice"
            // },
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "bob"
            }
        ], imageName: image, tagName: tag, pullImage: false);//, postgresDSN: LNDConnectionStrings["carol"]);

        await Builder.Build(lndRoot: lndRoot);
        var a = Builder.WaitUntilAliasIsServerReady("alice");
        var b = Builder.WaitUntilAliasIsServerReady("bob");
        var c = Builder.WaitUntilAliasIsServerReady("carol");

        Task.WaitAll(a, b, c);
        
        var graphReady = false;
        while (!graphReady)
        {
            var graph = await Builder.GetGraphFromAlias("alice");
            if (graph.Nodes.Count < 3)
            {
                "Graph not ready...".Print();
                await Task.Delay(250); //let the graph sync 
            }
            else
            {
                graphReady = true;
            }
        }
    }

    public async Task StartPostgres()
    {
        await _client.PullImageAndWaitForCompleted("postgres", "16.2-alpine");
        await RemoveContainer(DbContainerName);
        var nodeContainer = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "postgres:16.2-alpine",
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge"
            },
            Name = $"{DbContainerName}",
            Hostname = $"{DbContainerName}",
            Env =
            [
                "POSTGRES_PASSWORD=superuser",
                "POSTGRES_USER=superuser",
                "POSTGRES_DB=postgres"
            ]
        });
        Assert.NotNull(nodeContainer);
        _containerId = nodeContainer.ID;
        var started = await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());

        //Build connection string
        var ipAddressReady = false;
        while (!ipAddressReady)
        {
            var listContainers = await _client.Containers.ListContainersAsync(new ContainersListParameters());

            var db = listContainers.FirstOrDefault(x => x.ID == nodeContainer.ID);
            if (db != null)
            {
                _ip = db.NetworkSettings.Networks.First().Value.IPAddress;
                DbConnectionString = $"Host={_ip};Database=postgres;Username=superuser;Password=superuser";
                ipAddressReady = true;
            }
            else
            {
                await Task.Delay(100);
            }

        }
        //wait for TCP socket to open
        var tcpConnectable = false;
        while (!tcpConnectable)
        {
            try
            {
                TcpClient c = new()
                {
                    ReceiveTimeout = 1,
                    SendTimeout = 1
                };
                await c.ConnectAsync(new IPEndPoint(IPAddress.Parse(_ip), 5432));
                if (c.Connected)
                {
                    tcpConnectable = true;
                }
            }
            catch (Exception e)
            {
                await Task.Delay(50);
            }
        }
    }

    private async Task RemoveContainer(string name)
    {
        try
        {
            await _client.Containers.RemoveContainerAsync(name,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch
        {
            // ignored
        }
    }

    public async Task<bool> IsRunning()
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(DbContainerName);
            return inspect.State.Running;
        }
        catch
        {
            // ignored
        }

        return false;
    }
}
 