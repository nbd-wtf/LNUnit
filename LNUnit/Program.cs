using System.CommandLine;
using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);


builder.Services.Configure<BrotliCompressionProviderOptions>(options => { options.Level = CompressionLevel.Fastest; });

builder.Services.Configure<GzipCompressionProviderOptions>(options => { options.Level = CompressionLevel.Fastest; });

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardLimit = 1;
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.Configure<HttpsRedirectionOptions>(options => { options.HttpsPort = 443; });
builder.Services.Configure<MemoryCacheOptions>(o =>
{
    o.ExpirationScanFrequency = new TimeSpan(0, 10, 0); // we can have stale records up to 10m
    o.SizeLimit = 100_000; //Should cover whole never work for a while, it is in record, not size
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

var task = app.RunAsync();

await SetupCommands(args);
// task.Wait();


async Task SetupCommands(string[] args)
{
    var rootCommand = new RootCommand("LNUnit - Lightning network unit testing toolkit");

    var filePath = new Option<string>
    ("--file",
        "scenario file to load/save");
    filePath.IsRequired = true;

    var nodeAlias = new Option<string>("--alias")
    {
        Description = "Node Alias",
        IsRequired = true
    };

    var startCommand = new Command("start", "Start scenario and run LNUnit daemon");
    startCommand.Add(filePath);
    startCommand.SetHandler(file => { Console.WriteLine(file); }, filePath);

    var stopCommand = new Command("stop", "Stop current LNUnit daemon");
    stopCommand.Add(filePath);
    startCommand.SetHandler(file => { Console.WriteLine(file); }, filePath);

    var pauseCommand = new Command("pause", "Pause node");
    pauseCommand.Add(nodeAlias);
    pauseCommand.SetHandler(alias => { Console.WriteLine(alias); }, nodeAlias);

    var resumeCommand = new Command("resume", "Pause node");
    resumeCommand.Add(nodeAlias);
    resumeCommand.SetHandler(alias => { Console.WriteLine(alias); }, nodeAlias);

    rootCommand.SetHandler(x => { Console.WriteLine("Use --help"); });
    rootCommand.Add(startCommand);
    rootCommand.Add(stopCommand);
    rootCommand.Add(pauseCommand);
    rootCommand.Add(resumeCommand);
    await rootCommand.InvokeAsync(args);
}