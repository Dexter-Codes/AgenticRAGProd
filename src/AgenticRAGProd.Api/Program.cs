using Microsoft.AspNetCore.Mvc;
using AgenticRAGProd.Api;
using AgenticRAGProd.Api.Contracts;
using AgenticRAGProd.Application;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource =>
            resource.AddService(
                serviceName: "AgenticRAGProd.Api"))
        .WithTracing(tracing =>
            tracing
                .SetSampler(new AlwaysOnSampler())
                .AddAspNetCoreInstrumentation()
                .AddSource(ChatTelemetry.ActivitySourceName)
                .AddConsoleExporter())
        .WithMetrics(metrics =>
            metrics
                .AddMeter(ChatTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddView(
                    ChatTelemetry.DurationMetricName,
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = new double[]
                        {
                            0.01, 0.05, 0.1, 0.25, 0.5,
                            1, 2, 5, 10, 20, 30,
                            60, 120, 180, 240
                        }
                    })
                .AddConsoleExporter((_, readerOptions) =>
                {
                    readerOptions
                        .PeriodicExportingMetricReaderOptions
                        .ExportIntervalMilliseconds = 5000;
                }));
}
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});
builder.Services.AddProblemDetails();
builder.Services.AddApiDocumentation();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = Program.CreateApp(builder);
app.Run();

public partial class Program
{
    internal static WebApplication CreateApp(WebApplicationBuilder builder)
    {
        var localChatEnabled = builder.Configuration.GetValue<bool>("Chat:LocalSingleUserMode");
        if (localChatEnabled && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Local single-user chat is supported only in Development.");

        var app = builder.Build();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = error => error is BadHttpRequestException requestError
                ? requestError.StatusCode : StatusCodes.Status500InternalServerError
        });
        if (app.Environment.IsDevelopment())
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        app.UseApiDocumentation();

        #region Document upload and indexing

        app.MapPost("/api/documents", async (
            IFormFile file,
            [FromServices] IDocumentStorage storage,
            CancellationToken cancellationToken) =>
        {
            const long maxFileSize = 10 * 1024 * 1024;
            if (file.Length == 0 || file.Length > maxFileSize)
                return Results.BadRequest(new { error = "Upload a non-empty PDF no larger than 10 MB." });

            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only PDF files are supported." });

            var documentId = Guid.NewGuid();
            await using var content = file.OpenReadStream();
            await storage.UploadAsync(documentId, content, cancellationToken);

            return Results.Ok(new
            {
                documentId,
                fileName = Path.GetFileName(file.FileName),
                sizeBytes = file.Length,
                status = "Uploaded"
            });
        }).DisableAntiforgery();

        app.MapPost("/api/documents/{documentId:guid}/index", async (
            Guid documentId,
            [FromQuery] string documentName,
            [FromServices] DocumentIndexingService indexingService,
            CancellationToken cancellationToken) =>
        {
            if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(documentName) || documentName.Length > 255)
                return Results.BadRequest(new { error = "Provide a document ID and a name of 1–255 characters." });

            return Results.Ok(await indexingService.IndexAsync(documentId, documentName, cancellationToken));
        });

        #endregion

        if (app.Environment.IsDevelopment() && localChatEnabled)
        {
            app.MapPost("/api/chat", async (ChatRequest request, ChatService service, CancellationToken token) =>
                Results.Ok(await service.SendAsync(request.ConversationId, request.Query, token)))
                .AddEndpointFilter<ChatExceptionFilter>();
        }

        return app;
    }
}
