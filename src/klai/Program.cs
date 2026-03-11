using dotenv.net;
using klai.Notion;
using klai.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

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

        builder.Services.AddKernel()
            .AddAzureOpenAIChatCompletion(
                deploymentName: fastModel,     // e.g., "gpt-5-mini"
                endpoint: endpoint,
                apiKey: apiKey,
                serviceId: "fast"              // Tag for dynamic retrieval
            )
            .AddAzureOpenAIChatCompletion(
                deploymentName: advancedModel, // e.g., "o4-mini"
                endpoint: endpoint,
                apiKey: apiKey,
                serviceId: "advanced"          // Tag for dynamic retrieval
            );


        // 1. Register as a Singleton so other classes can read the 'CurrentState' property
        builder.Services.AddSingleton<NotionSyncWorker>();

        // 2. Register that exact Singleton as a Hosted Service so its background loop runs
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NotionSyncWorker>());

        // Register your Telegram Worker
        builder.Services.AddHostedService<TelegramBotWorker>();

        var host = builder.Build();
        host.Run();

    }

}
