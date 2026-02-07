using FileMonitorService.Configuration;
using FileMonitorService.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: @"C:\ProgramData\FileMonitorService\Logs\service-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting File Monitor Service");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    // Bind configuration
    builder.Services.Configure<MonitorSettings>(
        builder.Configuration.GetSection(MonitorSettings.SectionName));

    // Register services
    builder.Services.AddSingleton<PathClassifier>();
    builder.Services.AddSingleton<UserIdentityService>();
    builder.Services.AddSingleton<EventLogService>();
    builder.Services.AddSingleton<CsvLogService>();
    builder.Services.AddSingleton<ProcessHelper>();
    builder.Services.AddSingleton<FileServerVerifier>();

    // Register the background worker
    builder.Services.AddHostedService<FileMonitorWorker>();

    // Enable running as a Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "FileMonitorService";
    });

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
