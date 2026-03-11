using CommandLine;
using Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;
using System.Net;           // for HttpListener
using HeadlessServer;      // for StateEndpointService

namespace HeadlessServer;

public sealed class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("MAIN STARTED - basic console works");
        Console.WriteLine($"Args count: {args.Length}");
        if (args.Length > 0)
        {
            Console.WriteLine("Args: " + string.Join(" ", args));
        }
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("headless_appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"headless_appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        IServiceCollection services = new ServiceCollection();

        ILoggerFactory logFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders().AddSerilog();
        });

        services.AddLogging(builder =>
        {
            const string outputTemplate = "[{@t:HH:mm:ss:fff} {@l:u1}] {#if Length(SourceContext) > 0}[{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1),-17}] {#end}{@m}\n{@x}";
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Enrich.FromLogContext()
                .WriteTo.File(new ExpressionTemplate(outputTemplate),
                    path: "headless_out.log",
                    rollingInterval: RollingInterval.Day)
                .WriteTo.Debug(new ExpressionTemplate(outputTemplate))
                .WriteTo.Console(new ExpressionTemplate(outputTemplate, theme: TemplateTheme.Literate))
                .CreateLogger();
            builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(logFactory.CreateLogger(string.Empty));
            builder.AddSerilog();
        });

        ILogger<Program> log = logFactory.CreateLogger<Program>();
        log.LogInformation($"Hosting environment: {environmentName ?? "Production"}");
        log.LogInformation($"{Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName} {DateTimeOffset.Now}");

        var parserResult = Parser.Default.ParseArguments<RunOptions>(args)
            .WithNotParsed(errors =>
            {
                foreach (Error? e in errors)
                {
                    log.LogError($"{e}");
                }
            });

        if (parserResult.Tag == ParserResultType.NotParsed)
        {
            Console.WriteLine("EXIT PATH: before goto Exit at [if (parserResult.Tag == ParserResultType.NotParsed)]");
            goto Exit;
        }

        var options = parserResult.Value;
        services.AddSingleton<RunOptions>(options);

        services.AddStartupConfigFactories();

        if (!FrameConfig.Exists() || !AddonConfig.Exists())
        {
            log.LogError($"Unable to run {nameof(HeadlessServer)} as crucial configuration files were missing!");
            // ... warnings ...
            Console.WriteLine("EXIT PATH: before goto Exit at [if (!FrameConfig.Exists() || !AddonConfig.Exists())]");
            goto Exit;
        }

        if (!ConfigureServices(log, services))
        {
            Console.WriteLine("EXIT PATH: before goto Exit at [if (!ConfigureServices(log, services))]");
            goto Exit;
        }

        // Build provider
        var provider = services
            .AddSingleton<HeadlessServer>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Console.WriteLine("DEBUG: ServiceProvider built.");

        StateEndpointService? stateEndpoint = null;

        Console.WriteLine("DEBUG: Attempting to resolve and start StateEndpointService...");
        try
        {
            stateEndpoint = provider.GetRequiredService<StateEndpointService>();
            Console.WriteLine("DEBUG: StateEndpointService resolved OK.");
            stateEndpoint.Start();
            Console.WriteLine("DEBUG: stateEndpoint.Start() executed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: Failed to resolve/start StateEndpointService!");
            Console.WriteLine(ex.ToString());
            // Optional: Environment.Exit(1); if you want to fail fast
        }

        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger>();

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            Exception e = (Exception)args.ExceptionObject;
            logger.LogError(e, e.Message);
        };

        var headlessServer = provider.GetRequiredService<HeadlessServer>();

        // Only register cleanup if we actually started the endpoint
        if (stateEndpoint != null)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stateEndpoint.Stop();
            Console.CancelKeyPress += (_, e) =>
            {
                stateEndpoint.Stop();
                e.Cancel = true;
            };
        }

        if (options.LoadOnly)
        {
            bool success = headlessServer.RunLoadOnly(parserResult);  // ← pass parserResult here
            Environment.Exit(success ? 0 : 1);
        }
        else
        {
            headlessServer.Run(parserResult);  // ← pass parserResult here (instead of options)
        }

    Exit:
        Console.ReadKey(true);
    }

    private static bool ConfigureServices(
        Microsoft.Extensions.Logging.ILogger log,
        IServiceCollection services)
    {
        if (!services.AddWoWProcess(log))
            return false;

        services.AddCoreBase(log);
        services.AddCoreNormal(log);

        // === STATE ENDPOINT FOR LLM OVERSEER (added March 2026) ===
        services.AddSingleton<StateEndpointService>();

        return true;
    }
}