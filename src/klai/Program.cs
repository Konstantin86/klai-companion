using dotenv.net;
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

        // Register your Telegram Worker
        builder.Services.AddHostedService<TelegramBotWorker>();

        var host = builder.Build();
        host.Run();

    }

}
