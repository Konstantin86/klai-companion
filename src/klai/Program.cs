using dotenv.net;
using klai.Data;
using klai.KnowledgeBase;
using klai.LLM;
using klai.LLM.RAG;
using klai.Notion;
using klai.RAG;
using klai.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Core;
using Notion.Client;
using Qdrant.Client;

namespace klai;

class Program
{
    static void Main(string[] args)
    {
        DotEnv.Load();
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole();

        builder.Services.Configure<AiAgentConfig>(builder.Configuration.GetSection("AiAgentConfig"));

        var endpoint = builder.Configuration["OPENAIENDPOINT"]!;
        var apiKey = builder.Configuration["OPENAIAPIKEY"]!;
        var fastModel = builder.Configuration["AiAgentConfig:Models:Fast"]!;
        var advancedModel = builder.Configuration["AiAgentConfig:Models:Advanced"]!;

        var embeddingModel = builder.Configuration["EMBEDDING_MODEL"] ?? "text-embedding-3-large";
        var qdrantEndpoint = builder.Configuration["QDRANT_ENDPOINT"] ?? "http://localhost:6333";
        var qdrantHost = builder.Configuration["QDRANT_HOST"] ?? "localhost";
        builder.Services.AddSingleton(sp => new QdrantClient(qdrantHost, 6334));

        // --- NEW: Register the Notion Client into DI so the Plugin can use it ---
        builder.Services.AddSingleton<INotionClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var secret = config["NOTION_SECRET"] ?? throw new ArgumentNullException("NOTION_SECRET is missing");
            return NotionClientFactory.Create(new ClientOptions { AuthToken = secret });
        });

        builder.Services.AddKernel()
                    .AddAzureOpenAIChatCompletion(
                        deploymentName: fastModel,
                        endpoint: endpoint,
                        apiKey: apiKey,
                        serviceId: "fast"
                    )
                    .AddAzureOpenAIChatCompletion(
                        deploymentName: advancedModel,
                        endpoint: endpoint,
                        apiKey: apiKey,
                        serviceId: "advanced"
                    )
                    // --- REVERT BACK TO THE OLD METHOD ---
                    .AddAzureOpenAITextEmbeddingGeneration(
                        deploymentName: embeddingModel,
                        endpoint: endpoint,
                        apiKey: apiKey,
                        serviceId: "embedding",
                        dimensions: 3072 // Keep this!
                    )
                    .Plugins.AddFromType<TimePlugin>("Time")
                    .AddFromType<LongTermMemoryPlugin>("LongTermMemory")
                    .AddFromType<LocalDocumentPlugin>("LocalDocument")
                    .AddFromType<GoogleSheetsPlugin>("GoogleSheets")
                    .AddFromType<NotionPlannerPlugin>("NotionPlanner")
                    .AddFromType<NotionTaskModifierPlugin>("NotionTaskModifier");

        // Register EF Core SQLite DbContext
        var sqliteConnectionString = builder.Configuration["SQLITE_CONNECTION_STRING"]
            ?? "Data Source=data/klai_memory.db";

        builder.Services.AddDbContext<KlaiDbContext>(options =>
            options.UseSqlite(sqliteConnectionString));

        builder.Services.AddSingleton<TokenManagementService>();

        // 1. Register as a Singleton so other classes can read the 'CurrentState' property
        builder.Services.AddSingleton<NotionSyncWorker>();

        // 2. Register that exact Singleton as a Hosted Service so its background loop runs
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NotionSyncWorker>());

        // Register your Telegram Worker
        builder.Services.AddHostedService<TelegramBotWorker>();

        builder.Services.AddHostedService<MemoryConsolidationWorker>();

        var host = builder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<klai.Data.KlaiDbContext>(); // Update namespace if yours is different

            // This safely creates the database and all tables if they don't exist. 
            // It does nothing if they are already there.
            dbContext.Database.EnsureCreated();
        }

        host.Run();

    }

}
