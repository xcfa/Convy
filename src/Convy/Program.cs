using Convy.Configuration;
using Convy.Health;
using Convy.Infrastructure.Helpers;
using Convy.Mcp;
using Convy.Middleware;
using Convy.Services;
using Convy.Services.Downloaders.QBittorrent;
using Convy.Services.Downloaders.Slskd;
using Convy.Services.Downloads;
using Convy.Services.Files;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Services.Storage;
using Convy.Sources;
using Convy.Sources.Prowlarr;
using Convy.Sources.Slskd;
using Convy.Sources.Torrents;
using Convy.Services.Linking;
using Convy.Services.Sync;
using Convy.Services.Rules;
using Convy.Services.Security;
using Convy.Services.Settings;
using Convy.Services.Tracking;
using Convy.Services.Webhooks;
using Convy.Services.Diagnostics;
using Convy.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Convy.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Convy;

public class Program
{
	// Console template with a full date (yyyy-MM-dd) in addition to the time. Shared by
	// the bootstrap logger and the main logger so startup and runtime lines match.
	private const string ConsoleOutputTemplate =
		"[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

	private const string TorrentMetadataOptionsSection = "TorrentMetadata";

	public static async Task Main(string[] args)
	{
		// Capture failures during host construction until the full logger is built.
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
			.CreateBootstrapLogger();

		var builder = WebApplication.CreateBuilder(args);

		// Recent log entries for the web UI; filled by a Serilog sink below.
		var logBuffer = new LogBuffer();
		builder.Services.AddSingleton(logBuffer);

		builder.Configuration.AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true);
		builder.Configuration.AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
		// User configuration; a broken edit keeps the previous version in effect.
		builder.Configuration.Add<ResilientYamlConfigurationSource>(source =>
		{
			source.Path = "config/configuration.yml";
			source.Optional = true;
			source.ReloadOnChange = true;
			source.ResolveFileProvider();
		});
		builder.Configuration.AddEnvironmentVariables();
		builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);  // секреты перекрывают env
		var dbConfigSource = builder.Configuration.AddDbConfiguration();
		builder.Configuration.AddCommandLine(args);

		// Console and baseline levels are configured here so Docker stdout always works,
		// even if the config file is missing. File sinks and Seq are added from the
		// `Serilog` section of configuration.yml via ReadFrom.Configuration.
		builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
			.MinimumLevel.Information()
			.MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
			.Enrich.FromLogContext()
			.WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
			.WriteTo.Sink(new LogBufferSink(logBuffer))
			.ReadFrom.Configuration(context.Configuration));

		builder.Services
			.AddControllers()
			.AddJsonOptions(options =>
				options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

		builder.Services
			.AddOptions<QBitTorrentConnectionSettings>()
			.Bind(builder.Configuration.GetSection("QBitTorrent"))
			.ValidateDataAnnotations()
			.ValidateOnStart();

		builder.Services
			.AddOptions<UserSettings>()
			.Bind(builder.Configuration.GetSection("UserSettings"));

		// IP allow-list for incoming requests. PostConfigure applies the built-in
		// defaults only when nothing is configured (binding a list would otherwise
		// merge config entries with the defaults by index).
		builder.Services
			.AddOptions<IpAccessControlOptions>()
			.Bind(builder.Configuration.GetSection(IpAccessControlOptions.SectionName))
			.PostConfigure(options =>
			{
				if (options.AllowedNetworks.Count == 0)
				{
					options.AllowedNetworks = new List<string>(IpAccessControlOptions.DefaultAllowedNetworks);
				}
			});

		builder.Services.AddSingleton<IClientIpValidator, ClientIpValidator>();

		builder.Services.AddSingleton<IValidateOptions<QBitTorrentConnectionSettings>, ValidateQBitTorrentConnectionSettings>();

		var connectionString = builder.Configuration.GetConnectionString("SQLite");

		builder.Services.AddDbContextFactory<ConvyDbContext>(dbBuilder =>
		{
			dbBuilder.UseSqlite(connectionString);

			// SQL command logging is noisy at Information (every query + migration DDL).
			// Emit it at Debug so it only shows when the log level is lowered on purpose.
			dbBuilder.ConfigureWarnings(w => w.Log((RelationalEventId.CommandExecuted, LogLevel.Debug)));

			if (builder.Environment.IsDevelopment())
			{
				dbBuilder.EnableSensitiveDataLogging();
			}
		});

		// User settings live in the same SQLite file but a dedicated context, so
		// they keep their own migrations history table (otherwise the two contexts
		// would clash over __EFMigrationsHistory).
		builder.Services.AddDbContextFactory<SettingsDbContext>(dbBuilder =>
		{
			dbBuilder.UseSqlite(connectionString,
				sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistorySettings"));

			dbBuilder.ConfigureWarnings(w => w.Log((RelationalEventId.CommandExecuted, LogLevel.Debug)));

			if (builder.Environment.IsDevelopment())
			{
				dbBuilder.EnableSensitiveDataLogging();
			}
		});

		// Liveness/readiness: report unhealthy only when our own SQLite database is
		// unreachable. qBittorrent is a separate service; its availability is not part
		// of Convy's health.
		builder.Services.AddHealthChecks()
			.AddCheck<DatabaseHealthCheck>("database")
			.AddCheck<StorageLayoutHealthCheck>("storage");

		// Routing rules: loaded from a YAML file and reloaded when the file changes.
		builder.Services.AddSingleton<IRulesProvider>(sp =>
		{
			var path = builder.Configuration["Convy:RulesPath"] ?? "config/rules.yaml";
			return new RulesProvider(path, sp.GetRequiredService<ILogger<RulesProvider>>());
		});

		// State tracking: persists each download item's completion/size so that after a
		// restart only items that changed while we were down are reprocessed.
		builder.Services.AddSingleton<IDownloadStateStore, EfDownloadStateStore>();
		builder.Services.AddSingleton<IDownloadStateTracker, DownloadStateTracker>();

		// Hard-link creation and filesystem checks (mounts, free space, symlink resolution).
		builder.Services.AddSingleton<IFileLinker, FileLinker>();
		builder.Services.AddSingleton<IFileSystemInspector, FileSystemInspector>();
		builder.Services.AddSingleton<FileLinkingService>();
		builder.Services.AddSingleton<StorageLayoutStatus>();
		builder.Services.AddSingleton<StorageLayoutValidator>();

		// Jobs: downloads started by the agent, with a unified status and placement wishes.
		builder.Services
			.AddOptions<JobOptions>()
			.Bind(builder.Configuration.GetSection(JobOptions.SectionName));
		builder.Services.AddSingleton<IJobStore, EfJobStore>();
		builder.Services.AddSingleton<IJobEvents, WebhookJobEvents>();
		builder.Services.AddSingleton<JobTransitions>();
		builder.Services.AddSingleton<JobService>();

		// Webhooks: the `linked` batch after each sync cycle, plus single events (job status,
		// source errors) delivered with retries by a background dispatcher. The configuration
		// is read on every send, so edits to configuration.yml apply without a restart.
		builder.Services.Configure<List<WebhookConfig>>(builder.Configuration.GetSection("Webhooks"));
		builder.Services.AddSingleton<WebhookConfigSource>();
		builder.Services.AddSingleton<Func<IReadOnlyList<WebhookConfig>>>(sp =>
		{
			var source = sp.GetRequiredService<WebhookConfigSource>();
			return () => source.Current;
		});
		builder.Services.AddSingleton(sp => new WebhookSender(
			new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
			{
				// A dead endpoint must not hold up the other webhooks for long.
				Timeout = TimeSpan.FromSeconds(30),
			},
			sp.GetRequiredService<ILogger<WebhookSender>>()));
		builder.Services.AddSingleton<IWebhookNotifier, WebhookNotifier>();
		builder.Services.AddSingleton(new WebhookDeliveryOptions());
		builder.Services.AddSingleton<WebhookEventDispatcher>();
		builder.Services.AddSingleton<IWebhookEventQueue>(sp => sp.GetRequiredService<WebhookEventDispatcher>());
		builder.Services.AddHostedService<WebhookDispatchWorker>();
		builder.Services.AddSingleton<ISourceHealth, SourceHealthMonitor>();

		builder.Services.AddSingleton(TimeProvider.System);

		// Downloaders: each client sits behind IDownloader; the resolver picks one by protocol.
		builder.Services.AddSingleton<IQBittorrentApi, QBittorrentApi>();
		builder.Services.AddSingleton<IDownloader, QBittorrentDownloader>();
		builder.Services.AddSingleton<IDownloaderResolver, DownloaderResolver>();

		// slskd is both a source (Soulseek search) and a downloader; both use one client.
		var slskdOptions = builder.Configuration.GetSection(SlskdOptions.SectionName).Get<SlskdOptions>()
		                   ?? new SlskdOptions();
		builder.Services.AddSingleton(_ => new SlskdClient(
			new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
			{
				BaseAddress = slskdOptions.IsConfigured ? new Uri(slskdOptions.Url!.TrimEnd('/') + "/api/v0/") : null,
			},
			slskdOptions));
		builder.Services.AddSingleton<ISourceProvider, SoulseekSourceProvider>();
		if (slskdOptions.IsConfigured)
		{
			builder.Services.AddSingleton<IDownloader, SlskdDownloader>();
		}

		// Business logic for a single sync cycle over all downloaders.
		builder.Services.AddSingleton<SyncStatusTracker>();
		builder.Services.AddSingleton<SyncCycleService>();

		// User settings persistence: writes to DB and triggers config provider reload.
		builder.Services.AddSingleton<IUserSettingsService>(sp =>
		{
			var dbFactory = sp.GetRequiredService<IDbContextFactory<SettingsDbContext>>();
			return new UserSettingsService(dbFactory,
				ct => dbConfigSource.Provider?.ReloadAsync(ct) ?? Task.CompletedTask);
		});

		// ── Search and download for the agent (MCP) ─────────────────────────
		builder.Services.AddOptions<CategoriesOptions>().Bind(builder.Configuration);
		builder.Services.AddOptions<SearchOptions>().Bind(builder.Configuration.GetSection(SearchOptions.SectionName));
		builder.Services.AddOptions<FilesOptions>().Bind(builder.Configuration.GetSection(FilesOptions.SectionName));
		builder.Services.AddOptions<McpOptions>().Bind(builder.Configuration.GetSection(McpOptions.SectionName));

		builder.Services.AddSingleton(builder.Configuration.GetSection(TorrentMetadataOptionsSection).Get<TorrentMetadataOptions>()
		                              ?? new TorrentMetadataOptions());
		builder.Services.AddSingleton<ITorrentMetadataService, TorrentMetadataService>();

		// Prowlarr: one source per indexer. Its own HttpClient never follows redirects, because
		// download links redirect to magnet: URIs (and must not leak the key to other hosts).
		var prowlarrOptions = builder.Configuration.GetSection(ProwlarrOptions.SectionName).Get<ProwlarrOptions>()
		                      ?? new ProwlarrOptions();
		builder.Services.AddSingleton(prowlarrOptions);
		builder.Services.AddSingleton(_ => new ProwlarrClient(
			new HttpClient(new SocketsHttpHandler
			{
				AllowAutoRedirect = false,
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
			})
			{
				BaseAddress = prowlarrOptions.IsConfigured ? new Uri(prowlarrOptions.Url!.TrimEnd('/') + "/") : null,
			},
			prowlarrOptions));
		builder.Services.AddSingleton<ISourceProvider, ProwlarrSourceProvider>();

		builder.Services.AddSingleton<ISourceRegistry, SourceRegistry>();
		builder.Services.AddSingleton<CategoryCatalog>();
		builder.Services.AddSingleton<ISearchCache, EfSearchCache>();
		builder.Services.AddSingleton<SearchService>();
		builder.Services.AddSingleton<FileListingService>();
		builder.Services.AddSingleton<MediaDownloadService>();
		builder.Services.AddSingleton<MediaCatalogService>();
		builder.Services.AddSingleton<McpApiKeyValidator>();
		builder.Services.AddConvyMcp();

		// Controller-facing services: all endpoint logic lives here, controllers only delegate.
		builder.Services.AddScoped<IFileEntryQueryService, FileEntryQueryService>();
		builder.Services.AddSingleton<ISyncControlService, SyncControlService>();

		// Web UI (logs, status, database) with OIDC sign-in; see Ui/UiSetup.cs.
		builder.AddConvyUi(connectionString);

		// Background sync loop — registered as singleton for DI + hosted service.
		builder.Services.AddSingleton<SyncWorker>();
		builder.Services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncWorker>());
		builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncWorker>());

		var app = builder.Build();

		await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ConvyDbContext>>().CreateDbContextAsync())
		{
			await db.Database.MigrateAsync();
		}

		await using (var settingsDb = await app.Services.GetRequiredService<IDbContextFactory<SettingsDbContext>>().CreateDbContextAsync())
		{
			await settingsDb.Database.MigrateAsync();
		}

		// Load DB-backed settings now that the table exists — async, so startup never
		// blocks on the database. Subsequent writes refresh it via UserSettingsService.
		if (dbConfigSource.Provider is not null)
		{
			await dbConfigSource.Provider.ReloadAsync();
		}


		// ── HTTP pipeline ────────────────────────────────────────────────────

		// Reject clients outside the configured IP allow-list before anything else.
		app.UseMiddleware<IpAccessControlMiddleware>();

		if (app.Environment.IsDevelopment())
		{
			// Serve the OpenAPI JSON (Microsoft.AspNetCore.OpenApi) at /openapi/v1.json …
			app.MapOpenApi();

			// … and an interactive Swagger UI over it at /swagger.
			app.UseSwaggerUI(options =>
			{
				// WHERE the UI loads the spec from (the JSON document), NOT the UI route.
				options.SwaggerEndpoint("/openapi/v1.json", "Convy API v1");
				// WHERE the UI page itself lives → /swagger.
				options.RoutePrefix = "swagger";
			});
		}

		// Web UI sign-in. Must come before the HTTPS redirect: it may set the public scheme
		// of the request (Ui:PublicUrl).
		app.UseConvyUi();

		// In dev/containers we expose plain HTTP and test over it (Postman/Swagger); redirecting
		// to HTTPS here would bounce those calls to a port that isn't mapped outside the container.
		if (!app.Environment.IsDevelopment())
		{
			app.UseHttpsRedirection();
		}

		app.MapControllers();
		app.MapConvyUi();

		// MCP endpoint for the agent; behind the IP allow-list and MCP__APIKEY.
		app.MapConvyMcp();

		app.MapHealthChecks("/health", new HealthCheckOptions
		{
			ResponseWriter = HealthCheckResponseWriter.WriteAsync,
		});

		try
		{
			await app.RunAsync();
		}
		catch (Exception ex)
		{
			Log.Fatal(ex, "Convy terminated unexpectedly");
			throw;
		}
		finally
		{
			await Log.CloseAndFlushAsync();
		}
	}
}
