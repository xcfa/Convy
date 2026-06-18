using Convy.Configuration;
using Convy.Infrastructure.Helpers;
using Convy.Middleware;
using Convy.Services;
using Convy.Services.Files;
using Convy.Services.Linking;
using Convy.Services.Sync;
using Convy.Services.Rules;
using Convy.Services.Security;
using Convy.Services.Services;
using Convy.Services.Settings;
using Convy.Services.Tracking;
using Convy.Services.Webhooks;
using Microsoft.AspNetCore.Builder;
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
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Convy;

public class Program
{
	public static async Task Main(string[] args)
	{
		// Capture failures during host construction until the full logger is built.
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console()
			.CreateBootstrapLogger();

		var builder = WebApplication.CreateBuilder(args);

		builder.Configuration.AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true);
		builder.Configuration.AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
		builder.Configuration.AddYamlFile("config/configuration.yml", optional: true, reloadOnChange: true);
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
			.WriteTo.Console()
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

			if (builder.Environment.IsDevelopment())
			{
				dbBuilder.EnableSensitiveDataLogging();
			}
		});

		// Routing rules: loaded from a YAML file and reloaded when the file changes.
		builder.Services.AddSingleton<IRulesProvider>(sp =>
		{
			var path = builder.Configuration["Convy:RulesPath"] ?? "config/rules.yaml";
			return new RulesProvider(path, sp.GetRequiredService<ILogger<RulesProvider>>());
		});

		// State tracking: persists each torrent's completion/size so that after a
		// restart only torrents that changed while we were down are reprocessed.
		builder.Services.AddSingleton<ITorrentStateStore, EfTorrentStateStore>();
		builder.Services.AddSingleton<ITorrentStateTracker, TorrentStateTracker>();

		// Hard-link creation.
		builder.Services.AddSingleton<IFileLinker, FileLinker>();
		builder.Services.AddSingleton<FileLinkingService>();

		// Webhook notifications after successful linking.
		builder.Services.AddSingleton<IWebhookNotifier>(sp =>
		{
			var configs = builder.Configuration.GetSection("Webhooks").Get<List<WebhookConfig>>()
			               ?? new List<WebhookConfig>();
			return new WebhookNotifier(
				configs,
				new HttpClient(),
				sp.GetRequiredService<ILogger<WebhookNotifier>>());
		});

		// Business logic for a single sync cycle (owns the qBittorrent connection).
		builder.Services.AddSingleton<QBitTorrentCommunicationService>();

		// User settings persistence: writes to DB and triggers config provider reload.
		builder.Services.AddSingleton<IUserSettingsService>(sp =>
		{
			var dbFactory = sp.GetRequiredService<IDbContextFactory<SettingsDbContext>>();
			return new UserSettingsService(dbFactory,
				ct => dbConfigSource.Provider?.ReloadAsync(ct) ?? Task.CompletedTask);
		});

		// Controller-facing services: all endpoint logic lives here, controllers only delegate.
		builder.Services.AddScoped<IFileEntryQueryService, FileEntryQueryService>();
		builder.Services.AddSingleton<ISyncControlService, SyncControlService>();

		// Background sync loop — registered as singleton for DI + hosted service.
		builder.Services.AddSingleton<QBitTorrentSyncService>();
		builder.Services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<QBitTorrentSyncService>());
		builder.Services.AddHostedService(sp => sp.GetRequiredService<QBitTorrentSyncService>());

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

		// In dev/containers we expose plain HTTP and test over it (Postman/Swagger); redirecting
		// to HTTPS here would bounce those calls to a port that isn't mapped outside the container.
		if (!app.Environment.IsDevelopment())
		{
			app.UseHttpsRedirection();
		}

		app.MapControllers();

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
