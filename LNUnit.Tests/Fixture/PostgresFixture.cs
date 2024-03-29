using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using LNUnit.Setup;
using Npgsql;
using Assert = NUnit.Framework.Assert;
using HostConfig = Docker.DotNet.Models.HostConfig;

namespace LNUnit.Tests.Fixture;

/// <summary>
///     Used Globally to get Postgres support
/// </summary>
public static class PostgresFixture
{
    public static Dictionary<string, string> LNDConnectionStrings = new();
    public static string DbConnectionStringDotNet { get; internal set; } = string.Empty;
    public static string DbContainerName { get; internal set; } = "postgres";

    internal static PostgresInstanceFixture? Instance { get; set; }

    public static async Task<bool> IsReady()
    {
        return await Instance?.IsRunning();
    }

    public static void AddDb(string dbName)
    {
        Instance?.AddDb(dbName);
    }
}

[SetUpFixture]
public class PostgresInstanceFixture
{
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    public readonly Dictionary<string, string> LNDConnectionStrings = PostgresFixture.LNDConnectionStrings;
    private string _containerId;
    private string _ip;

    public string DbConnectionStringDotNet { get; private set; }
    public string DbContainerName { get; set; } = "postgres";

    [OneTimeSetUp]
    public async Task RunBeforeAnyTests()
    {
        await StartPostgres();
        PostgresFixture.Instance = this;
    }

    [OneTimeTearDown]
    public async Task RunAfterAnyTests()
    {
        await _client.RemoveContainer(DbContainerName);
        _client.Dispose();
    }

    internal void AddDb(string dbName)
    {
        LNDConnectionStrings.Remove(dbName);
        using (NpgsqlConnection connection = new(DbConnectionStringDotNet))
        {
            connection.Open();
            using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"; CREATE DATABASE \"{dbName}\"",
                connection);
            command.ExecuteNonQuery();
        }

        LNDConnectionStrings.Add(dbName,
            $"postgresql://superuser:superuser@{_ip}:5432/{dbName}?sslmode=disable");
    }

    private async Task StartPostgres(
        string image = "postgres",
        string tag = "16.2-alpine",
        string password = "superuser",
        string username = "superuser")
    {
        await _client.PullImageAndWaitForCompleted(image, tag);
        await _client.RemoveContainer(DbContainerName);
        var nodeContainer = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{image}:{tag}",
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge"
            },
            Name = $"{DbContainerName}",
            Hostname = $"{DbContainerName}",
            Env =
            [
                $"POSTGRES_PASSWORD={username}",
                $"POSTGRES_USER={password}",
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
                DbConnectionStringDotNet = $"Host={_ip};Database=postgres;Username=superuser;Password=superuser;Include Error Detail=true;";
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
            try
            {
                TcpClient c = new()
                {
                    ReceiveTimeout = 1,
                    SendTimeout = 1
                };
                await c.ConnectAsync(new IPEndPoint(IPAddress.Parse(_ip), 5432));
                if (c.Connected) tcpConnectable = true;
            }
            catch (Exception e)
            {
                await Task.Delay(50);
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