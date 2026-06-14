using Convy.PathExpressions.Mappings;
using Convy.Services;
using Convy.Services.Services;
using Convy.Services.Tracking;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Convy.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Convy;

public class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Configuration.AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true);
		builder.Configuration.AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
		builder.Configuration.AddEnvironmentVariables();
		builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);  // секреты перекрывают env
		builder.Configuration.AddCommandLine(args);

		builder.Services
			.AddControllers()
			.AddJsonOptions(options =>
				// Serialize enums as their names ("Container", "Ru") instead of numbers.
				options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

		builder.Services
			.AddOptions<QBitTorrentConnectionSettings>()
			.Bind(builder.Configuration.GetSection("QBitTorrent"))
			.ValidateDataAnnotations()
			.ValidateOnStart();

		builder.Services.AddSingleton<IValidateOptions<QBitTorrentConnectionSettings>, ValidateQBitTorrentConnectionSettings>();

		builder.Services.AddDbContextFactory<ConvyDbContext>(dbBuilder =>
		{
			dbBuilder.UseSqlite(builder.Configuration.GetConnectionString("SQLite"));

			if (builder.Environment.IsDevelopment())
			{
				dbBuilder.EnableSensitiveDataLogging();
			}
		});

		// Output routing rules (ConvyMappings.ini). The path is configurable; if the
		// file is absent we start with an empty rule set rather than failing to boot.
		builder.Services.AddSingleton(sp =>
		{
			var path = builder.Configuration["Convy:MappingsFile"] ?? "config/ConvyMappings.ini";
			return File.Exists(path)
				? OutputDirectoryMatcher.FromFile(path)
				: new OutputDirectoryMatcher(ConvyMappings.Parse(Array.Empty<string>()));
		});

		// State tracking: persists each torrent's completion/size so that after a
		// restart only torrents that changed while we were down are reprocessed.
		builder.Services.AddSingleton<ITorrentStateStore, EfTorrentStateStore>();
		builder.Services.AddSingleton<ITorrentStateTracker, TorrentStateTracker>();

		// Business logic for a single sync cycle (owns the qBittorrent connection).
		builder.Services.AddSingleton<QBitTorrentCommunicationService>();

		builder.Services.AddHostedService<QBitTorrentSyncService>();

		var app = builder.Build();

		await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ConvyDbContext>>().CreateDbContextAsync())
		{
			await db.Database.MigrateAsync();
		}


		// ── HTTP pipeline ────────────────────────────────────────────────────
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

		await app.RunAsync();
	}
}