using ResourceGuard;

// Handle --help before building the host
if (args.Any(a => a is "--help" or "-h"))
{
    Console.WriteLine("""
        ResourceGuard — Background CPU & memory monitor with popup notifications

        Usage: ResourceGuard [options]

        Options:
          -t, --threshold <percent>       Memory % to trigger alert (default: 85)
          --cpu-threshold <percent>       CPU % to trigger alert (default: 90)
          -p, --polling <seconds>         Polling interval in seconds (default: 30)
          -c, --cooldown <minutes>        Minutes between notifications (default: 5)
          -h, --help                      Show this help
        """);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<ResourceGuardOptions>(opts =>
{
    builder.Configuration.GetSection("ResourceGuard").Bind(opts);

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--threshold" or "-t" when i + 1 < args.Length:
                opts.MemoryThresholdPercent = int.Parse(args[++i]);
                break;
            case "--cpu-threshold" when i + 1 < args.Length:
                opts.CpuThresholdPercent = int.Parse(args[++i]);
                break;
            case "--polling" or "-p" when i + 1 < args.Length:
                opts.PollingIntervalSeconds = int.Parse(args[++i]);
                break;
            case "--cooldown" or "-c" when i + 1 < args.Length:
                opts.CooldownMinutes = int.Parse(args[++i]);
                break;
        }
    }
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
